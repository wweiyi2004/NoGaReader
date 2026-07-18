namespace NoGaReader.Models;

public sealed record LibraryScanItem
{
    public required string Path { get; init; }

    public required string Title { get; init; }

    public string Author { get; init; } = string.Empty;

    public required string Format { get; init; }

    public long FileSize { get; init; }

    public DateTimeOffset LastModifiedUtc { get; init; }

    public byte[]? CoverBytes { get; init; }

    public string? CoverExtension { get; init; }

    public string? CoverPath { get; init; }

    public bool IsUnchanged { get; init; }
}

public sealed record LibraryScanSnapshot
{
    public required string Path { get; init; }

    public required string Title { get; init; }

    public string Author { get; init; } = string.Empty;

    public required string Format { get; init; }

    public long FileSize { get; init; }

    public DateTimeOffset? LastModifiedUtc { get; init; }

    public bool IsMissing { get; init; }
}

public sealed record LibraryScanOptions
{
    public const int DefaultMaximumFileCount = 50_000;

    public int MaximumFileCount { get; init; } = DefaultMaximumFileCount;

    public bool ReadBookMetadata { get; init; } = true;

    public bool IncludeCoverImages { get; init; } = true;

    public bool IncludeSubfolders { get; init; } = true;

    public IReadOnlyDictionary<string, LibraryScanSnapshot>? KnownBooks { get; init; }

    public bool PersistCoverImagesToCache { get; init; }

    public int MaximumReportedIssueCount { get; init; } = 500;
}

public sealed record LibraryScanProgress
{
    public required string CurrentPath { get; init; }

    public int CandidateFileCount { get; init; }

    public int ProcessedFileCount { get; init; }

    public int ImportedFileCount { get; init; }
}

public sealed record LibraryScanIssue(string Path, string Message);

public sealed record LibraryScanResult
{
    public required string RootDirectory { get; init; }

    public required IReadOnlyList<LibraryScanItem> Items { get; init; }

    public required IReadOnlyList<LibraryScanIssue> Issues { get; init; }

    public bool IsFileLimitReached { get; init; }

    public int CandidateFileCount { get; init; }
}
