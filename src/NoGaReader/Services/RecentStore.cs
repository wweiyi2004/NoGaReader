using System.Collections.Concurrent;
using NoGaReader.Models;

namespace NoGaReader.Services;

public sealed class RecentStore
{
    private const int MaximumItems = 30;
    private static readonly ConcurrentDictionary<string, StoreState> States =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _path;
    private readonly StoreState _state;

    public RecentStore()
    {
        _path = Path.GetFullPath(Path.Combine(AppPaths.DataRoot, "recent.json"));
        _state = States.GetOrAdd(_path, static _ => new StoreState());
    }

    public IReadOnlyList<RecentBook> Load()
    {
        lock (_state.Sync)
        {
            EnsureLoaded();
            return _state.Items!
                .OrderByDescending(item => item.LastOpened)
                .Take(MaximumItems)
                .Select(Clone)
                .ToList();
        }
    }

    public RecentBook? Find(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return Load().FirstOrDefault(item => PathsEqual(item.Path, fullPath));
    }

    public void Touch(
        ReaderSession session,
        int sectionIndex,
        double sectionProgress,
        double zoomFactor,
        int currentPage = 0)
    {
        Update(session, sectionIndex, sectionProgress, zoomFactor, currentPage);
        Flush();
    }

    public void Update(
        ReaderSession session,
        int sectionIndex,
        double sectionProgress,
        double zoomFactor,
        int currentPage = 0)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_state.Sync)
        {
            EnsureLoaded();
            var fullPath = Path.GetFullPath(session.SourcePath);
            var existing = _state.Items!.FirstOrDefault(item => PathsEqual(item.Path, fullPath));

            if (existing is null)
            {
                existing = new RecentBook
                {
                    Path = fullPath,
                    Title = session.Title,
                    Kind = session.Kind
                };
                _state.Items!.Add(existing);
            }

            existing.Title = session.Title;
            existing.Kind = session.Kind;
            existing.LastOpened = DateTimeOffset.Now;
            existing.SectionIndex = Math.Clamp(sectionIndex, 0, Math.Max(0, session.Sections.Count - 1));
            existing.SectionCount = Math.Max(1, session.Sections.Count);
            existing.SectionProgress = Math.Clamp(sectionProgress, 0, 1);
            existing.CurrentPage = Math.Max(0, currentPage);
            existing.ZoomFactor = Math.Clamp(zoomFactor, 0.5, 3.0);
            _state.Dirty = true;
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
        lock (_state.Sync)
        {
            EnsureLoaded();
            if (_state.Items!.RemoveAll(item => PathsEqual(item.Path, fullPath)) > 0)
            {
                _state.Dirty = true;
            }
        }
    }

    public void Flush()
    {
        lock (_state.Sync)
        {
            EnsureLoaded();
            if (!_state.Dirty)
            {
                return;
            }

            var snapshot = _state.Items!
                .OrderByDescending(item => item.LastOpened)
                .Take(MaximumItems)
                .Select(Clone)
                .ToList();
            JsonFileStore.Save(_path, snapshot);
            _state.Items = snapshot;
            _state.Dirty = false;
        }
    }

    private void EnsureLoaded()
    {
        _state.Items ??= JsonFileStore.Load(_path, new List<RecentBook>())
            .OrderByDescending(item => item.LastOpened)
            .Take(MaximumItems)
            .ToList();
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return PathSemantics.Equals(left, right);
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
        CurrentPage = item.CurrentPage,
        ZoomFactor = item.ZoomFactor
    };

    private sealed class StoreState
    {
        public object Sync { get; } = new();

        public List<RecentBook>? Items { get; set; }

        public bool Dirty { get; set; }
    }
}
