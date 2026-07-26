namespace NoGaReader.Services;

internal static class DesktopAppPaths
{
    public static string WebView2Root
    {
        get
        {
            var path = Path.Combine(AppPaths.Root, "WebView2");
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
