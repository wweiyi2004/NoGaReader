using System.Diagnostics;
using System.Text;
using NoGaReader.Models;

namespace NoGaReader.Services;

public interface IFormatConverter
{
    bool IsAvailable { get; }

    string? RuntimePath { get; }

    string? RuntimeVersion { get; }

    string RuntimeSource { get; }

    bool CanConvert(string sourceExtension, string targetExtension);

    IReadOnlyList<ConversionFormat> InputFormats { get; }

    IReadOnlyList<ConversionFormat> OutputFormats { get; }

    Task<ConversionResult> ConvertAsync(
        string sourcePath,
        string targetExtension,
        string outputDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class CalibreConverter : IFormatConverter
{
    private static readonly HashSet<string> SupportedInputs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".mobi", ".azw", ".azw3", ".azw4", ".pdf", ".fb2", ".txt", ".html", ".htm",
        ".docx", ".rtf", ".cbz", ".cbr", ".cb7", ".zip", ".rar", ".md", ".markdown",
        ".lit", ".lrf", ".pdb", ".prc", ".pml", ".rb", ".snb", ".tcr", ".txtz", ".htmlz"
    };

    private static readonly ConversionFormat[] SupportedOutputs =
    [
        new(".epub", "EPUB"),
        new(".pdf", "PDF"),
        new(".mobi", "MOBI"),
        new(".azw3", "AZW3"),
        new(".docx", "DOCX"),
        new(".fb2", "FB2"),
        new(".rtf", "RTF"),
        new(".txt", "TXT"),
        new(".htmlz", "HTMLZ"),
        new(".zip", "ZIP")
    ];

    private static readonly ConversionFormat[] SupportedInputFormats =
    [
        new(".epub", "EPUB"),
        new(".mobi", "MOBI"),
        new(".azw", "AZW"),
        new(".azw3", "AZW3"),
        new(".pdf", "PDF"),
        new(".fb2", "FB2"),
        new(".txt", "TXT"),
        new(".html", "HTML"),
        new(".docx", "DOCX"),
        new(".rtf", "RTF"),
        new(".cbz", "CBZ"),
        new(".cbr", "CBR"),
        new(".md", "Markdown")
    ];

    private readonly AppSettings _settings;
    private CalibreRuntimeInfo? _runtime;

    public CalibreConverter(AppSettings settings)
    {
        _settings = settings;
        RefreshRuntime();
    }

    public bool IsAvailable => _runtime?.Exists == true;

    public string? RuntimePath => _runtime?.EbookConvertPath;

    public string? RuntimeVersion => _runtime?.Version;

    public string RuntimeSource => _runtime?.Source ?? "未找到";

    public IReadOnlyList<ConversionFormat> InputFormats => SupportedInputFormats;

    public IReadOnlyList<ConversionFormat> OutputFormats => SupportedOutputs;

    public void RefreshRuntime()
    {
        _runtime = CalibreRuntimeLocator.Locate(_settings);
    }

    public bool CanConvert(string sourceExtension, string targetExtension)
    {
        if (!IsAvailable)
        {
            return false;
        }

        var source = NormalizeExtension(sourceExtension);
        var target = NormalizeExtension(targetExtension);
        return SupportedInputs.Contains(source) &&
               SupportedOutputs.Any(item => item.NormalizedExtension == target);
    }

    public async Task<ConversionResult> ConvertAsync(
        string sourcePath,
        string targetExtension,
        string outputDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetExtension);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        RefreshRuntime();
        if (_runtime is null || !_runtime.Exists)
        {
            return new ConversionResult(
                false,
                null,
                CalibreRuntimeLocator.BuildMissingRuntimeMessage(),
                -1,
                string.Empty,
                string.Empty);
        }

        if (!File.Exists(sourcePath))
        {
            return new ConversionResult(false, null, "源文件不存在。", -1, string.Empty, string.Empty);
        }

        var sourceExtension = Path.GetExtension(sourcePath);
        var target = NormalizeExtension(targetExtension);
        if (!CanConvert(sourceExtension, target))
        {
            return new ConversionResult(
                false,
                null,
                $"不支持从 {sourceExtension.ToUpperInvariant()} 转换到 {target.ToUpperInvariant()}。",
                -1,
                string.Empty,
                string.Empty);
        }

        Directory.CreateDirectory(outputDirectory);
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var outputPath = Path.Combine(outputDirectory, baseName + target);
        outputPath = EnsureUniquePath(outputPath);

        progress?.Report("正在启动 Calibre…");
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _runtime.EbookConvertPath,
                    ArgumentList = { sourcePath, outputPath },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = Path.GetDirectoryName(_runtime.EbookConvertPath) ?? AppContext.BaseDirectory
                },
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(args.Data))
                {
                    return;
                }

                stdout.AppendLine(args.Data);
                progress?.Report(TrimProgress(args.Data));
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(args.Data))
                {
                    return;
                }

                stderr.AppendLine(args.Data);
                progress?.Report(TrimProgress(args.Data));
            };

            if (!process.Start())
            {
                return new ConversionResult(
                    false,
                    null,
                    "无法启动 ebook-convert 进程。",
                    -1,
                    stdout.ToString(),
                    stderr.ToString());
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Best-effort process cleanup.
                }

                TryDelete(outputPath);
                throw;
            }

            var succeeded = process.ExitCode == 0 &&
                            File.Exists(outputPath) &&
                            new FileInfo(outputPath).Length > 0;
            if (!succeeded)
            {
                TryDelete(outputPath);
                var detail = FirstUsefulLine(stderr.ToString()) ??
                             FirstUsefulLine(stdout.ToString()) ??
                             $"ebook-convert 退出码 {process.ExitCode}";
                return new ConversionResult(
                    false,
                    null,
                    detail,
                    process.ExitCode,
                    stdout.ToString(),
                    stderr.ToString());
            }

            progress?.Report("转换完成");
            return new ConversionResult(
                true,
                outputPath,
                null,
                process.ExitCode,
                stdout.ToString(),
                stderr.ToString());
        }
        catch (OperationCanceledException)
        {
            TryDelete(outputPath);
            throw;
        }
        catch (Exception exception)
        {
            TryDelete(outputPath);
            return new ConversionResult(
                false,
                null,
                exception.Message,
                -1,
                stdout.ToString(),
                stderr.ToString());
        }
    }

    public static bool IsKindleExtension(string extension)
    {
        extension = NormalizeExtension(extension);
        return extension is ".mobi" or ".azw" or ".azw3" or ".azw4";
    }

    public static bool IsConvertibleInput(string extension) =>
        SupportedInputs.Contains(NormalizeExtension(extension));

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        return extension.StartsWith(".", StringComparison.Ordinal)
            ? extension.ToLowerInvariant()
            : "." + extension.ToLowerInvariant();
    }

    private static string EnsureUniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 2; index < 10_000; index++)
        {
            var candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{name}-{Guid.NewGuid():N}{extension}");
    }

    private static string TrimProgress(string value)
    {
        value = value.Trim();
        return value.Length <= 160 ? value : value[..157] + "…";
    }

    private static string? FirstUsefulLine(string text)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed.Length <= 300 ? trimmed : trimmed[..297] + "…";
            }
        }

        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
