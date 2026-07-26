using System.Globalization;
using System.Text.Json;
using NoGaReader.Mobile.Platforms.Android;
using NoGaReader.Mobile.Services;
using NoGaReader.Models;
using NoGaReader.Services;

namespace NoGaReader.Mobile;

/// <summary>
/// Thin reader host: all paging/section/progress/contents state lives in
/// <see cref="MobileReaderPresenter"/> (Core, smoke-tested); this page renders
/// that state and bridges Android WebView, PdfRenderer, and dialogs.
/// </summary>
public partial class ReaderPage : ContentPage
{
    private readonly MobileLibraryService _library;
    private readonly LibraryBook _book;
    private readonly ReaderSession _session;
    private readonly MobileReaderPresenter _presenter;
    private readonly IDispatcherTimer _progressTimer;
    private AndroidPdfDocument? _pdfDocument;
    private bool _darkTheme;
    private double _fontSize;
    private double _lineHeight;
    private bool _changingContents;
    private bool _isLeaving;
    private bool _savedBeforeLeaving;
    private int _ticksSinceSave;
    private SearchHit? _pendingSearchHit;
    private readonly WebView? _documentWebView;
    private readonly string? _webViewUnavailableMessage;

    private WebView DocumentWebView => _documentWebView
        ?? throw new InvalidOperationException("当前文档格式不使用 WebView。");

    public ReaderPage(MobileLibraryService library, LibraryBook book, ReaderSession session)
    {
        InitializeComponent();
        _library = library;
        _book = book;
        _session = session;
        _presenter = new MobileReaderPresenter(session);
        Title = session.Title;
        ReaderTitle.Text = session.Title;
        ReaderSubtitle.Text = string.IsNullOrWhiteSpace(session.Author)
            ? Path.GetExtension(session.SourcePath).TrimStart('.').ToUpperInvariant()
            : session.Author;
        _fontSize = Math.Clamp(Preferences.Default.Get("reader_font_size", 19d), 16d, 28d);
        _lineHeight = Math.Clamp(Preferences.Default.Get("reader_line_height", 1.8d), 1.4d, 2.2d);
        _darkTheme = Preferences.Default.Get("reader_dark_theme", false);
        if (_presenter.UsesWebView)
        {
            try
            {
                using var probe = new Android.Webkit.WebView(
                    Platform.CurrentActivity ?? Android.App.Application.Context);
                _documentWebView = new WebView
                {
                    IsVisible = true
                };
                _documentWebView.HandlerChanged += OnWebViewHandlerChanged;
                _documentWebView.Navigating += OnWebViewNavigating;
                _documentWebView.Navigated += OnWebViewNavigated;
                WebViewHost.Children.Add(_documentWebView);
            }
            catch (Exception)
            {
                _webViewUnavailableMessage =
                    "设备的 Android System WebView 无法启动。请在应用商店更新“Android System WebView”和 Chrome，重启设备后再试。";
            }
        }

        SearchButton.IsVisible = _documentWebView is not null && session.SupportsInPageSearch;
        AnnotationButton.IsVisible = _documentWebView is not null && session.EnableScriptExecution;
        DisplayButton.IsVisible = _documentWebView is not null && session.EnableScriptExecution;

        ContentsPicker.ItemsSource = _presenter.Contents.Select(item => item.Title).ToList();
        SelectCurrentContentsEntry();

        _progressTimer = Dispatcher.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromSeconds(2);
        _progressTimer.Tick += OnProgressTimerTick;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        ApplyChromeTheme();
        if (_webViewUnavailableMessage is not null)
        {
            ShowReaderError(new NotSupportedException(_webViewUnavailableMessage));
            return;
        }

        try
        {
            ReaderErrorState.IsVisible = false;
            await LoadCurrentAsync(restoreSavedPosition: true);
            _progressTimer.Start();
        }
        catch (Exception exception)
        {
            ShowReaderError(exception);
        }
    }

    protected override async void OnDisappearing()
    {
        _progressTimer.Stop();
        try
        {
            if (!_savedBeforeLeaving)
            {
                await CaptureAndSaveLocationAsync();
            }
        }
        catch
        {
            // Leaving the page must remain possible even when the current file became unavailable.
        }

        base.OnDisappearing();
    }

    private async Task LoadCurrentAsync(bool restoreSavedPosition)
    {
        LoadingIndicator.IsVisible = true;
        LoadingIndicator.IsRunning = true;
        try
        {
            switch (_session.Kind)
            {
                case ReaderDocumentKind.Pdf:
                    await RenderPdfAsync(restoreSavedPosition);
                    break;
                case ReaderDocumentKind.Image:
                    ShowImage(_session.CurrentSection.FullPath);
                    break;
                case ReaderDocumentKind.Comic:
                    RenderComicPage(restoreSavedPosition);
                    break;
                default:
                    ShowWebSection();
                    break;
            }

            UpdatePositionLabel();
        }
        finally
        {
            LoadingIndicator.IsRunning = false;
            LoadingIndicator.IsVisible = false;
        }
    }

    private async Task RenderPdfAsync(bool restoreSavedPosition)
    {
        _pdfDocument ??= new AndroidPdfDocument(_session.SourcePath);
        _presenter.SetPdfPageCount(_pdfDocument.PageCount);
        if (restoreSavedPosition)
        {
            _presenter.RestoreFromDocumentProgress(_book.Location?.DocumentProgress ?? 0);
        }

        PageImage.Source = await _pdfDocument.RenderPageAsync(_presenter.VisualPageIndex);
        ShowImageHost();
    }

    private void RenderComicPage(bool restoreSavedPosition)
    {
        if (restoreSavedPosition)
        {
            _presenter.RestoreFromDocumentProgress(_book.Location?.DocumentProgress ?? 0);
        }

        SelectCurrentContentsEntry();
        ShowImage(_presenter.CurrentComicPagePath);
    }

    private void ShowImage(string path)
    {
        PageImage.Source = DownsampledImageLoader.Load(path);
        ShowImageHost();
    }

    private void ShowImageHost()
    {
        WebViewHost.IsVisible = false;
        ImageScroller.IsVisible = true;
    }

    private void ShowWebSection()
    {
        ImageScroller.IsVisible = false;
        WebViewHost.IsVisible = true;
        ConfigureNativeWebView();
        DocumentWebView.Source = new UrlWebViewSource
        {
            Url = new Uri(_session.CurrentSection.FullPath).AbsoluteUri
        };
    }

    private async void OnWebViewNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (e.Result != WebNavigationResult.Success)
        {
            return;
        }

        // An in-book link (EPUB cross-chapter navigation) changes the document
        // without going through MoveAsync; resync the presenter so the position
        // label, contents picker, and saved progress stay truthful.
        SyncSectionFromNavigatedUrl(e.Url);
        if (!_session.EnableScriptExecution)
        {
            return;
        }

        var savedProgress = _book.Location?.SectionIndex == _session.CurrentSectionIndex
            ? Math.Clamp(_book.Location.SectionProgress, 0, 1)
            : 0;
        _presenter.SectionProgress = savedProgress;
        await ApplyReaderStyleAsync(savedProgress);
        await ApplyAnnotationsAsync();
        if (_pendingSearchHit is { } hit && hit.SectionIndex == _session.CurrentSectionIndex)
        {
            await RevealSearchHitAsync(hit);
            _pendingSearchHit = null;
        }
    }

    private void OnWebViewHandlerChanged(object? sender, EventArgs e) => ConfigureNativeWebView();

    private async void OnWebViewNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) &&
            uri.IsFile &&
            IsWithinSessionRoot(uri))
        {
            return;
        }

        // Everything else would leave the prepared local content. Block it in
        // the reader and hand explicit web/mail links to the system, matching
        // the desktop client's "no network inside the reader" guarantee.
        e.Cancel = true;
        if (uri is not null && uri.Scheme is "http" or "https" or "mailto")
        {
            try
            {
                await Launcher.Default.OpenAsync(uri);
            }
            catch (Exception)
            {
                // No system handler for this link type; the tap does nothing.
            }
        }
    }

    private void SyncSectionFromNavigatedUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile)
        {
            return;
        }

        if (_presenter.SyncSectionFromPath(uri.LocalPath))
        {
            SelectCurrentContentsEntry();
            UpdatePositionLabel();
        }
    }

    private bool IsWithinSessionRoot(Uri uri)
    {
        try
        {
            var candidate = Path.GetFullPath(uri.LocalPath);
            var root = Path.GetFullPath(_session.RootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            return candidate.StartsWith(root, StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private void ConfigureNativeWebView()
    {
        if (DocumentWebView.Handler is not Microsoft.Maui.Handlers.WebViewHandler webViewHandler ||
            webViewHandler.PlatformView is not Android.Webkit.WebView nativeWebView)
        {
            return;
        }

        // Resource-level offline enforcement; page-level navigation is filtered
        // by OnWebViewNavigating on top of this.
        nativeWebView.SetWebViewClient(new LocalContentWebViewClient(webViewHandler));
        nativeWebView.Settings.JavaScriptEnabled = _session.EnableScriptExecution;
        nativeWebView.Settings.AllowFileAccess = true;
        nativeWebView.Settings.AllowContentAccess = false;
        nativeWebView.Settings.SetSupportZoom(true);
        nativeWebView.Settings.BuiltInZoomControls = true;
        nativeWebView.Settings.DisplayZoomControls = false;
    }

    private async Task ApplyReaderStyleAsync(double progress)
    {
        var background = _darkTheme ? "#151515" : "#f8f4e8";
        var foreground = _darkTheme ? "#e7e2d8" : "#27231d";
        var linkColor = _darkTheme ? "#d6d3d1" : "#57534e";
        var fontSize = _fontSize.ToString("0.#", CultureInfo.InvariantCulture);
        var lineHeight = _lineHeight.ToString("0.##", CultureInfo.InvariantCulture);
        var script = $$"""
            (() => {
              let style = document.getElementById('nogareader-mobile-style');
              if (!style) {
                style = document.createElement('style');
                style.id = 'nogareader-mobile-style';
                document.head.appendChild(style);
              }
              style.textContent = `html,body{background:{{background}}!important;color:{{foreground}}!important;}
                body{font-size:{{fontSize}}px!important;line-height:{{lineHeight}}!important;max-width:52rem;margin:0 auto!important;padding:1.25rem 1.15rem 3rem!important;box-sizing:border-box;text-rendering:optimizeLegibility;}
                p{margin:.25em 0 1em!important;} h1,h2,h3{line-height:1.35!important;margin:1.2em 0 .65em!important;}
                img,svg,video{max-width:100%!important;height:auto!important;} a{color:{{linkColor}}!important;}
                mark.nogar-mobile-note{background:#f6d86b;color:inherit;border-radius:.15em;padding:0 .08em;}
                mark.nogar-mobile-search{background:#ff8f65;color:#1b1b1b;outline:2px solid #fff;}`;
              const max = Math.max(0, document.documentElement.scrollHeight - innerHeight);
              scrollTo(0, max * {{progress.ToString("R", CultureInfo.InvariantCulture)}});
              return true;
            })();
            """;
        await DocumentWebView.EvaluateJavaScriptAsync(script);
    }

    private async Task ApplyAnnotationsAsync()
    {
        var annotations = await _library.ListAnnotationsAsync(_book.Id, _session.CurrentSectionIndex);
        await DocumentWebView.EvaluateJavaScriptAsync(
            MobileReaderScripts.BuildAnnotationMountScript(annotations));
    }

    private async Task RevealSearchHitAsync(SearchHit hit)
    {
        var text = JsonSerializer.Serialize(hit.MatchedText);
        var occurrence = Math.Max(0, hit.OccurrenceIndex);
        var script = $$"""
            (() => {
              const query = {{text}};
              const targetOccurrence = {{occurrence}};
              let occurrence = 0;
              const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
              while (walker.nextNode()) {
                const node = walker.currentNode;
                if (node.parentElement?.closest('script,style')) continue;
                const source = String(node.nodeValue || '');
                let from = 0;
                while (from <= source.length - query.length) {
                  const index = source.toLocaleLowerCase().indexOf(query.toLocaleLowerCase(), from);
                  if (index < 0) break;
                  if (occurrence++ === targetOccurrence) {
                    try {
                      const range = document.createRange();
                      range.setStart(node, index);
                      range.setEnd(node, index + query.length);
                      const mark = document.createElement('mark');
                      mark.className = 'nogar-mobile-search';
                      range.surroundContents(mark);
                      mark.scrollIntoView({ block: 'center' });
                    } catch {
                      // Highlighting failed (range crosses elements); still scroll close.
                      node.parentElement?.scrollIntoView({ block: 'center' });
                    }
                    return true;
                  }
                  from = index + Math.max(1, query.length);
                }
              }
              return false;
            })();
            """;
        await DocumentWebView.EvaluateJavaScriptAsync(script);
    }

    private async void OnProgressTimerTick(object? sender, EventArgs e)
    {
        await CaptureWebProgressAsync();
        UpdatePositionLabel();

        // Persist periodically (about every 10 s) so progress survives the app
        // being killed from recents instead of only saving on page exit.
        if (++_ticksSinceSave < 5)
        {
            return;
        }

        _ticksSinceSave = 0;
        try
        {
            await _library.SaveLocationAsync(_book.Id, _session, _presenter.ProgressForSave);
        }
        catch
        {
            // A missing or damaged source must not break the reading session;
            // leaving the page still retries the save.
        }
    }

    private async Task CaptureWebProgressAsync()
    {
        if (_documentWebView is null || !WebViewHost.IsVisible || !_session.EnableScriptExecution)
        {
            return;
        }

        try
        {
            var result = await DocumentWebView.EvaluateJavaScriptAsync(
                "String(Math.max(0,Math.min(1,scrollY/Math.max(1,document.documentElement.scrollHeight-innerHeight))))");
            result = DecodeJavaScriptString(result);
            if (double.TryParse(result, NumberStyles.Float, CultureInfo.InvariantCulture, out var progress))
            {
                _presenter.SectionProgress = progress;
            }
        }
        catch (InvalidOperationException)
        {
            // Navigation may replace the JavaScript context while this timer is running.
        }
    }

    private async Task CaptureAndSaveLocationAsync()
    {
        await CaptureWebProgressAsync();
        await _library.SaveLocationAsync(_book.Id, _session, _presenter.ProgressForSave);
    }

    private async void OnPreviousClicked(object? sender, EventArgs e)
    {
        await MoveAsync(-1);
    }

    private async void OnNextClicked(object? sender, EventArgs e)
    {
        await MoveAsync(1);
    }

    private async Task MoveAsync(int delta)
    {
        await CaptureAndSaveLocationAsync();
        switch (_presenter.Move(delta))
        {
            case MobileReaderPresenter.MoveResult.PageChanged:
                if (_session.Kind == ReaderDocumentKind.Pdf)
                {
                    await RenderPdfAsync(restoreSavedPosition: false);
                }
                else
                {
                    RenderComicPage(restoreSavedPosition: false);
                }

                break;
            case MobileReaderPresenter.MoveResult.SectionChanged:
                SelectCurrentContentsEntry();
                await LoadCurrentAsync(restoreSavedPosition: false);
                break;
        }

        UpdatePositionLabel();
    }

    private async void OnContentsChanged(object? sender, EventArgs e)
    {
        if (_changingContents || ContentsPicker.SelectedIndex < 0)
        {
            return;
        }

        var entry = _presenter.Contents[ContentsPicker.SelectedIndex];
        await CaptureAndSaveLocationAsync();
        _presenter.JumpToContents(entry);
        await LoadCurrentAsync(restoreSavedPosition: false);
    }

    private async void OnBookmarkClicked(object? sender, EventArgs e)
    {
        await CaptureWebProgressAsync();
        await _library.AddBookmarkAsync(_book.Id, _session, _presenter.ProgressForSave);
        await DisplayAlert("书签", "已保存当前位置。", "好");
    }

    private async void OnSearchClicked(object? sender, EventArgs e)
    {
        if (!_session.SupportsInPageSearch)
        {
            await DisplayAlert("搜索", "这种格式目前不提供正文搜索。", "好");
            return;
        }

        var query = await DisplayPromptAsync("全书搜索", "输入要查找的文字", "搜索", "取消");
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        try
        {
            IsBusy = true;
            var hits = await _library.SearchAsync(_book, _session, query);
            if (hits.Count == 0)
            {
                await DisplayAlert("搜索", "没有找到匹配内容。", "好");
                return;
            }

            var visibleHits = hits.Take(15).ToList();
            var choices = visibleHits.Select((hit, index) =>
                $"{index + 1}. {hit.SectionTitle} · {Shorten(hit.Snippet, 54)}").ToArray();
            var selected = await DisplayActionSheet($"找到 {hits.Count} 处", "取消", null, choices);
            var selectedIndex = Array.IndexOf(choices, selected);
            if (selectedIndex < 0)
            {
                return;
            }

            var hit = visibleHits[selectedIndex];
            _pendingSearchHit = hit;
            _presenter.JumpToSearchHit(hit);
            SelectCurrentContentsEntry();
            await LoadCurrentAsync(restoreSavedPosition: false);
        }
        catch (Exception exception)
        {
            await DisplayAlert("搜索失败", exception.Message, "好");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async void OnAnnotationClicked(object? sender, EventArgs e)
    {
        if (_documentWebView is null || !WebViewHost.IsVisible || !_session.EnableScriptExecution)
        {
            await DisplayAlert("批注", "这种格式目前只支持页级书签。", "好");
            return;
        }

        var action = await DisplayActionSheet(
            "批注",
            "取消",
            null,
            "高亮所选文字",
            "为所选文字写笔记",
            "查看本章批注");
        if (action == "查看本章批注")
        {
            await ShowSectionAnnotationsAsync();
            return;
        }

        if (action is not ("高亮所选文字" or "为所选文字写笔记"))
        {
            return;
        }

        var selectionResult = await DocumentWebView.EvaluateJavaScriptAsync(
            MobileReaderScripts.SelectionCaptureScript);
        var capture = MobileReaderScripts.ParseSelectionCapture(DecodeJavaScriptString(selectionResult));
        if (capture is null)
        {
            await DisplayAlert("批注", "请先在正文中选择一段文字。", "好");
            return;
        }

        string? note = null;
        if (action == "为所选文字写笔记")
        {
            note = await DisplayPromptAsync("添加笔记", Shorten(capture.Text, 80), "保存", "取消");
            if (note is null)
            {
                return;
            }
        }

        await CaptureWebProgressAsync();
        await _library.AddTextAnnotationAsync(
            _book.Id,
            _session,
            _presenter.SectionProgress,
            capture.Text,
            note,
            new TextAnchor
            {
                ExactText = capture.Text,
                Prefix = capture.Prefix,
                Suffix = capture.Suffix,
                Progress = _presenter.SectionProgress
            });
        await DocumentWebView.EvaluateJavaScriptAsync(
            "(() => { const s=getSelection(); if(!s||s.rangeCount===0||s.isCollapsed)return false;" +
            "const r=s.getRangeAt(0); const m=document.createElement('mark');m.className='nogar-mobile-note';" +
            "try{r.surroundContents(m);s.removeAllRanges();return true}catch{return false} })()");
    }

    private async Task ShowSectionAnnotationsAsync()
    {
        var annotations = await _library.ListAnnotationsAsync(_book.Id, _session.CurrentSectionIndex);
        if (annotations.Count == 0)
        {
            await DisplayAlert("本章批注", "本章还没有批注。", "好");
            return;
        }

        var choices = annotations.Select((item, index) =>
            $"{index + 1}. {Shorten(item.SelectedText ?? "书签", 48)}").ToArray();
        var selected = await DisplayActionSheet("本章批注", "关闭", null, choices);
        var selectedIndex = Array.IndexOf(choices, selected);
        if (selectedIndex >= 0)
        {
            var annotation = annotations[selectedIndex];
            await DisplayAlert(
                annotation.Type == AnnotationType.Note ? "笔记" : "高亮",
                string.IsNullOrWhiteSpace(annotation.Note)
                    ? annotation.SelectedText ?? ""
                    : $"{annotation.SelectedText}\n\n{annotation.Note}",
                "好");
        }
    }

    private async void OnDisplayClicked(object? sender, EventArgs e)
    {
        var action = await DisplayActionSheet(
            "阅读显示",
            "取消",
            null,
            $"减小字号（当前 {_fontSize:0}）",
            $"增大字号（当前 {_fontSize:0}）",
            _darkTheme ? "切换为浅色" : "切换为深色",
            "恢复默认");

        switch (action)
        {
            case string value when value.StartsWith("减小字号", StringComparison.Ordinal):
                _fontSize = Math.Max(16, _fontSize - 1);
                break;
            case string value when value.StartsWith("增大字号", StringComparison.Ordinal):
                _fontSize = Math.Min(28, _fontSize + 1);
                break;
            case "切换为浅色":
            case "切换为深色":
                _darkTheme = !_darkTheme;
                break;
            case "恢复默认":
                _fontSize = 19;
                _lineHeight = 1.8;
                _darkTheme = false;
                break;
            default:
                return;
        }

        Preferences.Default.Set("reader_font_size", _fontSize);
        Preferences.Default.Set("reader_line_height", _lineHeight);
        Preferences.Default.Set("reader_dark_theme", _darkTheme);
        ApplyChromeTheme();
        if (_documentWebView is not null && WebViewHost.IsVisible && _session.EnableScriptExecution)
        {
            await ApplyReaderStyleAsync(_presenter.SectionProgress);
        }
    }

    private void UpdatePositionLabel()
    {
        PositionLabel.Text = _presenter.PositionText;
        ReadingProgress.Progress = _presenter.DocumentProgress;
        PreviousButton.Text = _presenter.PreviousButtonText;
        NextButton.Text = _presenter.NextButtonText;
        PreviousButton.IsEnabled = _presenter.CanMovePrevious;
        NextButton.IsEnabled = _presenter.CanMoveNext;
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        await LeaveReaderAsync();
    }

    protected override bool OnBackButtonPressed()
    {
        _ = LeaveReaderAsync();
        return true;
    }

    private async Task LeaveReaderAsync()
    {
        if (_isLeaving)
        {
            return;
        }

        _isLeaving = true;
        _progressTimer.Stop();
        try
        {
            await CaptureAndSaveLocationAsync();
            _savedBeforeLeaving = true;
        }
        catch
        {
            // A missing or damaged source must not trap the reader on this page.
        }

        await Navigation.PopAsync();
    }

    private async void OnRetryClicked(object? sender, EventArgs e)
    {
        try
        {
            ReaderErrorState.IsVisible = false;
            await LoadCurrentAsync(restoreSavedPosition: true);
            _progressTimer.Start();
        }
        catch (Exception exception)
        {
            ShowReaderError(exception);
        }
    }

    private void ShowReaderError(Exception exception)
    {
        LoadingIndicator.IsRunning = false;
        LoadingIndicator.IsVisible = false;
        WebViewHost.IsVisible = false;
        ImageScroller.IsVisible = false;
        ReaderErrorMessage.Text = exception is NotSupportedException
            ? exception.Message
            : "文件可能已损坏、加密，或使用了暂不支持的排版方式。";
        ReaderErrorState.IsVisible = true;
    }

    private void ApplyChromeTheme()
    {
        if (Application.Current is not null)
        {
            Application.Current.UserAppTheme = _darkTheme ? AppTheme.Dark : AppTheme.Light;
        }

        // Matches the desktop ReaderCanvasBrush tones (#EBEAE6 light / #0F0F0F dark).
        var background = Color.FromArgb(_darkTheme ? "#0F0F0F" : "#EBEAE6");
        BackgroundColor = background;
        ReadingSurface.BackgroundColor = background;
    }

    private void SelectCurrentContentsEntry()
    {
        _changingContents = true;
        ContentsPicker.SelectedIndex = _presenter.SelectedContentsIndex;
        _changingContents = false;
    }

    private static string Shorten(string value, int maximumLength)
    {
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength] + "…";
    }

    private static string DecodeJavaScriptString(string result)
    {
        try
        {
            return JsonSerializer.Deserialize<string>(result) ?? string.Empty;
        }
        catch (JsonException)
        {
            return result.Trim('"');
        }
    }

    protected override void OnHandlerChanged()
    {
        if (Handler is null)
        {
            _pdfDocument?.Dispose();
            _pdfDocument = null;
        }

        base.OnHandlerChanged();
    }
}
