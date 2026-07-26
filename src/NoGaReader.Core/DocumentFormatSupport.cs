namespace NoGaReader.Services;

/// <summary>
/// Defines format capabilities independently of any UI or platform reader host.
/// A format may be part of the mobile catalog even when rendering it requires a
/// platform adapter, such as PDF.
/// </summary>
public static class DocumentFormatSupport
{
    private static readonly HashSet<string> DesktopExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf", ".mobi", ".azw", ".azw3", ".azw4",
        ".cbz", ".cbr", ".cb7", ".cbt", ".zip", ".rar",
        ".fb2", ".chm", ".xps", ".oxps", ".djvu", ".djv",
        ".html", ".htm", ".xhtml", ".mht", ".mhtml", ".xml",
        ".txt", ".md", ".markdown", ".log", ".nfo",
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tif", ".tiff"
    };

    private static readonly HashSet<string> MobileMvpExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf", ".fb2", ".cbz",
        ".html", ".htm", ".xhtml", ".xml",
        ".txt", ".md", ".markdown", ".log", ".nfo",
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tif", ".tiff"
    };

    private static readonly IReadOnlyList<string> MobileMvpExtensionList = Array.AsReadOnly(
        MobileMvpExtensions.OrderBy(extension => extension, StringComparer.OrdinalIgnoreCase).ToArray());

    public static bool IsDesktopSupported(string path) =>
        HasSupportedExtension(path, DesktopExtensions);

    public static bool IsMobileMvpSupported(string path) =>
        HasSupportedExtension(path, MobileMvpExtensions);

    public static IReadOnlyList<string> MobileMvpFileExtensions => MobileMvpExtensionList;

    private static bool HasSupportedExtension(string path, HashSet<string> extensions)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return extensions.Contains(Path.GetExtension(path));
    }
}
