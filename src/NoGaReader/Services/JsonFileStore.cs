using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoGaReader.Services;

internal static class JsonFileStore
{
    private static readonly ConcurrentDictionary<string, object> FileLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static T Load<T>(string path, T fallback)
    {
        var normalizedPath = NormalizePath(path);
        lock (FileLocks.GetOrAdd(normalizedPath, static _ => new object()))
        {
            try
            {
                if (!File.Exists(normalizedPath))
                {
                    return fallback;
                }

                var json = File.ReadAllText(normalizedPath);
                return JsonSerializer.Deserialize<T>(json, Options) ?? fallback;
            }
            catch (JsonException)
            {
                return fallback;
            }
            catch (IOException)
            {
                return fallback;
            }
            catch (UnauthorizedAccessException)
            {
                return fallback;
            }
        }
    }

    public static void Save<T>(string path, T value)
    {
        var normalizedPath = NormalizePath(path);
        lock (FileLocks.GetOrAdd(normalizedPath, static _ => new object()))
        {
            var directory = Path.GetDirectoryName(normalizedPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = Path.Combine(
                directory ?? string.Empty,
                $".{Path.GetFileName(normalizedPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                var json = JsonSerializer.Serialize(value, Options);
                File.WriteAllText(temporaryPath, json);
                File.Move(temporaryPath, normalizedPath, true);
            }
            finally
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // A failed cleanup must not hide the original save result.
                }
                catch (UnauthorizedAccessException)
                {
                    // A failed cleanup must not hide the original save result.
                }
            }
        }
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }
}
