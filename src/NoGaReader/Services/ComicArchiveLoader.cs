using System.Text;
using System.Text.Json;
using NoGaReader.Models;
using NoGaReader.Utilities;

namespace NoGaReader.Services;

internal sealed class ComicArchiveLoader
{
    private const string CacheVersion = "comic-reader-v3";
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tif", ".tiff"
    };

    public ReaderSession Load(string sourcePath, CancellationToken cancellationToken)
    {
        var cacheDirectory = AppPaths.GetDocumentCacheDirectory(sourcePath, "comic");
        var contentDirectory = Path.Combine(cacheDirectory, "content");
        var viewerDirectory = Path.Combine(cacheDirectory, ".viewer");
        var shellDirectory = Path.Combine(viewerDirectory, "pages");
        var completeMarker = Path.Combine(cacheDirectory, ".complete");

        var cacheReady = File.Exists(completeMarker) &&
                         string.Equals(File.ReadAllText(completeMarker), CacheVersion, StringComparison.Ordinal);
        if (!cacheReady)
        {
            ResetCache(cacheDirectory);
            try
            {
                Directory.CreateDirectory(contentDirectory);
                var extracted = ComicArchiveExtractor.ExtractPages(
                    sourcePath,
                    contentDirectory,
                    cancellationToken);
                WriteViewerAssets(cacheDirectory, viewerDirectory, shellDirectory, extracted, cancellationToken);
                File.WriteAllText(completeMarker, CacheVersion, Encoding.UTF8);
            }
            catch
            {
                ResetCache(cacheDirectory);
                throw;
            }
        }

        var images = Directory.EnumerateFiles(contentDirectory, "*", SearchOption.AllDirectories)
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => Path.GetRelativePath(contentDirectory, path), NaturalStringComparer.Instance)
            .ToArray();
        if (images.Length == 0)
        {
            ResetCache(cacheDirectory);
            throw new InvalidDataException("漫画缓存中没有可显示的图片，请重新打开文件。");
        }

        EnsureViewerAssets(cacheDirectory, viewerDirectory, shellDirectory, images, cancellationToken);
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

    private static void EnsureViewerAssets(
        string cacheDirectory,
        string viewerDirectory,
        string shellDirectory,
        IReadOnlyList<string> images,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(viewerDirectory, "manifest.json");
        var scriptPath = Path.Combine(viewerDirectory, "comic-reader.js");
        var stylePath = Path.Combine(viewerDirectory, "comic-reader.css");
        if (File.Exists(manifestPath) && File.Exists(scriptPath) && File.Exists(stylePath) &&
            images.Select((_, index) => Path.Combine(shellDirectory, $"{index + 1:D6}.html")).All(File.Exists))
        {
            return;
        }

        WriteViewerAssets(cacheDirectory, viewerDirectory, shellDirectory, images, cancellationToken);
    }

    internal static void WriteViewerAssets(
        string cacheDirectory,
        string viewerDirectory,
        string shellDirectory,
        IReadOnlyList<string> images,
        CancellationToken cancellationToken,
        Func<string, string>? pageUrlFactory = null)
    {
        Directory.CreateDirectory(viewerDirectory);
        Directory.CreateDirectory(shellDirectory);
        var manifest = new
        {
            schemaVersion = 1,
            pages = images.Select(path => pageUrlFactory?.Invoke(path) ?? MakeRelativeUrl(shellDirectory, path)).ToArray()
        };
        File.WriteAllText(
            Path.Combine(viewerDirectory, "manifest.json"),
            JsonSerializer.Serialize(manifest),
            Encoding.UTF8);
        File.WriteAllText(Path.Combine(viewerDirectory, "comic-reader.css"), ComicReaderAssets.StyleSheet, Encoding.UTF8);
        File.WriteAllText(Path.Combine(viewerDirectory, "comic-reader.js"), ComicReaderAssets.Script, Encoding.UTF8);

        for (var index = 0; index < images.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var shellPath = Path.Combine(shellDirectory, $"{index + 1:D6}.html");
            File.WriteAllText(shellPath, BuildShell(index, images.Count), Encoding.UTF8);
        }

        var normalizedCache = Path.GetFullPath(cacheDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(viewerDirectory).StartsWith(normalizedCache, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("漫画查看器资源位于缓存目录之外。");
        }
    }

    private static string BuildShell(int pageIndex, int pageCount)
    {
        return $$"""
                 <!doctype html>
                 <html lang="zh-CN">
                 <head>
                   <meta charset="utf-8">
                   <meta name="viewport" content="width=device-width,initial-scale=1,maximum-scale=3,user-scalable=yes">
                   <meta name="color-scheme" content="dark">
                   <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src 'self' https://comic-content.nogareader.local; style-src 'self'; script-src 'self'; connect-src 'self'">
                   <title>第 {{pageIndex + 1}} 页</title>
                   <link rel="stylesheet" href="../comic-reader.css">
                 </head>
                 <body data-start-page="{{pageIndex}}" data-manifest="../manifest.json">
                   <main id="viewport" aria-label="漫画阅读区域">
                     <div id="stage"></div>
                   </main>
                   <div id="loading">正在准备漫画页面…</div>
                   <div id="error" role="alert">漫画页面加载失败</div>
                   <div id="hud" aria-live="polite">{{pageIndex + 1}} / {{pageCount}}</div>
                   <script src="../comic-reader.js"></script>
                 </body>
                 </html>
                 """;
    }

    private static string MakeRelativeUrl(string baseDirectory, string path)
    {
        var relative = Path.GetRelativePath(baseDirectory, path).Replace('\\', '/');
        return string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
    }

    private static void ResetCache(string cacheDirectory)
    {
        if (Directory.Exists(cacheDirectory))
        {
            if (!AppPaths.IsInsideCache(cacheDirectory))
            {
                throw new InvalidOperationException("拒绝清理缓存根目录之外的路径。");
            }

            Directory.Delete(cacheDirectory, true);
        }

        Directory.CreateDirectory(cacheDirectory);
    }
}
