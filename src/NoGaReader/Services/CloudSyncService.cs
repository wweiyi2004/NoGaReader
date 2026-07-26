using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NoGaReader.Models;

namespace NoGaReader.Services;

public sealed class CloudSyncService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly AppSettings _settings;

    public CloudSyncService(AppSettings settings)
    {
        _settings = settings;
    }

    public bool IsConfigured =>
        _settings.SyncEnabled &&
        _settings.SyncProvider == SyncProviderKind.Folder &&
        !string.IsNullOrWhiteSpace(_settings.SyncFolderPath) &&
        Directory.Exists(_settings.SyncFolderPath);

    public string StatusText
    {
        get
        {
            if (!_settings.SyncEnabled)
            {
                return "云同步已关闭（完全本地）";
            }

            if (_settings.SyncProvider != SyncProviderKind.Folder)
            {
                return "未选择同步提供方";
            }

            if (string.IsNullOrWhiteSpace(_settings.SyncFolderPath))
            {
                return "请设置同步文件夹（可用网盘同步目录）";
            }

            if (!Directory.Exists(_settings.SyncFolderPath))
            {
                return "同步文件夹不存在";
            }

            var last = string.IsNullOrWhiteSpace(_settings.LastSyncUtc)
                ? "尚未同步"
                : $"上次同步 {_settings.LastSyncUtc}";
            return $"文件夹同步就绪 · {last}";
        }
    }

    public async Task<SyncResult> SynchronizeAsync(
        LibraryDatabase database,
        SettingsStore settingsStore,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.SyncEnabled)
        {
            return new SyncResult { Succeeded = false, Message = "云同步已关闭。" };
        }

        if (_settings.SyncProvider != SyncProviderKind.Folder)
        {
            return new SyncResult { Succeeded = false, Message = "当前仅支持文件夹/网盘目录同步。" };
        }

        if (string.IsNullOrWhiteSpace(_settings.SyncFolderPath))
        {
            return new SyncResult { Succeeded = false, Message = "请先选择同步文件夹。" };
        }

        Directory.CreateDirectory(_settings.SyncFolderPath);
        // Refresh the live settings timestamp before capturing a manifest. Delayed UI saves
        // persist cloned snapshots, so callers that invoke this service directly must not rely
        // on the UI dialog having refreshed the live AppSettings instance first.
        settingsStore.Save(_settings);
        var remotePath = Path.Combine(_settings.SyncFolderPath, "nogareader-sync.json");
        var localManifest = await BuildLocalManifestAsync(database, cancellationToken).ConfigureAwait(false);
        SyncManifest remoteManifest;
        if (File.Exists(remotePath))
        {
            var json = await File.ReadAllTextAsync(remotePath, cancellationToken).ConfigureAwait(false);
            remoteManifest = JsonSerializer.Deserialize<SyncManifest>(json, JsonOptions) ?? new SyncManifest();
        }
        else
        {
            remoteManifest = new SyncManifest();
        }

        var merged = Merge(localManifest, remoteManifest);
        var tempPath = remotePath + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(
            tempPath,
            JsonSerializer.Serialize(merged, JsonOptions),
            cancellationToken).ConfigureAwait(false);
        if (File.Exists(remotePath))
        {
            File.Replace(tempPath, remotePath, null);
        }
        else
        {
            File.Move(tempPath, remotePath);
        }

        var apply = await ApplyRemoteAsync(database, merged, cancellationToken).ConfigureAwait(false);
        if (_settings.SyncIncludeSettings && merged.Settings is not null)
        {
            ApplySettings(merged.Settings);
            settingsStore.Save(_settings);
        }

        _settings.LastSyncUtc = DateTimeOffset.UtcNow.ToString("u");
        settingsStore.Save(_settings);

        return new SyncResult
        {
            Succeeded = true,
            Message = "同步完成",
            UploadedBooks = localManifest.Books.Count,
            DownloadedBooks = apply.DownloadedBooks,
            MergedAnnotations = apply.MergedAnnotations
        };
    }

    private async Task<SyncManifest> BuildLocalManifestAsync(
        LibraryDatabase database,
        CancellationToken cancellationToken)
    {
        var books = await database.ListBooksAsync(includeMissing: true, cancellationToken).ConfigureAwait(false);
        var manifest = new SyncManifest
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            DeviceName = Environment.MachineName,
            Settings = _settings.SyncIncludeSettings ? CaptureSettings() : null
        };

        foreach (var book in books)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var location = await database.GetReaderLocationAsync(book.Id, cancellationToken).ConfigureAwait(false);
            var annotations = _settings.SyncIncludeAnnotations
                ? await database.ListAnnotationsAsync(book.Id, cancellationToken: cancellationToken).ConfigureAwait(false)
                : [];
            var tombstones = _settings.SyncIncludeAnnotations
                ? await database.ListAnnotationTombstonesAsync(book.Id, cancellationToken).ConfigureAwait(false)
                : [];
            manifest.Books.Add(new SyncBookState
            {
                PathKey = ToPathKey(book.Path),
                BookKey = CreateBookKey(book.Title, book.Author, book.Format, book.FileSize),
                Title = book.Title,
                Author = book.Author ?? string.Empty,
                Format = book.Format ?? string.Empty,
                SourcePathHint = book.Path,
                SourceSize = Math.Max(0, book.FileSize),
                SectionIndex = location?.SectionIndex,
                SectionProgress = location?.SectionProgress,
                DocumentProgress = location?.DocumentProgress,
                Fragment = location?.Fragment,
                AnchorJson = location?.Anchor is null ? null : JsonSerializer.Serialize(location.Anchor),
                UpdatedAt = location?.UpdatedUtc ?? book.LastOpenedUtc ?? book.AddedUtc,
                Annotations = annotations.Select(annotation => new SyncAnnotationState
                {
                    Id = annotation.Id,
                    Kind = annotation.Type.ToString(),
                    Color = annotation.Color,
                    Note = annotation.Note,
                    SelectedText = annotation.SelectedText,
                    SectionIndex = annotation.SectionIndex,
                    SectionProgress = annotation.SectionProgress,
                    Fragment = annotation.SectionPath,
                    AnchorJson = annotation.Anchor is null ? null : JsonSerializer.Serialize(annotation.Anchor),
                    CreatedAt = annotation.CreatedUtc,
                    UpdatedAt = annotation.ModifiedUtc
                }).Concat(tombstones.Select(tombstone => new SyncAnnotationState
                {
                    Id = tombstone.AnnotationId,
                    Kind = "Deleted",
                    IsDeleted = true,
                    CreatedAt = tombstone.DeletedUtc,
                    UpdatedAt = tombstone.DeletedUtc
                })).ToList()
            });
        }

        return manifest;
    }

    private static SyncManifest Merge(SyncManifest local, SyncManifest remote)
    {
        var map = new Dictionary<string, SyncBookState>(StringComparer.OrdinalIgnoreCase);
        foreach (var book in remote.Books)
        {
            map[GetBookIdentity(book)] = book;
        }

        foreach (var book in local.Books)
        {
            var identity = GetBookIdentity(book);
            var legacyIdentity = "path:" + book.PathKey;
            if (!map.ContainsKey(identity) && map.ContainsKey(legacyIdentity))
            {
                identity = legacyIdentity;
            }

            if (!map.TryGetValue(identity, out var existing) || book.UpdatedAt >= existing.UpdatedAt)
            {
                if (map.TryGetValue(identity, out var older))
                {
                    book.Annotations = MergeAnnotations(older.Annotations, book.Annotations);
                }

                map[identity] = book;
            }
            else
            {
                existing.Annotations = MergeAnnotations(existing.Annotations, book.Annotations);
                map[identity] = existing;
            }
        }

        return new SyncManifest
        {
            SchemaVersion = 2,
            UpdatedAt = DateTimeOffset.UtcNow,
            DeviceName = Environment.MachineName,
            Books = map.Values.OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ToList(),
            Settings = MergeSettings(local.Settings, remote.Settings)
        };
    }

    private static SyncSettingsSnapshot? MergeSettings(
        SyncSettingsSnapshot? local,
        SyncSettingsSnapshot? remote)
    {
        if (local is null)
        {
            return remote;
        }

        if (remote is null)
        {
            return local;
        }

        return local.UpdatedAt >= remote.UpdatedAt ? local : remote;
    }

    private static List<SyncAnnotationState> MergeAnnotations(
        IEnumerable<SyncAnnotationState> left,
        IEnumerable<SyncAnnotationState> right)
    {
        var map = new Dictionary<string, SyncAnnotationState>(StringComparer.Ordinal);
        var legacyIdsByCorrelation = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in left)
        {
            var isLegacy = string.IsNullOrWhiteSpace(item.Id);
            item.Id = GetAnnotationIdentity(item);
            MergeItem(item);
            if (isLegacy)
            {
                legacyIdsByCorrelation[GetAnnotationCorrelation(item)] = item.Id;
            }
        }

        foreach (var item in right)
        {
            var isLegacy = string.IsNullOrWhiteSpace(item.Id);
            item.Id = GetAnnotationIdentity(item);
            if (!isLegacy &&
                legacyIdsByCorrelation.Remove(GetAnnotationCorrelation(item), out var legacyId))
            {
                map.Remove(legacyId);
            }

            MergeItem(item);
        }

        return map.Values.ToList();

        void MergeItem(SyncAnnotationState item)
        {
            if (!map.TryGetValue(item.Id, out var existing) ||
                item.UpdatedAt > existing.UpdatedAt ||
                (item.UpdatedAt == existing.UpdatedAt && item.IsDeleted && !existing.IsDeleted))
            {
                map[item.Id] = item;
            }
        }
    }

    private async Task<(int DownloadedBooks, int MergedAnnotations)> ApplyRemoteAsync(
        LibraryDatabase database,
        SyncManifest manifest,
        CancellationToken cancellationToken)
    {
        var books = await database.ListBooksAsync(includeMissing: true, cancellationToken).ConfigureAwait(false);
        var byPathKey = books.ToDictionary(book => ToPathKey(book.Path), StringComparer.OrdinalIgnoreCase);
        var byBookKey = books
            .GroupBy(
                book => CreateBookKey(book.Title, book.Author, book.Format, book.FileSize),
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
        var downloaded = 0;
        var mergedAnnotations = 0;

        foreach (var remoteBook in manifest.Books)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byPathKey.TryGetValue(remoteBook.PathKey, out var localBook) &&
                (string.IsNullOrWhiteSpace(remoteBook.BookKey) ||
                 !byBookKey.TryGetValue(remoteBook.BookKey, out localBook)))
            {
                continue;
            }

            if (remoteBook.SectionIndex is int sectionIndex)
            {
                var location = new ReaderLocation
                {
                    BookId = localBook.Id,
                    SectionIndex = sectionIndex,
                    SectionProgress = Math.Clamp(remoteBook.SectionProgress ?? 0, 0, 1),
                    DocumentProgress = Math.Clamp(
                        remoteBook.DocumentProgress ?? remoteBook.SectionProgress ?? 0,
                        0,
                        1),
                    Fragment = remoteBook.Fragment,
                    Anchor = DeserializeAnchor(remoteBook.AnchorJson),
                    UpdatedUtc = remoteBook.UpdatedAt
                };
                await database.SaveReaderLocationAsync(location, cancellationToken).ConfigureAwait(false);
                downloaded++;
            }

            if (!_settings.SyncIncludeAnnotations || remoteBook.Annotations.Count == 0)
            {
                continue;
            }

            var existing = (await database.ListAnnotationsAsync(
                    localBook.Id,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false))
                .ToDictionary(item => item.Id, StringComparer.Ordinal);
            foreach (var remoteAnnotation in remoteBook.Annotations)
            {
                remoteAnnotation.Id = GetAnnotationIdentity(remoteAnnotation);
                if (remoteAnnotation.Id.Length > 128)
                {
                    continue;
                }

                if (remoteAnnotation.IsDeleted)
                {
                    await database.ApplyAnnotationDeletionAsync(
                        remoteAnnotation.Id,
                        localBook.Id,
                        remoteAnnotation.UpdatedAt,
                        cancellationToken).ConfigureAwait(false);
                    existing.Remove(remoteAnnotation.Id);
                    continue;
                }

                if (!Enum.TryParse<AnnotationType>(remoteAnnotation.Kind, true, out var kind))
                {
                    continue;
                }

                if (existing.TryGetValue(remoteAnnotation.Id, out var localAnnotation) &&
                    localAnnotation.ModifiedUtc >= remoteAnnotation.UpdatedAt)
                {
                    continue;
                }

                var annotation = new Annotation
                {
                    Id = remoteAnnotation.Id,
                    BookId = localBook.Id,
                    Type = kind,
                    Color = remoteAnnotation.Color,
                    Note = remoteAnnotation.Note,
                    SelectedText = remoteAnnotation.SelectedText,
                    SectionIndex = remoteAnnotation.SectionIndex,
                    SectionProgress = remoteAnnotation.SectionProgress,
                    SectionPath = remoteAnnotation.Fragment,
                    Anchor = DeserializeAnchor(remoteAnnotation.AnchorJson),
                    CreatedUtc = remoteAnnotation.CreatedAt,
                    ModifiedUtc = remoteAnnotation.UpdatedAt
                };
                var saved = await database.UpsertAnnotationAsync(annotation, cancellationToken).ConfigureAwait(false);
                existing[saved.Id] = saved;
                mergedAnnotations++;
            }
        }

        return (downloaded, mergedAnnotations);
    }

    private SyncSettingsSnapshot CaptureSettings() => new()
    {
        UpdatedAt = _settings.SyncSettingsModifiedUtc,
        Theme = _settings.Theme.ToString(),
        ReaderTheme = _settings.ReaderTheme.ToString(),
        ReaderFlow = _settings.ReaderFlow.ToString(),
        ReaderFontSize = _settings.ReaderFontSize,
        UsePublisherFont = _settings.UsePublisherFont,
        ReaderLineHeight = _settings.ReaderLineHeight,
        ReaderContentWidth = _settings.ReaderContentWidth,
        ComicDisplay = _settings.ComicDisplay.ToString(),
        ComicDirection = _settings.ComicDirection.ToString(),
        ComicFit = _settings.ComicFit.ToString(),
        ComicCoverSinglePage = _settings.ComicCoverSinglePage,
        ComicScale = _settings.ComicScale,
        UiLanguage = _settings.UiLanguage
    };

    private void ApplySettings(SyncSettingsSnapshot snapshot)
    {
        if (Enum.TryParse<AppThemeMode>(snapshot.Theme, true, out var theme))
        {
            _settings.Theme = theme;
        }

        if (Enum.TryParse<ReaderThemeMode>(snapshot.ReaderTheme, true, out var readerTheme))
        {
            _settings.ReaderTheme = readerTheme;
            _settings.ReaderThemePreferenceInitialized = true;
        }

        if (Enum.TryParse<ReaderFlowMode>(snapshot.ReaderFlow, true, out var flow))
        {
            _settings.ReaderFlow = flow;
        }

        _settings.ReaderFontSize = Math.Clamp(snapshot.ReaderFontSize, 14, 30);
        _settings.UsePublisherFont = snapshot.UsePublisherFont;
        _settings.ReaderLineHeight = Math.Clamp(snapshot.ReaderLineHeight, 1.4, 2.4);
        _settings.ReaderContentWidth = Math.Clamp(snapshot.ReaderContentWidth, 560, 1000);
        if (Enum.TryParse<ComicDisplayMode>(snapshot.ComicDisplay, true, out var comicDisplay))
        {
            _settings.ComicDisplay = comicDisplay;
        }

        if (Enum.TryParse<ComicReadingDirection>(snapshot.ComicDirection, true, out var direction))
        {
            _settings.ComicDirection = direction;
        }

        if (Enum.TryParse<ComicFitMode>(snapshot.ComicFit, true, out var fit))
        {
            _settings.ComicFit = fit;
        }

        _settings.ComicCoverSinglePage = snapshot.ComicCoverSinglePage;
        _settings.ComicScale = Math.Clamp(snapshot.ComicScale, 0.5, 3.0);
        _settings.UiLanguage = snapshot.UiLanguage?.Trim() ?? string.Empty;
        _settings.SyncSettingsModifiedUtc = snapshot.UpdatedAt;
    }

    private static TextAnchor? DeserializeAnchor(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TextAnchor>(json);
        }
        catch
        {
            return null;
        }
    }

    private static string GetBookIdentity(SyncBookState book) =>
        string.IsNullOrWhiteSpace(book.BookKey)
            ? "path:" + book.PathKey
            : "book:" + book.BookKey.Trim();

    private static string CreateBookKey(
        string? title,
        string? author,
        string? format,
        long sourceSize)
    {
        static string Normalize(string? value) => string.Join(
            ' ',
            (value ?? string.Empty)
                .Trim()
                .ToUpperInvariant()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        var identity = string.Join(
            '\u001f',
            Normalize(format),
            Math.Max(0, sourceSize).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Normalize(title),
            Normalize(author));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static string GetAnnotationIdentity(SyncAnnotationState annotation)
    {
        if (!string.IsNullOrWhiteSpace(annotation.Id))
        {
            return annotation.Id.Trim();
        }

        var legacyIdentity = string.Join(
            '\u001f',
            annotation.Kind,
            annotation.SectionIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            annotation.SectionProgress.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            annotation.SelectedText,
            annotation.Note,
            annotation.CreatedAt.ToUniversalTime().ToString("O"));
        return "legacy-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(legacyIdentity)));
    }

    private static string GetAnnotationCorrelation(SyncAnnotationState annotation) => string.Join(
        '\u001f',
        annotation.Kind,
        annotation.SectionIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
        annotation.SelectedText,
        annotation.CreatedAt.ToUniversalTime().ToString("O"));

    private static string ToPathKey(string path)
    {
        try
        {
            return PathSemantics.ToKey(Path.GetFullPath(path));
        }
        catch
        {
            return PathSemantics.ToKey(path.Trim());
        }
    }
}
