using NoGaReader.Mobile.Services;
using NoGaReader.Mobile.Models;
using NoGaReader.Models;

namespace NoGaReader.Mobile;

public partial class MainPage : ContentPage
{
    private readonly MobileLibraryService _library;
    private IReadOnlyList<LibraryBookItem> _books = [];
    private LibraryBookItem? _continueBook;

    public MainPage(MobileLibraryService library)
    {
        InitializeComponent();
        _library = library;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await ReloadAsync();
    }

    private async void OnImportClicked(object? sender, EventArgs e)
    {
        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "选择要导入的书籍"
            });
            if (result is null)
            {
                return;
            }

            SetBusy(true, $"正在导入 {result.FileName}…");
            var book = await _library.ImportAsync(result);
            await ReloadAsync();
            await OpenBookAsync(book);
        }
        catch (NotSupportedException exception)
        {
            await DisplayAlert("不支持的格式", exception.Message, "知道了");
        }
        catch (Exception exception)
        {
            await DisplayAlert("导入失败", exception.Message, "知道了");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnBookSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not LibraryBookItem item)
        {
            return;
        }

        BooksView.SelectedItem = null;
        await OpenBookAsync(item.Book);
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => ApplyFilter();

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        await ReloadAsync();
        RefreshContainer.IsRefreshing = false;
    }

    private async Task ReloadAsync()
    {
        try
        {
            _books = (await _library.ListAsync()).Select(LibraryBookItem.FromBook).ToList();
            LibrarySummary.Text = _books.Count == 0 ? "随身阅读，从一本书开始" : $"{_books.Count} 本书 · 数据仅保存在本机";
            UpdateContinueCard();
            ApplyFilter();
        }
        catch (Exception exception)
        {
            LibrarySummary.Text = "书库暂时不可用";
            await DisplayAlert("加载失败", exception.Message, "知道了");
        }
    }

    private void UpdateContinueCard()
    {
        // Mirrors the desktop dashboard's "continue reading" card: the most
        // recently opened book that has actual progress recorded.
        _continueBook = _books
            .Where(item => item.Progress > 0)
            .OrderByDescending(item => item.Book.LastOpenedUtc)
            .FirstOrDefault();
        ContinueCard.IsVisible = _continueBook is not null;
        if (_continueBook is not null)
        {
            ContinueTitle.Text = _continueBook.Title;
            ContinueProgress.Text = $"{_continueBook.ProgressText} · {_continueBook.FormatLabel}";
        }
    }

    private async void OnContinueClicked(object? sender, EventArgs e)
    {
        if (_continueBook is { } item)
        {
            await OpenBookAsync(item.Book);
        }
    }

    private void ApplyFilter()
    {
        var query = LibrarySearch.Text?.Trim();
        var visibleBooks = string.IsNullOrWhiteSpace(query)
            ? _books
            : _books.Where(book =>
                    book.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    book.Author.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        BooksView.ItemsSource = visibleBooks;
        BooksView.IsVisible = visibleBooks.Count > 0;
        EmptyState.IsVisible = visibleBooks.Count == 0;
        EmptyTitle.Text = _books.Count == 0 ? "书库还是空的" : "没有找到这本书";
        EmptyDescription.Text = _books.Count == 0
            ? "从手机中选择一本书，导入后即可离线阅读。"
            : "换个书名或作者关键词再试试。";
    }

    private async Task OpenBookAsync(LibraryBook book)
    {
        try
        {
            SetBusy(true, $"正在打开《{book.Title}》…");
            var session = await _library.OpenAsync(book);
            await Navigation.PushAsync(new ReaderPage(_library, book, session));
        }
        catch (Exception exception)
        {
            await DisplayAlert("无法打开", exception.Message, "知道了");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnDeleteInvoked(object? sender, EventArgs e)
    {
        if (sender is not SwipeItem { CommandParameter: LibraryBookItem item })
        {
            return;
        }

        var confirmed = await DisplayAlert(
            "移出书库",
            $"确定删除《{item.Title}》吗？导入到应用中的文件也会一并删除。",
            "删除",
            "取消");
        if (!confirmed)
        {
            return;
        }

        try
        {
            SetBusy(true, "正在删除…");
            await _library.DeleteAsync(item.Book);
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            await DisplayAlert("删除失败", exception.Message, "知道了");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy, string message = "正在处理…")
    {
        IsBusy = busy;
        BusyLabel.Text = message;
        BusyOverlay.IsVisible = busy;
    }

}
