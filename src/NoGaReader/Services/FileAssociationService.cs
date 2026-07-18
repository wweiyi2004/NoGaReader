using Microsoft.Win32;

namespace NoGaReader.Services;

public static class FileAssociationService
{
    public const string ProgId = "NoGaReader.Document";
    private const string ClassesRootPath = @"Software\Classes";

    public static readonly string[] AssociatedExtensions =
    [
        ".epub", ".pdf", ".fb2", ".mobi", ".azw", ".azw3",
        ".cbz", ".cbr", ".cb7", ".cbt", ".zip", ".rar",
        ".chm", ".xps", ".oxps", ".djvu", ".djv",
        ".md", ".markdown", ".txt",
        ".docx", ".rtf", ".html", ".htm"
    ];

    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{ClassesRootPath}\{ProgId}\shell\open\command");
            return key?.GetValue(string.Empty) is string value &&
                   value.Contains("NoGaReader", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void RegisterCurrentUser(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        executablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("找不到 NoGaReader 可执行文件。", executablePath);
        }

        var command = $"\"{executablePath}\" \"%1\"";
        using (var progId = Registry.CurrentUser.CreateSubKey($@"{ClassesRootPath}\{ProgId}"))
        {
            progId.SetValue(string.Empty, "NoGaReader 文档");
            using var icon = progId.CreateSubKey("DefaultIcon");
            icon.SetValue(string.Empty, $"\"{executablePath}\",0");
            using var commandKey = progId.CreateSubKey(@"shell\open\command");
            commandKey.SetValue(string.Empty, command);
        }

        foreach (var extension in AssociatedExtensions)
        {
            using var extKey = Registry.CurrentUser.CreateSubKey($@"{ClassesRootPath}\{extension}");
            extKey.SetValue(string.Empty, ProgId);
            using var openWith = Registry.CurrentUser.CreateSubKey(
                $@"{ClassesRootPath}\{extension}\OpenWithProgids");
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        NativeMethods.NotifyAssociationChanged();
    }

    public static void UnregisterCurrentUser()
    {
        foreach (var extension in AssociatedExtensions)
        {
            try
            {
                using var openWith = Registry.CurrentUser.OpenSubKey(
                    $@"{ClassesRootPath}\{extension}\OpenWithProgids", writable: true);
                openWith?.DeleteValue(ProgId, throwOnMissingValue: false);

                using var extKey = Registry.CurrentUser.OpenSubKey(
                    $@"{ClassesRootPath}\{extension}", writable: true);
                if (extKey?.GetValue(string.Empty) is string current &&
                    string.Equals(current, ProgId, StringComparison.OrdinalIgnoreCase))
                {
                    extKey.DeleteValue("", throwOnMissingValue: false);
                }
            }
            catch
            {
                // best effort
            }
        }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesRootPath}\{ProgId}", throwOnMissingSubKey: false);
        }
        catch
        {
            // best effort
        }

        NativeMethods.NotifyAssociationChanged();
    }

    private static class NativeMethods
    {
        private const uint SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0x0000;

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        public static void NotifyAssociationChanged()
        {
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
