using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Readers;
using NoGaReader.Utilities;

namespace NoGaReader.Services;

internal sealed record ComicCoverData(byte[] Bytes, string Extension);

internal static class ComicArchiveExtractor
{
    private const int MaximumArchiveEntryCount = 20_000;
    private const long MaximumPageBytes = 256L * 1024 * 1024;
    private const long MaximumExtractedBytes = 4L * 1024 * 1024 * 1024;
    private const long MaximumCoverBytes = 16L * 1024 * 1024;
    private const long SuspiciousRatioMinimumBytes = 32L * 1024 * 1024;
    private const double MaximumCompressionRatio = 1_000;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tif", ".tiff"
    };

    public static bool IsComicExtension(string extension)
    {
        return extension.Equals(".cbz", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".cbr", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".cb7", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".rar", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".cbt", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsImageExtension(string extension) => ImageExtensions.Contains(extension);

    public static IReadOnlyList<string> ExtractPages(
        string sourcePath,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (Path.GetExtension(sourcePath) is var extension &&
            (extension.Equals(".cbz", StringComparison.OrdinalIgnoreCase) ||
             extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)))
        {
            return ExtractZipPages(sourcePath, outputDirectory, cancellationToken);
        }

        Directory.CreateDirectory(outputDirectory);
        var normalizedRoot = Path.GetFullPath(outputDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;

        using var archive = ArchiveFactory.OpenArchive(
            sourcePath,
            new ReaderOptions { LookForHeader = true });
        if (archive.IsEncrypted)
        {
            throw new NotSupportedException("暂不支持带密码的漫画压缩包。");
        }

        var entries = archive.Entries.ToArray();
        if (entries.Length > MaximumArchiveEntryCount)
        {
            throw new InvalidDataException($"漫画压缩包包含超过 {MaximumArchiveEntryCount:N0} 个条目。");
        }

        var imageEntries = entries
            .Where(entry => !entry.IsDirectory &&
                !string.IsNullOrWhiteSpace(entry.Key) &&
                ImageExtensions.Contains(Path.GetExtension(entry.Key)))
            .OrderBy(entry => entry.Key!, NaturalStringComparer.Instance)
            .ToArray();
        if (imageEntries.Length == 0)
        {
            throw new InvalidDataException("漫画压缩包中没有可显示的图片。");
        }

        var extractedPaths = new List<string>(imageEntries.Length);
        var seenDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalBytes = 0L;
        foreach (var entry in imageEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateEntry(entry);
            var destinationPath = ResolveDestination(normalizedRoot, entry.Key!);
            if (!seenDestinations.Add(destinationPath))
            {
                throw new InvalidDataException($"漫画压缩包包含重复图片路径：{entry.Key}");
            }

            if (entry.Size > MaximumExtractedBytes - totalBytes)
            {
                throw new InvalidDataException("漫画解包后的图片总量超过 4 GB 安全限制。");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            using var input = entry.OpenEntryStream();
            using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            // Account for actually decompressed bytes: declared sizes come from the
            // archive directory and can lie, so the total budget caps the copy too.
            totalBytes += CopyWithLimit(
                input,
                output,
                Math.Min(MaximumPageBytes, MaximumExtractedBytes - totalBytes),
                cancellationToken);
            extractedPaths.Add(destinationPath);
        }

        return extractedPaths;
    }

    public static ComicCoverData? ReadCover(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        if (Path.GetExtension(sourcePath) is var extension &&
            (extension.Equals(".cbz", StringComparison.OrdinalIgnoreCase) ||
             extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)))
        {
            return ReadZipCover(sourcePath, cancellationToken);
        }

        using var archive = ArchiveFactory.OpenArchive(
            sourcePath,
            new ReaderOptions { LookForHeader = true });
        if (archive.IsEncrypted)
        {
            return null;
        }

        var cover = archive.Entries
            .Where(entry => !entry.IsDirectory &&
                !string.IsNullOrWhiteSpace(entry.Key) &&
                ImageExtensions.Contains(Path.GetExtension(entry.Key)))
            .OrderBy(entry => entry.Key!, NaturalStringComparer.Instance)
            .FirstOrDefault();
        if (cover is null || cover.IsEncrypted || cover.LinkTarget is not null ||
            cover.Size <= 0 || cover.Size > MaximumCoverBytes)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var input = cover.OpenEntryStream();
        using var output = new MemoryStream((int)cover.Size);
        CopyWithLimit(input, output, MaximumCoverBytes, cancellationToken);
        return new ComicCoverData(output.ToArray(), NormalizeImageExtension(Path.GetExtension(cover.Key!)));
    }

    private static IReadOnlyList<string> ExtractZipPages(
        string sourcePath,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ThrowIfZipEncrypted(sourcePath);
        Directory.CreateDirectory(outputDirectory);
        var normalizedRoot = Path.GetFullPath(outputDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(sourcePath);
        if (archive.Entries.Count > MaximumArchiveEntryCount)
        {
            throw new InvalidDataException($"漫画压缩包包含超过 {MaximumArchiveEntryCount:N0} 个条目。");
        }

        var imageEntries = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name) &&
                ImageExtensions.Contains(Path.GetExtension(entry.FullName)))
            .OrderBy(entry => entry.FullName, NaturalStringComparer.Instance)
            .ToArray();
        if (imageEntries.Length == 0)
        {
            throw new InvalidDataException("漫画压缩包中没有可显示的图片。");
        }

        var extractedPaths = new List<string>(imageEntries.Length);
        var seenDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalBytes = 0L;
        foreach (var entry in imageEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateZipEntry(entry);
            var destinationPath = ResolveDestination(normalizedRoot, entry.FullName);
            if (!seenDestinations.Add(destinationPath))
            {
                throw new InvalidDataException($"漫画压缩包包含重复图片路径：{entry.FullName}");
            }

            if (entry.Length > MaximumExtractedBytes - totalBytes)
            {
                throw new InvalidDataException("漫画解包后的图片总量超过 4 GB 安全限制。");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            using var input = entry.Open();
            using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            // Account for actually decompressed bytes: declared sizes come from the
            // archive directory and can lie, so the total budget caps the copy too.
            totalBytes += CopyWithLimit(
                input,
                output,
                Math.Min(MaximumPageBytes, MaximumExtractedBytes - totalBytes),
                cancellationToken);
            extractedPaths.Add(destinationPath);
        }

        return extractedPaths;
    }

    private static ComicCoverData? ReadZipCover(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        if (IsZipEncrypted(sourcePath))
        {
            return null;
        }

        using var archive = ZipFile.OpenRead(sourcePath);
        var cover = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name) &&
                ImageExtensions.Contains(Path.GetExtension(entry.FullName)))
            .OrderBy(entry => entry.FullName, NaturalStringComparer.Instance)
            .FirstOrDefault();
        if (cover is null || cover.Length <= 0 || cover.Length > MaximumCoverBytes)
        {
            return null;
        }

        ValidateZipEntry(cover);
        cancellationToken.ThrowIfCancellationRequested();
        using var input = cover.Open();
        using var output = new MemoryStream((int)cover.Length);
        CopyWithLimit(input, output, MaximumCoverBytes, cancellationToken);
        return new ComicCoverData(
            output.ToArray(),
            NormalizeImageExtension(Path.GetExtension(cover.FullName)));
    }

    private static void ThrowIfZipEncrypted(string sourcePath)
    {
        if (IsZipEncrypted(sourcePath))
        {
            throw new NotSupportedException("暂不支持带密码的漫画压缩包。");
        }
    }

    private static bool IsZipEncrypted(string sourcePath)
    {
        // System.IO.Compression cannot report encryption, so probe the central
        // directory with SharpCompress (headers only, no decompression) to keep
        // the friendly password rejection the slow path always had.
        using var probe = ArchiveFactory.OpenArchive(
            sourcePath,
            new ReaderOptions { LookForHeader = true });
        return probe.IsEncrypted || probe.Entries.Any(entry => entry.IsEncrypted);
    }

    private static void ValidateZipEntry(ZipArchiveEntry entry)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixSymbolicLink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        if (unixMode == UnixSymbolicLink)
        {
            throw new InvalidDataException("漫画压缩包包含符号链接，已拒绝解包。");
        }

        if (entry.Length < 0 || entry.Length > MaximumPageBytes)
        {
            throw new InvalidDataException($"漫画图片超过 256 MB 安全限制：{entry.FullName}");
        }

        if (entry.Length >= SuspiciousRatioMinimumBytes && entry.CompressedLength > 0 &&
            (double)entry.Length / entry.CompressedLength > MaximumCompressionRatio)
        {
            throw new InvalidDataException($"漫画图片压缩比异常，已拒绝解包：{entry.FullName}");
        }
    }

    private static void ValidateEntry(IArchiveEntry entry)
    {
        if (entry.IsEncrypted)
        {
            throw new NotSupportedException("漫画中包含加密图片，当前无法打开。");
        }

        if (!string.IsNullOrWhiteSpace(entry.LinkTarget))
        {
            throw new InvalidDataException("漫画压缩包包含符号链接，已拒绝解包。");
        }

        if (entry.Size < 0 || entry.Size > MaximumPageBytes)
        {
            throw new InvalidDataException($"漫画图片超过 256 MB 安全限制：{entry.Key}");
        }

        if (entry.Size >= SuspiciousRatioMinimumBytes && entry.CompressedSize > 0 &&
            (double)entry.Size / entry.CompressedSize > MaximumCompressionRatio)
        {
            throw new InvalidDataException($"漫画图片压缩比异常，已拒绝解包：{entry.Key}");
        }
    }

    private static string ResolveDestination(string normalizedRoot, string entryKey)
    {
        if (string.IsNullOrWhiteSpace(entryKey))
        {
            throw new InvalidDataException("漫画压缩包包含空图片路径。");
        }

        var normalizedKey = entryKey.Replace('\\', '/');
        if (normalizedKey.StartsWith("/", StringComparison.Ordinal) ||
            normalizedKey.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidDataException($"漫画图片使用了绝对路径：{entryKey}");
        }

        var segments = normalizedKey.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException($"漫画图片路径试图越过缓存目录：{entryKey}");
        }

        var destination = Path.GetFullPath(Path.Combine([normalizedRoot, .. segments]));
        if (!destination.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"漫画图片路径位于缓存目录之外：{entryKey}");
        }

        return destination;
    }

    private static long CopyWithLimit(
        Stream input,
        Stream output,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
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
            if (written > maximumBytes)
            {
                throw new InvalidDataException("漫画图片解压后超过安全大小限制。");
            }

            output.Write(buffer, 0, read);
        }

        return written;
    }

    private static string NormalizeImageExtension(string extension)
    {
        return extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : extension.ToLowerInvariant();
    }
}
