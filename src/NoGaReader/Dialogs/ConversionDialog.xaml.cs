using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using NoGaReader.Models;
using NoGaReader.Services;

namespace NoGaReader.Dialogs;

public partial class ConversionDialog : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly CalibreConverter _converter;
    private readonly ObservableCollection<ConversionJobViewModel> _jobs = [];
    private CancellationTokenSource? _runCancellation;
    private bool _running;

    public ConversionDialog(AppSettings settings, SettingsStore settingsStore, CalibreConverter converter)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _converter = converter;
        InitializeComponent();
        JobList.ItemsSource = _jobs;
        TargetFormatCombo.ItemsSource = _converter.OutputFormats;
        var preferred = _converter.OutputFormats.FirstOrDefault(item =>
            string.Equals(
                item.NormalizedExtension,
                _settings.ConversionDefaultTargetExtension,
                StringComparison.OrdinalIgnoreCase));
        TargetFormatCombo.SelectedItem = preferred ?? _converter.OutputFormats[0];
        if (string.IsNullOrWhiteSpace(_settings.ConversionOutputDirectory))
        {
            UseSourceDirectoryCheck.IsChecked = true;
            OutputDirectoryBox.Text = string.Empty;
            OutputDirectoryBox.IsEnabled = false;
        }
        else
        {
            UseSourceDirectoryCheck.IsChecked = false;
            OutputDirectoryBox.Text = _settings.ConversionOutputDirectory;
            OutputDirectoryBox.IsEnabled = true;
        }

        RefreshRuntimeStatus();
        FooterStatusText.Text = "添加文件后点击开始转换。无次数限制。";
        Loaded += (_, _) => App.ApplyWindowChromeTheme(this);
    }

    private void RefreshRuntimeStatus()
    {
        _converter.RefreshRuntime();
        if (_converter.IsAvailable)
        {
            RuntimeStatusText.Text = $"转换引擎就绪 · {_converter.RuntimeSource}";
            RuntimeStatusText.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
            var version = string.IsNullOrWhiteSpace(_converter.RuntimeVersion)
                ? "版本未知"
                : _converter.RuntimeVersion;
            RuntimePathText.Text = $"{version}\n{_converter.RuntimePath}";
            StartButton.IsEnabled = !_running;
        }
        else
        {
            RuntimeStatusText.Text = "未找到转换引擎";
            RuntimeStatusText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            RuntimePathText.Text =
                "请安装 Calibre、放入 engines\\calibre\\，或点击“指定引擎”选择 ebook-convert.exe。";
            StartButton.IsEnabled = false;
        }
    }

    private void DetectRuntimeButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.CalibreEbookConvertPath = string.Empty;
        _settingsStore.Save(_settings);
        RefreshRuntimeStatus();
        FooterStatusText.Text = _converter.IsAvailable
            ? $"已检测到：{_converter.RuntimeSource}"
            : "仍未找到引擎";
    }

    private void BrowseRuntimeButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 ebook-convert.exe",
            Filter = "ebook-convert|ebook-convert.exe|可执行文件|*.exe|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _settings.CalibreEbookConvertPath = dialog.FileName;
        _settingsStore.Save(_settings);
        RefreshRuntimeStatus();
        FooterStatusText.Text = _converter.IsAvailable
            ? "已使用指定引擎"
            : "指定路径无效";
    }

    private void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "添加要转换的文件",
            Filter =
                "可转换文件|*.epub;*.mobi;*.azw;*.azw3;*.pdf;*.fb2;*.txt;*.html;*.htm;*.docx;*.odt;*.rtf;*.cbz;*.cbr;*.cb7;*.zip;*.rar;*.md;*.markdown;*.chm;*.djvu;*.lit;*.lrf;*.pdb|" +
                "所有文件|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            AddFiles(dialog.FileNames);
        }
    }

    private void RemoveSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = JobList.SelectedItems.Cast<ConversionJobViewModel>().ToList();
        foreach (var item in selected)
        {
            if (item.Status is ConversionJobStatus.Running)
            {
                continue;
            }

            _jobs.Remove(item);
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            return;
        }

        _jobs.Clear();
        FooterStatusText.Text = "任务列表已清空";
    }

    private void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择转换输出目录",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            UseSourceDirectoryCheck.IsChecked = false;
            OutputDirectoryBox.IsEnabled = true;
            OutputDirectoryBox.Text = dialog.FolderName;
        }
    }

    private void UseSourceDirectoryCheck_Changed(object sender, RoutedEventArgs e)
    {
        var useSource = UseSourceDirectoryCheck.IsChecked == true;
        OutputDirectoryBox.IsEnabled = !useSource;
        if (useSource)
        {
            OutputDirectoryBox.Text = string.Empty;
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            return;
        }

        RefreshRuntimeStatus();
        if (!_converter.IsAvailable)
        {
            MessageBox.Show(
                this,
                CalibreRuntimeLocator.BuildMissingRuntimeMessage(),
                "无法开始转换",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (_jobs.Count == 0)
        {
            FooterStatusText.Text = "请先添加文件";
            return;
        }

        if (TargetFormatCombo.SelectedItem is not ConversionFormat target)
        {
            FooterStatusText.Text = "请选择目标格式";
            return;
        }

        var useSourceDirectory = UseSourceDirectoryCheck.IsChecked == true;
        var outputDirectory = OutputDirectoryBox.Text.Trim();
        if (!useSourceDirectory)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                FooterStatusText.Text = "请选择输出目录，或勾选保存到源文件目录";
                return;
            }

            try
            {
                Directory.CreateDirectory(outputDirectory);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, "无法创建输出目录", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        _settings.ConversionDefaultTargetExtension = target.NormalizedExtension;
        _settings.ConversionOutputDirectory = useSourceDirectory ? string.Empty : outputDirectory;
        _settingsStore.Save(_settings);

        foreach (var job in _jobs)
        {
            if (job.Status is ConversionJobStatus.Succeeded or ConversionJobStatus.Running)
            {
                continue;
            }

            job.TargetExtension = target.NormalizedExtension;
            job.Status = ConversionJobStatus.Pending;
            job.StatusText = "排队中";
            job.ErrorMessage = null;
            job.OutputPath = null;
        }

        _runCancellation?.Dispose();
        _runCancellation = new CancellationTokenSource();
        _running = true;
        StartButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        FooterStatusText.Text = "转换进行中…";

        try
        {
            await RunJobsAsync(useSourceDirectory, outputDirectory, _runCancellation.Token);
            FooterStatusText.Text = "本轮转换结束";
        }
        catch (OperationCanceledException)
        {
            FooterStatusText.Text = "已取消";
        }
        finally
        {
            _running = false;
            CancelButton.IsEnabled = false;
            RefreshRuntimeStatus();
        }
    }

    private async Task RunJobsAsync(
        bool useSourceDirectory,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        foreach (var job in _jobs.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (job.Status == ConversionJobStatus.Succeeded)
            {
                continue;
            }

            var directory = useSourceDirectory
                ? Path.GetDirectoryName(job.SourcePath) ?? outputDirectory
                : outputDirectory;
            job.Status = ConversionJobStatus.Running;
            job.StatusText = "转换中…";
            job.StartedAt = DateTimeOffset.UtcNow;

            var progress = new Progress<string>(message =>
            {
                job.StatusText = message;
            });

            try
            {
                var result = await _converter.ConvertAsync(
                    job.SourcePath,
                    job.TargetExtension,
                    directory,
                    progress,
                    cancellationToken);
                job.CompletedAt = DateTimeOffset.UtcNow;
                if (result.Succeeded)
                {
                    job.Status = ConversionJobStatus.Succeeded;
                    job.StatusText = "完成";
                    job.OutputPath = result.OutputPath;
                    job.ErrorMessage = null;
                }
                else
                {
                    job.Status = ConversionJobStatus.Failed;
                    job.StatusText = "失败";
                    job.ErrorMessage = result.ErrorMessage;
                    job.OutputPath = null;
                    if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                    {
                        job.StatusText = "失败：" + Trim(result.ErrorMessage, 80);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                job.Status = ConversionJobStatus.Cancelled;
                job.StatusText = "已取消";
                job.CompletedAt = DateTimeOffset.UtcNow;
                throw;
            }
            catch (Exception exception)
            {
                job.Status = ConversionJobStatus.Failed;
                job.StatusText = "失败：" + Trim(exception.Message, 80);
                job.ErrorMessage = exception.Message;
                job.CompletedAt = DateTimeOffset.UtcNow;
            }
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _runCancellation?.Cancel();
        FooterStatusText.Text = "正在取消…";
    }

    private void OpenOutputButton_Click(object sender, RoutedEventArgs e)
    {
        if (JobList.SelectedItem is ConversionJobViewModel { OutputPath: { Length: > 0 } path } &&
            File.Exists(path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
            return;
        }

        var directory = UseSourceDirectoryCheck.IsChecked == true
            ? _jobs.Select(job => Path.GetDirectoryName(job.SourcePath))
                .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item))
            : OutputDirectoryBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            FooterStatusText.Text = "没有可打开的输出位置";
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true
        });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_running)
        {
            Close();
            e.Handled = true;
        }
    }

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        AddFiles(paths.Where(File.Exists));
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        var target = TargetFormatCombo.SelectedItem as ConversionFormat
                     ?? _converter.OutputFormats[0];
        var added = 0;
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var extension = Path.GetExtension(path);
            if (!CalibreConverter.IsConvertibleInput(extension))
            {
                continue;
            }

            if (_jobs.Any(job => string.Equals(job.SourcePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _jobs.Add(new ConversionJobViewModel
            {
                SourcePath = Path.GetFullPath(path),
                TargetExtension = target.NormalizedExtension,
                Status = ConversionJobStatus.Pending,
                StatusText = "待转换"
            });
            added++;
        }

        FooterStatusText.Text = added > 0 ? $"已添加 {added} 个文件" : "没有可添加的转换文件";
    }

    private static string Trim(string value, int max)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }

    protected override void OnClosed(EventArgs e)
    {
        _runCancellation?.Cancel();
        _runCancellation?.Dispose();
        base.OnClosed(e);
    }

    private sealed class ConversionJobViewModel : INotifyPropertyChanged
    {
        private string _targetExtension = ".epub";
        private ConversionJobStatus _status = ConversionJobStatus.Pending;
        private string _statusText = "待转换";
        private string? _outputPath;
        private string? _errorMessage;

        public required string SourcePath { get; init; }

        public string FileName => Path.GetFileName(SourcePath);

        public string TargetExtension
        {
            get => _targetExtension;
            set
            {
                if (SetField(ref _targetExtension, value))
                {
                    OnPropertyChanged(nameof(TargetLabel));
                }
            }
        }

        public string TargetLabel => TargetExtension.TrimStart('.').ToUpperInvariant();

        public ConversionJobStatus Status
        {
            get => _status;
            set => SetField(ref _status, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => SetField(ref _statusText, value);
        }

        public string? OutputPath
        {
            get => _outputPath;
            set => SetField(ref _outputPath, value);
        }

        public string? ErrorMessage
        {
            get => _errorMessage;
            set => SetField(ref _errorMessage, value);
        }

        public DateTimeOffset? StartedAt { get; set; }

        public DateTimeOffset? CompletedAt { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(name);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
