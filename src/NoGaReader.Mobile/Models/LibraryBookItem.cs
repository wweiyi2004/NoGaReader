using NoGaReader.Models;

namespace NoGaReader.Mobile.Models;

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
