using System.Security.Cryptography;
using System.Text;
using NoGaReader.Models;
using NoGaReader.Utilities;

namespace NoGaReader.Services;

internal sealed class ComicFolderLoader
{
    public const string ContentHostName = "comic-content.nogareader.local";

    private const string CacheVersion = "comic-folder-v1";
    private const int MaximumPageCount = 20_000;
    private const int MaximumScannedFileCount = 100_000;

    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false
    };

    public ReaderSession Load(string sourceDirectory, CancellationToken cancellationToken)
    {
        var rootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        if (!Directory.Exists(rootDirectory))
        {
            throw new DirectoryNotFoundException($"漫画图片文件夹不存在：{rootDirectory}");
        }

        if ((File.GetAttributes(rootDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new NotSupportedException("为避免越过用户选择的目录，不能直接打开符号链接或重解析点文件夹。");
        }

        var images = EnumerateImages(rootDirectory, cancellationToken);
        if (images.Count == 0)
        {
            throw new InvalidDataException("所选文件夹及其子文件夹中没有可显示的漫画图片。");
        }

        var cacheDirectory = AppPaths.GetDirectoryCacheDirectory(rootDirectory, "comic-folder");
        var viewerDirectory = Path.Combine(cacheDirectory, ".viewer");
        var shellDirectory = Path.Combine(viewerDirectory, "pages");
        var completeMarker = Path.Combine(cacheDirectory, ".complete");
        var signature = $"{CacheVersion}|{CreateSignature(rootDirectory, images)}";
        var cacheReady = File.Exists(completeMarker) &&
                         string.Equals(File.ReadAllText(completeMarker), signature, StringComparison.Ordinal);
        if (!cacheReady)
        {
            ResetCache(cacheDirectory);
            try
            {
                ComicArchiveLoader.WriteViewerAssets(
                    cacheDirectory,
                    viewerDirectory,
                    shellDirectory,
                    images,
                    cancellationToken,
                    path => CreateContentUrl(rootDirectory, path));
                File.WriteAllText(completeMarker, signature, Encoding.UTF8);
            }
            catch
            {
                ResetCache(cacheDirectory);
                throw;
            }
        }

        var shellPaths = images
            .Select((_, index) => Path.Combine(shellDirectory, $"{index + 1:D6}.html"))
            .ToArray();
        if (!File.Exists(Path.Combine(viewerDirectory, "manifest.json")) ||
            !File.Exists(Path.Combine(viewerDirectory, "comic-reader.js")) ||
            !File.Exists(Path.Combine(viewerDirectory, "comic-reader.css")) ||
            shellPaths.Any(path => !File.Exists(path)))
        {
            ComicArchiveLoader.WriteViewerAssets(
                cacheDirectory,
                viewerDirectory,
                shellDirectory,
                images,
                cancellationToken,
                path => CreateContentUrl(rootDirectory, path));
        }

        var sections = shellPaths
            .Select((path, index) => new ReaderSection($"第 {index + 1} 页", path))
            .ToArray();
        var pages = images.Select((path, index) => new ComicPage(index, path)).ToArray();
        var directoryInfo = new DirectoryInfo(rootDirectory);
        return new ReaderSession
        {
            SourcePath = rootDirectory,
            Title = string.IsNullOrWhiteSpace(directoryInfo.Name) ? rootDirectory : directoryInfo.Name,
            RootDirectory = cacheDirectory,
            ComicContentRootDirectory = rootDirectory,
            Kind = ReaderDocumentKind.Comic,
            Sections = sections,
            ComicPages = pages,
            CoverImagePath = images[0],
            IsReflowable = false,
            SupportsInPageSearch = false,
            EnableScriptExecution = true
        };
    }

    private static IReadOnlyList<string> EnumerateImages(
        string rootDirectory,
        CancellationToken cancellationToken)
    {
        var images = new List<string>();
        var directories = new Stack<string>();
        directories.Push(rootDirectory);
        var scannedFiles = 0;
        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = directories.Pop();
            FileSystemInfo[] entries;
            try
            {
                entries = new DirectoryInfo(directory).GetFileSystemInfos("*", EnumerationOptions);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"无法读取漫画子文件夹：{directory}", exception);
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(entry.FullName);
                    continue;
                }

                scannedFiles++;
                if (scannedFiles > MaximumScannedFileCount)
                {
                    throw new InvalidDataException($"图片文件夹扫描超过 {MaximumScannedFileCount:N0} 个文件的安全上限。");
                }

                if (!ComicArchiveExtractor.IsImageExtension(entry.Extension))
                {
                    continue;
                }

                if (images.Count >= MaximumPageCount)
                {
                    throw new InvalidDataException($"图片文件夹包含超过 {MaximumPageCount:N0} 张漫画页。");
                }
                images.Add(Path.GetFullPath(entry.FullName));
            }
        }

        return images
            .OrderBy(path => Path.GetRelativePath(rootDirectory, path), NaturalStringComparer.Instance)
            .ToArray();
    }

    private static string CreateSignature(string rootDirectory, IReadOnlyList<string> images)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in images)
        {
            var info = new FileInfo(path);
            var value = $"{Path.GetRelativePath(rootDirectory, path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}\n";
            hash.AppendData(Encoding.UTF8.GetBytes(value));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string CreateContentUrl(string rootDirectory, string path)
    {
        var relative = Path.GetRelativePath(rootDirectory, path).Replace('\\', '/');
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new InvalidDataException("漫画图片位于所选文件夹之外。");
        }

        var encodedPath = string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
        return $"https://{ContentHostName}/{encodedPath}";
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
