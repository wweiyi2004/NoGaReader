using System.Net;
using System.Text;
using System.Xml.Linq;
using NoGaReader.Models;

namespace NoGaReader.Services;

internal sealed class Fb2Loader
{
    private const string CacheVersion = "fb2-cache-v1";
    private const long MaximumFileBytes = 128L * 1024 * 1024;

    public ReaderSession Load(string sourcePath, CancellationToken cancellationToken)
    {
        if (new FileInfo(sourcePath).Length > MaximumFileBytes)
        {
            throw new InvalidDataException("FB2 文件超过 128 MB，当前版本暂不直接加载。");
        }

        var cacheDirectory = AppPaths.GetDocumentCacheDirectory(sourcePath, "fb2");
        var completeMarker = Path.Combine(cacheDirectory, ".complete");
        if (TryLoadCachedSession(sourcePath, cacheDirectory, completeMarker) is { } cachedSession)
        {
            return cachedSession;
        }

        ResetCache(cacheDirectory);
        var document = SafeXml.Load(sourcePath);
        var title = document.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "book-title")?
            .Value
            .Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = Path.GetFileNameWithoutExtension(sourcePath);
        }

        var images = ReadImages(document);
        var topLevelSections = document.Descendants()
            .Where(element => element.Name.LocalName == "body")
            .SelectMany(body => body.Elements().Where(element => element.Name.LocalName == "section"))
            .ToList();

        if (topLevelSections.Count == 0)
        {
            var body = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "body");
            if (body is not null)
            {
                topLevelSections.Add(body);
            }
        }

        if (topLevelSections.Count == 0)
        {
            throw new InvalidDataException("FB2 中没有可阅读的正文。");
        }

        var sections = new List<ReaderSection>(topLevelSections.Count);

        for (var index = 0; index < topLevelSections.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var section = topLevelSections[index];
            var sectionTitle = ExtractSectionTitle(section) ?? $"章节 {index + 1}";
            var outputPath = Path.Combine(cacheDirectory, $"{index + 1:D5}.html");
            var bodyBuilder = new StringBuilder();
            foreach (var node in section.Nodes())
            {
                RenderNode(node, bodyBuilder, images);
            }

            File.WriteAllText(
                outputPath,
                BuildDocumentHtml(title, sectionTitle, bodyBuilder.ToString()),
                Encoding.UTF8);
            sections.Add(new ReaderSection(sectionTitle, outputPath));
        }

        JsonFileStore.Save(completeMarker, new CacheManifest
        {
            Version = CacheVersion,
            Title = title,
            Sections = sections.Select(section => new CachedSection
            {
                Title = section.Title,
                FileName = Path.GetFileName(section.FullPath)
            }).ToList()
        });
        return CreateSession(sourcePath, title, cacheDirectory, sections);
    }

    private static ReaderSession? TryLoadCachedSession(
        string sourcePath,
        string cacheDirectory,
        string completeMarker)
    {
        var manifest = JsonFileStore.Load<CacheManifest?>(completeMarker, null);
        if (manifest is null ||
            !string.Equals(manifest.Version, CacheVersion, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(manifest.Title) ||
            manifest.Sections.Count == 0)
        {
            return null;
        }

        var sections = new List<ReaderSection>(manifest.Sections.Count);
        foreach (var section in manifest.Sections)
        {
            if (string.IsNullOrWhiteSpace(section.Title) ||
                string.IsNullOrWhiteSpace(section.FileName) ||
                Path.GetFileName(section.FileName) != section.FileName)
            {
                return null;
            }

            var path = Path.Combine(cacheDirectory, section.FileName);
            if (!File.Exists(path))
            {
                return null;
            }

            sections.Add(new ReaderSection(section.Title, path));
        }

        return CreateSession(sourcePath, manifest.Title, cacheDirectory, sections);
    }

    private static ReaderSession CreateSession(
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
            Kind = ReaderDocumentKind.FictionBook,
            Sections = sections,
            IsReflowable = true,
            SupportsInPageSearch = true,
            EnableScriptExecution = true
        };
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

    private static Dictionary<string, string> ReadImages(XDocument document)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var binary in document.Descendants().Where(element => element.Name.LocalName == "binary"))
        {
            var id = binary.Attribute("id")?.Value;
            var mediaType = binary.Attribute("content-type")?.Value?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(id) || mediaType is not ("image/jpeg" or "image/png" or "image/gif" or "image/webp"))
            {
                continue;
            }

            var value = string.Concat(binary.Value.Where(character => !char.IsWhiteSpace(character)));
            try
            {
                _ = Convert.FromBase64String(value);
                result[id] = $"data:{mediaType};base64,{value}";
            }
            catch (FormatException)
            {
                // Ignore malformed embedded images while keeping the text readable.
            }
        }

        return result;
    }

    private static string? ExtractSectionTitle(XElement section)
    {
        var title = section.Elements().FirstOrDefault(element => element.Name.LocalName == "title");
        if (title is null)
        {
            return null;
        }

        var text = string.Join(" ", title.DescendantsAndSelf()
            .Where(element => element.Name.LocalName is "p" or "title")
            .Select(element => element.Value.Trim())
            .Where(value => value.Length > 0));
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static void RenderNode(XNode node, StringBuilder output, IReadOnlyDictionary<string, string> images)
    {
        if (node is XText text)
        {
            output.Append(WebUtility.HtmlEncode(text.Value));
            return;
        }

        if (node is not XElement element)
        {
            return;
        }

        var name = element.Name.LocalName;
        switch (name)
        {
            case "title":
                Wrap("h2", element, output, images);
                break;
            case "subtitle":
                Wrap("h3", element, output, images);
                break;
            case "p":
                Wrap("p", element, output, images);
                break;
            case "strong":
                Wrap("strong", element, output, images);
                break;
            case "emphasis":
                Wrap("em", element, output, images);
                break;
            case "strikethrough":
                Wrap("s", element, output, images);
                break;
            case "sub":
                Wrap("sub", element, output, images);
                break;
            case "sup":
                Wrap("sup", element, output, images);
                break;
            case "poem":
            case "stanza":
            case "cite":
                Wrap("blockquote", element, output, images);
                break;
            case "v":
                Wrap("p", element, output, images, "verse");
                break;
            case "empty-line":
                output.Append("<br>");
                break;
            case "image":
                {
                    var reference = element.Attributes()
                        .FirstOrDefault(attribute => attribute.Name.LocalName == "href")?
                        .Value
                        .TrimStart('#');
                    if (reference is not null && images.TryGetValue(reference, out var source))
                    {
                        output.Append("<figure><img alt=\"\" src=\"")
                            .Append(WebUtility.HtmlEncode(source))
                            .Append("\"></figure>");
                    }
                    break;
                }
            default:
                foreach (var child in element.Nodes())
                {
                    RenderNode(child, output, images);
                }
                break;
        }
    }

    private static void Wrap(
        string tag,
        XElement element,
        StringBuilder output,
        IReadOnlyDictionary<string, string> images,
        string? cssClass = null)
    {
        output.Append('<').Append(tag);
        if (cssClass is not null)
        {
            output.Append(" class=\"").Append(cssClass).Append('"');
        }
        output.Append('>');
        foreach (var child in element.Nodes())
        {
            RenderNode(child, output, images);
        }
        output.Append("</").Append(tag).Append('>');
    }

    private static string BuildDocumentHtml(string bookTitle, string sectionTitle, string body)
    {
        return $$"""
                 <!doctype html>
                 <html lang="zh-CN">
                 <head>
                   <meta charset="utf-8">
                   <meta name="color-scheme" content="dark light">
                   <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'">
                   <title>{{WebUtility.HtmlEncode(bookTitle)}} — {{WebUtility.HtmlEncode(sectionTitle)}}</title>
                   <style>
                     body { max-width: 820px; margin: 0 auto; padding: 48px 60px 100px;
                            font: 18px/1.85 Georgia, "Microsoft YaHei UI", serif;
                            color: #172033; background: #fff; }
                     h2 { margin: 1.4em 0 .8em; line-height: 1.35; }
                     h3 { margin: 1.2em 0 .7em; }
                     p { margin: .7em 0; text-align: justify; }
                     blockquote { margin: 1em 2em; color: #526079; }
                     .verse { margin: .15em 0; text-align: left; }
                     figure { margin: 1.5em auto; text-align: center; }
                     img { max-width: 100%; height: auto; }
                     @media (prefers-color-scheme: dark) {
                       body { color: #e5e7eb; background: #111827; }
                       blockquote { color: #a9b5c8; }
                     }
                   </style>
                 </head>
                 <body>{{body}}</body>
                 </html>
                 """;
    }

    private sealed class CacheManifest
    {
        public string Version { get; init; } = string.Empty;

        public string Title { get; init; } = string.Empty;

        public List<CachedSection> Sections { get; init; } = [];
    }

    private sealed class CachedSection
    {
        public string Title { get; init; } = string.Empty;

        public string FileName { get; init; } = string.Empty;
    }
}
