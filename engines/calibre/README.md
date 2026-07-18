# 内嵌 Calibre 运行时目录

将 Calibre 的 `ebook-convert.exe` 及其依赖放在此目录，发布时会复制到：

`NoGaReader/engines/calibre/ebook-convert.exe`

也可放在用户数据目录：

`%LOCALAPPDATA%/NoGaReader/engines/calibre/ebook-convert.exe`

或在应用“转换”窗口中手动指定路径。

当前仓库不捆绑完整 Calibre 二进制（体积与 GPL 分发策略另行处理）；运行时探测顺序：

1. 用户设置中的路径
2. 安装目录 `engines/calibre`
3. 用户数据 `engines/calibre`
4. 系统安装 / PATH
