using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using NoGaReader.Services;

namespace NoGaReader.Dialogs;

public partial class DocumentEditorDialog : Window
{
    private string? _filePath;
    private bool _dirty;
    private bool _suppressDirty;
    private bool _requiresSafeSaveAs;
    private string _lastFind = string.Empty;

    public DocumentEditorDialog(string? initialPath = null)
    {
        InitializeComponent();
        NewDocument();
        if (!string.IsNullOrWhiteSpace(initialPath) && File.Exists(initialPath))
        {
            OpenPath(initialPath);
        }

        Loaded += (_, _) => App.ApplyWindowChromeTheme(this);
    }

    public string? FilePath => _filePath;

    private void NewDocument()
    {
        if (!ConfirmDiscardChanges())
        {
            return;
        }

        _suppressDirty = true;
        Editor.Document = OfficeDocumentService.CreateBlank();
        _filePath = null;
        _requiresSafeSaveAs = false;
        _dirty = false;
        _suppressDirty = false;
        UpdateHeader();
        UpdateStats();
        StatusText.Text = "已新建文档";
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开文档",
            Filter = OfficeDocumentService.OpenFilter,
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            OpenPath(dialog.FileName);
        }
    }

    public bool OpenPath(string path)
    {
        if (!ConfirmDiscardChanges())
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var requiresSafeSaveAs = OfficeDocumentService.RequiresSafeSaveAs(fullPath);
            var document = OfficeDocumentService.Load(fullPath);
            _suppressDirty = true;
            Editor.Document = document;
            _filePath = fullPath;
            _requiresSafeSaveAs = requiresSafeSaveAs;
            _dirty = false;
            _suppressDirty = false;
            UpdateHeader();
            UpdateStats();
            StatusText.Text = $"已打开 {Path.GetFileName(path)}";
            return true;
        }
        catch (Exception exception)
        {
            _suppressDirty = false;
            MessageBox.Show(this, exception.Message, "无法打开文档", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e) => Save(false);

    private void SaveAsButton_Click(object sender, RoutedEventArgs e) => Save(true);

    private bool Save(bool forceSaveAs)
    {
        if (!forceSaveAs && _requiresSafeSaveAs && !string.IsNullOrWhiteSpace(_filePath))
        {
            var result = MessageBox.Show(
                this,
                "此 DOCX 包含当前编辑器无法完整保留的图片、样式、链接或页面结构。\n\n" +
                "为保护原文件，本次修改必须另存为一个新副本。",
                "需要另存为",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            return result == MessageBoxResult.OK && Save(true);
        }

        var path = _filePath;
        if (forceSaveAs || string.IsNullOrWhiteSpace(path))
        {
            var dialog = new SaveFileDialog
            {
                Title = "保存文档",
                Filter = OfficeDocumentService.SaveFilter,
                FileName = string.IsNullOrWhiteSpace(path)
                    ? "未命名.docx"
                    : _requiresSafeSaveAs
                        ? $"{Path.GetFileNameWithoutExtension(path)} - 编辑副本{Path.GetExtension(path)}"
                        : Path.GetFileName(path),
                AddExtension = true,
                OverwritePrompt = true
            };
            if (!string.IsNullOrWhiteSpace(path))
            {
                dialog.InitialDirectory = Path.GetDirectoryName(path);
            }

            if (dialog.ShowDialog(this) != true)
            {
                return false;
            }

            path = dialog.FileName;
        }

        try
        {
            OfficeDocumentService.Save(Editor.Document, path!);
            _filePath = Path.GetFullPath(path!);
            _requiresSafeSaveAs = false;
            _dirty = false;
            UpdateHeader();
            UpdateStats();
            StatusText.Text = $"已保存 {Path.GetFileName(_filePath)}";
            return true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void NewButton_Click(object sender, RoutedEventArgs e) => NewDocument();

    private void PrintButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var paginator = ((IDocumentPaginatorSource)Editor.Document).DocumentPaginator;
        dialog.PrintDocument(paginator, Path.GetFileName(_filePath) ?? "NoGaReader 文档");
        StatusText.Text = "已发送到打印机";
    }

    private void FindButton_Click(object sender, RoutedEventArgs e)
    {
        FindBox.Focus();
        FindBox.SelectAll();
    }

    private void FindNextButton_Click(object sender, RoutedEventArgs e) => FindNext();

    private void FindNext()
    {
        var query = FindBox.Text;
        if (string.IsNullOrEmpty(query))
        {
            StatusText.Text = "请输入查找内容";
            return;
        }

        _lastFind = query;
        var start = Editor.Selection.End;
        if (string.IsNullOrEmpty(Editor.Selection.Text) && Editor.CaretPosition is not null)
        {
            start = Editor.CaretPosition;
        }

        if (!TryFind(query, start, Editor.Document.ContentEnd, out var match) &&
            !TryFind(query, Editor.Document.ContentStart, start, out match))
        {
            StatusText.Text = "未找到匹配";
            return;
        }

        Editor.Selection.Select(match.Start, match.End);
        Editor.Focus();
        StatusText.Text = $"找到：{query}";
    }

    private static bool TryFind(string query, TextPointer start, TextPointer end, out TextRange match)
    {
        match = null!;
        var current = start;
        while (current is not null && current.CompareTo(end) < 0)
        {
            var text = current.GetTextInRun(LogicalDirection.Forward);
            if (!string.IsNullOrEmpty(text))
            {
                var index = text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
                if (index >= 0)
                {
                    var matchStart = current.GetPositionAtOffset(index) ?? current;
                    var matchEnd = matchStart.GetPositionAtOffset(query.Length) ?? matchStart;
                    match = new TextRange(matchStart, matchEnd);
                    return true;
                }
            }

            current = current.GetNextContextPosition(LogicalDirection.Forward);
        }

        return false;
    }

    private void InsertImageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "插入图片",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(dialog.FileName, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            var image = new System.Windows.Controls.Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                MaxWidth = 640,
                MaxHeight = 480
            };
            var container = new InlineUIContainer(image, Editor.CaretPosition ?? Editor.Document.ContentEnd);
            Editor.CaretPosition = container.ElementEnd;
            MarkDirty();
            StatusText.Text = $"已插入图片 {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法插入图片", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void InsertTableButton_Click(object sender, RoutedEventArgs e)
    {
        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 0, 0, 12)
        };
        table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });

        var group = new TableRowGroup();
        for (var row = 0; row < 3; row++)
        {
            var tableRow = new TableRow();
            for (var column = 0; column < 3; column++)
            {
                var cell = new TableCell(new Paragraph(new Run(row == 0 ? $"列 {column + 1}" : string.Empty)))
                {
                    BorderBrush = (Brush)FindResource("BorderBrush"),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(6)
                };
                tableRow.Cells.Add(cell);
            }

            group.Rows.Add(tableRow);
        }

        table.RowGroups.Add(group);
        var caret = Editor.CaretPosition ?? Editor.Document.ContentEnd;
        var block = FindTopLevelBlock(caret, Editor.Document);
        if (block is null)
        {
            Editor.Document.Blocks.Add(table);
        }
        else
        {
            Editor.Document.Blocks.InsertAfter(block, table);
        }

        MarkDirty();
        StatusText.Text = "已插入 3×3 表格";
    }

    private static Block? FindTopLevelBlock(TextPointer caret, FlowDocument document)
    {
        DependencyObject? current = caret.Parent;
        while (current is not null && !ReferenceEquals(current, document))
        {
            if (current is Block block && ReferenceEquals(block.Parent, document))
            {
                return block;
            }

            current = current switch
            {
                FrameworkContentElement contentElement => contentElement.Parent,
                FrameworkElement element => element.Parent,
                _ => LogicalTreeHelper.GetParent(current)
            };
        }

        return document.Blocks.LastBlock;
    }

    private void ConvertLegacyButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            this,
            "旧版 DOC：请使用 Microsoft Word 或 LibreOffice 另存为 DOCX。\n\n" +
            "ODT：可通过主窗口侧栏「转换」转为 DOCX，再回到文档编辑器打开。\n\n" +
            "转换使用本地 Calibre ebook-convert，无需联网。",
            "转换为 DOCX",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void BoldButton_Click(object sender, RoutedEventArgs e) =>
        ToggleSelectionProperty(TextElement.FontWeightProperty, FontWeights.Bold, FontWeights.Normal);

    private void ItalicButton_Click(object sender, RoutedEventArgs e) =>
        ToggleSelectionProperty(TextElement.FontStyleProperty, FontStyles.Italic, FontStyles.Normal);

    private void UnderlineButton_Click(object sender, RoutedEventArgs e)
    {
        var selection = Editor.Selection;
        var current = selection.GetPropertyValue(Inline.TextDecorationsProperty);
        if (current is TextDecorationCollection decorations &&
            decorations.Contains(TextDecorations.Underline[0]))
        {
            selection.ApplyPropertyValue(Inline.TextDecorationsProperty, null);
        }
        else
        {
            selection.ApplyPropertyValue(Inline.TextDecorationsProperty, TextDecorations.Underline);
        }

        MarkDirty();
        UpdateToolbarState();
    }

    private void ToggleSelectionProperty(DependencyProperty property, object active, object inactive)
    {
        var selection = Editor.Selection;
        var current = selection.GetPropertyValue(property);
        selection.ApplyPropertyValue(property, Equals(current, active) ? inactive : active);
        MarkDirty();
        UpdateToolbarState();
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressDirty)
        {
            return;
        }

        MarkDirty();
        UpdateStats();
    }

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e) => UpdateToolbarState();

    private void MarkDirty()
    {
        if (_dirty)
        {
            return;
        }

        _dirty = true;
        UpdateHeader();
    }

    private void UpdateHeader()
    {
        var name = string.IsNullOrWhiteSpace(_filePath) ? "未命名文档" : Path.GetFileName(_filePath);
        if (_dirty)
        {
            name += " *";
        }

        TitleText.Text = name;
        Title = $"文档编辑器 - {name}";
        PathText.Text = string.IsNullOrWhiteSpace(_filePath) ? "尚未保存" : _filePath;
    }

    private void UpdateStats()
    {
        var text = OfficeDocumentService.GetPlainText(Editor.Document);
        var chars = text.Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "", StringComparison.Ordinal)
            .Length;
        var words = OfficeDocumentService.CountWords(text);
        StatsText.Text = $"{chars:N0} 字 · {words:N0} 词";
    }

    private void UpdateToolbarState()
    {
        var weight = Editor.Selection.GetPropertyValue(TextElement.FontWeightProperty);
        BoldButton.IsChecked = weight is FontWeight fontWeight &&
                               (fontWeight == FontWeights.Bold || fontWeight == FontWeights.SemiBold);

        var style = Editor.Selection.GetPropertyValue(TextElement.FontStyleProperty);
        ItalicButton.IsChecked = style is FontStyle fontStyle && fontStyle == FontStyles.Italic;

        var decorations = Editor.Selection.GetPropertyValue(Inline.TextDecorationsProperty);
        UnderlineButton.IsChecked = decorations is TextDecorationCollection collection &&
                                    collection.Contains(TextDecorations.Underline[0]);
    }

    private bool ConfirmDiscardChanges()
    {
        if (!_dirty)
        {
            return true;
        }

        var result = MessageBox.Show(
            this,
            "文档有未保存的更改，是否保存？",
            "未保存的更改",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        return result switch
        {
            MessageBoxResult.Yes => Save(false),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.S)
            {
                Save(false);
                e.Handled = true;
            }
            else if (e.Key == Key.O)
            {
                OpenButton_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.N)
            {
                NewDocument();
                e.Handled = true;
            }
            else if (e.Key == Key.P)
            {
                PrintButton_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.F)
            {
                FindButton_Click(sender, e);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.F3)
        {
            if (!string.IsNullOrEmpty(FindBox.Text) || !string.IsNullOrEmpty(_lastFind))
            {
                if (string.IsNullOrEmpty(FindBox.Text))
                {
                    FindBox.Text = _lastFind;
                }

                FindNext();
                e.Handled = true;
            }
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscardChanges())
        {
            e.Cancel = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
