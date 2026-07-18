using System.IO.Compression;

namespace NoGaReader.Services;

internal static class SafeArchiveExtractor
{
    private const int MaximumEntryCount = 10_000;
    private const long MaximumSingleEntryBytes = 192L * 1024 * 1024;
    private const long MaximumTotalBytes = 768L * 1024 * 1024;

    public static void ExtractAll(ZipArchive archive, string destination, CancellationToken cancellationToken)
    {
        if (archive.Entries.Count > MaximumEntryCount)
        {
            throw new InvalidDataException($"压缩包包含过多文件（上限 {MaximumEntryCount} 个）。");
        }

        Directory.CreateDirectory(destination);
        var destinationRoot = Path.GetFullPath(destination)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        long declaredTotalBytes = 0;
        long actualTotalBytes = 0;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsSymbolicLink(entry))
            {
                throw new InvalidDataException("压缩包包含不受支持的符号链接。");
            }

            if (entry.Length > MaximumSingleEntryBytes)
            {
                throw new InvalidDataException($"压缩包中的单个文件过大：{entry.FullName}");
            }

            declaredTotalBytes += entry.Length;
            if (declaredTotalBytes > MaximumTotalBytes)
            {
                throw new InvalidDataException("解压后的文件总量超过 768 MB 安全限制。");
            }

            var normalizedName = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                continue;
            }

            var outputPath = Path.GetFullPath(Path.Combine(destinationRoot, normalizedName));
            if (!outputPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("压缩包包含越界路径。");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(outputPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var input = entry.Open();
            using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81_920];
            long actualEntryBytes = 0;
            int bytesRead;
            while ((bytesRead = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                actualEntryBytes += bytesRead;
                actualTotalBytes += bytesRead;
                if (actualEntryBytes > MaximumSingleEntryBytes || actualTotalBytes > MaximumTotalBytes)
                {
                    throw new InvalidDataException("压缩包的实际解压体积超过安全限制。");
                }

                output.Write(buffer, 0, bytesRead);
            }
        }
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixSymbolicLink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        return unixMode == UnixSymbolicLink;
    }
}
