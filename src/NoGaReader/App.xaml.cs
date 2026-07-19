using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using NoGaReader.Models;
using NoGaReader.Services;

namespace NoGaReader;

public partial class App : Application
{
    private static int _showingUnhandledError;
    private SingleInstanceService? _singleInstance;

    public static bool IsDarkTheme { get; private set; }

    public static event EventHandler? ThemeChanged;

    private static AppThemeMode _currentThemeMode = AppThemeMode.System;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

        _singleInstance = SingleInstanceService.Acquire();
        if (!_singleInstance.IsPrimaryInstance)
        {
            _singleInstance.TrySignalExistingInstance(e.Args);
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);
        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
            {
                Current?.Dispatcher.BeginInvoke(RefreshSystemThemeIfNeeded);
            }
        };

        AppPaths.EnsureCreated();
        AppPaths.StartCacheCleanup();
        var settingsStore = new SettingsStore();
        var settings = settingsStore.Load();
        UiStrings.ApplyFromSettings(settings.UiLanguage);
        ApplyTheme(settings.Theme);

        var initialFile = e.Args
            .Select(TryGetFullPath)
            .FirstOrDefault(path => path is not null && (File.Exists(path) || Directory.Exists(path)));

        var window = new MainWindow(initialFile, settings, settingsStore);
        MainWindow = window;
        _singleInstance.OpenPathRequested += path =>
        {
            if (MainWindow is MainWindow mainWindow)
            {
                mainWindow.ActivateFromSecondaryInstance(path);
            }
        };
        _singleInstance.StartListening();
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        _singleInstance = null;
        base.OnExit(e);
    }

    private static string? TryGetFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static void App_DispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        if (IsFatal(e.Exception))
        {
            return;
        }

        LogUnhandledException("Dispatcher", e.Exception);
        e.Handled = true;
        ShowUnhandledError(e.Exception);
    }

    private static void TaskScheduler_UnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        LogUnhandledException("TaskScheduler", e.Exception);
        e.SetObserved();
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            LogUnhandledException("AppDomain", exception);
        }
    }

    private static void ShowUnhandledError(Exception exception)
    {
        if (Interlocked.Exchange(ref _showingUnhandledError, 1) != 0)
        {
            return;
        }

        try
        {
            var owner = Current?.MainWindow;
            var message = exception is IOException or UnauthorizedAccessException
                ? "本地文件暂时无法读写。请检查磁盘空间、文件权限，或稍后重试。"
                : "操作没有完成，但应用已避免意外退出。你可以重试刚才的操作。";
            MessageBox.Show(
                owner,
                message,
                "NoGaReader 遇到问题",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
            // Error reporting must never cause a second unhandled exception.
        }
        finally
        {
            Interlocked.Exchange(ref _showingUnhandledError, 0);
        }
    }

    private static void LogUnhandledException(string source, Exception exception)
    {
        Debug.WriteLine($"[{source}] {exception}");
        try
        {
            AppPaths.EnsureCreated();
            var logPath = Path.Combine(AppPaths.DataRoot, "errors.log");
            File.AppendAllText(
                logPath,
                $"{DateTimeOffset.Now:O} [{source}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Disk-full and permission failures are common reasons for arriving here.
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException or
            AppDomainUnloadedException or BadImageFormatException;

    public static void ApplyTheme(AppThemeMode mode)
    {
        _currentThemeMode = mode;
        var dark = ResolveIsDark(mode);

        var changed = IsDarkTheme != dark;
        IsDarkTheme = dark;
        var resources = Current.Resources;

        // Neutral stone palette: warm grays, minimal blue cast.
        resources["WindowBackgroundBrush"] = Brush(dark ? "#141414" : "#F5F4F2");
        resources["SidebarBrush"] = Brush(dark ? "#1A1A1A" : "#FAF9F7");
        resources["SurfaceBrush"] = Brush(dark ? "#222222" : "#FFFFFF");
        resources["SurfaceAltBrush"] = Brush(dark ? "#2A2A2A" : "#F0EFEC");
        resources["ReaderCanvasBrush"] = Brush(dark ? "#0F0F0F" : "#EBEAE6");
        // Keep primary action buttons high-contrast in both themes:
        // dark mode uses a solid light chip + dark label so "完成" stays readable.
        resources["PrimaryBrush"] = Brush(dark ? "#E7E5E4" : "#57534E");
        resources["PrimaryHoverBrush"] = Brush(dark ? "#F5F5F4" : "#44403C");
        resources["PrimarySoftBrush"] = Brush(dark ? "#2F2D2B" : "#E7E5E4");
        resources["PrimaryContrastBrush"] = Brush(dark ? "#1C1917" : "#FFFFFF");
        resources["TextBrush"] = Brush(dark ? "#F5F5F4" : "#1C1917");
        resources["MutedTextBrush"] = Brush(dark ? "#A8A29E" : "#78716C");
        resources["BorderBrush"] = Brush(dark ? "#3F3F3F" : "#E7E5E4");
        resources["HoverBrush"] = Brush(dark ? "#2C2C2C" : "#F5F5F4");
        resources["DangerBrush"] = Brush(dark ? "#F08A7E" : "#DC2626");
        resources["SuccessBrush"] = Brush(dark ? "#5FD0A5" : "#059669");
        resources["ThumbBackdropBrush"] = Brush(dark ? "#0A0A0A" : "#1C1917");
        resources["OverlayShadowBrush"] = Brush(dark ? "#000000" : "#292524");
        resources["TitleBarBrush"] = Brush(dark ? "#141414" : "#F5F4F2");

        ThemeChanged?.Invoke(Current, EventArgs.Empty);
        NotifyWindowsThemeChanged();
    }

    public static bool ResolveIsDark(AppThemeMode mode) =>
        mode == AppThemeMode.Dark ||
        (mode == AppThemeMode.System && IsWindowsDarkMode());

    private static void NotifyWindowsThemeChanged()
    {
        if (Current is null)
        {
            return;
        }

        foreach (Window window in Current.Windows)
        {
            if (window is MainWindow mainWindow)
            {
                mainWindow.OnApplicationThemeChanged();
            }
            else
            {
                ApplyWindowChromeTheme(window);
            }
        }
    }

    public static void RefreshSystemThemeIfNeeded()
    {
        if (_currentThemeMode != AppThemeMode.System)
        {
            return;
        }

        var dark = ResolveIsDark(AppThemeMode.System);
        if (dark == IsDarkTheme)
        {
            return;
        }

        ApplyTheme(AppThemeMode.System);
        if (Current is not null)
        {
            foreach (Window window in Current.Windows)
            {
                ApplyWindowChromeTheme(window);
            }
        }
    }

    public static void ApplyWindowChromeTheme(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var useDark = IsDarkTheme ? 1 : 0;
        if (DwmSetWindowAttribute(handle, 20, ref useDark, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(handle, 19, ref useDark, sizeof(int));
        }
    }

    private static SolidColorBrush Brush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }

    private static bool IsWindowsDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}
