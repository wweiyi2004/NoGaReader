using System.Diagnostics;
using NoGaReader.Models;

namespace NoGaReader.Services;

public sealed class CalibreRuntimeInfo
{
    public required string EbookConvertPath { get; init; }

    public string? Version { get; init; }

    public required string Source { get; init; }

    public bool Exists => File.Exists(EbookConvertPath);
}

public static class CalibreRuntimeLocator
{
    public const string EmbeddedRelativePath = @"engines\calibre\ebook-convert.exe";

    public static string EmbeddedEngineDirectory =>
        Path.Combine(AppContext.BaseDirectory, "engines", "calibre");

    public static string EmbeddedEbookConvertPath =>
        Path.Combine(AppContext.BaseDirectory, EmbeddedRelativePath);

    public static string UserEngineDirectory =>
        Path.Combine(AppPaths.Root, "engines", "calibre");

    public static string UserEbookConvertPath =>
        Path.Combine(UserEngineDirectory, "ebook-convert.exe");

    public static CalibreRuntimeInfo? Locate(AppSettings settings)
    {
        foreach (var candidate in EnumerateCandidates(settings))
        {
            if (!File.Exists(candidate.Path))
            {
                continue;
            }

            return new CalibreRuntimeInfo
            {
                EbookConvertPath = candidate.Path,
                Version = TryReadVersion(candidate.Path),
                Source = candidate.Source
            };
        }

        return null;
    }

    public static IReadOnlyList<(string Path, string Source)> EnumerateCandidates(AppSettings settings)
    {
        var results = new List<(string Path, string Source)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path, string source)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return;
            }

            if (!seen.Add(fullPath))
            {
                return;
            }

            results.Add((fullPath, source));
        }

        if (!string.IsNullOrWhiteSpace(settings.CalibreEbookConvertPath))
        {
            Add(settings.CalibreEbookConvertPath, "用户指定");
        }

        Add(EmbeddedEbookConvertPath, "安装包内嵌");
        Add(UserEbookConvertPath, "用户数据引擎");

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        Add(Path.Combine(programFiles, "Calibre2", "ebook-convert.exe"), "系统安装");
        Add(Path.Combine(programFilesX86, "Calibre2", "ebook-convert.exe"), "系统安装");
        Add(Path.Combine(programFiles, "Calibre", "ebook-convert.exe"), "系统安装");

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var segment in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                Add(Path.Combine(segment.Trim().Trim('"'), "ebook-convert.exe"), "PATH");
            }
        }

        return results;
    }

    public static string? TryReadVersion(string ebookConvertPath)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ebookConvertPath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(8_000);
            var text = string.IsNullOrWhiteSpace(output) ? error : output;
            text = text.Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal)
                .Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    public static string BuildMissingRuntimeMessage()
    {
        return
            "未找到 Calibre 转换引擎（ebook-convert.exe）。\n\n" +
            "可选方式：\n" +
            "1. 将 Calibre 运行时放到安装目录 engines\\calibre\\\n" +
            "2. 放到用户数据目录 engines\\calibre\\\n" +
            "3. 安装系统版 Calibre\n" +
            "4. 在转换设置中手动指定 ebook-convert.exe\n\n" +
            "转换功能免费且无次数限制。";
    }
}
