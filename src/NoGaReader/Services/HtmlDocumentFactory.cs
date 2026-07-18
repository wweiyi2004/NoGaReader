using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using NoGaReader.Models;

namespace NoGaReader.Services;

internal static partial class HtmlDocumentFactory
{
    private const string TextCacheVersion = "text-cache-v2-chunked";
    private const string MarkdownCacheVersion = "markdown-cache-v1";
    private const int MaximumTextSectionCharacters = 512 * 1024;
    private const long MaximumTextBytes = 64L * 1024 * 1024;
    private const long MaximumMarkdownImageBytes = 32L * 1024 * 1024;
    private const long MaximumMarkdownImagesBytes = 128L * 1024 * 1024;

    private static readonly HashSet<string> MarkdownImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".svg"
    };

    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public static ReaderSession CreateTextDocument(string sourcePath)
    {
        EnsureTextSize(sourcePath);
        var cacheDirectory = AppPaths.GetDocumentCacheDirectory(sourcePath, "text");
        var title = Path.GetFileNameWithoutExtension(sourcePath);
        var markerPath = Path.Combine(cacheDirectory, ".complete");
        if (TryCreateCachedTextSession(sourcePath, title, cacheDirectory, markerPath) is { } cachedSession)
        {
            return cachedSession;
        }

        ResetCache(cacheDirectory);
        var text = File.ReadAllText(sourcePath);
        var sections = WriteTextSections(title, text, cacheDirectory);
        WriteMarker(markerPath, TextCacheVersion);
        return CreateTextSession(sourcePath, title, cacheDirectory, sections);
    }

    public static ReaderSession CreateMarkdownDocument(string sourcePath)
    {
        EnsureTextSize(sourcePath);
        var cacheDirectory = AppPaths.GetDocumentCacheDirectory(sourcePath, "markdown");
        var outputPath = Path.Combine(cacheDirectory, "index.html");
        var title = Path.GetFileNameWithoutExtension(sourcePath);
        var markerPath = Path.Combine(cacheDirectory, ".complete");
        if (IsCacheReady(markerPath, MarkdownCacheVersion, outputPath))
        {
            return CreateGeneratedSession(sourcePath, title, cacheDirectory, outputPath, ReaderDocumentKind.Markdown);
        }

        ResetCache(cacheDirectory);
        var assetsDirectory = Path.Combine(cacheDirectory, "assets");
        Directory.CreateDirectory(assetsDirectory);

        var markdown = File.ReadAllText(sourcePath);
        var rendered = Markdig.Markdown.ToHtml(markdown, MarkdownPipeline);
        var copiedImageBytes = 0L;
        rendered = ImageSourceRegex().Replace(rendered, match =>
            RewriteMarkdownImage(match, sourcePath, assetsDirectory, ref copiedImageBytes));
        rendered = DangerousLinkRegex().Replace(rendered, "$1#blocked$2");

        File.WriteAllText(outputPath, BuildReadingDocument(title, rendered, "markdown"), Encoding.UTF8);
        WriteMarker(markerPath, MarkdownCacheVersion);
        return CreateGeneratedSession(sourcePath, title, cacheDirectory, outputPath, ReaderDocumentKind.Markdown);
    }

    private static string RewriteMarkdownImage(
        Match match,
        string sourcePath,
        string assetsDirectory,
        ref long copiedImageBytes)
    {
        var originalValue = WebUtility.HtmlDecode(match.Groups[2].Value).Trim();
        if (originalValue.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return match.Value;
        }

        if (Uri.TryCreate(originalValue, UriKind.Absolute, out _))
        {
            return $"{match.Groups[1].Value}#blocked{match.Groups[3].Value}";
        }

        var localPart = originalValue.Split(['?', '#'], 2)[0];
        try
        {
            localPart = Uri.UnescapeDataString(localPart).Replace('/', Path.DirectorySeparatorChar);
            var sourceDirectory = Path.GetDirectoryName(sourcePath)!;
            var normalizedRoot = Path.GetFullPath(sourceDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var imagePath = Path.GetFullPath(Path.Combine(sourceDirectory, localPart));
            var extension = Path.GetExtension(imagePath);
            if (!imagePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                !MarkdownImageExtensions.Contains(extension) ||
                !File.Exists(imagePath))
            {
                return $"{match.Groups[1].Value}#missing-image{match.Groups[3].Value}";
            }

            var file = new FileInfo(imagePath);
            if (file.Length > MaximumMarkdownImageBytes ||
                copiedImageBytes + file.Length > MaximumMarkdownImagesBytes)
            {
                return $"{match.Groups[1].Value}#image-too-large{match.Groups[3].Value}";
            }

            using var stream = File.OpenRead(imagePath);
            var hash = Convert.ToHexString(SHA256.HashData(stream))[..16].ToLowerInvariant();
            var outputName = hash + extension.ToLowerInvariant();
            var outputPath = Path.Combine(assetsDirectory, outputName);
            if (!File.Exists(outputPath))
            {
                File.Copy(imagePath, outputPath);
            }

            copiedImageBytes += file.Length;
            return $"{match.Groups[1].Value}assets/{outputName}{match.Groups[3].Value}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"{match.Groups[1].Value}#missing-image{match.Groups[3].Value}";
        }
    }

    private static ReaderSession CreateGeneratedSession(
        string sourcePath,
        string title,
        string cacheDirectory,
        string outputPath,
        ReaderDocumentKind kind)
    {
        return new ReaderSession
        {
            SourcePath = sourcePath,
            Title = title,
            RootDirectory = cacheDirectory,
            Kind = kind,
            Sections = [new ReaderSection("正文", outputPath)],
            IsReflowable = true,
            SupportsInPageSearch = true,
            EnableScriptExecution = true
        };
    }

    private static ReaderSession? TryCreateCachedTextSession(
        string sourcePath,
        string title,
        string cacheDirectory,
        string markerPath)
    {
        try
        {
            if (!File.Exists(markerPath) ||
                !string.Equals(File.ReadAllText(markerPath).Trim(), TextCacheVersion, StringComparison.Ordinal) ||
                !Directory.Exists(cacheDirectory))
            {
                return null;
            }

            var paths = Directory.EnumerateFiles(cacheDirectory, "*.html", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return paths.Count == 0
                ? null
                : CreateTextSession(sourcePath, title, cacheDirectory, CreateTextSections(paths));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static IReadOnlyList<ReaderSection> WriteTextSections(
        string title,
        string text,
        string cacheDirectory)
    {
        var ranges = new List<(int Start, int Length)>();
        var start = 0;
        do
        {
            var end = Math.Min(text.Length, start + MaximumTextSectionCharacters);
            if (end < text.Length)
            {
                var minimumBreak = start + MaximumTextSectionCharacters / 2;
                var newline = text.LastIndexOf('\n', end - 1, end - minimumBreak);
                if (newline >= minimumBreak)
                {
                    end = newline + 1;
                }

                if (end > start && char.IsHighSurrogate(text[end - 1]))
                {
                    end--;
                }
            }

            if (end <= start)
            {
                end = Math.Min(text.Length, start + MaximumTextSectionCharacters);
            }

            ranges.Add((start, end - start));
            start = end;
        } while (start < text.Length);

        var paths = new List<string>(ranges.Count);
        for (var index = 0; index < ranges.Count; index++)
        {
            var range = ranges[index];
            var sectionTitle = ranges.Count == 1 ? "正文" : $"正文 · {index + 1}";
            var outputPath = Path.Combine(cacheDirectory, $"{index + 1:D5}.html");
            var body = $"<pre class=\"plain-text\">{WebUtility.HtmlEncode(text.AsSpan(range.Start, range.Length).ToString())}</pre>";
            File.WriteAllText(
                outputPath,
                BuildReadingDocument($"{title} — {sectionTitle}", body, "plain"),
                Encoding.UTF8);
            paths.Add(outputPath);
        }

        return CreateTextSections(paths);
    }

    private static IReadOnlyList<ReaderSection> CreateTextSections(IReadOnlyList<string> paths)
    {
        return paths.Select((path, index) => new ReaderSection(
                paths.Count == 1 ? "正文" : $"正文 · {index + 1}",
                path))
            .ToList();
    }

    private static ReaderSession CreateTextSession(
        string sourcePath,
        string title,
        string cacheDirectory,
        IReadOnlyList<ReaderSection> sections)
    {
        return new ReaderSession
        {
            SourcePath = sourcePath,
            Title = title,
            RootDirectory = cacheDirectory,
            Kind = ReaderDocumentKind.Text,
            Sections = sections,
            TableOfContents = sections.Select(section => new TocNode
            {
                Title = section.Title,
                FullPath = section.FullPath
            }).ToList(),
            IsReflowable = true,
            SupportsInPageSearch = true,
            EnableScriptExecution = true
        };
    }

    private static string BuildReadingDocument(string title, string body, string documentClass)
    {
        return $$"""
                 <!doctype html>
                 <html lang="zh-CN">
                 <head>
                   <meta charset="utf-8">
                   <meta name="viewport" content="width=device-width,initial-scale=1">
                   <meta name="color-scheme" content="dark light">
                   <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src 'self' data:; style-src 'unsafe-inline'">
                   <title>{{WebUtility.HtmlEncode(title)}}</title>
                   <style>
                     *, *::before, *::after { box-sizing: border-box; }
                     html { -webkit-text-size-adjust: 100%; }
                     body { margin: 0 auto; padding: 3.25rem 3.5rem 7rem; max-width: 48rem;
                            color: #332f2a; background: #fbf7ed;
                            font-family: "Microsoft YaHei UI", "PingFang SC", sans-serif;
                            font-size: 18px; line-height: 1.9; overflow-wrap: anywhere; }
                     h1, h2, h3, h4, h5, h6 { color: inherit; line-height: 1.35; font-weight: 650;
                                               text-wrap: balance; }
                     h1 { margin: .25em 0 1.5em; font-size: 2em; }
                     h2 { margin: 2.1em 0 .85em; padding-bottom: .35em; font-size: 1.5em;
                          border-bottom: 1px solid rgba(100, 91, 78, .18); }
                     h3 { margin: 1.75em 0 .7em; font-size: 1.22em; }
                     p, ul, ol, blockquote, pre, table { margin: 1em 0; }
                     ul, ol { padding-left: 1.6em; }
                     li + li { margin-top: .3em; }
                     a { color: #4d5fc1; text-decoration-thickness: .08em; text-underline-offset: .16em; }
                     blockquote { margin-left: 0; padding: .15em 0 .15em 1.15em;
                                  color: #6d6458; border-left: 3px solid #c6bda9; }
                     code { padding: .12em .34em; border-radius: .3em; background: rgba(87, 79, 68, .09);
                            font: .88em/1.55 "Cascadia Code", Consolas, monospace; }
                     pre { overflow-x: auto; padding: 1.05em 1.2em; border: 1px solid rgba(87, 79, 68, .12);
                           border-radius: .65em; background: rgba(87, 79, 68, .06); }
                     pre code { padding: 0; background: transparent; font-size: .88em; }
                     .plain-text { margin: 0; border: 0; background: transparent;
                                   font: inherit; white-space: pre-wrap; }
                     table { display: block; max-width: 100%; overflow-x: auto; border-collapse: collapse; }
                     th, td { padding: .55em .75em; border: 1px solid rgba(87, 79, 68, .2); text-align: left; }
                     th { background: rgba(87, 79, 68, .06); font-weight: 650; }
                     img { display: block; max-width: 100%; height: auto; margin: 1.4em auto; border-radius: .35em; }
                     hr { margin: 2.5em 0; border: 0; border-top: 1px solid rgba(87, 79, 68, .2); }
                     input[type=checkbox] { width: 1em; height: 1em; margin-right: .45em; }
                   </style>
                 </head>
                 <body class="nogareader-source nogareader-{{documentClass}}">{{body}}</body>
                 </html>
                 """;
    }

    private static void EnsureTextSize(string sourcePath)
    {
        if (new FileInfo(sourcePath).Length > MaximumTextBytes)
        {
            throw new InvalidDataException("文本文件超过 64 MB，当前版本暂不直接加载。");
        }
    }

    private static bool IsCacheReady(string markerPath, string version, string outputPath)
    {
        try
        {
            return File.Exists(outputPath) &&
                   File.Exists(markerPath) &&
                   string.Equals(File.ReadAllText(markerPath).Trim(), version, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void WriteMarker(string markerPath, string version)
    {
        var temporaryPath = markerPath + ".tmp";
        File.WriteAllText(temporaryPath, version, Encoding.UTF8);
        File.Move(temporaryPath, markerPath, true);
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

    [GeneratedRegex("(<img\\b[^>]*?\\bsrc\\s*=\\s*[\\\"'])([^\\\"']*)([\\\"'])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageSourceRegex();

    [GeneratedRegex("(href\\s*=\\s*[\\\"'])\\s*(?:javascript|vbscript|file)\\s*:[^\\\"']*([\\\"'])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DangerousLinkRegex();
}
