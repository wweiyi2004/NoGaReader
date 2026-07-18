using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using NoGaReader.Models;

namespace NoGaReader.Services;

internal sealed class XpsLoader
{
    private const string CacheCategory = "xps";
    private const string CacheVersion = "xps-v3";
    private const int MaximumEntryCount = 10_000;
    private const long MaximumImageBytes = 256L * 1024 * 1024;
    private const long MaximumExtractedBytes = 1024L * 1024 * 1024;
    private const long SuspiciousRatioMinimumBytes = 32L * 1024 * 1024;
    private const double MaximumCompressionRatio = 1_000;
    private readonly CalibreConverter _converter;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tif", ".tiff"
    };

    public XpsLoader(CalibreConverter converter)
    {
        _converter = converter;
    }

    public ReaderSession Load(string sourcePath, CancellationToken cancellationToken)
    {
        // Prefer embedded images inside XPS/OXPS package (ZIP). Fallback: Calibre -> PDF.
        if (TryLoadAsImagePackage(sourcePath, cancellationToken, out var comicSession) && comicSession is not null)
        {
            return comicSession;
        }

        return LoadViaPdfConversion(sourcePath, cancellationToken);
    }

    private bool TryLoadAsImagePackage(
        string sourcePath,
        CancellationToken cancellationToken,
        out ReaderSession? session)
    {
        session = null;
        var cacheDirectory = AppPaths.GetDocumentCacheDirectory(sourcePath, CacheCategory);
        var completeMarker = Path.Combine(cacheDirectory, ".complete");
        var contentDirectory = Path.Combine(cacheDirectory, "content");
        var marker = $"{CacheVersion}|images|{CreateIdentity(sourcePath)}";

        try
        {
            var ready = Directory.Exists(contentDirectory) &&
                        File.Exists(completeMarker) &&
                        string.Equals(File.ReadAllText(completeMarker).Trim(), marker, StringComparison.Ordinal);
            List<string> images;
            if (ready)
            {
                images = Directory.EnumerateFiles(contentDirectory, "*", SearchOption.AllDirectories)
                    .Where(path => ImageExtensions.Contains(Path.GetExtension(path)))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            else
            {
                Reset(cacheDirectory);
                Directory.CreateDirectory(contentDirectory);
                images = ExtractImages(sourcePath, contentDirectory, cancellationToken);
                if (images.Count == 0)
                {
                    return false;
                }

                File.WriteAllText(completeMarker, marker, Encoding.UTF8);
            }

            if (images.Count == 0)
            {
                return false;
            }

            var viewerDirectory = Path.Combine(cacheDirectory, ".viewer");
            var shellDirectory = Path.Combine(viewerDirectory, "pages");
            ComicArchiveLoader.WriteViewerAssets(
                cacheDirectory,
                viewerDirectory,
                shellDirectory,
                images,
                cancellationToken);
            var sections = images.Select((_, index) => new ReaderSection(
                    $"第 {index + 1} 页",
                    Path.Combine(shellDirectory, $"{index + 1:D6}.html")))
                .ToArray();
            var comicPages = images.Select((path, index) => new ComicPage(index, path)).ToArray();
            session = new ReaderSession
            {
                SourcePath = sourcePath,
                Title = Path.GetFileNameWithoutExtension(sourcePath),
                RootDirectory = cacheDirectory,
                Kind = ReaderDocumentKind.Comic,
                Sections = sections,
                ComicPages = comicPages,
                CoverImagePath = images[0],
                IsReflowable = false,
                SupportsInPageSearch = false,
                EnableScriptExecution = true
            };
            return true;
        }
        catch (OperationCanceledException)
        {
            Reset(cacheDirectory);
            throw;
        }
        catch (InvalidDataException)
        {
            Reset(cacheDirectory);
            throw;
        }
        catch
        {
            Reset(cacheDirectory);
            return false;
        }
    }

    private ReaderSession LoadViaPdfConversion(string sourcePath, CancellationToken cancellationToken)
    {
        if (!_converter.IsAvailable)
        {
            throw new InvalidOperationException(
                "无法打开 XPS/OXPS。\n\n包内没有可提取图片，且未找到 Calibre ebook-convert。\n" +
                "请安装 Calibre 或在转换窗口指定引擎后重试。");
        }

        var cacheDirectory = AppPaths.GetDocumentCacheDirectory(sourcePath, CacheCategory);
        var pdfPath = Path.Combine(cacheDirectory, "book.pdf");
        var completeMarker = Path.Combine(cacheDirectory, ".complete");
        var marker = $"{CacheVersion}|pdf|{CreateIdentity(sourcePath)}";
        var ready = File.Exists(pdfPath) &&
                    new FileInfo(pdfPath).Length > 0 &&
                    File.Exists(completeMarker) &&
                    string.Equals(File.ReadAllText(completeMarker).Trim(), marker, StringComparison.Ordinal);
        if (!ready)
        {
            Reset(cacheDirectory);
            Directory.CreateDirectory(cacheDirectory);
            cancellationToken.ThrowIfCancellationRequested();
            var result = _converter.ConvertAsync(sourcePath, ".pdf", cacheDirectory, null, cancellationToken)
                .GetAwaiter().GetResult();
            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.OutputPath) || !File.Exists(result.OutputPath))
            {
                throw new InvalidOperationException(result.ErrorMessage ?? "XPS 转换为 PDF 失败。");
            }

            if (!string.Equals(result.OutputPath, pdfPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(result.OutputPath, pdfPath, overwrite: true);
                try { File.Delete(result.OutputPath); } catch { /* ignore */ }
            }

            File.WriteAllText(completeMarker, marker, Encoding.UTF8);
        }

        return new ReaderSession
        {
            SourcePath = sourcePath,
            Title = Path.GetFileNameWithoutExtension(sourcePath),
            RootDirectory = Path.GetDirectoryName(pdfPath)!,
            Kind = ReaderDocumentKind.Pdf,
            Sections = [new ReaderSection("正文", pdfPath)],
            IsReflowable = false,
            SupportsInPageSearch = false,
            EnableScriptExecution = true
        };
    }

    private static List<string> ExtractImages(
        string sourcePath,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var images = new List<string>();
        using var archive = ZipFile.OpenRead(sourcePath);
        if (archive.Entries.Count > MaximumEntryCount)
        {
            throw new InvalidDataException($"XPS 包含超过 {MaximumEntryCount:N0} 个条目。");
        }

        var index = 0;
        var totalBytes = 0L;
        foreach (var entry in archive.Entries
                     .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
                     .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(entry.Name);
            if (!ImageExtensions.Contains(extension))
            {
                continue;
            }

            if (entry.Length < 0 || entry.Length > MaximumImageBytes)
            {
                throw new InvalidDataException($"XPS 图片超过 256 MB 安全限制：{entry.FullName}");
            }

            if (entry.Length > MaximumExtractedBytes - totalBytes)
            {
                throw new InvalidDataException("XPS 图片解压总量超过 1 GB 安全限制。");
            }

            if (entry.Length >= SuspiciousRatioMinimumBytes &&
                (entry.CompressedLength <= 0 ||
                 (double)entry.Length / entry.CompressedLength > MaximumCompressionRatio))
            {
                throw new InvalidDataException($"XPS 图片压缩比异常：{entry.FullName}");
            }

            totalBytes += entry.Length;

            index++;
            var safeName = $"page-{index:D4}{extension.ToLowerInvariant()}";
            var destination = Path.Combine(outputDirectory, safeName);
            ExtractWithLimit(entry, destination, cancellationToken);
            images.Add(destination);
        }

        // Also try Document/Pages relationships order if available
        return images;
    }

    private static void ExtractWithLimit(
        ZipArchiveEntry entry,
        string destination,
        CancellationToken cancellationToken)
    {
        using var input = entry.Open();
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[128 * 1024];
        var written = 0L;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            written += read;
            if (written > MaximumImageBytes)
            {
                throw new InvalidDataException($"XPS 图片实际解压大小超过安全限制：{entry.FullName}");
            }

            output.Write(buffer, 0, read);
        }
    }

    private static string CreateIdentity(string sourcePath)
    {
        var file = new FileInfo(sourcePath);
        return $"{Path.GetFullPath(sourcePath)}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
    }

    private static void Reset(string directory)
    {
        if (Directory.Exists(directory))
        {
            try { Directory.Delete(directory, true); } catch { /* best effort */ }
        }

        Directory.CreateDirectory(directory);
    }
}
