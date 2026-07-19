using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Packaging;
using NoGaReader.Models;

namespace NoGaReader.Services;

internal sealed class XpsLoader
{
    private const string CacheCategory = "xps";
    private const string CacheVersion = "xps-v4-rendered-pages";
    private const int MaximumEntryCount = 10_000;
    private const int MaximumPageCount = 2_000;
    private const int MaximumPageDimension = 16_384;
    private const long MaximumEntryBytes = 256L * 1024 * 1024;
    private const long MaximumPackageBytes = 1024L * 1024 * 1024;
    private const long SuspiciousRatioMinimumBytes = 32L * 1024 * 1024;
    private const double MaximumCompressionRatio = 1_000;
    private const long MaximumPagePixels = 24_000_000;
    private const long MaximumTotalPixels = 500_000_000;
    private const long MaximumTotalOutputBytes = 1024L * 1024 * 1024;
    private const double TargetDpi = 144;
    private static readonly TimeSpan MaximumRenderingDuration = TimeSpan.FromMinutes(2);

    private readonly CalibreConverter _converter;

    public XpsLoader(CalibreConverter converter)
    {
        _converter = converter;
    }

    public ReaderSession Load(string sourcePath, CancellationToken cancellationToken)
    {
        var cacheDirectory = AppPaths.GetDocumentCacheDirectory(sourcePath, CacheCategory);
        var contentDirectory = Path.Combine(cacheDirectory, "content");
        var viewerDirectory = Path.Combine(cacheDirectory, ".viewer");
        var shellDirectory = Path.Combine(viewerDirectory, "pages");
        var completeMarker = Path.Combine(cacheDirectory, ".complete");
        var marker = $"{CacheVersion}|{CreateIdentity(sourcePath)}";

        try
        {
            var ready = Directory.Exists(contentDirectory) &&
                        File.Exists(completeMarker) &&
                        string.Equals(File.ReadAllText(completeMarker).Trim(), marker, StringComparison.Ordinal);
            IReadOnlyList<string> images;
            if (ready)
            {
                images = EnumerateRenderedPages(contentDirectory);
            }
            else
            {
                Reset(cacheDirectory);
                var renderDirectory = Path.Combine(
                    cacheDirectory,
                    $".render-{Guid.NewGuid():N}");
                Directory.CreateDirectory(renderDirectory);
                try
                {
                    var renderedImages = RenderDocumentPages(
                        sourcePath,
                        renderDirectory,
                        cancellationToken);
                    if (renderedImages.Count == 0)
                    {
                        throw new InvalidDataException("XPS/OXPS 文档不包含可显示页面。");
                    }

                    Directory.Move(renderDirectory, contentDirectory);
                    images = EnumerateRenderedPages(contentDirectory);
                }
                finally
                {
                    TryDeleteDirectory(renderDirectory);
                }

                File.WriteAllText(completeMarker, marker, Encoding.UTF8);
            }

            if (images.Count == 0)
            {
                throw new InvalidDataException("XPS/OXPS 页面缓存为空，请重新打开文档。");
            }

            ComicArchiveLoader.WriteViewerAssets(
                cacheDirectory,
                viewerDirectory,
                shellDirectory,
                images,
                cancellationToken);
            return CreateSession(sourcePath, cacheDirectory, shellDirectory, images);
        }
        catch (OperationCanceledException)
        {
            // A timed-out STA thread may still be unwinding its isolated render directory.
            // Without a complete marker, the next open will safely rebuild the cache.
            throw;
        }
        catch (InvalidDataException)
        {
            Reset(cacheDirectory);
            throw;
        }
        catch (IOException)
        {
            Reset(cacheDirectory);
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            Reset(cacheDirectory);
            throw;
        }
        catch (Exception exception)
        {
            return LoadViaPdfConversion(sourcePath, cancellationToken, exception);
        }
    }

    private static ReaderSession CreateSession(
        string sourcePath,
        string cacheDirectory,
        string shellDirectory,
        IReadOnlyList<string> images)
    {
        var sections = images.Select((_, index) => new ReaderSection(
                $"第 {index + 1} 页",
                Path.Combine(shellDirectory, $"{index + 1:D6}.html")))
            .ToArray();
        var comicPages = images.Select((path, index) => new ComicPage(index, path)).ToArray();
        return new ReaderSession
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
    }

    private ReaderSession LoadViaPdfConversion(
        string sourcePath,
        CancellationToken cancellationToken,
        Exception renderingException)
    {
        var sourceExtension = Path.GetExtension(sourcePath);
        if (!_converter.IsAvailable)
        {
            throw BuildUnsupportedException(
                "内置 XPS 页面渲染失败，且未找到可用的整文档转换引擎。",
                renderingException);
        }

        if (!_converter.CanConvert(sourceExtension, ".pdf"))
        {
            throw BuildUnsupportedException(
                "内置 XPS 页面渲染失败；当前 Calibre ebook-convert 不支持 XPS/OXPS 输入。",
                renderingException);
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
                throw BuildUnsupportedException(
                    result.ErrorMessage ?? "XPS/OXPS 整文档转换为 PDF 失败。",
                    renderingException);
            }

            if (!string.Equals(result.OutputPath, pdfPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(result.OutputPath, pdfPath, overwrite: true);
                try
                {
                    File.Delete(result.OutputPath);
                }
                catch
                {
                    // Best-effort cleanup of the converter's uniquely named output.
                }
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

    private static InvalidOperationException BuildUnsupportedException(string detail, Exception innerException)
    {
        return new InvalidOperationException(
            $"无法打开 XPS/OXPS 文档。{detail}\n\n" +
            "请先使用 Microsoft Print to PDF 或其他可靠转换工具将文档转为 PDF 后再打开。",
            innerException);
    }

    private static IReadOnlyList<string> EnumerateRenderedPages(string contentDirectory)
    {
        return Directory.EnumerateFiles(contentDirectory, "page-*.png", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> RenderDocumentPages(
        string sourcePath,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ValidatePackageLimits(sourcePath, cancellationToken);
        IReadOnlyList<string>? result = null;
        ExceptionDispatchInfo? failure = null;
        var abandoned = 0;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                result = RenderDocumentPagesCore(sourcePath, outputDirectory, cancellationToken);
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                if (Volatile.Read(ref abandoned) != 0)
                {
                    TryDeleteDirectory(outputDirectory);
                }

                completion.TrySetResult();
            }
        })
        {
            IsBackground = true,
            Name = "NoGaReader XPS renderer"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var stopwatch = Stopwatch.StartNew();
        while (!completion.Task.Wait(TimeSpan.FromMilliseconds(100)))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Volatile.Write(ref abandoned, 1);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (stopwatch.Elapsed >= MaximumRenderingDuration)
            {
                Volatile.Write(ref abandoned, 1);
                throw new TimeoutException(
                    $"XPS/OXPS 页面渲染超过 {MaximumRenderingDuration.TotalMinutes:0} 分钟安全时限。");
            }
        }

        failure?.Throw();
        return result ?? throw new InvalidOperationException("XPS/OXPS 页面渲染线程未返回结果。");
    }

    private static void ValidatePackageLimits(string sourcePath, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(sourcePath);
        if (archive.Entries.Count > MaximumEntryCount)
        {
            throw new InvalidDataException($"XPS/OXPS 包含超过 {MaximumEntryCount:N0} 个条目。");
        }

        var totalBytes = 0L;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Length < 0 || entry.Length > MaximumEntryBytes)
            {
                throw new InvalidDataException($"XPS/OXPS 资源超过 256 MB 安全限制：{entry.FullName}");
            }

            if (entry.Length > MaximumPackageBytes - totalBytes)
            {
                throw new InvalidDataException("XPS/OXPS 解压总量超过 1 GB 安全限制。");
            }

            if (entry.Length >= SuspiciousRatioMinimumBytes &&
                (entry.CompressedLength <= 0 ||
                 (double)entry.Length / entry.CompressedLength > MaximumCompressionRatio))
            {
                throw new InvalidDataException($"XPS/OXPS 资源压缩比异常：{entry.FullName}");
            }

            totalBytes += entry.Length;
        }
    }

    private static IReadOnlyList<string> RenderDocumentPagesCore(
        string sourcePath,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = new XpsDocument(sourcePath, FileAccess.Read);
        var sequence = document.GetFixedDocumentSequence()
            ?? throw new InvalidDataException("XPS/OXPS 文档缺少 FixedDocumentSequence。");
        var paginator = sequence.DocumentPaginator;
        paginator.ComputePageCount();
        var pageCount = paginator.PageCount;
        if (pageCount <= 0)
        {
            throw new InvalidDataException("XPS/OXPS 文档不包含 FixedPage。");
        }

        if (pageCount > MaximumPageCount)
        {
            throw new InvalidDataException($"XPS/OXPS 文档超过 {MaximumPageCount:N0} 页安全限制。");
        }

        var pages = new List<string>(pageCount);
        var totalPixels = 0L;
        var totalOutputBytes = 0L;
        for (var index = 0; index < pageCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = paginator.GetPage(index);
            try
            {
                if (ReferenceEquals(page, DocumentPage.Missing) || page.Visual is null)
                {
                    throw new InvalidDataException($"XPS/OXPS 第 {index + 1:N0} 页缺少可渲染内容。");
                }

                var size = GetValidPageSize(page, paginator.PageSize, index);
                var metrics = CalculateRenderMetrics(size);
                if (metrics.PixelCount > MaximumTotalPixels - totalPixels)
                {
                    throw new InvalidDataException("XPS/OXPS 页面累计像素超过安全限制。");
                }

                totalPixels += metrics.PixelCount;
                var destination = Path.Combine(outputDirectory, $"page-{index + 1:D6}.png");
                RenderPage(page.Visual, size, metrics, destination);
                var outputBytes = new FileInfo(destination).Length;
                if (outputBytes <= 0 || outputBytes > MaximumTotalOutputBytes - totalOutputBytes)
                {
                    throw new InvalidDataException("XPS/OXPS 页面输出总量超过 1 GB 安全限制。");
                }

                totalOutputBytes += outputBytes;
                pages.Add(destination);
            }
            finally
            {
                page.Dispose();
            }
        }

        return pages;
    }

    private static Size GetValidPageSize(DocumentPage page, Size paginatorSize, int pageIndex)
    {
        var size = page.Size;
        if (!IsValidDimension(size.Width) || !IsValidDimension(size.Height))
        {
            size = paginatorSize;
        }

        if (!IsValidDimension(size.Width) || !IsValidDimension(size.Height))
        {
            throw new InvalidDataException($"XPS/OXPS 第 {pageIndex + 1:N0} 页尺寸无效。");
        }

        return size;
    }

    private static bool IsValidDimension(double value)
    {
        return double.IsFinite(value) && value > 0;
    }

    private static RenderMetrics CalculateRenderMetrics(Size pageSize)
    {
        var scale = TargetDpi / 96d;
        var targetPixels = pageSize.Width * scale * pageSize.Height * scale;
        if (!double.IsFinite(targetPixels) || targetPixels <= 0)
        {
            throw new InvalidDataException("XPS/OXPS 页面像素尺寸无效。");
        }

        if (targetPixels > MaximumPagePixels)
        {
            scale *= Math.Sqrt(MaximumPagePixels / targetPixels);
        }

        var largestDimension = Math.Max(pageSize.Width, pageSize.Height) * scale;
        if (largestDimension > MaximumPageDimension)
        {
            scale *= MaximumPageDimension / largestDimension;
        }

        var pixelWidth = Math.Max(1, (int)Math.Ceiling(pageSize.Width * scale));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(pageSize.Height * scale));
        var pixelCount = (long)pixelWidth * pixelHeight;
        if (pixelCount > MaximumPagePixels)
        {
            throw new InvalidDataException("XPS/OXPS 单页像素超过安全限制。");
        }

        return new RenderMetrics(pixelWidth, pixelHeight, scale * 96d, pixelCount);
    }

    private static void RenderPage(
        Visual pageVisual,
        Size pageSize,
        RenderMetrics metrics,
        string destination)
    {
        var canvas = new DrawingVisual();
        using (var drawing = canvas.RenderOpen())
        {
            var bounds = new Rect(new Point(0, 0), pageSize);
            drawing.DrawRectangle(Brushes.White, null, bounds);
            var pageBrush = new VisualBrush(pageVisual)
            {
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                Stretch = Stretch.Fill
            };
            drawing.DrawRectangle(pageBrush, null, bounds);
        }

        var bitmap = new RenderTargetBitmap(
            metrics.PixelWidth,
            metrics.PixelHeight,
            metrics.Dpi,
            metrics.Dpi,
            PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(output);
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
            try
            {
                Directory.Delete(directory, true);
            }
            catch
            {
                // Best-effort cache cleanup.
            }
        }

        Directory.CreateDirectory(directory);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch (IOException)
        {
            // The abandoned render thread will retry cleanup after it unwinds.
        }
        catch (UnauthorizedAccessException)
        {
            // The abandoned render thread will retry cleanup after it unwinds.
        }
    }

    private readonly record struct RenderMetrics(
        int PixelWidth,
        int PixelHeight,
        double Dpi,
        long PixelCount);
}
