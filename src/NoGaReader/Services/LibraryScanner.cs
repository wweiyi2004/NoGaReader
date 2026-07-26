using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using NoGaReader.Models;

namespace NoGaReader.Services;

public sealed class LibraryScanner
{
    private const long MaximumEpubContainerBytes = 1024 * 1024;
    private const long MaximumEpubPackageBytes = 8 * 1024 * 1024;
    private const long MaximumCoverBytes = 16 * 1024 * 1024;
    private const long MaximumFb2MetadataFileBytes = 128 * 1024 * 1024;
    private const int MaximumArchiveEntryCount = 100_000;
    private const int ProgressReportInterval = 25;

    private static readonly EnumerationOptions DirectoryEnumerationOptions = new()
    {
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false
    };

    public Task<LibraryScanResult> ScanAsync(
        string rootDirectory,
        LibraryScanOptions? options = null,
        IProgress<LibraryScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        options ??= new LibraryScanOptions();
        ValidateOptions(options);

        var fullRootDirectory = Path.GetFullPath(rootDirectory);
        return Task.Run(
            () => Scan(fullRootDirectory, options, progress, cancellationToken),
            cancellationToken);
    }

    private static LibraryScanResult Scan(
        string rootDirectory,
        LibraryScanOptions options,
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(rootDirectory))
        {
            throw new DirectoryNotFoundException($"书库目录不存在：{rootDirectory}");
        }

        var items = new List<LibraryScanItem>();
        var issues = new List<LibraryScanIssue>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new Stack<string>();
        var candidateFileCount = 0;
        var processedFileCount = 0;
        var fileLimitReached = false;

        try
        {
            var rootAttributes = File.GetAttributes(rootDirectory);
            if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
            {
                AddIssue(issues, options, rootDirectory, "已跳过符号链接或重解析点目录。");
                return CreateResult(
                    rootDirectory,
                    items,
                    issues,
                    fileLimitReached,
                    candidateFileCount);
            }
        }
        catch (Exception exception) when (IsRecoverableFileException(exception))
        {
            AddIssue(issues, options, rootDirectory, FriendlyMessage(exception));
            return CreateResult(
                rootDirectory,
                items,
                issues,
                fileLimitReached,
                candidateFileCount);
        }

        directories.Push(rootDirectory);
        while (directories.Count > 0 && !fileLimitReached)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = directories.Pop();
            FileSystemInfo[] entries;
            try
            {
                entries = new DirectoryInfo(directory)
                    .GetFileSystemInfos("*", DirectoryEnumerationOptions);
            }
            catch (Exception exception) when (IsRecoverableFileException(exception))
            {
                AddIssue(issues, options, directory, FriendlyMessage(exception));
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try
                {
                    attributes = entry.Attributes;
                }
                catch (Exception exception) when (IsRecoverableFileException(exception))
                {
                    AddIssue(issues, options, entry.FullName, FriendlyMessage(exception));
                    continue;
                }

                if ((attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (options.IncludeSubfolders)
                    {
                        directories.Push(entry.FullName);
                    }

                    continue;
                }

                if (!DocumentFormatSupport.IsDesktopSupported(entry.FullName))
                {
                    continue;
                }

                if (candidateFileCount >= options.MaximumFileCount)
                {
                    fileLimitReached = true;
                    break;
                }

                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(entry.FullName);
                }
                catch (Exception exception) when (IsRecoverableFileException(exception))
                {
                    AddIssue(issues, options, entry.FullName, FriendlyMessage(exception));
                    continue;
                }

                if (!seenPaths.Add(fullPath))
                {
                    continue;
                }

                candidateFileCount++;
                ReportProgress(
                    progress,
                    fullPath,
                    candidateFileCount,
                    processedFileCount,
                    items.Count,
                    force: candidateFileCount == 1);

                try
                {
                    var item = CreateItem(fullPath, options, cancellationToken, out var metadataWarning);
                    items.Add(item);
                    if (!string.IsNullOrWhiteSpace(metadataWarning))
                    {
                        AddIssue(issues, options, fullPath, metadataWarning);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (IsRecoverableFileException(exception))
                {
                    AddIssue(issues, options, fullPath, FriendlyMessage(exception));
                }
                finally
                {
                    processedFileCount++;
                }

                ReportProgress(
                    progress,
                    fullPath,
                    candidateFileCount,
                    processedFileCount,
                    items.Count,
                    force: processedFileCount % ProgressReportInterval == 0);
            }
        }

        ReportProgress(
            progress,
            rootDirectory,
            candidateFileCount,
            processedFileCount,
            items.Count,
            force: true);

        return CreateResult(
            rootDirectory,
            items,
            issues,
            fileLimitReached,
            candidateFileCount);
    }

    private static LibraryScanItem CreateItem(
        string path,
        LibraryScanOptions options,
        CancellationToken cancellationToken,
        out string? metadataWarning)
    {
        metadataWarning = null;
        var file = new FileInfo(path);
        var extension = file.Extension;
        var modifiedUtc = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
        if (options.KnownBooks is not null &&
            options.KnownBooks.TryGetValue(path, out var known) &&
            !known.IsMissing &&
            known.FileSize == file.Length &&
            known.LastModifiedUtc?.ToUnixTimeMilliseconds() == modifiedUtc.ToUnixTimeMilliseconds())
        {
            return new LibraryScanItem
            {
                Path = path,
                Title = known.Title,
                Author = known.Author,
                Format = known.Format,
                FileSize = file.Length,
                LastModifiedUtc = modifiedUtc,
                IsUnchanged = true
            };
        }

        var title = Path.GetFileNameWithoutExtension(path);
        var author = string.Empty;
        byte[]? coverBytes = null;
        string? coverExtension = null;
        if (options.ReadBookMetadata)
        {
            BookMetadata metadata;
            if (extension.Equals(".epub", StringComparison.OrdinalIgnoreCase))
            {
                metadata = ReadEpubMetadata(path, options.IncludeCoverImages, cancellationToken);
            }
            else if (extension.Equals(".fb2", StringComparison.OrdinalIgnoreCase))
            {
                metadata = ReadFb2Metadata(path, file.Length, options.IncludeCoverImages, cancellationToken);
            }
            else if (ComicArchiveExtractor.IsComicExtension(extension))
            {
                metadata = ReadComicMetadata(path, options.IncludeCoverImages, cancellationToken);
            }
            else
            {
                metadata = BookMetadata.Empty;
            }

            title = string.IsNullOrWhiteSpace(metadata.Title) ? title : metadata.Title;
            author = metadata.Author ?? string.Empty;
            coverBytes = metadata.CoverBytes;
            coverExtension = metadata.CoverExtension;
            metadataWarning = metadata.Warning;
        }

        string? coverPath = null;
        if (options.PersistCoverImagesToCache &&
            coverBytes is { Length: > 0 } &&
            !string.IsNullOrWhiteSpace(coverExtension))
        {
            coverPath = PersistCoverImage(path, coverBytes, coverExtension);
            coverBytes = null;
        }

        return new LibraryScanItem
        {
            Path = path,
            Title = title,
            Author = author,
            Format = extension.TrimStart('.').ToUpperInvariant(),
            FileSize = file.Length,
            LastModifiedUtc = modifiedUtc,
            CoverBytes = coverBytes,
            CoverExtension = coverExtension,
            CoverPath = coverPath
        };
    }

    private static string PersistCoverImage(string sourcePath, byte[] bytes, string extension)
    {
        var directory = Path.Combine(AppPaths.CacheRoot, "library-covers");
        Directory.CreateDirectory(directory);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(sourcePath).ToUpperInvariant())))[..24];
        var outputPath = Path.Combine(directory, hash + extension.ToLowerInvariant());
        var temporaryPath = outputPath + ".tmp";
        File.WriteAllBytes(temporaryPath, bytes);
        File.Move(temporaryPath, outputPath, true);
        return outputPath;
    }

    private static BookMetadata ReadComicMetadata(
        string path,
        bool includeCover,
        CancellationToken cancellationToken)
    {
        if (!includeCover)
        {
            return BookMetadata.Empty;
        }

        try
        {
            var cover = ComicArchiveExtractor.ReadCover(path, cancellationToken);
            return cover is null
                ? BookMetadata.Empty
                : new BookMetadata(null, null, cover.Bytes, cover.Extension, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            return new BookMetadata(null, null, null, null, $"漫画封面读取失败：{exception.Message}");
        }
    }

    private static BookMetadata ReadEpubMetadata(
        string path,
        bool includeCover,
        CancellationToken cancellationToken)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count > MaximumArchiveEntryCount)
            {
                throw new InvalidDataException("EPUB 条目过多，已跳过元数据读取。");
            }

            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsArchiveSymlink(entry) ||
                    !TryNormalizeArchiveEntryPath(entry.FullName, out var normalizedPath) ||
                    normalizedPath.Length == 0)
                {
                    continue;
                }

                entries.TryAdd(normalizedPath, entry);
            }

            if (!entries.TryGetValue("META-INF/container.xml", out var containerEntry))
            {
                throw new InvalidDataException("EPUB 缺少 META-INF/container.xml。");
            }

            var container = LoadBoundedXml(
                ReadArchiveEntry(containerEntry, MaximumEpubContainerBytes, cancellationToken),
                MaximumEpubContainerBytes);
            var packageReference = container.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "rootfile")?
                .Attributes()
                .FirstOrDefault(attribute => attribute.Name.LocalName == "full-path")?
                .Value;
            if (!TryResolveArchivePath(string.Empty, packageReference, out var packagePath) ||
                !entries.TryGetValue(packagePath, out var packageEntry))
            {
                throw new InvalidDataException("EPUB 内容包路径无效或不存在。");
            }

            var package = LoadBoundedXml(
                ReadArchiveEntry(packageEntry, MaximumEpubPackageBytes, cancellationToken),
                MaximumEpubPackageBytes);
            var metadataElement = package.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "metadata");
            var title = NormalizeMetadataText(metadataElement?.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "title")?.Value);
            var authors = metadataElement?.Descendants()
                .Where(element => element.Name.LocalName == "creator")
                .Select(element => NormalizeMetadataText(element.Value))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .Take(4)
                .ToArray() ?? [];
            var author = string.Join("、", authors!);

            if (!includeCover)
            {
                return new BookMetadata(title, author, null, null, null);
            }

            var manifestItems = package.Descendants()
                .Where(element => element.Name.LocalName == "item")
                .Select(element => new EpubManifestItem(
                    element.Attribute("id")?.Value ?? string.Empty,
                    element.Attribute("href")?.Value ?? string.Empty,
                    element.Attribute("media-type")?.Value ?? string.Empty,
                    element.Attribute("properties")?.Value ?? string.Empty))
                .Where(item => item.Id.Length > 0 && item.Href.Length > 0)
                .ToList();

            var coverItem = manifestItems.FirstOrDefault(item =>
                item.Properties.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains("cover-image", StringComparer.OrdinalIgnoreCase));
            if (coverItem is null)
            {
                var coverId = metadataElement?.Descendants()
                    .FirstOrDefault(element =>
                        element.Name.LocalName == "meta" &&
                        string.Equals(element.Attribute("name")?.Value, "cover", StringComparison.OrdinalIgnoreCase))?
                    .Attribute("content")?
                    .Value;
                if (!string.IsNullOrWhiteSpace(coverId))
                {
                    coverItem = manifestItems.FirstOrDefault(item =>
                        string.Equals(item.Id, coverId, StringComparison.Ordinal));
                }
            }

            coverItem ??= manifestItems.FirstOrDefault(item =>
                item.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                (item.Id.Equals("cover", StringComparison.OrdinalIgnoreCase) ||
                 item.Id.Equals("cover-image", StringComparison.OrdinalIgnoreCase)));
            if (coverItem is null ||
                !IsSupportedCoverMediaType(coverItem.MediaType) ||
                !TryResolveArchivePath(
                    GetArchiveDirectory(packagePath),
                    coverItem.Href,
                    out var coverPath) ||
                !entries.TryGetValue(coverPath, out var coverEntry))
            {
                return new BookMetadata(title, author, null, null, null);
            }

            try
            {
                var coverBytes = ReadArchiveEntry(coverEntry, MaximumCoverBytes, cancellationToken);
                var coverExtension = DetectCoverExtension(coverBytes, coverItem.MediaType);
                return coverExtension is null
                    ? new BookMetadata(title, author, null, null, "EPUB 封面格式无法安全识别，已忽略封面。")
                    : new BookMetadata(title, author, coverBytes, coverExtension, null);
            }
            catch (InvalidDataException exception)
            {
                return new BookMetadata(title, author, null, null, exception.Message);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableMetadataException(exception))
        {
            return new BookMetadata(null, null, null, null, $"EPUB 元数据读取失败：{FriendlyMessage(exception)}");
        }
    }

    private static BookMetadata ReadFb2Metadata(
        string path,
        long fileLength,
        bool includeCover,
        CancellationToken cancellationToken)
    {
        if (fileLength > MaximumFb2MetadataFileBytes)
        {
            return new BookMetadata(
                null,
                null,
                null,
                null,
                "FB2 超过 128 MB，已跳过轻量元数据读取。");
        }

        string? title = null;
        var authors = new List<string>();
        string? coverId = null;
        byte[]? coverBytes = null;
        string? coverExtension = null;

        try
        {
            using var file = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.SequentialScan);
            using var cancellationStream = new CancellationReadStream(file, cancellationToken);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumFb2MetadataFileBytes + 1024,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                CloseInput = false
            };
            using var reader = XmlReader.Create(cancellationStream, settings);
            var titleInfoDepth = -1;

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType == XmlNodeType.Element)
                {
                    var localName = reader.LocalName;
                    if (localName == "title-info")
                    {
                        titleInfoDepth = reader.Depth;
                    }
                    else if (titleInfoDepth >= 0 && localName == "book-title" && title is null)
                    {
                        title = NormalizeMetadataText(ReadSubtreeText(reader, 1024, ignoreWhitespace: false));
                    }
                    else if (titleInfoDepth >= 0 && localName == "author" && authors.Count < 4)
                    {
                        var author = ReadFb2Author(reader);
                        if (!string.IsNullOrWhiteSpace(author))
                        {
                            authors.Add(author);
                        }
                    }
                    else if (includeCover && titleInfoDepth >= 0 && localName == "coverpage")
                    {
                        coverId = ReadFb2CoverId(reader);
                    }
                    else if (includeCover &&
                             coverId is not null &&
                             localName == "binary" &&
                             string.Equals(reader.GetAttribute("id"), coverId, StringComparison.Ordinal))
                    {
                        var mediaType = reader.GetAttribute("content-type") ?? string.Empty;
                        if (IsSupportedCoverMediaType(mediaType))
                        {
                            var maximumBase64Characters = checked((int)(((MaximumCoverBytes + 2) / 3) * 4));
                            var encoded = ReadSubtreeText(
                                reader,
                                maximumBase64Characters,
                                ignoreWhitespace: true);
                            try
                            {
                                coverBytes = Convert.FromBase64String(encoded);
                                if (coverBytes.LongLength > MaximumCoverBytes)
                                {
                                    coverBytes = null;
                                }
                                else
                                {
                                    coverExtension = DetectCoverExtension(coverBytes, mediaType);
                                }
                            }
                            catch (FormatException)
                            {
                                coverBytes = null;
                            }
                        }

                        break;
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement &&
                         titleInfoDepth >= 0 &&
                         reader.Depth == titleInfoDepth &&
                         reader.LocalName == "title-info")
                {
                    titleInfoDepth = -1;
                    if (!includeCover || string.IsNullOrWhiteSpace(coverId))
                    {
                        break;
                    }
                }
            }

            var distinctAuthors = authors
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            if (coverExtension is null)
            {
                coverBytes = null;
            }

            return new BookMetadata(
                title,
                string.Join("、", distinctAuthors),
                coverBytes,
                coverExtension,
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableMetadataException(exception))
        {
            return new BookMetadata(
                title,
                string.Join("、", authors.Distinct(StringComparer.CurrentCultureIgnoreCase)),
                null,
                null,
                $"FB2 元数据读取失败：{FriendlyMessage(exception)}");
        }
    }

    private static string ReadFb2Author(XmlReader reader)
    {
        string? firstName = null;
        string? middleName = null;
        string? lastName = null;
        string? nickname = null;

        using var subtree = reader.ReadSubtree();
        while (subtree.Read())
        {
            if (subtree.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (subtree.LocalName)
            {
                case "first-name":
                    firstName ??= NormalizeMetadataText(ReadSubtreeText(subtree, 256, ignoreWhitespace: false));
                    break;
                case "middle-name":
                    middleName ??= NormalizeMetadataText(ReadSubtreeText(subtree, 256, ignoreWhitespace: false));
                    break;
                case "last-name":
                    lastName ??= NormalizeMetadataText(ReadSubtreeText(subtree, 256, ignoreWhitespace: false));
                    break;
                case "nickname":
                    nickname ??= NormalizeMetadataText(ReadSubtreeText(subtree, 256, ignoreWhitespace: false));
                    break;
            }
        }

        var fullName = string.Join(" ", new[] { firstName, middleName, lastName }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        return fullName.Length > 0 ? fullName : nickname ?? string.Empty;
    }

    private static string? ReadFb2CoverId(XmlReader reader)
    {
        using var subtree = reader.ReadSubtree();
        while (subtree.Read())
        {
            if (subtree.NodeType != XmlNodeType.Element || subtree.LocalName != "image")
            {
                continue;
            }

            var reference = subtree.GetAttribute("href") ??
                            subtree.GetAttribute("href", "http://www.w3.org/1999/xlink");
            if (!string.IsNullOrWhiteSpace(reference))
            {
                return reference.Trim().TrimStart('#');
            }
        }

        return null;
    }

    private static string ReadSubtreeText(XmlReader reader, int maximumCharacters, bool ignoreWhitespace)
    {
        var output = new StringBuilder(Math.Min(maximumCharacters, 4096));
        var buffer = new char[4096];
        using var subtree = reader.ReadSubtree();
        while (subtree.Read())
        {
            if (subtree.NodeType is not (XmlNodeType.Text or XmlNodeType.CDATA or
                XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace))
            {
                continue;
            }

            int read;
            while ((read = subtree.ReadValueChunk(buffer, 0, buffer.Length)) > 0)
            {
                for (var index = 0; index < read; index++)
                {
                    var character = buffer[index];
                    if (ignoreWhitespace && char.IsWhiteSpace(character))
                    {
                        continue;
                    }

                    if (output.Length >= maximumCharacters)
                    {
                        throw new InvalidDataException("内嵌封面或元数据超过安全大小限制。");
                    }

                    output.Append(character);
                }
            }
        }

        return output.ToString();
    }

    private static XDocument LoadBoundedXml(byte[] bytes, long maximumCharacters)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = maximumCharacters,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CloseInput = false
        };
        using var reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static byte[] ReadArchiveEntry(
        ZipArchiveEntry entry,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (IsArchiveSymlink(entry) || entry.Length < 0 || entry.Length > maximumBytes)
        {
            throw new InvalidDataException($"EPUB 条目 {entry.FullName} 超过安全大小限制。");
        }

        using var input = entry.Open();
        using var output = new MemoryStream((int)Math.Min(entry.Length, maximumBytes));
        var buffer = new byte[64 * 1024];
        long totalBytes = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalBytes += read;
            if (totalBytes > maximumBytes)
            {
                throw new InvalidDataException($"EPUB 条目 {entry.FullName} 超过安全大小限制。");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static bool TryNormalizeArchiveEntryPath(string path, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0)
        {
            return false;
        }

        path = path.Replace('\\', '/');
        if (path.StartsWith('/') || path.Contains(':'))
        {
            return false;
        }

        var parts = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                return false;
            }

            parts.Add(part);
        }

        normalizedPath = string.Join('/', parts);
        return true;
    }

    private static bool TryResolveArchivePath(
        string baseDirectory,
        string? href,
        out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(href))
        {
            return false;
        }

        var localPart = href.Split('#', 2)[0].Split('?', 2)[0];
        try
        {
            localPart = Uri.UnescapeDataString(localPart).Replace('\\', '/');
        }
        catch (UriFormatException)
        {
            return false;
        }

        if (localPart.StartsWith('/') ||
            localPart.Contains(':') ||
            Uri.TryCreate(localPart, UriKind.Absolute, out _))
        {
            return false;
        }

        var parts = new List<string>();
        foreach (var part in string.Concat(baseDirectory.TrimEnd('/'), "/", localPart)
                     .Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count == 0)
                {
                    return false;
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(part);
        }

        normalizedPath = string.Join('/', parts);
        return normalizedPath.Length > 0;
    }

    private static string GetArchiveDirectory(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? string.Empty : path[..separator];
    }

    private static bool IsArchiveSymlink(ZipArchiveEntry entry)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixSymbolicLink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        return unixMode == UnixSymbolicLink;
    }

    private static bool IsSupportedCoverMediaType(string mediaType)
    {
        return mediaType.Trim().ToLowerInvariant() is
            "image/jpeg" or "image/jpg" or "image/png" or "image/gif" or
            "image/webp" or "image/bmp" or "image/x-ms-bmp";
    }

    private static string? DetectCoverExtension(byte[] bytes, string mediaType)
    {
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return ".png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return ".jpg";
        }

        if (bytes.Length >= 6 &&
            bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F' &&
            bytes[3] == '8' && (bytes[4] == '7' || bytes[4] == '9') && bytes[5] == 'a')
        {
            return ".gif";
        }

        if (bytes.Length >= 12 &&
            bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
            bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P')
        {
            return ".webp";
        }

        if (bytes.Length >= 2 && bytes[0] == 'B' && bytes[1] == 'M')
        {
            return ".bmp";
        }

        _ = mediaType;
        return null;
    }

    private static string? NormalizeMetadataText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = string.Join(' ', value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 1024 ? normalized : normalized[..1024];
    }

    private static void ValidateOptions(LibraryScanOptions options)
    {
        if (options.MaximumFileCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaximumFileCount 必须大于 0。");
        }

        if (options.MaximumReportedIssueCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaximumReportedIssueCount 不能小于 0。");
        }
    }

    private static void ReportProgress(
        IProgress<LibraryScanProgress>? progress,
        string currentPath,
        int candidateFileCount,
        int processedFileCount,
        int importedFileCount,
        bool force)
    {
        if (progress is null || (!force && processedFileCount % ProgressReportInterval != 0))
        {
            return;
        }

        progress.Report(new LibraryScanProgress
        {
            CurrentPath = currentPath,
            CandidateFileCount = candidateFileCount,
            ProcessedFileCount = processedFileCount,
            ImportedFileCount = importedFileCount
        });
    }

    private static void AddIssue(
        ICollection<LibraryScanIssue> issues,
        LibraryScanOptions options,
        string path,
        string message)
    {
        if (issues.Count < options.MaximumReportedIssueCount)
        {
            issues.Add(new LibraryScanIssue(path, message));
        }
    }

    private static LibraryScanResult CreateResult(
        string rootDirectory,
        List<LibraryScanItem> items,
        List<LibraryScanIssue> issues,
        bool fileLimitReached,
        int candidateFileCount)
    {
        return new LibraryScanResult
        {
            RootDirectory = rootDirectory,
            Items = items.AsReadOnly(),
            Issues = issues.AsReadOnly(),
            IsFileLimitReached = fileLimitReached,
            CandidateFileCount = candidateFileCount
        };
    }

    private static bool IsRecoverableFileException(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException or SecurityException or
            ArgumentException or NotSupportedException;
    }

    private static bool IsRecoverableMetadataException(Exception exception)
    {
        return IsRecoverableFileException(exception) ||
               exception is XmlException or InvalidDataException or FormatException;
    }

    private static string FriendlyMessage(Exception exception)
    {
        return exception switch
        {
            UnauthorizedAccessException => "没有访问权限，已跳过。",
            SecurityException => "安全策略拒绝访问，已跳过。",
            InvalidDataException => exception.Message,
            XmlException => "XML 内容损坏或不安全，已跳过元数据。",
            _ => string.IsNullOrWhiteSpace(exception.Message) ? "读取失败，已跳过。" : exception.Message
        };
    }

    private sealed record BookMetadata(
        string? Title,
        string? Author,
        byte[]? CoverBytes,
        string? CoverExtension,
        string? Warning)
    {
        public static BookMetadata Empty { get; } = new(null, null, null, null, null);
    }

    private sealed record EpubManifestItem(
        string Id,
        string Href,
        string MediaType,
        string Properties);

    private sealed class CancellationReadStream(Stream inner, CancellationToken cancellationToken) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush()
        {
            inner.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return inner.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return inner.Read(buffer);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return inner.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
