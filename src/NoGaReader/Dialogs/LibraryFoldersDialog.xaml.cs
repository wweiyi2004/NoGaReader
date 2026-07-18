using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NoGaReader.Models;

namespace NoGaReader.Dialogs;

public partial class LibraryFoldersDialog : Window
{
    private readonly ObservableCollection<LibraryFolderItemViewModel> _folderItems;

    public LibraryFoldersDialog(IReadOnlyList<LibraryFolder> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);

        _folderItems = new ObservableCollection<LibraryFolderItemViewModel>(
            folders.Select(folder => new LibraryFolderItemViewModel(CloneFolder(folder))));

        InitializeComponent();
        FolderList.ItemsSource = _folderItems;

        var hasFolders = _folderItems.Count > 0;
        FolderList.Visibility = hasFolders ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = hasFolders ? Visibility.Collapsed : Visibility.Visible;
        SelectionPanel.Visibility = hasFolders ? Visibility.Visible : Visibility.Collapsed;
        if (hasFolders)
        {
            FolderList.SelectedIndex = 0;
        }

        Loaded += (_, _) =>
        {
            App.ApplyWindowChromeTheme(this);
            (hasFolders ? (Control)FolderList : ApplyButton).Focus();
        };
    }

    /// <summary>
    /// Gets the immutable change summary after the user applies the dialog; otherwise <see langword="null"/>.
    /// </summary>
    public LibraryFoldersDialogChanges? Result { get; private set; }

    /// <summary>
    /// Alias for <see cref="Result"/> for callers that prefer an explicit change-set name.
    /// </summary>
    public LibraryFoldersDialogChanges? Changes => Result;

    private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FolderList.SelectedItem is not null)
        {
            SelectionPanel.Visibility = Visibility.Visible;
        }
    }

    private void ToggleRemovalButton_Click(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is not LibraryFolderItemViewModel item)
        {
            return;
        }

        item.IsMarkedForRemoval = !item.IsMarkedForRemoval;
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyChanges();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Result = null;
            DialogResult = false;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            ApplyChanges();
        }
    }

    private void ApplyChanges()
    {
        var updated = _folderItems
            .Where(item => !item.IsMarkedForRemoval && item.HasScanScopeChanged)
            .Select(item => CloneFolder(item.Folder))
            .ToArray();
        var removed = _folderItems
            .Where(item => item.IsMarkedForRemoval)
            .Select(item => CloneFolder(item.Folder))
            .ToArray();

        Result = new LibraryFoldersDialogChanges(updated, removed);
        DialogResult = true;
    }

    private static LibraryFolder CloneFolder(LibraryFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        return new LibraryFolder
        {
            Id = folder.Id,
            Path = folder.Path,
            Name = folder.Name,
            IncludeSubfolders = folder.IncludeSubfolders,
            AddedAt = folder.AddedAt,
            LastScannedAt = folder.LastScannedAt
        };
    }
}

public sealed class LibraryFoldersDialogChanges
{
    internal LibraryFoldersDialogChanges(
        IReadOnlyList<LibraryFolder> updatedFolders,
        IReadOnlyList<LibraryFolder> removedFolders)
    {
        UpdatedFolders = Array.AsReadOnly(updatedFolders.ToArray());
        RemovedFolders = Array.AsReadOnly(removedFolders.ToArray());
        UpdatedFolderIds = Array.AsReadOnly(updatedFolders.Select(folder => folder.Id).ToArray());
        RemovedFolderIds = Array.AsReadOnly(removedFolders.Select(folder => folder.Id).ToArray());
    }

    public IReadOnlyList<LibraryFolder> UpdatedFolders { get; }

    public IReadOnlyList<long> UpdatedFolderIds { get; }

    public IReadOnlyList<LibraryFolder> RemovedFolders { get; }

    public IReadOnlyList<long> RemovedFolderIds { get; }

    public bool HasChanges => UpdatedFolders.Count > 0 || RemovedFolders.Count > 0;
}

internal sealed class LibraryFolderItemViewModel : INotifyPropertyChanged
{
    private bool _includeSubfolders;
    private bool _isMarkedForRemoval;

    internal LibraryFolderItemViewModel(LibraryFolder folder)
    {
        Folder = folder;
        OriginalIncludeSubfolders = folder.IncludeSubfolders;
        _includeSubfolders = folder.IncludeSubfolders;
        PathExists = Directory.Exists(folder.Path);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public LibraryFolder Folder { get; }

    public string Name => string.IsNullOrWhiteSpace(Folder.Name)
        ? System.IO.Path.GetFileName(Folder.Path.TrimEnd(System.IO.Path.DirectorySeparatorChar))
        : Folder.Name;

    public string Path => Folder.Path;

    public bool PathExists { get; }

    public string PathStatusLabel => PathExists ? "路径正常" : "路径不存在";

    public string LastScannedLabel => Folder.LastScannedAt is { } timestamp
        ? $"上次扫描 {timestamp.ToLocalTime():yyyy-MM-dd HH:mm}"
        : "尚未扫描";

    public bool IncludeSubfolders
    {
        get => _includeSubfolders;
        set
        {
            if (!SetField(ref _includeSubfolders, value))
            {
                return;
            }

            Folder.IncludeSubfolders = value;
            OnPropertyChanged(nameof(IncludeSubfoldersLabel));
            OnPropertyChanged(nameof(HasScanScopeChanged));
            OnPropertyChanged(nameof(AutomationName));
        }
    }

    public string IncludeSubfoldersLabel => IncludeSubfolders ? "包含子文件夹" : "仅当前文件夹";

    public bool IsMarkedForRemoval
    {
        get => _isMarkedForRemoval;
        set
        {
            if (!SetField(ref _isMarkedForRemoval, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsEditable));
            OnPropertyChanged(nameof(RemovalActionLabel));
            OnPropertyChanged(nameof(RemovalVisibility));
            OnPropertyChanged(nameof(AutomationName));
        }
    }

    public bool IsEditable => !IsMarkedForRemoval;

    public string RemovalActionLabel => IsMarkedForRemoval ? "撤销移除" : "标记移除";

    public Visibility RemovalVisibility => IsMarkedForRemoval ? Visibility.Visible : Visibility.Collapsed;

    public bool HasScanScopeChanged => IncludeSubfolders != OriginalIncludeSubfolders;

    public string AutomationName => $"{Name}，{Path}，{IncludeSubfoldersLabel}，{LastScannedLabel}，{PathStatusLabel}"
                                    + (IsMarkedForRemoval ? "，待移除" : string.Empty);

    private bool OriginalIncludeSubfolders { get; }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
