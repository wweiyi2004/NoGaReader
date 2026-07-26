using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using NoGaReader.Dialogs;
using NoGaReader.Models;
using NoGaReader.Services;

namespace NoGaReader;

public partial class MainWindow : Window
{
    private const string BookHostName = "book.nogareader.local";
    private const string ComicContentHostName = ComicFolderLoader.ContentHostName;
    private const double SidebarWidth = 452;

    private readonly string? _initialFile;
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly bool _isReaderWindow;
    private string? _readerSourcePath;
    private readonly ObservableCollection<ReaderTabItem> _readerTabs = [];
    private ReaderTabItem? _activeReaderTab;
    private bool _suppressTabSelection;
    private readonly RecentStore _recentStore = new();
    private readonly DocumentLoader _documentLoader;
    private readonly LibraryScanner _libraryScanner = new();
    private readonly BookSearchIndexer _searchIndexer = new();
    private readonly AnnotationExportService _annotationExportService = new();
    private readonly ObservableCollection<RecentBook> _recentItems = [];
    private readonly ObservableCollection<LibraryBook> _libraryItems = [];
    private readonly ObservableCollection<AnnotationListItem> _annotationItems = [];
    private readonly ObservableCollection<SearchResultListItem> _searchItems = [];
    private readonly ObservableCollection<ComicPageListItem> _comicPageItems = [];
    private readonly List<LibraryBook> _allLibraryBooks = [];
    private readonly ICollectionView _libraryView;
    private readonly DispatcherTimer _readingStatsTimer;
    private ReaderViewController _readerController = null!;
    private ComicReaderController _comicController = null!;
    private LibraryDatabase? _libraryDatabase;

    private ReaderSession? _session;
    private LibraryBook? _currentBook;
    private PendingTextSelection? _pendingSelection;
    private Annotation? _pendingAnnotationJump;
    private TextAnchor? _currentLocationAnchor;
    private TextAnchor? _pendingRestoreAnchor;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _appearanceCancellation;
    private CancellationTokenSource? _libraryScanCancellation;
    private CancellationTokenSource? _searchIndexCancellation;
    private CancellationTokenSource? _locationSaveCancellation;
    private CancellationTokenSource? _jsonSaveCancellation;
    private Task? _searchIndexTask;
    private Task? _jsonSaveTask;
    private readonly SemaphoreSlim _jsonSaveGate = new(1, 1);
    private readonly Dictionary<UIElement, int> _overlayAnimationTokens = new();
    private ReaderSettingsDialog? _readerSettingsDialog;
    private ConversionDialog? _conversionDialog;
    private DocumentEditorDialog? _documentEditorDialog;
    private SyncDialog? _syncDialog;
    private bool _webViewReady;
    private bool _comicReady;
    private bool _initializing = true;
    private bool _isFullScreen;
    private bool _isClosing;
    private bool _sidebarVisible = true;
    private bool _sidebarVisibleBeforeFullScreen = true;
    private double _zoomFactor;
    private double _sectionProgress;
    private double? _navigationRestoreProgress;
    private int _currentPage;
    private int _pageCount;
    private string? _pendingFragment;
    private string? _pendingSearchText;
    private int _pendingSearchOccurrenceIndex;
    private string _lastSearchQuery = string.Empty;
    private WindowStyle _previousWindowStyle;
    private ResizeMode _previousResizeMode;
    private WindowState _previousWindowState;
    private DateTimeOffset? _activeReadingStartedAt;

    public MainWindow(string? initialFile, AppSettings settings, SettingsStore settingsStore)
        : this(initialFile, settings, settingsStore, isReaderWindow: false)
    {
    }

    public MainWindow(string? initialFile, AppSettings settings, SettingsStore settingsStore, bool isReaderWindow)
    {
        _initialFile = initialFile;
        _settings = settings;
        _settingsStore = settingsStore;
        _isReaderWindow = isReaderWindow;
        _readerSourcePath = initialFile;
        _documentLoader = new DocumentLoader(settings);
        _zoomFactor = settings.ZoomFactor;

        InitializeComponent();
        _readerController = new ReaderViewController(ReaderView);
        _comicController = new ComicReaderController(ReaderView);
        RecentList.ItemsSource = _recentItems;
        _libraryView = CollectionViewSource.GetDefaultView(_libraryItems);
        _libraryView.Filter = LibraryBookMatchesFilter;
        LibraryList.ItemsSource = _libraryView;
        AnnotationList.ItemsSource = _annotationItems;
        SearchResultsList.ItemsSource = _searchItems;
        ComicPageList.ItemsSource = _comicPageItems;
        if (ReaderTabsList is not null)
        {
            ReaderTabsList.ItemsSource = _readerTabs;
        }
        _readingStatsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _readingStatsTimer.Tick += ReadingStatsTimer_Tick;
        Activated += MainWindow_Activated;
        Deactivated += MainWindow_Deactivated;
        InitializeSettingsControls();
        App.ThemeChanged += App_ThemeChanged;
        Closed += (_, _) => App.ThemeChanged -= App_ThemeChanged;
        _initializing = false;
        ApplyWindowRoleLayout();
        if (_isReaderWindow)
        {
            // Start with full reading surface; open TOC from top bar when needed.
            CloseInspectors();
        }
        else
        {
            ShowLibraryPanel();
        }
        UpdateCaptionMaxGlyph();
        UpdateReaderControls();
        UpdateWelcomeDashboard();
    }


    private void OpenInReaderWindow(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            // Prefer existing shared reader with browser-style tabs.
            if (BookOpenCoordinator.SharedReader is MainWindow existing &&
                !ReferenceEquals(existing, this) &&
                existing._isReaderWindow)
            {
                existing.Activate();
                _ = existing.OpenOrFocusTabAsync(fullPath);
                StatusText.Text = $"已在阅读窗口打开 {Path.GetFileName(fullPath)}";
                return;
            }

            if (_isReaderWindow)
            {
                _ = OpenOrFocusTabAsync(fullPath);
                return;
            }

            var reader = new MainWindow(fullPath, _settings, _settingsStore, isReaderWindow: true);
            BookOpenCoordinator.RegisterSharedReader(reader);
            reader.Closed += (_, _) =>
            {
                if (IsLoaded)
                {
                    RefreshRecentItems();
                    _ = RefreshLibraryItemsAsync();
                    UpdateWelcomeDashboard();
                }
            };
            reader.Show();
            StatusText.Text = $"已在阅读窗口打开 {Path.GetFileName(fullPath)}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法打开阅读窗口", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task OpenOrFocusTabAsync(string path)
    {
        if (!_isReaderWindow)
        {
            OpenInReaderWindow(path);
            return;
        }

        var key = BookOpenCoordinator.NormalizeKey(path);
        var existing = _readerTabs.FirstOrDefault(tab =>
            string.Equals(tab.PathKey, key, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            await SwitchToReaderTabAsync(existing);
            return;
        }

        // Open as a new tab inside this shared reader window.
        await OpenFileInCurrentReaderAsync(path, createTab: true);
    }

    private async Task OpenFileInCurrentReaderAsync(string path, bool createTab)
    {
        // Snapshot current tab before replacing session.
        if (_activeReaderTab is not null && _session is not null)
        {
            await CaptureReadingLocationAsync();
            SaveReadingState();
            await PersistCurrentLocationAsync();
            SnapshotActiveTabFromSession();
        }

        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        var cancellationToken = _loadCancellation.Token;

        SetLoading(true, $"正在打开 {Path.GetFileName(path)}…");
        try
        {
            if (!_webViewReady)
            {
                // Reader window initializes WebView on load; wait briefly.
                for (var i = 0; i < 50 && !_webViewReady; i++)
                {
                    await Task.Delay(50, cancellationToken);
                }
            }

            if (!_webViewReady)
            {
                throw new InvalidOperationException("阅读组件尚未就绪。");
            }

            var session = await _documentLoader.LoadAsync(path, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var recent = _recentStore.Find(path);
            var libraryBook = await UpsertOpenedBookAsync(session, cancellationToken);
            var databaseLocation = libraryBook is not null && _libraryDatabase is not null
                ? await _libraryDatabase.GetReaderLocationAsync(libraryBook.Id, cancellationToken)
                : null;
            if (libraryBook is not null)
            {
                libraryBook.Location = databaseLocation;
            }

            session.CurrentSectionIndex = Math.Clamp(
                databaseLocation?.SectionIndex ?? recent?.SectionIndex ?? 0,
                0,
                session.Sections.Count - 1);

            ReaderTabItem tab;
            if (createTab)
            {
                tab = new ReaderTabItem
                {
                    PathKey = BookOpenCoordinator.NormalizeKey(session.SourcePath),
                    SourcePath = session.SourcePath,
                    Title = session.Title
                };
                _readerTabs.Add(tab);
            }
            else
            {
                tab = _activeReaderTab ?? new ReaderTabItem
                {
                    PathKey = BookOpenCoordinator.NormalizeKey(session.SourcePath),
                    SourcePath = session.SourcePath,
                    Title = session.Title
                };
                if (_activeReaderTab is null)
                {
                    _readerTabs.Add(tab);
                }
            }

            tab.Session = session;
            tab.Book = libraryBook;
            tab.SectionProgress = Math.Clamp(
                databaseLocation?.SectionProgress ?? recent?.SectionProgress ?? 0, 0, 1);
            tab.ZoomFactor = Math.Clamp(recent?.ZoomFactor ?? _settings.ZoomFactor, 0.5, 3.0);
            tab.LocationAnchor = databaseLocation?.Anchor;
            tab.RestoreAnchor = databaseLocation?.Anchor;
            var restoredFragment = databaseLocation?.Fragment;
            var restoredPdfPage = 0;
            if (session.Kind == ReaderDocumentKind.Pdf)
            {
                if (!TryGetPdfPage(restoredFragment, out restoredPdfPage) && recent?.CurrentPage > 0)
                {
                    restoredPdfPage = recent.CurrentPage;
                    restoredFragment = $"page={restoredPdfPage}";
                }
            }

            tab.Fragment = restoredFragment;
            tab.CurrentPage = session.Kind == ReaderDocumentKind.Comic
                ? session.CurrentSectionIndex + 1
                : restoredPdfPage;
            tab.PageCount = session.Kind == ReaderDocumentKind.Comic ? session.ComicPages.Count : 0;
            tab.Title = session.Title;
            tab.SourcePath = session.SourcePath;

            await ApplyReaderTabAsync(tab, navigate: true);
            if (libraryBook is not null)
            {
                UpdateLibraryItem(libraryBook);
            }
            RefreshRecentItems();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消打开文件";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                NotSupportedException or InvalidOperationException)
        {
            StatusText.Text = "无法打开文件";
            MessageBox.Show(this, exception.Message, "打开失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetLoading(false);
        }
    }

    private void SnapshotActiveTabFromSession()
    {
        if (_activeReaderTab is null || _session is null)
        {
            return;
        }

        _activeReaderTab.Session = _session;
        _activeReaderTab.Book = _currentBook;
        _activeReaderTab.SectionProgress = _sectionProgress;
        _activeReaderTab.ZoomFactor = _zoomFactor;
        _activeReaderTab.LocationAnchor = _currentLocationAnchor;
        _activeReaderTab.RestoreAnchor = _pendingRestoreAnchor;
        _activeReaderTab.Fragment = _pendingFragment;
        _activeReaderTab.CurrentPage = _currentPage;
        _activeReaderTab.PageCount = _pageCount;
        _activeReaderTab.Title = _session.Title;
        _activeReaderTab.SourcePath = _session.SourcePath;
        _activeReaderTab.PathKey = BookOpenCoordinator.NormalizeKey(_session.SourcePath);
    }

    private async Task ApplyReaderTabAsync(ReaderTabItem tab, bool navigate)
    {
        if (tab.Session is null)
        {
            return;
        }

        _suppressTabSelection = true;
        try
        {
            foreach (var item in _readerTabs)
            {
                item.IsActive = ReferenceEquals(item, tab);
            }

            _activeReaderTab = tab;
            if (!ReferenceEquals(ReaderTabsList.SelectedItem, tab))
            {
                ReaderTabsList.SelectedItem = tab;
            }
        }
        finally
        {
            _suppressTabSelection = false;
        }

        var session = tab.Session;
        _sectionProgress = tab.SectionProgress;
        _zoomFactor = tab.ZoomFactor;
        _currentPage = tab.CurrentPage;
        _pageCount = tab.PageCount;
        _comicReady = false;
        FlushActiveReadingTime();
        _session = session;
        _readerSourcePath = session.SourcePath;
        BeginReadingTracking();
        _currentBook = tab.Book;
        StartSearchIndexBuild(session, tab.Book);
        _pendingSelection = null;
        _pendingAnnotationJump = null;
        _currentLocationAnchor = tab.LocationAnchor;
        _pendingRestoreAnchor = tab.RestoreAnchor;
        _pendingFragment = tab.Fragment;
        _pendingSearchText = null;
        _pendingSearchOccurrenceIndex = 0;

        var tocSource = session.Kind == ReaderDocumentKind.Comic
            ? null
            : session.TableOfContents.Count > 0
                ? session.TableOfContents
                : session.Sections.Select(section => new TocNode
                {
                    Title = section.Title,
                    FullPath = section.FullPath
                }).ToList();
        TocTree.ItemsSource = tocSource;
        if (ReaderTocTree is not null)
        {
            ReaderTocTree.ItemsSource = tocSource;
        }
        _comicPageItems.Clear();
        ComicThumbnailCache.Clear();
        foreach (var page in session.ComicPages)
        {
            _comicPageItems.Add(new ComicPageListItem(page));
        }
        ComicPageCountText.Text = $"{session.ComicPages.Count:N0} 页";
        NoComicPagesText.Visibility = session.ComicPages.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ComicPageList.SelectedIndex = session.Kind == ReaderDocumentKind.Comic
            ? session.CurrentSectionIndex
            : -1;
        await RefreshAnnotationsAsync();

        DocumentTitleText.Text = session.Title;
        CurrentSectionText.Text = session.CurrentSection.Title;
        Title = $"{session.Title} — NoGaReader";
        WelcomePanel.Visibility = Visibility.Collapsed;
        ReaderView.Visibility = Visibility.Visible;
        NoTocText.Text = session.Kind == ReaderDocumentKind.Comic
            ? "漫画页请使用顶部的漫画模式面板"
            : "打开电子书后显示目录";
        NoTocPanel.Visibility = session.Kind == ReaderDocumentKind.Comic
            ? Visibility.Visible
            : Visibility.Collapsed;
        CloseInspectors();
        if (!_isReaderWindow)
        {
            ShowTocPanel();
        }
        UpdateReaderControls();
        if (ReaderTabsBar is not null)
        {
            ReaderTabsBar.Visibility = _isReaderWindow && _readerTabs.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (navigate)
        {
            await NavigateToCurrentSectionAsync(_pendingFragment);
            SaveReadingState();
        }
    }

    private async Task SwitchToReaderTabAsync(ReaderTabItem tab)
    {
        if (ReferenceEquals(_activeReaderTab, tab))
        {
            Activate();
            return;
        }

        if (_activeReaderTab is not null && _session is not null)
        {
            await CaptureReadingLocationAsync();
            SaveReadingState();
            await PersistCurrentLocationAsync();
            SnapshotActiveTabFromSession();
        }

        if (tab.Session is null)
        {
            await OpenFileInCurrentReaderAsync(tab.SourcePath, createTab: false);
            return;
        }

        await ApplyReaderTabAsync(tab, navigate: true);
        Activate();
    }

    private async void ReaderTabsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTabSelection || !_isReaderWindow)
        {
            return;
        }

        if (ReaderTabsList.SelectedItem is ReaderTabItem tab)
        {
            await SwitchToReaderTabAsync(tab);
        }
    }

    private async void CloseReaderTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ReaderTabItem tab })
        {
            return;
        }

        e.Handled = true;
        await CloseReaderTabAsync(tab);
    }

    private async Task CloseReaderTabAsync(ReaderTabItem tab)
    {
        if (ReferenceEquals(_activeReaderTab, tab) && _session is not null)
        {
            try
            {
                await CaptureReadingLocationAsync();
                SaveReadingState();
                await PersistCurrentLocationAsync();
                SnapshotActiveTabFromSession();
            }
            catch
            {
                // Closing must not be blocked by save failures.
            }
        }

        var index = _readerTabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        _readerTabs.Remove(tab);

        if (_readerTabs.Count == 0)
        {
            _activeReaderTab = null;
            _session = null;
            _currentBook = null;
            _comicPageItems.Clear();
            TocTree.ItemsSource = null;
            if (ReaderTocTree is not null)
            {
                ReaderTocTree.ItemsSource = null;
            }

            if (ReaderTabsBar is not null)
            {
                ReaderTabsBar.Visibility = Visibility.Collapsed;
            }

            // Last tab closed: dismiss the whole reader window (console stays open).
            if (_isReaderWindow)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        Close();
                    }
                    catch
                    {
                        // ignore
                    }
                });
            }
            else
            {
                WelcomePanel.Visibility = Visibility.Visible;
                ReaderView.Visibility = Visibility.Collapsed;
                DocumentTitleText.Text = "NoGaReader";
                CurrentSectionText.Text = UiStrings.LocalReaderSubtitle;
                UpdateReaderControls();
            }

            return;
        }

        if (ReferenceEquals(_activeReaderTab, tab))
        {
            var nextIndex = Math.Clamp(index, 0, _readerTabs.Count - 1);
            await SwitchToReaderTabAsync(_readerTabs[nextIndex]);
        }
    }


    private void ApplyWindowRoleLayout()
    {
        if (_isReaderWindow)
        {
            // Reader window: pure reading surface + top tools/tabs. No console left rail.
            Title = "阅读窗口 — NoGaReader";
            _sidebarVisible = false;
            Sidebar.Visibility = Visibility.Collapsed;
            SidebarColumn.Width = new GridLength(0);
            MainContentColumn.Width = new GridLength(1, GridUnitType.Star);
            if (MainContentHost is not null)
            {
                MainContentHost.Visibility = Visibility.Visible;
            }

            if (ReaderTabsBar is not null)
            {
                ReaderTabsBar.Visibility = Visibility.Visible;
            }
            if (AppThemePanel is not null)
            {
                AppThemePanel.Visibility = Visibility.Collapsed;
            }
            if (TocToggleButton is not null)
            {
                TocToggleButton.Visibility = Visibility.Visible;
            }
            if (SidebarToggleButton is not null)
            {
                SidebarToggleButton.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            // Shell/console only: library + tools, no empty reader pane.
            Title = "NoGaReader 控制台";
            _sidebarVisible = true;
            Sidebar.Visibility = Visibility.Visible;
            SidebarColumn.Width = new GridLength(1, GridUnitType.Star);
            MainContentColumn.Width = new GridLength(0);
            if (MainContentHost is not null)
            {
                MainContentHost.Visibility = Visibility.Collapsed;
            }
            SidebarNavColumn.Width = new GridLength(176);
            SidebarPanelColumn.Width = new GridLength(1, GridUnitType.Star);

            if (ReaderTabsBar is not null)
            {
                ReaderTabsBar.Visibility = Visibility.Collapsed;
            }
            if (AppThemePanel is not null)
            {
                AppThemePanel.Visibility = Visibility.Visible;
            }
            if (TocToggleButton is not null)
            {
                TocToggleButton.Visibility = Visibility.Collapsed;
            }
            if (SidebarToggleButton is not null)
            {
                SidebarToggleButton.Visibility = Visibility.Collapsed;
            }

            // Hide reading-only side nav entries in console.
            TocTabButton.Visibility = Visibility.Collapsed;
            SetNavVisibility("NotesNavButton", Visibility.Collapsed);
            SetNavVisibility("SettingsNavButton", Visibility.Collapsed);

            if (WindowState == WindowState.Normal && Width > 1100)
            {
                Width = 980;
                Height = 720;
            }
        }
    }

    private void SetNavVisibility(string name, Visibility visibility)
    {
        if (FindName(name) is UIElement element)
        {
            element.Visibility = visibility;
        }
    }


    private sealed class ReaderTabItem
    {
        public required string PathKey { get; set; }
        public required string SourcePath { get; set; }
        public string Title { get; set; } = "未命名";
        public ReaderSession? Session { get; set; }
        public LibraryBook? Book { get; set; }
        public double SectionProgress { get; set; }
        public double ZoomFactor { get; set; } = 1.0;
        public TextAnchor? LocationAnchor { get; set; }
        public TextAnchor? RestoreAnchor { get; set; }
        public string? Fragment { get; set; }
        public int CurrentPage { get; set; }
        public int PageCount { get; set; }
        public bool IsActive { get; set; }
    }

    private sealed record PendingTextSelection(string Text, TextAnchor Anchor);

    private sealed class AnnotationListItem(Annotation annotation)
    {
        public Annotation Value { get; } = annotation;

        public string TypeText => Value.Type switch
        {
            AnnotationType.Bookmark => "书签",
            AnnotationType.Note => "笔记",
            _ => "高亮"
        };

        public string SectionText => $"章节 {Value.SectionIndex + 1}";

        public string Quote => Value.SelectedText ?? string.Empty;

        public string? Note => Value.Note;

        public Visibility EditVisibility => Value.Type == AnnotationType.Note
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private sealed class SearchResultListItem(SearchHit hit)
    {
        public SearchHit Value { get; } = hit;

        public string SectionTitle => Value.SectionTitle;

        public string SectionText => Value.MatchInTitle
            ? $"章节 {Value.SectionIndex + 1} · 标题"
            : $"章节 {Value.SectionIndex + 1} · 第 {Value.OccurrenceIndex + 1} 处";

        public string Snippet => Value.Snippet;
    }

    private sealed class ComicPageListItem(ComicPage page)
    {
        public ComicPage Value { get; } = page;

        public string PageText => $"第 {Value.Index + 1} 页";

        public string FileName => Path.GetFileName(Value.FullPath);

        public ImageSource? Thumbnail => ComicThumbnailCache.Get(Value.FullPath);
    }

    private static class ComicThumbnailCache
    {
        private const int MaximumCachedThumbnails = 96;
        private static readonly Dictionary<string, (ImageSource Image, LinkedListNode<string> Node)> Items =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly LinkedList<string> UsageOrder = [];

        public static ImageSource? Get(string path)
        {
            if (Items.TryGetValue(path, out var cached))
            {
                UsageOrder.Remove(cached.Node);
                UsageOrder.AddFirst(cached.Node);
                return cached.Image;
            }

            var image = CreateThumbnail(path);
            if (image is null)
            {
                return null;
            }

            var node = UsageOrder.AddFirst(path);
            Items[path] = (image, node);
            while (Items.Count > MaximumCachedThumbnails && UsageOrder.Last is { } oldest)
            {
                UsageOrder.RemoveLast();
                Items.Remove(oldest.Value);
            }
            return image;
        }

        public static void Clear()
        {
            Items.Clear();
            UsageOrder.Clear();
        }

        private static ImageSource? CreateThumbnail(string path)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 172;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Debug.WriteLine(exception);
                return null;
            }
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        App.ApplyWindowChromeTheme(this);
        RefreshRecentItems();
        try
        {
            _libraryDatabase = new LibraryDatabase();
            await _libraryDatabase.InitializeAsync();
            await RefreshLibraryItemsAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or SqliteException)
        {
            _libraryDatabase = null;
            StatusText.Text = "本地书库暂不可用，仍可直接打开文件";
        }

        UpdateWelcomeDashboard();

        if (_isReaderWindow)
        {
            await InitializeWebViewAsync();
            if (_initialFile is not null)
            {
                await OpenFileAsync(_initialFile);
            }
        }
        else
        {
            // Shell does not host the reader WebView; books open in ReaderWindow.
            _webViewReady = false;
            if (_initialFile is not null)
            {
                OpenInReaderWindow(_initialFile);
            }
        }
    }

    private void InitializeSettingsControls()
    {
        UiStrings.ApplyFromSettings(_settings.UiLanguage);
        var languageTag = string.IsNullOrWhiteSpace(_settings.UiLanguage)
            ? string.Empty
            : (_settings.UiLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en-US" : "zh-CN");
        SelectExclusiveToggle(languageTag, LanguageSystemButton, LanguageZhButton, LanguageEnButton);
        ApplyLocalizedShellText();

        SelectExclusiveToggle(
            _settings.Theme.ToString(),
            SystemThemeButton,
            LightThemeButton,
            DarkThemeButton);
        SelectExclusiveToggle(
            _settings.ReaderTheme.ToString(),
            AutoReaderThemeButton,
            PaperReaderThemeButton,
            LightReaderThemeButton,
            DarkReaderThemeButton);
        SelectExclusiveToggle(
            _settings.ReaderFlow.ToString(),
            PagedFlowButton,
            ScrollingFlowButton);
        SelectExclusiveToggle(
            _settings.ComicDisplay.ToString(),
            ComicSingleButton,
            ComicDoubleButton,
            ComicContinuousButton);
        SelectExclusiveToggle(
            _settings.ComicDirection.ToString(),
            ComicLtrButton,
            ComicRtlButton);
        SelectExclusiveToggle(
            _settings.ComicFit.ToString(),
            ComicFitWidthButton,
            ComicFitHeightButton,
            ComicFitOriginalButton);

        FontSizeSlider.Value = _settings.ReaderFontSize;
        LineHeightSlider.Value = _settings.ReaderLineHeight;
        ContentWidthSlider.Value = _settings.ReaderContentWidth;
        ComicCoverSingleCheckBox.IsChecked = _settings.ComicCoverSinglePage;
        ComicScaleSlider.Value = _settings.ComicScale * 100;
        UpdateReaderSettingsReadout();
        UpdateComicSettingsReadout();
        UpdateReaderSurfaceColor();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: DesktopAppPaths.WebView2Root);
            await ReaderView.EnsureCoreWebView2Async(environment);

            var core = ReaderView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(ReaderRuntime.BuildRuntimeScript());
            core.NavigationStarting += Core_NavigationStarting;
            core.NavigationCompleted += Core_NavigationCompleted;
            core.NewWindowRequested += Core_NewWindowRequested;
            core.PermissionRequested += Core_PermissionRequested;
            core.DownloadStarting += Core_DownloadStarting;
            core.WebMessageReceived += Core_WebMessageReceived;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += Core_WebResourceRequested;

            _webViewReady = true;
            StatusText.Text = "就绪 · 内容不会上传";
        }
        catch (WebView2RuntimeNotFoundException)
        {
            StatusText.Text = "未找到 WebView2 Runtime";
            MessageBox.Show(
                this,
                "NoGaReader 需要 Microsoft Edge WebView2 Runtime。Windows 10/11 通常已经预装，可从 Microsoft 官方安装后重试。",
                "缺少阅读组件",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception exception)
        {
            StatusText.Text = "阅读组件初始化失败";
            MessageBox.Show(this, exception.Message, "无法初始化阅读器", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }


    public async void ActivateFromSecondaryInstance(string? path)
    {
        try
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
            if (!string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
            {
                await OpenFileAsync(path);
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = "无法打开外部传入的文件";
            MessageBox.Show(this, exception.Message, "打开失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开本地阅读或文档文件",
            Filter = DocumentLoader.OpenFileFilter + "|" + OfficeDocumentService.OpenFilter,
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            await OpenFileAsync(dialog.FileName);
        }
    }

    private async void OpenComicFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择漫画图片文件夹",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            await OpenFileAsync(dialog.FolderName);
        }
    }

    private async Task OpenFileAsync(string path)
    {
        var extension = Path.GetExtension(path);
        if (File.Exists(path) && OfficeDocumentService.CanEditNatively(extension))
        {
            OpenDocumentEditor(path);
            return;
        }

        // Shell opens a dedicated reader window (EPUB+ style console + reader split).
        if (!_isReaderWindow)
        {
            OpenInReaderWindow(path);
            return;
        }

        // Reader window uses tabbed open path.
        await OpenFileInCurrentReaderAsync(path, createTab: true);
    }

    private async Task NavigateToCurrentSectionAsync(string? fragment = null)
    {
        if (_session is null || !_webViewReady)
        {
            return;
        }

        SetLoading(true, $"正在载入 {_session.CurrentSection.Title}…");
        _comicReady = false;
        try
        {
            ReaderView.CoreWebView2.ClearVirtualHostNameToFolderMapping(BookHostName);
        }
        catch (ArgumentException)
        {
            // The first document has no previous mapping.
        }
        try
        {
            ReaderView.CoreWebView2.ClearVirtualHostNameToFolderMapping(ComicContentHostName);
        }
        catch (ArgumentException)
        {
            // The previous document did not use an external comic content root.
        }

        ReaderView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            BookHostName,
            _session.RootDirectory,
            CoreWebView2HostResourceAccessKind.DenyCors);
        if (_session.ComicContentRootDirectory is { Length: > 0 } comicContentRoot)
        {
            ReaderView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                ComicContentHostName,
                comicContentRoot,
                CoreWebView2HostResourceAccessKind.DenyCors);
        }
        ReaderView.CoreWebView2.Settings.IsScriptEnabled = _session.EnableScriptExecution;
        ReaderView.ZoomFactor = SupportsReaderRuntime || IsComicSession ? 1.0 : _zoomFactor;

        var relativePath = Path.GetRelativePath(_session.RootDirectory, _session.CurrentSection.FullPath);
        if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("阅读内容位于允许目录之外。");
        }

        var encodedPath = string.Join(
            '/',
            relativePath.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
        _pendingFragment = string.IsNullOrWhiteSpace(fragment) ? null : fragment;
        _navigationRestoreProgress = _sectionProgress;
        string fragmentSuffix;
        if (_pendingFragment is null)
        {
            fragmentSuffix = string.Empty;
        }
        else if (_session.Kind == ReaderDocumentKind.Pdf &&
                 TryGetPdfPage(_pendingFragment, out var pdfPage))
        {
            fragmentSuffix = $"#page={pdfPage}";
        }
        else
        {
            fragmentSuffix = $"#{Uri.EscapeDataString(_pendingFragment)}";
        }

        ReaderView.CoreWebView2.Navigate($"https://{BookHostName}/{encodedPath}{fragmentSuffix}");
        CurrentSectionText.Text = _session.CurrentSection.Title;
        UpdateReaderControls();
        await Task.CompletedTask;
    }

    private void Core_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            return;
        }

        if (IsInternalUri(uri))
        {
            SynchronizeSectionFromUri(uri);
            return;
        }

        e.Cancel = true;
        if (e.IsUserInitiated)
        {
            OpenExternalUri(uri);
        }
        else
        {
            StatusText.Text = "已阻止文档自动打开外部链接";
        }
    }

    private void SynchronizeSectionFromUri(Uri uri)
    {
        if (_session is null || !string.Equals(uri.Host, BookHostName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var relativePath = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'))
                .Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(_session.RootDirectory, relativePath));
            var index = _session.Sections
                .Select((section, sectionIndex) => (section, sectionIndex))
                .FirstOrDefault(item => string.Equals(
                    Path.GetFullPath(item.section.FullPath),
                    fullPath,
                    StringComparison.OrdinalIgnoreCase))
                .sectionIndex;

            if (index < 0 || index >= _session.Sections.Count ||
                !string.Equals(Path.GetFullPath(_session.Sections[index].FullPath), fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (index != _session.CurrentSectionIndex)
            {
                _session.CurrentSectionIndex = index;
                _sectionProgress = 0;
                _currentLocationAnchor = null;
                _pendingRestoreAnchor = null;
                CurrentSectionText.Text = _session.CurrentSection.Title;
            }

            _pendingFragment = string.IsNullOrWhiteSpace(uri.Fragment)
                ? null
                : Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
            if (_session.Kind == ReaderDocumentKind.Pdf &&
                TryGetPdfPage(_pendingFragment, out var pdfPage))
            {
                _currentPage = pdfPage;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            // Invalid in-book links are ignored.
        }
    }

    private void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.IsUserInitiated && Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && !IsInternalUri(uri))
        {
            OpenExternalUri(uri);
        }
    }

    private static void Core_PermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.State = CoreWebView2PermissionState.Deny;
    }

    private void Core_DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;
        e.Handled = true;
        StatusText.Text = "已阻止文档发起下载";
    }

    private void Core_WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri))
        {
            return;
        }

        if (uri.Scheme is "http" or "https" &&
            !string.Equals(uri.Host, BookHostName, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Host, ComicContentHostName, StringComparison.OrdinalIgnoreCase))
        {
            e.Response = ReaderView.CoreWebView2.Environment.CreateWebResourceResponse(
                new MemoryStream(),
                403,
                "Blocked by NoGaReader",
                "Content-Type: text/plain; charset=utf-8");
        }
    }

    private async void Core_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        SetLoading(false);
        if (!e.IsSuccess)
        {
            _navigationRestoreProgress = null;
            StatusText.Text = $"页面加载失败：{e.WebErrorStatus}";
            return;
        }

        if (_navigationRestoreProgress is { } restoreProgress)
        {
            _sectionProgress = restoreProgress;
        }

        await ApplyReaderExperienceAsync();
        if (SupportsReaderRuntime && _pendingFragment is { Length: > 0 } fragment)
        {
            _pendingFragment = null;
            _ = await _readerController.GoToFragmentAsync(fragment);
        }

        if (SupportsReaderRuntime && _pendingAnnotationJump is { } annotation)
        {
            _pendingAnnotationJump = null;
            if (annotation.Anchor is not null)
            {
                _ = await _readerController.GoToAnnotationAsync(annotation.Id);
            }
        }

        if (SupportsReaderRuntime && _pendingSearchText is { Length: > 0 } searchText)
        {
            _pendingSearchText = null;
            var occurrenceIndex = _pendingSearchOccurrenceIndex;
            _pendingSearchOccurrenceIndex = 0;
            _ = await _readerController.RevealTextAsync(searchText, occurrenceIndex);
        }

        _navigationRestoreProgress = null;

        if (_session?.Kind == ReaderDocumentKind.Pdf)
        {
            _ = SyncPdfLocationAsync();
        }

        UpdateReaderControls();
        StatusText.Text = _session?.Kind switch
        {
            ReaderDocumentKind.Epub => "EPUB · 已启用阅读排版",
            ReaderDocumentKind.Pdf => "PDF · 本地阅读 · 支持页码跳转与页内查找",
            ReaderDocumentKind.Comic => $"{GetComicSourceText()} 漫画 · {GetComicDisplayText()} · {GetComicDirectionText()}",
            ReaderDocumentKind.FictionBook => "FB2 · 已启用阅读排版",
            ReaderDocumentKind.Image => "图片 · 本地查看",
            ReaderDocumentKind.Markdown => "Markdown · 已完成格式渲染",
            ReaderDocumentKind.Text => "文本 · 本地阅读",
            ReaderDocumentKind.Html => "网页文档 · 已阻止外部网络资源",
            _ => "就绪 · 内容不会上传"
        };
    }

    private void Core_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source) ||
                source.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(source.Host, BookHostName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement))
            {
                return;
            }

            var messageType = typeElement.GetString();
            if (messageType == "nogareader.location")
            {
                if (_navigationRestoreProgress is not null)
                {
                    return;
                }

                UpdateLocation(root);
                SaveReadingState();
            }
            else if (messageType == "nogareader.selection")
            {
                _pendingSelection = ReadPendingSelection(root);
                if (_pendingSelection is not null)
                {
                    StatusText.Text = $"已选择 {_pendingSelection.Text.Length} 个字符 · 可高亮或添加笔记";
                }

                UpdateReaderControls();
            }
            else if (messageType == "nogareader.selection-clear")
            {
                _pendingSelection = null;
                UpdateReaderControls();
            }
            else if (messageType == "nogareader.annotation-click" &&
                     root.TryGetProperty("id", out var annotationIdElement) &&
                     annotationIdElement.ValueKind == JsonValueKind.String &&
                     annotationIdElement.GetString() is { Length: > 0 and <= 128 } annotationId)
            {
                RevealAnnotationFromReader(annotationId);
            }
            else if (messageType == "nogareader.resize")
            {
                _ = ApplyReaderExperienceAsync();
            }
            else if (messageType == "nogareader.runtime-error")
            {
                var message = root.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : null;
                StatusText.Text = string.IsNullOrWhiteSpace(message)
                    ? "阅读排版运行时发生错误"
                    : $"阅读排版错误：{message}";
            }
            else if (messageType == "nogareader.comic-ready")
            {
                _comicReady = true;
                _ = SynchronizeComicRuntimeAsync();
            }
            else if (messageType == "nogareader.comic-location")
            {
                UpdateComicLocation(root);
                SaveReadingState();
            }
            else if (messageType == "nogareader.comic-scale")
            {
                UpdateComicScale(root);
            }
            else if (messageType == "nogareader.comic-error")
            {
                var message = root.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : null;
                StatusText.Text = string.IsNullOrWhiteSpace(message)
                    ? "漫画页面加载失败"
                    : $"漫画阅读错误：{message}";
            }
        }
        catch (JsonException)
        {
            // Only validated reader-runtime messages affect local progress.
        }
    }

    private async Task ApplyReaderExperienceAsync()
    {
        UpdateReaderSurfaceColor();
        if (IsComicSession && _webViewReady)
        {
            try
            {
                await _comicController.ApplySettingsAsync(_settings);
            }
            catch (InvalidOperationException)
            {
                // Navigation may have changed before the comic runtime was installed.
            }
            return;
        }

        if (!SupportsReaderRuntime || !_webViewReady)
        {
            return;
        }

        try
        {
            var restoreAnchor = _pendingRestoreAnchor;
            await _readerController.ApplyAsync(
                _settings,
                _sectionProgress,
                GetCurrentSectionAnnotationsJson(),
                restoreAnchor);
            if (ReferenceEquals(_pendingRestoreAnchor, restoreAnchor))
            {
                _pendingRestoreAnchor = null;
            }

            // Keep force theme layer in sync after every runtime apply.
            var palette = ResolveReaderPalette();
            var dark = _settings.ReaderTheme switch
            {
                ReaderThemeMode.Dark => true,
                ReaderThemeMode.Light => false,
                ReaderThemeMode.Paper => false,
                _ => App.IsDarkTheme
            };
            await _readerController.ApplyThemeColorsAsync(
                palette.Background,
                palette.Foreground,
                palette.Muted,
                palette.Link,
                palette.Rule,
                dark);
        }
        catch (InvalidOperationException)
        {
            // Navigation may have changed before the runtime was installed.
        }
    }

    private async Task SynchronizeComicRuntimeAsync()
    {
        if (!IsComicSession || !_comicReady || _session is null)
        {
            return;
        }

        try
        {
            await _comicController.ApplySettingsAsync(_settings);
            _ = await _comicController.GoToPageAsync(_session.CurrentSectionIndex, false);
            await _comicController.RequestLocationAsync();
        }
        catch (InvalidOperationException)
        {
            // A newer navigation superseded this comic page.
        }
    }

    private void UpdateComicLocation(JsonElement root)
    {
        if (_session is not { Kind: ReaderDocumentKind.Comic } session ||
            !root.TryGetProperty("pageIndex", out var pageElement) ||
            !pageElement.TryGetInt32(out var pageIndex))
        {
            return;
        }

        var pageCount = root.TryGetProperty("pageCount", out var countElement) && countElement.TryGetInt32(out var count)
            ? count
            : session.ComicPages.Count;
        if (pageIndex < 0 || pageIndex >= session.ComicPages.Count || pageCount != session.ComicPages.Count)
        {
            return;
        }

        session.CurrentSectionIndex = pageIndex;
        _sectionProgress = 0;
        _currentPage = pageIndex + 1;
        _pageCount = pageCount;
        _currentLocationAnchor = null;
        CurrentSectionText.Text = session.CurrentSection.Title;
        if (ComicPageList.SelectedIndex != pageIndex)
        {
            ComicPageList.SelectedIndex = pageIndex;
            ComicPageList.ScrollIntoView(ComicPageList.SelectedItem);
        }
        UpdateReaderControls();
    }

    private void UpdateComicScale(JsonElement root)
    {
        if (!IsComicSession ||
            !root.TryGetProperty("scale", out var scaleElement) ||
            !scaleElement.TryGetDouble(out var scale))
        {
            return;
        }

        _settings.ComicScale = Math.Round(Math.Clamp(scale, 0.5, 3.0), 2);
        _initializing = true;
        ComicScaleSlider.Value = _settings.ComicScale * 100;
        _initializing = false;
        UpdateComicSettingsReadout();
        ScheduleJsonSave();
        UpdateReaderControls();
    }

    private void UpdateLocation(JsonElement root)
    {
        if (root.TryGetProperty("progress", out var progressElement) &&
            progressElement.TryGetDouble(out var progress))
        {
            _sectionProgress = Math.Clamp(progress, 0, 1);
        }

        _currentPage = root.TryGetProperty("page", out var pageElement) && pageElement.TryGetInt32(out var page)
            ? Math.Max(0, page)
            : 0;
        _pageCount = root.TryGetProperty("pageCount", out var countElement) && countElement.TryGetInt32(out var count)
            ? Math.Max(0, count)
            : 0;
        _currentLocationAnchor = root.TryGetProperty("anchor", out var anchorElement)
            ? ReaderViewController.ParseAnchor(anchorElement)
            : null;
        UpdateReaderControls();
    }

    private void UpdateLocation(WebReaderLocation location)
    {
        _sectionProgress = location.Progress;
        _currentPage = location.Page;
        _pageCount = location.PageCount;
        _currentLocationAnchor = location.Anchor;
        UpdateReaderControls();
    }

    private static PendingTextSelection? ReadPendingSelection(JsonElement root)
    {
        if (!root.TryGetProperty("selectedText", out var textElement) ||
            textElement.GetString() is not { } selectedText ||
            string.IsNullOrWhiteSpace(selectedText) ||
            selectedText.Length > 4096 ||
            !root.TryGetProperty("anchor", out var anchorElement) ||
            anchorElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        static string? ReadString(JsonElement element, string name, int maximumLength)
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var text = value.GetString();
            return text is not null && text.Length <= maximumLength ? text : null;
        }

        var startPath = ReadString(anchorElement, "startPath", 1024);
        var endPath = ReadString(anchorElement, "endPath", 1024);
        if (startPath is null || endPath is null)
        {
            return null;
        }

        var startOffset = anchorElement.TryGetProperty("startOffset", out var startOffsetElement) &&
                          startOffsetElement.TryGetInt32(out var startOffsetValue)
            ? Math.Clamp(startOffsetValue, 0, 1_000_000)
            : 0;
        var endOffset = anchorElement.TryGetProperty("endOffset", out var endOffsetElement) &&
                        endOffsetElement.TryGetInt32(out var endOffsetValue)
            ? Math.Clamp(endOffsetValue, 0, 1_000_000)
            : 0;
        var progress = anchorElement.TryGetProperty("progress", out var progressElement) &&
                       progressElement.TryGetDouble(out var progressValue)
            ? Math.Clamp(progressValue, 0, 1)
            : 0;

        return new PendingTextSelection(selectedText.Trim(), new TextAnchor
        {
            StartPath = startPath,
            StartOffset = startOffset,
            EndPath = endPath,
            EndOffset = endOffset,
            ExactText = ReadString(anchorElement, "exactText", 4096) ?? selectedText,
            Prefix = ReadString(anchorElement, "prefix", 256),
            Suffix = ReadString(anchorElement, "suffix", 256),
            Progress = progress
        });
    }

    private async void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        var direction = IsComicRightToLeft && ReferenceEquals(sender, PagePreviousButton) ? 1 : -1;
        await TurnPageOrMoveSectionAsync(direction);
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        var direction = IsComicRightToLeft && ReferenceEquals(sender, PageNextButton) ? -1 : 1;
        await TurnPageOrMoveSectionAsync(direction);
    }

    private async void PreviousChapterButton_Click(object sender, RoutedEventArgs e)
    {
        await MoveSectionAsync(-1);
    }

    private async void NextChapterButton_Click(object sender, RoutedEventArgs e)
    {
        await MoveSectionAsync(1);
    }

    private async void PageJumpButton_Click(object sender, RoutedEventArgs e)
    {
        await JumpToPageFromBoxAsync();
    }

    private async void PageJumpBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await JumpToPageFromBoxAsync();
        }
    }

    private async Task JumpToPageFromBoxAsync()
    {
        if (_session is null || !_webViewReady)
        {
            return;
        }

        if (!int.TryParse(PageJumpBox.Text.Trim(), out var page) || page < 1)
        {
            StatusText.Text = "请输入有效页码";
            return;
        }

        if (IsComicSession)
        {
            var index = Math.Clamp(page - 1, 0, Math.Max(0, _session.ComicPages.Count - 1));
            if (_comicReady)
            {
                _ = await _comicController.GoToPageAsync(index);
            }
            else
            {
                _session.CurrentSectionIndex = index;
                await NavigateToCurrentSectionAsync();
            }

            StatusText.Text = $"已跳到第 {index + 1} 页";
            return;
        }

        if (SupportsReaderRuntime)
        {
            if (_pageCount > 0)
            {
                var progress = Math.Clamp((page - 1d) / Math.Max(1, _pageCount - 1), 0, 1);
                try
                {
                    await _readerController.RestoreProgressAsync(progress);
                    _sectionProgress = progress;
                    _currentPage = Math.Clamp(page, 1, _pageCount);
                    UpdateReaderControls();
                    StatusText.Text = $"已跳到本章第 {page} 页";
                }
                catch (InvalidOperationException)
                {
                    StatusText.Text = "当前无法跳页";
                }

                return;
            }

            // No in-chapter paging info: treat as section index for multi-section books.
            if (_session.Sections.Count > 1)
            {
                var sectionIndex = Math.Clamp(page - 1, 0, _session.Sections.Count - 1);
                _session.CurrentSectionIndex = sectionIndex;
                _sectionProgress = 0;
                await NavigateToCurrentSectionAsync();
                StatusText.Text = $"已跳到第 {sectionIndex + 1} 章";
            }

            return;
        }

        if (_session.Kind is ReaderDocumentKind.Pdf or ReaderDocumentKind.Image)
        {
            await JumpFixedLayoutPageAsync(page);
            return;
        }

        StatusText.Text = "当前文档不支持页码跳转";
    }

    private static bool TryGetPdfPage(string? fragment, out int page)
    {
        page = 0;
        if (string.IsNullOrWhiteSpace(fragment))
        {
            return false;
        }

        var normalized = fragment.Trim().TrimStart('#');
        try
        {
            normalized = Uri.UnescapeDataString(normalized);
        }
        catch (UriFormatException)
        {
            // Keep the original fragment and let the bounded parser reject it.
        }

        foreach (var component in normalized.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = component.IndexOf('=');
            if (separator <= 0 ||
                !component[..separator].Equals("page", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(component[(separator + 1)..], out var parsedPage) ||
                parsedPage <= 0)
            {
                continue;
            }

            page = parsedPage;
            return true;
        }

        return false;
    }

    private async Task JumpFixedLayoutPageAsync(int page)
    {
        if (!_webViewReady || ReaderView.CoreWebView2 is null || _session is null)
        {
            return;
        }

        // Edge PDF viewer understands #page=N; also try JS fallback for some viewers.
        try
        {
            var relativePath = Path.GetRelativePath(_session.RootDirectory, _session.CurrentSection.FullPath);
            var encodedPath = string.Join(
                '/',
                relativePath.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
            ReaderView.CoreWebView2.Navigate($"https://{BookHostName}/{encodedPath}#page={page}");
            _currentPage = page;
            if (_session.Kind == ReaderDocumentKind.Pdf)
            {
                _pendingFragment = $"page={page}";
            }

            if (_pageCount > 0)
            {
                _pageCount = Math.Max(_pageCount, page);
            }

            UpdateReaderControls();
            SaveReadingState();
            SnapshotActiveTabFromSession();
            StatusText.Text = $"已请求跳到第 {page} 页";
        }
        catch (Exception)
        {
            StatusText.Text = "PDF 跳页失败";
        }

        await Task.CompletedTask;
    }

    private async Task FindInPageAsync(string query)
    {
        if (!_webViewReady || ReaderView.CoreWebView2 is null || string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        try
        {
            // Prefer the WebView2 Find API when the installed runtime supports it;
            // older runtimes can still use the script fallback below.
            var core = ReaderView.CoreWebView2;
            try
            {
                var options = core.Environment.CreateFindOptions();
                options.FindTerm = query;
                options.IsCaseSensitive = false;
                options.ShouldHighlightAllMatches = true;
                await core.Find.StartAsync(options);
                StatusText.Text = $"页内查找：{query}";
                return;
            }
            catch (Exception exception)
            {
                Debug.WriteLine(exception);
                // Fall through whenever the native Find API is unavailable or rejects
                // the current document; window.find still works for ordinary HTML.
            }

            var script =
                "(function(q){" +
                "try{" +
                "if(window.find){ var ok=window.find(q,false,false,true,false,false,false); return ok?'hit':'miss'; }" +
                "return 'unsupported';" +
                "}catch(e){ return 'error'; }" +
                "})(" + JsonSerializer.Serialize(query) + ")";
            var result = await core.ExecuteScriptAsync(script);
            StatusText.Text = result.Contains("hit", StringComparison.OrdinalIgnoreCase)
                ? $"页内找到：{query}"
                : $"页内未找到：{query}";
        }
        catch (Exception exception)
        {
            StatusText.Text = "页内查找失败";
            Debug.WriteLine(exception);
        }
    }

    private async Task SyncPdfLocationAsync()
    {
        var session = _session;
        if (_isClosing ||
            session?.Kind != ReaderDocumentKind.Pdf ||
            !_webViewReady ||
            ReaderView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            // Best-effort: Edge PDF viewer may expose page via hash or PDF.js-like API.
            const string script =
                """
                (() => {
                  try {
                    const hash = String(location.hash || '');
                    const pageMatch = hash.match(/page=(\d+)/i);
                    const page = pageMatch ? Number(pageMatch[1]) : 0;
                    let pages = 0;
                    if (window.PDFViewerApplication && PDFViewerApplication.pagesCount) {
                      pages = Number(PDFViewerApplication.pagesCount) || 0;
                      const current = Number(PDFViewerApplication.page) || page || 0;
                      return JSON.stringify({ page: current, pages });
                    }
                    return JSON.stringify({ page: page || 0, pages: 0 });
                  } catch (e) {
                    return JSON.stringify({ page: 0, pages: 0 });
                  }
                })()
                """;
            var raw = await ReaderView.CoreWebView2.ExecuteScriptAsync(script);
            using var document = JsonDocument.Parse(raw);
            var text = document.RootElement.ValueKind == JsonValueKind.String
                ? document.RootElement.GetString()
                : raw;
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            using var payload = JsonDocument.Parse(text);
            if (_isClosing || !ReferenceEquals(_session, session))
            {
                return;
            }

            if (payload.RootElement.TryGetProperty("page", out var pageElement) &&
                pageElement.TryGetInt32(out var page) &&
                page > 0)
            {
                _currentPage = page;
                _pendingFragment = $"page={page}";
            }

            if (payload.RootElement.TryGetProperty("pages", out var pagesElement) &&
                pagesElement.TryGetInt32(out var pages) &&
                pages > 0)
            {
                _pageCount = pages;
            }

            UpdateReaderControls();
        }
        catch
        {
            // PDF location is best-effort only.
        }
    }

    private async Task GoToDocumentBoundaryAsync(bool toEnd)
    {
        if (_session is null)
        {
            return;
        }

        if (IsComicSession)
        {
            var index = toEnd ? Math.Max(0, _session.ComicPages.Count - 1) : 0;
            if (_comicReady)
            {
                _ = await _comicController.GoToPageAsync(index);
            }
            else
            {
                _session.CurrentSectionIndex = index;
                await NavigateToCurrentSectionAsync();
            }

            return;
        }

        if (SupportsReaderRuntime)
        {
            try
            {
                await _readerController.RestoreProgressAsync(toEnd ? 1 : 0);
                _sectionProgress = toEnd ? 1 : 0;
                if (_pageCount > 0)
                {
                    _currentPage = toEnd ? _pageCount : 1;
                }

                UpdateReaderControls();
            }
            catch (InvalidOperationException)
            {
                // Runtime not ready.
            }

            return;
        }

        if (_session.Kind is ReaderDocumentKind.Pdf)
        {
            if (toEnd && _pageCount <= 0)
            {
                StatusText.Text = "暂时无法确定 PDF 总页数，请使用内置查看器跳到末页";
                return;
            }

            await JumpFixedLayoutPageAsync(toEnd ? Math.Max(1, _pageCount) : 1);
        }
    }

    private async Task TurnPageOrMoveSectionAsync(int direction)
    {
        if (_session is null)
        {
            return;
        }

        if (IsComicSession)
        {
            if (!_comicReady)
            {
                return;
            }

            try
            {
                var moved = await _comicController.TurnAsync(direction);
                // Comic turn=false at last page means finish.
                if (!moved && direction > 0)
                {
                    await CloseReaderAfterFinishAsync();
                }
            }
            catch (InvalidOperationException)
            {
                // The comic is currently navigating.
            }
            return;
        }

        if (SupportsReaderRuntime)
        {
            try
            {
                var result = await _readerController.TurnPageAsync(direction);
                if (result.Location is not null)
                {
                    UpdateLocation(result.Location);
                }

                if (result.Moved)
                {
                    return;
                }

                // Page did not move: either chapter boundary or document end.
                if (direction > 0)
                {
                    if (_session.CurrentSectionIndex < _session.Sections.Count - 1)
                    {
                        await MoveSectionAsync(1);
                        return;
                    }

                    // Last chapter + cannot move further => finished.
                    await CloseReaderAfterFinishAsync();
                    return;
                }

                if (direction < 0)
                {
                    if (_session.CurrentSectionIndex > 0)
                    {
                        await MoveSectionAsync(-1);
                    }

                    return;
                }
            }
            catch (InvalidOperationException)
            {
                return;
            }

            return;
        }

        // PDF / image / other fixed layout.
        if (direction > 0)
        {
            // Try page jump forward when we know page numbers.
            if (_pageCount > 0 && _currentPage > 0 && _currentPage < _pageCount)
            {
                await JumpFixedLayoutPageAsync(_currentPage + 1);
                return;
            }

            if (_session.Sections.Count > 1 &&
                _session.CurrentSectionIndex < _session.Sections.Count - 1)
            {
                await MoveSectionAsync(1);
                return;
            }

            // Only a known final page is enough evidence to finish a fixed-layout document.
            // In particular, Edge's PDF viewer commonly reports no page count while loading;
            // closing here would turn the first PageDown/Space press into "close reader".
            if (_pageCount > 0 && _currentPage >= _pageCount)
            {
                await CloseReaderAfterFinishAsync();
                return;
            }

            StatusText.Text = "页数尚未就绪，请使用文档内置翻页";
            return;
        }

        if (direction < 0)
        {
            if (_pageCount > 0 && _currentPage > 1)
            {
                await JumpFixedLayoutPageAsync(_currentPage - 1);
                return;
            }

            if (_session.Sections.Count > 1 && _session.CurrentSectionIndex > 0)
            {
                await MoveSectionAsync(-1);
            }
        }
    }

    private async Task MoveSectionAsync(int offset)
    {
        if (_session is null)
        {
            return;
        }

        var nextIndex = Math.Clamp(
            _session.CurrentSectionIndex + offset,
            0,
            _session.Sections.Count - 1);
        if (nextIndex == _session.CurrentSectionIndex)
        {
            if (offset > 0)
            {
                await CloseReaderAfterFinishAsync();
            }

            return;
        }

        _session.CurrentSectionIndex = nextIndex;
        _sectionProgress = offset < 0 ? 1 : 0;
        _currentLocationAnchor = null;
        _pendingRestoreAnchor = null;
        _currentPage = 0;
        _pageCount = 0;
        await NavigateToCurrentSectionAsync();
        SaveReadingState();
        RefreshRecentItems();
    }

    private async Task CloseReaderAfterFinishAsync()
    {
        if (!_isReaderWindow)
        {
            StatusText.Text = UiStrings.ReachedEnd;
            return;
        }

        StatusText.Text = UiStrings.FinishedClosing;

        // Closing the last/active tab will shut down the whole reader window.
        if (_activeReaderTab is not null)
        {
            await CloseReaderTabAsync(_activeReaderTab);
            return;
        }

        if (_readerTabs.Count > 0)
        {
            await CloseReaderTabAsync(_readerTabs[^1]);
            return;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            try
            {
                Close();
            }
            catch
            {
                // ignore
            }
        });
    }

    private async void TocTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_session is null || e.NewValue is not TocNode node)
        {
            return;
        }

        await CaptureReadingLocationAsync();
        var sectionIndex = _session.Sections
            .Select((section, index) => (section, index))
            .Where(item => string.Equals(
                Path.GetFullPath(item.section.FullPath),
                Path.GetFullPath(node.FullPath),
                StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .First();
        if (sectionIndex < 0)
        {
            StatusText.Text = "该目录项不属于正文阅读顺序";
            return;
        }

        _session.CurrentSectionIndex = sectionIndex;
        _sectionProgress = 0;
        _currentLocationAnchor = null;
        _pendingRestoreAnchor = null;
        _currentPage = IsComicSession ? sectionIndex + 1 : 0;
        _pageCount = IsComicSession ? _session.ComicPages.Count : 0;
        if (IsComicSession && _comicReady)
        {
            _ = await _comicController.GoToPageAsync(sectionIndex);
            SaveReadingState();
            RefreshRecentItems();
            return;
        }
        await NavigateToCurrentSectionAsync(node.Fragment);
        SaveReadingState();
        RefreshRecentItems();
    }

    private async Task CaptureReadingLocationAsync()
    {
        if (IsComicSession && _comicReady && _webViewReady)
        {
            try
            {
                await _comicController.RequestLocationAsync();
            }
            catch (InvalidOperationException)
            {
                // There is no stable comic document to query while navigating.
            }
            return;
        }

        if (_session?.Kind == ReaderDocumentKind.Pdf && _webViewReady)
        {
            await SyncPdfLocationAsync();
            return;
        }

        if (!SupportsReaderRuntime || !_webViewReady)
        {
            return;
        }

        try
        {
            var location = await _readerController.GetLocationAsync();
            if (location is not null)
            {
                UpdateLocation(location);
            }
        }
        catch (InvalidOperationException)
        {
            // There is no stable document to query while navigating.
        }
    }

    private void ChangeZoom(double delta)
    {
        if (IsComicSession)
        {
            _settings.ComicScale = Math.Round(Math.Clamp(_settings.ComicScale + delta, 0.5, 3.0), 2);
            _initializing = true;
            ComicScaleSlider.Value = _settings.ComicScale * 100;
            _initializing = false;
            UpdateComicSettingsReadout();
            _ = ApplyComicScaleAsync();
            return;
        }

        if (SupportsReaderRuntime)
        {
            _settings.ReaderFontSize = Math.Clamp(_settings.ReaderFontSize + Math.Sign(delta), 14, 30);
            _initializing = true;
            FontSizeSlider.Value = _settings.ReaderFontSize;
            _initializing = false;
            UpdateReaderSettingsReadout();
            ScheduleReaderAppearanceUpdate();
            return;
        }

        _zoomFactor = Math.Round(Math.Clamp(_zoomFactor + delta, 0.5, 3.0), 2);
        _settings.ZoomFactor = _zoomFactor;
        if (_webViewReady)
        {
            ReaderView.ZoomFactor = _zoomFactor;
        }

        UpdateReaderControls();
        SaveReadingState();
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        await SearchWholeBookAsync();
    }

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SearchWholeBookAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            HideSearch();
        }
    }

    private void SearchToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsSearchOpen())
        {
            HideSearch();
        }
        else
        {
            ShowSearch();
        }
    }

    private void ShowSearch()
    {
        // EPUB/FB2/text: full-book index search. PDF/HTML: in-page find.
        if (_session?.SupportsInPageSearch != true &&
            _session?.Kind is not (ReaderDocumentKind.Pdf or ReaderDocumentKind.Html or ReaderDocumentKind.Image))
        {
            return;
        }

        AnimateOverlayOpen(SearchEntryPanel, slideFromTop: true);
        SearchToggleButton.Background = (Brush)FindResource("PrimarySoftBrush");
        SearchToggleButton.Foreground = (Brush)FindResource("PrimaryBrush");
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void HideSearch()
    {
        AnimateOverlayClose(SearchEntryPanel);
        SearchToggleButton.Background = Brushes.Transparent;
        SearchToggleButton.Foreground = (Brush)FindResource("TextBrush");
        Focus();
    }

    private bool IsSearchOpen() => IsOverlayOpen(SearchEntryPanel);

    private async Task SearchWholeBookAsync()
    {
        var query = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(query) || !_webViewReady || _session is null)
        {
            return;
        }

        // Fixed-layout documents: use WebView2 page find instead of FTS index.
        if (_session.Kind is ReaderDocumentKind.Pdf or ReaderDocumentKind.Html or ReaderDocumentKind.Image)
        {
            await FindInPageAsync(query);
            return;
        }

        if (_session.SupportsInPageSearch != true)
        {
            return;
        }

        _lastSearchQuery = query;
        if (_libraryDatabase is not null && _currentBook is not null)
        {
            try
            {
                if (_searchIndexTask is not null)
                {
                    StatusText.Text = "正在准备全书索引…";
                    await _searchIndexTask;
                }

                var hits = await _libraryDatabase.SearchAsync(query, _currentBook.Id, 100);
                _searchItems.Clear();
                foreach (var hit in hits)
                {
                    _searchItems.Add(new SearchResultListItem(hit));
                }

                SearchQueryText.Text = $"“{query}”";
                SearchResultCountText.Text = $"{_searchItems.Count} 个结果";
                NoSearchResultsText.Text = _searchItems.Count == 0
                    ? "整本书中没有找到匹配内容"
                    : string.Empty;
                NoSearchResultsText.Visibility = _searchItems.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                ShowInspector(SearchResultsPanel);
                StatusText.Text = _searchItems.Count == 0
                    ? $"整本书未找到：{query}"
                    : $"全书找到 {_searchItems.Count} 处：{query}";
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or SqliteException)
            {
                StatusText.Text = "全书索引暂不可用，已改为查找当前页面";
            }
        }

        var result = await ReaderView.CoreWebView2.ExecuteScriptAsync(
            $"window.find({JsonSerializer.Serialize(query)}, false, false, true, false, false, false)");
        StatusText.Text = string.Equals(result, "true", StringComparison.OrdinalIgnoreCase)
            ? $"已在当前页面找到：{query}"
            : $"当前页面未找到：{query}";
    }

    private async void HighlightButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveSelectionAnnotationAsync(AnnotationType.Highlight, null, "yellow");
    }

    private async void HighlightColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string color })
        {
            await SaveSelectionAnnotationAsync(AnnotationType.Highlight, null, color);
        }
    }

    private async void NoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingSelection is not { } selection)
        {
            return;
        }

        var dialog = new NoteDialog(selection.Text) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await SaveSelectionAnnotationAsync(AnnotationType.Note, dialog.NoteText, dialog.ColorKey);
        }
    }

    private async Task SaveSelectionAnnotationAsync(
        AnnotationType type,
        string? note,
        string color)
    {
        if (_pendingSelection is not { } selection ||
            _session is null ||
            _currentBook is null ||
            _libraryDatabase is null)
        {
            return;
        }

        var annotation = new Annotation
        {
            BookId = _currentBook.Id,
            Type = type,
            SectionIndex = _session.CurrentSectionIndex,
            SectionPath = _session.CurrentSection.FullPath,
            SectionProgress = selection.Anchor.Progress,
            Anchor = selection.Anchor,
            SelectedText = selection.Text,
            Note = note,
            Color = color
        };
        await _libraryDatabase.UpsertAnnotationAsync(annotation);
        await RefreshAnnotationsAsync();
        await _readerController.ApplyAnnotationsAsync(GetCurrentSectionAnnotationsJson());
        await _readerController.ClearSelectionAsync();
        _pendingSelection = null;
        UpdateReaderControls();
        StatusText.Text = type == AnnotationType.Note ? "笔记已保存在本机" : "高亮已保存在本机";
    }

    private void BookmarkButton_Click(object sender, RoutedEventArgs e)
    {
        _ = RunGuardedUiActionAsync(ToggleBookmarkAsync, "书签操作失败");
    }

    private async Task ToggleBookmarkAsync()
    {
        if (_session is null || _currentBook is null || _libraryDatabase is null)
        {
            return;
        }

        await CaptureReadingLocationAsync();
        var existing = _annotationItems.FirstOrDefault(item =>
            item.Value.Type == AnnotationType.Bookmark &&
            item.Value.SectionIndex == _session.CurrentSectionIndex &&
            Math.Abs(item.Value.SectionProgress - _sectionProgress) < 0.012);
        if (existing is not null)
        {
            await _libraryDatabase.DeleteAnnotationAsync(existing.Value.Id);
            StatusText.Text = "当前位置书签已移除";
        }
        else
        {
            await _libraryDatabase.UpsertAnnotationAsync(new Annotation
            {
                BookId = _currentBook.Id,
                Type = AnnotationType.Bookmark,
                SectionIndex = _session.CurrentSectionIndex,
                SectionPath = _session.CurrentSection.FullPath,
                SectionProgress = _sectionProgress,
                SelectedText = _session.CurrentSection.Title,
                Color = "blue"
            });
            StatusText.Text = "书签已保存在本机";
        }

        await RefreshAnnotationsAsync();
    }

    private void TocToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsOverlayOpen(ReaderTocPanel))
        {
            AnimateOverlayClose(ReaderTocPanel);
        }
        else
        {
            ShowInspector(ReaderTocPanel);
        }
    }

    private void AnnotationsButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsOverlayOpen(AnnotationsPanel))
        {
            AnimateOverlayClose(AnnotationsPanel);
        }
        else
        {
            ShowInspector(AnnotationsPanel);
        }
    }

    private async void AnnotationList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AnnotationList.SelectedItem is not AnnotationListItem item || _session is null)
        {
            return;
        }

        var annotation = item.Value;

        await CaptureReadingLocationAsync();
        _session.CurrentSectionIndex = Math.Clamp(annotation.SectionIndex, 0, _session.Sections.Count - 1);
        _sectionProgress = Math.Clamp(annotation.SectionProgress, 0, 1);
        _currentLocationAnchor = annotation.Anchor;
        _pendingRestoreAnchor = annotation.Anchor;
        _pendingAnnotationJump = annotation;
        await NavigateToCurrentSectionAsync();
        StatusText.Text = annotation.Type switch
        {
            AnnotationType.Bookmark => "已回到书签位置",
            AnnotationType.Note => "已回到笔记原文",
            _ => "已回到高亮原文"
        };
    }

    private async void DeleteAnnotation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { CommandParameter: AnnotationListItem item } || _libraryDatabase is null)
        {
            return;
        }

        await DeleteAnnotationAsync(item);
    }

    private void AnnotationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        AnnotationDetailCard.DataContext = AnnotationList.SelectedItem;
        AnnotationDetailCard.Visibility = AnnotationList.SelectedItem is AnnotationListItem
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void DeleteSelectedAnnotation_Click(object sender, RoutedEventArgs e)
    {
        if (AnnotationList.SelectedItem is AnnotationListItem item)
        {
            await DeleteAnnotationAsync(item);
        }
    }

    private async Task DeleteAnnotationAsync(AnnotationListItem item)
    {
        if (_libraryDatabase is null)
        {
            return;
        }

        await _libraryDatabase.DeleteAnnotationAsync(item.Value.Id);
        await RefreshAnnotationsAsync();
        if (SupportsReaderRuntime)
        {
            await _readerController.ApplyAnnotationsAsync(GetCurrentSectionAnnotationsJson());
        }

        StatusText.Text = item.Value.Type switch
        {
            AnnotationType.Bookmark => "书签已取消",
            AnnotationType.Highlight => "高亮已取消",
            _ => "笔记已删除"
        };
    }

    private void RevealAnnotationFromReader(string annotationId)
    {
        var item = _annotationItems.FirstOrDefault(candidate => candidate.Value.Id == annotationId);
        if (item is null)
        {
            return;
        }

        ShowInspector(AnnotationsPanel);
        AnnotationList.SelectedItem = item;
        AnnotationList.ScrollIntoView(item);
        StatusText.Text = item.Value.Type == AnnotationType.Note
            ? "已打开这段文字的完整笔记"
            : "已选中这处高亮，可在右侧取消标记";
    }

    private async void ImportAnnotations_Click(object sender, RoutedEventArgs e)
    {
        var targetBook = _currentBook;
        var database = _libraryDatabase;
        if (targetBook is null || database is null)
        {
            return;
        }

        var targetBookId = targetBook.Id;
        var targetBookTitle = targetBook.Title;

        var dialog = new OpenFileDialog
        {
            Title = "导入批注 JSON",
            Filter = "NoGaReader JSON 批注 (*.json)|*.json|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(dialog.FileName);
            var document = _annotationExportService.ReadJsonDocument(json);
            var imported = _annotationExportService.ToAnnotations(document, targetBookId);
            if (imported.Count == 0)
            {
                StatusText.Text = "文件中没有可导入的批注";
                return;
            }

            var existing = await database.ListAnnotationsAsync(targetBookId);
            var existingKeys = new HashSet<string>(
                existing.Select(GetAnnotationDedupeKey),
                StringComparer.Ordinal);
            var unique = imported
                .Where(item => existingKeys.Add(GetAnnotationDedupeKey(item)))
                .ToList();
            var skipped = imported.Count - unique.Count;

            if (unique.Count == 0)
            {
                StatusText.Text = skipped > 0
                    ? $"没有新批注可导入（跳过 {skipped} 条重复）"
                    : "文件中没有可导入的批注";
                return;
            }

            var confirm = MessageBox.Show(
                this,
                $"将向《{targetBookTitle}》导入 {unique.Count} 条新批注" +
                (skipped > 0 ? $"（跳过 {skipped} 条重复）" : string.Empty) +
                "。\n\n导入只新增记录，不会覆盖现有批注。是否继续？",
                "导入批注",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            await database.UpsertAnnotationsAsync(unique);
            var saved = unique.Count;

            if (_currentBook?.Id == targetBookId)
            {
                await RefreshAnnotationsAsync();
                if (SupportsReaderRuntime && _webViewReady)
                {
                    await _readerController.ApplyAnnotationsAsync(GetCurrentSectionAnnotationsJson());
                }
            }

            StatusText.Text = skipped > 0
                ? $"已导入 {saved} 条批注，跳过 {skipped} 条重复"
                : $"已导入 {saved} 条批注";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or
                InvalidOperationException or SqliteException or ArgumentException or FormatException or NotSupportedException)
        {
            StatusText.Text = "批注导入失败";
            MessageBox.Show(this, exception.Message, "无法导入批注", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void EditLibraryBookMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (_libraryDatabase is null)
        {
            return;
        }

        var book = ResolveLibraryBookFromSender(sender);
        if (book is null)
        {
            return;
        }

        var titleDialog = new NoteDialog(
            selectedText: $"当前书名：{book.Title}",
            existingNote: book.Title,
            color: "yellow",
            isEditing: true)
        {
            Owner = this,
            Title = "编辑书名"
        };
        if (titleDialog.ShowDialog() != true)
        {
            return;
        }

        var newTitle = titleDialog.NoteText.Trim();
        if (string.IsNullOrWhiteSpace(newTitle))
        {
            StatusText.Text = "书名不能为空";
            return;
        }

        var authorDialog = new NoteDialog(
            selectedText: $"《{newTitle}》",
            existingNote: book.Author ?? string.Empty,
            color: "yellow",
            isEditing: true)
        {
            Owner = this,
            Title = "编辑作者"
        };
        if (authorDialog.ShowDialog() != true)
        {
            return;
        }

        book.Title = newTitle;
        book.Author = string.IsNullOrWhiteSpace(authorDialog.NoteText)
            ? null
            : authorDialog.NoteText.Trim();
        try
        {
            var saved = await _libraryDatabase.UpsertBookAsync(book);
            if (_currentBook?.Id == saved.Id)
            {
                _currentBook = saved;
                DocumentTitleText.Text = saved.Title;
            }

            await RefreshLibraryItemsAsync();
            StatusText.Text = "已更新书名与作者";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法更新元数据", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void EditLibraryBookTags_Click(object sender, RoutedEventArgs e)
    {
        if (_libraryDatabase is null)
        {
            return;
        }

        var book = ResolveLibraryBookFromSender(sender);
        if (book is null)
        {
            return;
        }

        var dialog = new NoteDialog(
            selectedText: "多个标签用逗号分隔，例如：经典,科幻,待读",
            existingNote: book.Tags ?? string.Empty,
            color: "yellow",
            isEditing: true)
        {
            Owner = this,
            Title = "编辑标签"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        book.Tags = string.IsNullOrWhiteSpace(dialog.NoteText) ? string.Empty : dialog.NoteText.Trim();
        try
        {
            var saved = await _libraryDatabase.UpsertBookAsync(book);
            if (_currentBook?.Id == saved.Id)
            {
                _currentBook = saved;
            }

            await RefreshLibraryItemsAsync();
            StatusText.Text = string.IsNullOrWhiteSpace(saved.Tags)
                ? "已清除标签"
                : $"已更新标签：{saved.Tags}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法更新标签", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private LibraryBook? ResolveLibraryBookFromSender(object sender)
    {
        if (sender is MenuItem { CommandParameter: LibraryBook menuBook })
        {
            return menuBook;
        }

        return LibraryList.SelectedItem as LibraryBook;
    }

    private static string GetAnnotationDedupeKey(Annotation annotation)
    {
        var quote = (annotation.SelectedText ?? annotation.Anchor?.ExactText ?? string.Empty).Trim();
        var note = (annotation.Note ?? string.Empty).Trim();
        var color = (annotation.Color ?? string.Empty).Trim().ToLowerInvariant();
        return string.Join(
            "|",
            ((int)annotation.Type).ToString(),
            annotation.SectionIndex.ToString(),
            Math.Round(annotation.SectionProgress, 4).ToString("0.####"),
            quote,
            note,
            color);
    }

    private async void ExportAnnotations_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBook is null || _libraryDatabase is null)
        {
            return;
        }

        var annotations = await _libraryDatabase.ListAnnotationsAsync(_currentBook.Id);
        if (annotations.Count == 0)
        {
            StatusText.Text = "当前书籍还没有可导出的批注";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出书签与笔记",
            Filter = "Markdown 批注 (*.md)|*.md|NoGaReader JSON 批注 (*.json)|*.json",
            FilterIndex = 1,
            AddExtension = true,
            OverwritePrompt = true,
            FileName = _annotationExportService.CreateSuggestedFileName(
                _currentBook,
                AnnotationExportFormat.Markdown)
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var format = dialog.FilterIndex == 2
            ? AnnotationExportFormat.Json
            : AnnotationExportFormat.Markdown;
        var expectedExtension = format == AnnotationExportFormat.Json ? ".json" : ".md";
        var destinationPath = string.Equals(
            Path.GetExtension(dialog.FileName),
            expectedExtension,
            StringComparison.OrdinalIgnoreCase)
            ? dialog.FileName
            : Path.ChangeExtension(dialog.FileName, expectedExtension);
        if (!string.Equals(destinationPath, dialog.FileName, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(destinationPath) &&
            MessageBox.Show(
                this,
                $"{Path.GetFileName(destinationPath)} 已存在，是否覆盖？",
                "确认覆盖",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var path = await _annotationExportService.ExportToFileAsync(
                _currentBook,
                annotations,
                destinationPath,
                format);
            StatusText.Text = $"批注已导出：{Path.GetFileName(path)}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = "批注导出失败";
            MessageBox.Show(this, exception.Message, "无法导出批注", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void EditAnnotation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { CommandParameter: AnnotationListItem item })
        {
            return;
        }

        await EditAnnotationAsync(item);
    }

    private async void EditSelectedAnnotation_Click(object sender, RoutedEventArgs e)
    {
        if (AnnotationList.SelectedItem is AnnotationListItem item)
        {
            await EditAnnotationAsync(item);
        }
    }

    private async Task EditAnnotationAsync(AnnotationListItem item)
    {
        if (item.Value.Type != AnnotationType.Note || _libraryDatabase is null)
        {
            StatusText.Text = "这条标记没有笔记正文";
            return;
        }

        var annotation = item.Value;
        var dialog = new NoteDialog(
            annotation.SelectedText ?? string.Empty,
            annotation.Note,
            annotation.Color ?? "yellow",
            isEditing: true)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        annotation.Note = dialog.NoteText;
        annotation.Color = dialog.ColorKey;
        annotation.ModifiedUtc = DateTimeOffset.UtcNow;
        await _libraryDatabase.UpsertAnnotationAsync(annotation);
        await RefreshAnnotationsAsync();
        if (SupportsReaderRuntime)
        {
            await _readerController.ApplyAnnotationsAsync(GetCurrentSectionAnnotationsJson());
        }

        StatusText.Text = "笔记已更新";
    }

    private async void ChangeAnnotationColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem
            {
                Tag: string color,
                CommandParameter: AnnotationListItem item
            } ||
            item.Value.Type == AnnotationType.Bookmark ||
            _libraryDatabase is null)
        {
            StatusText.Text = "书签不需要高亮颜色";
            return;
        }

        item.Value.Color = color;
        item.Value.ModifiedUtc = DateTimeOffset.UtcNow;
        await _libraryDatabase.UpsertAnnotationAsync(item.Value);
        await RefreshAnnotationsAsync();
        if (SupportsReaderRuntime)
        {
            await _readerController.ApplyAnnotationsAsync(GetCurrentSectionAnnotationsJson());
        }

        StatusText.Text = "批注颜色已更新";
    }

    private async void SearchResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SearchResultsList.SelectedItem is not SearchResultListItem item || _session is null)
        {
            return;
        }

        var hit = item.Value;

        await CaptureReadingLocationAsync();
        _session.CurrentSectionIndex = Math.Clamp(hit.SectionIndex, 0, _session.Sections.Count - 1);
        _sectionProgress = Math.Clamp(hit.SectionProgress, 0, 1);
        _currentLocationAnchor = null;
        _pendingRestoreAnchor = null;
        _pendingSearchText = hit.MatchInTitle ? null : _lastSearchQuery;
        _pendingSearchOccurrenceIndex = hit.MatchInTitle ? 0 : Math.Max(0, hit.OccurrenceIndex);
        await NavigateToCurrentSectionAsync();
        StatusText.Text = hit.MatchInTitle
            ? $"已跳到标题匹配章节：{hit.SectionTitle}"
            : $"已跳到第 {hit.OccurrenceIndex + 1} 处：{hit.SectionTitle}";
    }

    private void CloseInspectorButton_Click(object sender, RoutedEventArgs e)
    {
        CloseInspectors();
    }

    private void ShowInspector(Border panel)
    {
        var panels = new[] { ReaderSettingsPanel, ComicPanel, AnnotationsPanel, SearchResultsPanel, ReaderTocPanel };
        foreach (var candidate in panels)
        {
            if (candidate == panel)
            {
                if (candidate == ReaderSettingsPanel)
                {
                    AnimateOverlayOpen(candidate);
                }
                else
                {
                    AnimateOverlayOpen(candidate, slideFromRight: true);
                }
            }
            else if (candidate.Visibility == Visibility.Visible || IsOverlayAnimating(candidate))
            {
                AnimateOverlayClose(candidate);
            }
        }
    }

    private void CloseInspectors()
    {
        AnimateOverlayClose(ReaderSettingsPanel);
        AnimateOverlayClose(ComicPanel);
        AnimateOverlayClose(AnnotationsPanel);
        AnimateOverlayClose(SearchResultsPanel);
        AnimateOverlayClose(ReaderTocPanel);
    }

    private bool IsOverlayOpen(UIElement element) =>
        element.Visibility == Visibility.Visible &&
        (!(_overlayAnimationTokens.TryGetValue(element, out var token)) || token >= 0);

    private bool IsOverlayAnimating(UIElement element) =>
        _overlayAnimationTokens.TryGetValue(element, out var token) && token != 0;

    private int NextOverlayToken(UIElement element, bool opening)
    {
        var token = opening
            ? Math.Abs(_overlayAnimationTokens.GetValueOrDefault(element)) + 1
            : -(Math.Abs(_overlayAnimationTokens.GetValueOrDefault(element)) + 1);
        _overlayAnimationTokens[element] = token;
        return token;
    }

    private void ClearOverlayAnimations(UIElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (element.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            element.RenderTransform = transform;
        }
        else
        {
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, null);
        }

        transform.X = 0;
        transform.Y = 0;
    }

    private void AnimateOverlayOpen(UIElement element, bool slideFromTop = false, bool slideFromRight = false)
    {
        var token = NextOverlayToken(element, opening: true);
        ClearOverlayAnimations(element);
        element.Visibility = Visibility.Visible;
        element.Opacity = 0;

        var openDuration = TryFindResource("OverlayOpenDuration") is Duration duration
            ? duration.TimeSpan
            : TimeSpan.FromMilliseconds(180);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var transform = (TranslateTransform)element.RenderTransform;

        var fade = new DoubleAnimation(0, 1, openDuration) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            if (_overlayAnimationTokens.GetValueOrDefault(element) != token)
            {
                return;
            }

            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            _overlayAnimationTokens[element] = 0;
        };
        element.BeginAnimation(UIElement.OpacityProperty, fade);

        if (slideFromTop)
        {
            transform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-10, 0, openDuration) { EasingFunction = ease });
        }
        else if (slideFromRight)
        {
            transform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(18, 0, openDuration) { EasingFunction = ease });
        }
        else
        {
            transform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(14, 0, openDuration) { EasingFunction = ease });
        }
    }

    private void AnimateOverlayClose(UIElement element, bool immediate = false)
    {
        if (element.Visibility != Visibility.Visible && !IsOverlayAnimating(element))
        {
            element.Visibility = Visibility.Collapsed;
            element.Opacity = 1;
            return;
        }

        var token = NextOverlayToken(element, opening: false);
        if (immediate)
        {
            ClearOverlayAnimations(element);
            element.Opacity = 1;
            element.Visibility = Visibility.Collapsed;
            _overlayAnimationTokens[element] = 0;
            return;
        }

        element.BeginAnimation(UIElement.OpacityProperty, null);
        var closeDuration = TryFindResource("OverlayCloseDuration") is Duration duration
            ? duration.TimeSpan
            : TimeSpan.FromMilliseconds(120);
        var fade = new DoubleAnimation(element.Opacity, 0, closeDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) =>
        {
            if (_overlayAnimationTokens.GetValueOrDefault(element) != token)
            {
                return;
            }

            ClearOverlayAnimations(element);
            element.Opacity = 1;
            element.Visibility = Visibility.Collapsed;
            _overlayAnimationTokens[element] = 0;
        };
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void AddLibraryFolder_Click(object sender, RoutedEventArgs e)
    {
        _ = RunGuardedUiActionAsync(AddLibraryFolderAsync, "无法添加书库文件夹");
    }

    private async Task AddLibraryFolderAsync()
    {
        if (_libraryDatabase is null)
        {
            StatusText.Text = "本地书库暂不可用";
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "选择本地书库文件夹",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var folder = await _libraryDatabase.UpsertLibraryFolderAsync(new LibraryFolder
        {
            Path = dialog.FolderName,
            Name = Path.GetFileName(dialog.FolderName.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name
                ? name
                : dialog.FolderName,
            IncludeSubfolders = true
        });
        await ScanLibraryFoldersAsync([folder]);
    }

    private void ManageLibraryFolders_Click(object sender, RoutedEventArgs e)
    {
        _ = RunGuardedUiActionAsync(ManageLibraryFoldersAsync, "无法更新书库文件夹");
    }

    private async Task ManageLibraryFoldersAsync()
    {
        if (_libraryDatabase is null)
        {
            StatusText.Text = "本地书库暂不可用";
            return;
        }

        var folders = await _libraryDatabase.ListLibraryFoldersAsync();
        var dialog = new LibraryFoldersDialog(folders) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { HasChanges: true } changes)
        {
            return;
        }

        foreach (var folder in changes.UpdatedFolders)
        {
            await _libraryDatabase.UpsertLibraryFolderAsync(folder);
        }

        foreach (var folderId in changes.RemovedFolderIds)
        {
            await _libraryDatabase.RemoveLibraryFolderAsync(folderId);
        }

        var remainingFolders = await _libraryDatabase.ListLibraryFoldersAsync();
        if (changes.UpdatedFolders.Count > 0 && remainingFolders.Count > 0)
        {
            await ScanLibraryFoldersAsync(remainingFolders);
        }
        else
        {
            StatusText.Text = changes.RemovedFolders.Count > 0
                ? $"已移除 {changes.RemovedFolders.Count} 个扫描文件夹，书籍记录与批注仍保留"
                : "书库文件夹设置已更新";
        }
    }

    private async void RefreshLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_libraryDatabase is null)
        {
            return;
        }

        var folders = await _libraryDatabase.ListLibraryFoldersAsync();
        if (folders.Count == 0)
        {
            await RefreshLibraryItemsAsync();
            StatusText.Text = "请先添加一个书库文件夹";
            return;
        }

        await ScanLibraryFoldersAsync(folders);
    }

    private async Task ScanLibraryFoldersAsync(IReadOnlyList<LibraryFolder> folders)
    {
        if (_libraryDatabase is null)
        {
            return;
        }

        _libraryScanCancellation?.Cancel();
        _libraryScanCancellation?.Dispose();
        _libraryScanCancellation = new CancellationTokenSource();
        var cancellationToken = _libraryScanCancellation.Token;
        SetLoading(true, "正在扫描本地书库…");
        try
        {
            var imported = 0;
            var issueCount = 0;
            var knownBooks = (await _libraryDatabase.ListBooksAsync(true, cancellationToken)).ToList();
            var knownBooksByPath = knownBooks.ToDictionary(
                book => Path.GetFullPath(book.Path),
                StringComparer.OrdinalIgnoreCase);
            var scanSnapshots = knownBooksByPath.ToDictionary(
                pair => pair.Key,
                pair => new LibraryScanSnapshot
                {
                    Path = pair.Key,
                    Title = pair.Value.Title,
                    Author = pair.Value.Author ?? string.Empty,
                    Format = pair.Value.Format,
                    FileSize = pair.Value.FileSize,
                    LastModifiedUtc = pair.Value.ModifiedUtc,
                    IsMissing = pair.Value.IsMissing
                },
                StringComparer.OrdinalIgnoreCase);
            var relocationLookup = knownBooks
                .Where(candidate =>
                    !File.Exists(candidate.Path) &&
                    !Directory.Exists(candidate.Path) &&
                    IsBookInsideAnyLibraryFolder(candidate.Path, folders))
                .GroupBy(candidate => CreateRelocationKey(
                    candidate.FileSize,
                    candidate.Format,
                    candidate.Title,
                    candidate.Author))
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
            var automaticallyRelocatedBookIds = new HashSet<long>();
            foreach (var folder in folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(folder.Path))
                {
                    issueCount++;
                    continue;
                }

                var progress = new Progress<LibraryScanProgress>(value =>
                {
                    StatusText.Text = $"扫描书库 · {value.ProcessedFileCount} / {Math.Max(value.CandidateFileCount, value.ProcessedFileCount)}";
                });
                var result = await _libraryScanner.ScanAsync(
                    folder.Path,
                    new LibraryScanOptions
                    {
                        IncludeSubfolders = folder.IncludeSubfolders,
                        KnownBooks = scanSnapshots,
                        PersistCoverImagesToCache = true
                    },
                    progress,
                    cancellationToken);
                issueCount += result.Issues.Count;
                foreach (var item in result.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (item.IsUnchanged)
                    {
                        imported++;
                        continue;
                    }

                    knownBooksByPath.TryGetValue(Path.GetFullPath(item.Path), out var existing);
                    if (existing is null && item.FileSize > 0)
                    {
                        var relocationKey = CreateRelocationKey(
                            item.FileSize,
                            item.Format,
                            item.Title,
                            item.Author);
                        var relocationCandidates = (relocationLookup.GetValueOrDefault(relocationKey) ?? [])
                            .Where(candidate => !automaticallyRelocatedBookIds.Contains(candidate.Id))
                            .Take(2)
                            .ToArray();
                        if (relocationCandidates.Length == 1)
                        {
                            existing = await _libraryDatabase.RelocateBookAsync(
                                relocationCandidates[0].Id,
                                item.Path,
                                cancellationToken);
                            automaticallyRelocatedBookIds.Add(existing.Id);
                            knownBooksByPath.Remove(Path.GetFullPath(relocationCandidates[0].Path));
                            knownBooksByPath[Path.GetFullPath(existing.Path)] = existing;
                        }
                    }

                    var coverPath = item.CoverPath ??
                                    await SaveLibraryCoverAsync(item, cancellationToken) ??
                                    existing?.CoverPath;
                    var saved = await _libraryDatabase.UpsertBookAsync(new LibraryBook
                    {
                        Path = item.Path,
                        Title = string.IsNullOrWhiteSpace(existing?.Title) ? item.Title : existing.Title,
                        Author = existing is null
                            ? string.IsNullOrWhiteSpace(item.Author) ? null : item.Author
                            : existing.Author,
                        Format = item.Format,
                        CoverPath = coverPath,
                        FileSize = item.FileSize,
                        ModifiedUtc = item.LastModifiedUtc,
                        AddedUtc = existing?.AddedUtc ?? DateTimeOffset.UtcNow,
                        LastOpenedUtc = existing?.LastOpenedUtc,
                        SectionCount = existing?.SectionCount ?? 1,
                        IsMissing = false,
                        Tags = existing?.Tags
                    }, cancellationToken);
                    var knownIndex = knownBooks.FindIndex(candidate => candidate.Id == saved.Id);
                    if (knownIndex >= 0)
                    {
                        knownBooks[knownIndex] = saved;
                    }
                    else
                    {
                        knownBooks.Add(saved);
                    }
                    knownBooksByPath[Path.GetFullPath(saved.Path)] = saved;
                    scanSnapshots[Path.GetFullPath(saved.Path)] = new LibraryScanSnapshot
                    {
                        Path = saved.Path,
                        Title = saved.Title,
                        Author = saved.Author ?? string.Empty,
                        Format = saved.Format,
                        FileSize = saved.FileSize,
                        LastModifiedUtc = saved.ModifiedUtc,
                        IsMissing = saved.IsMissing
                    };

                    imported++;
                }

                folder.LastScannedAt = DateTimeOffset.UtcNow;
                await _libraryDatabase.UpsertLibraryFolderAsync(folder, cancellationToken);
            }

            await RefreshMissingBookStatesAsync(cancellationToken);
            await RefreshLibraryItemsAsync(cancellationToken);
            StatusText.Text = issueCount == 0
                ? $"书库已更新 · {imported} 本书"
                : $"书库已更新 · {imported} 本书，跳过 {issueCount} 个异常项";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消书库扫描";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or SqliteException)
        {
            StatusText.Text = $"书库扫描失败：{exception.Message}";
        }
        finally
        {
            SetLoading(false);
        }
    }

    private static bool IsBookInsideAnyLibraryFolder(
        string bookPath,
        IReadOnlyList<LibraryFolder> folders)
    {
        var fullBookPath = Path.GetFullPath(bookPath);
        var bookDirectory = Path.GetDirectoryName(fullBookPath) ?? string.Empty;
        foreach (var folder in folders)
        {
            var fullFolderPath = Path.GetFullPath(folder.Path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (folder.IncludeSubfolders)
            {
                var prefix = fullFolderPath + Path.DirectorySeparatorChar;
                if (fullBookPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.Equals(bookDirectory, fullFolderPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string CreateRelocationKey(
        long fileSize,
        string format,
        string title,
        string? author)
    {
        return string.Join(
            '\u001f',
            fileSize.ToString(CultureInfo.InvariantCulture),
            format.ToUpper(CultureInfo.InvariantCulture),
            title.ToUpper(CultureInfo.CurrentCulture),
            (author ?? string.Empty).ToUpper(CultureInfo.CurrentCulture));
    }

    private async Task RefreshMissingBookStatesAsync(CancellationToken cancellationToken)
    {
        if (_libraryDatabase is null)
        {
            return;
        }

        foreach (var book in await _libraryDatabase.ListBooksAsync(true, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isMissing = !File.Exists(book.Path) && !Directory.Exists(book.Path);
            if (book.IsMissing == isMissing)
            {
                continue;
            }

            book.IsMissing = isMissing;
            await _libraryDatabase.UpsertBookAsync(book, cancellationToken);
        }
    }

    private static async Task<string?> SaveLibraryCoverAsync(
        LibraryScanItem item,
        CancellationToken cancellationToken)
    {
        if (item.CoverBytes is not { Length: > 0 } bytes || string.IsNullOrWhiteSpace(item.CoverExtension))
        {
            return null;
        }

        var directory = Path.Combine(AppPaths.CacheRoot, "library-covers");
        Directory.CreateDirectory(directory);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(item.Path).ToUpperInvariant())))[..24];
        var path = Path.Combine(directory, hash + item.CoverExtension.ToLowerInvariant());
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        return path;
    }

    private async Task<LibraryBook?> UpsertOpenedBookAsync(
        ReaderSession session,
        CancellationToken cancellationToken)
    {
        if (_libraryDatabase is null)
        {
            return null;
        }

        try
        {
            var info = new FileInfo(session.SourcePath);
            var directoryInfo = new DirectoryInfo(session.SourcePath);
            var isDirectory = directoryInfo.Exists;
            var existing = await _libraryDatabase.FindBookByPathAsync(session.SourcePath, cancellationToken);
            return await _libraryDatabase.UpsertBookAsync(new LibraryBook
            {
                Path = session.SourcePath,
                Title = string.IsNullOrWhiteSpace(existing?.Title) ? session.Title : existing.Title,
                Author = existing is null ? session.Author : existing.Author,
                Format = isDirectory ? "图片文件夹" : info.Extension.TrimStart('.').ToUpperInvariant(),
                CoverPath = session.CoverImagePath ?? existing?.CoverPath,
                FileSize = info.Exists ? info.Length : 0,
                ModifiedUtc = info.Exists
                    ? info.LastWriteTimeUtc
                    : isDirectory ? directoryInfo.LastWriteTimeUtc : null,
                AddedUtc = existing?.AddedUtc ?? DateTimeOffset.UtcNow,
                LastOpenedUtc = DateTimeOffset.UtcNow,
                SectionCount = session.Sections.Count,
                IsMissing = !info.Exists && !isDirectory,
                Tags = existing?.Tags
            }, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or SqliteException)
        {
            Debug.WriteLine(exception);
            StatusText.Text = "书库记录暂时无法保存，阅读不受影响";
            return null;
        }
    }

    private async Task RefreshLibraryItemsAsync(CancellationToken cancellationToken = default)
    {
        if (_libraryDatabase is null)
        {
            return;
        }

        try
        {
            var books = await _libraryDatabase.ListBooksAsync(true, cancellationToken);
            using (_libraryView.DeferRefresh())
            {
                _allLibraryBooks.Clear();
                _allLibraryBooks.AddRange(books);
                _libraryItems.Clear();
                foreach (var book in books)
                {
                    _libraryItems.Add(book);
                }
            }
            ApplyLibraryFilter();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or SqliteException)
        {
            Debug.WriteLine(exception);
            StatusText.Text = "本地书库列表暂不可用";
        }
    }

    private void LibraryFilter_Changed(object sender, RoutedEventArgs e)
    {
        ApplyLibraryFilter();
    }

    private void ApplyLibraryFilter()
    {
        if (_libraryView is null)
        {
            return;
        }

        _libraryView.Refresh();

        if (NoLibraryPanel is not null)
        {
            NoLibraryPanel.Visibility = _libraryView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        }
        if (LibraryCountText is not null)
        {
            LibraryCountText.Text = UiStrings.BooksCount(_allLibraryBooks.Count);
        }
        UpdateWelcomeDashboard();
    }

    private bool LibraryBookMatchesFilter(object item)
    {
        if (item is not LibraryBook book)
        {
            return false;
        }

        var query = LibraryFilterBox?.Text.Trim() ?? string.Empty;
        var format = (LibraryFormatFilter?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
        return (string.IsNullOrEmpty(query) ||
                book.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                (book.Author?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
                (book.Tags?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false)) &&
               (string.IsNullOrEmpty(format) ||
                format == "other" && book.Format is not ("EPUB" or "PDF") ||
                string.Equals(book.Format, format, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateLibraryItem(LibraryBook book)
    {
        var allIndex = _allLibraryBooks.FindIndex(candidate => candidate.Id == book.Id);
        if (allIndex >= 0)
        {
            _allLibraryBooks[allIndex] = book;
        }
        else
        {
            _allLibraryBooks.Insert(0, book);
        }

        var visibleIndex = _libraryItems
            .Select((candidate, index) => (candidate, index))
            .FirstOrDefault(item => item.candidate.Id == book.Id)
            .index;
        if (visibleIndex >= 0 &&
            visibleIndex < _libraryItems.Count &&
            _libraryItems[visibleIndex].Id == book.Id)
        {
            _libraryItems[visibleIndex] = book;
        }
        else
        {
            _libraryItems.Insert(0, book);
        }

        ApplyLibraryFilter();
    }

    private async void LibraryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LibraryList.SelectedItem is not LibraryBook book)
        {
            return;
        }

        if (!File.Exists(book.Path) && !Directory.Exists(book.Path))
        {
            await RelocateLibraryBookAsync(book);
            return;
        }

        await OpenFileAsync(book.Path);
    }

    private async void RelocateLibraryBook_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { CommandParameter: LibraryBook book })
        {
            await RelocateLibraryBookAsync(book);
        }
    }

    private async Task RelocateLibraryBookAsync(LibraryBook book)
    {
        if (_libraryDatabase is null)
        {
            return;
        }

        var previousDirectory = Path.GetDirectoryName(book.Path);
        var isComicFolder = string.Equals(book.Format, "图片文件夹", StringComparison.OrdinalIgnoreCase);
        string selectedPath;
        if (isComicFolder)
        {
            var folderDialog = new OpenFolderDialog
            {
                Title = $"重新定位《{book.Title}》的图片文件夹",
                Multiselect = false,
                InitialDirectory = previousDirectory is { Length: > 0 } && Directory.Exists(previousDirectory)
                    ? previousDirectory
                    : string.Empty
            };
            if (folderDialog.ShowDialog(this) != true)
            {
                return;
            }
            selectedPath = folderDialog.FolderName;
        }
        else
        {
            var fileDialog = new OpenFileDialog
            {
                Title = $"重新定位《{book.Title}》",
                Filter = DocumentLoader.OpenFileFilter,
                CheckFileExists = true,
                Multiselect = false,
                FileName = Path.GetFileName(book.Path)
            };
            if (previousDirectory is { Length: > 0 } && Directory.Exists(previousDirectory))
            {
                fileDialog.InitialDirectory = previousDirectory;
            }
            if (fileDialog.ShowDialog(this) != true)
            {
                return;
            }
            selectedPath = fileDialog.FileName;
        }

        if (!isComicFolder && !string.Equals(
                Path.GetExtension(selectedPath),
                Path.GetExtension(book.Path),
                StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "请选择与原书相同格式的文件，避免批注定位到错误内容。", "格式不一致", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var info = isComicFolder ? null : new FileInfo(selectedPath);
        if (info is not null && book.FileSize > 0 && info.Length != book.FileSize &&
            MessageBox.Show(
                this,
                "所选文件大小与原记录不同，可能不是同一本书。仍要保留原阅读位置和批注吗？",
                "确认重新定位",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var destinationRecord = await _libraryDatabase.FindBookByPathAsync(selectedPath);
            LibraryBook relocated;
            if (destinationRecord is not null && destinationRecord.Id != book.Id)
            {
                if (MessageBox.Show(
                        this,
                        "新位置已经作为另一条书库记录存在。是否合并两条记录，并保留原书的阅读位置和双方批注？",
                        "合并书库记录",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    return;
                }

                relocated = await _libraryDatabase.MergeBookRecordsAsync(
                    book.Id,
                    destinationRecord.Id,
                    selectedPath);
            }
            else
            {
                relocated = await _libraryDatabase.RelocateBookAsync(book.Id, selectedPath);
            }

            relocated.FileSize = info?.Length ?? 0;
            relocated.ModifiedUtc = info is not null
                ? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)
                : new DateTimeOffset(Directory.GetLastWriteTimeUtc(selectedPath), TimeSpan.Zero);
            relocated.IsMissing = false;
            await _libraryDatabase.UpsertBookAsync(relocated);
            await RefreshLibraryItemsAsync();
            StatusText.Text = "源文件已重新定位，阅读位置与批注已保留";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or SqliteException)
        {
            MessageBox.Show(this, exception.Message, "无法重新定位", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveLibraryBook_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { CommandParameter: LibraryBook book })
        {
            _ = RunGuardedUiActionAsync(() => RemoveLibraryBookAsync(book), "无法移除书库记录");
        }
    }

    private async Task RemoveLibraryBookAsync(LibraryBook book)
    {
        if (_libraryDatabase is null)
        {
            return;
        }

        if (MessageBox.Show(
                this,
                $"从书库移除《{book.Title}》？\n\n源文件不会删除，但该书的阅读位置、书签和笔记会一并移除。",
                "从书库移除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await _libraryDatabase.RemoveBookAsync(book.Id);
        if (_currentBook?.Id == book.Id)
        {
            _currentBook = null;
            _annotationItems.Clear();
        }

        await RefreshLibraryItemsAsync();
        StatusText.Text = "书库记录已移除，源文件未更改";
        UpdateReaderControls();
    }

    private async void RecentList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RecentList.SelectedItem is not RecentBook item)
        {
            return;
        }

        if (!File.Exists(item.Path) && !Directory.Exists(item.Path))
        {
            MessageBox.Show(this, "文件或图片文件夹已移动/删除，可以从右键菜单移除这条记录。", "找不到阅读来源", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await OpenFileAsync(item.Path);
    }

    private async void WelcomeContinueButton_Click(object sender, RoutedEventArgs e)
    {
        var recent = _recentItems.FirstOrDefault(item => File.Exists(item.Path) || Directory.Exists(item.Path));
        if (recent is not null)
        {
            await OpenFileAsync(recent.Path);
        }
    }

    private void RemoveRecent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { CommandParameter: RecentBook item })
        {
            _recentStore.RemoveInMemory(item.Path);
            ScheduleJsonSave();
            RefreshRecentItems();
        }
    }

    private void TocTabButton_Click(object sender, RoutedEventArgs e)
    {
        ShowTocPanel();
    }

    private void LibraryTabButton_Click(object sender, RoutedEventArgs e)
    {
        ShowLibraryPanel();
    }

    private void RecentTabButton_Click(object sender, RoutedEventArgs e)
    {
        ShowRecentPanel();
    }


    private void PrintDocumentButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady || ReaderView.CoreWebView2 is null)
        {
            StatusText.Text = "阅读组件尚未就绪，无法打印";
            return;
        }

        if (_session is null)
        {
            StatusText.Text = "请先打开要打印的内容";
            return;
        }

        try
        {
            ReaderView.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
            StatusText.Text = "已打开打印对话框";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法打印", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        if (_syncDialog is { IsLoaded: true })
        {
            _syncDialog.Activate();
            return;
        }

        var dialog = new SyncDialog(_settings, _settingsStore, _libraryDatabase)
        {
            Owner = this
        };
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(_syncDialog, dialog))
            {
                _syncDialog = null;
            }
        };
        _syncDialog = dialog;
        dialog.Show();
        StatusText.Text = "已打开云同步";
    }

    private void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        OpenConversionDialog();
    }

    private void OpenConversionDialog()
    {
        if (_conversionDialog is { IsLoaded: true })
        {
            _conversionDialog.Activate();
            return;
        }

        var dialog = new ConversionDialog(_settings, _settingsStore, _documentLoader.Converter)
        {
            Owner = this
        };
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(_conversionDialog, dialog))
            {
                _conversionDialog = null;
            }
        };
        _conversionDialog = dialog;
        dialog.Show();
        StatusText.Text = "已打开批量转换";
    }

    private void DocumentsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenDocumentEditor();
    }

    private void OpenDocumentEditor(string? path = null)
    {
        if (_documentEditorDialog is { IsLoaded: true })
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                _documentEditorDialog.OpenPath(path);
            }

            _documentEditorDialog.Activate();
            return;
        }

        var dialog = new DocumentEditorDialog(path)
        {
            Owner = this
        };
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(_documentEditorDialog, dialog))
            {
                _documentEditorDialog = null;
            }
        };
        _documentEditorDialog = dialog;
        dialog.Show();
        StatusText.Text = "已打开文档编辑器";
    }

    private void ShowTocPanel()
    {
        if (_isReaderWindow)
        {
            // Reader uses top-bar directory drawer instead of left rail.
            ShowInspector(ReaderTocPanel);
            return;
        }

        LibraryPanel.Visibility = Visibility.Collapsed;
        TocPanel.Visibility = Visibility.Visible;
        RecentPanel.Visibility = Visibility.Collapsed;
        SetNavigationTabState(LibraryTabButton, false);
        SetNavigationTabState(TocTabButton, true);
        SetNavigationTabState(RecentTabButton, false);
    }

    private void ShowLibraryPanel()
    {
        LibraryPanel.Visibility = Visibility.Visible;
        TocPanel.Visibility = Visibility.Collapsed;
        RecentPanel.Visibility = Visibility.Collapsed;
        SetNavigationTabState(LibraryTabButton, true);
        SetNavigationTabState(TocTabButton, false);
        SetNavigationTabState(RecentTabButton, false);
    }

    private void ShowRecentPanel()
    {
        LibraryPanel.Visibility = Visibility.Collapsed;
        TocPanel.Visibility = Visibility.Collapsed;
        RecentPanel.Visibility = Visibility.Visible;
        SetNavigationTabState(LibraryTabButton, false);
        SetNavigationTabState(TocTabButton, false);
        SetNavigationTabState(RecentTabButton, true);
    }

    private void SetNavigationTabState(Button button, bool selected)
    {
        button.Tag = selected ? "Selected" : null;
        button.ClearValue(BackgroundProperty);
        button.ClearValue(ForegroundProperty);
        button.ClearValue(FontWeightProperty);
    }

    private void CaptionMinButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CaptionMaxButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CaptionCloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        UpdateCaptionMaxGlyph();
    }

    private void UpdateCaptionMaxGlyph()
    {
        if (CaptionMaxGlyph is null || CaptionMaxButton is null)
        {
            return;
        }

        var maximized = WindowState == WindowState.Maximized;
        CaptionMaxGlyph.Text = maximized ? "\uE923" : "\uE922";
        CaptionMaxButton.ToolTip = maximized ? "还原" : "最大化";
    }

    public void OnApplicationThemeChanged()
    {
        if (!IsLoaded)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnApplicationThemeChanged);
            return;
        }

        App.ApplyWindowChromeTheme(this);
        UpdateReaderSurfaceColor();
        _ = RefreshReaderThemeAsync();
    }

    private async Task RefreshReaderThemeAsync()
    {
        if (!_isReaderWindow)
        {
            return;
        }

        // Wait briefly if WebView is still starting.
        for (var attempt = 0; attempt < 20 && (!_webViewReady || ReaderView.CoreWebView2 is null); attempt++)
        {
            await Task.Delay(50);
        }

        if (!_webViewReady || ReaderView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            var palette = ResolveReaderPalette();
            var dark = App.IsDarkTheme || _settings.ReaderTheme == ReaderThemeMode.Dark;
            if (_settings.ReaderTheme is ReaderThemeMode.Light or ReaderThemeMode.Paper)
            {
                dark = false;
            }

            try
            {
                ReaderView.DefaultBackgroundColor =
                    (System.Drawing.Color)System.Drawing.ColorTranslator.FromHtml(palette.Background);
            }
            catch
            {
                // ignore color parse issues
            }

            // 1) Direct force-override CSS (always wins over publisher styles).
            await _readerController.ApplyThemeColorsAsync(
                palette.Background,
                palette.Foreground,
                palette.Muted,
                palette.Link,
                palette.Rule,
                dark);

            // 2) Rebuild runtime styles for Auto so next navigation stays correct.
            if (SupportsReaderRuntime || IsComicSession)
            {
                await ApplyReaderExperienceAsync();
                await _readerController.ApplyThemeColorsAsync(
                    palette.Background,
                    palette.Foreground,
                    palette.Muted,
                    palette.Link,
                    palette.Rule,
                    dark);
            }

            StatusText.Text = _settings.ReaderTheme == ReaderThemeMode.Auto
                ? (App.IsDarkTheme ? "阅读背景已跟随：深色" : "阅读背景已跟随：浅色")
                : "阅读背景已更新";
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            StatusText.Text = "阅读背景同步失败";
        }
    }

    private (string Background, string Foreground, string Muted, string Link, string Rule) ResolveReaderPalette()
    {
        // Keep in sync with ReaderRuntime / app neutral stone palette.
        return _settings.ReaderTheme switch
        {
            ReaderThemeMode.Paper => ("#fbf7ed", "#37312a", "#746b60", "#57534e", "#d8cfbe"),
            ReaderThemeMode.Light => ("#ffffff", "#1c1917", "#78716c", "#57534e", "#e7e5e4"),
            ReaderThemeMode.Dark => ("#141414", "#f5f5f4", "#a8a29e", "#d6d3d1", "#3f3f3f"),
            _ => App.IsDarkTheme
                ? ("#1a1a1a", "#f5f5f4", "#a8a29e", "#d6d3d1", "#3f3f3f")
                : ("#faf9f7", "#1c1917", "#78716c", "#57534e", "#e7e5e4")
        };
    }


    private void App_ThemeChanged(object? sender, EventArgs e)
    {
        OnApplicationThemeChanged();
    }

    private void UiLanguageButton_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not ToggleButton { Tag: string value })
        {
            return;
        }

        SelectExclusiveToggle(value, LanguageSystemButton, LanguageZhButton, LanguageEnButton);
        _settings.UiLanguage = value;
        UiStrings.ApplyFromSettings(value);
        ScheduleJsonSave();
        ApplyLocalizedShellText();
        StatusText.Text = UiStrings.LanguageUpdated;
        UpdateReaderControls();
    }

    private void ApplyLocalizedShellText()
    {
        SetText(BrandSubtitleText, UiStrings.LocalReader);
        SetText(ImportBookText, UiStrings.ImportBook);
        SetText(ImportComicText, UiStrings.ImportComic);
        SetText(ReadingSpaceLabel, UiStrings.ReadingSpace);
        SetText(NavLibraryText, UiStrings.NavLibrary);
        SetText(NavReadingText, UiStrings.NavReading);
        SetText(NavRecentText, UiStrings.NavRecent);
        SetText(NavConvertText, UiStrings.NavConvert);
        SetText(NavDocumentsText, UiStrings.NavDocuments);
        SetText(NavNotesText, UiStrings.NavNotes);
        SetText(NavSettingsText, UiStrings.NavSettings);
        SetText(SystemSectionLabel, UiStrings.SystemSection);
        SetText(NavSyncText, UiStrings.CloudSync);

        SetText(LibraryTitleText, UiStrings.Library);
        SetText(LibrarySearchPlaceholder, UiStrings.SearchLibrary);
        SetText(EmptyLibraryText, UiStrings.EmptyLibrary);
        SetText(TocTitleText, UiStrings.Toc);
        SetText(TocHintText, UiStrings.CurrentChapters);
        SetText(NoTocText, UiStrings.EmptyToc);
        SetText(RecentTitleText, UiStrings.Recent);
        SetText(RecentHintText, UiStrings.RecentHint);
        SetText(EmptyRecentText, UiStrings.EmptyRecent);
        // Force list rebinding so ProgressText/LastOpenedText re-evaluate under new language.
        RefreshRecentItems();

        SetText(AppAppearanceLabel, UiStrings.AppAppearance);
        SetText(UiLanguageLabel, UiStrings.UiLanguage);
        SetText(LocalOnlyLabel, UiStrings.LocalOnly);
        LanguageSystemButton.Content = UiStrings.LangSystem;
        LanguageZhButton.Content = UiStrings.LangChinese;
        LanguageEnButton.Content = UiStrings.LangEnglish;

        SystemThemeButton.ToolTip = UiStrings.ThemeSystem;
        LightThemeButton.ToolTip = UiStrings.ThemeLight;
        DarkThemeButton.ToolTip = UiStrings.ThemeDark;

        PreviousChapterButton.ToolTip = UiStrings.PreviousChapter;
        NextChapterButton.ToolTip = UiStrings.NextChapter;
        PreviousButton.ToolTip = UiStrings.PreviousPage;
        NextButton.ToolTip = UiStrings.NextPage;
        PageJumpButton.ToolTip = UiStrings.JumpPage;
        SearchToggleButton.ToolTip = UiStrings.SearchBook;
        HighlightButton.ToolTip = UiStrings.Highlight;
        NoteButton.ToolTip = UiStrings.AddNote;
        BookmarkButton.ToolTip = UiStrings.Bookmark;
        AnnotationsButton.ToolTip = UiStrings.NotesPanel;
        ComicModeButton.ToolTip = UiStrings.ComicMode;
        ReaderSettingsButton.ToolTip = UiStrings.ReaderSettings;
        SearchActionButton.Content = UiStrings.Search;

        SetText(WelcomeBackText, UiStrings.WelcomeBack);
        SetText(WelcomePromptText, UiStrings.WelcomePrompt);
        SetText(WelcomeHintText, UiStrings.WelcomeHint);
        SetText(WelcomeTotalReadingLabel, UiStrings.TotalReading);
        SetText(WelcomeLocalBooksLabel, UiStrings.LocalBooks);
        SetText(WelcomeReadingDaysLabel, UiStrings.ReadingDays);
        SetText(ContinueReadingLabel, UiStrings.ContinueReading);
        SetText(StartReadingText, UiStrings.StartReading);
        SetText(StartReadingHintText, UiStrings.StartReadingHint);
        SetText(ShortcutHintText, UiStrings.ShortcutHint);
        if (WelcomeSelectFileButton is not null)
        {
            WelcomeSelectFileButton.Content = UiStrings.IsEnglish ? "Choose file" : "选择文件";
        }

        if (WelcomeOpenComicFolderButton is not null)
        {
            WelcomeOpenComicFolderButton.Content = UiStrings.IsEnglish ? "Image folder" : "图片文件夹";
        }

        SetText(ReaderSettingsTitleText, UiStrings.ReaderSettings);
        SetText(ReadingBackgroundLabel, UiStrings.ReadingBackground);
        SetText(ReadingModeLabel, UiStrings.ReadingMode);
        SetText(FontSizeLabel, UiStrings.FontSize);
        SetText(LineHeightLabel, UiStrings.LineHeight);
        SetText(ContentWidthLabel, UiStrings.ContentWidth);
        SetText(ReaderThemeHintText, UiStrings.ReaderThemeHint);
        SetText(NotesTitleText, UiStrings.NotesTitle);
        SetText(NotesHintText, UiStrings.NotesHint);
        if (ImportAnnotationsButton is not null)
        {
            ImportAnnotationsButton.ToolTip = UiStrings.ImportAnnotations;
        }

        if (CurrentSectionText is not null &&
            (CurrentSectionText.Text is "本地阅读器" or "Local reader"))
        {
            CurrentSectionText.Text = UiStrings.LocalReaderSubtitle;
        }

        if (StatusText is not null)
        {
            StatusText.Text = UiStrings.Ready;
        }

        if (LibraryCountText is not null)
        {
            LibraryCountText.Text = UiStrings.BooksCount(_allLibraryBooks.Count);
        }

        if (!_isReaderWindow)
        {
            Title = UiStrings.ConsoleTitle;
        }
        else if (_session is null)
        {
            Title = UiStrings.ReaderTitle;
        }

        UpdateWelcomeDashboard();
    }

    private static void SetText(TextBlock? block, string value)
    {
        if (block is not null)
        {
            block.Text = value;
        }
    }

    private void AppThemeButton_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string value } ||
            !Enum.TryParse<AppThemeMode>(value, out var mode))
        {
            return;
        }

        SelectExclusiveToggle(value, SystemThemeButton, LightThemeButton, DarkThemeButton);
        _settings.Theme = mode;
        App.ApplyTheme(mode);
        // ApplyTheme notifies all windows; also ensure this shell chrome is current.
        App.ApplyWindowChromeTheme(this);
        ScheduleJsonSave();
        if (LibraryPanel.Visibility == Visibility.Visible)
        {
            ShowLibraryPanel();
        }
        else if (TocPanel.Visibility == Visibility.Visible)
        {
            ShowTocPanel();
        }
        else
        {
            ShowRecentPanel();
        }
        ScheduleJsonSave();
        UpdateReaderSurfaceColor();
        if (_settings.ReaderTheme == ReaderThemeMode.Auto)
        {
            ScheduleReaderAppearanceUpdate();
        }
    }

    private void ReaderThemeButton_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not ToggleButton { IsChecked: true, Tag: string value } ||
            !Enum.TryParse<ReaderThemeMode>(value, out var mode))
        {
            return;
        }

        SelectExclusiveToggle(value, AutoReaderThemeButton, PaperReaderThemeButton, LightReaderThemeButton, DarkReaderThemeButton);
        _settings.ReaderTheme = mode;
        UpdateReaderSurfaceColor();
        ScheduleReaderAppearanceUpdate();
    }

    private void ReaderFlowButton_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not ToggleButton { IsChecked: true, Tag: string value } ||
            !Enum.TryParse<ReaderFlowMode>(value, out var mode))
        {
            return;
        }

        SelectExclusiveToggle(value, PagedFlowButton, ScrollingFlowButton);
        _settings.ReaderFlow = mode;
        ScheduleReaderAppearanceUpdate();
    }

    private void ReaderSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing)
        {
            return;
        }

        _settings.ReaderFontSize = (int)Math.Round(FontSizeSlider.Value);
        _settings.ReaderLineHeight = Math.Round(LineHeightSlider.Value, 2);
        _settings.ReaderContentWidth = (int)Math.Round(ContentWidthSlider.Value);
        UpdateReaderSettingsReadout();
        ScheduleReaderAppearanceUpdate();
    }

    private void UpdateReaderSettingsReadout()
    {
        FontSizeValueText.Text = $"{_settings.ReaderFontSize} px";
        LineHeightValueText.Text = _settings.ReaderLineHeight.ToString("0.00");
        ContentWidthValueText.Text = $"{_settings.ReaderContentWidth} px";
    }

    private void ComicDisplayButton_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not ToggleButton { IsChecked: true, Tag: string value } ||
            !Enum.TryParse<ComicDisplayMode>(value, out var mode))
        {
            return;
        }

        SelectExclusiveToggle(value, ComicSingleButton, ComicDoubleButton, ComicContinuousButton);
        _settings.ComicDisplay = mode;
        _ = ApplyComicSettingsAsync();
    }

    private void ComicDirectionButton_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not ToggleButton { IsChecked: true, Tag: string value } ||
            !Enum.TryParse<ComicReadingDirection>(value, out var direction))
        {
            return;
        }

        SelectExclusiveToggle(value, ComicLtrButton, ComicRtlButton);
        _settings.ComicDirection = direction;
        _ = ApplyComicSettingsAsync();
    }

    private void ComicFitButton_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not ToggleButton { IsChecked: true, Tag: string value } ||
            !Enum.TryParse<ComicFitMode>(value, out var fit))
        {
            return;
        }

        SelectExclusiveToggle(value, ComicFitWidthButton, ComicFitHeightButton, ComicFitOriginalButton);
        _settings.ComicFit = fit;
        _ = ApplyComicSettingsAsync();
    }

    private void ComicCoverSingleCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        _settings.ComicCoverSinglePage = ComicCoverSingleCheckBox.IsChecked == true;
        _ = ApplyComicSettingsAsync();
    }

    private void ComicScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing)
        {
            return;
        }

        _settings.ComicScale = Math.Round(Math.Clamp(ComicScaleSlider.Value / 100, 0.5, 3.0), 2);
        UpdateComicSettingsReadout();
        _ = ApplyComicScaleAsync();
    }

    private void UpdateComicSettingsReadout()
    {
        ComicScaleValueText.Text = $"{_settings.ComicScale:P0}";
    }

    private async Task ApplyComicSettingsAsync()
    {
        ScheduleJsonSave();
        UpdateComicSettingsReadout();
        UpdateReaderControls();
        if (!IsComicSession || !_comicReady)
        {
            return;
        }

        try
        {
            await _comicController.ApplySettingsAsync(_settings);
            StatusText.Text = $"漫画模式 · {GetComicDisplayText()} · {GetComicDirectionText()}";
        }
        catch (InvalidOperationException)
        {
            // A navigation can replace the viewer while a setting is being applied.
        }
    }

    private async Task ApplyComicScaleAsync()
    {
        ScheduleJsonSave();
        UpdateComicSettingsReadout();
        UpdateReaderControls();
        if (!IsComicSession || !_comicReady)
        {
            return;
        }

        try
        {
            _ = await _comicController.SetScaleAsync(_settings.ComicScale);
        }
        catch (InvalidOperationException)
        {
            // A navigation can replace the viewer while a scale is being applied.
        }
    }

    private void ComicModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsComicSession)
        {
            return;
        }

        if (IsOverlayOpen(ComicPanel))
        {
            AnimateOverlayClose(ComicPanel);
        }
        else
        {
            ShowInspector(ComicPanel);
            if (ComicPageList.SelectedItem is not null)
            {
                ComicPageList.ScrollIntoView(ComicPageList.SelectedItem);
            }
        }
    }

    private async void ComicPageList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!IsComicSession || !_comicReady || ComicPageList.SelectedItem is not ComicPageListItem item)
        {
            return;
        }

        try
        {
            _ = await _comicController.GoToPageAsync(item.Value.Index);
        }
        catch (InvalidOperationException)
        {
            // The comic view changed before the click was handled.
        }
    }

    private void ResetReaderSettings_Click(object sender, RoutedEventArgs e)
    {
        _settings.ReaderTheme = ReaderThemeMode.Auto;
        _settings.ReaderFlow = ReaderFlowMode.Paged;
        _settings.ReaderFontSize = 18;
        _settings.ReaderLineHeight = 1.9;
        _settings.ReaderContentWidth = 720;
        _settings.UsePublisherFont = true;

        _initializing = true;
        InitializeSettingsControls();
        _initializing = false;
        ScheduleReaderAppearanceUpdate();
    }

    private void ScheduleReaderAppearanceUpdate()
    {
        ScheduleJsonSave();
        _appearanceCancellation?.Cancel();
        _appearanceCancellation?.Dispose();
        _appearanceCancellation = new CancellationTokenSource();
        _ = ApplyReaderAppearanceAfterDelayAsync(_appearanceCancellation.Token);
    }

    private async Task ApplyReaderAppearanceAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(110, cancellationToken);
            await ApplyReaderExperienceAsync();
            UpdateReaderControls();
        }
        catch (OperationCanceledException)
        {
            // A newer appearance value superseded this one.
        }
    }

    private void ReaderSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // Always open a real Window so the settings UI is never covered by WebView2.
        AnimateOverlayClose(ReaderSettingsPanel, immediate: true);
        OpenReaderSettingsDialog();
        if (!SupportsReaderRuntime)
        {
            StatusText.Text = _session is null
                ? "阅读设置已打开；打开 EPUB/Markdown/文本后会立即生效"
                : "当前格式不支持实时排版调整，设置会保存供下次使用";
        }
    }

    private void OpenReaderSettingsDialog()
    {
        if (_readerSettingsDialog is { IsLoaded: true })
        {
            _readerSettingsDialog.ReloadFromSettings();
            _readerSettingsDialog.Activate();
            return;
        }

        var dialog = new ReaderSettingsDialog(_settings)
        {
            Owner = this
        };
        dialog.SettingsChanged += ReaderSettingsDialog_SettingsChanged;
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(_readerSettingsDialog, dialog))
            {
                _readerSettingsDialog = null;
            }
        };
        _readerSettingsDialog = dialog;
        dialog.Show();
        StatusText.Text = "已打开阅读设置";
    }

    private void ReaderSettingsDialog_SettingsChanged(object? sender, EventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        // Keep the hidden panel controls in sync for any code still reading them.
        _initializing = true;
        try
        {
            SelectExclusiveToggle(
                _settings.ReaderTheme.ToString(),
                AutoReaderThemeButton,
                PaperReaderThemeButton,
                LightReaderThemeButton,
                DarkReaderThemeButton);
            SelectExclusiveToggle(
                _settings.ReaderFlow.ToString(),
                PagedFlowButton,
                ScrollingFlowButton);
            FontSizeSlider.Value = _settings.ReaderFontSize;
            LineHeightSlider.Value = _settings.ReaderLineHeight;
            ContentWidthSlider.Value = _settings.ReaderContentWidth;
            UpdateReaderSettingsReadout();
        }
        finally
        {
            _initializing = false;
        }

        ScheduleJsonSave();
        UpdateReaderSurfaceColor();
        ScheduleReaderAppearanceUpdate();
        UpdateReaderControls();
    }

    private static void SelectExclusiveToggle(string value, params ToggleButton[] buttons)
    {
        foreach (var button in buttons)
        {
            button.IsChecked = string.Equals(button.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void SidebarToggleButton_Click(object sender, RoutedEventArgs e)
    {
        SetSidebarVisible(!_sidebarVisible);
    }

    private void SetSidebarVisible(bool visible)
    {
        _sidebarVisible = visible;
        SidebarColumn.Width = new GridLength(visible ? SidebarWidth : 0);
        Sidebar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FullScreenButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleFullScreen();
    }

    private void ToggleFullScreen()
    {
        if (!_isFullScreen)
        {
            _previousWindowStyle = WindowStyle;
            _previousResizeMode = ResizeMode;
            _previousWindowState = WindowState;
            _sidebarVisibleBeforeFullScreen = _sidebarVisible;
            SetSidebarVisible(false);
            TitleBar.Visibility = Visibility.Collapsed;
            TitleBarRow.Height = new GridLength(0);
            TopBar.Visibility = Visibility.Collapsed;
            StatusBar.Visibility = Visibility.Collapsed;
            TopBarRow.Height = new GridLength(0);
            StatusBarRow.Height = new GridLength(0);
            CloseInspectors();
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            _isFullScreen = true;
        }
        else
        {
            WindowStyle = _previousWindowStyle;
            ResizeMode = _previousResizeMode;
            WindowState = _previousWindowState;
            TitleBar.Visibility = Visibility.Visible;
            TitleBarRow.Height = new GridLength(40);
            TopBar.Visibility = Visibility.Visible;
            StatusBar.Visibility = Visibility.Visible;
            TopBarRow.Height = new GridLength(64);
            StatusBarRow.Height = new GridLength(36);
            SetSidebarVisible(_sidebarVisibleBeforeFullScreen);
            _isFullScreen = false;
            UpdateCaptionMaxGlyph();
        }

        _ = ApplyReaderExperienceAsync();
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.O)
        {
            e.Handled = true;
            OpenButton_Click(OpenButton, new RoutedEventArgs());
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.F &&
            (_session?.SupportsInPageSearch == true ||
             _session?.Kind is ReaderDocumentKind.Pdf or ReaderDocumentKind.Html or ReaderDocumentKind.Image))
        {
            e.Handled = true;
            ShowSearch();
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key is Key.Add or Key.OemPlus)
        {
            e.Handled = true;
            ChangeZoom(0.1);
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key is Key.Subtract or Key.OemMinus)
        {
            e.Handled = true;
            ChangeZoom(-0.1);
            return;
        }

        var deferFixedLayoutNavigation =
            _session is { } session &&
            !SupportsReaderRuntime &&
            !IsComicSession &&
            _pageCount <= 0 &&
            session.Sections.Count <= 1 &&
            session.Kind is ReaderDocumentKind.Pdf or ReaderDocumentKind.Image or ReaderDocumentKind.Html;

        switch (e.Key)
        {
            case Key.Left when !deferFixedLayoutNavigation && !SearchBox.IsKeyboardFocusWithin && !PageJumpBox.IsKeyboardFocusWithin:
            case Key.PageUp when !deferFixedLayoutNavigation && !SearchBox.IsKeyboardFocusWithin && !PageJumpBox.IsKeyboardFocusWithin:
                e.Handled = true;
                await TurnPageOrMoveSectionAsync(IsComicRightToLeft ? 1 : -1);
                break;
            case Key.Right when !deferFixedLayoutNavigation && !SearchBox.IsKeyboardFocusWithin && !PageJumpBox.IsKeyboardFocusWithin:
            case Key.PageDown when !deferFixedLayoutNavigation && !SearchBox.IsKeyboardFocusWithin && !PageJumpBox.IsKeyboardFocusWithin:
            case Key.Space when !deferFixedLayoutNavigation && !SearchBox.IsKeyboardFocusWithin && !PageJumpBox.IsKeyboardFocusWithin:
                e.Handled = true;
                await TurnPageOrMoveSectionAsync(IsComicRightToLeft ? -1 : 1);
                break;
            case Key.Home when !deferFixedLayoutNavigation && !SearchBox.IsKeyboardFocusWithin && !PageJumpBox.IsKeyboardFocusWithin:
                e.Handled = true;
                await GoToDocumentBoundaryAsync(toEnd: false);
                break;
            case Key.End when !deferFixedLayoutNavigation && !SearchBox.IsKeyboardFocusWithin && !PageJumpBox.IsKeyboardFocusWithin:
                e.Handled = true;
                await GoToDocumentBoundaryAsync(toEnd: true);
                break;
            case Key.G when Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !SearchBox.IsKeyboardFocusWithin:
                e.Handled = true;
                PageJumpBox.Focus();
                PageJumpBox.SelectAll();
                break;
            case Key.D0 when Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
                             _session?.Kind is ReaderDocumentKind.Pdf or ReaderDocumentKind.Image:
                e.Handled = true;
                _zoomFactor = 1.0;
                if (_webViewReady)
                {
                    ReaderView.ZoomFactor = _zoomFactor;
                }
                UpdateReaderControls();
                SaveReadingState();
                StatusText.Text = "已恢复 100% 缩放";
                break;
            case Key.D1 when Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
                             _session?.Kind is ReaderDocumentKind.Pdf or ReaderDocumentKind.Image:
                e.Handled = true;
                // Approximate "fit width" for fixed-layout viewers.
                _zoomFactor = 1.15;
                if (_webViewReady)
                {
                    ReaderView.ZoomFactor = _zoomFactor;
                }
                UpdateReaderControls();
                SaveReadingState();
                StatusText.Text = "已切换到适合宽度（近似）";
                break;
            case Key.F11:
                e.Handled = true;
                ToggleFullScreen();
                break;
            case Key.Escape when _isFullScreen:
                e.Handled = true;
                ToggleFullScreen();
                break;
            case Key.Escape when IsSearchOpen():
                e.Handled = true;
                HideSearch();
                break;
            case Key.Escape when IsOverlayOpen(ReaderSettingsPanel) ||
                                      IsOverlayOpen(ComicPanel) ||
                                      IsOverlayOpen(AnnotationsPanel) ||
                                      IsOverlayOpen(SearchResultsPanel):
                e.Handled = true;
                CloseInspectors();
                break;
        }
    }

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return;
        }

        var source = files.FirstOrDefault(path => File.Exists(path) || Directory.Exists(path));
        if (source is not null)
        {
            await OpenFileAsync(source);
        }
    }

    private void UpdateReaderControls()
    {
        var hasSession = _session is not null;
        var customNavigation = hasSession && _session!.Kind is not (ReaderDocumentKind.Pdf or ReaderDocumentKind.Image or ReaderDocumentKind.Html);
        var canPrevious = customNavigation &&
                          (_session!.CurrentSectionIndex > 0 || SupportsReaderRuntime && _sectionProgress > 0.001);
        var canNext = customNavigation &&
                      (_session!.CurrentSectionIndex < _session.Sections.Count - 1 || SupportsReaderRuntime && _sectionProgress < 0.999);

        PreviousButton.IsEnabled = canPrevious;
        NextButton.IsEnabled = canNext;
        PagePreviousButton.IsEnabled = IsComicRightToLeft ? canNext : canPrevious;
        PageNextButton.IsEnabled = IsComicRightToLeft ? canPrevious : canNext;
        PagePreviousButton.ToolTip = IsComicRightToLeft ? "下一页" : "上一页";
        PageNextButton.ToolTip = IsComicRightToLeft ? "上一页" : "下一页";
        System.Windows.Automation.AutomationProperties.SetName(
            PagePreviousButton,
            IsComicRightToLeft ? "左侧翻到下一页" : "左侧翻到上一页");
        System.Windows.Automation.AutomationProperties.SetName(
            PageNextButton,
            IsComicRightToLeft ? "右侧翻到上一页" : "右侧翻到下一页");

        var sectionCount = _session?.Sections.Count ?? 0;
        var multiSection = hasSession && sectionCount > 1 && !IsComicSession &&
                           _session!.Kind is not (ReaderDocumentKind.Pdf or ReaderDocumentKind.Image or ReaderDocumentKind.Html);
        var currentSectionIndex = _session?.CurrentSectionIndex ?? 0;
        var canPreviousChapter = multiSection && currentSectionIndex > 0;
        var canNextChapter = multiSection && currentSectionIndex < sectionCount - 1;
        PreviousChapterButton.Visibility = multiSection ? Visibility.Visible : Visibility.Collapsed;
        NextChapterButton.Visibility = multiSection ? Visibility.Visible : Visibility.Collapsed;
        PreviousChapterButton.IsEnabled = canPreviousChapter;
        NextChapterButton.IsEnabled = canNextChapter;
        PreviousChapterButton.Opacity = canPreviousChapter ? 1 : 0.35;
        NextChapterButton.Opacity = canNextChapter ? 1 : 0.35;

        var canJumpPage = hasSession && (
            IsComicSession ||
            SupportsReaderRuntime ||
            _session!.Kind is ReaderDocumentKind.Pdf or ReaderDocumentKind.Image ||
            sectionCount > 1);
        PageJumpBox.IsEnabled = canJumpPage;
        PageJumpButton.IsEnabled = canJumpPage;
        PageJumpBox.Visibility = canJumpPage ? Visibility.Visible : Visibility.Collapsed;
        PageJumpButton.Visibility = canJumpPage ? Visibility.Visible : Visibility.Collapsed;
        if (canJumpPage)
        {
            if (IsComicSession)
            {
                PageJumpBox.Text = Math.Max(1, _currentPage).ToString();
                PageJumpBox.ToolTip = "跳转到漫画页码";
            }
            else if (_pageCount > 0)
            {
                PageJumpBox.Text = Math.Max(1, _currentPage).ToString();
                PageJumpBox.ToolTip = "跳转到本章页码";
            }
            else if (sectionCount > 1)
            {
                PageJumpBox.Text = (currentSectionIndex + 1).ToString();
                PageJumpBox.ToolTip = "跳转到章节序号";
            }
            else if (_session!.Kind == ReaderDocumentKind.Pdf)
            {
                PageJumpBox.ToolTip = "跳转到 PDF 页码";
            }
        }

        var canSearch = _session?.SupportsInPageSearch == true ||
                        _session?.Kind is ReaderDocumentKind.Pdf or ReaderDocumentKind.Html or ReaderDocumentKind.Image;
        SearchToggleButton.IsEnabled = canSearch;
        if (TocToggleButton is not null)
        {
            TocToggleButton.IsEnabled = _session is not null && !IsComicSession;
            TocToggleButton.Visibility = _isReaderWindow ? Visibility.Visible : Visibility.Collapsed;
        }
        SearchActionButton.IsEnabled = canSearch;
        ReaderSettingsButton.IsEnabled = SupportsReaderRuntime;
        ComicModeButton.Visibility = IsComicSession ? Visibility.Visible : Visibility.Collapsed;
        ComicModeButton.IsEnabled = IsComicSession;
        HighlightButton.IsEnabled = SupportsReaderRuntime && _pendingSelection is not null && _libraryDatabase is not null;
        NoteButton.IsEnabled = SupportsReaderRuntime && _pendingSelection is not null && _libraryDatabase is not null;
        BookmarkButton.IsEnabled = hasSession && _currentBook is not null && _libraryDatabase is not null;
        AnnotationsButton.IsEnabled = hasSession && _currentBook is not null && _libraryDatabase is not null;

        var showPageTurnButtons = customNavigation &&
                                  (!SupportsReaderRuntime || _settings.ReaderFlow == ReaderFlowMode.Paged) &&
                                  (!IsComicSession || _settings.ComicDisplay != ComicDisplayMode.Continuous);
        var pageControlsVisibility = showPageTurnButtons ? Visibility.Visible : Visibility.Collapsed;
        PagePreviousButton.Visibility = pageControlsVisibility;
        PageNextButton.Visibility = pageControlsVisibility;
        PreviousPageColumn.Width = new GridLength(showPageTurnButtons ? 56 : 0);
        NextPageColumn.Width = new GridLength(showPageTurnButtons ? 56 : 0);
        UpdateAnnotationVisualState();

        if (_session is null)
        {
            ReadingProgressBar.Value = 0;
            ProgressText.Text = "等待打开文件";
            return;
        }

        CurrentSectionText.Text = _session.CurrentSection.Title;
        var total = Math.Max(1, _session.Sections.Count);
        var wholeBookProgress = IsComicSession
            ? Math.Clamp((_session.CurrentSectionIndex + 1d) / total, 0, 1)
            : Math.Clamp((_session.CurrentSectionIndex + _sectionProgress) / total, 0, 1);
        ReadingProgressBar.Value = wholeBookProgress;

        if (IsComicSession)
        {
            ProgressText.Text = $"{wholeBookProgress:P0}  ·  {_currentPage} / {_pageCount} 页  ·  {_settings.ComicScale:P0}";
        }
        else if (_pageCount > 0 && _session.Sections.Count > 1)
        {
            ProgressText.Text = $"{wholeBookProgress:P0}  ·  章 {_session.CurrentSectionIndex + 1}/{_session.Sections.Count}  ·  页 {_currentPage}/{_pageCount}";
        }
        else if (_pageCount > 0)
        {
            ProgressText.Text = $"{wholeBookProgress:P0}  ·  页 {_currentPage} / {_pageCount}";
        }
        else if (_session.Sections.Count > 1)
        {
            ProgressText.Text = $"{wholeBookProgress:P0}  ·  章 {_session.CurrentSectionIndex + 1} / {_session.Sections.Count}";
        }
        else if (_session.IsReflowable)
        {
            ProgressText.Text = $"{wholeBookProgress:P0}";
        }
        else if (_session.Kind == ReaderDocumentKind.Pdf)
        {
            ProgressText.Text = _currentPage > 0
                ? $"PDF · 第 {_currentPage} 页  ·  缩放 {_zoomFactor:P0}"
                : $"PDF · 缩放 {_zoomFactor:P0}";
        }
        else
        {
            ProgressText.Text = $"缩放 {_zoomFactor:P0}";
        }
    }

    private void UpdateAnnotationVisualState()
    {
        var isBookmarked = _session is not null && _annotationItems.Any(item =>
            item.Value.Type == AnnotationType.Bookmark &&
            item.Value.SectionIndex == _session.CurrentSectionIndex &&
            Math.Abs(item.Value.SectionProgress - _sectionProgress) < 0.012);
        BookmarkButton.Background = isBookmarked
            ? (Brush)FindResource("PrimarySoftBrush")
            : Brushes.Transparent;
        BookmarkButton.Foreground = isBookmarked
            ? (Brush)FindResource("PrimaryBrush")
            : (Brush)FindResource("TextBrush");
        BookmarkGlyph.Text = isBookmarked ? "\uE735" : "\uE734";
        BookmarkButton.ToolTip = isBookmarked ? "移除当前位置书签" : "添加当前位置书签";
        System.Windows.Automation.AutomationProperties.SetName(
            BookmarkButton,
            isBookmarked ? "移除当前位置书签" : "在当前位置添加书签");

        var annotationCount = _annotationItems.Count;
        AnnotationBadge.Visibility = annotationCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        AnnotationBadgeText.Text = annotationCount > 99 ? "99+" : annotationCount.ToString();
        AnnotationsButton.ToolTip = annotationCount > 0
            ? $"书签与笔记（{annotationCount} 条）"
            : "书签与笔记";
    }

    private void RefreshRecentItems()
    {
        _recentItems.Clear();
        foreach (var item in _recentStore.Load())
        {
            _recentItems.Add(item);
        }

        if (NoRecentPanel is not null)
        {
            NoRecentPanel.Visibility = _recentItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // Refresh bound list so language-dependent display strings update immediately.
        if (RecentList is not null)
        {
            RecentList.Items.Refresh();
        }
        UpdateWelcomeDashboard();
    }

    private void UpdateWelcomeDashboard()
    {
        if (WelcomeReadingTimeText is null)
        {
            return;
        }

        var totalMinutes = Math.Max(0, (int)Math.Floor(_settings.TotalReadingSeconds / 60));
        WelcomeReadingTimeText.Text = totalMinutes < 60
            ? UiStrings.Minutes(totalMinutes)
            : (UiStrings.IsEnglish
                ? $"{totalMinutes / 60}h {totalMinutes % 60}m"
                : $"{totalMinutes / 60} 小时 {totalMinutes % 60} 分");
        WelcomeBookCountText.Text = UiStrings.BooksShort(_allLibraryBooks.Count);
        WelcomeReadingDaysText.Text = UiStrings.Days(_settings.ReadingDates.Count);

        var recent = _recentItems.FirstOrDefault(item => File.Exists(item.Path) || Directory.Exists(item.Path));
        WelcomeRecentCard.Visibility = recent is null ? Visibility.Collapsed : Visibility.Visible;
        if (recent is not null)
        {
            WelcomeRecentTitleText.Text = recent.Title;
            WelcomeRecentProgressText.Text = $"{recent.ProgressText} · {recent.LastOpenedText}";
        }
    }

    private void MainWindow_Activated(object? sender, EventArgs e)
    {
        BeginReadingTracking();
    }

    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        FlushActiveReadingTime();
    }

    private async void ReadingStatsTimer_Tick(object? sender, EventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        if (_session?.Kind == ReaderDocumentKind.Pdf)
        {
            await SyncPdfLocationAsync();
            if (_isClosing)
            {
                return;
            }

            SaveReadingState();
        }

        FlushActiveReadingTime();
        BeginReadingTracking();
    }

    private void BeginReadingTracking()
    {
        if (_session is null || !IsActive || _activeReadingStartedAt is not null)
        {
            return;
        }

        _activeReadingStartedAt = DateTimeOffset.Now;
        _readingStatsTimer.Start();
    }

    private void FlushActiveReadingTime()
    {
        if (_activeReadingStartedAt is not { } startedAt)
        {
            _readingStatsTimer.Stop();
            return;
        }

        var now = DateTimeOffset.Now;
        var elapsed = now - startedAt;
        _activeReadingStartedAt = null;
        _readingStatsTimer.Stop();
        if (elapsed <= TimeSpan.Zero)
        {
            return;
        }

        _settings.TotalReadingSeconds = Math.Clamp(
            _settings.TotalReadingSeconds + elapsed.TotalSeconds,
            0,
            315_576_000);
        var date = startedAt.LocalDateTime.ToString("yyyy-MM-dd");
        if (!_settings.ReadingDates.Contains(date, StringComparer.Ordinal))
        {
            _settings.ReadingDates.Add(date);
            _settings.ReadingDates = _settings.ReadingDates
                .OrderByDescending(value => value, StringComparer.Ordinal)
                .Take(3660)
                .ToList();
        }

        ScheduleJsonSave();
        UpdateWelcomeDashboard();
    }

    private async Task RefreshAnnotationsAsync(CancellationToken cancellationToken = default)
    {
        var selectedId = (AnnotationList.SelectedItem as AnnotationListItem)?.Value.Id;
        _annotationItems.Clear();
        try
        {
            if (_libraryDatabase is not null && _currentBook is not null)
            {
                foreach (var annotation in await _libraryDatabase.ListAnnotationsAsync(
                             _currentBook.Id,
                             cancellationToken: cancellationToken))
                {
                    _annotationItems.Add(new AnnotationListItem(annotation));
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or SqliteException)
        {
            Debug.WriteLine(exception);
            StatusText.Text = "批注暂时无法读取";
        }

        AnnotationCountText.Text = $"{_annotationItems.Count} 条";
        NoAnnotationsPanel.Visibility = _annotationItems.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        AnnotationList.SelectedItem = selectedId is null
            ? null
            : _annotationItems.FirstOrDefault(item => item.Value.Id == selectedId);
        UpdateAnnotationVisualState();
        UpdateWelcomeDashboard();
    }

    private string GetCurrentSectionAnnotationsJson()
    {
        if (_session is null)
        {
            return "[]";
        }

        return JsonSerializer.Serialize(_annotationItems
            .Select(item => item.Value)
            .Where(annotation =>
                annotation.SectionIndex == _session.CurrentSectionIndex &&
                annotation.Anchor is not null));
    }

    private void StartSearchIndexBuild(ReaderSession session, LibraryBook? book)
    {
        _searchIndexCancellation?.Cancel();
        _searchIndexCancellation?.Dispose();
        _searchIndexCancellation = null;
        _searchIndexTask = null;
        if (_libraryDatabase is null || book is null)
        {
            return;
        }

        _searchIndexCancellation = new CancellationTokenSource();
        var stamp = new SearchIndexStamp
        {
            SourceSize = book.FileSize,
            SourceModifiedUtc = book.ModifiedUtc,
            SectionCount = session.Sections.Count,
            IndexVersion = BookSearchIndexer.IndexVersion
        };
        _searchIndexTask = BuildAndStoreSearchIndexAsync(
            session,
            book.Id,
            stamp,
            _searchIndexCancellation.Token);
    }

    private async Task BuildAndStoreSearchIndexAsync(
        ReaderSession session,
        long bookId,
        SearchIndexStamp stamp,
        CancellationToken cancellationToken)
    {
        try
        {
            var database = _libraryDatabase;
            if (database is null ||
                await database.IsSearchIndexCurrentAsync(bookId, stamp, cancellationToken))
            {
                return;
            }

            var sections = await _searchIndexer.BuildAsync(session, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await database.ReplaceSearchIndexAsync(bookId, sections, stamp, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A newly opened book superseded this index operation.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or SqliteException)
        {
            if (_currentBook?.Id == bookId)
            {
                StatusText.Text = "全书索引构建失败，当前页面仍可查找";
            }
        }
    }

    private ReaderLocation? CreateCurrentLocationSnapshot()
    {
        if (_session is null || _currentBook is null)
        {
            return null;
        }

        var total = Math.Max(1, _session.Sections.Count);
        return new ReaderLocation
        {
            BookId = _currentBook.Id,
            SectionIndex = _session.CurrentSectionIndex,
            Fragment = _session.Kind == ReaderDocumentKind.Pdf && _currentPage > 0
                ? $"page={_currentPage}"
                : _pendingFragment,
            SectionProgress = Math.Clamp(_sectionProgress, 0, 1),
            DocumentProgress = Math.Clamp(
                (_session.CurrentSectionIndex + _sectionProgress) / total,
                0,
                1),
            Anchor = _pendingSelection?.Anchor ?? _currentLocationAnchor,
            TextQuote = _pendingSelection?.Text ?? _currentLocationAnchor?.ExactText,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
    }

    private async Task PersistCurrentLocationAsync(CancellationToken cancellationToken = default)
    {
        var database = _libraryDatabase;
        var location = CreateCurrentLocationSnapshot();
        if (database is null || location is null)
        {
            return;
        }

        await database.SaveReaderLocationAsync(location, cancellationToken).ConfigureAwait(false);
    }

    private void ScheduleDatabaseLocationSave()
    {
        var database = _libraryDatabase;
        var location = CreateCurrentLocationSnapshot();
        if (database is null || location is null)
        {
            return;
        }

        _locationSaveCancellation?.Cancel();
        _locationSaveCancellation?.Dispose();
        _locationSaveCancellation = new CancellationTokenSource();
        _ = PersistLocationAfterDelayAsync(database, location, _locationSaveCancellation.Token);
    }

    private static async Task PersistLocationAfterDelayAsync(
        LibraryDatabase database,
        ReaderLocation location,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(350, cancellationToken);
            await database.SaveReaderLocationAsync(location, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A newer location will be written instead.
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or SqliteException)
        {
            Debug.WriteLine(exception);
        }
    }

    private void SaveReadingState()
    {
        if (_session is not null)
        {
            _recentStore.Update(
                _session,
                _session.CurrentSectionIndex,
                _sectionProgress,
                _zoomFactor,
                _currentPage);
        }

        _settings.ZoomFactor = _zoomFactor;
        ScheduleJsonSave();
        ScheduleDatabaseLocationSave();
    }

    private void ScheduleJsonSave()
    {
        var revision = _settingsStore.ReserveSaveRevision();
        var snapshot = CloneSettings(_settings);
        _jsonSaveCancellation?.Cancel();
        _jsonSaveCancellation?.Dispose();
        _jsonSaveCancellation = new CancellationTokenSource();
        _jsonSaveTask = PersistJsonAfterDelayAsync(snapshot, revision, _jsonSaveCancellation.Token);
    }

    private async Task PersistJsonAfterDelayAsync(
        AppSettings settingsSnapshot,
        long revision,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(350, cancellationToken).ConfigureAwait(false);
            await _jsonSaveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.Run(() =>
                {
                    _recentStore.Flush();
                    _settingsStore.Save(settingsSnapshot, revision);
                }).ConfigureAwait(false);
            }
            finally
            {
                _jsonSaveGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // A newer in-memory snapshot superseded this write.
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Debug.WriteLine(exception);
        }
    }

    private void FlushJsonState()
    {
        _jsonSaveCancellation?.Cancel();
        _jsonSaveGate.Wait();
        try
        {
            _recentStore.Flush();
            _settingsStore.Save(CloneSettings(_settings));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Debug.WriteLine(exception);
        }
        finally
        {
            _jsonSaveGate.Release();
        }
    }

    private static AppSettings CloneSettings(AppSettings source) => source.Clone();

    private void SetLoading(bool loading, string? message = null)
    {
        LoadingProgress.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        OpenButton.IsEnabled = !loading;
        OpenComicFolderButton.IsEnabled = !loading;
        WelcomeOpenComicFolderButton.IsEnabled = !loading;
        if (message is not null)
        {
            StatusText.Text = message;
        }
    }

    private async Task RunGuardedUiActionAsync(Func<Task> action, string failureMessage)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            // A newer user action superseded this operation.
        }
        catch (Exception exception) when (
            exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException or
                AppDomainUnloadedException or BadImageFormatException))
        {
            Debug.WriteLine(exception);
            StatusText.Text = failureMessage;
            MessageBox.Show(
                this,
                exception is IOException or UnauthorizedAccessException
                    ? "本地文件暂时无法读写。请检查磁盘空间和文件权限后重试。"
                    : exception.Message,
                failureMessage,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private bool SupportsReaderRuntime =>
        _session is { IsReflowable: true, EnableScriptExecution: true };

    private bool IsComicSession => _session?.Kind == ReaderDocumentKind.Comic;

    private bool IsComicRightToLeft =>
        IsComicSession && _settings.ComicDirection == ComicReadingDirection.RightToLeft;

    private string GetComicDisplayText() => _settings.ComicDisplay switch
    {
        ComicDisplayMode.Double => _settings.ComicCoverSinglePage ? "双页（封面单页）" : "双页",
        ComicDisplayMode.Continuous => "纵向连续",
        _ => "单页"
    };

    private string GetComicDirectionText() =>
        _settings.ComicDirection == ComicReadingDirection.RightToLeft ? "右向左" : "左向右";

    private string GetComicSourceText() => _session?.ComicContentRootDirectory is not null
        ? "图片文件夹"
        : Path.GetExtension(_session?.SourcePath ?? string.Empty).TrimStart('.').ToUpperInvariant();

    private void UpdateReaderSurfaceColor()
    {
        var color = IsComicSession ? "#0F0F0F" : _settings.ReaderTheme switch
        {
            ReaderThemeMode.Auto => App.IsDarkTheme ? "#1A1A1A" : "#FAF9F7",
            ReaderThemeMode.Paper => "#FBF7ED",
            ReaderThemeMode.Light => "#FFFFFF",
            ReaderThemeMode.Dark => "#141414",
            _ => "#FBF7ED"
        };
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        ReaderSurface.Background = brush;

        try
        {
            if (ReaderView is not null)
            {
                ReaderView.DefaultBackgroundColor = (System.Drawing.Color)System.Drawing.ColorTranslator.FromHtml(color);
            }
        }
        catch
        {
            // WebView may not be ready yet.
        }
    }


    private bool IsInternalUri(Uri uri)
    {
        if (uri.Scheme == Uri.UriSchemeHttps &&
            (string.Equals(uri.Host, BookHostName, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Host, ComicContentHostName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return uri.Scheme == "about" ||
               (_session?.Kind == ReaderDocumentKind.Pdf &&
                uri.Scheme is "data" or "blob" or "edge" or "chrome");
    }

    private void OpenExternalUri(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https" or "mailto"))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            StatusText.Text = "已在系统浏览器中打开外部链接";
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            StatusText.Text = "无法打开外部链接";
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_documentEditorDialog is { IsLoaded: true } documentEditor)
        {
            documentEditor.Close();
            if (documentEditor.IsLoaded)
            {
                e.Cancel = true;
                return;
            }
        }

        _isClosing = true;
        if (_readerSettingsDialog is { } settingsDialog)
        {
            try
            {
                settingsDialog.Close();
            }
            catch (InvalidOperationException)
            {
                // Dialog may already be closing with the main window.
            }
        }

        FlushActiveReadingTime();
        SaveReadingState();
        FlushJsonState();
        _locationSaveCancellation?.Cancel();
        try
        {
            PersistCurrentLocationAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or SqliteException)
        {
            Debug.WriteLine(exception);
        }

        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _appearanceCancellation?.Cancel();
        _appearanceCancellation?.Dispose();
        _libraryScanCancellation?.Cancel();
        _libraryScanCancellation?.Dispose();
        _searchIndexCancellation?.Cancel();
        _searchIndexCancellation?.Dispose();
        _locationSaveCancellation?.Dispose();
        _jsonSaveCancellation?.Cancel();
        _jsonSaveCancellation?.Dispose();
        _jsonSaveGate.Dispose();
        _readingStatsTimer.Stop();
        Activated -= MainWindow_Activated;
        Deactivated -= MainWindow_Deactivated;
        ReaderView.Dispose();
    }
}
