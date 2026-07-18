using NoGaReader.Models;

namespace NoGaReader.Services;

public sealed class DocumentLoader
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
    private readonly CalibreConverter _calibreConverter;
    private readonly ConvertedEbookLoader _convertedEbookLoader;
    private readonly ChmLoader _chmLoader = new();
    private readonly XpsLoader _xpsLoader;
    private readonly DjvuLoader _djvuLoader;

    public DocumentLoader()
        : this(new AppSettings())
    {
    }

    public DocumentLoader(AppSettings settings)
        : this(new CalibreConverter(settings))
    {
    }

    public DocumentLoader(CalibreConverter calibreConverter)
    {
        _calibreConverter = calibreConverter;
        _convertedEbookLoader = new ConvertedEbookLoader(calibreConverter);
        _xpsLoader = new XpsLoader(calibreConverter);
        _djvuLoader = new DjvuLoader(calibreConverter);
    }

    public CalibreConverter Converter => _calibreConverter;

    public static string OpenFileFilter =>
        "支持的阅读文件|*.epub;*.pdf;*.mobi;*.azw;*.azw3;*.cbz;*.cbr;*.cb7;*.cbt;*.zip;*.rar;*.fb2;*.chm;*.xps;*.oxps;*.djvu;*.djv;*.html;*.htm;*.xhtml;*.mht;*.mhtml;*.xml;*.txt;*.md;*.markdown;*.log;*.nfo;*.jpg;*.jpeg;*.png;*.gif;*.webp;*.bmp;*.tif;*.tiff|" +
        "电子书与漫画|*.epub;*.mobi;*.azw;*.azw3;*.fb2;*.cbz;*.cbr;*.cb7;*.cbt;*.zip;*.rar|" +
        "固定版式|*.pdf;*.xps;*.oxps;*.djvu;*.djv;*.chm|" +
        "Kindle|*.mobi;*.azw;*.azw3|" +
        "PDF 文件|*.pdf|" +
        "网页与文本|*.html;*.htm;*.xhtml;*.mht;*.mhtml;*.xml;*.txt;*.md;*.markdown;*.log;*.nfo|" +
        "图片|*.jpg;*.jpeg;*.png;*.gif;*.webp;*.bmp;*.tif;*.tiff|" +
        "所有文件|*.*";

    public static bool IsSupported(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".epub", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ||
               CalibreConverter.IsKindleExtension(extension) ||
               ComicArchiveExtractor.IsComicExtension(extension) ||
               extension.Equals(".fb2", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".chm", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".xps", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".oxps", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".djvu", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".djv", StringComparison.OrdinalIgnoreCase) ||
               ImageExtensions.Contains(extension) ||
               HtmlExtensions.Contains(extension) ||
               MarkdownExtensions.Contains(extension) ||
               TextExtensions.Contains(extension);
    }

    public Task<ReaderSession> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        return Task.Run(() => Load(fullPath, cancellationToken), cancellationToken);
    }

    private ReaderSession Load(string path, CancellationToken cancellationToken)
    {
        if (Directory.Exists(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _comicFolderLoader.Load(path, cancellationToken);
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("文件或图片文件夹不存在，或已被移动。", path);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(path);
        if (extension.Equals(".epub", StringComparison.OrdinalIgnoreCase))
        {
            return _epubLoader.Load(path, cancellationToken);
        }

        if (CalibreConverter.IsKindleExtension(extension))
        {
            return _convertedEbookLoader.Load(path, cancellationToken);
        }

        if (ComicArchiveExtractor.IsComicExtension(extension))
        {
            return _comicLoader.Load(path, cancellationToken);
        }

        if (extension.Equals(".chm", StringComparison.OrdinalIgnoreCase))
        {
            return _chmLoader.Load(path, cancellationToken);
        }

        if (extension.Equals(".xps", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".oxps", StringComparison.OrdinalIgnoreCase))
        {
            return _xpsLoader.Load(path, cancellationToken);
        }

        if (extension.Equals(".djvu", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".djv", StringComparison.OrdinalIgnoreCase))
        {
            return _djvuLoader.Load(path, cancellationToken);
        }

        if (extension.Equals(".fb2", StringComparison.OrdinalIgnoreCase))
        {
            return _fb2Loader.Load(path, cancellationToken);
        }

        if (MarkdownExtensions.Contains(extension))
        {
            return HtmlDocumentFactory.CreateMarkdownDocument(path);
        }

        if (TextExtensions.Contains(extension))
        {
            return HtmlDocumentFactory.CreateTextDocument(path);
        }

        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return Direct(path, ReaderDocumentKind.Pdf, isReflowable: false, supportsSearch: false);
        }

        if (ImageExtensions.Contains(extension))
        {
            return Direct(path, ReaderDocumentKind.Image, isReflowable: false, supportsSearch: false);
        }

        if (HtmlExtensions.Contains(extension))
        {
            return Direct(path, ReaderDocumentKind.Html, isReflowable: true, supportsSearch: false);
        }

        throw new NotSupportedException(
            $"当前版本还不支持 {extension.ToUpperInvariant()}。");
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
