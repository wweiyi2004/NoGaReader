namespace NoGaReader.Models;

public enum AnnotationType
{
    Bookmark,
    Highlight,
    Note
}

public sealed class LibraryBook
{
    public long Id { get; set; }

    public string Path { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Author { get; set; }

    public string Format { get; set; } = string.Empty;

    public string? CoverPath { get; set; }

    public long FileSize { get; set; }

    public DateTimeOffset? ModifiedUtc { get; set; }

    public DateTimeOffset AddedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastOpenedUtc { get; set; }

    public int SectionCount { get; set; } = 1;

    public bool IsMissing { get; set; }

    public ReaderLocation? Location { get; set; }

    public string ProgressText => IsMissing
        ? "等待重新定位"
        : Location is { } location
            ? $"已读 {Math.Clamp(location.DocumentProgress, 0, 1):P0}"
            : LastOpenedUtc is not null ? "最近打开" : "未开始";
}

public sealed class TextAnchor
{
    public string? StartPath { get; set; }

    public int StartOffset { get; set; }

    public string? EndPath { get; set; }

    public int EndOffset { get; set; }

    public string? ExactText { get; set; }

    public string? Prefix { get; set; }

    public string? Suffix { get; set; }

    public double Progress { get; set; }
}

public sealed class ReaderLocation
{
    public long BookId { get; set; }

    public int SectionIndex { get; set; }

    public string? Fragment { get; set; }

    public double SectionProgress { get; set; }

    public double DocumentProgress { get; set; }

    public TextAnchor? Anchor { get; set; }

    public string? TextQuote { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Annotation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public long BookId { get; set; }

    public AnnotationType Type { get; set; }

    public int SectionIndex { get; set; }

    public string? SectionPath { get; set; }

    public double SectionProgress { get; set; }

    public TextAnchor? Anchor { get; set; }

    public string? SelectedText { get; set; }

    public string? Note { get; set; }

    public string? Color { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record AnnotationTombstone(
    string AnnotationId,
    long BookId,
    DateTimeOffset DeletedUtc);

public sealed class LibraryFolder
{
    public long Id { get; set; }

    public string Path { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool IncludeSubfolders { get; set; } = true;

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastScannedAt { get; set; }
}

public sealed class SearchSection
{
    public int SectionIndex { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
}

public sealed record SearchIndexStamp
{
    public long SourceSize { get; init; }

    public DateTimeOffset? SourceModifiedUtc { get; init; }

    public int SectionCount { get; init; }

    public string IndexVersion { get; init; } = string.Empty;
}

public sealed class SearchHit
{
    public long BookId { get; set; }

    public int SectionIndex { get; set; }

    public string SectionTitle { get; set; } = string.Empty;

    public string Snippet { get; set; } = string.Empty;

    public double Rank { get; set; }

    /// <summary>
    /// Zero-based UTF-16 offset of this match in the indexed section text (or
    /// in <see cref="SectionTitle"/> when <see cref="MatchInTitle"/> is true).
    /// </summary>
    public int MatchStart { get; set; } = -1;

    /// <summary>
    /// Length of the actual matched text in UTF-16 code units.
    /// </summary>
    public int MatchLength { get; set; }

    /// <summary>
    /// Zero-based occurrence number within the matching field of this section.
    /// This lets the reader reveal the second and later equal strings precisely.
    /// </summary>
    public int OccurrenceIndex { get; set; }

    /// <summary>
    /// True when the occurrence belongs to the section title rather than its body.
    /// </summary>
    public bool MatchInTitle { get; set; }

    /// <summary>
    /// The source text at <see cref="MatchStart"/>, preserving its original case.
    /// </summary>
    public string MatchedText { get; set; } = string.Empty;

    /// <summary>
    /// Approximate normalized position in the indexed section text.
    /// </summary>
    public double SectionProgress { get; set; }
}
