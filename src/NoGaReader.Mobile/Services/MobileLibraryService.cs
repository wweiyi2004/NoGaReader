using NoGaReader.Models;
using NoGaReader.Services;

namespace NoGaReader.Mobile.Services;

/// <summary>
/// Thin Android adapter over the shared <see cref="PortableLibraryService"/>:
/// binds the app sandbox location and the platform file picker result. All
/// library behavior lives in Core where the smoke suite exercises it.
/// </summary>
public sealed class MobileLibraryService
{
    private readonly PortableLibraryService _library = new(
        Path.Combine(FileSystem.AppDataDirectory, "Books"));

    public string BooksDirectory => _library.BooksDirectory;

    public Task<IReadOnlyList<LibraryBook>> ListAsync(CancellationToken cancellationToken = default) =>
        _library.ListAsync(cancellationToken);

    public async Task<LibraryBook> ImportAsync(FileResult pickedFile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pickedFile);
        await using var source = await pickedFile.OpenReadAsync();
        return await _library.ImportAsync(source, pickedFile.FileName, cancellationToken);
    }

    public Task<ReaderSession> OpenAsync(LibraryBook book, CancellationToken cancellationToken = default) =>
        _library.OpenAsync(book, cancellationToken);

    public Task DeleteAsync(LibraryBook book, CancellationToken cancellationToken = default) =>
        _library.DeleteAsync(book, cancellationToken);

    public Task SaveLocationAsync(
        long bookId,
        ReaderSession session,
        double sectionProgress,
        CancellationToken cancellationToken = default) =>
        _library.SaveLocationAsync(bookId, session, sectionProgress, cancellationToken);

    public Task AddBookmarkAsync(
        long bookId,
        ReaderSession session,
        double sectionProgress,
        CancellationToken cancellationToken = default) =>
        _library.AddBookmarkAsync(bookId, session, sectionProgress, cancellationToken);

    public Task<IReadOnlyList<SearchHit>> SearchAsync(
        LibraryBook book,
        ReaderSession session,
        string query,
        CancellationToken cancellationToken = default) =>
        _library.SearchAsync(book, session, query, cancellationToken);

    public Task<IReadOnlyList<Annotation>> ListAnnotationsAsync(
        long bookId,
        int sectionIndex,
        CancellationToken cancellationToken = default) =>
        _library.ListAnnotationsAsync(bookId, sectionIndex, cancellationToken);

    public Task<Annotation> AddTextAnnotationAsync(
        long bookId,
        ReaderSession session,
        double sectionProgress,
        string selectedText,
        string? note,
        CancellationToken cancellationToken = default) =>
        _library.AddTextAnnotationAsync(bookId, session, sectionProgress, selectedText, note, cancellationToken);
}
