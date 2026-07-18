using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoGaReader.Services;

internal static class JsonFileStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static T Load<T>(string path, T fallback)
    {
        try
        {
            if (!File.Exists(path))
            {
                return fallback;
            }

            var json = File.ReadAllText(path);
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

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        var json = JsonSerializer.Serialize(value, Options);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, path, true);
    }
}
