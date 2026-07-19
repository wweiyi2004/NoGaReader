namespace NoGaReader.Services;

/// <summary>
/// Bilingual UI string table (zh-CN / en-US). Default Chinese.
/// </summary>
public static class UiStrings
{
    private static string _language = "zh-CN";

    public static string Language
    {
        get => _language;
        set => _language = Normalize(value);
    }

    public static bool IsEnglish => _language.StartsWith("en", StringComparison.OrdinalIgnoreCase);

    public static void ApplyFromSettings(string? languageCode)
    {
        Language = string.IsNullOrWhiteSpace(languageCode)
            ? System.Globalization.CultureInfo.CurrentUICulture.Name
            : languageCode;
    }

    private static string T(string zh, string en) => IsEnglish ? en : zh;

    // Shell / status
    public static string Ready => T("就绪 · 内容不会上传", "Ready · content stays on this PC");
    public static string LocalReader => T("本地阅读", "Local reading");
    public static string LocalReaderSubtitle => T("本地阅读器", "Local reader");
    public static string ConsoleTitle => T("NoGaReader 控制台", "NoGaReader Console");
    public static string ReaderTitle => T("阅读窗口 — NoGaReader", "Reader — NoGaReader");
    public static string LanguageUpdated => T(
        "界面语言已更新",
        "UI language updated");

    // Sidebar brand / actions
    public static string ImportBook => T("导入图书", "Import book");
    public static string ImportComic => T("导入漫画", "Import comic");
    public static string ReadingSpace => T("阅读空间", "Reading");
    public static string NavLibrary => T("书库", "Library");
    public static string NavReading => T("阅读中", "Reading");
    public static string NavRecent => T("最近阅读", "Recent");
    public static string NavConvert => T("转换", "Convert");
    public static string NavDocuments => T("文档", "Documents");
    public static string NavNotes => T("笔记", "Notes");
    public static string NavSettings => T("设置", "Settings");
    public static string SystemSection => T("系统", "System");
    public static string CloudSync => T("云同步", "Cloud sync");

    // Library panel
    public static string Library => T("我的书库", "My library");
    public static string SearchLibrary => T("搜索书库", "Search library");
    public static string EmptyLibrary => T("添加文件夹，建立本地书库", "Add a folder to build your library");
    public static string FileMissing => T("文件缺失", "Missing file");
    public static string EditMetadata => T("编辑书名与作者…", "Edit title & author…");
    public static string EditTags => T("编辑标签…", "Edit tags…");
    public static string RelocateSource => T("重新定位源文件…", "Relocate source…");
    public static string RemoveFromLibrary => T("从书库移除", "Remove from library");
    public static string BooksCount(int count) =>
        T($"{count:N0} 本本地书籍", $"{count:N0} local books");

    // Recent / TOC panels
    public static string Recent => T("最近阅读", "Recent");
    public static string RecentHint => T("从上次停留的位置继续", "Continue where you left off");
    public static string EmptyRecent => T("还没有最近阅读", "No recent books yet");
    public static string RemoveRecent => T("从最近列表移除", "Remove from recent");
    public static string Toc => T("目录", "Contents");
    public static string CurrentChapters => T("当前图书章节", "Current book chapters");
    public static string EmptyToc => T("打开电子书后显示目录", "Open a book to see contents");

    // Appearance / language
    public static string AppAppearance => T("应用外观", "Appearance");
    public static string UiLanguage => T("界面语言", "UI language");
    public static string LocalOnly => T("内容只保存在本机", "Content stays on this PC");
    public static string LangSystem => T("系统", "System");
    public static string LangChinese => T("中文", "中文");
    public static string LangEnglish => "EN";
    public static string ThemeSystem => T("跟随系统", "System");
    public static string ThemeLight => T("浅色", "Light");
    public static string ThemeDark => T("深色", "Dark");

    // Reader chrome
    public static string PreviousChapter => T("上一章", "Previous chapter");
    public static string NextChapter => T("下一章", "Next chapter");
    public static string PreviousPage => T("上一页", "Previous page");
    public static string NextPage => T("下一页", "Next page");
    public static string JumpPage => T("跳转到指定页/章", "Jump to page/chapter");
    public static string Search => T("搜索", "Search");
    public static string SearchBook => T("全书搜索（Ctrl+F）", "Search book (Ctrl+F)");
    public static string Highlight => T("高亮选中文字（右键选择颜色）", "Highlight selection (right-click for color)");
    public static string AddNote => T("添加笔记", "Add note");
    public static string Bookmark => T("添加或移除当前位置书签", "Toggle bookmark here");
    public static string NotesPanel => T("书签与笔记", "Bookmarks & notes");
    public static string ComicMode => T("漫画模式", "Comic mode");
    public static string ReaderSettings => T("阅读设置", "Reading settings");
    public static string Print => T("打印", "Print");
    public static string FullScreen => T("沉浸全屏（F11）", "Fullscreen (F11)");
    public static string ToggleSidebar => T("显示或隐藏侧栏", "Show or hide sidebar");
    public static string Close => T("关闭", "Close");

    // Notes panel
    public static string NotesTitle => T("笔记", "Notes");
    public static string NotesHint => T(
        "双击跳回原文；右键可编辑、改色或删除。",
        "Double-click to jump back; right-click to edit, recolor, or delete.");
    public static string ExportAnnotations => T("导出批注", "Export annotations");
    public static string ImportAnnotations => T("导入批注 JSON", "Import annotations JSON");
    public static string AnnotationCount(int count) =>
        T($"{count} 条", $"{count} items");

    // Search panel
    public static string SearchResults => T("搜索结果", "Search results");
    public static string SearchPlaceholder => T("输入关键词搜索整本书", "Type a keyword to search this book");
    public static string SearchEmpty => T("整本书中没有找到匹配内容", "No matches in this book");
    public static string ResultCount(int count) =>
        T($"{count} 个结果", $"{count} results");

    // Welcome
    public static string WelcomeBack => T("欢迎回来", "Welcome back");
    public static string WelcomePrompt => T("今天想读点什么？", "What would you like to read?");
    public static string WelcomeHint => T(
        "你的书、阅读进度和批注都只保存在这台电脑上。",
        "Your books, progress, and notes stay on this PC.");
    public static string TotalReading => T("累计阅读", "Reading time");
    public static string LocalBooks => T("本地藏书", "Books");
    public static string ReadingDays => T("阅读天数", "Days read");
    public static string ContinueReading => T("继续上次阅读", "Continue reading");
    public static string StartReading => T("开始阅读", "Start reading");
    public static string StartReadingHint => T(
        "支持 EPUB、PDF、Markdown、FB2 和漫画；也可以把文件直接拖到窗口。",
        "Supports EPUB, PDF, Markdown, FB2, and comics. You can also drag files here.");
    public static string ShortcutHint => T(
        "Ctrl+O 打开  ·  F11 沉浸阅读",
        "Ctrl+O open  ·  F11 fullscreen");
    public static string Minutes(int n) => T($"{n} 分钟", $"{n} min");
    public static string BooksShort(int n) => T($"{n} 本", $"{n} books");
    public static string Days(int n) => T($"{n} 天", $"{n} days");

    // Reader settings panel
    public static string ReadingBackground => T("阅读背景", "Reading background");
    public static string ReadingMode => T("阅读方式", "Reading mode");
    public static string FontSize => T("正文字号", "Font size");
    public static string LineHeight => T("行距", "Line height");
    public static string ContentWidth => T("版心宽度", "Content width");
    public static string ReaderThemeHint => T(
        "“跟随”会匹配应用明暗主题；其他选项保留为自定义阅读背景。",
        "“Follow app” matches the app theme; other options are custom backgrounds.");
    public static string ThemeFollow => T("跟随", "Follow");
    public static string ThemePaper => T("纸张", "Paper");
    public static string ThemeBright => T("明亮", "Light");
    public static string ThemeNight => T("夜间", "Dark");
    public static string FlowPaged => T("分页", "Paged");
    public static string FlowScroll => T("滚动", "Scroll");

    // Comic panel
    public static string ComicLayout => T("阅读布局", "Layout");
    public static string Pages(int n) => T($"{n} 页", $"{n} pages");

    // Status / runtime messages
    public static string FinishedClosing => T("已读完，正在关闭阅读窗口…", "Finished — closing reader…");
    public static string ReachedEnd => T("已读到结尾", "Reached the end");
    public static string PageFind(string q) => T($"页内查找：{q}", $"Find in page: {q}");
    public static string PageFound(string q) => T($"页内找到：{q}", $"Found in page: {q}");
    public static string PageNotFound(string q) => T($"页内未找到：{q}", $"Not found in page: {q}");
    public static string BookSearchFound(int n, string q) =>
        T($"全书找到 {n} 处：{q}", $"Found {n} matches: {q}");
    public static string BookSearchMiss(string q) =>
        T($"整本书未找到：{q}", $"No matches: {q}");
    public static string BuildingIndex => T("正在准备全书索引…", "Building search index…");
    public static string TagsUpdated(string tags) => T($"已更新标签：{tags}", $"Tags updated: {tags}");
    public static string TagsCleared => T("已清除标签", "Tags cleared");
    public static string MetadataUpdated => T("已更新书名与作者", "Title and author updated");
    public static string ImportedAnnotations(int n) => T($"已导入 {n} 条批注", $"Imported {n} annotations");
    public static string ImportedAnnotationsSkipped(int n, int skipped) =>
        T($"已导入 {n} 条批注，跳过 {skipped} 条重复", $"Imported {n} annotations, skipped {skipped} duplicates");
    public static string NoNewAnnotations(int skipped) =>
        T($"没有新批注可导入（跳过 {skipped} 条重复）", $"No new annotations (skipped {skipped} duplicates)");
    public static string ChapterProgress(int current, int total) =>
        T($"第 {current} / {total} 章", $"Chapter {current} / {total}");

    public static string FormatProgress(string format, string progress) =>
        IsEnglish ? $"{format} · {progress}" : $"{format} · {progress}";

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "zh-CN";
        }

        value = value.Trim();
        if (value.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return "en-US";
        }

        return "zh-CN";
    }
}
