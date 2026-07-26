using NoGaReader.Models;

namespace NoGaReader.Services;

/// <summary>
/// Display projection of a library book for the mobile shelf: precomputed
/// format label, cover availability, and progress text.
/// </summary>
public sealed record LibraryBookItem(
    LibraryBook Book,
    string Title,
    string Author,
    string FormatLabel,
    string? CoverPath,
    bool HasCover,
    bool HasFallbackCover,
    double Progress,
    string ProgressText)
{
    public static LibraryBookItem FromBook(LibraryBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        var progress = Math.Clamp(book.Location?.DocumentProgress ?? 0, 0, 1);
        var author = string.IsNullOrWhiteSpace(book.Author) ? "未知作者" : book.Author;
        var format = book.Format.TrimStart('.').ToUpperInvariant();
        var hasCover = !string.IsNullOrWhiteSpace(book.CoverPath) && File.Exists(book.CoverPath);
        return new LibraryBookItem(
            book,
            book.Title,
            author!,
            format,
            book.CoverPath,
            hasCover,
            !hasCover,
            progress,
            progress > 0 ? $"{progress:P0}" : "未开始");
    }
}

/// <summary>
/// Pure shelf state for the mobile library page: filtering, summary and
/// empty-state copy, and continue-reading selection. Mirrors the desktop
/// dashboard semantics and keeps the MAUI page a thin renderer.
/// </summary>
public sealed class MobileLibraryPresenter
{
    private IReadOnlyList<LibraryBookItem> _books = [];

    public IReadOnlyList<LibraryBookItem> Books => _books;

    /// <summary>
    /// The desktop dashboard's continue-reading pick: the most recently opened
    /// book that has actual progress, or null when nothing qualifies.
    /// </summary>
    public LibraryBookItem? ContinueReading { get; private set; }

    public string ContinueReadingSubtitle => ContinueReading is { } item
        ? $"{item.ProgressText} · {item.FormatLabel}"
        : string.Empty;

    public string Summary => _books.Count == 0
        ? "随身阅读，从一本书开始"
        : $"{_books.Count} 本书 · 数据仅保存在本机";

    public void SetBooks(IEnumerable<LibraryBook> books)
    {
        ArgumentNullException.ThrowIfNull(books);
        _books = books.Select(LibraryBookItem.FromBook).ToList();
        ContinueReading = _books
            .Where(item => item.Progress > 0)
            .OrderByDescending(item => item.Book.LastOpenedUtc ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
    }

    public LibraryView BuildView(string? searchQuery)
    {
        var query = searchQuery?.Trim();
        var visible = string.IsNullOrWhiteSpace(query)
            ? _books
            : _books.Where(book =>
                    book.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    book.Author.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        return new LibraryView(
            visible,
            Summary,
            _books.Count == 0 ? "书库还是空的" : "没有找到这本书",
            _books.Count == 0
                ? "从手机中选择一本书，导入后即可离线阅读。"
                : "换个书名或作者关键词再试试。",
            visible.Count == 0);
    }

    public sealed record LibraryView(
        IReadOnlyList<LibraryBookItem> VisibleBooks,
        string Summary,
        string EmptyTitle,
        string EmptyDescription,
        bool ShowEmptyState);
}
