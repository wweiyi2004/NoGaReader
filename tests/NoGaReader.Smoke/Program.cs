using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NoGaReader.Models;
using NoGaReader.Services;

var testRoot = Path.Combine(AppContext.BaseDirectory, ".smoke-temp", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
Environment.SetEnvironmentVariable(
    "NOGAREADER_DATA_DIR",
    Path.Combine(testRoot, "AppData"),
    EnvironmentVariableTarget.Process);
var generatedSources = new List<(string Path, string Category)>();

try
{
    var loader = new DocumentLoader();
    Assert(typeof(AppSettings).Assembly.GetName().Name == "NoGaReader.Core" &&
           typeof(PortableDocumentLoader).Assembly == typeof(AppSettings).Assembly &&
           typeof(DocumentLoader).Assembly != typeof(AppSettings).Assembly,
        "Cross-platform core assembly boundary");
    var containmentRoot = Path.Combine(Path.GetTempPath(), "nogareader-smoke-root");
    Assert(PathSemantics.IsInside(containmentRoot, Path.Combine(containmentRoot, "child.epub")) &&
           PathSemantics.IsInside(containmentRoot, Path.Combine(containmentRoot, "nested", "page.png")) &&
           !PathSemantics.IsInside(containmentRoot, containmentRoot) &&
           !PathSemantics.IsInside(containmentRoot, Path.Combine(containmentRoot, "..", "escape.epub")) &&
           !PathSemantics.IsInside(containmentRoot, containmentRoot + "-sibling"),
        "PathSemantics containment is strict: children only, no root, no escapes");
    var settingsStore = new SettingsStore();
    settingsStore.Save(new AppSettings
    {
        ComicDisplay = ComicDisplayMode.Double,
        ComicDirection = ComicReadingDirection.LeftToRight,
        ComicFit = ComicFitMode.Width,
        ComicCoverSinglePage = false,
        ComicScale = 1.7
    });
    var comicSettings = settingsStore.Load();
    Assert(comicSettings.ComicDisplay == ComicDisplayMode.Double, "Comic display setting persistence");
    Assert(comicSettings.ComicDirection == ComicReadingDirection.LeftToRight, "Comic direction setting persistence");
    Assert(comicSettings.ComicFit == ComicFitMode.Width, "Comic fit setting persistence");
    Assert(!comicSettings.ComicCoverSinglePage && NearlyEqual(comicSettings.ComicScale, 1.7),
        "Comic cover and scale setting persistence");

    settingsStore.Save(new AppSettings
    {
        ReaderTheme = ReaderThemeMode.Paper,
        ReaderThemePreferenceInitialized = false,
        TotalReadingSeconds = -120,
        ReadingDates = ["2026-07-17", "2026-07-17", "not-a-date"]
    });
    var migratedSettings = settingsStore.Load();
    Assert(migratedSettings.ReaderTheme == ReaderThemeMode.Auto &&
           migratedSettings.ReaderThemePreferenceInitialized,
        "Legacy reader theme migrates once to application-following mode");
    Assert(NearlyEqual(migratedSettings.TotalReadingSeconds, 0) &&
           migratedSettings.ReadingDates.SequenceEqual(["2026-07-17"]),
        "Reading statistics sanitization");

    settingsStore.Save(new AppSettings
    {
        ReaderTheme = ReaderThemeMode.Dark,
        ReaderThemePreferenceInitialized = true
    });
    var customizedReaderTheme = settingsStore.Load();
    Assert(customizedReaderTheme.ReaderTheme == ReaderThemeMode.Dark,
        "Explicit reader background remains customized after migration");

    TestSettingsCloneCompleteness();
    await TestConcurrentSettingsSaveAsync();

    settingsStore.Save(new AppSettings
    {
        ComicDisplay = (ComicDisplayMode)99,
        ComicDirection = (ComicReadingDirection)99,
        ComicFit = (ComicFitMode)99,
        ComicScale = 9
    });
    var sanitizedComicSettings = settingsStore.Load();
    Assert(sanitizedComicSettings.ComicDisplay == ComicDisplayMode.Single, "Invalid comic display fallback");
    Assert(sanitizedComicSettings.ComicDirection == ComicReadingDirection.RightToLeft,
        "Invalid comic direction fallback");
    Assert(sanitizedComicSettings.ComicFit == ComicFitMode.Height, "Invalid comic fit fallback");
    Assert(NearlyEqual(sanitizedComicSettings.ComicScale, 3), "Comic scale clamp");

    var textPath = Path.Combine(testRoot, "sample.txt");
    File.WriteAllText(textPath, "第一行\nNoGaReader smoke test", Encoding.UTF8);
    generatedSources.Add((textPath, "text"));
    var text = await loader.LoadAsync(textPath);
    Assert(text.Kind == ReaderDocumentKind.Text, "TXT kind");
    Assert(File.Exists(text.CurrentSection.FullPath), "TXT generated HTML");
    await AssertCacheHitDoesNotRewrite(loader, textPath, text.CurrentSection.FullPath, "TXT generated cache hit");

    var markdownImagePath = Path.Combine(testRoot, "diagram.png");
    File.WriteAllBytes(markdownImagePath, PixelPng());
    var markdownPath = Path.Combine(testRoot, "sample.md");
    File.WriteAllText(markdownPath, """
        # Markdown 标题

        这里有 **粗体**、一个 [危险链接](javascript:alert(1)) 和本地图片：

        ![示意图](diagram.png)

        | 项目 | 状态 |
        | --- | --- |
        | Markdown | 正常 |

        <script>alert('must be escaped')</script>
        """, Encoding.UTF8);
    generatedSources.Add((markdownPath, "markdown"));
    var markdown = await loader.LoadAsync(markdownPath);
    Assert(markdown.Kind == ReaderDocumentKind.Markdown, "Markdown kind");
    var markdownHtml = File.ReadAllText(markdown.CurrentSection.FullPath);
    Assert(markdownHtml.Contains("<h1", StringComparison.OrdinalIgnoreCase), "Markdown heading rendering");
    Assert(markdownHtml.Contains("<strong>", StringComparison.OrdinalIgnoreCase), "Markdown emphasis rendering");
    Assert(markdownHtml.Contains("<table", StringComparison.OrdinalIgnoreCase), "Markdown table rendering");
    Assert(markdownHtml.Contains("assets/", StringComparison.OrdinalIgnoreCase), "Markdown local image copy");
    Assert(!markdownHtml.Contains("<script", StringComparison.OrdinalIgnoreCase), "Markdown raw script disabled");
    Assert(!markdownHtml.Contains("javascript:", StringComparison.OrdinalIgnoreCase), "Markdown dangerous link blocked");
    await AssertCacheHitDoesNotRewrite(loader, markdownPath, markdown.CurrentSection.FullPath, "Markdown generated cache hit");

    var largeTextPath = Path.Combine(testRoot, "large-sample.txt");
    File.WriteAllText(
        largeTextPath,
        string.Concat(Enumerable.Repeat("大文本分段布局测试。\n", 80_000)),
        Encoding.UTF8);
    generatedSources.Add((largeTextPath, "text"));
    var largeText = await loader.LoadAsync(largeTextPath);
    Assert(largeText.Sections.Count > 1 && largeText.TableOfContents.Count == largeText.Sections.Count,
        "Large TXT is split into bounded reader sections");

    var htmlPath = Path.Combine(testRoot, "sample.html");
    File.WriteAllText(htmlPath, "<!doctype html><title>Sample</title><p>Hello</p>", Encoding.UTF8);
    var html = await loader.LoadAsync(htmlPath);
    Assert(html.Kind == ReaderDocumentKind.Html && !html.EnableScriptExecution,
        "Direct HTML is routed without document script execution");

    var pdfPath = Path.Combine(testRoot, "sample.pdf");
    File.WriteAllBytes(pdfPath, "%PDF-1.4\n%%EOF"u8.ToArray());
    var pdf = await loader.LoadAsync(pdfPath);
    Assert(pdf.Kind == ReaderDocumentKind.Pdf, "PDF routing");

    var recentStoreA = new RecentStore();
    var recentStoreB = new RecentStore();
    _ = recentStoreA.Load();
    _ = recentStoreB.Load();
    recentStoreA.Touch(text, 0, 0.1, 1.0);
    recentStoreB.Touch(pdf, 0, 0, 1.0, currentPage: 7);
    var sharedRecents = new RecentStore().Load();
    Assert(sharedRecents.Any(item => string.Equals(
               Path.GetFullPath(item.Path), Path.GetFullPath(textPath), StringComparison.OrdinalIgnoreCase)) &&
           sharedRecents.Any(item => string.Equals(
               Path.GetFullPath(item.Path), Path.GetFullPath(pdfPath), StringComparison.OrdinalIgnoreCase)),
        "RecentStore instances merge updates instead of overwriting each other");
    Assert(sharedRecents.Single(item => string.Equals(
               Path.GetFullPath(item.Path), Path.GetFullPath(pdfPath), StringComparison.OrdinalIgnoreCase)).CurrentPage == 7,
        "RecentStore persists fixed-layout page numbers");

    var fb2Path = Path.Combine(testRoot, "sample.fb2");
    File.WriteAllText(fb2Path, """
        <?xml version="1.0" encoding="utf-8"?>
        <FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0">
          <description><title-info><book-title>测试 FB2</book-title></title-info></description>
          <body>
            <section><title><p>第一章</p></title><p>第一章正文。</p></section>
            <section><title><p>第二章</p></title><p><strong>第二章</strong>正文。</p></section>
          </body>
        </FictionBook>
        """, Encoding.UTF8);
    generatedSources.Add((fb2Path, "fb2"));
    var fb2 = await loader.LoadAsync(fb2Path);
    Assert(fb2.Kind == ReaderDocumentKind.FictionBook, "FB2 kind");
    Assert(fb2.Title == "测试 FB2", "FB2 metadata title");
    Assert(fb2.Sections.Count == 2, "FB2 section count");
    await AssertCacheHitDoesNotRewrite(loader, fb2Path, fb2.CurrentSection.FullPath, "FB2 generated cache hit");

    var epubPath = Path.Combine(testRoot, "sample.epub");
    CreateEpub(epubPath);
    generatedSources.Add((epubPath, "epub"));
    var epub = await loader.LoadAsync(epubPath);
    Assert(epub.Kind == ReaderDocumentKind.Epub, "EPUB kind");
    Assert(epub.Title == "测试 EPUB", "EPUB metadata title");
    Assert(epub.Sections.Count == 2, "EPUB spine count");
    Assert(epub.Sections[0].Title == "开篇", "EPUB navigation title");
    var sanitizedChapter = File.ReadAllText(epub.Sections[0].FullPath);
    Assert(!sanitizedChapter.Contains("<script", StringComparison.OrdinalIgnoreCase), "EPUB script removal");
    Assert(!sanitizedChapter.Contains("<iframe", StringComparison.OrdinalIgnoreCase), "EPUB iframe removal");
    Assert(!sanitizedChapter.Contains("onclick", StringComparison.OrdinalIgnoreCase), "EPUB event handler removal");
    Assert(!sanitizedChapter.Contains("data:text/html", StringComparison.OrdinalIgnoreCase),
        "EPUB active data navigation removal");
    var unlistedPayload = Path.Combine(epub.RootDirectory, "OEBPS", "payload.xhtml");
    Assert(File.Exists(unlistedPayload) &&
           !File.ReadAllText(unlistedPayload).Contains("<script", StringComparison.OrdinalIgnoreCase),
        "EPUB sanitizes unlisted active documents");
    await AssertCacheHitDoesNotRewrite(loader, epubPath, epub.CurrentSection.FullPath, "EPUB sanitized cache hit");

    var cbzPath = Path.Combine(testRoot, "sample.cbz");
    CreateCbz(cbzPath);
    generatedSources.Add((cbzPath, "comic"));
    var cbz = await loader.LoadAsync(cbzPath);
    Assert(cbz.Kind == ReaderDocumentKind.Comic, "CBZ kind");
    Assert(cbz.Sections.Count == 2, "CBZ image count");
    Assert(cbz.ComicPages.Count == 2, "CBZ comic page model");
    Assert(cbz.ComicPages[0].FullPath.EndsWith("page2.png", StringComparison.OrdinalIgnoreCase), "CBZ natural page order");
    var comicShell = File.ReadAllText(cbz.Sections[0].FullPath);
    Assert(comicShell.Contains("data-start-page=\"0\"", StringComparison.Ordinal), "Comic shell start page");
    var comicViewerRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cbz.Sections[0].FullPath)!, ".."));
    using (var comicManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(comicViewerRoot, "manifest.json"))))
    {
        var manifestPages = comicManifest.RootElement.GetProperty("pages");
        Assert(manifestPages.GetArrayLength() == 2, "Comic manifest page count");
        Assert(manifestPages[0].GetString()!.EndsWith("page2.png", StringComparison.OrdinalIgnoreCase),
            "Comic manifest natural order");
    }
    Assert(File.ReadAllText(Path.Combine(comicViewerRoot, "comic-reader.js"))
        .Contains("window.__nogareaderComic", StringComparison.Ordinal), "Comic runtime API");

    foreach (var extension in new[] { ".cbr", ".cb7", ".zip", ".rar" })
    {
        var archivePath = Path.Combine(testRoot, $"signature-detected{extension}");
        File.Copy(cbzPath, archivePath);
        generatedSources.Add((archivePath, "comic"));
        var archiveComic = await loader.LoadAsync(archivePath);
        Assert(archiveComic.Kind == ReaderDocumentKind.Comic, $"{extension} signature routing");
        Assert(archiveComic.ComicPages.Count == 2, $"{extension} page extraction");
    }

    var maliciousComicPath = Path.Combine(testRoot, "malicious.cbz");
    CreateMaliciousComic(maliciousComicPath);
    generatedSources.Add((maliciousComicPath, "comic"));
    var maliciousComicRejected = false;
    try
    {
        await loader.LoadAsync(maliciousComicPath);
    }
    catch (InvalidDataException)
    {
        maliciousComicRejected = true;
    }
    Assert(maliciousComicRejected, "Comic traversal rejection");

    var comicFolderPath = Path.Combine(testRoot, "comic-folder");
    Directory.CreateDirectory(comicFolderPath);
    File.WriteAllBytes(Path.Combine(comicFolderPath, "page10.png"), PixelPng());
    File.WriteAllBytes(Path.Combine(comicFolderPath, "page2.png"), PixelPng());
    File.WriteAllText(Path.Combine(comicFolderPath, "notes.txt"), "not a comic page", Encoding.UTF8);
    var folderComic = await loader.LoadAsync(comicFolderPath);
    Assert(folderComic.Kind == ReaderDocumentKind.Comic, "Comic folder kind");
    Assert(folderComic.ComicContentRootDirectory == Path.GetFullPath(comicFolderPath),
        "Comic folder content root");
    Assert(folderComic.ComicPages.Count == 2, "Comic folder ignores non-images");
    Assert(folderComic.ComicPages[0].FullPath.EndsWith("page2.png", StringComparison.OrdinalIgnoreCase),
        "Comic folder natural page order");
    var folderViewerRoot = Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(folderComic.Sections[0].FullPath)!, ".."));
    using (var folderManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folderViewerRoot, "manifest.json"))))
    {
        var firstUrl = folderManifest.RootElement.GetProperty("pages")[0].GetString();
        Assert(firstUrl is not null && firstUrl.StartsWith(
            "https://comic-content.nogareader.local/", StringComparison.Ordinal),
            "Comic folder uses isolated content host");
    }
    File.WriteAllBytes(Path.Combine(comicFolderPath, "page3.png"), PixelPng());
    var refreshedFolderComic = await loader.LoadAsync(comicFolderPath);
    Assert(refreshedFolderComic.ComicPages.Count == 3, "Comic folder cache invalidation");
    Assert(refreshedFolderComic.ComicPages[1].FullPath.EndsWith("page3.png", StringComparison.OrdinalIgnoreCase),
        "Comic folder refreshed natural order");

    var emptyComicFolder = Path.Combine(testRoot, "empty-comic-folder");
    Directory.CreateDirectory(emptyComicFolder);
    var emptyComicFolderRejected = false;
    try
    {
        await loader.LoadAsync(emptyComicFolder);
    }
    catch (InvalidDataException)
    {
        emptyComicFolderRejected = true;
    }
    Assert(emptyComicFolderRejected, "Empty comic folder rejection");

    var maliciousPath = Path.Combine(testRoot, "malicious.epub");
    CreateMaliciousEpub(maliciousPath);
    generatedSources.Add((maliciousPath, "epub"));
    var rejected = false;
    try
    {
        await loader.LoadAsync(maliciousPath);
    }
    catch (InvalidDataException)
    {
        rejected = true;
    }

    Assert(rejected, "ZIP traversal rejection");
    Assert(!File.Exists(Path.Combine(AppPaths.CacheRoot, "epub", "escape.txt")), "ZIP traversal did not write outside cache");

    await TestLibraryDatabaseAsync(testRoot);
    await TestPortableLibraryServiceAsync(testRoot);
    TestMobileReaderPresenter(testRoot);
    TestMobileReaderScripts();
    TestMobileLibraryPresenter();
    await TestBookSearchIndexerAsync(testRoot);
    TestReaderRuntimeAnnotationApi();
    await TestLibraryScannerAsync(testRoot);
    await TestAnnotationExportAsync(testRoot);
    TestTocNodeFlatten(testRoot);

    // V0.8 conversion plugin surface
    var conversionSettings = new AppSettings();
    var converter = new CalibreConverter(conversionSettings);
    Assert(converter.OutputFormats.Count >= 5, "Conversion output formats");
    Assert(converter.CanConvert(".epub", ".pdf") == converter.IsAvailable,
        "CanConvert reflects runtime availability");
    Assert(!CalibreConverter.IsConvertibleInput(".doc") &&
           !CalibreConverter.IsConvertibleInput(".xps") &&
           !CalibreConverter.IsConvertibleInput(".oxps") &&
           converter.OutputFormats.All(format => format.NormalizedExtension != ".oeb"),
        "Calibre format matrix excludes unsupported DOC/XPS inputs and directory-based OEB output");
    Assert(CalibreConverter.IsKindleExtension(".mobi") &&
           CalibreConverter.IsKindleExtension(".azw3") &&
           CalibreConverter.IsKindleExtension(".azw"),
        "Kindle extension recognition");
    Assert(DocumentLoader.IsSupported(Path.Combine(testRoot, "book.mobi")) &&
           DocumentLoader.IsSupported(Path.Combine(testRoot, "book.azw3")) &&
           DocumentLoader.IsSupported(Path.Combine(testRoot, "book.azw4")),
        "DocumentLoader supports Kindle extensions");
    Assert(DocumentLoader.OpenFileFilter.Contains("*.mobi", StringComparison.OrdinalIgnoreCase) &&
           DocumentLoader.OpenFileFilter.Contains("*.azw3", StringComparison.OrdinalIgnoreCase) &&
           DocumentLoader.OpenFileFilter.Contains("*.azw4", StringComparison.OrdinalIgnoreCase),
        "Open filter lists Kindle formats");
    Assert(DocumentFormatSupport.IsMobileMvpSupported("book.epub") &&
           DocumentFormatSupport.IsMobileMvpSupported("book.pdf") &&
           DocumentFormatSupport.IsMobileMvpSupported("book.cbz") &&
           !DocumentFormatSupport.IsMobileMvpSupported("book.mobi") &&
           !DocumentFormatSupport.IsMobileMvpSupported("book.xps"),
        "Mobile MVP format catalog only advertises planned capabilities");

    var missingKindlePath = Path.Combine(testRoot, "missing-engine.mobi");
    File.WriteAllBytes(missingKindlePath, [0x00, 0x01, 0x02, 0x03]);
    generatedSources.Add((missingKindlePath, "converted"));
    if (!converter.IsAvailable)
    {
        var kindleRejected = false;
        try
        {
            await loader.LoadAsync(missingKindlePath);
        }
        catch (InvalidOperationException exception)
        {
            kindleRejected = exception.Message.Contains("Calibre", StringComparison.OrdinalIgnoreCase) ||
                             exception.Message.Contains("ebook-convert", StringComparison.OrdinalIgnoreCase);
        }
        Assert(kindleRejected, "Kindle open fails clearly without Calibre runtime");

        var convertResult = await converter.ConvertAsync(
            textPath,
            ".epub",
            Path.Combine(testRoot, "convert-out"));
        Assert(!convertResult.Succeeded &&
               !string.IsNullOrWhiteSpace(convertResult.ErrorMessage),
            "Convert without runtime returns friendly failure");
    }
    else
    {
        var convertOut = Path.Combine(testRoot, "convert-out");
        Directory.CreateDirectory(convertOut);
        var convertResult = await converter.ConvertAsync(textPath, ".epub", convertOut);
        Assert(convertResult.Succeeded &&
               convertResult.OutputPath is not null &&
               File.Exists(convertResult.OutputPath) &&
               new FileInfo(convertResult.OutputPath).Length > 0,
            "TXT converts to EPUB when Calibre is available");
        generatedSources.Add((convertResult.OutputPath!, "converted"));
    }

    var conversionSettingsStore = new SettingsStore();
    conversionSettingsStore.Save(new AppSettings
    {
        CalibreEbookConvertPath = Path.Combine(testRoot, "not-real", "ebook-convert.exe"),
        ConversionDefaultTargetExtension = "PDF",
        ConversionOutputDirectory = Path.Combine(testRoot, "out-dir")
    });
    var loadedConversionSettings = conversionSettingsStore.Load();
    Assert(loadedConversionSettings.ConversionDefaultTargetExtension == ".pdf",
        "Conversion target extension normalization");
    Assert(loadedConversionSettings.ConversionOutputDirectory.EndsWith("out-dir", StringComparison.OrdinalIgnoreCase),
        "Conversion output directory persistence");
    conversionSettingsStore.Save(new AppSettings
    {
        ConversionDefaultTargetExtension = "PMLZ"
    });
    Assert(conversionSettingsStore.Load().ConversionDefaultTargetExtension == ".pmlz",
        "New conversion output formats survive settings normalization");

    // V0.9 document editor Open XML round-trip
    Assert(OfficeDocumentService.CanEditNatively(".docx") &&
           OfficeDocumentService.CanEditNatively(".rtf") &&
           OfficeDocumentService.CanEditNatively(".txt") &&
           !OfficeDocumentService.CanEditNatively(".html") &&
           !OfficeDocumentService.CanEditNatively(".doc"),
        "Office native edit matrix");
    var htmlEditRejected = false;
    try
    {
        _ = OfficeDocumentService.Load(htmlPath);
    }
    catch (NotSupportedException)
    {
        htmlEditRejected = true;
    }

    Assert(htmlEditRejected &&
           !OfficeDocumentService.SaveFilter.Contains("HTML", StringComparison.OrdinalIgnoreCase),
        "HTML remains a safe reader format and is not advertised as a native editor format");
    var docxPath = Path.Combine(testRoot, "editor-sample.docx");
    var doc = OfficeDocumentService.CreateBlank("Smoke 标题");
    doc.Blocks.Add(new System.Windows.Documents.Paragraph(
        new System.Windows.Documents.Run("粗体段落") { FontWeight = System.Windows.FontWeights.Bold }));
    OfficeDocumentService.Save(doc, docxPath);
    generatedSources.Add((docxPath, "office"));
    Assert(File.Exists(docxPath) && new FileInfo(docxPath).Length > 0, "DOCX save creates file");
    var reloaded = OfficeDocumentService.Load(docxPath);
    var reloadedText = OfficeDocumentService.GetPlainText(reloaded);
    Assert(reloadedText.Contains("Smoke 标题", StringComparison.Ordinal) &&
           reloadedText.Contains("粗体段落", StringComparison.Ordinal),
        "DOCX round-trip preserves text");
    var imageDocxPath = Path.Combine(testRoot, "editor-image.docx");
    TestDocxImageRoundTrip(imageDocxPath);
    generatedSources.Add((imageDocxPath, "office"));
    var rtfPath = Path.Combine(testRoot, "editor-sample.rtf");
    OfficeDocumentService.Save(reloaded, rtfPath);
    generatedSources.Add((rtfPath, "office"));
    var rtfReloaded = OfficeDocumentService.Load(rtfPath);
    Assert(OfficeDocumentService.GetPlainText(rtfReloaded).Contains("Smoke", StringComparison.Ordinal),
        "RTF round-trip preserves text");
    var txtPath = Path.Combine(testRoot, "editor-sample.txt");
    OfficeDocumentService.Save(OfficeDocumentService.CreateBlank("纯文本"), txtPath);
    generatedSources.Add((txtPath, "office"));
    Assert(OfficeDocumentService.GetPlainText(OfficeDocumentService.Load(txtPath))
            .Contains("纯文本", StringComparison.Ordinal),
        "TXT round-trip");
    Assert(OfficeDocumentService.CountWords("Hello world 测试") >= 2, "Word count");
    Assert(!OfficeDocumentService.RequiresSafeSaveAs(docxPath),
        "Simple editor-created DOCX can be overwritten safely");
    var complexDocxPath = Path.Combine(testRoot, "editor-complex.docx");
    File.Copy(docxPath, complexDocxPath);
    AddHyperlinkToDocx(complexDocxPath);
    generatedSources.Add((complexDocxPath, "office"));
    Assert(OfficeDocumentService.RequiresSafeSaveAs(complexDocxPath),
        "DOCX with unsupported relationships requires safe Save As");

    // V1.0 productization surfaces
    Assert(DocumentLoader.IsSupported(Path.Combine(testRoot, "sample.cbt")), "DocumentLoader supports CBT");
    Assert(DocumentLoader.OpenFileFilter.Contains("*.cbt", StringComparison.OrdinalIgnoreCase),
        "Open filter lists CBT");
    Assert(!string.IsNullOrWhiteSpace(SingleInstanceService.MutexName) &&
           !string.IsNullOrWhiteSpace(SingleInstanceService.PipeName),
        "Single-instance identifiers");

    Assert(DocumentLoader.IsSupported(Path.Combine(testRoot, "a.chm")) &&
           DocumentLoader.IsSupported(Path.Combine(testRoot, "a.xps")) &&
           DocumentLoader.IsSupported(Path.Combine(testRoot, "a.djvu")),
        "DocumentLoader supports CHM/XPS/DJVU extensions");
    Assert(DocumentLoader.OpenFileFilter.Contains("*.chm", StringComparison.OrdinalIgnoreCase) &&
           DocumentLoader.OpenFileFilter.Contains("*.djvu", StringComparison.OrdinalIgnoreCase),
        "Open filter lists fixed-layout formats");

    // A real two-page XPS can contain resource images that are not standalone pages.
    var xpsPath = Path.Combine(testRoot, "sample.xps");
    CreateXpsWithFixedPagesAndResourceImage(xpsPath);
    generatedSources.Add((xpsPath, "xps"));
    ReaderSession? xpsSession = null;
    try
    {
        xpsSession = await loader.LoadAsync(xpsPath);
    }
    catch (InvalidOperationException exception)
    {
        Assert(exception.Message.Contains("XPS", StringComparison.OrdinalIgnoreCase) &&
               (exception.Message.Contains("PDF", StringComparison.OrdinalIgnoreCase) ||
                exception.Message.Contains("转换", StringComparison.Ordinal)),
            "XPS fails with explicit conversion guidance when the rendering engine is unavailable");
    }
    if (xpsSession is not null)
    {
        Assert(xpsSession.Sections.Count == 2 && xpsSession.ComicPages.Count == 2,
            "XPS renders declared FixedPages instead of treating resource images as pages");
        Assert(xpsSession.ComicPages.All(page =>
                   Path.GetExtension(page.FullPath).Equals(".png", StringComparison.OrdinalIgnoreCase) &&
                   !File.ReadAllBytes(page.FullPath).SequenceEqual(PixelPng())),
            "XPS pages are rendered output rather than raw package resources");
        Assert(!File.ReadAllBytes(xpsSession.ComicPages[0].FullPath)
                .SequenceEqual(File.ReadAllBytes(xpsSession.ComicPages[1].FullPath)),
            "Distinct XPS FixedPages produce distinct rendered pixels");
    }

    var excessiveXpsPath = Path.Combine(testRoot, "too-many-entries.xps");
    CreateXpsWithTooManyEntries(excessiveXpsPath);
    generatedSources.Add((excessiveXpsPath, "xps"));
    var excessiveXpsRejected = false;
    try
    {
        _ = await loader.LoadAsync(excessiveXpsPath);
    }
    catch (InvalidDataException)
    {
        excessiveXpsRejected = true;
    }
    Assert(excessiveXpsRejected, "XPS entry count safety limit");

    // Cloud sync folder merge
    var syncRoot = Path.Combine(testRoot, "sync-folder");
    Directory.CreateDirectory(syncRoot);
    var syncSettings = new AppSettings
    {
        SyncEnabled = true,
        SyncProvider = SyncProviderKind.Folder,
        SyncFolderPath = syncRoot,
        SyncIncludeAnnotations = true,
        SyncIncludeSettings = true,
        ReaderFontSize = 20
    };
    var syncStore = new SettingsStore();
    // isolate settings path already via NOGAREADER_DATA_DIR
    syncStore.Save(syncSettings);
    syncSettings = syncStore.Load();
    var db = new LibraryDatabase();
    await db.InitializeAsync();
    var book = await db.UpsertBookAsync(new LibraryBook
    {
        Path = textPath,
        Title = "Sync Book",
        Format = "TXT",
        FileSize = new FileInfo(textPath).Length,
        SectionCount = 1
    });
    await db.SaveReaderLocationAsync(new ReaderLocation
    {
        BookId = book.Id,
        SectionIndex = 0,
        SectionProgress = 0.42,
        DocumentProgress = 0.42,
        TextQuote = "sync-quote"
    });
    await db.UpsertAnnotationAsync(new Annotation
    {
        BookId = book.Id,
        Type = AnnotationType.Highlight,
        SectionIndex = 0,
        SectionProgress = 0.1,
        SelectedText = "第一行",
        Color = "#FFD54F"
    });

    var sync = new CloudSyncService(syncSettings);
    var result = await sync.SynchronizeAsync(db, syncStore);
    Assert(result.Succeeded, "Sync upload succeeds");
    Assert(File.Exists(Path.Combine(syncRoot, "nogareader-sync.json")), "Sync manifest written");

    // Simulate older local state so remote newer progress wins on merge.
    await db.SaveReaderLocationAsync(new ReaderLocation
    {
        BookId = book.Id,
        SectionIndex = 0,
        SectionProgress = 0,
        DocumentProgress = 0,
        UpdatedUtc = DateTimeOffset.UtcNow.AddDays(-2)
    });
    var result2 = await sync.SynchronizeAsync(db, syncStore);
    Assert(result2.Succeeded, "Sync download succeeds");
    var restored = await db.GetReaderLocationAsync(book.Id);
    Assert(restored is not null && restored.SectionProgress > 0.4, "Sync restores newer remote progress");

    var optOutRoot = Path.Combine(testRoot, "sync-opt-out");
    var optOutSettings = new AppSettings
    {
        SyncEnabled = true,
        SyncProvider = SyncProviderKind.Folder,
        SyncFolderPath = optOutRoot,
        SyncIncludeAnnotations = false,
        SyncIncludeSettings = false
    };
    var optOutSync = new CloudSyncService(optOutSettings);
    Assert((await optOutSync.SynchronizeAsync(db, syncStore)).Succeeded,
        "Sync with annotation opt-out succeeds");
    using (var optOutManifest = JsonDocument.Parse(
               await File.ReadAllTextAsync(Path.Combine(optOutRoot, "nogareader-sync.json"))))
    {
        Assert(optOutManifest.RootElement.GetProperty("books").EnumerateArray()
                .All(item => item.GetProperty("annotations").GetArrayLength() == 0),
            "Annotation opt-out excludes local annotations from sync manifest");
    }

    await TestCrossDeviceSyncAsync(testRoot, syncStore);

    var cbtPath = Path.Combine(testRoot, "sample.cbt");
    CreateCbt(cbtPath);
    generatedSources.Add((cbtPath, "comic"));
    var cbt = await loader.LoadAsync(cbtPath);
    Assert(cbt.Kind == ReaderDocumentKind.Comic && cbt.ComicPages.Count == 1, "CBT loads as comic");

    Console.WriteLine(
        "NoGaReader smoke tests passed: document loaders, comic archives/folders/runtime/safety, SQLite library, " +
        "multi-hit search, relocation, annotation export/runtime, library scanning, hierarchical TOC, conversion plugin, document editor, single-instance/CBT, CHM/XPS/DJVU, cloud sync.");
}
finally
{
    SqliteConnection.ClearAllPools();

    foreach (var (sourcePath, category) in generatedSources)
    {
        if (!File.Exists(sourcePath))
        {
            continue;
        }

        var cacheDirectory = AppPaths.GetDocumentCacheDirectory(sourcePath, category);
        if (Directory.Exists(cacheDirectory) && AppPaths.IsInsideCache(cacheDirectory))
        {
            Directory.Delete(cacheDirectory, true);
        }
    }

    var normalizedTestRoot = Path.GetFullPath(testRoot);
    var normalizedBaseRoot = Path.GetFullPath(AppContext.BaseDirectory)
        .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (normalizedTestRoot.StartsWith(normalizedBaseRoot, StringComparison.OrdinalIgnoreCase) &&
        normalizedTestRoot.Contains(".smoke-temp", StringComparison.OrdinalIgnoreCase))
    {
        Directory.Delete(normalizedTestRoot, true);
    }
}

static void TestSettingsCloneCompleteness()
{
    var source = new AppSettings
    {
        CalibreEbookConvertPath = @"C:\Tools\Calibre\ebook-convert.exe",
        ConversionOutputDirectory = @"D:\Converted",
        ConversionDefaultTargetExtension = ".pdf",
        SyncEnabled = true,
        SyncProvider = SyncProviderKind.Folder,
        SyncFolderPath = @"D:\Sync\NoGaReader",
        SyncIncludeAnnotations = false,
        SyncIncludeSettings = false,
        SyncSettingsModifiedUtc = new DateTimeOffset(2026, 7, 18, 11, 0, 0, TimeSpan.Zero),
        LastSyncUtc = "2026-07-18 12:00:00Z",
        UiLanguage = "en-US"
    };
    var mainWindowType = typeof(DocumentLoader).Assembly.GetType("NoGaReader.MainWindow", throwOnError: true)!;
    var cloneMethod = mainWindowType.GetMethod(
        "CloneSettings",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("MainWindow.CloneSettings was not found.");
    var clone = (AppSettings)(cloneMethod.Invoke(null, [source])
        ?? throw new InvalidOperationException("MainWindow.CloneSettings returned null."));

    Assert(clone.CalibreEbookConvertPath == source.CalibreEbookConvertPath &&
           clone.ConversionOutputDirectory == source.ConversionOutputDirectory &&
           clone.ConversionDefaultTargetExtension == source.ConversionDefaultTargetExtension,
        "Settings snapshot preserves conversion configuration");
    Assert(clone.SyncEnabled == source.SyncEnabled &&
           clone.SyncProvider == source.SyncProvider &&
           clone.SyncFolderPath == source.SyncFolderPath &&
           clone.SyncIncludeAnnotations == source.SyncIncludeAnnotations &&
           clone.SyncIncludeSettings == source.SyncIncludeSettings &&
           clone.SyncSettingsModifiedUtc == source.SyncSettingsModifiedUtc &&
           clone.LastSyncUtc == source.LastSyncUtc &&
           clone.UiLanguage == source.UiLanguage,
        "Settings snapshot preserves sync configuration");
}

static async Task TestConcurrentSettingsSaveAsync()
{
    const int workerCount = 12;
    const int savesPerWorker = 60;
    var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var tasks = Enumerable.Range(0, workerCount).Select(async worker =>
    {
        await start.Task;
        await Task.Run(() =>
        {
            var store = new SettingsStore();
            for (var iteration = 0; iteration < savesPerWorker; iteration++)
            {
                store.Save(new AppSettings
                {
                    ReaderFontSize = 14 + ((worker + iteration) % 17),
                    ReaderContentWidth = 560 + ((worker + iteration) % 9) * 50,
                    ReaderThemePreferenceInitialized = true
                });
            }
        });
    }).ToArray();

    start.SetResult();
    await Task.WhenAll(tasks);

    var settingsPath = Path.Combine(AppPaths.DataRoot, "settings.json");
    using var json = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath, Encoding.UTF8));
    var loadedAfterConcurrentSaves = new SettingsStore().Load();
    Assert(json.RootElement.ValueKind == JsonValueKind.Object &&
           loadedAfterConcurrentSaves.ReaderFontSize is >= 14 and <= 30 &&
           loadedAfterConcurrentSaves.ReaderContentWidth is >= 560 and <= 1000,
        "Concurrent SettingsStore saves remain atomic and produce valid JSON");

    var revisionStore = new SettingsStore();
    var staleRevision = revisionStore.ReserveSaveRevision();
    var staleSnapshot = new AppSettings
    {
        ReaderFontSize = 15,
        ReaderThemePreferenceInitialized = true,
        UiLanguage = "zh-CN"
    };
    var latestSnapshot = new AppSettings
    {
        ReaderFontSize = 29,
        ReaderThemePreferenceInitialized = true,
        UiLanguage = "en-US"
    };
    revisionStore.Save(latestSnapshot);
    revisionStore.Save(staleSnapshot, staleRevision);
    var loadedAfterStaleSave = revisionStore.Load();
    Assert(loadedAfterStaleSave.ReaderFontSize == 29 && loadedAfterStaleSave.UiLanguage == "en-US",
        "A delayed stale settings snapshot cannot overwrite a newer revision");
}

static async Task TestCrossDeviceSyncAsync(string testRoot, SettingsStore settingsStore)
{
    var fixtureRoot = Path.Combine(testRoot, "cross-device-sync");
    var deviceARoot = Path.Combine(fixtureRoot, "device-a", "library");
    var deviceBRoot = Path.Combine(fixtureRoot, "device-b", "different-library-root");
    var sharedRoot = Path.Combine(fixtureRoot, "shared");
    Directory.CreateDirectory(deviceARoot);
    Directory.CreateDirectory(deviceBRoot);
    Directory.CreateDirectory(sharedRoot);
    var sourceA = Path.Combine(deviceARoot, "portable-book.txt");
    var sourceB = Path.Combine(deviceBRoot, "renamed-book.txt");
    File.WriteAllText(sourceA, "portable sync identity", Encoding.UTF8);
    File.Copy(sourceA, sourceB);

    var databaseA = new LibraryDatabase(Path.Combine(fixtureRoot, "device-a.db"));
    var databaseB = new LibraryDatabase(Path.Combine(fixtureRoot, "device-b.db"));
    await databaseA.InitializeAsync();
    await databaseB.InitializeAsync();
    var bookA = await databaseA.UpsertBookAsync(new LibraryBook
    {
        Path = sourceA,
        Title = "Portable Sync Book",
        Author = "NoGaReader",
        Format = "TXT",
        FileSize = new FileInfo(sourceA).Length
    });
    var annotationA = await databaseA.UpsertAnnotationAsync(new Annotation
    {
        Id = "portable-note",
        BookId = bookA.Id,
        Type = AnnotationType.Note,
        SectionIndex = 0,
        SectionProgress = 0.25,
        SelectedText = "portable",
        Note = "version one"
    });

    var settingsA = new AppSettings
    {
        SyncEnabled = true,
        SyncProvider = SyncProviderKind.Folder,
        SyncFolderPath = sharedRoot,
        SyncIncludeAnnotations = true,
        SyncIncludeSettings = false
    };
    var settingsB = new AppSettings
    {
        SyncEnabled = true,
        SyncProvider = SyncProviderKind.Folder,
        SyncFolderPath = sharedRoot,
        SyncIncludeAnnotations = true,
        SyncIncludeSettings = false
    };
    var syncA = new CloudSyncService(settingsA);
    var syncB = new CloudSyncService(settingsB);
    Assert((await syncA.SynchronizeAsync(databaseA, settingsStore)).Succeeded,
        "Cross-device sync initial upload");

    var bookB = await databaseB.UpsertBookAsync(new LibraryBook
    {
        Path = sourceB,
        Title = bookA.Title,
        Author = bookA.Author,
        Format = bookA.Format,
        FileSize = new FileInfo(sourceB).Length
    });
    Assert((await syncB.SynchronizeAsync(databaseB, settingsStore)).Succeeded,
        "Cross-device sync matches different absolute paths");
    var downloaded = (await databaseB.ListAnnotationsAsync(bookB.Id)).Single();
    Assert(downloaded.Id == annotationA.Id && downloaded.Note == "version one",
        "Cross-device sync preserves annotation identity");

    await Task.Delay(20);
    downloaded.Note = "version two";
    downloaded.ModifiedUtc = DateTimeOffset.UtcNow;
    await databaseB.UpsertAnnotationAsync(downloaded);
    Assert((await syncB.SynchronizeAsync(databaseB, settingsStore)).Succeeded,
        "Cross-device annotation update upload");
    Assert((await syncA.SynchronizeAsync(databaseA, settingsStore)).Succeeded,
        "Cross-device annotation update download");
    var updatedOnA = await databaseA.ListAnnotationsAsync(bookA.Id);
    Assert(updatedOnA.Count == 1 && updatedOnA[0].Id == annotationA.Id && updatedOnA[0].Note == "version two",
        "Annotation edits update in place without duplicates");

    await Task.Delay(20);
    Assert(await databaseB.DeleteAnnotationAsync(annotationA.Id),
        "Local annotation deletion records tombstone");
    Assert((await databaseB.ListAnnotationTombstonesAsync(bookB.Id)).Single().AnnotationId == annotationA.Id,
        "Annotation tombstone persisted");
    Assert((await syncB.SynchronizeAsync(databaseB, settingsStore)).Succeeded,
        "Annotation deletion upload");
    Assert((await syncA.SynchronizeAsync(databaseA, settingsStore)).Succeeded,
        "Annotation deletion download");
    Assert((await databaseA.ListAnnotationsAsync(bookA.Id)).Count == 0 &&
           (await databaseA.ListAnnotationTombstonesAsync(bookA.Id)).Any(item => item.AnnotationId == annotationA.Id),
        "Remote deletion does not resurrect annotation");

    // Delayed UI persistence writes a cloned snapshot. A direct service caller must refresh
    // the live object's timestamp before merging, even without going through SyncDialog.
    var directSettingsRoot = Path.Combine(fixtureRoot, "direct-settings-sync");
    Directory.CreateDirectory(directSettingsRoot);
    var staleLiveSettings = new AppSettings
    {
        SyncEnabled = true,
        SyncProvider = SyncProviderKind.Folder,
        SyncFolderPath = directSettingsRoot,
        SyncIncludeAnnotations = false,
        SyncIncludeSettings = true,
        ReaderFontSize = 16,
        ReaderThemePreferenceInitialized = true
    };
    settingsStore.Save(staleLiveSettings);
    var staleLiveTimestamp = staleLiveSettings.SyncSettingsModifiedUtc;
    staleLiveSettings.ReaderFontSize = 27;
    await Task.Delay(20);
    var delayedSnapshot = new AppSettings
    {
        SyncEnabled = true,
        SyncProvider = SyncProviderKind.Folder,
        SyncFolderPath = directSettingsRoot,
        SyncIncludeAnnotations = false,
        SyncIncludeSettings = true,
        ReaderFontSize = 27,
        ReaderThemePreferenceInitialized = true,
        SyncSettingsModifiedUtc = staleLiveTimestamp
    };
    settingsStore.Save(delayedSnapshot);
    var persistedDelayedTimestamp = delayedSnapshot.SyncSettingsModifiedUtc;
    Assert(persistedDelayedTimestamp > staleLiveTimestamp.AddTicks(1),
        "Delayed cloned settings snapshot receives a newer persisted timestamp");
    var intermediateRemoteTimestamp = staleLiveTimestamp.AddTicks(1);
    var intermediateRemoteManifest = new SyncManifest
    {
        SchemaVersion = 2,
        UpdatedAt = intermediateRemoteTimestamp,
        DeviceName = "intermediate-remote-device",
        Settings = new SyncSettingsSnapshot
        {
            UpdatedAt = intermediateRemoteTimestamp,
            ReaderFontSize = 18
        }
    };
    await File.WriteAllTextAsync(
        Path.Combine(directSettingsRoot, "nogareader-sync.json"),
        JsonSerializer.Serialize(intermediateRemoteManifest, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        }),
        Encoding.UTF8);
    var directSettingsSync = new CloudSyncService(staleLiveSettings);
    Assert((await directSettingsSync.SynchronizeAsync(databaseA, settingsStore)).Succeeded &&
           staleLiveSettings.ReaderFontSize == 27 &&
           staleLiveSettings.SyncSettingsModifiedUtc >= persistedDelayedTimestamp,
        "Direct cloud sync refreshes a stale live settings timestamp before merge");

    var settingsPrecedenceRoot = Path.Combine(fixtureRoot, "settings-precedence");
    Directory.CreateDirectory(settingsPrecedenceRoot);
    var localSettingsTimestamp = DateTimeOffset.UtcNow.AddDays(-1);
    var remoteSettingsTimestamp = DateTimeOffset.UtcNow.AddDays(1);
    var localSettings = new AppSettings
    {
        SyncEnabled = true,
        SyncProvider = SyncProviderKind.Folder,
        SyncFolderPath = settingsPrecedenceRoot,
        SyncIncludeAnnotations = false,
        SyncIncludeSettings = true,
        ReaderFontSize = 16,
        ReaderTheme = ReaderThemeMode.Light,
        ReaderThemePreferenceInitialized = true,
        UiLanguage = "zh-CN",
        SyncSettingsModifiedUtc = localSettingsTimestamp
    };
    var remoteManifest = new SyncManifest
    {
        SchemaVersion = 2,
        UpdatedAt = remoteSettingsTimestamp,
        DeviceName = "newer-remote-device",
        Settings = new SyncSettingsSnapshot
        {
            UpdatedAt = remoteSettingsTimestamp,
            ReaderFontSize = 30,
            ReaderTheme = ReaderThemeMode.Dark.ToString(),
            UiLanguage = "en-US"
        }
    };
    await File.WriteAllTextAsync(
        Path.Combine(settingsPrecedenceRoot, "nogareader-sync.json"),
        JsonSerializer.Serialize(remoteManifest, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        }),
        Encoding.UTF8);

    var settingsSync = new CloudSyncService(localSettings);
    Assert((await settingsSync.SynchronizeAsync(databaseA, settingsStore)).Succeeded,
        "Settings precedence sync succeeds");
    var persistedRemoteSettings = settingsStore.Load();
    Assert(localSettings.ReaderFontSize == 30 &&
           localSettings.ReaderTheme == ReaderThemeMode.Dark &&
           localSettings.UiLanguage == "en-US" &&
           localSettings.SyncSettingsModifiedUtc == remoteSettingsTimestamp &&
           persistedRemoteSettings.ReaderFontSize == 30 &&
           persistedRemoteSettings.UiLanguage == "en-US",
        "Newer remote settings snapshot wins and persists locally");

    SqliteConnection.ClearAllPools();
}

static async Task AssertCacheHitDoesNotRewrite(
    DocumentLoader loader,
    string sourcePath,
    string generatedPath,
    string assertionName)
{
    var sentinel = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    File.SetLastWriteTimeUtc(generatedPath, sentinel);
    var cached = await loader.LoadAsync(sourcePath);
    Assert(Path.GetFullPath(cached.CurrentSection.FullPath) == Path.GetFullPath(generatedPath) &&
           File.GetLastWriteTimeUtc(generatedPath) == sentinel,
        assertionName);
}

static async Task TestLibraryDatabaseAsync(string testRoot)
{
    var fixtureRoot = Path.Combine(testRoot, "database-fixture");
    Directory.CreateDirectory(fixtureRoot);
    var databasePath = Path.Combine(fixtureRoot, "library-test.db");
    var sourcePath = Path.Combine(fixtureRoot, "database-book.epub");
    File.WriteAllText(sourcePath, "database fixture", Encoding.UTF8);

    var database = new LibraryDatabase(databasePath);
    await database.InitializeAsync();
    Assert(Path.GetFullPath(database.DatabasePath) == Path.GetFullPath(databasePath), "Database uses explicit temporary path");
    await using (var schemaConnection = new SqliteConnection($"Data Source={databasePath}"))
    {
        await schemaConnection.OpenAsync();
        await using var schemaCommand = schemaConnection.CreateCommand();
        schemaCommand.CommandText = "SELECT sqlite_version();";
        var sqliteVersionText = Convert.ToString(await schemaCommand.ExecuteScalarAsync());
        Assert(Version.TryParse(sqliteVersionText, out var sqliteVersion) &&
               sqliteVersion >= new Version(3, 50, 2),
            $"Native SQLite runtime is patched (loaded {sqliteVersionText ?? "unknown"})");
        schemaCommand.CommandText =
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'search_sections_fts';";
        var ftsSchema = await schemaCommand.ExecuteScalarAsync() as string;
        Assert(ftsSchema?.Contains("trigram", StringComparison.OrdinalIgnoreCase) == true,
            "Database FTS uses trigram tokenizer");
        schemaCommand.CommandText = "PRAGMA user_version;";
        Assert(Convert.ToInt32(await schemaCommand.ExecuteScalarAsync()) == 4,
            "Database schema includes book tags");
    }

    var savedBook = await database.UpsertBookAsync(new LibraryBook
    {
        Path = sourcePath,
        Title = "数据库测试书",
        Author = "NoGaReader",
        Format = "EPUB",
        FileSize = new FileInfo(sourcePath).Length,
        ModifiedUtc = File.GetLastWriteTimeUtc(sourcePath),
        LastOpenedUtc = DateTimeOffset.UtcNow,
        SectionCount = 2,
        Tags = "科幻； 经典,科幻"
    });
    Assert(savedBook.Id > 0 && savedBook.Tags == "科幻,经典",
        "Database book identity and normalized tags");
    Assert((await database.ListBooksAsync()).Count == 1, "Database book insert/list");

    var updatedBook = await database.UpsertBookAsync(new LibraryBook
    {
        Path = sourcePath,
        Title = "数据库测试书（更新）",
        Author = "NoGaReader",
        Format = "EPUB",
        FileSize = new FileInfo(sourcePath).Length,
        SectionCount = 3
    });
    Assert(updatedBook.Id == savedBook.Id, "Database path upsert preserves identity");
    var foundUpdatedBook = await database.FindBookByPathAsync(sourcePath);
    Assert(foundUpdatedBook?.Title == "数据库测试书（更新）" &&
           foundUpdatedBook.Tags == "科幻,经典",
        "Sparse database book upsert preserves existing tags");

    var location = new ReaderLocation
    {
        BookId = savedBook.Id,
        SectionIndex = 1,
        Fragment = "chapter-two",
        SectionProgress = 0.42,
        DocumentProgress = 0.71,
        TextQuote = "精确恢复锚点",
        Anchor = new TextAnchor
        {
            StartPath = "0/2/1",
            StartOffset = 3,
            EndPath = "0/2/1",
            EndOffset = 9,
            ExactText = "精确恢复",
            Prefix = "这里是",
            Suffix = "的位置",
            Progress = 0.42
        }
    };
    await database.SaveReaderLocationAsync(location);
    var restoredLocation = await database.GetReaderLocationAsync(savedBook.Id);
    Assert(restoredLocation is not null, "Database reader location insert/get");
    Assert(restoredLocation!.SectionIndex == 1 && NearlyEqual(restoredLocation.SectionProgress, 0.42), "Database reader location values");
    Assert(restoredLocation.Anchor?.ExactText == "精确恢复" && restoredLocation.Anchor.StartPath == "0/2/1", "Database reader text anchor round-trip");
    var listedBookWithLocation = (await database.ListBooksAsync()).Single();
    Assert(listedBookWithLocation.Tags == "科幻,经典" &&
           listedBookWithLocation.Location is not null &&
           NearlyEqual(listedBookWithLocation.Location.DocumentProgress, 0.71) &&
           listedBookWithLocation.ProgressText == "已读 71%",
        "Database library list reads tags and location together");
    var clearedTagsBook = await database.UpsertBookAsync(new LibraryBook
    {
        Path = sourcePath,
        Title = updatedBook.Title,
        Author = updatedBook.Author,
        Format = updatedBook.Format,
        FileSize = updatedBook.FileSize,
        SectionCount = updatedBook.SectionCount,
        Tags = string.Empty
    });
    Assert(clearedTagsBook.Tags is null &&
           (await database.FindBookByPathAsync(sourcePath))?.Tags is null,
        "Explicit empty tags clear existing book tags");

    var annotation = await database.UpsertAnnotationAsync(new Annotation
    {
        Id = "smoke-highlight",
        BookId = savedBook.Id,
        Type = AnnotationType.Highlight,
        SectionIndex = 1,
        SectionPath = "chapter2.xhtml",
        SectionProgress = 0.42,
        SelectedText = "精确恢复",
        Color = "yellow",
        Anchor = location.Anchor
    });
    Assert(annotation.Id == "smoke-highlight", "Database annotation insert");
    Assert((await database.ListAnnotationsAsync(savedBook.Id, 1, AnnotationType.Highlight)).Count == 1, "Database annotation filtering");

    annotation.Type = AnnotationType.Note;
    annotation.Note = "这是一条本地笔记";
    annotation.ModifiedUtc = DateTimeOffset.UtcNow.AddSeconds(1);
    var updatedAnnotation = await database.UpsertAnnotationAsync(annotation);
    Assert(updatedAnnotation.Type == AnnotationType.Note && updatedAnnotation.Note == "这是一条本地笔记", "Database annotation update");
    Assert((await database.FindAnnotationAsync(annotation.Id))?.Anchor?.Suffix == "的位置", "Database annotation anchor round-trip");

    var atomicBatchRejected = false;
    try
    {
        await database.UpsertAnnotationsAsync(
        [
            new Annotation
            {
                Id = "atomic-import-valid",
                BookId = savedBook.Id,
                Type = AnnotationType.Note,
                SectionIndex = 0,
                Note = "must roll back with the batch"
            },
            new Annotation
            {
                Id = "atomic-import-invalid-book",
                BookId = long.MaxValue,
                Type = AnnotationType.Highlight,
                SectionIndex = 0,
                SelectedText = "missing parent book"
            }
        ]);
    }
    catch (SqliteException)
    {
        atomicBatchRejected = true;
    }

    Assert(atomicBatchRejected &&
           await database.FindAnnotationAsync("atomic-import-valid") is null &&
           await database.FindAnnotationAsync("atomic-import-invalid-book") is null &&
           (await database.FindAnnotationAsync(annotation.Id))?.Note == "这是一条本地笔记",
        "Annotation batch upsert rolls back atomically when any row fails");

    var libraryFolderPath = Path.Combine(fixtureRoot, "library-folder");
    Directory.CreateDirectory(libraryFolderPath);
    var folder = await database.UpsertLibraryFolderAsync(new LibraryFolder
    {
        Path = libraryFolderPath,
        Name = "临时书库",
        IncludeSubfolders = true,
        LastScannedAt = DateTimeOffset.UtcNow
    });
    Assert(folder.Id > 0 && (await database.ListLibraryFoldersAsync()).Count == 1, "Database folder insert/list");
    Assert((await database.FindLibraryFolderByPathAsync(libraryFolderPath))?.Name == "临时书库", "Database folder find");

    const string repeatedSearchText =
        "needle 开始，然后 NEEDLE 再次出现，最后 needle。中文阅读体验，继续阅读。";
    var indexStamp = new SearchIndexStamp
    {
        SourceSize = new FileInfo(sourcePath).Length,
        SourceModifiedUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(sourcePath), TimeSpan.Zero),
        SectionCount = 2,
        IndexVersion = BookSearchIndexer.IndexVersion
    };
    await database.ReplaceSearchIndexAsync(savedBook.Id,
    [
        new SearchSection { SectionIndex = 0, Title = "第一章", Text = "ordinary searchable content" },
        new SearchSection { SectionIndex = 1, Title = "第二章", Text = repeatedSearchText }
    ], indexStamp);
    Assert(await database.IsSearchIndexCurrentAsync(savedBook.Id, indexStamp),
        "Database search index source stamp hit");
    Assert(!await database.IsSearchIndexCurrentAsync(savedBook.Id, indexStamp with
    {
        SourceSize = indexStamp.SourceSize + 1
    }), "Database search index source stamp invalidation");
    var searchHits = await database.SearchAsync("needle", savedBook.Id);
    var expectedNeedleStarts = FindAllOccurrences(repeatedSearchText, "needle", StringComparison.OrdinalIgnoreCase);
    Assert(searchHits.Count == 3 && searchHits.All(hit => hit.SectionIndex == 1), "Database same-section multi-hit search");
    Assert(searchHits.Select(hit => hit.OccurrenceIndex).SequenceEqual([0, 1, 2]), "Database search occurrence indexes");
    Assert(searchHits.Select(hit => hit.MatchStart).SequenceEqual(expectedNeedleStarts), "Database search UTF-16 match offsets");
    Assert(searchHits.All(hit => hit.MatchLength == "needle".Length &&
                                 hit.Snippet.Contains($"‹{hit.MatchedText}›", StringComparison.Ordinal)),
        "Database occurrence snippets and matched text");

    var limitedHits = await database.SearchAsync("needle", savedBook.Id, limit: 2);
    Assert(limitedHits.Count == 2 &&
           limitedHits.Select(hit => hit.OccurrenceIndex).SequenceEqual([0, 1]),
        "Database search result limit applies to occurrences");

    var chineseHits = await database.SearchAsync("阅读", savedBook.Id);
    var expectedChineseStarts = FindAllOccurrences(repeatedSearchText, "阅读", StringComparison.Ordinal);
    Assert(chineseHits.Count == 2 &&
           chineseHits.Select(hit => hit.OccurrenceIndex).SequenceEqual([0, 1]),
        "Database Chinese same-section occurrences");
    Assert(chineseHits.Select(hit => hit.MatchStart).SequenceEqual(expectedChineseStarts) &&
           chineseHits.All(hit => hit.Snippet.Contains("‹阅读›", StringComparison.Ordinal)),
        "Database Chinese match offsets and snippets");
    Assert((await database.SearchAsync("needle", savedBook.Id + 1000)).Count == 0, "Database search book filter");

    var recallPath = Path.Combine(fixtureRoot, "search-recall.epub");
    File.WriteAllText(recallPath, "search recall fixture", Encoding.UTF8);
    var recallBook = await database.UpsertBookAsync(new LibraryBook
    {
        Path = recallPath,
        Title = "FTS 召回测试",
        Format = "EPUB"
    });
    var crowdedSections = Enumerable.Range(0, 1_001)
        .Select(index => new SearchSection
        {
            SectionIndex = index,
            Title = $"章节 {index}",
            Text = index == 1_000 ? "这里包含 alpha beta 精确短语" : $"alpha filler {index} beta"
        })
        .ToList();
    await database.ReplaceSearchIndexAsync(recallBook.Id, crowdedSections);
    var recallHits = await database.SearchAsync("alpha beta", recallBook.Id, limit: 50);
    Assert(recallHits.Count == 1 && recallHits[0].SectionIndex == 1_000,
        "Database LIKE supplement preserves exact-match recall beyond FTS candidate cap");

    var relocatedDirectory = Path.Combine(fixtureRoot, "relocated");
    Directory.CreateDirectory(relocatedDirectory);
    var relocatedPath = Path.Combine(relocatedDirectory, "database-book.epub");
    File.Copy(sourcePath, relocatedPath);
    var relocatedBook = await database.RelocateBookAsync(savedBook.Id, relocatedPath);
    Assert(relocatedBook.Id == savedBook.Id &&
           Path.GetFullPath(relocatedBook.Path) == Path.GetFullPath(relocatedPath),
        "Database relocation preserves book identity and updates path");
    Assert(await database.FindBookByPathAsync(sourcePath) is null &&
           (await database.FindBookByPathAsync(relocatedPath))?.Id == savedBook.Id,
        "Database relocation replaces the path key");
    Assert((await database.GetReaderLocationAsync(savedBook.Id))?.TextQuote == "精确恢复锚点",
        "Database relocation preserves reader location");
    Assert((await database.ListAnnotationsAsync(savedBook.Id)).Single().Note == "这是一条本地笔记",
        "Database relocation preserves annotations");
    Assert((await database.SearchAsync("needle", savedBook.Id)).Count == 3 &&
           (await database.SearchAsync("阅读", savedBook.Id)).Count == 2,
        "Database relocation preserves search index");

    var collisionPath = Path.Combine(fixtureRoot, "collision.epub");
    File.WriteAllText(collisionPath, "collision fixture", Encoding.UTF8);
    var collisionBook = await database.UpsertBookAsync(new LibraryBook
    {
        Path = collisionPath,
        Title = "路径冲突书籍",
        Format = "EPUB"
    });
    var collisionRejected = false;
    try
    {
        _ = await database.RelocateBookAsync(savedBook.Id, collisionPath);
    }
    catch (InvalidOperationException)
    {
        collisionRejected = true;
    }

    Assert(collisionRejected, "Database relocation rejects an occupied target path");
    Assert((await database.FindBookByPathAsync(relocatedPath))?.Id == savedBook.Id &&
           (await database.FindBookByPathAsync(collisionPath))?.Id == collisionBook.Id,
        "Database rejected relocation leaves both records unchanged");

    await database.SaveReaderLocationAsync(new ReaderLocation
    {
        BookId = collisionBook.Id,
        SectionIndex = 9,
        SectionProgress = 0.99,
        DocumentProgress = 0.99,
        TextQuote = "重复记录的位置不应覆盖保留记录"
    });
    _ = await database.UpsertAnnotationAsync(new Annotation
    {
        Id = "duplicate-highlight",
        BookId = collisionBook.Id,
        Type = AnnotationType.Highlight,
        SectionIndex = 0,
        SectionProgress = 0.2,
        SelectedText = "从重复记录迁移的高亮",
        Color = "green"
    });
    await database.ReplaceSearchIndexAsync(collisionBook.Id,
    [
        new SearchSection
        {
            SectionIndex = 0,
            Title = "重复记录章节",
            Text = "duplicate-marker 只存在于重复记录的搜索索引"
        }
    ]);

    var mergedBook = await database.MergeBookRecordsAsync(
        savedBook.Id,
        collisionBook.Id,
        collisionPath);
    Assert(mergedBook.Id == savedBook.Id &&
           Path.GetFullPath(mergedBook.Path) == Path.GetFullPath(collisionPath),
        "Database merge preserves identity and adopts the selected path");
    Assert((await database.GetReaderLocationAsync(savedBook.Id))?.TextQuote == "精确恢复锚点",
        "Database merge keeps the preserved record location");
    var mergedAnnotations = await database.ListAnnotationsAsync(savedBook.Id);
    Assert(mergedAnnotations.Count == 2 &&
           mergedAnnotations.Select(item => item.Id).Order().SequenceEqual(
               new[] { "duplicate-highlight", "smoke-highlight" }),
        "Database merge migrates duplicate annotations");
    var mergedSearchHits = await database.SearchAsync("duplicate-marker", savedBook.Id);
    Assert(mergedSearchHits.Count == 1 && mergedSearchHits[0].BookId == savedBook.Id,
        "Database merge migrates duplicate search index");
    Assert(await database.FindBookByIdAsync(collisionBook.Id) is null &&
           (await database.FindBookByPathAsync(collisionPath))?.Id == savedBook.Id &&
           await database.FindBookByPathAsync(relocatedPath) is null,
        "Database merge deletes duplicate and updates preserved path key");

    Assert(await database.RemoveLibraryFolderAsync(folder.Id), "Database folder delete");
    Assert((await database.ListLibraryFoldersAsync()).Count == 0, "Database folder deletion persisted");

    Assert(await database.RemoveBookAsync(savedBook.Id), "Database book delete");
    Assert(await database.RemoveBookAsync(recallBook.Id), "Database search recall fixture delete");
    Assert(await database.GetReaderLocationAsync(savedBook.Id) is null, "Database location cascades with book");
    Assert((await database.ListAnnotationsAsync(savedBook.Id)).Count == 0, "Database annotations cascade with book");
    Assert((await database.SearchAsync("duplicate-marker", savedBook.Id)).Count == 0, "Database search index cascades with book");
    Assert((await database.ListBooksAsync()).Count == 0, "Database relocation fixtures fully removed");

    SqliteConnection.ClearAllPools();
}

static async Task TestPortableLibraryServiceAsync(string testRoot)
{
    // Exercises the shared mobile library flows (import → open → progress →
    // annotations → search → delete) on Windows, isolated from other fixtures.
    var fixtureRoot = Path.Combine(testRoot, "portable-library");
    Directory.CreateDirectory(fixtureRoot);
    var booksRoot = Path.Combine(fixtureRoot, "Books");
    var service = new PortableLibraryService(
        booksRoot,
        Path.Combine(fixtureRoot, "portable-library.db"));

    var sourceEpub = Path.Combine(fixtureRoot, "portable-source.epub");
    CreateEpub(sourceEpub);

    LibraryBook imported;
    await using (var source = File.OpenRead(sourceEpub))
    {
        imported = await service.ImportAsync(source, "portable-source.epub");
    }

    Assert(File.Exists(imported.Path) &&
           PathSemantics.IsInside(booksRoot, imported.Path) &&
           imported.Title == "测试 EPUB",
        "Portable library import copies into the managed directory and reads metadata");

    LibraryBook duplicate;
    await using (var source = File.OpenRead(sourceEpub))
    {
        duplicate = await service.ImportAsync(source, "portable-source.epub");
    }

    Assert(duplicate.Id != imported.Id &&
           !PathSemantics.Equals(duplicate.Path, imported.Path) &&
           Path.GetFileName(duplicate.Path).Contains("(2)", StringComparison.Ordinal),
        "Portable library import keeps duplicates apart with a numbered copy");

    var unsupportedRejected = false;
    try
    {
        _ = await service.ImportAsync(Stream.Null, "book.mobi");
    }
    catch (NotSupportedException)
    {
        unsupportedRejected = true;
    }

    Assert(unsupportedRejected && !File.Exists(Path.Combine(booksRoot, "book.mobi")),
        "Portable library rejects non-MVP formats before writing anything");

    var managedCountBeforeBroken = Directory.GetFiles(booksRoot).Length;
    var brokenRejected = false;
    try
    {
        await using var broken = new MemoryStream("not an epub"u8.ToArray());
        _ = await service.ImportAsync(broken, "broken.epub");
    }
    catch (Exception exception) when (exception is not NotSupportedException)
    {
        brokenRejected = true;
    }

    Assert(brokenRejected && Directory.GetFiles(booksRoot).Length == managedCountBeforeBroken,
        "Portable library cleans up the managed copy when a broken import fails to load");

    var opened = await service.OpenAsync(imported);
    Assert(opened.Sections.Count == 2 && opened.CurrentSectionIndex == 0,
        "Portable library open loads the imported book");

    opened.CurrentSectionIndex = 1;
    await service.SaveLocationAsync(imported.Id, opened, 0.5);
    var listed = (await service.ListAsync()).Single(book => book.Id == imported.Id);
    Assert(listed.Location is { SectionIndex: 1 } savedLocation &&
           Math.Abs(savedLocation.SectionProgress - 0.5) < 0.0001,
        "Portable library persists reading progress");

    var reopened = await service.OpenAsync(listed);
    Assert(reopened.CurrentSectionIndex == 1,
        "Portable library open restores the saved section");

    await service.AddBookmarkAsync(imported.Id, reopened, 0.5);
    await service.AddTextAnnotationAsync(
        imported.Id,
        reopened,
        0.5,
        "Second section.",
        "移动端笔记",
        new TextAnchor
        {
            ExactText = "Second section.",
            Prefix = "继续",
            Suffix = "尾注",
            Progress = 0.5
        });
    var annotations = await service.ListAnnotationsAsync(imported.Id, sectionIndex: 1);
    Assert(annotations.Count == 2 &&
           annotations.Any(item => item.Type == AnnotationType.Bookmark) &&
           annotations.Any(item => item.Type == AnnotationType.Note &&
                                   item.SelectedText == "Second section." &&
                                   item.Note == "移动端笔记"),
        "Portable library stores bookmarks and text annotations per section");
    var anchored = annotations.Single(item => item.Type == AnnotationType.Note);
    Assert(anchored.Anchor is { ExactText: "Second section.", Prefix: "继续", Suffix: "尾注" } roundTripped &&
           Math.Abs(roundTripped.Progress - 0.5) < 0.0001,
        "Portable library persists the portable text anchor with the annotation");

    var hits = await service.SearchAsync(listed, reopened, "Second section");
    Assert(hits.Count >= 1 && hits[0].SectionIndex == 1,
        "Portable library search indexes the book and finds section text");

    var cachedHits = await service.SearchAsync(listed, reopened, "Second section");
    Assert(cachedHits.Count == hits.Count,
        "Portable library search reuses the current index");

    await service.DeleteAsync(listed);
    Assert(!File.Exists(listed.Path) &&
           (await service.ListAsync()).All(book => book.Id != listed.Id),
        "Portable library delete removes the managed copy and the record");

    var outside = new LibraryBook
    {
        Id = duplicate.Id,
        Path = sourceEpub
    };
    await service.DeleteAsync(outside);
    Assert(File.Exists(sourceEpub),
        "Portable library delete never touches files outside the managed directory");

    SqliteConnection.ClearAllPools();
}

static void TestMobileReaderPresenter(string testRoot)
{
    // Pure-state coverage of the mobile reader host: paging bounds, section
    // flow, contents mapping, restore math, and in-book link resync. Paths
    // are synthetic — the presenter never touches the filesystem.
    var root = Path.Combine(testRoot, "presenter");

    // Section-based document (EPUB-like): three chapters, TOC with a fragment.
    var chapters = new[]
    {
        new ReaderSection("第一章", Path.Combine(root, "ch1.xhtml")),
        new ReaderSection("第二章", Path.Combine(root, "ch2.xhtml")),
        new ReaderSection("第三章", Path.Combine(root, "ch3.xhtml"))
    };
    var epubSession = new ReaderSession
    {
        SourcePath = Path.Combine(root, "book.epub"),
        Title = "Presenter EPUB",
        RootDirectory = root,
        Kind = ReaderDocumentKind.Epub,
        Sections = chapters,
        TableOfContents =
        [
            new TocNode
            {
                Title = "第一章",
                FullPath = chapters[0].FullPath,
                Children = [new TocNode { Title = "小节", FullPath = chapters[1].FullPath + "#anchor" }]
            },
            new TocNode { Title = "第三章", FullPath = chapters[2].FullPath }
        ],
        IsReflowable = true,
        EnableScriptExecution = true
    };

    var epub = new MobileReaderPresenter(epubSession);
    Assert(epub.UsesWebView && !epub.IsPaged &&
           epub.Contents.Count == 3 &&
           epub.Contents.Select(entry => entry.SectionIndex).SequenceEqual([0, 1, 2]) &&
           epub.SelectedContentsIndex == 0,
        "Presenter maps a fragment-bearing TOC onto section indexes");
    Assert(!epub.CanMovePrevious && epub.CanMoveNext &&
           epub.PreviousButtonText == "‹ 上一章" &&
           epub.PositionText == $"1 / 3 · {0d:P0}",
        "Presenter reports section-mode chrome at the first section");

    epub.SectionProgress = 0.5;
    Assert(Math.Abs(epub.DocumentProgress - 0.5 / 3) < 0.0001 &&
           Math.Abs(epub.ProgressForSave - 0.5) < 0.0001,
        "Presenter derives document progress from section index and progress");

    Assert(epub.Move(1) == MobileReaderPresenter.MoveResult.SectionChanged &&
           epubSession.CurrentSectionIndex == 1 &&
           epub.SectionProgress == 0 &&
           epub.SelectedContentsIndex == 1,
        "Presenter moves forward one section and resets progress to the top");
    Assert(epub.Move(-1) == MobileReaderPresenter.MoveResult.SectionChanged &&
           epubSession.CurrentSectionIndex == 0 &&
           Math.Abs(epub.SectionProgress - 1) < 0.0001,
        "Presenter moves backward one section and lands at the bottom");
    Assert(epub.Move(-1) == MobileReaderPresenter.MoveResult.None &&
           epubSession.CurrentSectionIndex == 0,
        "Presenter refuses to move before the first section");

    epub.JumpToSearchHit(new SearchHit { SectionIndex = 2, SectionProgress = 0.25 });
    Assert(epubSession.CurrentSectionIndex == 2 &&
           Math.Abs(epub.SectionProgress - 0.25) < 0.0001 &&
           epub.SelectedContentsIndex == 2 &&
           !epub.CanMoveNext,
        "Presenter targets a search hit's section and progress");

    Assert(epub.SyncSectionFromPath(chapters[1].FullPath) &&
           epubSession.CurrentSectionIndex == 1 &&
           epub.SectionProgress == 0 &&
           !epub.SyncSectionFromPath(chapters[1].FullPath) &&
           !epub.SyncSectionFromPath(Path.Combine(root, "outside.xhtml")),
        "Presenter resyncs from in-book navigation exactly when the section changes");

    // Comic: five pages, one section per page.
    var comicSession = new ReaderSession
    {
        SourcePath = Path.Combine(root, "comic.cbz"),
        Title = "Presenter Comic",
        RootDirectory = root,
        Kind = ReaderDocumentKind.Comic,
        Sections = Enumerable.Range(1, 5)
            .Select(page => new ReaderSection($"第 {page} 页", Path.Combine(root, $"p{page}.png")))
            .ToArray(),
        ComicPages = Enumerable.Range(1, 5)
            .Select(page => new ComicPage(page - 1, Path.Combine(root, $"p{page}.png")))
            .ToArray()
    };

    var comic = new MobileReaderPresenter(comicSession);
    Assert(comic.IsPaged && !comic.UsesWebView &&
           comic.VisualPageCount == 5 &&
           comic.PreviousButtonText == "‹ 上一页",
        "Presenter treats comics as paged documents");

    comic.RestoreFromDocumentProgress(0.5);
    Assert(comic.VisualPageIndex == 2 &&
           comicSession.CurrentSectionIndex == 2 &&
           comic.CurrentComicPagePath == comicSession.ComicPages[2].FullPath &&
           Math.Abs(comic.ProgressForSave - 0.5) < 0.0001,
        "Presenter restores a comic page from saved document progress");

    Assert(comic.Move(1) == MobileReaderPresenter.MoveResult.PageChanged &&
           comic.VisualPageIndex == 3 &&
           comic.Move(10) == MobileReaderPresenter.MoveResult.PageChanged &&
           comic.VisualPageIndex == 4 &&
           comic.Move(1) == MobileReaderPresenter.MoveResult.None &&
           !comic.CanMoveNext &&
           comic.PositionText == "5 / 5",
        "Presenter clamps comic paging at the last page");

    comic.JumpToContents(comic.Contents[1]);
    Assert(comic.VisualPageIndex == 1 &&
           comicSession.CurrentSectionIndex == 1 &&
           comic.SectionProgress == 0,
        "Presenter contents jump drives both the page and the section");

    // PDF: single logical section, page count arrives from the renderer.
    var pdfSession = new ReaderSession
    {
        SourcePath = Path.Combine(root, "doc.pdf"),
        Title = "Presenter PDF",
        RootDirectory = root,
        Kind = ReaderDocumentKind.Pdf,
        Sections = [new ReaderSection("正文", Path.Combine(root, "doc.pdf"))]
    };

    var pdf = new MobileReaderPresenter(pdfSession);
    Assert(pdf.IsPaged && pdf.VisualPageCount == 1, "Presenter defaults PDFs to one page");
    pdf.SetPdfPageCount(10);
    pdf.RestoreFromDocumentProgress(1);
    Assert(pdf.VisualPageIndex == 9 &&
           Math.Abs(pdf.ProgressForSave - 1) < 0.0001 &&
           pdf.PositionText == "10 / 10",
        "Presenter restores the last PDF page from full progress");
    pdf.SetPdfPageCount(4);
    Assert(pdf.VisualPageIndex == 3, "Presenter clamps the PDF page when the count shrinks");
    Assert(pdf.Move(-2) == MobileReaderPresenter.MoveResult.PageChanged && pdf.VisualPageIndex == 1,
        "Presenter moves PDF pages by delta");

    // Single-section text: pure percentage chrome, no section navigation.
    var textSession = new ReaderSession
    {
        SourcePath = Path.Combine(root, "note.txt"),
        Title = "Presenter TXT",
        RootDirectory = root,
        Kind = ReaderDocumentKind.Text,
        Sections = [new ReaderSection("正文", Path.Combine(root, "note.html"))],
        IsReflowable = true,
        EnableScriptExecution = true
    };

    var text = new MobileReaderPresenter(textSession);
    text.SectionProgress = 0.42;
    Assert(text.PositionText == $"{0.42:P0}" &&
           text.Move(1) == MobileReaderPresenter.MoveResult.None &&
           !text.CanMoveNext && !text.CanMovePrevious,
        "Presenter renders single-section documents as a bare percentage");
}

static void TestMobileReaderScripts()
{
    // Selection-capture parsing: the JS returns JSON or null.
    Assert(MobileReaderScripts.ParseSelectionCapture(null) is null &&
           MobileReaderScripts.ParseSelectionCapture("null") is null &&
           MobileReaderScripts.ParseSelectionCapture("not json") is null &&
           MobileReaderScripts.ParseSelectionCapture("""{"text":"  "}""") is null,
        "Mobile capture parser rejects empty and malformed selections");

    var capture = MobileReaderScripts.ParseSelectionCapture(
        """{"text":"  引用的正文  ","prefix":"前文上下文","suffix":"后文上下文"}""");
    Assert(capture is { Text: "引用的正文", Prefix: "前文上下文", Suffix: "后文上下文" },
        "Mobile capture parser trims the text and keeps raw context");

    foreach (var contract in new[] { "window.getSelection", "createTreeWalker", "JSON.stringify" })
    {
        Assert(MobileReaderScripts.SelectionCaptureScript.Contains(contract, StringComparison.Ordinal),
            $"Mobile capture script contract: {contract}");
    }

    // Mount script: anchored, legacy (no anchor), and hostile text payloads.
    var mountScript = MobileReaderScripts.BuildAnnotationMountScript(
    [
        new Annotation
        {
            SelectedText = "重复文本",
            Note = "第二处的\"笔记\"",
            Anchor = new TextAnchor { ExactText = "重复文本", Prefix = "第二段落里的", Suffix = "继续" }
        },
        new Annotation { SelectedText = "legacy </script> text" },
        new Annotation { SelectedText = "   " }
    ]);
    // Expected payload fragments must go through the same serializer the
    // script factory uses: the default encoder escapes CJK and quotes.
    var expectedPrefix = System.Text.Json.JsonSerializer.Serialize("第二段落里的");
    var expectedNote = System.Text.Json.JsonSerializer.Serialize("第二处的\"笔记\"");
    Assert(mountScript.Contains("nogar-mobile-note", StringComparison.Ordinal) &&
           mountScript.Contains("sort((a, b) => b.start - a.start)", StringComparison.Ordinal) &&
           mountScript.Contains("sharedTail", StringComparison.Ordinal) &&
           mountScript.Contains(expectedPrefix, StringComparison.Ordinal),
        "Mobile mount script scores anchor context and highlights in descending order");
    Assert(mountScript.Contains(expectedNote, StringComparison.Ordinal),
        "Mobile mount script escapes quotes inside annotation payloads");
    Assert(!mountScript.Contains("</script>", StringComparison.OrdinalIgnoreCase),
        "Mobile mount script never embeds a raw close-script tag");
    Assert(!mountScript.Contains("\"   \"", StringComparison.Ordinal),
        "Mobile mount script drops whitespace-only annotations");
}

static void TestMobileLibraryPresenter()
{
    var presenter = new MobileLibraryPresenter();
    var emptyView = presenter.BuildView(null);
    Assert(presenter.Summary == "随身阅读，从一本书开始" &&
           presenter.ContinueReading is null &&
           emptyView.ShowEmptyState &&
           emptyView.EmptyTitle == "书库还是空的",
        "Library presenter renders the empty-shelf state");

    presenter.SetBooks(
    [
        new LibraryBook
        {
            Id = 1,
            Title = "尘埃之书",
            Author = "远行者",
            Format = ".epub",
            LastOpenedUtc = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero),
            Location = new ReaderLocation { DocumentProgress = 0.6 }
        },
        new LibraryBook
        {
            Id = 2,
            Title = "Paper Atlas",
            Format = ".pdf",
            LastOpenedUtc = new DateTimeOffset(2026, 7, 25, 8, 0, 0, TimeSpan.Zero),
            Location = new ReaderLocation { DocumentProgress = 0.3 }
        },
        new LibraryBook
        {
            Id = 3,
            Title = "未开始的书",
            Author = "远行者",
            Format = ".txt",
            LastOpenedUtc = new DateTimeOffset(2026, 7, 26, 8, 0, 0, TimeSpan.Zero)
        }
    ]);

    Assert(presenter.Summary == "3 本书 · 数据仅保存在本机" &&
           presenter.Books.Count == 3,
        "Library presenter summarizes a populated shelf");
    Assert(presenter.Books.Single(item => item.Book.Id == 2).Author == "未知作者" &&
           presenter.Books.Single(item => item.Book.Id == 1).FormatLabel == "EPUB" &&
           presenter.Books.Single(item => item.Book.Id == 3).ProgressText == "未开始" &&
           presenter.Books.All(item => item.HasFallbackCover && !item.HasCover),
        "Library projection normalizes author, format label, progress text, and cover fallback");

    // Most recently opened *with progress* wins: book 3 is newer but unread.
    Assert(presenter.ContinueReading is { Book.Id: 2 } &&
           presenter.ContinueReadingSubtitle == $"{0.3:P0} · PDF",
        "Library presenter picks the newest in-progress book to continue");

    var filtered = presenter.BuildView("远行者");
    Assert(filtered.VisibleBooks.Count == 2 &&
           filtered.VisibleBooks.All(item => item.Author == "远行者") &&
           !filtered.ShowEmptyState,
        "Library presenter filters by author");
    Assert(presenter.BuildView("PAPER").VisibleBooks.Single().Book.Id == 2,
        "Library presenter filters titles case-insensitively");

    var noMatch = presenter.BuildView("不存在的书名");
    Assert(noMatch.ShowEmptyState &&
           noMatch.EmptyTitle == "没有找到这本书" &&
           noMatch.EmptyDescription == "换个书名或作者关键词再试试。",
        "Library presenter distinguishes no-match from an empty shelf");

    presenter.SetBooks([new LibraryBook { Id = 9, Title = "只导入未读", Format = ".epub" }]);
    Assert(presenter.ContinueReading is null,
        "Library presenter hides continue-reading when nothing has progress");
}

static async Task TestBookSearchIndexerAsync(string testRoot)
{
    var fixtureRoot = Path.Combine(testRoot, "indexer-fixture");
    Directory.CreateDirectory(fixtureRoot);
    var htmlPath = Path.Combine(fixtureRoot, "chapter.xhtml");
    File.WriteAllText(htmlPath, """
        <!doctype html>
        <html>
          <head><title>不可索引的标题</title><style>.secret { display:none }</style></head>
          <body>
            <h1>可见标题</h1>
            <p>正文&nbsp;内容 &amp; 实体</p>
            <script>script-secret-marker()</script>
            <style>style-secret-marker { color: red }</style>
            <noscript>noscript-secret-marker</noscript>
            <!-- comment-secret-marker -->
          </body>
        </html>
        """, Encoding.UTF8);

    var boundedPath = Path.Combine(fixtureRoot, "bounded.txt");
    await using (var stream = new FileStream(boundedPath, FileMode.Create, FileAccess.Write, FileShare.None))
    {
        var block = Encoding.ASCII.GetBytes(new string('x', 64 * 1024));
        var remaining = BookSearchIndexer.MaximumSectionBytes + block.Length;
        while (remaining > 0)
        {
            var count = Math.Min(remaining, block.Length);
            await stream.WriteAsync(block.AsMemory(0, count));
            remaining -= count;
        }

        await stream.WriteAsync("beyond-limit-marker"u8.ToArray());
    }

    var skippedPath = Path.Combine(fixtureRoot, "cover.png");
    File.WriteAllBytes(skippedPath, PixelPng());
    var session = new ReaderSession
    {
        SourcePath = htmlPath,
        Title = "索引器测试",
        RootDirectory = fixtureRoot,
        Kind = ReaderDocumentKind.Epub,
        Sections =
        [
            new ReaderSection("HTML 章节", htmlPath),
            new ReaderSection("有界文本", boundedPath),
            new ReaderSection("不支持的二进制章节", skippedPath),
            new ReaderSection("缺失章节", Path.Combine(fixtureRoot, "missing.xhtml"))
        ],
        IsReflowable = true,
        SupportsInPageSearch = true
    };

    var sections = await new BookSearchIndexer().BuildAsync(session);
    Assert(sections.Count == 2, "Search indexer skips unsupported and unreadable sections");
    var htmlText = sections.Single(section => section.SectionIndex == 0).Text;
    Assert(htmlText.Contains("可见标题 正文 内容 & 实体", StringComparison.Ordinal), "Search indexer extracts visible normalized text");
    Assert(!htmlText.Contains("不可索引", StringComparison.Ordinal) &&
           !htmlText.Contains("secret-marker", StringComparison.Ordinal), "Search indexer strips head/script/style/noscript/comments");
    var boundedText = sections.Single(section => section.SectionIndex == 1).Text;
    Assert(boundedText.Length <= BookSearchIndexer.MaximumSectionBytes, "Search indexer enforces per-section byte bound");
    Assert(!boundedText.Contains("beyond-limit-marker", StringComparison.Ordinal), "Search indexer excludes bytes beyond bound");
}

static void TestReaderRuntimeAnnotationApi()
{
    var script = ReaderRuntime.BuildRuntimeScript();
    var darkBootstrap = ReaderRuntime.BuildBootstrapScript(
        new AppSettings { ReaderTheme = ReaderThemeMode.Auto },
        restoreProgress: 0,
        isDarkAppTheme: true);
    Assert(darkBootstrap.Contains("auto-dark", StringComparison.Ordinal),
        "ReaderRuntime accepts platform theme state without a WPF dependency");

    foreach (var contract in new[]
             {
                 "nogareader.selection",
                 "nogareader.selection-clear",
                 "selectionAnchor",
                 "applyAnnotations",
                 "goToAnnotation",
                 "revealText",
                 "const revealText = (query, occurrenceIndex = 0) =>",
                 "occurrence <= requestedOccurrence",
                 "searchFrom = index + Math.max(1, normalizedNeedle.length)",
                 "clearSelection",
                 "data-nogar-id",
                 "nogareader.annotation-click",
                 "点击查看完整笔记"
             })
    {
        Assert(script.Contains(contract, StringComparison.Ordinal), $"ReaderRuntime annotation API: {contract}");
    }

    var controllerType = typeof(DocumentLoader).Assembly.GetType(
        "NoGaReader.Services.ReaderViewController",
        throwOnError: true)!;
    var revealMethod = controllerType.GetMethod(
        "RevealTextAsync",
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("ReaderViewController.RevealTextAsync was not found.");
    var revealParameters = revealMethod.GetParameters();
    Assert(revealMethod.ReturnType == typeof(Task<bool>), "ReaderViewController revealText return contract");
    Assert(revealParameters.Length == 2 &&
           revealParameters[0].ParameterType == typeof(string) &&
           revealParameters[1].ParameterType == typeof(int),
        "ReaderViewController revealText occurrence parameter contract");
    Assert(revealParameters[1].HasDefaultValue && Convert.ToInt32(revealParameters[1].DefaultValue) == 0,
        "ReaderViewController revealText occurrence default");
}

static async Task TestLibraryScannerAsync(string testRoot)
{
    var fixtureRoot = Path.Combine(testRoot, "scanner-fixture");
    var nestedRoot = Path.Combine(fixtureRoot, "nested");
    Directory.CreateDirectory(nestedRoot);
    File.WriteAllText(Path.Combine(fixtureRoot, "alpha.txt"), "alpha", Encoding.UTF8);
    File.WriteAllText(Path.Combine(nestedRoot, "beta.md"), "# beta", Encoding.UTF8);
    File.WriteAllText(Path.Combine(nestedRoot, "ignored.bin"), "not a book", Encoding.UTF8);

    var scanner = new LibraryScanner();
    var shallowResult = await scanner.ScanAsync(
        fixtureRoot,
        new LibraryScanOptions
        {
            ReadBookMetadata = false,
            IncludeCoverImages = false,
            IncludeSubfolders = false,
            MaximumFileCount = 10
        });

    Assert(Path.GetFullPath(shallowResult.RootDirectory) == Path.GetFullPath(fixtureRoot), "Library scanner temporary root");
    Assert(shallowResult.CandidateFileCount == 1 && shallowResult.Items.Count == 1,
        "Library scanner excludes child directories when disabled");
    Assert(shallowResult.Items.Single().Title == "alpha" && shallowResult.Items.Single().Format == "TXT",
        "Library scanner shallow result");

    var recursiveResult = await scanner.ScanAsync(
        fixtureRoot,
        new LibraryScanOptions
        {
            ReadBookMetadata = false,
            IncludeCoverImages = false,
            IncludeSubfolders = true,
            MaximumFileCount = 10
        });

    Assert(recursiveResult.CandidateFileCount == 2 && recursiveResult.Items.Count == 2,
        "Library scanner includes child directories when enabled");
    Assert(recursiveResult.Items.Any(item => item.Title == "alpha" && item.Format == "TXT"), "Library scanner TXT metadata");
    Assert(recursiveResult.Items.Any(item => item.Title == "beta" && item.Format == "MD"), "Library scanner Markdown metadata");
    Assert(!recursiveResult.IsFileLimitReached && recursiveResult.Issues.Count == 0, "Library scanner clean completion");

    var knownSnapshots = recursiveResult.Items.ToDictionary(
        item => item.Path,
        item => new LibraryScanSnapshot
        {
            Path = item.Path,
            Title = item.Title,
            Author = item.Author,
            Format = item.Format,
            FileSize = item.FileSize,
            LastModifiedUtc = item.LastModifiedUtc
        },
        StringComparer.OrdinalIgnoreCase);
    var unchangedResult = await scanner.ScanAsync(
        fixtureRoot,
        new LibraryScanOptions
        {
            IncludeSubfolders = true,
            KnownBooks = knownSnapshots
        });
    Assert(unchangedResult.Items.Count == 2 && unchangedResult.Items.All(item => item.IsUnchanged),
        "Library scanner skips unchanged metadata and cover extraction");
}

static async Task TestAnnotationExportAsync(string testRoot)
{
    var fixtureRoot = Path.Combine(testRoot, "annotation-export-fixture");
    Directory.CreateDirectory(fixtureRoot);
    var book = new LibraryBook
    {
        Id = 7001,
        Path = Path.Combine(fixtureRoot, "source.epub"),
        Title = "# [危险](javascript:alert(1)) | *书名*",
        Author = "A_B",
        Format = "EPUB"
    };
    var createdAt = new DateTimeOffset(2026, 7, 17, 1, 2, 3, TimeSpan.Zero);
    var annotations = new[]
    {
        new Annotation
        {
            Id = "export-note",
            BookId = book.Id,
            Type = AnnotationType.Note,
            SectionIndex = 1,
            SectionProgress = 0.375,
            SelectedText = "第一行\n> 伪引用\n# 伪标题\n[链接](javascript:alert(1))",
            Note = "*强调* | <tag>\r\n---",
            Color = "yellow",
            Anchor = new TextAnchor
            {
                ExactText = "精确文本",
                Prefix = "前文",
                Suffix = "后文",
                StartPath = "0/1",
                EndPath = "0/2"
            },
            CreatedUtc = createdAt,
            ModifiedUtc = createdAt.AddMinutes(1)
        },
        new Annotation
        {
            Id = "export-bookmark",
            BookId = book.Id,
            Type = AnnotationType.Bookmark,
            SectionIndex = 0,
            SectionProgress = 0.1,
            CreatedUtc = createdAt.AddMinutes(-1),
            ModifiedUtc = createdAt
        }
    };
    var exportedAt = new DateTimeOffset(2026, 7, 17, 6, 7, 8, TimeSpan.Zero);
    var service = new AnnotationExportService();

    var markdown = service.CreateMarkdown(book, annotations, exportedAt);
    Assert(markdown.StartsWith(
            "# \\# \\[危险\\]\\(javascript:alert\\(1\\)\\) \\| \\*书名\\* — 批注导出\n",
            StringComparison.Ordinal),
        "Annotation Markdown escapes heading metacharacters");
    Assert(markdown.Contains("- 作者：A\\_B\n", StringComparison.Ordinal),
        "Annotation Markdown escapes inline metadata");
    Assert(markdown.Contains(
            "> \\> 伪引用\n> \\# 伪标题\n> \\[链接\\]\\(javascript:alert\\(1\\)\\)\n",
            StringComparison.Ordinal),
        "Annotation Markdown neutralizes quote, heading, and link injection");
    Assert(markdown.Contains("> \\*强调\\* \\| \\<tag\\>\n> \\-\\-\\-\n", StringComparison.Ordinal),
        "Annotation Markdown safely escapes note content");
    Assert(!markdown.Contains('\r') &&
           !markdown.Contains("\n# 伪标题", StringComparison.Ordinal) &&
           !markdown.Contains("\n> > 伪引用", StringComparison.Ordinal),
        "Annotation Markdown uses LF and contains no injected block syntax");

    var json = service.CreateJson(book, annotations, exportedAt);
    using (var document = JsonDocument.Parse(json))
    {
        var root = document.RootElement;
        Assert(root.GetProperty("schemaVersion").GetInt32() == AnnotationExportDocument.CurrentSchemaVersion,
            "Annotation JSON schema version");
        Assert(root.GetProperty("exportedAtUtc").GetDateTimeOffset() == exportedAt,
            "Annotation JSON export timestamp");
        var items = root.GetProperty("annotations").EnumerateArray().ToArray();
        Assert(items.Length == 2 &&
               items[0].GetProperty("kind").GetString() == "bookmark" &&
               items[1].GetProperty("kind").GetString() == "note",
            "Annotation JSON stable ordering and kinds");
        Assert(items[1].GetProperty("sectionNumber").GetInt32() == 2 &&
               items[1].GetProperty("textSelector").GetProperty("exact").GetString() == "精确文本",
            "Annotation JSON portable section and quote selector");
    }

    Assert(!json.Contains("\"bookId\"", StringComparison.Ordinal) &&
           !json.Contains("\"id\"", StringComparison.Ordinal) &&
           !json.Contains("startPath", StringComparison.OrdinalIgnoreCase) &&
           !json.Contains("endPath", StringComparison.OrdinalIgnoreCase),
        "Annotation JSON excludes database and DOM-specific fields");

    var suggestedName = service.CreateSuggestedFileName(book, AnnotationExportFormat.Markdown);
    Assert(suggestedName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0,
        "Annotation export filename removes unsafe characters");
    var reservedName = service.CreateSuggestedFileName(
        new LibraryBook { Id = 7002, Path = Path.Combine(fixtureRoot, "con.epub"), Title = "CON" },
        AnnotationExportFormat.Markdown);
    Assert(reservedName == "_CON - 批注.md", "Annotation export avoids Windows reserved filename");

    var markdownPath = Path.Combine(fixtureRoot, "explicit", "annotations.md");
    var jsonPath = Path.Combine(fixtureRoot, "explicit", "annotations.json");
    var writtenMarkdownPath = await service.ExportToFileAsync(
        book,
        annotations,
        markdownPath,
        AnnotationExportFormat.Markdown);
    var writtenJsonPath = await service.ExportToFileAsync(
        book,
        annotations,
        jsonPath,
        AnnotationExportFormat.Json);
    Assert(writtenMarkdownPath == Path.GetFullPath(markdownPath) &&
           writtenJsonPath == Path.GetFullPath(jsonPath),
        "Annotation ExportToFileAsync returns explicit absolute paths");
    Assert(!HasUtf8Bom(File.ReadAllBytes(markdownPath)) && !HasUtf8Bom(File.ReadAllBytes(jsonPath)),
        "Annotation exports use UTF-8 without BOM");
    using (var writtenJson = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath, Encoding.UTF8)))
    {
        Assert(writtenJson.RootElement.GetProperty("schemaVersion").GetInt32() ==
               AnnotationExportDocument.CurrentSchemaVersion,
            "Annotation JSON file retains schema version");
    }

    var cancelledPath = Path.Combine(fixtureRoot, "explicit", "cancelled.json");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var cancelled = false;
    try
    {
        _ = await service.ExportToFileAsync(
            book,
            annotations,
            cancelledPath,
            AnnotationExportFormat.Json,
            cancellation.Token);
    }
    catch (OperationCanceledException)
    {
        cancelled = true;
    }

    Assert(cancelled && !File.Exists(cancelledPath), "Annotation export honors pre-cancellation");
    Assert(!Directory.EnumerateFiles(fixtureRoot, "*.tmp", SearchOption.AllDirectories).Any(),
        "Annotation export cancellation leaves no temporary files");
}

static void TestTocNodeFlatten(string testRoot)
{
    var chapterOnePath = Path.Combine(testRoot, "chapter-one.xhtml");
    var chapterTwoPath = Path.Combine(testRoot, "chapter-two.xhtml");
    var nodes = new[]
    {
        new TocNode
        {
            Title = "第一部",
            FullPath = chapterOnePath,
            Children =
            [
                new TocNode
                {
                    Title = "第一章",
                    FullPath = chapterOnePath,
                    Fragment = "chapter-1",
                    Children =
                    [
                        new TocNode
                        {
                            Title = "第一节",
                            FullPath = chapterOnePath,
                            Fragment = "section-1"
                        }
                    ]
                }
            ]
        },
        new TocNode { Title = "第二部", FullPath = chapterTwoPath }
    };

    var flattened = nodes.SelectMany(node => node.Flatten()).ToArray();
    Assert(flattened.Select(node => node.Title).SequenceEqual(["第一部", "第一章", "第一节", "第二部"]), "TOC flatten pre-order");
    Assert(flattened[1].Fragment == "chapter-1" && flattened[2].Fragment == "section-1", "TOC flatten preserves fragments");
}

static IReadOnlyList<int> FindAllOccurrences(
    string source,
    string query,
    StringComparison comparison)
{
    var results = new List<int>();
    var searchStart = 0;
    while (searchStart <= source.Length - query.Length)
    {
        var matchStart = source.IndexOf(query, searchStart, comparison);
        if (matchStart < 0)
        {
            break;
        }

        results.Add(matchStart);
        searchStart = matchStart + Math.Max(1, query.Length);
    }

    return results;
}

static bool HasUtf8Bom(byte[] bytes) =>
    bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

static bool NearlyEqual(double left, double right) => Math.Abs(left - right) < 0.000_001;

static void CreateEpub(string path)
{
    using var stream = File.Create(path);
    using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
    WriteEntry(archive, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
    WriteEntry(archive, "META-INF/container.xml", """
        <?xml version="1.0"?>
        <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
          <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
        </container>
        """);
    WriteEntry(archive, "OEBPS/content.opf", """
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
          <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>测试 EPUB</dc:title></metadata>
          <manifest>
            <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
            <item id="c1" href="chapter1.html" media-type="text/html"/>
            <item id="c2" href="chapter2.xhtml" media-type="application/xhtml+xml"/>
          </manifest>
          <spine><itemref idref="c1"/><itemref idref="c2"/></spine>
        </package>
        """);
    WriteEntry(archive, "OEBPS/nav.xhtml", """
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
          <body><nav epub:type="toc"><ol><li><a href="chapter1.html">开篇</a></li><li><a href="chapter2.xhtml">继续</a></li></ol></nav></body>
        </html>
        """);
    WriteEntry(archive, "OEBPS/chapter1.html", "<HTML xmlns=\"http://www.w3.org/1999/xhtml\"><BODY><h1>开篇</h1><p ONCLICK=\"alert(1)\">Hello EPUB.</p><a HREF=\"data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==\">active data</a><a href=\"payload.xhtml\">payload</a><SCRIPT>alert('blocked')</SCRIPT><IFRAME SRC=\"https://example.com\"/></BODY></HTML>");
    WriteEntry(archive, "OEBPS/chapter2.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><h1>继续</h1><p>Second section.</p></body></html>");
    WriteEntry(archive, "OEBPS/payload.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><script>window.chrome.webview.postMessage('unsafe')</script></body></html>");
}


static void CreateCbt(string path)
{
    using var stream = File.Create(path);
    using var writer = SharpCompress.Writers.WriterFactory.OpenWriter(
        stream,
        SharpCompress.Common.ArchiveType.Tar,
        SharpCompress.Writers.WriterOptions.ForTar(SharpCompress.Common.CompressionType.None));
    using var image = new MemoryStream(PixelPng(), writable: false);
    writer.Write("page1.png", image, DateTime.UtcNow);
}


static void CreateXpsWithFixedPagesAndResourceImage(string path)
{
    using var stream = File.Create(path);
    using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
    WriteEntry(archive, "[Content_Types].xml", """
        <?xml version="1.0" encoding="utf-8"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />
          <Default Extension="fdseq" ContentType="application/vnd.ms-package.xps-fixeddocumentsequence+xml" />
          <Default Extension="fdoc" ContentType="application/vnd.ms-package.xps-fixeddocument+xml" />
          <Default Extension="fpage" ContentType="application/vnd.ms-package.xps-fixedpage+xml" />
          <Default Extension="png" ContentType="image/png" />
        </Types>
        """);
    WriteEntry(archive, "_rels/.rels", """
        <?xml version="1.0" encoding="utf-8"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="Rfdseq"
                        Type="http://schemas.microsoft.com/xps/2005/06/fixedrepresentation"
                        Target="/FixedDocumentSequence.fdseq" />
        </Relationships>
        """);
    WriteEntry(archive, "FixedDocumentSequence.fdseq", """
        <?xml version="1.0" encoding="utf-8"?>
        <FixedDocumentSequence xmlns="http://schemas.microsoft.com/xps/2005/06">
          <DocumentReference Source="/Documents/1/FixedDocument.fdoc" />
        </FixedDocumentSequence>
        """);
    WriteEntry(archive, "Documents/1/FixedDocument.fdoc", """
        <?xml version="1.0" encoding="utf-8"?>
        <FixedDocument xmlns="http://schemas.microsoft.com/xps/2005/06">
          <PageContent Source="/Documents/1/Pages/1.fpage" />
          <PageContent Source="/Documents/1/Pages/2.fpage" />
        </FixedDocument>
        """);
    WriteEntry(archive, "Documents/1/Pages/1.fpage", """
        <?xml version="1.0" encoding="utf-8"?>
        <FixedPage xmlns="http://schemas.microsoft.com/xps/2005/06"
                   Width="480" Height="640" xml:lang="zh-CN">
          <Path Fill="#FFF1F5F9" Data="M 0,0 L 480,0 480,640 0,640 Z" />
          <Path Fill="#FF2563EB" Data="M 48,48 L 432,48 432,224 48,224 Z" />
          <Path Data="M 48,272 L 176,272 176,400 48,400 Z">
            <Path.Fill>
              <ImageBrush ImageSource="/Documents/1/Resources/Images/resource.png" />
            </Path.Fill>
          </Path>
        </FixedPage>
        """);
    WriteEntry(archive, "Documents/1/Pages/2.fpage", """
        <?xml version="1.0" encoding="utf-8"?>
        <FixedPage xmlns="http://schemas.microsoft.com/xps/2005/06"
                   Width="480" Height="640" xml:lang="zh-CN">
          <Path Fill="#FFFFF7ED" Data="M 0,0 L 480,0 480,640 0,640 Z" />
          <Path Fill="#FFEA580C" Data="M 48,48 L 432,48 432,224 48,224 Z" />
          <Path Stroke="#FF7C2D12" StrokeThickness="12"
                Data="M 64,320 L 176,448 416,272" />
        </FixedPage>
        """);
    WriteEntry(archive, "Documents/1/Pages/_rels/1.fpage.rels", """
        <?xml version="1.0" encoding="utf-8"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="Rimage"
                        Type="http://schemas.microsoft.com/xps/2005/06/required-resource"
                        Target="../Resources/Images/resource.png" />
        </Relationships>
        """);
    WriteBinaryEntry(archive, "Documents/1/Resources/Images/resource.png", PixelPng());
}

static void CreateXpsWithTooManyEntries(string path)
{
    using var stream = File.Create(path);
    using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
    for (var index = 0; index <= 10_000; index++)
    {
        _ = archive.CreateEntry($"metadata/{index:D5}.xml", CompressionLevel.NoCompression);
    }
}

static void TestDocxImageRoundTrip(string path)
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            using var imageStream = new MemoryStream(PixelPng());
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.StreamSource = imageStream;
            bitmap.EndInit();
            bitmap.Freeze();

            var image = new System.Windows.Controls.Image
            {
                Source = bitmap,
                Width = 64,
                Height = 64
            };
            var paragraph = new System.Windows.Documents.Paragraph(
                new System.Windows.Documents.Run("image before "));
            paragraph.Inlines.Add(new System.Windows.Documents.InlineUIContainer(image));
            paragraph.Inlines.Add(new System.Windows.Documents.Run(" image after"));
            var document = new System.Windows.Documents.FlowDocument(paragraph);
            OfficeDocumentService.Save(document, path);

            var plainTextPath = Path.ChangeExtension(path, ".txt");
            File.WriteAllText(plainTextPath, "sentinel", Encoding.UTF8);
            var unsafeTextSaveRejected = false;
            try
            {
                OfficeDocumentService.Save(document, plainTextPath);
            }
            catch (NotSupportedException)
            {
                unsafeTextSaveRejected = true;
            }
            Assert(unsafeTextSaveRejected &&
                   File.ReadAllText(plainTextPath, Encoding.UTF8) == "sentinel",
                "TXT save rejects embedded images without overwriting the original file");

            using (var word = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(path, false))
            {
                var mainPart = word.MainDocumentPart
                    ?? throw new InvalidDataException("Image DOCX fixture has no main document part.");
                Assert(mainPart.ImageParts.Any() &&
                       mainPart.Document.Body?.Descendants<DocumentFormat.OpenXml.Wordprocessing.Drawing>().Any() == true,
                    "DOCX image save creates an image part and w:drawing");
            }

            using (var archive = ZipFile.OpenRead(path))
            {
                var packageEntries = archive.Entries.Select(entry => entry.FullName).ToArray();
                Assert(archive.Entries.Any(entry =>
                        entry.FullName.StartsWith("media/", StringComparison.OrdinalIgnoreCase) ||
                        entry.FullName.Contains("/media/", StringComparison.OrdinalIgnoreCase)),
                    "DOCX image save writes package media content; entries: " +
                    string.Join(", ", packageEntries));
            }

            var reloadedDocument = OfficeDocumentService.Load(path);
            var reloadedImage = reloadedDocument.Blocks
                .OfType<System.Windows.Documents.Paragraph>()
                .SelectMany(item => item.Inlines.OfType<System.Windows.Documents.InlineUIContainer>())
                .Select(container => container.Child)
                .OfType<System.Windows.Controls.Image>()
                .FirstOrDefault();
            Assert(reloadedImage?.Source is System.Windows.Media.Imaging.BitmapSource,
                "DOCX image round-trip restores InlineUIContainer image content");
        }
        catch (Exception exception)
        {
            failure = exception;
        }
    })
    {
        IsBackground = true,
        Name = "NoGaReader DOCX image smoke"
    };
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if (!thread.Join(TimeSpan.FromSeconds(30)))
    {
        throw new TimeoutException("DOCX image round-trip did not finish within 30 seconds.");
    }

    if (failure is not null)
    {
        throw new InvalidOperationException("DOCX image round-trip failed.", failure);
    }
}

static void AddHyperlinkToDocx(string path)
{
    using var word = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(path, true);
    var mainPart = word.MainDocumentPart
        ?? throw new InvalidDataException("DOCX fixture has no main document part.");
    var relationship = mainPart.AddHyperlinkRelationship(new Uri("https://example.com"), true);
    mainPart.Document.Body!.AppendChild(
        new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
            new DocumentFormat.OpenXml.Wordprocessing.Hyperlink(
                new DocumentFormat.OpenXml.Wordprocessing.Run(
                    new DocumentFormat.OpenXml.Wordprocessing.Text("external link")))
            {
                Id = relationship.Id
            }));
    mainPart.Document.Save();
}

static void CreateCbz(string path)
{
    var pixel = PixelPng();
    using var stream = File.Create(path);
    using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
    WriteBinaryEntry(archive, "page10.png", pixel);
    WriteBinaryEntry(archive, "page2.png", pixel);
}

static void CreateMaliciousComic(string path)
{
    using var stream = File.Create(path);
    using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
    WriteBinaryEntry(archive, "../escape.png", PixelPng());
}

static byte[] PixelPng()
{
    return Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
}

static void CreateMaliciousEpub(string path)
{
    using var stream = File.Create(path);
    using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
    WriteEntry(archive, "META-INF/container.xml", "<container><rootfiles><rootfile full-path=\"content.opf\"/></rootfiles></container>");
    WriteEntry(archive, "content.opf", "<package><manifest/><spine/></package>");
    WriteEntry(archive, "../escape.txt", "must not escape");
}

static void WriteEntry(
    ZipArchive archive,
    string name,
    string content,
    CompressionLevel compressionLevel = CompressionLevel.Optimal)
{
    var entry = archive.CreateEntry(name, compressionLevel);
    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
    writer.Write(content);
}

static void WriteBinaryEntry(ZipArchive archive, string name, byte[] content)
{
    var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
    using var output = entry.Open();
    output.Write(content);
}

static void Assert(bool condition, string name)
{
    if (!condition)
    {
        throw new InvalidOperationException($"Smoke test failed: {name}");
    }
}
