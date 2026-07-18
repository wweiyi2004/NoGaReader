using System.Diagnostics;
using System.Text;
using NoGaReader.Models;

namespace NoGaReader.Services;

internal sealed class DjvuLoader
{
    private const string CacheCategory = "djvu";
    private const string CacheVersion = "djvu-v1";
    private readonly CalibreConverter _converter;
    public DjvuLoader(CalibreConverter converter)
    {
        _converter = converter;
    }

    public ReaderSession Load(string sourcePath, CancellationToken cancellationToken)
    {
        var cacheDirectory = AppPaths.GetDocumentCacheDirectory(sourcePath, CacheCategory);
        var completeMarker = Path.Combine(cacheDirectory, ".complete");
        var pdfPath = Path.Combine(cacheDirectory, "book.pdf");
        var identity = CreateIdentity(sourcePath);
        var marker = $"{CacheVersion}|{identity}";

        var ready = File.Exists(pdfPath) &&
                    new FileInfo(pdfPath).Length > 0 &&
                    File.Exists(completeMarker) &&
                    string.Equals(File.ReadAllText(completeMarker).Trim(), marker, StringComparison.Ordinal);

        if (!ready)
        {
            Reset(cacheDirectory);
            Directory.CreateDirectory(cacheDirectory);
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryConvertWithCalibre(sourcePath, cacheDirectory, pdfPath, cancellationToken) &&
                !TryConvertWithDdjvu(sourcePath, pdfPath, cancellationToken))
            {
                throw new InvalidOperationException(
                    "无法打开 DJVU。\n\n请安装 Calibre（ebook-convert）或 DjVuLibre（ddjvu），" +
                    "也可在转换窗口中指定引擎后重试。");
            }

            if (!File.Exists(pdfPath) || new FileInfo(pdfPath).Length == 0)
            {
                throw new InvalidDataException("DJVU 转换未生成有效 PDF。");
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

    private bool TryConvertWithCalibre(
        string sourcePath,
        string cacheDirectory,
        string pdfPath,
        CancellationToken cancellationToken)
    {
        if (!_converter.IsAvailable)
        {
            return false;
        }

        var result = _converter.ConvertAsync(sourcePath, ".pdf", cacheDirectory, null, cancellationToken)
            .GetAwaiter().GetResult();
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.OutputPath) || !File.Exists(result.OutputPath))
        {
            return false;
        }

        if (!string.Equals(result.OutputPath, pdfPath, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(result.OutputPath, pdfPath, overwrite: true);
            try { File.Delete(result.OutputPath); } catch { /* ignore */ }
        }

        return true;
    }

    private static bool TryConvertWithDdjvu(string sourcePath, string pdfPath, CancellationToken cancellationToken)
    {
        var ddjvu = ResolveDdjvu();
        if (ddjvu is null)
        {
            return false;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ddjvu,
                ArgumentList = { "-format=pdf", sourcePath, pdfPath },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }
        };
        process.Start();
        process.WaitForExit(180_000);
        cancellationToken.ThrowIfCancellationRequested();
        return process.ExitCode == 0 && File.Exists(pdfPath) && new FileInfo(pdfPath).Length > 0;
    }

    private static string? ResolveDdjvu()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return null;
        }

        foreach (var segment in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(segment.Trim().Trim('"'), "ddjvu.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
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
            try { Directory.Delete(directory, true); } catch { /* best effort */ }
        }

        Directory.CreateDirectory(directory);
    }
}
