using NoGaReader.Models;

namespace NoGaReader.Services;

/// <summary>
/// Loads formats that can be prepared without a Windows-only renderer or an
/// external desktop conversion process. UI clients remain responsible for
/// hosting the returned HTML, image, or PDF content.
/// </summary>
public sealed class PortableDocumentLoader
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tif", ".tiff"
    };

    private static readonly HashSet<string> HtmlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".xhtml", ".mht", ".mhtml", ".xml"
    };

    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".nfo"
    };

    private readonly EpubLoader _epubLoader = new();
    private readonly ComicArchiveLoader _comicLoader = new();
    private readonly ComicFolderLoader _comicFolderLoader = new();
    private readonly Fb2Loader _fb2Loader = new();

    public static bool IsSupported(string path)
    {
        if (Directory.Exists(path))
        {
            return true;
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".epub", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ||
               ComicArchiveExtractor.IsComicExtension(extension) ||
               extension.Equals(".fb2", StringComparison.OrdinalIgnoreCase) ||
               ImageExtensions.Contains(extension) ||
               HtmlExtensions.Contains(extension) ||
               MarkdownExtensions.Contains(extension) ||
               TextExtensions.Contains(extension);
    }

    public Task<ReaderSession> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        return Task.Run(() => Load(fullPath, cancellationToken), cancellationToken);
    }

    public ReaderSession Load(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _comicFolderLoader.Load(fullPath, cancellationToken);
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("文件或图片文件夹不存在，或已被移动。", fullPath);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(fullPath);
        if (extension.Equals(".epub", StringComparison.OrdinalIgnoreCase))
        {
            return _epubLoader.Load(fullPath, cancellationToken);
        }

        if (ComicArchiveExtractor.IsComicExtension(extension))
        {
            return _comicLoader.Load(fullPath, cancellationToken);
        }

        if (extension.Equals(".fb2", StringComparison.OrdinalIgnoreCase))
        {
            return _fb2Loader.Load(fullPath, cancellationToken);
        }

        if (MarkdownExtensions.Contains(extension))
        {
            return HtmlDocumentFactory.CreateMarkdownDocument(fullPath);
        }

        if (TextExtensions.Contains(extension))
        {
            return HtmlDocumentFactory.CreateTextDocument(fullPath);
        }

        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return Direct(fullPath, ReaderDocumentKind.Pdf, isReflowable: false, supportsSearch: false);
        }

        if (ImageExtensions.Contains(extension))
        {
            return Direct(fullPath, ReaderDocumentKind.Image, isReflowable: false, supportsSearch: false);
        }

        if (HtmlExtensions.Contains(extension))
        {
            return Direct(fullPath, ReaderDocumentKind.Html, isReflowable: true, supportsSearch: false);
        }

        throw new NotSupportedException($"当前版本还不支持 {extension.ToUpperInvariant()}。");
    }

    private static ReaderSession Direct(
        string path,
        ReaderDocumentKind kind,
        bool isReflowable,
        bool supportsSearch)
    {
        return new ReaderSession
        {
            SourcePath = path,
            Title = Path.GetFileNameWithoutExtension(path),
            RootDirectory = Path.GetDirectoryName(path)!,
            Kind = kind,
            Sections = [new ReaderSection("正文", path)],
            IsReflowable = isReflowable,
            SupportsInPageSearch = supportsSearch,
            EnableScriptExecution = kind is ReaderDocumentKind.Pdf or ReaderDocumentKind.Image
        };
    }
}
