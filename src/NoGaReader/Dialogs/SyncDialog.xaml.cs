using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using NoGaReader.Models;
using NoGaReader.Services;

namespace NoGaReader.Dialogs;

public partial class SyncDialog : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly LibraryDatabase? _database;
    private readonly CloudSyncService _syncService;

    public SyncDialog(AppSettings settings, SettingsStore settingsStore, LibraryDatabase? database)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _database = database;
        _syncService = new CloudSyncService(settings);
        InitializeComponent();
        EnableSyncCheck.IsChecked = settings.SyncEnabled;
        FolderBox.Text = settings.SyncFolderPath;
        IncludeAnnotationsCheck.IsChecked = settings.SyncIncludeAnnotations;
        IncludeSettingsCheck.IsChecked = settings.SyncIncludeSettings;
        UpdateStatus();
        Loaded += (_, _) => App.ApplyWindowChromeTheme(this);
    }

    private void EnableSyncCheck_Changed(object sender, RoutedEventArgs e) => UpdateStatus();

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择同步文件夹",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            FolderBox.Text = dialog.FolderName;
            UpdateStatus();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ApplyToSettings();
        _settingsStore.Save(_settings);
        UpdateStatus();
        StatusText.Text = "设置已保存。\n" + _syncService.StatusText;
    }

    private async void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        ApplyToSettings();
        _settingsStore.Save(_settings);
        if (_database is null)
        {
            MessageBox.Show(this, "书库尚未初始化，无法同步。", "云同步", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            StatusText.Text = "正在同步…";
            var result = await _syncService.SynchronizeAsync(_database, _settingsStore);
            UpdateStatus();
            StatusText.Text =
                $"{result.Message}\n上传/本地书籍 {result.UploadedBooks}，更新进度 {result.DownloadedBooks}，合并批注 {result.MergedAnnotations}\n{_syncService.StatusText}";
            if (!result.Succeeded)
            {
                MessageBox.Show(this, result.Message, "同步未完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "同步失败", MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateStatus();
        }
    }

    private void ApplyToSettings()
    {
        _settings.SyncEnabled = EnableSyncCheck.IsChecked == true;
        _settings.SyncProvider = _settings.SyncEnabled ? SyncProviderKind.Folder : SyncProviderKind.None;
        _settings.SyncFolderPath = FolderBox.Text.Trim();
        _settings.SyncIncludeAnnotations = IncludeAnnotationsCheck.IsChecked == true;
        _settings.SyncIncludeSettings = IncludeSettingsCheck.IsChecked == true;
    }

    private void UpdateStatus()
    {
        ApplyToSettings();
        StatusText.Text = _syncService.StatusText;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
