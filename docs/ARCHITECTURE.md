# NoGaReader V1.1 架构说明

## 目标与边界

NoGaReader 的目标不是复制 EPUB+ Reader 的“多个大型第三方程序打包到一起”方案，而是提供一个体积小、边界清楚、数据留在本机并可持续演进的 Windows 阅读器。

V1.1 的设计约束：

1. 阅读、书库、搜索和批注均不依赖远程服务。
2. 只读取用户主动打开的文件，或用户明确加入书库的文件夹。
3. 不执行电子书自带脚本，不允许书籍静默联网。
4. 每种格式通过独立适配器进入统一 `ReaderSession`。
5. 阅读页面操作通过 `ReaderViewController` 与受控 `ReaderRuntime` 交互。
6. 长期数据由有版本的 SQLite 数据库保存；JSON 继续负责小型偏好与最近列表兼容。
7. 元数据扫描、正文索引和压缩包处理都必须有资源上限。
8. 书库重定位、移除和导出等有状态操作必须明确区分“数据库记录”与“源文件”。
9. 对外 JSON 使用显式、版本化的交换 DTO，不直接序列化数据库模型。
10. 大型转换引擎通过本地 `ebook-convert` 提供；优先内嵌于 `engines/calibre`，也可使用系统安装或用户指定路径。

## 模块地图

```text
MainWindow（界面与用例编排）
│
├─ 文档入口
│  └─ DocumentLoader
│     ├─ EpubLoader
│     ├─ Fb2Loader
│     ├─ ComicArchiveLoader + ComicArchiveExtractor
│     ├─ ComicFolderLoader
│     ├─ ConvertedEbookLoader（MOBI/AZW/AZW3）
│     └─ HtmlDocumentFactory + Markdig
│
├─ 转换子系统
│  ├─ CalibreRuntimeLocator
│  ├─ CalibreConverter（IFormatConverter）
│  └─ ConversionDialog
│
├─ 文档编辑子系统
│  ├─ OfficeDocumentService（Open XML / RTF / TXT / HTML）
│  └─ DocumentEditorDialog（WPF RichTextBox）
│
├─ 阅读视图
│  ├─ ReaderViewController
│  ├─ ReaderRuntime
│  ├─ ComicReaderController + ComicReaderAssets
│  └─ WebView2
│
├─ 本地书库
│  ├─ LibraryFoldersDialog
│  ├─ LibraryScanner（可选递归）
│  └─ LibraryDatabase（SQLite）
│
├─ 批注生命周期
│  ├─ NoteDialog（新建/编辑与四色选择）
│  ├─ LibraryDatabase（更新/级联删除）
│  └─ AnnotationExportService（Markdown / schema v1 JSON）
│
├─ 全书搜索
│  ├─ BookSearchIndexer
│  ├─ LibraryDatabase（FTS5 候选 + occurrence 展开）
│  └─ ReaderRuntime（精确 occurrence 显示）
│
├─ 产品化
│  ├─ SingleInstanceService（Mutex + Named Pipe）
│  ├─ FileAssociationService（HKCU 扩展名）
│  └─ CloudSyncService（文件夹清单同步）
│
└─ 轻量状态
   ├─ SettingsStore
   └─ RecentStore
```

`MainWindow` 仍是应用层协调者：它组织打开、导航、扫描、书库维护、搜索、批注编辑和导出用例，但格式解析、数据库访问、索引抽取、文件导出与 WebView2 阅读命令已经拆到独立服务中。

## 打开文档的数据流

```text
文件选择 / 拖放 / 命令行 / 书库双击
                    │
                    ▼
              DocumentLoader
                    │
     ┌──────────────┼────────────────────────────┐
     │              │                            │
   EPUB       FB2 / CBZ-CBR-CB7             MD / TXT / 直接文件
     │              │                            │
 EpubLoader     独立加载器             HtmlDocumentFactory / Direct
     │              │                            │
     └──────────────┴──────────────┬─────────────┘
                                   ▼
                              ReaderSession
                                   │
                   ┌───────────────┼────────────────┐
                   ▼               ▼                ▼
          LibraryDatabase    WebView2 导航    BookSearchIndexer
          书籍/位置/批注       + 阅读运行时       后台本地索引
```

`DocumentLoader.IsSupported` 是打开对话框与书库扫描共同使用的格式边界，避免书库列出应用无法打开的文件。

## 统一阅读模型

`ReaderSession` 向 UI 暴露：

- 原始文件路径、书名、作者和可选封面路径。
- `ReaderDocumentKind` 格式类型与内容根目录。
- 有序 `ReaderSection` 列表及当前章节索引。
- 可选的分层 `TocNode` 列表。
- 漫画会话的有序 `ComicPage` 列表。
- 是否支持重排版、页面搜索和宿主阅读脚本。

`TocNode` 保存标题、规范化后的本地章节路径、可选片段标识和子节点。UI 用 WPF `TreeView` 直接呈现层级；选择节点时先切换对应 spine 章节，再由 `ReaderViewController.GoToFragmentAsync` 定位章节内 `id` 或 `name`。

当 EPUB 没有可用 Nav/NCX 时，加载器用 spine 章节生成一级目录，阅读本身不会因目录损坏而失败。

## 阅读视图与控制器

`ReaderViewController` 是 .NET 与页面阅读 API 的窄边界，负责：

- 安装/更新 `ReaderRuntime` 与阅读设置。
- 获取页码和章内归一化进度。
- 执行章内翻页并报告首尾边界。
- 应用批注、跳到批注、显示搜索命中和清除选择。
- 跳到 EPUB 章节片段。
- 对脚本返回的 JSON 做容错解析。

`ReaderRuntime` 是宿主安装到已清理、可重排文档中的受控 CSS/JavaScript 层：

- 用阅读器颜色、字体、字号、行距和版心覆盖失控的出版社样式。
- 分页模式使用单列 CSS Columns；连续模式使用居中版心。
- 左右键先调用章内 `turnPage`，只有命中边界后才切换 spine 章节。
- 滚动或翻页后从视口内的可见正文生成位置锚点，并通过 `nogareader.location` 消息连同章内进度返回。
- 监听受控正文内的文字选择，构造文本锚点并通过 WebMessage 发给宿主。
- 根据保存的文本锚点重新挂载 `<mark>` 高亮，页面脚本本身不直接访问数据库。

PDF、图片和直接网页不安装这套文本阅读运行时。漫画安装独立的 `ComicReaderAssets` 运行时。归档漫画只访问文档专属缓存；图片文件夹漫画额外通过 `comic-content.nogareader.local` 只读虚拟主机请求原图，不复制原始图片。

## 阅读位置与导航

V0.7 有四种相关的定位信息：

1. `ReaderLocation`：保存章节序号、章内进度、全书近似进度和可选的视口文本锚点，用于恢复普通阅读位置。
2. `TocNode.Fragment`：来自 EPUB Nav/NCX 的章节内目标，用于目录跳转。
3. `TextAnchor`：阅读位置、高亮和笔记共用的原文级恢复结构。
4. 漫画页序号：直接复用 `ReaderLocation.SectionIndex`，单页、双页和连续模式之间切换时保持同一页。

在可重排内容中，`preciseLocation` 从视口上方或中央的可见文本获取最多 128 个字符及其上下文，数据库同时保存该锚点和归一化进度。重新打开时先按锚点恢复原文；锚点失效后才按进度恢复，并按照新页面尺寸吸附到最接近的一页。这个机制比只存滚动比例稳定，但不是 EPUB CFI，因此不能承诺正文大改后的字符级一致。

数据库写入采用约 350 ms 的防抖保存，窗口关闭和文档切换时也会持久化当前状态。`RecentStore` 保留原有 JSON 最近列表，SQLite `reader_locations` 是更完整的长期位置记录。

## 高亮、笔记与书签

### 选择与锚点

在受控可重排内容中，`ReaderRuntime` 把一次有效选择表示为 `TextAnchor`：

- 起止文本节点相对于 `<body>` 的子节点路径。
- 起止字符偏移。
- 完整选中文字（上限 4,096 个字符）。
- 最多 64 个字符的前文与后文。
- 选择发生时的章内进度。

重新打开章节时，运行时先验证 DOM 路径与偏移是否仍能得到相同原文；验证失败后，退回到全章原文匹配，并用前后文为重复候选评分。两种方式都失败时，该批注仍保留在数据库和批注列表中，但正文无法显示对应高亮。

### 保存与渲染

```text
用户选择文字
    │ WebMessage: nogareader.selection
    ▼
MainWindow + TextAnchor
    │
    ├─ 高亮 ───────────────┐
    └─ 笔记 + 颜色/正文 ───┤
                           ▼
                  LibraryDatabase.annotations
                           │
                           ▼
              ReaderViewController.ApplyAnnotations
                           │
                           ▼
               ReaderRuntime 重新挂载 mark
```

书签不要求文字选择，保存当前章节和章内进度。在批注面板双击书签、高亮或笔记，会先导航到章节，再按文本锚点或进度定位。

V0.7 沿用以下批注更新规则：

- 新建高亮默认黄色；高亮按钮的上下文菜单可直接选择黄、绿、蓝、粉。
- 已有高亮与笔记可通过批注菜单改为四种颜色之一；书签没有颜色语义。
- 只有 `AnnotationType.Note` 可打开 `NoteDialog` 编辑正文；对话框同时允许修改颜色。
- 编辑或改色会更新 `ModifiedUtc`，通过 `UpsertAnnotationAsync` 覆盖同一批注 ID，然后重新应用当前章节的标记。
- 删除会移除 SQLite 批注记录，并立即刷新当前页标记。

### 批注导出

`AnnotationExportService` 把当前书籍的全部书签、高亮和笔记导出为两种格式：

- Markdown：按章节和位置排序，包含书籍元数据、引用、笔记、颜色及创建时间，适合直接阅读。
- JSON：`schemaVersion = 1` 的显式 `AnnotationExportDocument`，使用稳定的小写 `bookmark`、`highlight`、`note` 类型，并保留可移植的 `exact/prefix/suffix` 文本选择器。

JSON DTO 刻意不输出数据库 ID、缓存章节路径和 DOM 子节点路径，避免把内部持久化结构变成公共协议；它仍包含用户可识别的书籍来源路径。V0.7 只提供导出，没有导入流程。

```text
LibraryDatabase.ListAnnotations
              │
              ▼
    AnnotationExportService
       │                 │
       ├─ Markdown       └─ schema v1 JSON
       │                 │
       └──── UTF-8 内容 ─┘
                    │
                    ▼
       同目录临时文件 + Flush
                    │
                    ▼
       File.Move(overwrite: true)
```

建议文件名会清理 Windows 非法字符、保留名称和过长标题。写入在目标目录创建唯一临时文件，成功刷新后才覆盖最终文件；失败或取消时尝试删除临时文件，避免把部分内容误当成完整导出。

## 本地书库

### 文件夹扫描

`LibraryScanner` 在后台枚举用户添加的目录。`LibraryScanOptions.IncludeSubfolders` 决定是否把子目录压入待扫描栈；每个 `LibraryFolder` 都能独立保存“包含子文件夹”或“仅当前文件夹”的范围。

扫描遵循以下边界：

- 只接收 `DocumentLoader` 已支持的扩展名。
- `IncludeSubfolders = false` 时只枚举根目录文件，`true` 时才递归普通子目录。
- 跳过隐藏、系统、符号链接和其他重解析点。
- 无权限或损坏的单个路径作为扫描问题记录，不中断整个目录。
- 默认最多接收 50,000 个候选文件，最多报告 500 个异常项。
- EPUB 元数据读取限制 container.xml、OPF、ZIP 条目数量和封面大小。
- FB2 元数据流式读取，并限制可读取文件与内嵌封面大小。

目前只有 EPUB/FB2 提取内嵌书名、作者和封面；其他已支持格式使用文件名、扩展名、文件大小和修改时间。封面按源文件规范路径的 SHA-256 摘要命名，写入 `Cache\library-covers`。

### 文件夹注册、缺失与重定位

`LibraryFoldersDialog` 先在内存副本上编辑设置，用户点击“应用”后才返回变更集：

- 可切换每个文件夹的递归范围。
- 显示上次扫描时间以及“路径正常/路径不存在”。
- 可标记移除或撤销移除。
- 移除只删除 `library_folders` 注册，不删除已发现的 `library_books`、位置或批注。

一次书库刷新按以下顺序工作：

```text
LibraryFolder 注册 + IncludeSubfolders
                  │
                  ▼
           LibraryScanner
                  │
       元数据/封面/规范路径候选
                  │
                  ├─ 正常路径 ───────> UpsertBook
                  │
                  ├─ 旧路径失效且唯一匹配
                  │       └──────────> 自动重定位原 book ID
                  │
                  └─ 扫描结束 ───────> File.Exists 全库复核
                                             │
                                             └─ 更新 IsMissing
```

自动重定位只考虑旧路径已失效、且旧路径仍属于本次注册扫描范围的记录；新候选按格式、文件大小、书名和作者得到唯一的合格旧记录时才复用其 ID。匹配不唯一、关键特征不符或旧路径已不在扫描范围时不猜测，旧记录继续显示“文件缺失”，新文件按普通候选处理。

用户也可以从书籍菜单手动选择新文件。扩展名必须与原记录一致；大小变化会再次确认。如果新路径已经属于另一条书库记录，界面必须经用户确认后才合并：原书的阅读位置继续保留，双方批注合并到最终书籍记录。重定位和合并复用稳定的书籍关联，不修改或移动源文件。

“从书库移除”是另一条显式流程：确认框提示源文件不会删除，但 `library_books` 记录及其阅读位置、书签、笔记和全文索引将通过外键级联删除。仍位于已注册扫描目录中的源文件，后续扫描可能作为新书重新加入。

### 数据库

`LibraryDatabase` 使用 `Microsoft.Data.Sqlite`，数据库默认为 `Data\library.db`。每次操作使用短生命周期连接，通过共享缓存、连接池、5 秒 busy timeout 和 WAL 协调后台索引、位置保存与界面查询。

当前 schema 版本为 1，使用 `PRAGMA user_version` 管理迁移：

| 表 | 用途 |
|---|---|
| `library_books` | 路径、标题、作者、格式、封面、`IsMissing` 和最近打开时间 |
| `library_folders` | 扫描根目录、`IncludeSubfolders` 与最近扫描时间 |
| `reader_locations` | 每本书的章节、片段、进度和可选文本锚点 |
| `annotations` | 书签、高亮、笔记、颜色、选中文字和锚点 |
| `search_sections` | 每章提取后的纯文本正文 |
| `search_sections_fts` | 可选 FTS5 外部内容索引，由触发器维护 |

书籍和书库文件夹按大小写不敏感的规范路径键去重。普通重定位保持原 `library_books.id`，因此 `reader_locations`、`annotations` 和 `search_sections` 的外键关系不变；目标路径冲突时只能进入经用户确认的合并流程，不能静默覆盖另一条记录。合并保留原书阅读位置并合并双方批注。

`library_folders` 是扫描范围注册，不与 `library_books` 建立所有权外键，所以移除文件夹注册不会级联删除书籍。相反，删除 `library_books` 时，外键会级联删除它的位置、批注和索引；数据库操作始终不删除源文件。

## 全书搜索

`BookSearchIndexer` 在成功打开一本书后异步建立索引：

1. 按 `ReaderSession.Sections` 顺序读取本地 HTML/XML 或纯文本章节。
2. 对标记文档移除 `head`、脚本、样式、注释和标签，再进行 HTML 实体解码与空白归一化。
3. 损坏或不可读取的单章被跳过，其余章节继续建立索引。
4. 结果在一个事务中替换该书旧的 `search_sections`。
5. 查询先用 FTS5 `unicode61` 和 BM25 选出相关章节；FTS 在这一层只负责候选召回，不把“一章”误当成“一处结果”。
6. 对每个候选章节的正文和标题执行大小写不敏感的精确子串扫描，把每个非重叠 occurrence 展开成独立 `SearchHit`。
7. 每个命中记录 UTF-16 起点、长度、章内 occurrence 序号、原始大小写文本、标题/正文标志和近似章内进度，并生成带 `‹ ›` 标记的上下文片段。
8. 转义后的 `LIKE` 查询负责无 FTS 环境和补充召回；FTS 与 LIKE 结果按书籍、章节、字段和偏移去重合并。
9. UI 双击正文结果后切换章节，把查询和 occurrence 序号交给 `ReaderRuntime.revealText`；运行时逐次查找并标记对应的第二处或之后文本。标题命中只跳到对应章节，不伪装成正文偏移。

索引资源上限：

- 最多 10,000 个章节。
- 每章最多读取 8 MB。
- 单本书最多读取 64 MB。
- 查询字符串最多 512 个字符。
- 服务单次最多返回 200 个 occurrence；超过上限的后续命中不会进入本次结果集。

当前索引不读取 PDF、图片或漫画页文本，也不执行 OCR。直接 HTML 因安全原因不启用阅读运行时，因此也不提供相同的结果跳转体验。

## 格式处理

### Markdown 与 TXT

Markdown 与 TXT 不共用简单 `<pre>` 路径：

1. Markdig 在 .NET 侧解析 CommonMark 和常用扩展。
2. 原始 HTML 关闭，危险协议链接被改写。
3. 相对图片只允许来自 Markdown 源文件目录，并限制扩展名、单文件大小和累计大小。
4. 实际引用图片按内容哈希复制到文档缓存；远程图片继续被网络边界阻止。
5. 生成页使用严格 CSP，再进入统一阅读运行时。

纯文本在 .NET 侧进行 HTML 转义和换行处理，再进入同一个受控运行时。

### EPUB

1. 根据源路径、大小和修改时间计算缓存键。
2. 安全解压 EPUB ZIP。
3. 从 `META-INF/container.xml` 找到 OPF。
4. 从 OPF manifest 与 spine 生成阅读顺序，并读取书名、作者和封面。
5. 优先解析 EPUB 3 Nav，退回 EPUB 2 NCX，递归生成 `TocNode`。
6. 目录目标必须解析到文档缓存内部；片段与本地路径分开保存。
7. 在缓存副本中移除主动内容；源 EPUB 不修改。
8. WebView2 通过虚拟主机读取缓存内相对 CSS、字体和图片。

### FB2、漫画归档/图片文件夹与直接文件

- FB2 使用禁用外部实体的 XML 读取器生成受控 HTML 章节，并处理常见内嵌图片。
- CBZ、CBR/RAR 和 CB7/7Z 由 SharpCompress 只读打开；`ComicArchiveExtractor` 过滤图片、验证路径与资源上限，再按自然文件名顺序提取。
- `ComicArchiveLoader` 生成只读清单和每页入口 shell；`ComicReaderController` 通过窄 WebView2 API 控制单/双/连续布局、RTL/LTR、适应、缩放和跳页。
- `ComicFolderLoader` 递归过滤非隐藏、非系统、非重解析点目录中的图片，使用相对路径/大小/修改时间签名检测页列表变更。
- 连续模式由视口中心页回传阅读位置；其他模式按页组跳转。前后三页使用 `Image` 对象预加载。
- WPF 缩略图列表使用 `VirtualizingStackPanel` 的回收模式，缩略图 LRU 强缓存最多 96 项；漫画不再在左侧普通目录树中建立第二份页列表。
- PDF 使用 WebView2 内置查看器。
- HTML/XHTML/MHT/MHTML/XML 直接显示，但禁用 JavaScript 和外部资源。
- 图片直接显示，不进入正文索引或文本批注链路。

## 安全边界

### 压缩文件

- 解压路径必须保持在文档专属缓存目录中。
- 不接受 ZIP 内符号链接。
- 阅读解包最多 10,000 个条目。
- 单条目上限 192 MB。
- 声明和实际解压总量上限 768 MB。
- 书库元数据扫描使用更小的专用读取上限，不会为取封面解压整本书。
- 漫画最多 20,000 个归档条目，单张图片最大 256 MB，总提取量最大 4 GB；密码条目、符号链接、路径穿越和异常压缩比会直接被拒绝。
- 漫画图片文件夹最多扫描 100,000 个文件、接受 20,000 张图片；根目录和子目录的符号链接/重解析点不会被跟随。

### XML

- OPF、container.xml、Nav/NCX 与 FB2 禁止 DTD/外部实体。
- EPUB 内容清理时允许忽略 DOCTYPE，但 `XmlResolver` 始终为空。

### Web 内容

- EPUB 移除 `script`、`iframe`、`object`、`embed`、`base`、meta refresh、事件属性和 `javascript:` URL。
- 原始 HTML/MHT 禁用脚本。
- WebView2 页面权限统一拒绝，页面发起的下载统一取消。
- 非 `book.nogareader.local` 的 HTTP/HTTPS 子资源返回 403。
- 非用户发起的外部导航被取消；用户主动点击的安全外链才交给系统浏览器。
- 页面运行时只能通过限定的 WebMessage 类型传递位置和选择数据，不能直接读写本地数据库。

### 本地数据库与隐私

- 数据库参数全部使用绑定参数，路径键在 .NET 侧规范化。
- 批注锚点用 `System.Text.Json` 序列化，反序列化失败不会执行其中内容。
- FTS 查询经过分词与转义构造；LIKE 回退会转义通配字符，查询长度和结果数量均有上限。
- 自动重定位只接受“旧路径失效且仍在注册范围 + 格式/大小/书名/作者唯一匹配”；不唯一时保留缺失状态，不静默合并。
- 手动重定位要求同扩展名，大小变化需要确认；目标已有记录时，只有用户确认后才合并位置与批注。
- 移除扫描文件夹不会删除书籍；移除书籍前明确提示 SQLite 级联范围，源文件从不随数据库记录删除。
- SQLite、缓存、索引和批注都位于 NoGaReader 数据根目录。
- 应用没有账户、遥测、云同步或上传接口。

### 批注导出

- 保存位置由用户通过系统对话框明确选择，覆盖已有文件时由对话框再次提示。
- 建议文件名清理非法字符、Windows 保留名称和过长标题，不能把书名解释为目录路径。
- Markdown 特殊字符和引用内容被转义；JSON 通过显式 DTO 序列化，不输出数据库 ID、缓存路径或 DOM 节点路径。
- 最终文件只在同目录临时文件完整写入并刷新后替换，失败或取消会清理临时文件。
- Markdown 与 JSON 都包含原书的本地来源路径。文件仍由用户控制，但分享前应检查这项可能暴露目录结构的信息。

## 数据目录与发布形态

数据根目录按以下优先级解析：

1. 绝对路径环境变量 `NOGAREADER_DATA_DIR`。
2. 发布目录存在 `portable.mode` 时，使用程序旁的 `UserData`。
3. 从源码仓库运行时，使用仓库根目录 `.local-data`。
4. 其他情况使用 `%LOCALAPPDATA%\NoGaReader`。

子目录：

```text
<DataRoot>
├─ Data
│  ├─ library.db
│  ├─ settings.json
│  └─ recent.json
├─ Cache
│  ├─ library-covers
│  └─ 文档专属缓存
└─ WebView2
```

运行 `scripts\publish-portable.ps1` 会生成 framework-dependent 的 Windows x64 目录：

```text
artifacts\publish\win-x64\NoGaReader.exe
```

该路径是 V0.7.0 便携版入口。目标机器需要 .NET 8 Desktop Runtime 和 WebView2 Runtime。

批注导出不属于应用数据树；用户可在保存对话框中选择任意可写目录。导出失败不会回写或删除 SQLite 中的原批注。

## 自动验证

逻辑与持久化冒烟测试通过 `tests\NoGaReader.Smoke` 运行；桌面交互由仓库内固定的 `scripts\ui-smoke.ps1` 覆盖。UI 脚本要求调用者显式传入可执行文件和一个真实阅读夹具（文件或漫画图片文件夹），例如：

```powershell
.\scripts\ui-smoke.ps1 `
    -ExecutablePath .\artifacts\publish\win-x64\NoGaReader.exe `
    -FixturePath .\README.md
```

脚本使用 Windows UI Automation：

- 解析并验证两个输入文件，确认实际启动的进程路径就是指定可执行文件。
- 为每次运行创建 `.tmp\ui-smoke\<guid>`，通过 `NOGAREADER_DATA_DIR` 隔离书库、SQLite 和 WebView2 状态。
- 等待主窗口，检查打开文件、书库文件夹管理、高亮、笔记、书签和批注等关键控件。
- 实际打开和关闭批注面板，验证批注列表可交互。
- 默认只关闭脚本持有的精确进程，并且只允许递归清理由仓库 `.tmp\ui-smoke` 下解析出的本次目录。
- `-KeepOpen` 用于人工接续检查，`-TimeoutSeconds` 接受 5–300 秒。

脚本需要交互式 Windows 桌面和可用的 WebView2 Runtime；它是 UI 结构与基本动作的冒烟覆盖，不代替对 EPUB 排版、全文搜索 occurrence 和文件对话框的专项人工验收。

## 已知限制与后续模块

### 已知限制

- 普通阅读位置不是 EPUB CFI；跨排版恢复是近似定位。
- 文本锚点依赖清理后 DOM 和原文上下文，正文大改后可能无法挂载。
- 高亮/笔记仅覆盖受控可重排内容，尚无 PDF 专用批注层。
- 全书索引不解析 PDF、图片或漫画文字，没有 OCR。
- occurrence 定位依赖索引纯文本与阅读 DOM 的文本顺序一致；出版社样式造成的特殊空白差异可能使正文标记退回到近似章节进度。一次查询最多返回 200 个非重叠命中。
- 书库仅支持列表、封面与基础筛选，没有标签、分组和手工元数据编辑。
- 缺失状态在书库扫描后更新，没有常驻文件系统监视器。自动重定位依赖格式、大小、书名和作者的唯一匹配，不做内容哈希身份验证。
- 手动重定位会检查格式并提示大小差异，但选择内容相近却错误的文件仍可能让旧锚点定位错误；目标记录合并前需要用户判断。
- 移除扫描文件夹会保留已发现书籍；反之，从书库移除但仍位于已注册目录的源文件，后续扫描可能作为新记录再次加入。
- 批注只有 Markdown/JSON 导出，没有导入；笔记正文和颜色可编辑，引用原文及锚点不能手工改写。
- MOBI/AZW/AZW3 与批量转换依赖本地 Calibre `ebook-convert`；未放置引擎时给出明确提示。仍不支持 DJVU 和 XPS。

### 转换子系统（V0.8）

未来可定义 `IFormatConverter`：

```text
CanConvert(sourceExtension)
ConvertAsync(source, target, options, progress, cancellationToken)
```

V0.8 已实现 `IFormatConverter` / `CalibreConverter`：探测安装包内嵌、用户数据目录、系统安装与 PATH；批量转换免费无限次；Kindle 格式经缓存 EPUB 后复用 `EpubLoader`。完整 Calibre 运行时按发布策略放入 `engines/calibre`，界面展示引擎来源与版本。

### 发布与产品化

- MSIX 安装包与文件关联。
- 应用图标、签名和更新通道。
- 书库标签、分组、手工元数据与封面管理。
- PDF 专用批注层与可选 OCR/索引适配器。
- EPUB CFI 或更稳定的位置映射。
- 许可证与第三方声明。
