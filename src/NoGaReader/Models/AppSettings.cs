namespace NoGaReader.Models;

public enum AppThemeMode
{
    System,
    Light,
    Dark
}
public enum ReaderThemeMode
{
    Auto,
    Paper,
    Light,
    Dark
}

public enum ReaderFlowMode
{
    Paged,
    Scrolling
}

public enum ComicDisplayMode
{
    Single,
    Double,
    Continuous
}

public enum ComicReadingDirection
{
    LeftToRight,
    RightToLeft
}

public enum ComicFitMode
{
    Width,
    Height,
    Original
}

public sealed class AppSettings
{
    public AppThemeMode Theme { get; set; } = AppThemeMode.System;

    public double ZoomFactor { get; set; } = 1.0;

    public ReaderThemeMode ReaderTheme { get; set; } = ReaderThemeMode.Auto;

    public bool ReaderThemePreferenceInitialized { get; set; }

    public ReaderFlowMode ReaderFlow { get; set; } = ReaderFlowMode.Paged;

    public int ReaderFontSize { get; set; } = 18;

    /// <summary>
    /// When true, keep EPUB/document fonts instead of forcing the app typeface.
    /// </summary>
    public bool UsePublisherFont { get; set; } = true;

    public double ReaderLineHeight { get; set; } = 1.9;

    public int ReaderContentWidth { get; set; } = 720;

    public ComicDisplayMode ComicDisplay { get; set; } = ComicDisplayMode.Single;

    public ComicReadingDirection ComicDirection { get; set; } = ComicReadingDirection.RightToLeft;

    public ComicFitMode ComicFit { get; set; } = ComicFitMode.Height;

    public bool ComicCoverSinglePage { get; set; } = true;

    public double ComicScale { get; set; } = 1.0;

    public double TotalReadingSeconds { get; set; }

    public List<string> ReadingDates { get; set; } = [];

    /// <summary>
    /// Optional full path to ebook-convert.exe. Empty means auto-detect embedded/system Calibre.
    /// </summary>
    public string CalibreEbookConvertPath { get; set; } = string.Empty;

    /// <summary>
    /// Default output directory for batch conversion. Empty means use source file directory.
    /// </summary>
    public string ConversionOutputDirectory { get; set; } = string.Empty;

    public string ConversionDefaultTargetExtension { get; set; } = ".epub";

    public bool SyncEnabled { get; set; }

    public SyncProviderKind SyncProvider { get; set; } = SyncProviderKind.None;

    public string SyncFolderPath { get; set; } = string.Empty;

    public bool SyncIncludeAnnotations { get; set; } = true;

    public bool SyncIncludeSettings { get; set; } = true;

    /// <summary>
    /// Last modification time of the settings subset that participates in cloud sync.
    /// </summary>
    public DateTimeOffset SyncSettingsModifiedUtc { get; set; } = DateTimeOffset.MinValue;

    public string LastSyncUtc { get; set; } = string.Empty;

    /// <summary>
    /// UI language code. Empty/system means follow OS; currently zh-CN and en-US are recognized.
    /// </summary>
    public string UiLanguage { get; set; } = string.Empty;

    public AppSettings Clone()
    {
        var clone = new AppSettings();
        clone.CopyFrom(this);
        return clone;
    }

    public void CopyFrom(AppSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Theme = source.Theme;
        ZoomFactor = source.ZoomFactor;
        ReaderTheme = source.ReaderTheme;
        ReaderThemePreferenceInitialized = source.ReaderThemePreferenceInitialized;
        ReaderFlow = source.ReaderFlow;
        ReaderFontSize = source.ReaderFontSize;
        UsePublisherFont = source.UsePublisherFont;
        ReaderLineHeight = source.ReaderLineHeight;
        ReaderContentWidth = source.ReaderContentWidth;
        ComicDisplay = source.ComicDisplay;
        ComicDirection = source.ComicDirection;
        ComicFit = source.ComicFit;
        ComicCoverSinglePage = source.ComicCoverSinglePage;
        ComicScale = source.ComicScale;
        TotalReadingSeconds = source.TotalReadingSeconds;
        ReadingDates = [.. source.ReadingDates];
        CalibreEbookConvertPath = source.CalibreEbookConvertPath;
        ConversionOutputDirectory = source.ConversionOutputDirectory;
        ConversionDefaultTargetExtension = source.ConversionDefaultTargetExtension;
        SyncEnabled = source.SyncEnabled;
        SyncProvider = source.SyncProvider;
        SyncFolderPath = source.SyncFolderPath;
        SyncIncludeAnnotations = source.SyncIncludeAnnotations;
        SyncIncludeSettings = source.SyncIncludeSettings;
        SyncSettingsModifiedUtc = source.SyncSettingsModifiedUtc;
        LastSyncUtc = source.LastSyncUtc;
        UiLanguage = source.UiLanguage;
    }
}
