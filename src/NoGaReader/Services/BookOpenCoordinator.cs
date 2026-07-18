using System.Windows;

namespace NoGaReader.Services;

/// <summary>
/// Tracks the shared reader window used for browser-style book tabs.
/// </summary>
public static class BookOpenCoordinator
{
    private static readonly object Gate = new();
    private static Window? _sharedReader;

    public static Window? SharedReader
    {
        get
        {
            lock (Gate)
            {
                if (_sharedReader is { IsLoaded: true })
                {
                    return _sharedReader;
                }

                _sharedReader = null;
                return null;
            }
        }
    }

    public static bool TryActivateSharedReader()
    {
        var window = SharedReader;
        if (window is null)
        {
            return false;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
        return true;
    }

    public static void RegisterSharedReader(Window window)
    {
        lock (Gate)
        {
            _sharedReader = window;
        }

        window.Closed += (_, _) =>
        {
            lock (Gate)
            {
                if (ReferenceEquals(_sharedReader, window))
                {
                    _sharedReader = null;
                }
            }
        };
    }

    public static string NormalizeKey(string path)
    {
        try
        {
            if (Directory.Exists(path) || File.Exists(path))
            {
                return Path.GetFullPath(path);
            }
        }
        catch
        {
            // fall through
        }

        return path.Trim();
    }
}
