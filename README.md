<h1 align="center">Typedown · Uno Platform Edition</h1>

<p align="center">A <b>WYSIWYG</b> Markdown editor for <b>Linux · Windows · macOS</b> — type Markdown, watch it render in place, no split preview pane.</p>

<p align="center">
  <a href="https://github.com/flintt/Typedown-Uno/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/flintt/Typedown-Uno?label=release"></a>
  <a href="https://github.com/flintt/Typedown-Uno/releases"><img alt="Downloads" src="https://img.shields.io/github/downloads/flintt/Typedown-Uno/total?label=downloads"></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/github/license/flintt/Typedown-Uno?label=license"></a>
</p>

<p align="center"><b>English</b> · <a href="README.zh-Hans.md">简体中文</a></p>

<p align="center"><img src="docs/media/wysiwyg.png" width="820" alt="Typedown editing a Markdown document"></p>

The same C# + XAML codebase (Uno Platform, Skia desktop) runs on **Linux, Windows and macOS**, driving the exact editor kernel (React + Muya) of the original [Typedown](https://github.com/flintt/Typedown). What you type is a plain `.md` file; what you see is the rendered result, inline.

---

## Showcase

**Switch between WYSIWYG, source and reading modes — same document, one keystroke:**

<p align="center"><img src="docs/media/demo-modes.gif" width="760" alt="Switching between WYSIWYG, source code and reading modes"></p>

**Jump around with the live outline:**

<p align="center"><img src="docs/media/demo-outline.gif" width="760" alt="Navigating a document through the outline pane"></p>

|  |  |
|:--:|:--:|
| **Source mode** — the raw Markdown, with line numbers | **Reading mode** — distraction-free, read-only |
| <img src="docs/media/source-mode.png" alt="Source code mode"> | <img src="docs/media/reading-mode.png" alt="Reading mode"> |
| **Diagrams & outline** — Mermaid, tables, math | **Dark theme** — follow system, light, dark or black |
| <img src="docs/media/diagram.png" alt="Mermaid diagram with outline"> | <img src="docs/media/dark-theme.png" alt="Dark theme"> |
| **Folder-wide search** — across files and names | **Settings** — everything in one dialog |
| <img src="docs/media/folder-search.png" alt="Folder-wide search"> | <img src="docs/media/settings.png" alt="Settings dialog"> |

---

## Features

- **WYSIWYG Markdown** — tables, math ($\LaTeX$), fenced code with syntax highlighting, diagrams (Mermaid / flowchart / sequence / Vega-Lite / PlantUML), task lists, footnotes — plus **source-code**, **focus**, **typewriter** and **reading** modes.
- **Tabs & windows** — `Ctrl+Shift+N` opens an independent window; `Ctrl+N` a tab. Single-click previews from the file tree, double-click pins; `Ctrl+Tab` cycles; each dirty tab is asked about on close; the last session is restored at startup.
- **Never lose work** — atomic saves, a **crash backup** of unsaved edits offered for recovery on the next open (even with auto-save off or before the file has a name), external-change detection, optional auto-save, and per-file cursor/scroll memory.
- **Sidebar** — file tree, clickable outline (with current-heading follow), and folder-wide full-text search (filenames included).
- **Find** — `Ctrl+F` in-page find with highlight and previous/next.
- **Images** — insert a local file, paste a screenshot, or drop an image; each is copied next to the document (`${filename}.assets`) and linked relatively, per your settings.
- **Export & share** — export HTML, print / export PDF, and one-click **share to HedgeDoc** (anonymous or with login; an unchanged document reuses its previous link instead of creating a new one).
- **Themes & languages** — follow system / light / dark / black, plus your own CSS themes; UI in English, 简体中文 and more.
- **Fully rebindable shortcuts** — Settings → Shortcuts.
- **Local automation** (Linux, off by default) — turn on *Allow local automation* in Settings → General, and scripts and AI assistants can read and edit open documents, switch a window's mode and side pane, and change settings. The deb and tar.gz packages carry the `typedownctl` CLI; `typedownctl mcp` is an MCP server for AI tools. What it can do and its security boundary: [local automation](docs/automation.md) (in Chinese) and [MCP](docs/automation-mcp.md).

---

## Download

Grab the package for your platform from the **[latest release](https://github.com/flintt/Typedown-Uno/releases/latest)**. The .NET runtime is bundled — nothing else to install.

| Platform | File | How to run |
|---|---|---|
| Debian / Ubuntu x64 | `typedown_*_amd64.deb` | `sudo dpkg -i typedown_*.deb` (pulls `libgtk-3-0`, `libwebkit2gtk-4.1-0`) |
| Any Linux x64 | `Typedown-*-x86_64.AppImage` | `chmod +x` then run (same two system libraries) |
| Linux x64 portable | `Typedown-linux-x64-*.tar.gz` | extract, then `./Typedown.Uno` |
| Windows 10/11 x64 | `Typedown-win-x64.zip` | extract and run; needs the WebView2 runtime (built into Win11) |
| macOS (Apple Silicon) | `Typedown-osx-arm64.tar.gz` | extract, move `Typedown.app` to Applications, first run → right-click → Open (unsigned); it is listed under Open With for `.md` files |

On Windows the native **WinUI** build is the better experience: **[flintt/Typedown](https://github.com/flintt/Typedown/releases/latest)**.

---

## Running

Runtime dependencies (everything else is in the package):

| Platform | System components |
|---|---|
| Linux x64 | `libgtk-3-0`, `libwebkit2gtk-4.1-0`, `libx11-6` (Ubuntu 22.04+ / Debian 12+) |
| Windows 10/11 x64 | WebView2 runtime (bundled with Win11; usually present via Edge on Win10) |
| macOS (Apple Silicon) | none |

Settings, session and the run log (`debug.log`) live in `~/.local/share/Typedown.Uno/` (Windows: `%LOCALAPPDATA%\Typedown.Uno\`, macOS: `~/Library/Application Support/Typedown.Uno/`).

### Linux notes

Uno's GTK web view P/Invokes **unversioned** library names (`libgdk-3.so`, `libsoup-3.0.so`, `libwebkit2gtk-4.1.so`), which ship only with the `-dev` packages; without them the app opens and the menus work but **the editor stays blank**. At startup Typedown installs a name resolver that maps to the versioned `.so.0`, so you do **not** need the `-dev` packages or manual symlinks. On Wayland the web view needs X11, and the launcher already sets `GDK_BACKEND=x11`.

---

## Themes

Drop a CSS file into the theme folder, or start with the bundled offline designer under View → Theme — see
**[Custom themes](docs/custom-theme.md)**.

## Development

```bash
export PATH=$HOME/.dotnet:$PATH          # .NET 9 SDK
dotnet build   Typedown.Uno/Typedown.Uno.csproj -f net9.0-desktop
dotnet run     --project Typedown.Uno/Typedown.Uno.csproj -f net9.0-desktop -- some.md
dotnet publish Typedown.Uno/Typedown.Uno.csproj -f net9.0-desktop -c Release -r linux-x64 --self-contained -o out
```

Layout is in `PORT_PLAN.md`. The editor build lives in `Typedown.Uno/Assets/Editor/` (from the original repo's `Statics`, plus the `uno-bridge.js` host bridge); when updating it, copy the build across and keep `uno-bridge.js` and the reference to it in `index.html`.

## Known limitations

- **Built-in file picker on Linux** — Uno's native picker needs an XDG desktop portal that many desktops lack, so a self-drawn picker is used instead; Windows / macOS use the native dialogs.
- Print, PDF and picture export go through a second, offscreen web view, since Uno's web view exposes no print API: WebKitGTK on Linux, and on macOS `typedown-webkit-export`, a WKWebView helper inside Typedown.app (a plain-folder macOS build has no helper and says PDF export is unavailable).
- Image upload (to a picture host) is not implemented.
- Each window has its own document, tabs and sidebar; session restore applies to the first window at startup only.
- On Linux the editor is a native WebKit window, so it cannot animate under/over XAML controls; dialogs overlay it.

## Reporting issues

Open an [issue](https://github.com/flintt/Typedown-Uno/issues/new/choose) — first paste the version block from **Help → About → Copy info**. Issues in this repository are triaged and fixed with the help of AI (Claude Code).

## License

MIT, following upstream [byxiaozhi/Typedown](https://github.com/byxiaozhi/Typedown) (see [LICENSE](LICENSE)). The
editor kernel is Muya from [MarkText](https://github.com/marktext/marktext) (also MIT). See the
[third-party notices](THIRD-PARTY-NOTICES.md) and [privacy statement](docs/privacy.md).
