using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using NoGaReader.Models;

namespace NoGaReader.Services;

internal sealed class EpubLoader
{
    private const string CacheVersion = "epub-cache-v3";
    private const int MaximumNavigationDepth = 64;
    private const int MaximumNavigationNodes = 10_000;

    public ReaderSession Load(string sourcePath, CancellationToken cancellationToken)
    {
        var cacheDirectory = AppPaths.GetDocumentCacheDirectory(sourcePath, "epub");
        var completeMarker = Path.Combine(cacheDirectory, ".complete");
        var markerValue = TryReadMarker(completeMarker);
        var cacheReady = markerValue is not null &&
                         markerValue.StartsWith(CacheVersion + "|", StringComparison.Ordinal);

        if (!cacheReady)
        {
            ResetCache(cacheDirectory);
            try
            {
                using var archive = ZipFile.OpenRead(sourcePath);
                SafeArchiveExtractor.ExtractAll(archive, cacheDirectory, cancellationToken);
            }
            catch
            {
                ResetCache(cacheDirectory);
                throw;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var containerPath = CombineInside(cacheDirectory, "META-INF", "container.xml");
        if (!File.Exists(containerPath))
        {
            throw new InvalidDataException("EPUB 缺少 META-INF/container.xml。");
        }

        var container = SafeXml.Load(containerPath);
        var packageRelativePath = container
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "rootfile")?
            .Attribute("full-path")?
            .Value;

        if (string.IsNullOrWhiteSpace(packageRelativePath))
        {
            throw new InvalidDataException("EPUB 没有声明内容包路径。");
        }

        var packagePath = CombineInside(cacheDirectory, DecodeHref(packageRelativePath));
        if (!File.Exists(packagePath))
        {
            throw new InvalidDataException("EPUB 内容包文件不存在。");
        }

        var package = SafeXml.Load(packagePath);
        var packageDirectory = Path.GetDirectoryName(packagePath)!;
        var title = package.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "title")?
            .Value
            .Trim();

        if (string.IsNullOrWhiteSpace(title))
        {
            title = Path.GetFileNameWithoutExtension(sourcePath);
        }

        var author = ReadAuthor(package);

        var manifest = package.Descendants()
            .Where(element => element.Name.LocalName == "item")
            .Select(element => new ManifestItem(
                element.Attribute("id")?.Value ?? string.Empty,
                element.Attribute("href")?.Value ?? string.Empty,
                element.Attribute("media-type")?.Value ?? string.Empty,
                element.Attribute("properties")?.Value ?? string.Empty))
            .Where(item => item.Id.Length > 0 && item.Href.Length > 0)
            .ToDictionary(item => item.Id, StringComparer.Ordinal);

        var coverImagePath = ResolveCoverImagePath(
            package,
            manifest,
            packageDirectory,
            cacheDirectory);
        var navigation = ReadNavigation(
            manifest.Values,
            packageDirectory,
            cacheDirectory);

        var sections = new List<ReaderSection>();
        foreach (var itemReference in package.Descendants().Where(element => element.Name.LocalName == "itemref"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var idReference = itemReference.Attribute("idref")?.Value;
            if (string.IsNullOrEmpty(idReference) || !manifest.TryGetValue(idReference, out var item))
            {
                continue;
            }

            var contentPath = ResolveHref(packageDirectory, item.Href, cacheDirectory);
            if (contentPath is null || !File.Exists(contentPath))
            {
                continue;
            }

            var sectionTitle = navigation.Titles.GetValueOrDefault(contentPath) ??
                               $"章节 {sections.Count + 1}";
            sections.Add(new ReaderSection(sectionTitle, contentPath));
        }

        if (sections.Count == 0)
        {
            sections.AddRange(manifest.Values
                .Where(item => item.MediaType is "application/xhtml+xml" or "text/html")
                .Select(item => ResolveHref(packageDirectory, item.Href, cacheDirectory))
                .Where(path => path is not null && File.Exists(path))
                .Select((path, index) => new ReaderSection($"章节 {index + 1}", path!)));
        }

        if (sections.Count == 0)
        {
            throw new InvalidDataException("EPUB 中没有可阅读的章节。");
        }

        var tableOfContents = navigation.TableOfContents.Count > 0
            ? navigation.TableOfContents
            : sections.Select(section => new TocNode
            {
                Title = section.Title,
                FullPath = section.FullPath
            }).ToList();

        var sanitized = string.Equals(markerValue, CacheVersion + "|safe", StringComparison.Ordinal);
        if (!cacheReady)
        {
            sanitized = true;
            foreach (var contentPath in sections.Select(section => section.FullPath)
                         .Concat(tableOfContents.SelectMany(node => node.Flatten()).Select(node => node.FullPath))
                         .Concat(Directory.EnumerateFiles(cacheDirectory, "*", SearchOption.AllDirectories)
                             .Where(IsActiveContentDocument))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                sanitized &= TrySanitizeContentDocument(contentPath);
            }

            WriteMarker(completeMarker, CacheVersion + (sanitized ? "|safe" : "|unsafe"));
        }

        return new ReaderSession
        {
            SourcePath = sourcePath,
            Title = title,
            RootDirectory = cacheDirectory,
            Kind = ReaderDocumentKind.Epub,
            Sections = sections,
            TableOfContents = tableOfContents,
            Author = author,
            CoverImagePath = coverImagePath,
            IsReflowable = true,
            SupportsInPageSearch = sanitized,
            EnableScriptExecution = sanitized
        };
    }

    private static bool TrySanitizeContentDocument(string path)
    {
        try
        {
            var document = SafeXml.LoadContent(path);
            var blockedElements = document.Descendants()
                .Where(element => IsBlockedElement(element.Name.LocalName))
                .ToList();
            foreach (var element in blockedElements)
            {
                element.Remove();
            }

            var refreshElements = document.Descendants()
                .Where(element => string.Equals(element.Name.LocalName, "meta", StringComparison.OrdinalIgnoreCase) &&
                                  string.Equals(
                                      element.Attributes().FirstOrDefault(attribute => string.Equals(
                                          attribute.Name.LocalName,
                                          "http-equiv",
                                          StringComparison.OrdinalIgnoreCase))?.Value,
                                      "refresh",
                                      StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var element in refreshElements)
            {
                element.Remove();
            }

            foreach (var element in document.Root?.DescendantsAndSelf() ?? [])
            {
                var blockedAttributes = element.Attributes()
                    .Where(attribute =>
                        attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
                        IsBlockedAttribute(attribute.Name.LocalName) ||
                        (IsReferenceAttribute(attribute.Name.LocalName) && IsUnsafeReference(attribute)))
                    .ToList();
                foreach (var attribute in blockedAttributes)
                {
                    attribute.Remove();
                }
            }

            document.Save(path, SaveOptions.DisableFormatting);
            return true;
        }
        catch (Exception exception) when (exception is XmlException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsBlockedElement(string localName) =>
        localName.Equals("script", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("iframe", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("object", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("embed", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("base", StringComparison.OrdinalIgnoreCase);

    private static bool IsBlockedAttribute(string localName) =>
        localName.Equals("srcdoc", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("action", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("formaction", StringComparison.OrdinalIgnoreCase);

    private static bool IsReferenceAttribute(string localName) =>
        localName.Equals("href", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("src", StringComparison.OrdinalIgnoreCase);

    private static bool IsActiveContentDocument(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".html" or ".htm" or ".xhtml" or ".svg";

    private static bool IsUnsafeReference(XAttribute attribute)
    {
        var value = attribute.Value.Trim();
        if (value.Length == 0 || value.StartsWith('#'))
        {
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (attribute.Name.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase))
        {
            return uri.Scheme is not ("http" or "https" or "mailto");
        }

        return uri.Scheme != "data" ||
               !value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadAuthor(XDocument package)
    {
        var authors = package.Descendants()
            .Where(element => element.Name.LocalName == "creator")
            .Select(element => NormalizeTitle(element.Value))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return authors.Count > 0 ? string.Join("; ", authors) : null;
    }

    private static string? ResolveCoverImagePath(
        XDocument package,
        IReadOnlyDictionary<string, ManifestItem> manifest,
        string packageDirectory,
        string cacheDirectory)
    {
        var coverItem = manifest.Values.FirstOrDefault(item => HasProperty(item, "cover-image"));
        if (coverItem is null)
        {
            var coverId = package.Descendants()
                .Where(element => element.Name.LocalName == "meta")
                .FirstOrDefault(element => string.Equals(
                    element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "name")?.Value,
                    "cover",
                    StringComparison.OrdinalIgnoreCase))?
                .Attributes()
                .FirstOrDefault(attribute => attribute.Name.LocalName == "content")?
                .Value;
            if (!string.IsNullOrWhiteSpace(coverId))
            {
                manifest.TryGetValue(coverId.Trim(), out coverItem);
            }
        }

        if (coverItem is null ||
            !coverItem.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = ResolveHref(packageDirectory, coverItem.Href, cacheDirectory);
        return path is not null && File.Exists(path) ? path : null;
    }

    private static NavigationData ReadNavigation(
        IEnumerable<ManifestItem> manifest,
        string packageDirectory,
        string cacheDirectory)
    {
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var items = manifest.ToList();
        IReadOnlyList<TocNode> epub3Contents = [];
        IReadOnlyList<TocNode> ncxContents = [];
        var navigationItem = items.FirstOrDefault(item => HasProperty(item, "nav"));

        if (navigationItem is not null)
        {
            var navigationPath = ResolveHref(packageDirectory, navigationItem.Href, cacheDirectory);
            if (navigationPath is not null && File.Exists(navigationPath))
            {
                try
                {
                    var document = SafeXml.LoadContent(navigationPath);
                    var navigationElement = document.Descendants()
                        .Where(element => element.Name.LocalName == "nav")
                        .FirstOrDefault(IsTableOfContentsNavigation);
                    var rootList = navigationElement?.Descendants()
                        .FirstOrDefault(element => element.Name.LocalName == "ol");
                    if (rootList is not null)
                    {
                        epub3Contents = ReadHtmlNavigationList(
                            rootList,
                            navigationPath,
                            cacheDirectory,
                            new NavigationBudget(MaximumNavigationNodes),
                            0);
                        AddNavigationTitles(titles, epub3Contents);
                    }
                }
                catch (Exception exception) when (
                    exception is XmlException or IOException or UnauthorizedAccessException)
                {
                    // Invalid navigation markup should not prevent reading the spine.
                }
            }
        }

        var ncxItem = items.FirstOrDefault(item => item.MediaType == "application/x-dtbncx+xml");
        if (ncxItem is not null)
        {
            var ncxPath = ResolveHref(packageDirectory, ncxItem.Href, cacheDirectory);
            if (ncxPath is not null && File.Exists(ncxPath))
            {
                try
                {
                    var document = SafeXml.Load(ncxPath);
                    var navigationMap = document.Descendants()
                        .FirstOrDefault(element => element.Name.LocalName == "navMap");
                    if (navigationMap is not null)
                    {
                        ncxContents = ReadNcxNavigationPoints(
                            navigationMap,
                            ncxPath,
                            cacheDirectory,
                            new NavigationBudget(MaximumNavigationNodes),
                            0);
                        AddNavigationTitles(titles, ncxContents);
                    }
                }
                catch (Exception exception) when (
                    exception is XmlException or IOException or UnauthorizedAccessException)
                {
                    // The EPUB spine remains usable even when NCX is malformed.
                }
            }
        }

        return new NavigationData(
            titles,
            epub3Contents.Count > 0 ? epub3Contents : ncxContents);
    }

    private static IReadOnlyList<TocNode> ReadHtmlNavigationList(
        XElement list,
        string navigationPath,
        string cacheDirectory,
        NavigationBudget budget,
        int depth)
    {
        var result = new List<TocNode>();
        if (depth >= MaximumNavigationDepth)
        {
            return result;
        }

        foreach (var listItem in list.Elements().Where(element => element.Name.LocalName == "li"))
        {
            if (!budget.TryTake())
            {
                break;
            }

            var childLists = listItem.Elements()
                .Where(element => element.Name.LocalName == "ol")
                .ToList();
            var children = childLists
                .SelectMany(child => ReadHtmlNavigationList(
                    child,
                    navigationPath,
                    cacheDirectory,
                    budget,
                    depth + 1))
                .ToList();
            var labelElements = listItem.Elements()
                .TakeWhile(element => element.Name.LocalName != "ol")
                .ToList();
            var anchor = labelElements
                .SelectMany(element => element.DescendantsAndSelf())
                .FirstOrDefault(element => element.Name.LocalName == "a");
            var title = NormalizeTitle(anchor?.Value ?? string.Empty);
            if (anchor is not null &&
                TryCreateTocNode(
                    title,
                    anchor.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "href")?.Value,
                    children,
                    navigationPath,
                    cacheDirectory,
                    out var node))
            {
                result.Add(node);
                continue;
            }

            // EPUB 3 also permits a non-link span as a group heading. Reuse the first
            // child's safe target so the hierarchy can be represented without an unsafe path.
            var groupTitle = NormalizeTitle(labelElements
                .SelectMany(element => element.DescendantsAndSelf())
                .FirstOrDefault(element => element.Name.LocalName == "span")?
                .Value ?? string.Empty);
            if (groupTitle.Length > 0 && children.Count > 0)
            {
                result.Add(new TocNode
                {
                    Title = groupTitle,
                    FullPath = children[0].FullPath,
                    Fragment = children[0].Fragment,
                    Children = children
                });
            }
            else
            {
                result.AddRange(children);
            }
        }

        return result;
    }

    private static IReadOnlyList<TocNode> ReadNcxNavigationPoints(
        XElement parent,
        string navigationPath,
        string cacheDirectory,
        NavigationBudget budget,
        int depth)
    {
        var result = new List<TocNode>();
        if (depth >= MaximumNavigationDepth)
        {
            return result;
        }

        foreach (var point in parent.Elements().Where(element => element.Name.LocalName == "navPoint"))
        {
            if (!budget.TryTake())
            {
                break;
            }

            var children = ReadNcxNavigationPoints(
                point,
                navigationPath,
                cacheDirectory,
                budget,
                depth + 1);
            var label = point.Elements()
                .FirstOrDefault(element => element.Name.LocalName == "navLabel")?
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "text")?
                .Value;
            var href = point.Elements()
                .FirstOrDefault(element => element.Name.LocalName == "content")?
                .Attributes()
                .FirstOrDefault(attribute => attribute.Name.LocalName == "src")?
                .Value;
            if (TryCreateTocNode(
                    NormalizeTitle(label ?? string.Empty),
                    href,
                    children,
                    navigationPath,
                    cacheDirectory,
                    out var node))
            {
                result.Add(node);
            }
            else
            {
                result.AddRange(children);
            }
        }

        return result;
    }

    private static bool TryCreateTocNode(
        string title,
        string? href,
        IReadOnlyList<TocNode> children,
        string navigationPath,
        string cacheDirectory,
        out TocNode node)
    {
        node = null!;
        if (title.Length == 0 ||
            !TryResolveNavigationTarget(
                navigationPath,
                href,
                cacheDirectory,
                out var fullPath,
                out var fragment))
        {
            return false;
        }

        node = new TocNode
        {
            Title = title,
            FullPath = fullPath,
            Fragment = fragment,
            Children = children
        };
        return true;
    }

    private static bool TryResolveNavigationTarget(
        string navigationPath,
        string? href,
        string cacheDirectory,
        out string fullPath,
        out string? fragment)
    {
        fullPath = string.Empty;
        fragment = null;
        if (string.IsNullOrWhiteSpace(href))
        {
            return false;
        }

        var value = href.Trim();
        var fragmentIndex = value.IndexOf('#');
        if (fragmentIndex >= 0)
        {
            fragment = fragmentIndex < value.Length - 1 ? value[(fragmentIndex + 1)..] : null;
            value = value[..fragmentIndex];
        }

        var queryIndex = value.IndexOf('?');
        if (queryIndex >= 0)
        {
            value = value[..queryIndex];
        }

        string? path;
        if (value.Length == 0)
        {
            path = Path.GetFullPath(navigationPath);
        }
        else
        {
            path = ResolveHref(
                Path.GetDirectoryName(navigationPath)!,
                value,
                cacheDirectory);
        }

        if (path is null || !File.Exists(path))
        {
            fragment = null;
            return false;
        }

        fullPath = path;
        return true;
    }

    private static void AddNavigationTitles(
        IDictionary<string, string> result,
        IEnumerable<TocNode> nodes)
    {
        foreach (var node in nodes.SelectMany(node => node.Flatten()))
        {
            result.TryAdd(node.FullPath, node.Title);
        }
    }

    private static bool IsTableOfContentsNavigation(XElement element)
    {
        return element.Attributes()
                   .Where(attribute => attribute.Name.LocalName is "type" or "role")
                   .SelectMany(attribute => attribute.Value.Split(
                       [' ', '\t', '\r', '\n'],
                       StringSplitOptions.RemoveEmptyEntries))
                   .Any(value => string.Equals(value, "toc", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(value, "doc-toc", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasProperty(ManifestItem item, string property)
    {
        return item.Properties.Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries)
            .Contains(property, StringComparer.Ordinal);
    }

    private static string NormalizeTitle(string value)
    {
        return string.Join(
            " ",
            value.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? ResolveHref(string baseDirectory, string href, string rootDirectory)
    {
        try
        {
            var localPart = DecodeHref(href.Split('#', 2)[0]);
            if (string.IsNullOrWhiteSpace(localPart) || Uri.TryCreate(localPart, UriKind.Absolute, out _))
            {
                return null;
            }

            var path = Path.GetFullPath(Path.Combine(baseDirectory, localPart));
            return PathSemantics.IsInside(rootDirectory, path) ? path : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or UriFormatException)
        {
            return null;
        }
    }

    private static string DecodeHref(string href)
    {
        return Uri.UnescapeDataString(href)
            .Replace('/', Path.DirectorySeparatorChar);
    }

    private static string CombineInside(string root, params string[] parts)
    {
        var path = Path.GetFullPath(parts.Aggregate(root, Path.Combine));
        if (!PathSemantics.IsInside(root, path))
        {
            throw new InvalidDataException("EPUB 引用了包外路径。");
        }

        return path;
    }

    private static void ResetCache(string cacheDirectory)
    {
        if (!Directory.Exists(cacheDirectory))
        {
            Directory.CreateDirectory(cacheDirectory);
            return;
        }

        if (!AppPaths.IsInsideCache(cacheDirectory))
        {
            throw new InvalidOperationException("拒绝清理缓存根目录之外的路径。");
        }

        Directory.Delete(cacheDirectory, true);
        Directory.CreateDirectory(cacheDirectory);
    }

    private static string? TryReadMarker(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteMarker(string path, string value)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, value);
        File.Move(temporaryPath, path, true);
    }

    private sealed record ManifestItem(string Id, string Href, string MediaType, string Properties);

    private sealed record NavigationData(
        IReadOnlyDictionary<string, string> Titles,
        IReadOnlyList<TocNode> TableOfContents);

    private sealed class NavigationBudget(int remaining)
    {
        private int _remaining = remaining;

        public bool TryTake()
        {
            if (_remaining <= 0)
            {
                return false;
            }

            _remaining--;
            return true;
        }
    }
}
