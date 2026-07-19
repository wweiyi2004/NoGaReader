namespace NoGaReader.Models;

public enum SyncProviderKind
{
    None,
    Folder
}

public sealed class SyncManifest
{
    public int SchemaVersion { get; set; } = 1;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string DeviceName { get; set; } = Environment.MachineName;

    public List<SyncBookState> Books { get; set; } = [];

    public SyncSettingsSnapshot? Settings { get; set; }
}

public sealed class SyncBookState
{
    public required string PathKey { get; set; }

    public string BookKey { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    public string Format { get; set; } = string.Empty;

    public string? SourcePathHint { get; set; }

    public long SourceSize { get; set; }

    public int? SectionIndex { get; set; }

    public double? SectionProgress { get; set; }

    public double? DocumentProgress { get; set; }

    public string? Fragment { get; set; }

    public string? AnchorJson { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<SyncAnnotationState> Annotations { get; set; } = [];
}

public sealed class SyncAnnotationState
{
    public string Id { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public bool IsDeleted { get; set; }

    public string? Color { get; set; }

    public string? Note { get; set; }

    public string? SelectedText { get; set; }

    public int SectionIndex { get; set; }

    public double SectionProgress { get; set; }

    public string? Fragment { get; set; }

    public string? AnchorJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SyncSettingsSnapshot
{
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.MinValue;

    public string Theme { get; set; } = "System";

    public string ReaderTheme { get; set; } = "Auto";

    public string ReaderFlow { get; set; } = "Paged";

    public int ReaderFontSize { get; set; } = 18;

    public bool UsePublisherFont { get; set; } = true;

    public double ReaderLineHeight { get; set; } = 1.9;

    public int ReaderContentWidth { get; set; } = 720;

    public string ComicDisplay { get; set; } = "Single";

    public string ComicDirection { get; set; } = "RightToLeft";

    public string ComicFit { get; set; } = "Height";

    public bool ComicCoverSinglePage { get; set; } = true;

    public double ComicScale { get; set; } = 1.0;

    public string UiLanguage { get; set; } = string.Empty;
}

public sealed class SyncResult
{
    public bool Succeeded { get; init; }

    public string Message { get; init; } = string.Empty;

    public int UploadedBooks { get; init; }

    public int DownloadedBooks { get; init; }

    public int MergedAnnotations { get; init; }
}
