<h1 align="center">Typedown · Uno Platform 版</h1>

<p align="center"><b>所见即所得</b>的 Markdown 编辑器，跨 <b>Linux · Windows · macOS</b> —— 输入 Markdown，原地渲染，没有分屏预览。</p>

<p align="center">
  <a href="https://github.com/flintt/Typedown-Uno/releases/latest"><img alt="最新版本" src="https://img.shields.io/github/v/release/flintt/Typedown-Uno?label=%E6%9C%80%E6%96%B0%E7%89%88%E6%9C%AC"></a>
  <a href="https://github.com/flintt/Typedown-Uno/releases"><img alt="下载量" src="https://img.shields.io/github/downloads/flintt/Typedown-Uno/total?label=%E4%B8%8B%E8%BD%BD%E9%87%8F"></a>
  <a href="LICENSE"><img alt="许可证" src="https://img.shields.io/github/license/flintt/Typedown-Uno?label=%E8%AE%B8%E5%8F%AF%E8%AF%81"></a>
</p>

<p align="center"><a href="README.md">English</a> · <b>简体中文</b></p>

<p align="center"><img src="docs/media/wysiwyg.png" width="820" alt="Typedown 编辑 Markdown 文档"></p>

同一套 C# + XAML（Uno Platform，Skia 桌面端）在 **Linux / Windows / macOS** 上运行，编辑内核（React + Muya）与原版 [Typedown](https://github.com/flintt/Typedown) 完全相同。你输入的是纯 `.md` 文件，看到的是原地渲染后的结果。

---

## 功能一览

**所见即所得 / 源代码 / 阅读模式，同一文档，一个快捷键切换：**

<p align="center"><img src="docs/media/demo-modes.gif" width="760" alt="在所见即所得、源代码、阅读模式之间切换"></p>

**用实时大纲在文档里跳转：**

<p align="center"><img src="docs/media/demo-outline.gif" width="760" alt="通过大纲面板在文档中导航"></p>

|  |  |
|:--:|:--:|
| **源代码模式** —— 原始 Markdown，带行号 | **阅读模式** —— 无干扰、只读 |
| <img src="docs/media/source-mode.png" alt="源代码模式"> | <img src="docs/media/reading-mode.png" alt="阅读模式"> |
| **图表与大纲** —— Mermaid、表格、公式 | **深色主题** —— 跟随系统 / 浅 / 深 / 纯黑 |
| <img src="docs/media/diagram.png" alt="Mermaid 图表与大纲"> | <img src="docs/media/dark-theme.png" alt="深色主题"> |
| **文件夹全文搜索** —— 含文件名 | **设置** —— 一个对话框搞定 |
| <img src="docs/media/folder-search.png" alt="文件夹全文搜索"> | <img src="docs/media/settings.png" alt="设置对话框"> |

---

## 功能

- **所见即所得**：表格、公式（$\LaTeX$）、带语法高亮的代码块、图表（Mermaid / 流程图 / 时序图 / Vega-Lite / PlantUML）、任务列表、脚注；另有**源代码**、**专注**、**打字机**、**阅读**模式。
- **多标签 + 多窗口**：`Ctrl+Shift+N` 开独立窗口，`Ctrl+N` 开标签；文件树单击预览、双击固定；`Ctrl+Tab` 切换；关闭时逐个询问保存；启动恢复上次会话。
- **不丢内容**：原子写入；未保存的编辑会做**崩溃备份**，下次打开时提示恢复（即使关了自动保存、或文件还没命名）；外部修改检测；可选自动保存；记住每个文件的光标与滚动位置。
- **侧边栏**：文件树、可点击的大纲（高亮跟随当前标题）、文件夹全文搜索（含文件名）。
- **查找**：`Ctrl+F` 页内查找，高亮 + 上一个/下一个。
- **图片**：插入本地图片、粘贴截图、拖入图片——按设置复制到文档旁的 `${filename}.assets` 并写成相对路径。
- **导出与分享**：导出 HTML、打印/导出 PDF，一键**分享到 HedgeDoc**（匿名或登录；内容未变时复用上次的链接，而不是重新生成）。
- **主题与语言**：跟随系统 / 浅色 / 深色 / 纯黑，也支持自定义 CSS 主题；界面语言：English、简体中文等。
- **快捷键全可重绑**：设置 → 快捷键。
- **本机自动化**（Linux，默认关闭）：在 设置 → 通用 里打开“允许本机自动化”后，脚本和 AI 助手可以读写已打开的文档、切换窗口的模式和侧栏、调整设置；deb 和 tar.gz 包带命令行工具 `typedownctl`，`typedownctl mcp` 是给 AI 工具用的 MCP 服务器。接口、能做的事和安全边界见 [本机自动化](docs/automation.md) 和 [MCP](docs/automation-mcp.md)。

---

## 下载

去 **[最新发布](https://github.com/flintt/Typedown-Uno/releases/latest)** 拿对应平台的包。.NET 运行时已打进包里，不需要另外安装。

| 平台 | 下载 | 怎么用 |
|---|---|---|
| Debian / Ubuntu x64 | `typedown_*_amd64.deb` | `sudo dpkg -i typedown_*.deb`，自动拉 `libgtk-3-0`、`libwebkit2gtk-4.1-0` |
| 任意 Linux x64 | `Typedown-*-x86_64.AppImage` | `chmod +x` 后运行（同样需要上面两个系统库） |
| Linux x64 便携版 | `Typedown-linux-x64-*.tar.gz` | 解压后 `./Typedown.Uno` |
| Windows 10/11 x64 | `Typedown-win-x64.zip` | 解压即用；需要 WebView2 运行时（Win11 自带） |
| macOS（Apple Silicon） | `Typedown-osx-arm64.tar.gz` | 解压后把 `Typedown.app` 拖进“应用程序”，首次运行右键 → 打开（未签名）；`.md` 文件的“打开方式”里会列出它 |

Windows 上原生 **WinUI** 版体验更好：**[flintt/Typedown](https://github.com/flintt/Typedown/releases/latest)**。

---

## 运行

运行时依赖（其余都在包里）：

| 平台 | 需要的系统组件 |
|---|---|
| Linux x64 | `libgtk-3-0`、`libwebkit2gtk-4.1-0`、`libx11-6`（Ubuntu 22.04+ / Debian 12+） |
| Windows 10/11 x64 | WebView2 运行时（Win11 自带，Win10 多随 Edge 安装） |
| macOS（Apple Silicon） | 无 |

设置、会话和运行日志（`debug.log`）保存在 `~/.local/share/Typedown.Uno/`（Windows：`%LOCALAPPDATA%\Typedown.Uno\`，macOS：`~/Library/Application Support/Typedown.Uno/`）。

### Linux 说明

Uno 的 GTK 网页视图按**不带版本号**的库名（`libgdk-3.so`、`libsoup-3.0.so`、`libwebkit2gtk-4.1.so`）做 P/Invoke，而这些符号链接只随 `-dev` 包安装；缺了它们，程序能开、菜单能点，但**编辑区一直空白**。本程序启动时会装一个库名解析器，自动映射到带版本号的 `.so.0`，所以**不需要装 `-dev` 包、也不用建符号链接**。Wayland 桌面下网页视图需要 X11，启动脚本已设好 `GDK_BACKEND=x11`。

---

## 主题

把一个 CSS 文件放进主题文件夹即可，也可以从“视图 → 主题”打开随应用打包的离线配色工具，格式见
**[自定义主题](docs/custom-theme.md)**。

## 开发

```bash
export PATH=$HOME/.dotnet:$PATH          # .NET 9 SDK
dotnet build   Typedown.Uno/Typedown.Uno.csproj -f net9.0-desktop
dotnet run     --project Typedown.Uno/Typedown.Uno.csproj -f net9.0-desktop -- some.md
dotnet publish Typedown.Uno/Typedown.Uno.csproj -f net9.0-desktop -c Release -r linux-x64 --self-contained -o out
```

结构见 `PORT_PLAN.md`。编辑器构建产物在 `Typedown.Uno/Assets/Editor/`（来自原仓库 `Statics`，加上 `uno-bridge.js` 桥接脚本）；更新编辑器时把构建产物复制过来，并保留 `uno-bridge.js` 及 `index.html` 里对它的引用。

## 已知限制

- **Linux 上使用内置文件选择器**：Uno 的系统选择器依赖 XDG desktop portal，很多桌面装不全，所以一律用自绘选择器；Windows / macOS 仍用系统原生对话框。
- 打印、导出 PDF 和导出图片都通过另一个后台网页视图完成（Uno 的网页视图没有打印接口）：Linux 上用 WebKitGTK，macOS 上用 Typedown.app 里的 `typedown-webkit-export`（基于 WKWebView 的辅助程序；不打包成 .app 的 macOS 文件夹版没有它，会提示无法导出 PDF）。
- 图床上传未实现。
- 每个窗口有独立的文档、标签和侧栏；会话恢复只作用于启动时的第一个窗口。
- Linux 上编辑器是原生 WebKit 窗口，无法与 XAML 控件重叠动画；对话框会覆盖在它上面。

## 反馈问题

开 [issue](https://github.com/flintt/Typedown-Uno/issues/new/choose)——先在 帮助 → 关于 里点「复制信息」，把版本块贴进来。本仓库的 issue 由 AI（Claude Code）分析和修复。

## 许可证

MIT，沿用上游 [byxiaozhi/Typedown](https://github.com/byxiaozhi/Typedown)（见 [LICENSE](LICENSE)）。编辑内核
来自 [MarkText](https://github.com/marktext/marktext) 的 Muya（同为 MIT）。另见[第三方组件声明](THIRD-PARTY-NOTICES.md)
和[隐私说明](docs/privacy.md)。
