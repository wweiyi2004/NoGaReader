# NoGaReader

NoGaReader 是一个面向 Windows 10/11 与 Android 的本地免费阅读器。它采用洁净室实现：参考同类产品的公开功能，但不复制其代码、资源、品牌或界面。

当前版本为 **1.2.0**。V0.7 集中重做阅读器与书库的视觉反馈：搜索不再遮挡书名，滚动阅读隐藏左右翻页键，收藏与批注状态清晰可见，并加入阅读时长、书籍数、阅读天数和继续阅读组成的现代空置页。

## V1.2 重点

- 控制台 + 阅读窗口分离：主窗口作为书库/转换/设置控制台；打开书籍时弹出独立阅读窗口。
- 同路径书籍复用已打开的阅读窗口（`BookOpenCoordinator`）。
- 阅读窗隐藏导入/书库/转换等控制台导航，保留目录、笔记与阅读设置。

## V1.1 重点

- CHM：通过 Windows `hh.exe` 解包为本地 HTML 阅读，解析 `.hhc` 目录。
- XPS/OXPS：通过 Windows XPS 引擎渲染真实 FixedPage，并忽略包内非页面资源图。
- DJVU：经 Calibre 或 DjVuLibre `ddjvu` 转为 PDF 阅读。
- 云同步：可选文件夹/网盘目录同步进度、批注与设置；默认关闭，不同步源书文件。

## V1.0 重点

- 单实例：二次启动通过命名管道把文件路径交给已运行窗口。
- 打印：阅读器工具栏调用 WebView2 打印对话框。
- 漫画 CBT（tar）扩展支持。

## V0.9 重点

- 文档编辑器：侧栏“文档”入口，支持新建/打开/编辑/保存/另存为/打印/查找。
- 格式：DOCX（Open XML）、RTF、TXT 原生编辑；HTML 使用禁用页面脚本的安全只读阅读，旧版 DOC 提示用 Word/LibreOffice 另存为 DOCX，ODT 可先转换。
- 打开路由：选择 DOCX/RTF/TXT 时进入编辑器；HTML/HTM 进入安全阅读器。
- 依赖：`DocumentFormat.OpenXml`，无商业文档控件授权。

## V0.8 重点

- 批量转换：侧栏新增“转换”入口，支持多文件拖放、目标格式、输出目录与任务状态；免费无次数限制。
- Calibre 引擎：自动探测安装包内嵌、用户数据目录、系统安装与 PATH 中的 `ebook-convert.exe`，也可手动指定。
- Kindle 阅读：MOBI / AZW / AZW3 通过本地转换缓存为 EPUB 后进入现有阅读器。
- 引擎目录：仓库 `engines/calibre/` 用于放置内嵌运行时，发布时复制到输出目录。

## V0.7 重点

- 阅读器布局：全书搜索改为阅读区内的浮动面板，避开居中书名；滚动模式和漫画连续模式自动收起左右翻页按钮。
- 主题一致性：工具栏悬停、目录树、下拉菜单、批注面板和笔记窗口统一使用应用主题色；阅读背景默认跟随应用明暗主题，同时保留纸张、明亮和夜间手动选项。
- 收藏反馈：收藏按钮使用空心/实心图标、主题强调色、状态提示和批注数量角标，是否已收藏一眼可见。
- 批注闭环：正文高亮可点击并打开对应完整批注；批注详情提供编辑与“取消标记”，高亮、笔记和书签都可撤销。
- 现代书库：更大的封面卡片、格式标签、阅读进度、书库数量与更清晰的搜索/筛选控件。
- 阅读仪表盘：没有打开书籍时显示累计阅读时长、书籍数量、阅读天数、最近一本书和继续阅读入口。

V0.6 的漫画文件夹读取、虚拟化缩略图与有界缓存继续保留。

## V0.6 重点

- 图片文件夹漫画：递归发现 JPG、PNG、WebP、GIF、BMP 和 TIFF，按相对路径自然排序，非图片文件会被忽略。
- 无复制读取：文件夹图片通过独立的只读虚拟主机提供给漫画画布，缓存只保存小型清单和查看器资源。
- 大漫画优化：缩略图列表使用 WPF 回收式虚拟化，最多强缓存 96 张解码后缩略图；漫画页不再在普通目录树中重复建模。
- 文件夹变更检测：根据相对路径、文件大小和修改时间重建清单，新增或替换页面后重新打开即可刷新。
- 真实归档验收：除 CBZ 与原生 7Z/CB7 外，已使用 RAR 4.x 签名的真实 CBR 执行端到端打开测试。

## V0.5 漫画模式

- 漫画归档：直接导入 CBZ/ZIP、CBR/RAR 和 CB7，按带数字的文件名自然排序，书库使用第一页作封面。
- 专用漫画画布：单页、双页和纵向连续三种布局，双页可保持封面单独显示。
- 日漫阅读方向：可在左向右与右向左之间切换，键盘和画布两侧箭头随阅读方向同步。
- 画面控制：适应宽度、适应高度或原始尺寸；50%–300% 缩放，支持 Ctrl+滚轮、双击快速缩放和拖动已放大页面。
- 页管理：缩略图面板直接跳页，预加载前后三页，页码、全书进度、缩放和书签状态同步。
- 安全解包：拒绝密码包、符号链接、绝对路径、目录穿越和异常压缩比，并限制条目数、单页与总解包体积。

V0.4 已有的 SQLite 书库、文本锚点、批注、全书搜索、缺失文件维护和批注导出在 V0.5 中保持不变。

## 书库与文本阅读

- SQLite 本地书库：记录书籍、书库文件夹、阅读位置、书签、批注和搜索索引。
- 文件夹管理：每个书库文件夹可选择“包含子文件夹”或“仅当前文件夹”，查看路径是否存在和上次扫描时间，也可只移除扫描注册而保留书籍与批注。
- 文件夹扫描：按各文件夹的递归范围发现 NoGaReader 已支持的文件；EPUB/FB2 可读取书名、作者与封面，其他格式使用文件名等基础信息。
- 缺失文件维护：扫描完成后检查已登记书籍的路径并显示“文件缺失”；格式、大小、书名和作者能唯一匹配时可自动重定位，也可手动选择同格式文件，同时保留书籍 ID、阅读位置、书签和批注。
- 安全移除书籍：确认框明确提示将级联清除该书的位置、书签、笔记与索引，但不会删除源文件。
- EPUB 分层目录：保留 EPUB 3 Nav 或 EPUB 2 NCX 的父子层级，并支持跳到章节内锚点。
- 本地批注：新建高亮时可直接选择黄、绿、蓝、粉四种颜色；高亮和笔记可随时改色，已有笔记可编辑正文。
- 批注导出：当前书籍的书签、高亮和笔记可导出为 Markdown 或 schema v1 的版本化 JSON；文件通过同目录临时文件原子替换，避免留下半写入结果。
- 文本锚点：可重排内容的阅读位置、高亮和笔记同时保存 DOM 文本路径、原文、前后文与章内进度；恢复时优先找回原文，失败后退回到归一化进度。
- 全书搜索：同一章节中的每个非重叠命中都会成为独立结果，记录 occurrence 序号和正文偏移；双击结果可准确跳到第二处及之后的相同文本。
- 阅读控制器：`ReaderViewController` 集中管理 WebView2 阅读运行时调用，减少窗口代码与页面脚本的直接耦合。
- 可重复 UI 冒烟：`scripts\ui-smoke.ps1` 在隔离数据目录中启动指定构建，检查关键 UI Automation 控件和批注面板开关，并只清理自己启动的进程与临时数据。

## 支持的阅读格式

- EPUB：安全解包、读取 OPF spine、EPUB 3 Nav/EPUB 2 NCX 分层目录、书名/作者/封面和统一阅读排版。
- FB2：安全 XML 解析为本地 HTML，支持标题、段落、强调、诗歌和常见内嵌图片。
- CBZ/ZIP、CBR/RAR、CB7/7Z 和图片文件夹：按自然文件名顺序进入专用漫画模式。
- Markdown：使用 Markdig 渲染标题、强调、列表、表格、任务列表、代码块、链接和受控本地图片。
- TXT、LOG、NFO：以安全的可换行文本方式阅读。
- PDF：使用 WebView2 内置 PDF 查看能力。
- HTML、XHTML、MHT、MHTML、XML：本地显示；页面脚本关闭，外部网络资源拦截。
- JPG、JPEG、PNG、GIF、WebP、BMP、TIFF 等常见图片。

EPUB、FB2、Markdown 与纯文本支持分页/连续滚动、统一排版和全书文本索引。漫画包使用独立的图像阅读运行时；支持页级进度和书签，但不提供图像 OCR 或文本高亮。

## 阅读体验

- 跟随应用、纸张、明亮、夜间四种阅读背景，正文字号、行距和版心宽度可调。
- 分页与连续滚动两种模式；左右键先在章内翻页，到边界后再切换章节。
- 保存章节、可见原文锚点和章内归一化进度；重新打开后优先恢复到原文，锚点失效时退回到接近原位置。
- EPUB 分层目录树与章节内片段跳转。
- 当前书籍的书签、高亮和笔记列表；正文标记可点击查看完整批注，支持回到原文、编辑笔记、四色改色和取消标记。
- 批注可导出为适合阅读的 Markdown，或用于后续交换工具的版本化 JSON。
- 全书搜索结果显示章节与上下文片段；同章重复文本分别列出，双击后标出对应 occurrence，而不是总回到第一处。
- 打开文件、拖放、命令行文件参数、最近阅读和沉浸全屏。
- 跟随系统、浅色、深色三种应用主题；深色模式同步 Windows 标题栏。
- 无账户、无广告、无遥测、无云端上传。

## 当前限制

- MOBI/AZW/AZW3/DJVU 在缺少引擎时会提示安装 Calibre 或专用工具；XPS/OXPS 使用 Windows 内置渲染；CHM 依赖系统 hh.exe。
- 高亮和笔记目前用于 EPUB、FB2、Markdown、TXT 等受控可重排内容；尚无 PDF 专用批注层。
- 精确阅读位置和批注使用 NoGaReader 自己的文本锚点，并以“章节 + 章内归一化进度”兜底，不是 EPUB CFI。正文被替换、重复文本缺少足够上下文或出版社结构变化很大时，旧位置可能退回邻近页面，旧批注也可能无法重新挂载。
- 全书索引仅处理本地 HTML/XML/纯文本章节，不解析 PDF 正文，也不提供 OCR。单章节最多读取 8 MB，单本书最多读取 64 MB，最多索引 10,000 个章节。
- 书库当前提供现代封面卡片、阅读进度与基础筛选，尚无标签、分组或手工元数据编辑。
- 批注目前只支持导出，不支持把 Markdown/JSON 再导入；笔记正文可以编辑，但引用原文和文本锚点没有手工编辑界面。
- 缺失状态在重新扫描书库后更新，没有常驻文件系统监视器。自动重定位只接受唯一元数据匹配，手动重定位用扩展名和文件大小提示降低误选风险，但两者都不验证文件内容身份。
- 尚无 MSIX 安装包与自动更新。格式转换依赖本地 Calibre 运行时。

转换引擎优先使用 `engines/calibre` 内嵌运行时，其次用户数据目录与系统 Calibre；应用只调用 `ebook-convert`，不实现自研转换内核。完整 Calibre 二进制可按 GPL 合规方式随发布附带。

## 技术结构

```text
WPF / .NET 8 主窗口
├─ DocumentLoader：按扩展名选择阅读适配器
│  ├─ ConvertedEbookLoader：MOBI/AZW/AZW3 → 缓存 EPUB
│  ├─ EpubLoader：ZIP → container.xml → OPF → spine + 分层 TOC
│  ├─ Fb2Loader：安全 XML → 本地 HTML
│  ├─ ComicArchiveLoader / Extractor：CBZ/ZIP、CBR/RAR、CB7 安全解包 → 漫画页模型
│  ├─ ComicFolderLoader：只读图片文件夹 → 漫画页模型
│  └─ HtmlDocumentFactory：Markdown/TXT → 受控本地 HTML
├─ ReaderViewController：WebView2 阅读命令与返回值边界
├─ ReaderRuntime：排版、分页、选择、文本锚点和批注挂载
├─ ComicReaderController / Assets：漫画布局、方向、缩放、跳页与预加载
├─ CalibreConverter / CalibreRuntimeLocator：本地 ebook-convert 探测与批量转换
├─ OfficeDocumentService + DocumentEditorDialog：DOCX/RTF/TXT 编辑与打印
├─ SingleInstanceService：单实例与打开文件 IPC
├─ CloudSyncService + SyncDialog：可选文件夹云同步
├─ ChmLoader / XpsLoader / DjvuLoader：固定版式扩展
├─ LibraryScanner：可选递归扫描、EPUB/FB2 元数据与封面
├─ BookSearchIndexer：有界正文抽取与纯文本索引
├─ LibraryDatabase：SQLite 书库、位置、批注、缺失/重定位与 FTS5
├─ AnnotationExportService：Markdown/版本化 JSON 与原子写入
├─ RecentStore：兼容最近阅读列表
└─ SettingsStore：应用主题与阅读排版参数
```

详细设计见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)，移动端拆分与交付路线见 [docs/mobile-architecture.md](docs/mobile-architecture.md)。原软件分析见 [cp.md](cp.md)。

## 构建与运行

开发要求：

- Windows 10/11 x64
- .NET 9.0.311 SDK（由 `global.json` 固定；Windows 客户端仍以 .NET 8 为目标）
- Microsoft Edge WebView2 Runtime（Windows 10/11 通常已安装）

```powershell
dotnet restore .\NoGaReader.sln
dotnet build .\NoGaReader.sln -c Debug
dotnet run --project .\src\NoGaReader\NoGaReader.csproj
```

Android 开发还需要 .NET MAUI Android workload、Android SDK 35 和 JDK 17/21：

```powershell
dotnet workload install maui-android
dotnet build .\src\NoGaReader.Mobile\NoGaReader.Mobile.csproj -c Debug
.\scripts\build-android.ps1 -Configuration Release -Format aab
```

Android 版通过系统文件选择器导入书籍并复制到应用私有目录，当前支持 EPUB、PDF、FB2、CBZ、TXT、Markdown、HTML 和常见图片；包含本地书库、目录、全文搜索、阅读进度、书签、高亮/笔记、明暗阅读主题以及 Android 原生 PDF 分页渲染。与桌面版一致，移动端阅读 WebView 同样离线：拦截全部外部网络资源，用户点击的网页/邮件链接交给系统应用处理。Release AAB 输出到 `artifacts\android\Release-aab`；发布到应用商店前仍需配置正式签名密钥。

打开指定文件：

```powershell
dotnet run --project .\src\NoGaReader\NoGaReader.csproj -- "E:\Books\example.epub"
```

直接打开漫画图片文件夹：

```powershell
dotnet run --project .\src\NoGaReader\NoGaReader.csproj -- "E:\Comics\chapter-01"
```

运行自动冒烟测试：

```powershell
dotnet run --project .\tests\NoGaReader.Smoke\NoGaReader.Smoke.csproj -c Debug
```

## 生成与运行便携版

```powershell
.\scripts\publish-portable.ps1
```

输出目录为 `artifacts\publish\win-x64`，V0.7.0 程序入口为 `artifacts\publish\win-x64\NoGaReader.exe`。当前脚本生成的是 Windows x64、依赖 .NET 8 Desktop Runtime 的 framework-dependent 便携版；目标电脑还需要 WebView2 Runtime。

发布目录包含 `portable.mode`，因此便携版用户数据写入：

```text
artifacts\publish\win-x64\UserData
```

发布后可在交互式 Windows 桌面运行永久 UI 冒烟脚本；下面使用仓库自带的 `README.md` 作为 Markdown 阅读夹具：

```powershell
.\scripts\ui-smoke.ps1 `
    -ExecutablePath .\artifacts\publish\win-x64\NoGaReader.exe `
    -FixturePath .\README.md
```

脚本会为本次运行创建 `.tmp\ui-smoke\<guid>` 隔离数据目录，验证主窗口、书库/批注关键控件，以及批注面板的打开与关闭，成功或失败后关闭它自己启动的进程并清理该目录。`FixturePath` 也可以直接指向漫画图片文件夹。调试时可加 `-KeepOpen` 保留程序和隔离数据，也可用 `-TimeoutSeconds` 调整 5–300 秒的等待时间。

## 快捷键

| 快捷键 | 功能 |
|---|---|
| `Ctrl+O` | 打开文件 |
| `←` / `→` | 上一页/下一页；到章节边界后切章 |
| `PageUp` / `PageDown` / `Space` | 翻页 |
| `Ctrl+F` | 打开当前书籍的全书搜索 |
| `Ctrl++` / `Ctrl+-` | 放大/缩小 |
| `F11` | 进入或退出全屏 |
| `Esc` | 关闭搜索/侧栏面板或退出全屏 |

## 数据位置

开发仓库内运行时，数据保存在仓库根目录 `.local-data`。也可以通过环境变量覆盖：

```powershell
$env:NOGAREADER_DATA_DIR = "E:\NoGaReaderData"
```

普通非便携发行版如果没有覆盖值，默认使用 `%LOCALAPPDATA%\NoGaReader`；便携版使用程序旁边的 `UserData`。

主要数据：

- `Data\library.db`：SQLite 书库、书库文件夹、阅读位置、书签、高亮、笔记和本地全文索引。
- `Data\settings.json`：应用主题、文本排版，以及漫画布局、方向、适应方式和缩放。
- `Data\recent.json`：兼容最近阅读列表、章节、章内进度和缩放。
- `Cache\library-covers\`：扫描得到的 EPUB/FB2/漫画封面缩略源。
- `Cache\`：EPUB/漫画安全解包、图片文件夹漫画清单和 FB2/Markdown/TXT 生成页。
- `WebView2\`：WebView2 用户数据。

删除 `library.db` 会清空书库、批注和全文索引；原始书籍不会被删除。需要备份批注时，应在程序退出后备份整个 `Data` 目录。

批注导出文件由用户在保存对话框中选择位置，不会自动写入数据目录。Markdown 和 JSON 都包含原书的本地来源路径；准备分享导出文件时，请先检查是否需要移除这项路径信息。

## 安全与隐私

- ZIP 解包拒绝路径穿越和符号链接，并限制文件数量、单文件大小与实际解压总量。
- 书库扫描按每个文件夹的递归开关工作，并跳过隐藏、系统和重解析点目录，忽略无权限路径；默认最多接收 50,000 个候选文件。
- EPUB/FB2 元数据与封面读取有独立的条目、XML 和图片大小限制。
- XML 禁止外部实体解析。
- EPUB 章节会移除脚本、iframe、对象、自动刷新和事件处理属性；无法安全清理的章节不启用阅读脚本。
- Markdown 禁用原始 HTML，危险协议链接会被改写；相对图片只有在源文件目录内、扩展名和大小符合限制时才复制到缓存。
- 直接打开的 HTML/MHT 页面禁用 JavaScript。
- 阅读器拒绝摄像头、麦克风、定位等页面权限，并阻止页面下载。
- 外部 HTTP/HTTPS 资源统一拦截；只有用户亲自点击的链接才交给系统浏览器。
- 自动重定位要求旧路径失效且仍在注册扫描范围内，并由格式、大小、书名和作者得到唯一匹配；手动重定位要求扩展名一致，文件大小变化时再次确认。若新路径已有记录，只有用户确认后才合并双方批注并保留原阅读位置；这些操作不会修改所选源文件。
- 批注导出使用安全文件名建议、显式版本化 DTO 和同目录临时文件原子替换；JSON 不暴露数据库 ID、缓存路径或 DOM 节点路径。
- 移除扫描文件夹只取消后续扫描范围；移除书籍记录才会在用户确认后由 SQLite 外键级联删除关联状态，两种操作都不会删除源文件。
- SQLite、封面、全文索引和批注默认保存在本机；可选文件夹云同步默认关闭，无账户、无遥测、不上传源书。

## 依赖

项目的直接 NuGet 依赖为：

- `Microsoft.Web.WebView2`：WPF 内嵌阅读视图。
- `Microsoft.Data.Sqlite`：本地书库、事务和全文搜索。
- [SharpCompress](https://www.nuget.org/packages/SharpCompress/)：解析 CBZ/ZIP、CBR/RAR 与 CB7/7Z 漫画归档。
- [Markdig](https://www.nuget.org/packages/Markdig/)：BSD-2-Clause 许可的 Markdown 解析器。

WPF 使用 .NET 8 Windows Desktop 运行时。WebView2 的 WPF 集成方式遵循 [Microsoft 官方文档](https://learn.microsoft.com/en-us/microsoft-edge/webview2/get-started/wpf)。

发布前还需要由项目所有者决定代码许可证；如果希望公开发布，MIT 是适合本项目定位的候选方案。
