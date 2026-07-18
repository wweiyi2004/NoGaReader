using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using NoGaReader.Models;

namespace NoGaReader.Dialogs;

public partial class ReaderSettingsDialog : Window
{
    private readonly AppSettings _settings;
    private bool _initializing = true;

    public event EventHandler? SettingsChanged;

    public ReaderSettingsDialog(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        LoadFromSettings();
        _initializing = false;
        Loaded += (_, _) => App.ApplyWindowChromeTheme(this);
    }

    public void ReloadFromSettings()
    {
        _initializing = true;
        LoadFromSettings();
        _initializing = false;
    }

    private void LoadFromSettings()
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
        UsePublisherFontCheck.IsChecked = _settings.UsePublisherFont;
        UpdateReadout();
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
        RaiseSettingsChanged();
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
        RaiseSettingsChanged();
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
        UpdateReadout();
        RaiseSettingsChanged();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.ReaderTheme = ReaderThemeMode.Auto;
        _settings.ReaderFlow = ReaderFlowMode.Paged;
        _settings.ReaderFontSize = 18;
        _settings.ReaderLineHeight = 1.9;
        _settings.ReaderContentWidth = 720;
        _settings.UsePublisherFont = true;
        ReloadFromSettings();
        RaiseSettingsChanged();
    }

    private void UsePublisherFontCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        _settings.UsePublisherFont = UsePublisherFontCheck.IsChecked == true;
        RaiseSettingsChanged();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void UpdateReadout()
    {
        FontSizeValueText.Text = $"{_settings.ReaderFontSize} px";
        LineHeightValueText.Text = _settings.ReaderLineHeight.ToString("0.00");
        ContentWidthValueText.Text = $"{_settings.ReaderContentWidth} px";
    }

    private void RaiseSettingsChanged()
    {
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void SelectExclusiveToggle(string value, params ToggleButton[] buttons)
    {
        foreach (var button in buttons)
        {
            button.IsChecked = string.Equals(button.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase);
        }
    }
}
