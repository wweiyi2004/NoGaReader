using System.Text.Json.Serialization;

namespace NoGaReader.Models;

public enum AnnotationExportFormat
{
    Markdown,
    Json
}

/// <summary>
/// Portable annotation export contract. This contract intentionally does not expose
/// database identifiers, cached section paths, or DOM-specific anchor details.
/// </summary>
public sealed class AnnotationExportDocument
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("exportedAtUtc")]
    public DateTimeOffset ExportedAtUtc { get; init; }

    [JsonPropertyName("book")]
    public required AnnotationExportBook Book { get; init; }

    [JsonPropertyName("annotations")]
    public required IReadOnlyList<AnnotationExportItem> Annotations { get; init; }
}

public sealed class AnnotationExportBook
{
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("author")]
    public string? Author { get; init; }

    [JsonPropertyName("sourcePath")]
    public required string SourcePath { get; init; }

    [JsonPropertyName("format")]
    public string? Format { get; init; }
}

public sealed class AnnotationExportItem
{
    /// <summary>
    /// Stable lower-case value: bookmark, highlight, or note.
    /// </summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>
    /// One-based section number intended for interchange and display.
    /// </summary>
    [JsonPropertyName("sectionNumber")]
    public int SectionNumber { get; init; }

    [JsonPropertyName("sectionProgress")]
    public double SectionProgress { get; init; }

    [JsonPropertyName("quote")]
    public string? Quote { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }

    [JsonPropertyName("color")]
    public string? Color { get; init; }

    [JsonPropertyName("textSelector")]
    public AnnotationExportTextSelector? TextSelector { get; init; }

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; init; }

    [JsonPropertyName("modifiedAtUtc")]
    public DateTimeOffset ModifiedAtUtc { get; init; }
}

/// <summary>
/// A portable quote selector. Prefix and suffix help a future importer relocate
/// text without depending on WebView DOM paths.
/// </summary>
public sealed class AnnotationExportTextSelector
{
    [JsonPropertyName("exact")]
    public required string Exact { get; init; }

    [JsonPropertyName("prefix")]
    public string? Prefix { get; init; }

    [JsonPropertyName("suffix")]
    public string? Suffix { get; init; }
}
