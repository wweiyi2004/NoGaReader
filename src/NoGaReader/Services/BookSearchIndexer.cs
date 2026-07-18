using System.Buffers;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using NoGaReader.Models;

namespace NoGaReader.Services;

/// <summary>
/// Builds bounded, plain-text search documents from a loaded reader session.
/// A malformed or unreadable section is skipped so it cannot make the whole
/// book unavailable for search.
/// </summary>
public sealed partial class BookSearchIndexer
{
    public const string IndexVersion = "plain-text-v2";
    public const int MaximumSections = 10_000;
    public const int MaximumSectionBytes = 8 * 1024 * 1024;
    public const int MaximumBookBytes = 64 * 1024 * 1024;

    private const int ReadBufferBytes = 64 * 1024;

    private static readonly HashSet<string> MarkupExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".xhtml", ".xht", ".xml", ".mht", ".mhtml"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".log", ".nfo"
    };

    public async Task<IReadOnlyList<SearchSection>> BuildAsync(
        ReaderSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var results = new List<SearchSection>(Math.Min(session.Sections.Count, MaximumSections));
        var remainingBookBytes = MaximumBookBytes;
        var sectionCount = Math.Min(session.Sections.Count, MaximumSections);

        for (var index = 0; index < sectionCount && remainingBookBytes > 0; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var section = session.Sections[index];
            var extension = Path.GetExtension(section.FullPath);
            var isMarkup = MarkupExtensions.Contains(extension);
            if (!isMarkup && !TextExtensions.Contains(extension))
            {
                continue;
            }

            try
            {
                var byteLimit = Math.Min(MaximumSectionBytes, remainingBookBytes);
                var content = await ReadBoundedAsync(section.FullPath, byteLimit, cancellationToken)
                    .ConfigureAwait(false);
                remainingBookBytes -= content.BytesRead;

                if (content.BytesRead == 0)
                {
                    continue;
                }

                var decoded = Decode(content.Buffer.AsSpan(0, content.BytesRead));
                var text = isMarkup ? ExtractText(decoded) : NormalizeWhitespace(decoded);
                if (text.Length == 0)
                {
                    continue;
                }

                results.Add(new SearchSection
                {
                    SectionIndex = index,
                    Title = string.IsNullOrWhiteSpace(section.Title)
                        ? $"章节 {index + 1}"
                        : NormalizeWhitespace(section.Title),
                    Text = text
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverableSectionError(exception))
            {
                // One broken chapter must not prevent the remaining book from
                // being indexed. Callers can compare the result count when they
                // need to surface partial-index diagnostics.
            }
        }

        return results;
    }

    private static async Task<BoundedContent> ReadBoundedAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0 || !File.Exists(path))
        {
            return new BoundedContent([], 0);
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            ReadBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var capacity = (int)Math.Min(stream.Length, maximumBytes);
        if (capacity <= 0)
        {
            return new BoundedContent([], 0);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(capacity);
        var bytesRead = 0;
        try
        {
            while (bytesRead < capacity)
            {
                var count = await stream.ReadAsync(
                        buffer.AsMemory(bytesRead, capacity - bytesRead),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                bytesRead += count;
            }

            var result = new byte[bytesRead];
            buffer.AsSpan(0, bytesRead).CopyTo(result);
            return new BoundedContent(result, bytesRead);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(Encoding.UTF8.Preamble))
        {
            return Encoding.UTF8.GetString(bytes[Encoding.UTF8.Preamble.Length..]);
        }

        // UTF-32 LE begins with the UTF-16 LE preamble, so test the longer
        // signature first.
        if (bytes.StartsWith(Encoding.UTF32.Preamble))
        {
            return Encoding.UTF32.GetString(bytes[Encoding.UTF32.Preamble.Length..]);
        }

        if (bytes.StartsWith(Encoding.Unicode.Preamble))
        {
            return Encoding.Unicode.GetString(bytes[Encoding.Unicode.Preamble.Length..]);
        }

        if (bytes.StartsWith(Encoding.BigEndianUnicode.Preamble))
        {
            return Encoding.BigEndianUnicode.GetString(bytes[Encoding.BigEndianUnicode.Preamble.Length..]);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static string ExtractText(string markup)
    {
        var text = HeadRegex().Replace(markup, " ");
        text = ScriptRegex().Replace(text, " ");
        text = StyleRegex().Replace(text, " ");
        text = NoScriptRegex().Replace(text, " ");
        text = CommentRegex().Replace(text, " ");
        text = BlockBoundaryRegex().Replace(text, " ");
        text = TagRegex().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        return NormalizeWhitespace(text);
    }

    private static string NormalizeWhitespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static bool IsRecoverableSectionError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or RegexMatchTimeoutException;

    private sealed record BoundedContent(byte[] Buffer, int BytesRead);

    [GeneratedRegex("<head\\b[^>]*>.*?(?:</head\\s*>|\\z)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex HeadRegex();

    [GeneratedRegex("<script\\b[^>]*>.*?(?:</script\\s*>|\\z)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex("<style\\b[^>]*>.*?(?:</style\\s*>|\\z)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex StyleRegex();

    [GeneratedRegex("<noscript\\b[^>]*>.*?(?:</noscript\\s*>|\\z)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex NoScriptRegex();

    [GeneratedRegex("<!--.*?(?:-->|\\z)", RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex CommentRegex();

    [GeneratedRegex("</?(?:address|article|aside|blockquote|br|dd|div|dl|dt|figcaption|figure|footer|h[1-6]|header|hr|li|main|nav|ol|p|pre|section|table|td|th|tr|ul)\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex BlockBoundaryRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex TagRegex();
}
