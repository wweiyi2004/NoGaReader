using System.Collections.Concurrent;
using NoGaReader.Models;

namespace NoGaReader.Services;

public sealed class SettingsStore
{
    private static readonly ConcurrentDictionary<string, SaveState> SaveStates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _path;
    private readonly SaveState _saveState;

    public SettingsStore()
    {
        _path = Path.GetFullPath(Path.Combine(AppPaths.DataRoot, "settings.json"));
        _saveState = SaveStates.GetOrAdd(_path, static _ => new SaveState());
    }

    public AppSettings Load()
    {
        lock (_saveState.Gate)
        {
            var settings = Normalize(JsonFileStore.Load(_path, new AppSettings()));
            if (settings.SyncSettingsModifiedUtc == DateTimeOffset.MinValue)
            {
                _saveState.LegacySettingsTimestamp =
                    _saveState.LegacySettingsTimestamp == DateTimeOffset.MinValue
                        ? DateTimeOffset.UtcNow
                        : _saveState.LegacySettingsTimestamp;
                settings.SyncSettingsModifiedUtc = _saveState.LegacySettingsTimestamp;
            }

            return settings;
        }
    }

    public long ReserveSaveRevision()
    {
        return Interlocked.Increment(ref _saveState.NextRevision);
    }

    public void Save(AppSettings settings)
    {
        Save(settings, ReserveSaveRevision());
    }

    public void Save(AppSettings settings, long revision)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        AdvanceReservedRevision(revision);
        lock (_saveState.Gate)
        {
            if (revision < _saveState.LastWrittenRevision)
            {
                return;
            }

            Normalize(settings);
            var persisted = Normalize(JsonFileStore.Load(_path, new AppSettings()));
            UpdateSyncSettingsTimestamp(settings, persisted);
            JsonFileStore.Save(_path, settings);
            _saveState.LastWrittenRevision = revision;
            _saveState.LegacySettingsTimestamp = settings.SyncSettingsModifiedUtc;
        }
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.Theme = Enum.IsDefined(settings.Theme)
            ? settings.Theme
            : AppThemeMode.System;
        settings.ZoomFactor = Math.Clamp(settings.ZoomFactor, 0.5, 3.0);
        settings.ReaderFontSize = Math.Clamp(settings.ReaderFontSize, 14, 30);
        settings.ReaderLineHeight = Math.Clamp(settings.ReaderLineHeight, 1.4, 2.4);
        settings.ReaderContentWidth = Math.Clamp(settings.ReaderContentWidth, 560, 1000);
        settings.ReaderTheme = Enum.IsDefined(settings.ReaderTheme)
            ? settings.ReaderTheme
            : ReaderThemeMode.Auto;
        if (!settings.ReaderThemePreferenceInitialized)
        {
            settings.ReaderTheme = ReaderThemeMode.Auto;
            settings.ReaderThemePreferenceInitialized = true;
        }
        settings.ReaderFlow = Enum.IsDefined(settings.ReaderFlow)
            ? settings.ReaderFlow
            : ReaderFlowMode.Paged;
        settings.ComicDisplay = Enum.IsDefined(settings.ComicDisplay)
            ? settings.ComicDisplay
            : ComicDisplayMode.Single;
        settings.ComicDirection = Enum.IsDefined(settings.ComicDirection)
            ? settings.ComicDirection
            : ComicReadingDirection.RightToLeft;
        settings.ComicFit = Enum.IsDefined(settings.ComicFit)
            ? settings.ComicFit
            : ComicFitMode.Height;
        settings.ComicScale = Math.Clamp(settings.ComicScale, 0.5, 3.0);
        settings.TotalReadingSeconds = Math.Clamp(settings.TotalReadingSeconds, 0, 315_576_000);
        settings.ReadingDates = (settings.ReadingDates ?? [])
            .Where(value => DateOnly.TryParseExact(value, "yyyy-MM-dd", out _))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value, StringComparer.Ordinal)
            .Take(3660)
            .ToList();
        settings.CalibreEbookConvertPath = NormalizeOptionalPath(settings.CalibreEbookConvertPath);
        settings.ConversionOutputDirectory = NormalizeOptionalPath(settings.ConversionOutputDirectory);
        settings.ConversionDefaultTargetExtension = NormalizeTargetExtension(
            settings.ConversionDefaultTargetExtension);
        settings.SyncFolderPath = NormalizeOptionalPath(settings.SyncFolderPath);
        settings.SyncProvider = Enum.IsDefined(settings.SyncProvider)
            ? settings.SyncProvider
            : SyncProviderKind.None;
        settings.LastSyncUtc = settings.LastSyncUtc?.Trim() ?? string.Empty;
        settings.UiLanguage = settings.UiLanguage?.Trim() ?? string.Empty;
        if (settings.SyncSettingsModifiedUtc != DateTimeOffset.MinValue)
        {
            settings.SyncSettingsModifiedUtc = settings.SyncSettingsModifiedUtc.ToUniversalTime();
        }

        return settings;
    }

    private void AdvanceReservedRevision(long revision)
    {
        while (true)
        {
            var current = Volatile.Read(ref _saveState.NextRevision);
            if (current >= revision)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _saveState.NextRevision, revision, current) == current)
            {
                return;
            }
        }
    }

    private static void UpdateSyncSettingsTimestamp(AppSettings settings, AppSettings persisted)
    {
        var incomingTimestamp = settings.SyncSettingsModifiedUtc;
        var persistedTimestamp = persisted.SyncSettingsModifiedUtc;
        if (incomingTimestamp > persistedTimestamp)
        {
            settings.SyncSettingsModifiedUtc = incomingTimestamp;
            return;
        }

        if (CreateSyncSettingsFingerprint(settings) == CreateSyncSettingsFingerprint(persisted) &&
            persistedTimestamp != DateTimeOffset.MinValue)
        {
            settings.SyncSettingsModifiedUtc = persistedTimestamp;
            return;
        }

        settings.SyncSettingsModifiedUtc = DateTimeOffset.UtcNow;
    }

    private static SyncSettingsFingerprint CreateSyncSettingsFingerprint(AppSettings settings)
    {
        return new SyncSettingsFingerprint(
            settings.Theme,
            settings.ReaderTheme,
            settings.ReaderFlow,
            settings.ReaderFontSize,
            settings.UsePublisherFont,
            settings.ReaderLineHeight,
            settings.ReaderContentWidth,
            settings.ComicDisplay,
            settings.ComicDirection,
            settings.ComicFit,
            settings.ComicCoverSinglePage,
            settings.ComicScale,
            settings.UiLanguage);
    }

    private static string NormalizeOptionalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeTargetExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return ".epub";
        }

        extension = extension.Trim();
        if (!extension.StartsWith(".", StringComparison.Ordinal))
        {
            extension = "." + extension;
        }

        extension = extension.ToLowerInvariant();
        return extension is ".epub" or ".pdf" or ".mobi" or ".azw3" or ".docx" or ".fb2" or ".rtf"
            or ".txt" or ".htmlz" or ".zip" or ".txtz" or ".lit" or ".lrf" or ".pmlz" or ".rb"
            ? extension
            : ".epub";
    }

    private sealed class SaveState
    {
        public object Gate { get; } = new();

        public long NextRevision;

        public long LastWrittenRevision;

        public DateTimeOffset LegacySettingsTimestamp;
    }

    private readonly record struct SyncSettingsFingerprint(
        AppThemeMode Theme,
        ReaderThemeMode ReaderTheme,
        ReaderFlowMode ReaderFlow,
        int ReaderFontSize,
        bool UsePublisherFont,
        double ReaderLineHeight,
        int ReaderContentWidth,
        ComicDisplayMode ComicDisplay,
        ComicReadingDirection ComicDirection,
        ComicFitMode ComicFit,
        bool ComicCoverSinglePage,
        double ComicScale,
        string UiLanguage);
}
