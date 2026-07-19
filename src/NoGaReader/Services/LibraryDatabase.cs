using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NoGaReader.Models;

namespace NoGaReader.Services;

/// <summary>
/// Owns NoGaReader's local library database. Every operation uses a short-lived
/// connection; SQLite WAL and busy timeouts coordinate concurrent callers.
/// </summary>
public sealed class LibraryDatabase
{
    private const int CurrentSchemaVersion = 4;
    private const int MaxSearchQueryLength = 512;
    private const int MaxSearchResultCount = 200;
    private const int MaxFtsCandidateSections = 1000;
    private const int MinimumFtsCandidateSections = 64;
    private const int SearchSnippetRadius = 64;
    private const string BookColumns =
        "id, source_path, title, author, format, cover_path, file_size, " +
        "file_modified_utc, added_utc, last_opened_utc, section_count, is_missing, tags";

    private static readonly SemaphoreSlim MigrationGate = new(1, 1);
    private readonly string _connectionString;
    private volatile bool _initialized;
    private int _ftsAvailable = -1;

    public LibraryDatabase(string? databasePath = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            AppPaths.EnsureCreated();
            DatabasePath = Path.Combine(AppPaths.DataRoot, "library.db");
        }
        else
        {
            DatabasePath = Path.GetFullPath(databasePath);
            var directory = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 5
        }.ToString();

    }

    public string DatabasePath { get; }

    public bool IsFullTextSearchAvailable => Volatile.Read(ref _ftsAvailable) == 1;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await MigrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = await OpenConnectionCoreAsync(cancellationToken).ConfigureAwait(false);

            await using (var wal = connection.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode = WAL;";
                await wal.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }

            var version = await GetSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            if (version > CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"library.db 的版本 {version} 高于当前程序支持的版本 {CurrentSchemaVersion}。");
            }

            if (version < 1)
            {
                await MigrateToVersion1Async(connection, cancellationToken).ConfigureAwait(false);
            }

            if (version < 2)
            {
                await MigrateToVersion2Async(connection, cancellationToken).ConfigureAwait(false);
            }

            if (version < 3)
            {
                await MigrateToVersion3Async(connection, cancellationToken).ConfigureAwait(false);
            }

            if (version < 4)
            {
                await MigrateToVersion4Async(connection, cancellationToken).ConfigureAwait(false);
            }

            await EnsureFtsAsync(connection, cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            MigrationGate.Release();
        }
    }

    public async Task<LibraryBook> UpsertBookAsync(
        LibraryBook book,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        if (string.IsNullOrWhiteSpace(book.Path))
        {
            throw new ArgumentException("书籍路径不能为空。", nameof(book));
        }

        var sourcePath = NormalizePath(book.Path);
        var pathKey = ToPathKey(sourcePath);
        var now = DateTimeOffset.UtcNow;
        var addedAt = book.AddedUtc == default ? now : book.AddedUtc;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO library_books (
                    source_path, path_key, title, author, format, cover_path, file_size,
                    file_modified_utc, added_utc, last_opened_utc, section_count, is_missing, tags)
                VALUES (
                    $sourcePath, $pathKey, $title, $author, $format, $coverPath, $fileSize,
                    $fileModifiedUtc, $addedUtc, $lastOpenedUtc, $sectionCount, $isMissing, $tags)
                ON CONFLICT(path_key) DO UPDATE SET
                    source_path = excluded.source_path,
                    title = excluded.title,
                    author = excluded.author,
                    format = excluded.format,
                    cover_path = excluded.cover_path,
                    file_size = excluded.file_size,
                    file_modified_utc = excluded.file_modified_utc,
                    last_opened_utc = excluded.last_opened_utc,
                    section_count = excluded.section_count,
                    is_missing = excluded.is_missing,
                    tags = CASE
                        WHEN $tagsSpecified = 1 THEN excluded.tags
                        ELSE library_books.tags
                    END;
                """;
            command.Parameters.AddWithValue("$sourcePath", sourcePath);
            command.Parameters.AddWithValue("$pathKey", pathKey);
            command.Parameters.AddWithValue("$title", EmptyIfNull(book.Title));
            command.Parameters.AddWithValue("$author", DbValue(book.Author));
            command.Parameters.AddWithValue("$format", EmptyIfNull(book.Format));
            command.Parameters.AddWithValue("$coverPath", DbValue(book.CoverPath));
            command.Parameters.AddWithValue("$fileSize", Math.Max(0, book.FileSize));
            command.Parameters.AddWithValue("$fileModifiedUtc", DbTimestamp(book.ModifiedUtc));
            command.Parameters.AddWithValue("$addedUtc", ToTimestamp(addedAt));
            command.Parameters.AddWithValue("$lastOpenedUtc", DbTimestamp(book.LastOpenedUtc));
            command.Parameters.AddWithValue("$sectionCount", Math.Max(1, book.SectionCount));
            command.Parameters.AddWithValue("$isMissing", book.IsMissing ? 1 : 0);
            command.Parameters.AddWithValue("$tags", DbValue(NormalizeTags(book.Tags)));
            command.Parameters.AddWithValue("$tagsSpecified", book.Tags is null ? 0 : 1);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        LibraryBook saved;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = $"SELECT {BookColumns} FROM library_books WHERE path_key = $pathKey;";
            select.Parameters.AddWithValue("$pathKey", pathKey);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("保存书籍后无法读取数据库记录。");
            }

            saved = ReadBook(reader);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return saved;
    }

    public async Task<IReadOnlyList<LibraryBook>> ListBooksAsync(
        bool includeMissing = true,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT {BookColumns},
                    locations.section_index, locations.section_progress,
                    locations.document_progress, locations.updated_utc
             FROM library_books
             LEFT JOIN reader_locations AS locations ON locations.book_id = library_books.id
             WHERE $includeMissing = 1 OR is_missing = 0
             ORDER BY COALESCE(last_opened_utc, added_utc) DESC, title COLLATE NOCASE;
             """;
        command.Parameters.AddWithValue("$includeMissing", includeMissing ? 1 : 0);

        var books = new List<LibraryBook>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var book = ReadBook(reader);
            if (!reader.IsDBNull(13))
            {
                book.Location = new ReaderLocation
                {
                    BookId = book.Id,
                    SectionIndex = reader.GetInt32(13),
                    SectionProgress = reader.GetDouble(14),
                    DocumentProgress = reader.GetDouble(15),
                    UpdatedUtc = FromTimestamp(reader.GetInt64(16))
                };
            }
            books.Add(book);
        }

        return books;
    }

    public async Task<LibraryBook?> FindBookByIdAsync(
        long bookId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {BookColumns} FROM library_books WHERE id = $id;";
        command.Parameters.AddWithValue("$id", bookId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadBook(reader) : null;
    }

    public async Task<LibraryBook?> FindBookByPathAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var pathKey = ToPathKey(NormalizePath(sourcePath));
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {BookColumns} FROM library_books WHERE path_key = $pathKey;";
        command.Parameters.AddWithValue("$pathKey", pathKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadBook(reader) : null;
    }

    public async Task<LibraryBook> RelocateBookAsync(
        long bookId,
        string newSourcePath,
        CancellationToken cancellationToken = default)
    {
        if (bookId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bookId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(newSourcePath);
        var sourcePath = NormalizePath(newSourcePath);
        var pathKey = ToPathKey(sourcePath);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using (var collision = connection.CreateCommand())
        {
            collision.Transaction = transaction;
            collision.CommandText = "SELECT id FROM library_books WHERE path_key = $pathKey AND id <> $id;";
            collision.Parameters.AddWithValue("$pathKey", pathKey);
            collision.Parameters.AddWithValue("$id", bookId);
            if (await collision.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                throw new InvalidOperationException("新位置已经属于书库中的另一本记录。");
            }
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE library_books SET source_path = $path, path_key = $pathKey, is_missing = 0 WHERE id = $id;";
            update.Parameters.AddWithValue("$path", sourcePath);
            update.Parameters.AddWithValue("$pathKey", pathKey);
            update.Parameters.AddWithValue("$id", bookId);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                throw new InvalidOperationException("要重新定位的书库记录不存在。");
            }
        }

        LibraryBook relocated;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = $"SELECT {BookColumns} FROM library_books WHERE id = $id;";
            select.Parameters.AddWithValue("$id", bookId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("重新定位后无法读取书库记录。");
            }

            relocated = ReadBook(reader);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return relocated;
    }

    public async Task<LibraryBook> MergeBookRecordsAsync(
        long preservedBookId,
        long duplicateBookId,
        string newSourcePath,
        CancellationToken cancellationToken = default)
    {
        if (preservedBookId <= 0 || duplicateBookId <= 0 || preservedBookId == duplicateBookId)
        {
            throw new ArgumentOutOfRangeException(nameof(duplicateBookId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(newSourcePath);
        var sourcePath = NormalizePath(newSourcePath);
        var pathKey = ToPathKey(sourcePath);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();

        async Task<bool> BookExistsAsync(long id)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT 1 FROM library_books WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
        }

        if (!await BookExistsAsync(preservedBookId) || !await BookExistsAsync(duplicateBookId))
        {
            throw new InvalidOperationException("要合并的书库记录不存在。");
        }

        await using (var annotations = connection.CreateCommand())
        {
            annotations.Transaction = transaction;
            annotations.CommandText = "UPDATE annotations SET book_id = $preservedId WHERE book_id = $duplicateId;";
            annotations.Parameters.AddWithValue("$preservedId", preservedBookId);
            annotations.Parameters.AddWithValue("$duplicateId", duplicateBookId);
            await annotations.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var tombstones = connection.CreateCommand())
        {
            tombstones.Transaction = transaction;
            tombstones.CommandText =
                "UPDATE annotation_tombstones SET book_id = $preservedId WHERE book_id = $duplicateId;";
            tombstones.Parameters.AddWithValue("$preservedId", preservedBookId);
            tombstones.Parameters.AddWithValue("$duplicateId", duplicateBookId);
            await tombstones.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var preservedHasLocation = false;
        await using (var locationCheck = connection.CreateCommand())
        {
            locationCheck.Transaction = transaction;
            locationCheck.CommandText = "SELECT 1 FROM reader_locations WHERE book_id = $id;";
            locationCheck.Parameters.AddWithValue("$id", preservedBookId);
            preservedHasLocation = await locationCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
        }

        await using (var location = connection.CreateCommand())
        {
            location.Transaction = transaction;
            location.CommandText = preservedHasLocation
                ? "DELETE FROM reader_locations WHERE book_id = $duplicateId;"
                : "UPDATE reader_locations SET book_id = $preservedId WHERE book_id = $duplicateId;";
            location.Parameters.AddWithValue("$preservedId", preservedBookId);
            location.Parameters.AddWithValue("$duplicateId", duplicateBookId);
            await location.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var search = connection.CreateCommand())
        {
            search.Transaction = transaction;
            search.CommandText =
                "DELETE FROM search_sections WHERE book_id = $preservedId; " +
                "UPDATE search_sections SET book_id = $preservedId WHERE book_id = $duplicateId;";
            search.Parameters.AddWithValue("$preservedId", preservedBookId);
            search.Parameters.AddWithValue("$duplicateId", duplicateBookId);
            await search.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM library_books WHERE id = $duplicateId;";
            delete.Parameters.AddWithValue("$duplicateId", duplicateBookId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE library_books SET source_path = $path, path_key = $pathKey, is_missing = 0 WHERE id = $id;";
            update.Parameters.AddWithValue("$path", sourcePath);
            update.Parameters.AddWithValue("$pathKey", pathKey);
            update.Parameters.AddWithValue("$id", preservedBookId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        LibraryBook merged;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = $"SELECT {BookColumns} FROM library_books WHERE id = $id;";
            select.Parameters.AddWithValue("$id", preservedBookId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("合并后无法读取书库记录。");
            }

            merged = ReadBook(reader);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return merged;
    }

    public async Task<bool> RemoveBookAsync(long bookId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM library_books WHERE id = $id;";
        command.Parameters.AddWithValue("$id", bookId);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    public async Task<bool> RemoveBookByPathAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var pathKey = ToPathKey(NormalizePath(sourcePath));
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM library_books WHERE path_key = $pathKey;";
        command.Parameters.AddWithValue("$pathKey", pathKey);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    public async Task SaveReaderLocationAsync(
        ReaderLocation location,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (location.BookId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(location), "阅读位置必须关联书籍。");
        }

        location.SectionIndex = Math.Max(0, location.SectionIndex);
        location.SectionProgress = Math.Clamp(location.SectionProgress, 0, 1);
        location.DocumentProgress = Math.Clamp(location.DocumentProgress, 0, 1);
        location.UpdatedUtc = location.UpdatedUtc == default ? DateTimeOffset.UtcNow : location.UpdatedUtc;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO reader_locations (
                book_id, section_index, section_key, section_progress,
                document_progress, anchor_json, text_quote, updated_utc)
            VALUES (
                $bookId, $sectionIndex, $sectionKey, $sectionProgress,
                $documentProgress, $anchorJson, $textQuote, $updatedUtc)
            ON CONFLICT(book_id) DO UPDATE SET
                section_index = excluded.section_index,
                section_key = excluded.section_key,
                section_progress = excluded.section_progress,
                document_progress = excluded.document_progress,
                anchor_json = excluded.anchor_json,
                text_quote = excluded.text_quote,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$bookId", location.BookId);
        command.Parameters.AddWithValue("$sectionIndex", location.SectionIndex);
        command.Parameters.AddWithValue("$sectionKey", DbValue(location.Fragment));
        command.Parameters.AddWithValue("$sectionProgress", location.SectionProgress);
        command.Parameters.AddWithValue("$documentProgress", location.DocumentProgress);
        command.Parameters.AddWithValue("$anchorJson", DbValue(SerializeAnchor(location.Anchor)));
        command.Parameters.AddWithValue("$textQuote", DbValue(location.TextQuote));
        command.Parameters.AddWithValue("$updatedUtc", ToTimestamp(location.UpdatedUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReaderLocation?> GetReaderLocationAsync(
        long bookId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT book_id, section_index, section_key, section_progress,
                   document_progress, anchor_json, text_quote, updated_utc
            FROM reader_locations
            WHERE book_id = $bookId;
            """;
        command.Parameters.AddWithValue("$bookId", bookId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ReaderLocation
        {
            BookId = reader.GetInt64(0),
            SectionIndex = reader.GetInt32(1),
            Fragment = GetNullableString(reader, 2),
            SectionProgress = reader.GetDouble(3),
            DocumentProgress = reader.GetDouble(4),
            Anchor = DeserializeAnchor(GetNullableString(reader, 5)),
            TextQuote = GetNullableString(reader, 6),
            UpdatedUtc = FromTimestamp(reader.GetInt64(7))
        };
    }

    public async Task<Annotation> UpsertAnnotationAsync(
        Annotation annotation,
        CancellationToken cancellationToken = default)
    {
        await UpsertAnnotationsAsync([annotation], cancellationToken).ConfigureAwait(false);

        return await FindAnnotationAsync(annotation.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("保存批注后无法读取数据库记录。");
    }

    public async Task UpsertAnnotationsAsync(
        IEnumerable<Annotation> annotations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        var batch = annotations.ToList();
        if (batch.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var annotation in batch)
        {
            PrepareAnnotationForUpsert(annotation, now);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText =
            """
            INSERT INTO annotations (
                id, book_id, annotation_type, section_index, section_key,
                section_progress, anchor_json, selected_text, note_text, color, created_utc, updated_utc)
            VALUES (
                $id, $bookId, $type, $sectionIndex, $sectionKey,
                $sectionProgress, $anchorJson, $selectedText, $note, $color, $createdUtc, $updatedUtc)
            ON CONFLICT(id) DO UPDATE SET
                book_id = excluded.book_id,
                annotation_type = excluded.annotation_type,
                section_index = excluded.section_index,
                section_key = excluded.section_key,
                section_progress = excluded.section_progress,
                anchor_json = excluded.anchor_json,
                selected_text = excluded.selected_text,
                note_text = excluded.note_text,
                color = excluded.color,
                updated_utc = excluded.updated_utc;
            """;

        await using var clearTombstone = connection.CreateCommand();
        clearTombstone.Transaction = transaction;
        clearTombstone.CommandText = "DELETE FROM annotation_tombstones WHERE annotation_id = $id;";
        var tombstoneId = clearTombstone.Parameters.Add("$id", SqliteType.Text);

        foreach (var annotation in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            upsert.Parameters.Clear();
            AddAnnotationParameters(upsert, annotation);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            tombstoneId.Value = annotation.Id;
            await clearTombstone.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Annotation?> FindAnnotationAsync(
        string annotationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(annotationId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, book_id, annotation_type, section_index, section_key,
                   section_progress, anchor_json, selected_text, note_text, color, created_utc, updated_utc
            FROM annotations
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", annotationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadAnnotation(reader)
            : null;
    }

    public async Task<IReadOnlyList<Annotation>> ListAnnotationsAsync(
        long bookId,
        int? sectionIndex = null,
        AnnotationType? type = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, book_id, annotation_type, section_index, section_key,
                   section_progress, anchor_json, selected_text, note_text, color, created_utc, updated_utc
            FROM annotations
            WHERE book_id = $bookId
              AND ($sectionIndex IS NULL OR section_index = $sectionIndex)
              AND ($type IS NULL OR annotation_type = $type)
            ORDER BY section_index, section_progress, created_utc;
            """;
        command.Parameters.AddWithValue("$bookId", bookId);
        command.Parameters.AddWithValue("$sectionIndex", DbValue(sectionIndex));
        command.Parameters.AddWithValue("$type", DbValue(type is null ? null : (int)type.Value));

        var annotations = new List<Annotation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            annotations.Add(ReadAnnotation(reader));
        }

        return annotations;
    }

    public async Task<bool> DeleteAnnotationAsync(
        string annotationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(annotationId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();

        long? bookId;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT book_id FROM annotations WHERE id = $id;";
            select.Parameters.AddWithValue("$id", annotationId);
            bookId = await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long value
                ? value
                : null;
        }

        if (bookId is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await DeleteAnnotationAndRecordTombstoneAsync(
            connection,
            transaction,
            annotationId,
            bookId.Value,
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task ApplyAnnotationDeletionAsync(
        string annotationId,
        long bookId,
        DateTimeOffset deletedUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(annotationId);
        if (bookId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bookId));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await DeleteAnnotationAndRecordTombstoneAsync(
            connection,
            transaction,
            annotationId,
            bookId,
            deletedUtc == default ? DateTimeOffset.UtcNow : deletedUtc,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AnnotationTombstone>> ListAnnotationTombstonesAsync(
        long bookId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT annotation_id, book_id, deleted_utc FROM annotation_tombstones " +
            "WHERE book_id = $bookId ORDER BY deleted_utc;";
        command.Parameters.AddWithValue("$bookId", bookId);

        var tombstones = new List<AnnotationTombstone>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tombstones.Add(new AnnotationTombstone(
                reader.GetString(0),
                reader.GetInt64(1),
                FromTimestamp(reader.GetInt64(2))));
        }

        return tombstones;
    }

    public async Task<LibraryFolder> UpsertLibraryFolderAsync(
        LibraryFolder folder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (string.IsNullOrWhiteSpace(folder.Path))
        {
            throw new ArgumentException("书库文件夹路径不能为空。", nameof(folder));
        }

        var folderPath = NormalizePath(folder.Path);
        var pathKey = ToPathKey(folderPath);
        var addedAt = folder.AddedAt == default ? DateTimeOffset.UtcNow : folder.AddedAt;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO library_folders (
                    folder_path, path_key, name, include_subfolders, added_utc, last_scanned_utc)
                VALUES ($path, $pathKey, $name, $recursive, $addedUtc, $lastScannedUtc)
                ON CONFLICT(path_key) DO UPDATE SET
                    folder_path = excluded.folder_path,
                    name = excluded.name,
                    include_subfolders = excluded.include_subfolders,
                    last_scanned_utc = excluded.last_scanned_utc;
                """;
            command.Parameters.AddWithValue("$path", folderPath);
            command.Parameters.AddWithValue("$pathKey", pathKey);
            command.Parameters.AddWithValue("$name", EmptyIfNull(folder.Name));
            command.Parameters.AddWithValue("$recursive", folder.IncludeSubfolders ? 1 : 0);
            command.Parameters.AddWithValue("$addedUtc", ToTimestamp(addedAt));
            command.Parameters.AddWithValue("$lastScannedUtc", DbTimestamp(folder.LastScannedAt));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        LibraryFolder saved;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                """
                SELECT id, folder_path, name, include_subfolders, added_utc, last_scanned_utc
                FROM library_folders
                WHERE path_key = $pathKey;
                """;
            select.Parameters.AddWithValue("$pathKey", pathKey);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("保存书库文件夹后无法读取数据库记录。");
            }

            saved = ReadFolder(reader);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return saved;
    }

    public async Task<IReadOnlyList<LibraryFolder>> ListLibraryFoldersAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, folder_path, name, include_subfolders, added_utc, last_scanned_utc
            FROM library_folders
            ORDER BY name COLLATE NOCASE, folder_path COLLATE NOCASE;
            """;
        var folders = new List<LibraryFolder>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            folders.Add(ReadFolder(reader));
        }

        return folders;
    }

    public async Task<LibraryFolder?> FindLibraryFolderByPathAsync(
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        var pathKey = ToPathKey(NormalizePath(folderPath));
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, folder_path, name, include_subfolders, added_utc, last_scanned_utc
            FROM library_folders
            WHERE path_key = $pathKey;
            """;
        command.Parameters.AddWithValue("$pathKey", pathKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadFolder(reader) : null;
    }

    public async Task<bool> RemoveLibraryFolderAsync(
        long folderId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM library_folders WHERE id = $id;";
        command.Parameters.AddWithValue("$id", folderId);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    public async Task<bool> RemoveLibraryFolderByPathAsync(
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        var pathKey = ToPathKey(NormalizePath(folderPath));
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM library_folders WHERE path_key = $pathKey;";
        command.Parameters.AddWithValue("$pathKey", pathKey);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    public async Task ReplaceSearchIndexAsync(
        long bookId,
        IEnumerable<SearchSection> sections,
        CancellationToken cancellationToken = default)
    {
        await ReplaceSearchIndexCoreAsync(bookId, sections, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceSearchIndexAsync(
        long bookId,
        IEnumerable<SearchSection> sections,
        SearchIndexStamp stamp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        await ReplaceSearchIndexCoreAsync(bookId, sections, stamp, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsSearchIndexCurrentAsync(
        long bookId,
        SearchIndexStamp stamp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM search_index_state
            WHERE book_id = $bookId
              AND source_size = $sourceSize
              AND (($sourceModifiedUtc IS NULL AND source_modified_utc IS NULL)
                   OR source_modified_utc = $sourceModifiedUtc)
              AND section_count = $sectionCount
              AND index_version = $indexVersion;
            """;
        command.Parameters.AddWithValue("$bookId", bookId);
        command.Parameters.AddWithValue("$sourceSize", Math.Max(0, stamp.SourceSize));
        command.Parameters.AddWithValue("$sourceModifiedUtc", DbTimestamp(stamp.SourceModifiedUtc));
        command.Parameters.AddWithValue("$sectionCount", Math.Max(0, stamp.SectionCount));
        command.Parameters.AddWithValue("$indexVersion", EmptyIfNull(stamp.IndexVersion));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private async Task ReplaceSearchIndexCoreAsync(
        long bookId,
        IEnumerable<SearchSection> sections,
        SearchIndexStamp? stamp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var materialized = sections
            .OrderBy(section => section.SectionIndex)
            .ToList();
        if (materialized.Any(section => section.SectionIndex < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(sections), "章节序号不能为负数。");
        }

        if (materialized.Select(section => section.SectionIndex).Distinct().Count() != materialized.Count)
        {
            throw new ArgumentException("同一本书不能包含重复的章节序号。", nameof(sections));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM search_sections WHERE book_id = $bookId;";
            delete.Parameters.AddWithValue("$bookId", bookId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var section in materialized)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO search_sections (book_id, section_index, section_key, title, content)
                VALUES ($bookId, $sectionIndex, $sectionKey, $title, $content);
                """;
            insert.Parameters.AddWithValue("$bookId", bookId);
            insert.Parameters.AddWithValue("$sectionIndex", section.SectionIndex);
            insert.Parameters.AddWithValue("$sectionKey", DBNull.Value);
            insert.Parameters.AddWithValue("$title", EmptyIfNull(section.Title));
            insert.Parameters.AddWithValue("$content", EmptyIfNull(section.Text));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            if (stamp is null)
            {
                state.CommandText = "DELETE FROM search_index_state WHERE book_id = $bookId;";
                state.Parameters.AddWithValue("$bookId", bookId);
            }
            else
            {
                state.CommandText =
                    """
                    INSERT INTO search_index_state (
                        book_id, source_size, source_modified_utc, section_count, index_version, indexed_utc)
                    VALUES (
                        $bookId, $sourceSize, $sourceModifiedUtc, $sectionCount, $indexVersion, $indexedUtc)
                    ON CONFLICT(book_id) DO UPDATE SET
                        source_size = excluded.source_size,
                        source_modified_utc = excluded.source_modified_utc,
                        section_count = excluded.section_count,
                        index_version = excluded.index_version,
                        indexed_utc = excluded.indexed_utc;
                    """;
                state.Parameters.AddWithValue("$bookId", bookId);
                state.Parameters.AddWithValue("$sourceSize", Math.Max(0, stamp.SourceSize));
                state.Parameters.AddWithValue("$sourceModifiedUtc", DbTimestamp(stamp.SourceModifiedUtc));
                state.Parameters.AddWithValue("$sectionCount", Math.Max(0, stamp.SectionCount));
                state.Parameters.AddWithValue("$indexVersion", EmptyIfNull(stamp.IndexVersion));
                state.Parameters.AddWithValue("$indexedUtc", ToTimestamp(DateTimeOffset.UtcNow));
            }

            await state.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        string query,
        long? bookId = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<SearchHit>();
        }

        query = query.Trim();
        if (query.Length > MaxSearchQueryLength)
        {
            // Search text is user input. Refuse pathological pasted input without
            // throwing through an async UI event or allocating a huge LIKE pattern.
            return Array.Empty<SearchHit>();
        }

        limit = Math.Clamp(limit, 1, MaxSearchResultCount);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        if (Volatile.Read(ref _ftsAvailable) == 1 && CanUseTrigramSearch(query))
        {
            try
            {
                return await SearchFtsAsync(query, bookId, limit, cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 1)
            {
                // A safely quoted query should not normally fail. If a platform
                // nevertheless rejects the MATCH syntax, fall back for this query;
                // only disable FTS when the optional FTS facility itself is absent.
                if (IsFtsUnavailableError(exception))
                {
                    Volatile.Write(ref _ftsAvailable, 0);
                }
            }
        }

        return await SearchLikeAsync(query, bookId, limit, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<SearchHit>> SearchFtsAsync(
        string query,
        long? bookId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.book_id, s.section_index, s.title, s.content,
                   bm25(search_sections_fts)
            FROM search_sections_fts
            JOIN search_sections AS s ON s.id = search_sections_fts.rowid
            WHERE search_sections_fts MATCH $query
              AND ($bookId IS NULL OR s.book_id = $bookId)
            ORDER BY bm25(search_sections_fts), s.section_index
            LIMIT $candidateLimit;
            """;
        command.Parameters.AddWithValue("$query", BuildFtsQuery(query));
        command.Parameters.AddWithValue("$bookId", DbValue(bookId));
        command.Parameters.AddWithValue(
            "$candidateLimit",
            Math.Clamp(limit * 8, MinimumFtsCandidateSections, MaxFtsCandidateSections));

        var hits = new List<SearchHit>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (hits.Count < limit && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendOccurrenceHits(
                hits,
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                query,
                reader.IsDBNull(4) ? 0 : reader.GetDouble(4),
                limit,
                cancellationToken);
        }

        return hits;
    }

    private async Task<IReadOnlyList<SearchHit>> SearchLikeAsync(
        string query,
        long? bookId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT book_id, section_index, title, content
            FROM search_sections
            WHERE ($bookId IS NULL OR book_id = $bookId)
              AND (title LIKE $pattern ESCAPE '\' COLLATE NOCASE
                   OR content LIKE $pattern ESCAPE '\' COLLATE NOCASE)
            ORDER BY book_id, section_index
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$bookId", DbValue(bookId));
        command.Parameters.AddWithValue("$pattern", $"%{EscapeLike(query)}%");
        command.Parameters.AddWithValue("$limit", limit);

        var hits = new List<SearchHit>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (hits.Count < limit && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendOccurrenceHits(
                hits,
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                query,
                0,
                limit,
                cancellationToken);
        }

        return hits;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await OpenConnectionCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenConnectionCoreAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                PRAGMA foreign_keys = ON;
                PRAGMA busy_timeout = 5000;
                PRAGMA synchronous = NORMAL;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<int> GetSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task MigrateToVersion1Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS library_books (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_path TEXT NOT NULL,
                path_key TEXT NOT NULL UNIQUE,
                title TEXT NOT NULL,
                author TEXT,
                format TEXT NOT NULL,
                cover_path TEXT,
                file_size INTEGER NOT NULL DEFAULT 0 CHECK(file_size >= 0),
                file_modified_utc INTEGER,
                added_utc INTEGER NOT NULL,
                last_opened_utc INTEGER,
                section_count INTEGER NOT NULL DEFAULT 1 CHECK(section_count > 0),
                is_missing INTEGER NOT NULL DEFAULT 0 CHECK(is_missing IN (0, 1))
            );

            CREATE TABLE IF NOT EXISTS reader_locations (
                book_id INTEGER PRIMARY KEY,
                section_index INTEGER NOT NULL DEFAULT 0 CHECK(section_index >= 0),
                section_key TEXT,
                section_progress REAL NOT NULL DEFAULT 0 CHECK(section_progress BETWEEN 0 AND 1),
                document_progress REAL NOT NULL DEFAULT 0 CHECK(document_progress BETWEEN 0 AND 1),
                anchor_json TEXT,
                text_quote TEXT,
                updated_utc INTEGER NOT NULL,
                FOREIGN KEY(book_id) REFERENCES library_books(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS annotations (
                id TEXT PRIMARY KEY,
                book_id INTEGER NOT NULL,
                annotation_type INTEGER NOT NULL CHECK(annotation_type BETWEEN 0 AND 2),
                section_index INTEGER NOT NULL DEFAULT 0 CHECK(section_index >= 0),
                section_key TEXT,
                section_progress REAL NOT NULL DEFAULT 0 CHECK(section_progress BETWEEN 0 AND 1),
                anchor_json TEXT,
                selected_text TEXT,
                note_text TEXT,
                color TEXT,
                created_utc INTEGER NOT NULL,
                updated_utc INTEGER NOT NULL,
                FOREIGN KEY(book_id) REFERENCES library_books(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_annotations_book_location
                ON annotations(book_id, section_index, section_progress);
            CREATE INDEX IF NOT EXISTS ix_annotations_book_type
                ON annotations(book_id, annotation_type);

            CREATE TABLE IF NOT EXISTS library_folders (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                folder_path TEXT NOT NULL,
                path_key TEXT NOT NULL UNIQUE,
                name TEXT NOT NULL,
                include_subfolders INTEGER NOT NULL DEFAULT 1 CHECK(include_subfolders IN (0, 1)),
                added_utc INTEGER NOT NULL,
                last_scanned_utc INTEGER
            );

            CREATE TABLE IF NOT EXISTS search_sections (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                book_id INTEGER NOT NULL,
                section_index INTEGER NOT NULL CHECK(section_index >= 0),
                section_key TEXT,
                title TEXT NOT NULL,
                content TEXT NOT NULL,
                UNIQUE(book_id, section_index),
                FOREIGN KEY(book_id) REFERENCES library_books(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_search_sections_book
                ON search_sections(book_id, section_index);

            PRAGMA user_version = 1;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MigrateToVersion2Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS search_index_state (
                book_id INTEGER PRIMARY KEY,
                source_size INTEGER NOT NULL DEFAULT 0 CHECK(source_size >= 0),
                source_modified_utc INTEGER,
                section_count INTEGER NOT NULL DEFAULT 0 CHECK(section_count >= 0),
                index_version TEXT NOT NULL,
                indexed_utc INTEGER NOT NULL,
                FOREIGN KEY(book_id) REFERENCES library_books(id) ON DELETE CASCADE
            );

            PRAGMA user_version = 2;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MigrateToVersion3Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS annotation_tombstones (
                annotation_id TEXT PRIMARY KEY,
                book_id INTEGER NOT NULL,
                deleted_utc INTEGER NOT NULL,
                FOREIGN KEY(book_id) REFERENCES library_books(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_annotation_tombstones_book
                ON annotation_tombstones(book_id, deleted_utc);

            PRAGMA user_version = 3;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MigrateToVersion4Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            ALTER TABLE library_books ADD COLUMN tags TEXT;

            PRAGMA user_version = 4;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureFtsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            string? existingSql;
            await using (var check = connection.CreateCommand())
            {
                check.CommandText =
                    "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'search_sections_fts';";
                existingSql = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            }

            var existed = existingSql is not null;
            var requiresRebuild = existed &&
                                  !existingSql!.Contains("tokenize = 'trigram'", StringComparison.OrdinalIgnoreCase);

            await using var transaction = connection.BeginTransaction();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            if (requiresRebuild)
            {
                command.CommandText =
                    """
                    DROP TRIGGER IF EXISTS search_sections_ai;
                    DROP TRIGGER IF EXISTS search_sections_ad;
                    DROP TRIGGER IF EXISTS search_sections_au;
                    DROP TABLE IF EXISTS search_sections_fts;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                existed = false;
            }

            command.CommandText =
                """
                CREATE VIRTUAL TABLE IF NOT EXISTS search_sections_fts USING fts5(
                    title,
                    content,
                    content = 'search_sections',
                    content_rowid = 'id',
                    tokenize = 'trigram'
                );

                CREATE TRIGGER IF NOT EXISTS search_sections_ai AFTER INSERT ON search_sections BEGIN
                    INSERT INTO search_sections_fts(rowid, title, content)
                    VALUES (new.id, new.title, new.content);
                END;

                CREATE TRIGGER IF NOT EXISTS search_sections_ad AFTER DELETE ON search_sections BEGIN
                    INSERT INTO search_sections_fts(search_sections_fts, rowid, title, content)
                    VALUES ('delete', old.id, old.title, old.content);
                END;

                CREATE TRIGGER IF NOT EXISTS search_sections_au AFTER UPDATE ON search_sections BEGIN
                    INSERT INTO search_sections_fts(search_sections_fts, rowid, title, content)
                    VALUES ('delete', old.id, old.title, old.content);
                    INSERT INTO search_sections_fts(rowid, title, content)
                    VALUES (new.id, new.title, new.content);
                END;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            if (!existed)
            {
                command.CommandText =
                    "INSERT INTO search_sections_fts(search_sections_fts) VALUES ('rebuild');";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _ftsAvailable, 1);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 1)
        {
            Volatile.Write(ref _ftsAvailable, 0);
        }
    }

    private static async Task DeleteAnnotationAndRecordTombstoneAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string annotationId,
        long bookId,
        DateTimeOffset deletedUtc,
        CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM annotations WHERE id = $id;";
            delete.Parameters.AddWithValue("$id", annotationId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var tombstone = connection.CreateCommand();
        tombstone.Transaction = transaction;
        tombstone.CommandText =
            """
            INSERT INTO annotation_tombstones (annotation_id, book_id, deleted_utc)
            VALUES ($id, $bookId, $deletedUtc)
            ON CONFLICT(annotation_id) DO UPDATE SET
                book_id = excluded.book_id,
                deleted_utc = MAX(annotation_tombstones.deleted_utc, excluded.deleted_utc);
            """;
        tombstone.Parameters.AddWithValue("$id", annotationId);
        tombstone.Parameters.AddWithValue("$bookId", bookId);
        tombstone.Parameters.AddWithValue("$deletedUtc", ToTimestamp(deletedUtc));
        await tombstone.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddAnnotationParameters(SqliteCommand command, Annotation annotation)
    {
        command.Parameters.AddWithValue("$id", annotation.Id);
        command.Parameters.AddWithValue("$bookId", annotation.BookId);
        command.Parameters.AddWithValue("$type", (int)annotation.Type);
        command.Parameters.AddWithValue("$sectionIndex", annotation.SectionIndex);
        command.Parameters.AddWithValue("$sectionKey", DbValue(annotation.SectionPath));
        command.Parameters.AddWithValue("$sectionProgress", annotation.SectionProgress);
        command.Parameters.AddWithValue("$anchorJson", DbValue(SerializeAnchor(annotation.Anchor)));
        command.Parameters.AddWithValue("$selectedText", DbValue(annotation.SelectedText));
        command.Parameters.AddWithValue("$note", DbValue(annotation.Note));
        command.Parameters.AddWithValue("$color", DbValue(annotation.Color));
        command.Parameters.AddWithValue("$createdUtc", ToTimestamp(annotation.CreatedUtc));
        command.Parameters.AddWithValue("$updatedUtc", ToTimestamp(annotation.ModifiedUtc));
    }

    private static void PrepareAnnotationForUpsert(Annotation annotation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        if (annotation.BookId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(annotation), "批注必须关联书籍。");
        }

        if (!Enum.IsDefined(annotation.Type))
        {
            throw new ArgumentOutOfRangeException(nameof(annotation), "批注类型无效。");
        }

        annotation.Id = string.IsNullOrWhiteSpace(annotation.Id)
            ? Guid.NewGuid().ToString("N")
            : annotation.Id.Trim();
        annotation.SectionIndex = Math.Max(0, annotation.SectionIndex);
        annotation.SectionProgress = Math.Clamp(annotation.SectionProgress, 0, 1);
        annotation.CreatedUtc = annotation.CreatedUtc == default ? now : annotation.CreatedUtc;
        annotation.ModifiedUtc = annotation.ModifiedUtc == default ? now : annotation.ModifiedUtc;
    }

    private static LibraryBook ReadBook(SqliteDataReader reader)
    {
        return new LibraryBook
        {
            Id = reader.GetInt64(0),
            Path = reader.GetString(1),
            Title = reader.GetString(2),
            Author = GetNullableString(reader, 3),
            Format = reader.GetString(4),
            CoverPath = GetNullableString(reader, 5),
            FileSize = reader.GetInt64(6),
            ModifiedUtc = GetNullableTimestamp(reader, 7),
            AddedUtc = FromTimestamp(reader.GetInt64(8)),
            LastOpenedUtc = GetNullableTimestamp(reader, 9),
            SectionCount = reader.GetInt32(10),
            IsMissing = reader.GetInt32(11) != 0,
            Tags = reader.FieldCount > 12 ? GetNullableString(reader, 12) : null
        };
    }

    private static string? NormalizeTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return null;
        }

        var parts = tags
            .Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
        return parts.Length == 0 ? null : string.Join(",", parts);
    }

    private static Annotation ReadAnnotation(SqliteDataReader reader)
    {
        return new Annotation
        {
            Id = reader.GetString(0),
            BookId = reader.GetInt64(1),
            Type = (AnnotationType)reader.GetInt32(2),
            SectionIndex = reader.GetInt32(3),
            SectionPath = GetNullableString(reader, 4),
            SectionProgress = reader.GetDouble(5),
            Anchor = DeserializeAnchor(GetNullableString(reader, 6)),
            SelectedText = GetNullableString(reader, 7),
            Note = GetNullableString(reader, 8),
            Color = GetNullableString(reader, 9),
            CreatedUtc = FromTimestamp(reader.GetInt64(10)),
            ModifiedUtc = FromTimestamp(reader.GetInt64(11))
        };
    }

    private static LibraryFolder ReadFolder(SqliteDataReader reader)
    {
        return new LibraryFolder
        {
            Id = reader.GetInt64(0),
            Path = reader.GetString(1),
            Name = reader.GetString(2),
            IncludeSubfolders = reader.GetInt32(3) != 0,
            AddedAt = FromTimestamp(reader.GetInt64(4)),
            LastScannedAt = GetNullableTimestamp(reader, 5)
        };
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path.Trim());
    }

    private static string ToPathKey(string normalizedPath) => normalizedPath.ToUpperInvariant();

    private static string EmptyIfNull(string? value) => value ?? string.Empty;

    private static object DbValue(object? value) => value ?? DBNull.Value;

    private static object DbTimestamp(DateTimeOffset? value) =>
        value is null ? DBNull.Value : ToTimestamp(value.Value);

    private static long ToTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToUnixTimeMilliseconds();

    private static DateTimeOffset FromTimestamp(long value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(value);

    private static DateTimeOffset? GetNullableTimestamp(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : FromTimestamp(reader.GetInt64(ordinal));

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string? SerializeAnchor(TextAnchor? anchor) =>
        anchor is null ? null : JsonSerializer.Serialize(anchor);

    private static TextAnchor? DeserializeAnchor(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TextAnchor>(json);
        }
        catch (JsonException)
        {
            // A malformed legacy anchor should not hide the annotation/location.
            return null;
        }
    }

    private static void AppendOccurrenceHits(
        List<SearchHit> hits,
        long bookId,
        int sectionIndex,
        string sectionTitle,
        string content,
        string query,
        double rank,
        int limit,
        CancellationToken cancellationToken)
    {
        // Body matches come first because they can be revealed in the reading
        // surface. A title-only match remains discoverable and is explicitly
        // identified so callers do not treat its offset as a body offset.
        AppendFieldOccurrenceHits(content, matchInTitle: false);
        AppendFieldOccurrenceHits(sectionTitle, matchInTitle: true);

        void AppendFieldOccurrenceHits(string source, bool matchInTitle)
        {
            if (hits.Count >= limit || source.Length == 0)
            {
                return;
            }

            var searchStart = 0;
            var occurrenceIndex = 0;
            while (searchStart <= source.Length - query.Length && hits.Count < limit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var matchStart = source.IndexOf(query, searchStart, StringComparison.OrdinalIgnoreCase);
                if (matchStart < 0)
                {
                    break;
                }

                hits.Add(new SearchHit
                {
                    BookId = bookId,
                    SectionIndex = sectionIndex,
                    SectionTitle = sectionTitle,
                    Snippet = BuildOccurrenceSnippet(source, matchStart, query.Length, matchInTitle),
                    Rank = rank,
                    MatchStart = matchStart,
                    MatchLength = query.Length,
                    OccurrenceIndex = occurrenceIndex,
                    MatchInTitle = matchInTitle,
                    MatchedText = source.Substring(matchStart, query.Length),
                    SectionProgress = matchInTitle
                        ? 0
                        : Math.Clamp((double)matchStart / Math.Max(1, source.Length), 0, 1)
                });

                occurrenceIndex++;
                // Normal reader search treats adjacent/overlapping characters as
                // one consumed match ("aaa" searched for "aa" yields one hit).
                searchStart = matchStart + Math.Max(1, query.Length);
            }
        }
    }

    private static string BuildOccurrenceSnippet(
        string source,
        int matchStart,
        int matchLength,
        bool matchInTitle)
    {
        var snippetStart = Math.Max(0, matchStart - SearchSnippetRadius);
        var snippetEnd = Math.Min(source.Length, matchStart + matchLength + SearchSnippetRadius);
        var before = source[snippetStart..matchStart];
        var match = source.Substring(matchStart, matchLength);
        var after = source[(matchStart + matchLength)..snippetEnd];
        var prefix = matchInTitle ? "标题：" : string.Empty;
        return $"{prefix}{(snippetStart > 0 ? "…" : string.Empty)}{before}‹{match}›{after}{(snippetEnd < source.Length ? "…" : string.Empty)}";
    }

    private static IReadOnlyList<SearchHit> MergeSearchHits(
        IReadOnlyList<SearchHit> primary,
        IReadOnlyList<SearchHit> fallback,
        int limit)
    {
        var merged = new List<SearchHit>(limit);
        var seen = new HashSet<(long BookId, int SectionIndex, bool MatchInTitle, int MatchStart, int MatchLength)>();
        Append(primary);
        Append(fallback);
        return merged;

        void Append(IReadOnlyList<SearchHit> source)
        {
            foreach (var hit in source)
            {
                if (merged.Count >= limit)
                {
                    return;
                }

                var key = (hit.BookId, hit.SectionIndex, hit.MatchInTitle, hit.MatchStart, hit.MatchLength);
                if (seen.Add(key))
                {
                    merged.Add(hit);
                }
            }
        }
    }

    private static bool IsFtsUnavailableError(SqliteException exception)
    {
        return exception.Message.Contains("no such module", StringComparison.OrdinalIgnoreCase) ||
               exception.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildFtsQuery(string query)
    {
        var terms = query
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => $"\"{term.Replace("\"", "\"\"")}\"");
        return string.Join(" AND ", terms);
    }

    private static bool CanUseTrigramSearch(string query)
    {
        var terms = query.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.Length > 0 && terms.All(term => term.EnumerateRunes().Take(3).Count() == 3);
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
