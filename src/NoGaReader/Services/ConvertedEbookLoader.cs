using NoGaReader.Models;

namespace NoGaReader.Services;

internal sealed class ConvertedEbookLoader
{
    private const string CacheCategory = "converted";
    private const string CacheVersion = "converted-epub-v1";
    private readonly CalibreConverter _converter;
    private readonly EpubLoader _epubLoader = new();

    public ConvertedEbookLoader(CalibreConverter converter)
    {
        _converter = converter;
    }

    public ReaderSession Load(string sourcePath, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(sourcePath);
        if (!CalibreConverter.IsKindleExtension(extension))
        {
            throw new NotSupportedException($"不是可通过转换打开的 Kindle 格式：{extension}");
        }

        if (!_converter.IsAvailable)
        {
            throw new InvalidOperationException(CalibreRuntimeLocator.BuildMissingRuntimeMessage());
        }

        var cacheDirectory = AppPaths.GetDocumentCacheDirectory(sourcePath, CacheCategory);
        Directory.CreateDirectory(cacheDirectory);
        var outputPath = Path.Combine(cacheDirectory, "book.epub");
        var markerPath = Path.Combine(cacheDirectory, ".complete");
        var marker = $"{CacheVersion}|{CreateIdentity(sourcePath)}";

        var cacheReady = File.Exists(outputPath) &&
                         new FileInfo(outputPath).Length > 0 &&
                         File.Exists(markerPath) &&
                         string.Equals(File.ReadAllText(markerPath).Trim(), marker, StringComparison.Ordinal);

        if (!cacheReady)
        {
            ResetDirectory(cacheDirectory);
            Directory.CreateDirectory(cacheDirectory);
            cancellationToken.ThrowIfCancellationRequested();

            var result = _converter.ConvertAsync(
                    sourcePath,
                    ".epub",
                    cacheDirectory,
                    progress: null,
                    cancellationToken)
                .GetAwaiter()
                .GetResult();

            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.OutputPath) || !File.Exists(result.OutputPath))
            {
                ResetDirectory(cacheDirectory);
                throw new InvalidOperationException(
                    result.ErrorMessage ?? "MOBI/AZW 转换失败，无法打开。");
            }

            if (!string.Equals(result.OutputPath, outputPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(result.OutputPath, outputPath, overwrite: true);
                TryDelete(result.OutputPath);
            }

            File.WriteAllText(markerPath, marker);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var session = _epubLoader.Load(outputPath, cancellationToken);
        return new ReaderSession
        {
            SourcePath = sourcePath,
            Title = string.IsNullOrWhiteSpace(session.Title)
                ? Path.GetFileNameWithoutExtension(sourcePath)
                : session.Title,
            Author = session.Author,
            CoverImagePath = session.CoverImagePath,
            RootDirectory = session.RootDirectory,
            Kind = ReaderDocumentKind.Epub,
            Sections = session.Sections,
            TableOfContents = session.TableOfContents,
            IsReflowable = session.IsReflowable,
            SupportsInPageSearch = session.SupportsInPageSearch,
            EnableScriptExecution = session.EnableScriptExecution,
            CurrentSectionIndex = session.CurrentSectionIndex
        };
    }

    private static string CreateIdentity(string sourcePath)
    {
        var file = new FileInfo(sourcePath);
        return $"{Path.GetFullPath(sourcePath)}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
    }

    private static void ResetDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, true);
        }
        catch
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                TryDelete(file);
            }
        }
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
            // Best-effort.
        }
    }
}
