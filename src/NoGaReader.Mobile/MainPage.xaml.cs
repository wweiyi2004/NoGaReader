using NoGaReader.Mobile.Services;
using NoGaReader.Models;
using NoGaReader.Services;

namespace NoGaReader.Mobile;

/// <summary>
/// Thin shelf host: filtering, summary/empty-state copy, and continue-reading
/// selection live in <see cref="MobileLibraryPresenter"/> (Core, smoke-tested);
/// this page renders that state and bridges the picker and dialogs.
/// </summary>
public partial class MainPage : ContentPage
{
    private readonly MobileLibraryService _library;
    private readonly MobileLibraryPresenter _presenter = new();

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

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => RenderLibrary();

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        await ReloadAsync();
        RefreshContainer.IsRefreshing = false;
    }

    private async Task ReloadAsync()
    {
        try
        {
            _presenter.SetBooks(await _library.ListAsync());
            RenderLibrary();
        }
        catch (Exception exception)
        {
            LibrarySummary.Text = "书库暂时不可用";
            await DisplayAlert("加载失败", exception.Message, "知道了");
        }
    }

    private void RenderLibrary()
    {
        var view = _presenter.BuildView(LibrarySearch.Text);
        LibrarySummary.Text = view.Summary;
        BooksView.ItemsSource = view.VisibleBooks;
        BooksView.IsVisible = !view.ShowEmptyState;
        EmptyState.IsVisible = view.ShowEmptyState;
        EmptyTitle.Text = view.EmptyTitle;
        EmptyDescription.Text = view.EmptyDescription;

        ContinueCard.IsVisible = _presenter.ContinueReading is not null;
        if (_presenter.ContinueReading is { } continueBook)
        {
            ContinueTitle.Text = continueBook.Title;
            ContinueProgress.Text = _presenter.ContinueReadingSubtitle;
        }
    }

    private async void OnContinueClicked(object? sender, EventArgs e)
    {
        if (_presenter.ContinueReading is { } item)
        {
            await OpenBookAsync(item.Book);
        }
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
