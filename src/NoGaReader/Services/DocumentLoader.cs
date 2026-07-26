using NoGaReader.Models;

namespace NoGaReader.Services;

public sealed class DocumentLoader
{
    private readonly PortableDocumentLoader _portableLoader = new();
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
        "支持的阅读文件|*.epub;*.pdf;*.mobi;*.azw;*.azw3;*.azw4;*.cbz;*.cbr;*.cb7;*.cbt;*.zip;*.rar;*.fb2;*.chm;*.xps;*.oxps;*.djvu;*.djv;*.html;*.htm;*.xhtml;*.mht;*.mhtml;*.xml;*.txt;*.md;*.markdown;*.log;*.nfo;*.jpg;*.jpeg;*.png;*.gif;*.webp;*.bmp;*.tif;*.tiff|" +
        "电子书与漫画|*.epub;*.mobi;*.azw;*.azw3;*.azw4;*.fb2;*.cbz;*.cbr;*.cb7;*.cbt;*.zip;*.rar|" +
        "固定版式|*.pdf;*.xps;*.oxps;*.djvu;*.djv;*.chm|" +
        "Kindle|*.mobi;*.azw;*.azw3;*.azw4|" +
        "PDF 文件|*.pdf|" +
        "网页与文本|*.html;*.htm;*.xhtml;*.mht;*.mhtml;*.xml;*.txt;*.md;*.markdown;*.log;*.nfo|" +
        "图片|*.jpg;*.jpeg;*.png;*.gif;*.webp;*.bmp;*.tif;*.tiff|" +
        "所有文件|*.*";

    public static bool IsSupported(string path)
    {
        return DocumentFormatSupport.IsDesktopSupported(path);
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
            return _portableLoader.Load(path, cancellationToken);
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("文件或图片文件夹不存在，或已被移动。", path);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(path);
        if (CalibreConverter.IsKindleExtension(extension))
        {
            return _convertedEbookLoader.Load(path, cancellationToken);
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

        if (PortableDocumentLoader.IsSupported(path))
        {
            return _portableLoader.Load(path, cancellationToken);
        }

        throw new NotSupportedException(
            $"当前版本还不支持 {extension.ToUpperInvariant()}。");
    }

}
