using NoGaReader.Models;

namespace NoGaReader.Services;

/// <summary>
/// Book library backed by an app-managed import directory. Owns the shared
/// import/open/progress/annotation/search flows for portable reader hosts
/// (currently the Android client) so the logic stays testable off-device;
/// platform clients only adapt file pickers and UI on top of this service.
/// </summary>
public sealed class PortableLibraryService
{
    private readonly LibraryDatabase _database;
    private readonly PortableDocumentLoader _loader = new();
    private readonly BookSearchIndexer _searchIndexer = new();
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private bool _initialized;

    public PortableLibraryService(string booksDirectory, string? databasePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(booksDirectory);
        BooksDirectory = Path.GetFullPath(booksDirectory);
        _database = new LibraryDatabase(databasePath);
    }

    public string BooksDirectory { get; }

    public async Task<IReadOnlyList<LibraryBook>> ListAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await _database.ListBooksAsync(includeMissing: false, cancellationToken);
    }

    public async Task<LibraryBook> ImportAsync(
        Stream source,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var extension = Path.GetExtension(fileName);
        if (!DocumentFormatSupport.IsMobileMvpSupported(fileName))
        {
            throw new NotSupportedException(
                $"Android 版暂不支持 {extension.ToUpperInvariant()}。支持 EPUB、PDF、FB2、CBZ、TXT、Markdown、HTML 和常见图片。");
        }

        await EnsureInitializedAsync(cancellationToken);
        var destination = GetAvailableDestination(MakeSafeFileName(fileName));
        await using (var target = new FileStream(
                         destination,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         81920,
                         useAsync: true))
        {
            await source.CopyToAsync(target, cancellationToken);
        }

        try
        {
            var session = await _loader.LoadAsync(destination, cancellationToken);
            return await UpsertSessionAsync(session, cancellationToken);
        }
        catch
        {
            try
            {
                File.Delete(destination);
            }
            catch (Exception cleanupException) when (
                cleanupException is IOException or UnauthorizedAccessException)
            {
                // Keep the original import failure; a leftover copy is harmless.
            }

            throw;
        }
    }

    public async Task<ReaderSession> OpenAsync(LibraryBook book, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        await EnsureInitializedAsync(cancellationToken);
        var session = await _loader.LoadAsync(book.Path, cancellationToken);
        session.CurrentSectionIndex = Math.Clamp(
            book.Location?.SectionIndex ?? 0,
            0,
            session.Sections.Count - 1);
        var saved = await UpsertSessionAsync(session, cancellationToken);
        book.Id = saved.Id;
        return session;
    }

    public async Task DeleteAsync(LibraryBook book, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        await EnsureInitializedAsync(cancellationToken);
        await _database.RemoveBookAsync(book.Id, cancellationToken);

        if (File.Exists(book.Path) &&
            PathSemantics.IsInside(BooksDirectory, Path.GetFullPath(book.Path)))
        {
            File.Delete(book.Path);
        }
    }

    public async Task SaveLocationAsync(
        long bookId,
        ReaderSession session,
        double sectionProgress,
        CancellationToken cancellationToken = default)
    {
        var sectionIndex = Math.Clamp(session.CurrentSectionIndex, 0, session.Sections.Count - 1);
        var normalizedSectionProgress = Math.Clamp(sectionProgress, 0, 1);
        var documentProgress = Math.Clamp(
            (sectionIndex + normalizedSectionProgress) / Math.Max(1, session.Sections.Count),
            0,
            1);
        await _database.SaveReaderLocationAsync(new ReaderLocation
        {
            BookId = bookId,
            SectionIndex = sectionIndex,
            SectionProgress = normalizedSectionProgress,
            DocumentProgress = documentProgress,
            UpdatedUtc = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    public async Task AddBookmarkAsync(
        long bookId,
        ReaderSession session,
        double sectionProgress,
        CancellationToken cancellationToken = default)
    {
        await _database.UpsertAnnotationAsync(new Annotation
        {
            BookId = bookId,
            Type = AnnotationType.Bookmark,
            SectionIndex = session.CurrentSectionIndex,
            SectionPath = session.CurrentSection.FullPath,
            SectionProgress = Math.Clamp(sectionProgress, 0, 1),
            CreatedUtc = DateTimeOffset.UtcNow,
            ModifiedUtc = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        LibraryBook book,
        ReaderSession session,
        string query,
        CancellationToken cancellationToken = default)
    {
        await EnsureSearchIndexAsync(book, session, cancellationToken);
        return await _database.SearchAsync(query, book.Id, limit: 50, cancellationToken);
    }

    public Task<IReadOnlyList<Annotation>> ListAnnotationsAsync(
        long bookId,
        int sectionIndex,
        CancellationToken cancellationToken = default) =>
        _database.ListAnnotationsAsync(bookId, sectionIndex, cancellationToken: cancellationToken);

    public Task<Annotation> AddTextAnnotationAsync(
        long bookId,
        ReaderSession session,
        double sectionProgress,
        string selectedText,
        string? note,
        CancellationToken cancellationToken = default) =>
        _database.UpsertAnnotationAsync(new Annotation
        {
            BookId = bookId,
            Type = string.IsNullOrWhiteSpace(note) ? AnnotationType.Highlight : AnnotationType.Note,
            SectionIndex = session.CurrentSectionIndex,
            SectionPath = session.CurrentSection.FullPath,
            SectionProgress = Math.Clamp(sectionProgress, 0, 1),
            SelectedText = selectedText.Trim(),
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            Color = "yellow",
            CreatedUtc = DateTimeOffset.UtcNow,
            ModifiedUtc = DateTimeOffset.UtcNow
        }, cancellationToken);

    private async Task EnsureSearchIndexAsync(
        LibraryBook book,
        ReaderSession session,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(session.SourcePath);
        var stamp = new SearchIndexStamp
        {
            SourceSize = info.Exists ? info.Length : 0,
            SourceModifiedUtc = info.Exists
                ? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)
                : null,
            SectionCount = session.Sections.Count,
            IndexVersion = BookSearchIndexer.IndexVersion
        };
        if (await _database.IsSearchIndexCurrentAsync(book.Id, stamp, cancellationToken))
        {
            return;
        }

        await _indexGate.WaitAsync(cancellationToken);
        try
        {
            if (await _database.IsSearchIndexCurrentAsync(book.Id, stamp, cancellationToken))
            {
                return;
            }

            var sections = await _searchIndexer.BuildAsync(session, cancellationToken);
            await _database.ReplaceSearchIndexAsync(book.Id, sections, stamp, cancellationToken);
        }
        finally
        {
            _indexGate.Release();
        }
    }

    private async Task<LibraryBook> UpsertSessionAsync(
        ReaderSession session,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(session.SourcePath);
        return await _database.UpsertBookAsync(new LibraryBook
        {
            Path = session.SourcePath,
            Title = session.Title,
            Author = session.Author,
            Format = Path.GetExtension(session.SourcePath),
            CoverPath = session.CoverImagePath,
            FileSize = info.Exists ? info.Length : 0,
            ModifiedUtc = info.Exists ? info.LastWriteTimeUtc : null,
            LastOpenedUtc = DateTimeOffset.UtcNow,
            SectionCount = Math.Max(1, session.Sections.Count)
        }, cancellationToken);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (!_initialized)
            {
                Directory.CreateDirectory(BooksDirectory);
                await _database.InitializeAsync(cancellationToken);
                _initialized = true;
            }
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private string GetAvailableDestination(string fileName)
    {
        var candidate = Path.Combine(BooksDirectory, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var suffix = 2; File.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(BooksDirectory, $"{stem} ({suffix}){extension}");
        }

        return candidate;
    }

    private static string MakeSafeFileName(string name)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var safe = string.Concat(Path.GetFileName(name).Select(character =>
            invalidCharacters.Contains(character) ? '_' : character));
        if (string.IsNullOrWhiteSpace(safe) || safe is "." or "..")
        {
            return $"book-{Guid.NewGuid():N}";
        }

        return safe;
    }
}
