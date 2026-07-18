using NoGaReader.Models;

namespace NoGaReader.Services;

public sealed class SettingsStore
{
    private readonly string _path = Path.Combine(AppPaths.DataRoot, "settings.json");

    public AppSettings Load()
    {
        var settings = JsonFileStore.Load(_path, new AppSettings());
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
        return settings;
    }

    public void Save(AppSettings settings)
    {
        settings.CalibreEbookConvertPath = NormalizeOptionalPath(settings.CalibreEbookConvertPath);
        settings.ConversionOutputDirectory = NormalizeOptionalPath(settings.ConversionOutputDirectory);
        settings.ConversionDefaultTargetExtension = NormalizeTargetExtension(
            settings.ConversionDefaultTargetExtension);
        settings.SyncFolderPath = NormalizeOptionalPath(settings.SyncFolderPath);
        settings.SyncProvider = Enum.IsDefined(settings.SyncProvider)
            ? settings.SyncProvider
            : SyncProviderKind.None;
        settings.LastSyncUtc = settings.LastSyncUtc?.Trim() ?? string.Empty;
        JsonFileStore.Save(_path, settings);
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
            or ".txt" or ".htmlz" or ".zip"
            ? extension
            : ".epub";
    }
}
