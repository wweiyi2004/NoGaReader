namespace NoGaReader.Models;

public enum ReaderDocumentKind
{
    Epub,
    Pdf,
    Html,
    Text,
    Markdown,
    Image,
    Comic,
    FictionBook
}

public sealed record ReaderSection(string Title, string FullPath);

public sealed record ComicPage(int Index, string FullPath);

public sealed class ReaderSession
{
    public required string SourcePath { get; init; }

    public required string Title { get; init; }

    public required string RootDirectory { get; init; }

    public required ReaderDocumentKind Kind { get; init; }

    public required IReadOnlyList<ReaderSection> Sections { get; init; }

    public IReadOnlyList<TocNode> TableOfContents { get; init; } = [];

    public string? Author { get; init; }

    public string? CoverImagePath { get; init; }

    public bool IsReflowable { get; init; }

    public bool SupportsInPageSearch { get; init; }

    public bool EnableScriptExecution { get; init; }

    public IReadOnlyList<ComicPage> ComicPages { get; init; } = [];

    public string? ComicContentRootDirectory { get; init; }

    public int CurrentSectionIndex { get; set; }

    public ReaderSection CurrentSection => Sections[CurrentSectionIndex];
}
