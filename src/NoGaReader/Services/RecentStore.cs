using NoGaReader.Models;

namespace NoGaReader.Services;

public sealed class RecentStore
{
    private const int MaximumItems = 30;
    private readonly string _path = Path.Combine(AppPaths.DataRoot, "recent.json");
    private readonly object _sync = new();
    private List<RecentBook>? _items;
    private bool _dirty;
    private long _changeVersion;

    public IReadOnlyList<RecentBook> Load()
    {
        lock (_sync)
        {
            EnsureLoaded();
            return _items!
                .OrderByDescending(item => item.LastOpened)
                .Take(MaximumItems)
                .Select(Clone)
                .ToList();
        }
    }

    public RecentBook? Find(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return Load().FirstOrDefault(item =>
            string.Equals(Path.GetFullPath(item.Path), fullPath, StringComparison.OrdinalIgnoreCase));
    }

    public void Touch(ReaderSession session, int sectionIndex, double sectionProgress, double zoomFactor)
    {
        Update(session, sectionIndex, sectionProgress, zoomFactor);
        Flush();
    }

    public void Update(ReaderSession session, int sectionIndex, double sectionProgress, double zoomFactor)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_sync)
        {
            EnsureLoaded();
            var fullPath = Path.GetFullPath(session.SourcePath);
            var existing = _items!.FirstOrDefault(item => PathsEqual(item.Path, fullPath));

            if (existing is null)
            {
                existing = new RecentBook
                {
                    Path = fullPath,
                    Title = session.Title,
                    Kind = session.Kind
                };
                _items!.Add(existing);
            }

            existing.Title = session.Title;
            existing.Kind = session.Kind;
            existing.LastOpened = DateTimeOffset.Now;
            existing.SectionIndex = Math.Clamp(sectionIndex, 0, Math.Max(0, session.Sections.Count - 1));
            existing.SectionCount = Math.Max(1, session.Sections.Count);
            existing.SectionProgress = Math.Clamp(sectionProgress, 0, 1);
            existing.ZoomFactor = Math.Clamp(zoomFactor, 0.5, 3.0);
            _dirty = true;
            _changeVersion++;
        }
    }

    public void Remove(string path)
    {
        RemoveInMemory(path);
        Flush();
    }

    public void RemoveInMemory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        lock (_sync)
        {
            EnsureLoaded();
            if (_items!.RemoveAll(item => PathsEqual(item.Path, fullPath)) > 0)
            {
                _dirty = true;
                _changeVersion++;
            }
        }
    }

    public void Flush()
    {
        List<RecentBook> snapshot;
        long snapshotVersion;
        lock (_sync)
        {
            EnsureLoaded();
            if (!_dirty)
            {
                return;
            }

            snapshot = _items!
                .OrderByDescending(item => item.LastOpened)
                .Take(MaximumItems)
                .Select(Clone)
                .ToList();
            snapshotVersion = _changeVersion;
        }

        JsonFileStore.Save(_path, snapshot);
        lock (_sync)
        {
            if (_changeVersion == snapshotVersion)
            {
                _dirty = false;
            }
        }
    }

    private void EnsureLoaded()
    {
        _items ??= JsonFileStore.Load(_path, new List<RecentBook>())
            .OrderByDescending(item => item.LastOpened)
            .Take(MaximumItems)
            .ToList();
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static RecentBook Clone(RecentBook item) => new()
    {
        Path = item.Path,
        Title = item.Title,
        Kind = item.Kind,
        LastOpened = item.LastOpened,
        SectionIndex = item.SectionIndex,
        SectionCount = item.SectionCount,
        SectionProgress = item.SectionProgress,
        ZoomFactor = item.ZoomFactor
    };
}
