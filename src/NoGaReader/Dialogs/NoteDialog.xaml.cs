using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace NoGaReader.Dialogs;

public partial class NoteDialog : Window
{
    private bool _initializing = true;

    public NoteDialog(
        string selectedText,
        string? existingNote = null,
        string color = "yellow",
        bool isEditing = false)
    {
        InitializeComponent();
        if (isEditing)
        {
            Title = "编辑笔记";
            HeadingText.Text = "编辑这条笔记";
            SaveButton.Content = "保存修改";
        }

        QuoteText.Text = selectedText;
        NoteTextBox.Text = existingNote ?? string.Empty;
        SelectColor(color);
        _initializing = false;
        SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(NoteTextBox.Text);
        Loaded += (_, _) =>
        {
            App.ApplyWindowChromeTheme(this);
            NoteTextBox.Focus();
            NoteTextBox.CaretIndex = NoteTextBox.Text.Length;
        };
    }

    public string NoteText => NoteTextBox.Text.Trim();

    public string ColorKey { get; private set; } = "yellow";

    private void ColorButton_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not ToggleButton { IsChecked: true, Tag: string color })
        {
            return;
        }

        SelectColor(color);
    }

    private void SelectColor(string color)
    {
        ColorKey = color;
        foreach (var button in new[] { YellowButton, GreenButton, BlueButton, PinkButton })
        {
            button.IsChecked = string.Equals(button.Tag?.ToString(), color, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void NoteTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SaveButton is not null)
        {
            SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(NoteTextBox.Text);
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NoteText))
        {
            return;
        }

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && SaveButton.IsEnabled)
        {
            e.Handled = true;
            DialogResult = true;
        }
    }
}
