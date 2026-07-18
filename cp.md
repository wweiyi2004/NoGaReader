# EPUB+ Reader & Converter：功能与架构概览

> 分析对象：本机 Microsoft Store 版 **EPUB+ Reader & Converter**
> 包版本：**4.9.2.0（x64）**
> 发布者：**Stargate Software**
> 分析日期：**2026-07-17**

## 一句话结论

EPUB+ Reader 并不是一套从零实现的单体阅读器，而是一个 **Microsoft Store/MSIX 封装的全信任桌面套件**：它以 **.NET 6 + WinUI 3** 主程序负责导航、最近文件、设置、授权和任务调度，再分别调用 **SumatraPDF/MuPDF 系阅读引擎**、**DevExpress WPF 文档查看/编辑器** 和 **Calibre 命令行转换引擎** 完成真正的阅读、文档处理与格式转换。

## 1. 用户可见功能

### 1.1 电子书与文档阅读

主界面提供六类入口：EPUB、MOBI、Kindle AZW、漫画、PDF 和“所有其他格式”。文件可以通过按钮选择、拖放、Windows 文件关联或“最近的文件”再次打开。

正式帮助页列出的主要阅读格式包括：

- EPUB、MOBI、AZW、AZW3
- PDF、DJVU、XPS/OXPS、CHM
- FB2/FB2Z
- CBZ、CBR、CBT、CB7 漫画包
- TXT

主程序的内部路由还识别 WebP、JPEG/JXR、PNG、GIF、HEIC、TIFF、TGA、BMP 等图片格式。这里要区分“程序内部能路由”与“已注册为 Windows 默认打开方式”：后者的范围更窄，见 4.2 节。

阅读器窗口提供的主要能力包括：

- 滚轮、触控板、方向键、Page Up/Down、Home/End 导航
- 指定页跳转
- 目录/书签导航
- 文本搜索、文本选择与内部链接
- 缩放、单页、对页和书本式布局
- 打印与另存为；打印属于高级版功能
- 默认、深色、复古和高对比度阅读主题；非默认主题需要高级版
- 记忆阅读器设置、最近文件与部分缩略图缓存

这些阅读能力实际由包内的 `ebookplus.exe` 提供。该文件的产品信息、内部字符串和设置格式表明它是基于 **SumatraPDF 3.5.2** 定制的 `EbookViewer`，底层使用 MuPDF 等原生解析/渲染组件。

### 1.2 批量电子书转换

“转换电子书”页面支持多文件列表、拖放、批量选择、逐项状态和转换后操作。直接验证到的工作流包括：

1. 添加一个或多个文件，或把文件拖入列表。
2. 为全部文件统一选择目标格式，也可以逐项选择。
3. 选择指定输出目录，或保存回各源文件所在目录。
4. 可选择转换完成后自动打开输出目录。
5. 转换后可以打开生成文件、打开所在目录或从列表移除任务。

转换器接受的输入扩展名包括：

`MOBI`、`EPUB`、`DOCX`、`PDF`、`AZW/AZW3/AZW4`、`CBC/CBZ/CBR/CB7`、`CHM`、`DJVU`、`FB2/FBZ`、`HTML/HTMLZ`、`LIT`、`LRF`、`PDB`、`PRC`、`PML`、`RB`、`RTF`、`SNB`、`TCR`、`TXT/TXTZ`。

界面中实际列出的 16 种输出格式为：

`PDF`、`EPUB`、`MOBI`、`AZW3`、`DOCX`、`FB2`、`RTF`、`TXT`、`ZIP`、`HTMLZ`、`OEB`、`LIT`、`LRF`、`PMLZ`、`RB`、`TXTZ`。

底层不是应用自己编写的转换器，而是调用随包附带的 **Calibre 7.22.0 `ebook-convert.exe`**。主程序为每个任务生成命令行，隐藏启动子进程，以 UTF-8 异步读取标准输出和错误输出，再把结果映射为“准备/转换中/完成/失败”。部分任务会继续使用 Calibre 的 `calibre-parallel.exe` 工作进程。

官方帮助说明免费版允许先进行 **3 次电子书转换**，高级版解锁无限转换和无限批量转换。

### 1.3 文本文档查看与编辑

“查看文档”页面提供以下入口：

- Word 文档：DOC、DOCX、DOT、DOTX、DOCM、DOTM 等
- Word 编辑器：新建和编辑 Microsoft Word 兼容文档
- TXT 与 RTF
- 本地 HTML/HTM 和 MHT 网页归档
- XML、ODT 等其他文本型文档

独立的 `docviewer.exe` 不只是只读查看器。程序集内存在保存/另存为、打印、快速打印、打印预览、插入表格、插入图片、插入对象、拼写检查和文档统计等命令。其“另存为”过滤器覆盖 DOCX、DOC、PDF、MHT、HTML、RTF、TXT、XML、ODT、EPUB、DOCM、DOTX、DOTM、DOT 等格式。

该模块是 **.NET Framework 4.7.2 + WPF + DevExpress RichEdit 23.2.5** 应用。界面中的 “PPTX PPT Editor” 属于另一款商店应用的推广/启动入口，不是当前安装包内置的 PowerPoint 编辑引擎。

### 1.4 壳层与通用功能

- 最近文件列表，可重新打开、定位或移除记录
- 文件拖放与 Windows“打开方式”集成
- 主界面浅色、深色、跟随系统三种外观
- 9 种界面语言：英语、简体中文、繁体中文、德语、西班牙语、法语、意大利语、日语、韩语
- 在线帮助、反馈、评分和更多应用入口
- Microsoft Store 许可证检测与刷新
- 月度、年度、终身高级版订阅/购买入口
- 高级版功能门控：无限转换、批量转换、打印、更多主题、去广告、升级与支持等

它更像“最近文件启动器 + 多引擎调度器”，没有发现面向用户的完整书库数据库、书籍标签管理、云同步或在线阅读服务。包内虽然带有大量 Calibre 资源，但主程序只明确调用了 `ebook-convert` 转换链路，不能据此把完整 Calibre 书库、服务器或编辑器都算作本软件功能。

## 2. 总体架构

```text
Microsoft Store / MSIX 包（x64，Full Trust）
└─ EpubReader.exe + EpubReader.dll
   ├─ WinUI 3 主界面、导航、设置、最近文件、Store 授权
   ├─ 阅读请求 ───────> EbookApp/ebookplus.exe
   │                    SumatraPDF 3.5.2 定制版 / MuPDF 等原生引擎
   ├─ 文档请求 ───────> docviewer/docviewer.exe
   │                    WPF / .NET Framework 4.7.2 / DevExpress 23.2
   ├─ 转换请求 ───────> EbooksConvert/ebook-convert.exe
   │                    Calibre 7.22 / Python / Qt / FFmpeg / SQLite
   │                    └─ 必要时启动 calibre-parallel.exe
   └─ Windows 服务 ───> 文件选择器、MRU、LocalSettings、Store、协议链接
```

这种架构的核心特点是：**主壳很小，真正的能力由三个重量级子系统提供**。

## 3. 各层技术栈

| 层/模块 | 直接证据 | 职责 |
|---|---|---|
| 安装与身份 | MSIX/AppX，包版本 4.9.2.0，x64，`Windows.FullTrustApplication` | 商店安装、更新、文件关联和包身份 |
| 主程序 | `.NET 6.0.36` 自包含运行时、WinUI 3、Windows App SDK 1.6、CommunityToolkit | 页面导航、任务编排、最近记录、设置、付费状态、子进程调度 |
| 阅读器 | `ebookplus.exe`，产品版本 3.5.2，SumatraPDF/MuPDF 特征 | EPUB/PDF/漫画/DjVu/XPS/图片等显示、搜索、缩放、目录和打印 |
| 文档模块 | WPF、.NET Framework 4.7.2、DevExpress 23.2.5 RichEdit | Word/RTF/TXT/HTML/XML/ODT 查看与编辑、打印、导出 |
| 转换模块 | Calibre 7.22.0、嵌入式 Python、Qt 插件、FFmpeg、ICU、SQLite、MathJax | 多格式导入、排版分析、格式转换与并行工作进程 |
| 系统集成 | Windows Storage、ApplicationData、StoreContext、Process、Named Pipes | 文件访问、设置保存、授权购买、进程间通信 |

安装包共约 **846 MB（约 807 MiB）/ 1516 个文件**。其中 Calibre 转换子系统约 **559 MB**，DevExpress 文档模块约 **155 MB**，说明安装体积主要来自完整第三方运行时和格式处理库，而不是 WinUI 主界面。

## 4. 启动、路由与进程通信

### 4.1 单实例与命令传递

主程序使用名为 `StarEpubAppMutex` 的互斥体维持单实例。第二次启动或从文件关联打开文件时，新实例会通过名为 `StarEbookPipe` 的命名管道把命令行/文件路径转交给已经运行的实例。

主窗口还监听 `StarEpubPipe`，用于接收阅读器侧事件，例如要求显示购买弹窗。UI 更新通过 WinUI DispatcherQueue 切回主线程。

### 4.2 文件路由

主程序按扩展名决定调用哪个外部进程：

- Word/RTF/HTML/XML/ODT/TXT 等文档 → `docviewer.exe`
- EPUB/MOBI/FB2、PDF、漫画、DjVu、XPS、图片等 → `ebookplus.exe`
- 转换列表 → `ebook-convert.exe`

MSIX 清单实际注册的 Windows 文件关联如下：

- 文档：`.doc`、`.docx`、`.dot`、`.dotx`、`.docm`、`.dotm`、`.odt`、`.rtf`、`.htm`、`.html`、`.xml`
- 电子书/固定版式：`.epub`、`.mobi`、`.fb2`、`.fb2z`、`.oxps`、`.xps`、`.cbt`、`.azw`、`.azw3`、`.djvu`、`.pdf`、`.cbr`、`.cbz`、`.cb7`

内部文件选择器和拖放支持的格式比系统关联更多，因此“程序能打开”不等于“安装后自动注册为该格式的处理程序”。

### 4.3 转换进程

转换任务的底层流程可以概括为：

```text
选择/拖入文件
  → 主程序校验扩展名和输出目录
  → 生成临时或最终输出路径
  → 启动隐藏的 ebook-convert.exe
  → 捕获 stdout/stderr 和退出结果
  → 检查输出文件是否存在且非空
  → 更新每项状态并提供打开/定位操作
```

从程序集可见，某些转换会附加 Calibre 的 `--enable-heuristics` 等选项；漫画类转换还会控制灰度处理。应用层主要负责编排，格式解析、内容抽取和重新封装都在 Calibre 进程中完成。

## 5. 数据与配置保存位置

### 5.1 安装文件

安装目录：

`C:\Program Files\WindowsApps\StargateSoftware.EPUB_4.9.2.0_x64__q477t2e6w9qj6`

该目录由 Windows 管理，普通使用时应视为只读；商店更新会整体替换包内容。

### 5.2 用户数据

应用包数据根目录：

`%LOCALAPPDATA%\Packages\StargateSoftware.EPUB_q477t2e6w9qj6`

主要内容为：

- `Settings\settings.dat`：Windows `ApplicationData.LocalSettings` 数据，包括语言、壳层主题、评分/提示状态、转换使用计数和部分许可证状态缓存。
- `LocalCache\Local\EbookViewer\SumatraPDF-settings.txt`：阅读器首选项和阅读状态。
- `LocalCache\Local\EbookViewer\sumatrapdfcache\`：阅读器生成的缩略图/封面缓存。
- `LocalCache\Local\calibre-cache\`：Calibre 转换缓存。
- `LocalCache\Roaming\calibre\`：Calibre 运行配置与缓存。
- `TempState\` 和系统临时目录：转换期间的临时输出或进程协调文件。

最近文件不是一份自建书库数据库，而是通过 Windows `StorageApplicationPermissions.MostRecentlyUsedList` 保存文件访问令牌和显示记录。转换后的成品则写入用户选择的输出目录或源文件目录。

## 6. 授权、联网与隐私边界

- 许可证与内购依赖 Microsoft Store API，包含月度、年度和永久授权产品。
- 帮助、反馈、评分、更多应用和推广入口会打开网站或 Microsoft Store。
- 阅读和转换核心链路是本地进程；未发现主壳把书籍上传到云端才能阅读或转换的证据。
- Calibre 包本身带有网络、内容服务器和新闻下载等通用资源，但当前主程序只明确使用本地 `ebook-convert`，不能把那些通用资源等同于本应用正在使用的联网功能。
- 清单声明 `runFullTrust` 和图片库访问能力。它不是严格受限的纯 UWP 沙箱应用，子进程能够处理用户选择的本地文件。

## 7. 工程与维护画像

### 优点

- 成熟引擎组合带来广泛的格式覆盖。
- 阅读、文档和转换模块相互隔离；单个子进程失败通常不必拖垮所有功能。
- MSIX 提供统一安装、卸载、更新和文件关联。
- 主壳职责清晰，批处理状态、输出目录与 Store 授权集中管理。

### 代价与风险

- 安装体积较大，三个 UI/运行时体系同时存在：WinUI/.NET 6、WPF/.NET Framework、原生 Sumatra/MuPDF，再加 Python/Qt Calibre。
- 视觉、快捷键和主题体验可能因进入不同子程序而不一致。
- 第三方引擎均以固定版本随包发布，安全修复和格式兼容更新依赖 Stargate Software 重新打包发布。
- 应用为全信任桌面程序，并会把不受信任的电子书/文档交给多个原生解析器处理；从安全工程角度，应及时更新应用，并对来源不明的文件保持谨慎。
- 主程序集仍保留一些旧产品名、推广链接和冗余资源，说明它包含复用/历史代码；不能仅凭包内存在某个资源就判断该能力已在界面正式开放。

## 8. 最终定位

从产品角度，它是一个面向 Windows 的“多格式阅读 + 文本文档处理 + 批量电子书转换”集合应用。

从工程角度，它是一个 **WinUI 调度壳**：

- 阅读质量和格式覆盖主要取决于 SumatraPDF/MuPDF；
- Word/RTF/HTML 类文档能力主要取决于 DevExpress RichEdit；
- 转换质量、速度和兼容性主要取决于 Calibre `ebook-convert`；
- EPUB+ Reader 自己的核心价值在于把这些引擎统一到同一个导航、最近文件、批处理、输出管理和商店授权体验中。

## 9. 证据来源与可信度

本概览综合了以下直接证据：

- 本机 `AppxManifest.xml`、运行时配置、依赖清单和文件版本信息
- 主程序集与文档查看器的类型、方法、字符串和调用关系
- 启动后的进程、模块与 Windows UI Automation 控件树
- 应用包本地数据目录结构；未读取或披露用户书籍内容
- [Stargate Software 官方帮助页](https://stargatesoft.com/apps/epub-reader/help/)
- [SumatraPDF 官方支持格式说明](https://www.sumatrapdfreader.org/docs/Supported-document-formats)
- [Calibre 官方 `ebook-convert` 文档](https://manual.calibre-ebook.com/en/generated/en/ebook-convert.html)

标为“内部识别”“引擎能力”或“推断”的内容，不保证一定在当前许可证、当前入口或所有文件上可用。应用后续更新也可能改变版本、格式范围和付费规则。
