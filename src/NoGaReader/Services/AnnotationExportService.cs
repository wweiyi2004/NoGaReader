using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NoGaReader.Models;

namespace NoGaReader.Services;

/// <summary>
/// Produces portable Markdown and JSON annotation exports and writes them atomically.
/// </summary>
public sealed class AnnotationExportService
{
    private const int MaximumFileStemLength = 96;
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly HashSet<string> ReservedFileNames = BuildReservedFileNames();

    private readonly TimeProvider _timeProvider;

    public AnnotationExportService(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Exports annotations to a generated safe filename inside <paramref name="destinationDirectory"/>.
    /// Returns the absolute path of the completed file.
    /// </summary>
    public Task<string> ExportMarkdownAsync(
        LibraryBook book,
        IReadOnlyList<Annotation> annotations,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        return ExportAsync(
            book,
            annotations,
            destinationDirectory,
            AnnotationExportFormat.Markdown,
            cancellationToken);
    }

    /// <inheritdoc cref="ExportMarkdownAsync"/>
    public Task<string> ExportJsonAsync(
        LibraryBook book,
        IReadOnlyList<Annotation> annotations,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        return ExportAsync(
            book,
            annotations,
            destinationDirectory,
            AnnotationExportFormat.Json,
            cancellationToken);
    }

    public async Task<string> ExportAsync(
        LibraryBook book,
        IReadOnlyList<Annotation> annotations,
        string destinationDirectory,
        AnnotationExportFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(annotations);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ValidateAnnotationsBelongToBook(book, annotations);
        cancellationToken.ThrowIfCancellationRequested();

        var exportedAtUtc = _timeProvider.GetUtcNow();
        var content = format switch
        {
            AnnotationExportFormat.Markdown => CreateMarkdown(book, annotations, exportedAtUtc),
            AnnotationExportFormat.Json => CreateJson(book, annotations, exportedAtUtc),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported export format.")
        };
        var directory = Path.GetFullPath(destinationDirectory);
        var destinationPath = Path.Combine(directory, CreateSuggestedFileName(book, format));

        await WriteAtomicallyAsync(destinationPath, Utf8WithoutBom.GetBytes(content), cancellationToken);
        return destinationPath;
    }

    public async Task<string> ExportToFileAsync(
        LibraryBook book,
        IReadOnlyList<Annotation> annotations,
        string destinationPath,
        AnnotationExportFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(annotations);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ValidateAnnotationsBelongToBook(book, annotations);
        cancellationToken.ThrowIfCancellationRequested();

        var exportedAtUtc = _timeProvider.GetUtcNow();
        var content = format switch
        {
            AnnotationExportFormat.Markdown => CreateMarkdown(book, annotations, exportedAtUtc),
            AnnotationExportFormat.Json => CreateJson(book, annotations, exportedAtUtc),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported export format.")
        };
        var fullPath = Path.GetFullPath(destinationPath);
        await WriteAtomicallyAsync(fullPath, Utf8WithoutBom.GetBytes(content), cancellationToken);
        return fullPath;
    }

    /// <summary>
    /// Creates a deterministic, Windows-safe filename without using the source path as a directory.
    /// </summary>
    public string CreateSuggestedFileName(LibraryBook book, AnnotationExportFormat format)
    {
        ArgumentNullException.ThrowIfNull(book);
        var fallback = Path.GetFileNameWithoutExtension(book.Path);
        var stem = MakeSafeFileStem(string.IsNullOrWhiteSpace(book.Title) ? fallback : book.Title);
        var extension = format switch
        {
            AnnotationExportFormat.Markdown => ".md",
            AnnotationExportFormat.Json => ".json",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported export format.")
        };
        return $"{stem} - 批注{extension}";
    }

    /// <summary>
    /// Creates Markdown using LF line endings. Supplying a timestamp makes the output deterministic in tests.
    /// </summary>
    public string CreateMarkdown(
        LibraryBook book,
        IReadOnlyList<Annotation> annotations,
        DateTimeOffset? exportedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(annotations);
        ValidateAnnotationsBelongToBook(book, annotations);

        var exportedAt = (exportedAtUtc ?? _timeProvider.GetUtcNow()).ToUniversalTime();
        var ordered = OrderAnnotations(annotations).ToArray();
        var builder = new StringBuilder();
        builder.Append("# ")
            .Append(EscapeMarkdownInline(DisplayTitle(book)))
            .Append(" — 批注导出\n\n")
            .Append("- 作者：")
            .Append(EscapeMarkdownInline(DisplayValue(book.Author)))
            .Append('\n')
            .Append("- 来源：")
            .Append(EscapeMarkdownInline(DisplayValue(book.Path)))
            .Append('\n')
            .Append("- 导出时间：")
            .Append(exportedAt.ToString("O"))
            .Append('\n')
            .Append("- 批注数：")
            .Append(ordered.Length)
            .Append("\n\n");

        if (ordered.Length == 0)
        {
            builder.Append("暂无批注。\n");
            return builder.ToString();
        }

        foreach (var section in ordered.GroupBy(item => item.SectionIndex))
        {
            builder.Append("## 第 ")
                .Append(ToSectionNumber(section.Key))
                .Append(" 章\n\n");

            foreach (var annotation in section)
            {
                builder.Append("### ")
                    .Append(ToChineseKind(annotation.Type))
                    .Append(" · ")
                    .Append(ClampProgress(annotation.SectionProgress).ToString("P1"))
                    .Append("\n\n")
                    .Append("- 创建：")
                    .Append(annotation.CreatedUtc.ToUniversalTime().ToString("O"))
                    .Append('\n');

                if (!string.IsNullOrWhiteSpace(annotation.Color))
                {
                    builder.Append("- 颜色：")
                        .Append(EscapeMarkdownInline(NormalizeSingleLine(annotation.Color)))
                        .Append('\n');
                }

                builder.Append('\n');
                var quote = GetQuote(annotation);
                if (!string.IsNullOrWhiteSpace(quote))
                {
                    builder.Append("**引用：**\n\n");
                    AppendEscapedBlockQuote(builder, quote);
                    builder.Append('\n');
                }

                if (!string.IsNullOrWhiteSpace(annotation.Note))
                {
                    builder.Append("**笔记：**\n\n");
                    AppendEscapedBlockQuote(builder, annotation.Note);
                    builder.Append('\n');
                }

                builder.Append("---\n\n");
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Creates versioned JSON through explicit export DTOs rather than serializing database models.
    /// </summary>
    public string CreateJson(
        LibraryBook book,
        IReadOnlyList<Annotation> annotations,
        DateTimeOffset? exportedAtUtc = null)
    {
        var document = CreateDocument(book, annotations, exportedAtUtc);
        return NormalizeLineEndings(JsonSerializer.Serialize(document, JsonOptions)) + "\n";
    }

    public AnnotationExportDocument CreateDocument(
        LibraryBook book,
        IReadOnlyList<Annotation> annotations,
        DateTimeOffset? exportedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(annotations);
        ValidateAnnotationsBelongToBook(book, annotations);

        return new AnnotationExportDocument
        {
            ExportedAtUtc = (exportedAtUtc ?? _timeProvider.GetUtcNow()).ToUniversalTime(),
            Book = new AnnotationExportBook
            {
                Title = DisplayTitle(book),
                Author = NormalizeNullable(book.Author),
                SourcePath = NormalizeSingleLine(book.Path),
                Format = NormalizeNullable(book.Format)
            },
            Annotations = OrderAnnotations(annotations)
                .Select(ToExportItem)
                .ToArray()
        };
    }

    public AnnotationExportDocument ReadJsonDocument(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var document = JsonSerializer.Deserialize<AnnotationExportDocument>(json, JsonOptions)
            ?? throw new InvalidDataException("批注 JSON 无效。");
        if (document.SchemaVersion is < 1 or > AnnotationExportDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"不支持的批注 schema 版本：{document.SchemaVersion}");
        }

        if (document.Annotations is null)
        {
            throw new InvalidDataException("批注 JSON 缺少 annotations 数组。");
        }

        return document;
    }

    public IReadOnlyList<Annotation> ToAnnotations(AnnotationExportDocument document, long bookId)
    {
        ArgumentNullException.ThrowIfNull(document);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var results = new List<Annotation>();
        foreach (var item in document.Annotations)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Kind))
            {
                continue;
            }

            var type = item.Kind.Trim().ToLowerInvariant() switch
            {
                "bookmark" => AnnotationType.Bookmark,
                "highlight" => AnnotationType.Highlight,
                "note" => AnnotationType.Note,
                _ => (AnnotationType?)null
            };
            if (type is null)
            {
                continue;
            }

            TextAnchor? anchor = null;
            if (item.TextSelector is { Exact: { Length: > 0 } exact })
            {
                anchor = new TextAnchor
                {
                    ExactText = exact,
                    Prefix = item.TextSelector.Prefix,
                    Suffix = item.TextSelector.Suffix,
                    Progress = ClampProgress(item.SectionProgress)
                };
            }

            results.Add(new Annotation
            {
                Id = Guid.NewGuid().ToString("N"),
                BookId = bookId,
                Type = type.Value,
                SectionIndex = Math.Max(0, item.SectionNumber - 1),
                SectionProgress = ClampProgress(item.SectionProgress),
                Anchor = anchor,
                SelectedText = item.Quote,
                Note = item.Note,
                Color = item.Color,
                CreatedUtc = item.CreatedAtUtc == default ? now : item.CreatedAtUtc.UtcDateTime,
                ModifiedUtc = item.ModifiedAtUtc == default ? now : item.ModifiedAtUtc.UtcDateTime
            });
        }

        return results;
    }

    private static AnnotationExportItem ToExportItem(Annotation annotation)
    {
        var anchor = annotation.Anchor;
        var exact = NormalizeNullable(anchor?.ExactText);
        var selector = exact is null
            ? null
            : new AnnotationExportTextSelector
            {
                Exact = exact,
                Prefix = NormalizeNullable(anchor?.Prefix),
                Suffix = NormalizeNullable(anchor?.Suffix)
            };

        return new AnnotationExportItem
        {
            Kind = ToJsonKind(annotation.Type),
            SectionNumber = ToSectionNumber(annotation.SectionIndex),
            SectionProgress = ClampProgress(annotation.SectionProgress),
            Quote = NormalizeNullable(GetQuote(annotation)),
            Note = NormalizeNullable(annotation.Note),
            Color = NormalizeNullable(annotation.Color),
            TextSelector = selector,
            CreatedAtUtc = annotation.CreatedUtc.ToUniversalTime(),
            ModifiedAtUtc = annotation.ModifiedUtc.ToUniversalTime()
        };
    }

    private static IOrderedEnumerable<Annotation> OrderAnnotations(IEnumerable<Annotation> annotations)
    {
        return annotations
            .OrderBy(item => item.SectionIndex)
            .ThenBy(item => ClampProgress(item.SectionProgress))
            .ThenBy(item => TypeSortOrder(item.Type))
            .ThenBy(item => item.CreatedUtc)
            .ThenBy(item => item.ModifiedUtc)
            .ThenBy(item => item.SelectedText, StringComparer.Ordinal)
            .ThenBy(item => item.Note, StringComparer.Ordinal)
            .ThenBy(item => item.Color, StringComparer.Ordinal)
            .ThenBy(item => item.Anchor?.ExactText, StringComparer.Ordinal)
            .ThenBy(item => item.Anchor?.Prefix, StringComparer.Ordinal)
            .ThenBy(item => item.Anchor?.Suffix, StringComparer.Ordinal);
    }

    private static void ValidateAnnotationsBelongToBook(
        LibraryBook book,
        IEnumerable<Annotation> annotations)
    {
        if (annotations.Any(item => item is null || item.BookId != book.Id))
        {
            throw new ArgumentException(
                "Every annotation must be non-null and belong to the supplied book.",
                nameof(annotations));
        }
    }

    private static async Task WriteAtomicallyAsync(
        string destinationPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The destination must include a parent directory.", nameof(destinationPath));
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // A failed cleanup must not hide the original write or cancellation error.
            }
            catch (UnauthorizedAccessException)
            {
                // A failed cleanup must not hide the original write or cancellation error.
            }
        }
    }

    private static string MakeSafeFileStem(string? value)
    {
        var source = NormalizeSingleLine(value);
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(source.Length);
        foreach (var character in source)
        {
            builder.Append(character < ' ' || invalid.Contains(character) ? '_' : character);
        }

        var stem = builder.ToString().Trim().TrimEnd('.', ' ');
        if (stem.Length > MaximumFileStemLength)
        {
            stem = stem[..MaximumFileStemLength].TrimEnd('.', ' ');
            if (stem.Length > 0 && char.IsHighSurrogate(stem[^1]))
            {
                stem = stem[..^1];
            }
        }

        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "NoGaReader";
        }

        if (ReservedFileNames.Contains(stem))
        {
            stem = $"_{stem}";
        }

        return stem;
    }

    private static HashSet<string> BuildReservedFileNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "CLOCK$"
        };
        for (var index = 1; index <= 9; index++)
        {
            names.Add($"COM{index}");
            names.Add($"LPT{index}");
        }

        return names;
    }

    private static void AppendEscapedBlockQuote(StringBuilder builder, string value)
    {
        foreach (var line in NormalizeLineEndings(value).Trim().Split('\n'))
        {
            builder.Append('>');
            if (line.Length > 0)
            {
                builder.Append(' ').Append(EscapeMarkdownInline(line));
            }

            builder.Append('\n');
        }
    }

    private static string EscapeMarkdownInline(string value)
    {
        var normalized = NormalizeSingleLine(value);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (character is '\\' or '`' or '*' or '_' or '{' or '}' or '[' or ']' or '<' or '>'
                or '(' or ')' or '#' or '+' or '-' or '.' or '!' or '|')
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string DisplayTitle(LibraryBook book)
    {
        if (!string.IsNullOrWhiteSpace(book.Title))
        {
            return NormalizeSingleLine(book.Title);
        }

        var fileName = Path.GetFileNameWithoutExtension(book.Path);
        return string.IsNullOrWhiteSpace(fileName) ? "未命名书籍" : NormalizeSingleLine(fileName);
    }

    private static string DisplayValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "未知" : NormalizeSingleLine(value);
    }

    private static string? NormalizeNullable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return NormalizeLineEndings(value).Trim();
    }

    private static string NormalizeSingleLine(string? value)
    {
        return string.Join(" ", NormalizeLineEndings(value ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string NormalizeLineEndings(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    private static string? GetQuote(Annotation annotation)
    {
        return !string.IsNullOrWhiteSpace(annotation.SelectedText)
            ? annotation.SelectedText
            : annotation.Anchor?.ExactText;
    }

    private static double ClampProgress(double progress)
    {
        return double.IsFinite(progress) ? Math.Clamp(progress, 0d, 1d) : 0d;
    }

    private static int ToSectionNumber(int zeroBasedSectionIndex)
    {
        return zeroBasedSectionIndex < 0 ? 1 : zeroBasedSectionIndex + 1;
    }

    private static int TypeSortOrder(AnnotationType type)
    {
        return type switch
        {
            AnnotationType.Bookmark => 0,
            AnnotationType.Highlight => 1,
            AnnotationType.Note => 2,
            _ => 3
        };
    }

    private static string ToChineseKind(AnnotationType type)
    {
        return type switch
        {
            AnnotationType.Bookmark => "书签",
            AnnotationType.Highlight => "高亮",
            AnnotationType.Note => "笔记",
            _ => "批注"
        };
    }

    private static string ToJsonKind(AnnotationType type)
    {
        return type switch
        {
            AnnotationType.Bookmark => "bookmark",
            AnnotationType.Highlight => "highlight",
            AnnotationType.Note => "note",
            _ => "unknown"
        };
    }
}
