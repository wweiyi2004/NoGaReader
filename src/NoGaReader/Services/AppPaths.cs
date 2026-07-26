using System.Security.Cryptography;
using System.Text;

namespace NoGaReader.Services;

public static class AppPaths
{
    private static readonly TimeSpan CacheCleanupInterval = TimeSpan.FromDays(7);
    private static readonly TimeSpan MaximumCacheAge = TimeSpan.FromDays(45);
    private const int MaximumCacheDeletesPerRun = 128;

    private static readonly object RootSync = new();
    private static string? _configuredRoot;
    private static string? _resolvedRoot;

    public static string Root
    {
        get
        {
            lock (RootSync)
            {
                return _configuredRoot ?? (_resolvedRoot ??= ResolveRoot());
            }
        }
    }

    public static string DataRoot => Path.Combine(Root, "Data");

    public static string CacheRoot => Path.Combine(Root, "Cache");

    public static void ConfigureRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathRooted(root))
        {
            throw new ArgumentException("数据目录必须是绝对路径。", nameof(root));
        }

        var normalized = Path.GetFullPath(root);
        lock (RootSync)
        {
            if (_configuredRoot is not null && !PathSemantics.Equals(_configuredRoot, normalized))
            {
                throw new InvalidOperationException("应用数据目录已配置，不能在运行期间更改。");
            }

            if (_configuredRoot is null &&
                _resolvedRoot is not null &&
                !PathSemantics.Equals(_resolvedRoot, normalized))
            {
                // Something already read Root and may have cached or created paths
                // under the default location. Silently switching roots would split
                // application data, so surface the ordering bug instead.
                throw new InvalidOperationException(
                    "应用数据目录已按默认规则解析并可能已被使用；ConfigureRoot 必须在任何数据访问之前调用。");
            }

            _configuredRoot = normalized;
        }
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(CacheRoot);
    }

    public static string GetDocumentCacheDirectory(string sourcePath, string category)
    {
        var file = new FileInfo(sourcePath);
        var identity = $"{Path.GetFullPath(sourcePath)}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        return Path.Combine(CacheRoot, category, hash);
    }

    public static string GetDirectoryCacheDirectory(string sourceDirectory, string category)
    {
        var identity = $"{Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory))}|directory";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        return Path.Combine(CacheRoot, category, hash);
    }

    public static bool IsInsideCache(string path)
    {
        return PathSemantics.IsInside(CacheRoot, path);
    }

    public static void StartCacheCleanup()
    {
        _ = Task.Run(() =>
        {
            try
            {
                EnsureCreated();
                var statePath = Path.Combine(DataRoot, "cache-cleanup.txt");
                if (File.Exists(statePath) &&
                    DateTime.UtcNow - File.GetLastWriteTimeUtc(statePath) < CacheCleanupInterval)
                {
                    return;
                }

                var cutoff = DateTime.UtcNow - MaximumCacheAge;
                var deleted = 0;
                foreach (var category in Directory.EnumerateDirectories(CacheRoot))
                {
                    if (string.Equals(
                            Path.GetFileName(category),
                            "library-covers",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foreach (var cacheDirectory in Directory.EnumerateDirectories(category))
                    {
                        if (deleted >= MaximumCacheDeletesPerRun)
                        {
                            break;
                        }

                        if (Directory.GetLastWriteTimeUtc(cacheDirectory) >= cutoff ||
                            !IsInsideCache(cacheDirectory))
                        {
                            continue;
                        }

                        Directory.Delete(cacheDirectory, true);
                        deleted++;
                    }

                    if (deleted >= MaximumCacheDeletesPerRun)
                    {
                        break;
                    }
                }

                File.WriteAllText(statePath, DateTimeOffset.UtcNow.ToString("O"));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException or
                    NotSupportedException)
            {
                // Cache cleanup is best-effort and must never affect reading.
            }
        });
    }

    private static string ResolveRoot()
    {
        var configured = Environment.GetEnvironmentVariable("NOGAREADER_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathRooted(configured))
        {
            return Path.GetFullPath(configured);
        }

        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.mode")))
        {
            return Path.Combine(AppContext.BaseDirectory, "UserData");
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NoGaReader.sln")))
            {
                return Path.Combine(directory.FullName, ".local-data");
            }

            directory = directory.Parent;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NoGaReader");
    }
}
