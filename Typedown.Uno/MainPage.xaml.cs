using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Typedown.Uno.Services;
using Typedown.Uno.ViewModels;
using Typedown.Uno.Views;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;

namespace Typedown.Uno;

public sealed partial class MainPage : Page, DocumentViewModel.IHostUi
{
    private const string EditorHost = "typedown.editor";

    /// <summary>What a freshly created window should open.</summary>
    /// <param name="MoreFiles">Further files opened together with <paramref name="File"/> (several selected in Finder), each in a tab.</param>
    public sealed record StartupOptions(string? File, bool RestoreSession, string Marker, IReadOnlyList<string>? MoreFiles = null);

    private StartupOptions options = new(null, false, "");
    private Window? window;
    private IntPtr nativeWindow;

    private DispatcherTimer? activationWatch;
    private bool watchedActive;
    // A window starts with the keyboard in its editor.
    private bool editorHadKeyboard = true;
    private int refocusTicks;
    // Dialogs up in this window: the keyboard is theirs, and the editor is not given it back under them.
    private int openDialogs;

    /// <summary>
    /// Gives the keyboard back to the editor when this window becomes active again, if the editor had it when the
    /// window was left. Uno raises no Activated when the window manager activates the window (another window closed,
    /// the reader switched), and the window manager gives the keyboard to the app's window, never to the web view's:
    /// keys went nowhere until a click. So the window manager's active window is watched, and while this window is
    /// active, whether the keyboard is the editor's (a field of the page keeps it: nothing is taken from there).
    /// The same tick puts the editor back when another window has taken it out (see NativeHosts).
    /// </summary>
    private void WatchActivation()
    {
        if (!OperatingSystem.IsLinux() || activationWatch != null) return;
        activationWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        activationWatch.Tick += (_, _) =>
        {
            var own = NativeWindow();
            if (own == IntPtr.Zero) return;
            Services.NativeHosts.Repair(EditorView);
            var active = Services.X11Window.ActiveWindow() == own;
            var editor = Services.WebViewCaret.WindowOf(EditorView);
            if (active && !watchedActive)
            {
                Services.WebViewCaret.SetActive(EditorView, true);
                // The window manager sets the focus on the activated window itself, possibly after this: the hand-over
                // is repeated on the next ticks while the keyboard is not the editor's.
                refocusTicks = editorHadKeyboard ? 3 : 0;
            }
            if (openDialogs > 0)
                refocusTicks = 0;
            else if (active && refocusTicks > 0)
            {
                refocusTicks--;
                if (editor == 0 || (ulong)Services.X11Window.InputFocus() != editor) Services.WebViewCaret.Focus(EditorView);
            }
            else if (active && editor != 0)
                editorHadKeyboard = (ulong)Services.X11Window.InputFocus() == editor;
            watchedActive = active;
        };
        activationWatch.Start();
        if (window != null) window.Closed += (_, _) => activationWatch?.Stop();
    }
    private bool nativeIconApplied;
    private DataPackage? clipboardBatch;
    private DateTime clipboardBatchTime;

    private readonly AppSettings settings = AppSettings.Current;
    private EditorTransport? transport;
    private DocumentViewModel? document;
    private TabsViewModel? tabs;
    private string? startupFile;
    private string? workFolder;
    private FolderItem? folderRoot;
    private CancellationTokenSource? pendingSingleClick;
    private CancellationTokenSource? searchCts;
    private bool suppressTabSelection;
    private bool closing;

    // Local automation: this window as the API sees it, and what its title says about connected programs.
    private Automation.AutomationWindow? automationWindow;
    private readonly TaskCompletionSource automationStartup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Set once the startup documents are in the tabs: the editor's first GetSettings waits for it (see OnLoaded).
    private readonly TaskCompletionSource documentsStaged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool automationConnected;
    private string? automationNotice;

    public MainPage()
    {
        Loc.Apply(settings.Language);
        this.InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is StartupOptions startup) options = startup;
        startupFile = options.File;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The document model is built first and never depends on the web view: if the editor fails to come up
        // (missing WebKitGTK, a stalled native initialization) the shell must still be usable and say why.
        window = App.WindowFor(XamlRoot) ?? App.MainWindow;
        // This window's X11 id (Uno exposes no handle). It may not exist yet, so the lookup is retried later.
        NativeWindow();
        if (window != null) window.Activated += (_, args) =>
        {
            PublishNativeChrome();
            // The caret follows the app window's activation (see WebViewCaret).
            Services.WebViewCaret.SetActive(EditorView, args.WindowActivationState != Windows.UI.Core.CoreWindowActivationState.Deactivated);
        };
        WatchActivation();
        ApplyStrings();
        HookTabBarWheel();
        BuildMenus();
        ApplyTheme(post: false);
        ApplySidePane();
        transport = new EditorTransport(PostToEditor);
        document = new DocumentViewModel(transport, this, settings);
        tabs = new TabsViewModel(document, settings);
        WireHistoryItems(); // the menus were built before the document existed
        document.PropertyChanged += (_, e) => DispatcherQueue.TryEnqueue(() =>
        {
            UpdateTitle();
            // Switching tabs restores a document rather than opening one, so the tree hears about it here.
            if (e.PropertyName == nameof(DocumentViewModel.FilePath)) FollowDocumentFolder(document.FilePath);
        });
        document.RunOnUi += work => DispatcherQueue.TryEnqueue(async () => await work());
        document.FileOpened += path => DispatcherQueue.TryEnqueue(() => OnFileOpened(path));
        tabs.TabsChanged += () => DispatcherQueue.TryEnqueue(UpdateTabBar);
        tabs.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateTabBar);
        TabBar.TabItemsSource = tabs.Tabs;
        settings.PropertyChanged += OnSettingChanged;
        // The settings are one object shared by every window, so a page that goes away has to take its handlers
        // with it — otherwise the next settings change runs them against a window that no longer exists.
        Unloaded += (_, _) =>
        {
            DetachFromSettings();
            activationWatch?.Stop();
            if (automationWindow != null) Automation.AutomationWindows.Unregister(automationWindow);
        };
        RegisterHostFunctions(transport);
        transport.MessageReceived += OnEditorMessage;
        RegisterForAutomation();

        // The web view and the editor page come up while the documents are staged and the folder tree is built,
        // rather than after: the page needs about half a second of its own before it asks for anything. Its first
        // GetSettings carries the active document, so that reply waits for documentsStaged.
        var editorStarted = StartEditorAsync();
        string? sessionFolder = null;
        try
        {
            if (startupFile != null)
            {
                await tabs.OpenFileAsync(startupFile);
                foreach (var more in options.MoreFiles ?? Array.Empty<string>()) await tabs.OpenFileAsync(more);
            }
            else
            {
                // Only the window opened at startup restores the session; further windows start empty.
                switch (options.RestoreSession ? settings.FileStartupAction : FileStartupAction.NewFile)
                {
                    case FileStartupAction.RestoreSession:
                        sessionFolder = await tabs.RestoreSessionAsync();
                        break;
                    case FileStartupAction.OpenLast:
                        var last = settings.RecentFiles.FirstOrDefault(File.Exists);
                        if (last != null) await tabs.OpenFileAsync(last);
                        break;
                }
            }
        }
        finally
        {
            documentsStaged.TrySetResult(); // a file that failed to open must not keep the editor waiting
        }
        var startFolder = settings.FolderStartupAction switch
        {
            FolderStartupAction.OpenFixed => settings.StartupFolder,
            FolderStartupAction.OpenLast => sessionFolder ?? settings.LastFolder,
            _ => null,
        };
        if (startFolder != null && Directory.Exists(startFolder)) SetWorkFolder(startFolder);
        if (workFolder == null && document.FilePath != null) SetWorkFolder(Path.GetDirectoryName(document.FilePath)!);
        ApplyStatusBar();
        ApplyReadOnlyBadge();
        UpdateTitle();
        UpdateTabBar();
        HookWindowClosing();
        PublishNativeChrome();
        HookWheelFallback();
        automationStartup.TrySetResult();

        await editorStarted;
    }

    /// <summary>
    /// Brings the web view up. <c>EnsureCoreWebView2Async</c> is only awaited with a timeout: on some Linux setups
    /// it never completes even though the native view works, so the code falls back to waiting for the control to
    /// publish its CoreWebView2 and, failing that, still navigates (Uno creates the view when Source is set).
    /// </summary>
    private async Task StartEditorAsync()
    {
        Services.Log.Write("initializing web view");
        try
        {
            var ensure = EditorView.EnsureCoreWebView2Async().AsTask();
            if (await Task.WhenAny(ensure, Task.Delay(TimeSpan.FromSeconds(5))) != ensure)
                Services.Log.Write("EnsureCoreWebView2Async did not complete in 5s, continuing");
            else
                await ensure;
        }
        catch (Exception ex)
        {
            Services.Log.Error("EnsureCoreWebView2Async failed", ex);
        }

        var core = EditorView.CoreWebView2;
        for (var i = 0; core == null && i < 50; i++)
        {
            await Task.Delay(200);
            core = EditorView.CoreWebView2;
        }
        if (core == null)
        {
            var missing = Services.Log.MissingLinuxLibraries();
            Services.Log.Write($"no CoreWebView2 after 15s; the editor cannot start (missing libs: {(missing.Count == 0 ? "none" : string.Join(", ", missing))})");
            SetStatus(missing.Count > 0
                ? $"Editor failed to start: missing {string.Join(", ", missing)} — run install-linux.sh"
                : $"Editor failed to start — see {Services.Log.Path}");
            return;
        }

        Services.Log.Write("web view initialized");
        core.WebMessageReceived += (_, args) =>
        {
            var raw = EditorTransport.GetRawMessage(args);
            if (raw != null) transport!.OnWebMessage(raw);
        };
        core.NavigationCompleted += (_, args) =>
        {
            editorPageLoaded |= args.IsSuccess;
            Services.Log.Write(args.IsSuccess ? "editor page loaded" : $"navigation failed: {args.WebErrorStatus}");
            // The app window was activated before the page existed; the caret needs the view's window active too.
            if (args.IsSuccess) Services.WebViewCaret.SetActive(EditorView, true);
        };
        core.NavigationCompleted += async (_, args) => { if (args.IsSuccess) { await PostShortcutMap(); await PostContextMenuStrings(); await PostFindStrings(); } };
        var indexPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Editor", "index.html");
        if (OperatingSystem.IsMacOS())
        {
            // Uno's macOS WebView resolves a mapped folder against the bundle's Resources path once the app runs
            // from an .app bundle, and joins the two without a separator (".../Contents/ResourcesAssets/Editor"),
            // so the virtual host never loads there and the page only came up after the 8 s fallback below.
            EditorView.Source = new Uri(indexPath);
            Services.Log.Write($"navigating to the editor page from disk ({indexPath})");
        }
        else
        {
            // The folder is relative to the app directory (Uno's X11 WebView joins it onto the base directory),
            // so it must stay relative — an absolute path would be concatenated onto the base directory.
            core.SetVirtualHostNameToFolderMapping(EditorHost, "Assets/Editor", CoreWebView2HostResourceAccessKind.Allow);
            EditorView.Source = new Uri($"http://{EditorHost}/index.html");
            Services.Log.Write("navigating to the editor page");
        }

        // Fallback: where the virtual host mapping does not take effect, load the page straight from disk. The
        // editor only needs same-origin access to its own folder, which a file:// URL gives it.
        await Task.Delay(TimeSpan.FromSeconds(8));
        if (editorPageLoaded || transport == null) return;
        Services.Log.Write($"virtual host did not load; falling back to {indexPath} (exists={File.Exists(indexPath)})");
        try
        {
            EditorView.Source = new Uri(indexPath);
        }
        catch (Exception ex)
        {
            Services.Log.Error("file fallback failed", ex);
        }
        await Task.Delay(TimeSpan.FromSeconds(8));
        if (editorPageLoaded) return;
        try
        {
            var probe = await EditorView.ExecuteScriptAsync("document.readyState + '|' + location.href");
            Services.Log.Write($"web view probe: {probe}");
        }
        catch (Exception ex)
        {
            Services.Log.Error("web view probe failed", ex);
        }
        var missingLibs = Services.Log.MissingLinuxLibraries();
        SetStatus(missingLibs.Count > 0
            ? $"Editor did not load: missing {string.Join(", ", missingLibs)} — run install-linux.sh"
            : $"Editor did not load — see {Services.Log.Path}");
    }

    private bool editorPageLoaded;
    private string? lastTocJson;
    private JsonNode? latestToc;
    // The heading the editor last reported as current, so the outline can mark it and bring it into view — and
    // re-mark it after the list is rebuilt (a new ItemsSource drops the selection).
    private string? currentOutlineSlug;

    /// <summary>Fills the outline from the most recent editor state (it is only tracked while visible).</summary>
    private void RefreshOutline()
    {
        lastTocJson = latestToc?.ToJsonString();
        OutlineList.ItemsSource = OutlineItem.FromToc(latestToc);
        MarkOutlineCurrent();
    }

    /// <summary>
    /// Marks the heading the reader is at in the outline and scrolls that row into view, following the page as
    /// it scrolls. Selecting the row programmatically does not raise ItemClick, so it never posts ScrollTo back
    /// to the editor — no feedback loop, no twitch.
    /// </summary>
    private void MarkOutlineCurrent()
    {
        if (currentOutlineSlug == null) return;
        if (OutlineList.ItemsSource is not IEnumerable<OutlineItem> items) return;
        OutlineItem? match = null;
        foreach (var it in items) if (it.Slug == currentOutlineSlug) { match = it; break; }
        if (match == null) { OutlineList.SelectedItem = null; return; }
        if (!ReferenceEquals(OutlineList.SelectedItem, match)) OutlineList.SelectedItem = match;
        OutlineList.ScrollIntoView(match);
    }
    private string? pendingWordCount;
    private string? shownWordCount;
    private DispatcherTimer? wordCountTimer;

    private void StartWordCountTimer()
    {
        if (wordCountTimer == null)
        {
            wordCountTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            wordCountTimer.Tick += (_, _) =>
            {
                if (pendingWordCount == shownWordCount)
                {
                    wordCountTimer!.Stop();
                    return;
                }
                shownWordCount = pendingWordCount;
                WordCountText.Text = shownWordCount ?? "";
            };
        }
        if (!wordCountTimer.IsEnabled) wordCountTimer.Start();
    }

    // ---- editor bridge ------------------------------------------------------------------------------------------

    private async Task PostToEditor(string json)
    {
        var script = "window.__unoDeliver(" + JsonSerializer.Serialize(json) + ")";
        var started = DateTime.UtcNow;
        try { await EditorView.ExecuteScriptAsync(script); }
        catch (Exception ex) { Console.Error.WriteLine($"[Typedown.Uno] post to editor failed: {ex.Message}"); }
        var ms = (DateTime.UtcNow - started).TotalMilliseconds;
        if (json.Length > 65536 || ms > 200) Services.Log.Write($"post to editor: {json.Length} chars took {ms:F0} ms");
    }

    private Task Post(string name, object? args) => transport?.PostMessage(name, args) ?? Task.CompletedTask;

    /// <summary>The web view swallows key presses, so it must know which combinations to hand back.</summary>
    private Task PostShortcutMap() => Post("ShortcutMap", new { keys = System.Text.Json.Nodes.JsonNode.Parse(settings.Shortcuts.ForwardedKeysJson()) });

    /// <summary>Labels for the page's own context menu (a host flyout would be drawn behind the native view).</summary>
    private Task PostContextMenuStrings() => Post("ContextMenuStrings", new
    {
        copy = Loc.Get("Copy"),
        cut = Loc.Get("Cut"),
        paste = Loc.Get("Paste"),
        selectAll = Loc.Get("SelectAll"),
        // the floating tools the page draws for the editor
        duplicate = Loc.Get("Duplicate"),
        insertBefore = Loc.Get("InsertBefore"),
        insertAfter = Loc.Get("InsertAfter"),
        deleteParagraph = Loc.Get("DeleteParagraph"),
        insertRowAbove = Loc.Get("InsertRowAbove"),
        insertRowBelow = Loc.Get("InsertRowBelow"),
        deleteRow = Loc.Get("DeleteRow"),
        insertColLeft = Loc.Get("InsertColumnLeft"),
        insertColRight = Loc.Get("InsertColumnRight"),
        deleteCol = Loc.Get("DeleteColumn"),
        // Hover tooltips the page draws (uno-bridge looks these up as menuStrings['tip_' + key]).
        tip_CopyContent = Loc.Get("CopyContent"),
        tip_CtrlAndClickOpenLink = Loc.Get("CtrlAndClickOpenLink"),
    });

    /// <summary>Labels for the find and replace bar, which the page draws for the same reason.</summary>
    private Task PostFindStrings() => Post("FindStrings", new
    {
        find = Loc.Get("FindPlaceholder"),
        replaceWith = Loc.Get("ReplaceWith"),
        replace = Loc.Get("ReplaceAction"),
        replaceAll = Loc.Get("ReplaceAll"),
        previous = Loc.Get("FindPrevious"),
        next = Loc.Get("FindNext"),
        close = Loc.Get("Close"),
    });

    /// <summary>Puts the clipboard's text through the editor's paste handler, as Ctrl+V would.</summary>
    private async Task PasteClipboardTextAsync()
    {
        try
        {
            var view = Clipboard.GetContent();
            if (view == null || !view.Contains(StandardDataFormats.Text)) return;
            var text = await view.GetTextAsync();
            if (string.IsNullOrEmpty(text)) return;
            // Text copied from the editor also carries HTML; handing it over keeps bold, links and the rest,
            // exactly as Ctrl+V does.
            string? html = null;
            if (view.Contains(StandardDataFormats.Html))
            {
                try { html = await view.GetHtmlFormatAsync(); } catch { }
                // Windows wraps the fragment in a CF_HTML header; the editor wants the markup only.
                var start = html?.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase) ?? -1;
                var end = html?.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase) ?? -1;
                if (start >= 0 && end > start) html = html![(start + "<!--StartFragment-->".Length)..end];
            }
            await Post("Paste", new { type = "normal", text, html });
        }
        catch (Exception ex)
        {
            Services.Log.Error("paste from clipboard", ex);
        }
    }

    /// <summary>Paste with formatting dropped: the same clipboard text, handed over without its HTML.</summary>
    private async Task PasteAsPlainTextAsync()
    {
        try
        {
            var view = Clipboard.GetContent();
            if (view == null || !view.Contains(StandardDataFormats.Text)) return;
            var text = await view.GetTextAsync();
            if (string.IsNullOrEmpty(text)) return;
            await Post("Paste", new { type = "pasteAsPlainText", text, html = (string?)null });
        }
        catch (Exception ex)
        {
            Services.Log.Error("paste as plain text", ex);
        }
    }

    private void RegisterHostFunctions(EditorTransport t)
    {
        t.Handle("GetSettings", async _ =>
        {
            await documentsStaged.Task;
            document!.EditorReady = true;
            var options = settings.EditorOptions();
            options["themeCss"] = Services.ThemeFiles.Read(settings.CustomTheme);
            foreach (var (key, value) in JsonSerializer.SerializeToNode(document.GetLoadPayload(), EditorTransport.JsonOptions)!.AsObject())
                options[key] = value?.DeepClone();
            return (object?)options;
        });
        t.Handle("GetCurrentTheme", _ => (object?)ThemePayload());
        t.Handle("ContentLoaded", _ => (object?)"");
        t.Handle("GetStringResources", args =>
        {
            // The editor asks for a list of string keys (placeholders, footnote tool, etc.) and expects each
            // mapped to its localized text — the same as the Windows host. Returning {} left them all English.
            var result = new Dictionary<string, string>();
            if (args?["names"] is System.Text.Json.Nodes.JsonArray names)
                foreach (var n in names)
                {
                    var key = n?.GetValue<string>();
                    if (key != null) result[key] = Loc.Get(key);
                }
            return (object)result;
        });
        t.Handle("LoadImage", args => new { url = args?["url"]?.GetValue<string>() ?? "" });
        t.Handle("ResizeTable", args => new { row = args?["row"]?.GetValue<int>() ?? 2, column = args?["column"]?.GetValue<int>() ?? 2 });
        t.Handle("SetClipboard", args =>
        {
            // One copy arrives as two calls (text/html then text/plain). A fresh package per call would leave
            // only the last format on the clipboard, so calls close together go into the same package.
            var type = args?["type"]?.GetValue<string>();
            var data = args?["data"]?.ToString() ?? "";
            var now = DateTime.UtcNow;
            if (clipboardBatch == null || now - clipboardBatchTime > TimeSpan.FromMilliseconds(500))
                clipboardBatch = new DataPackage();
            clipboardBatchTime = now;
            if (type == "text/html") clipboardBatch.SetHtmlFormat(data); else clipboardBatch.SetText(data);
            try { Clipboard.SetContent(clipboardBatch); } catch (Exception ex) { Services.Log.Error("set clipboard", ex); }
            return (object?)true;
        });
        t.Handle("OpenNewWindow", async args =>
        {
            var href = args?.GetValue<string>();
            if (!string.IsNullOrEmpty(href) && tabs != null)
            {
                var path = Path.IsPathRooted(href) ? href : Path.GetFullPath(Path.Combine(document!.BasePath, href));
                if (File.Exists(path)) await tabs.OpenFileAsync(path);
            }
            return null;
        });
        t.Handle("ExportCallback", async args =>
        {
            var html = args?["html"]?.GetValue<string>() ?? "";
            var path = args?["context"]?["filePath"]?.GetValue<string>();
            if (path != null)
            {
                await SafeFile.WriteAllTextAtomicAsync(path, html);
                // Exports that are a step towards something else (a PDF, the print dialog) say so by leaving a
                // continuation behind; a plain "Export HTML" has none and just reports where the file went.
                Func<string, Task>? next = null;
                lock (exportContinuations)
                {
                    if (exportContinuations.TryGetValue(path, out var found)) { next = found; exportContinuations.Remove(path); }
                }
                if (next != null)
                {
                    await next(path);
                }
                else
                {
                    SetStatus(Loc.Format("Exported", path));
                    if (settings.OpenFolderAfterExport) OpenContainingFolder(path);
                }
            }
            return (object?)true;
        });
        t.Handle("PrintHTML", _ => (object?)false);
        t.Handle("UnhandledException", args => { Console.Error.WriteLine($"[editor] {args}"); return (object?)null; });
    }

    private void OnEditorMessage(string name, JsonNode? args)
    {
        switch (name)
        {
            case "FileLoaded":
                Services.Log.Write($"editor handshake done ({args?["text"]?.GetValue<string>()?.Length ?? 0} chars)");
                DispatcherQueue.TryEnqueue(UpdateTitle);
                break;
            case "StateChange":
                // Fires on every keystroke: only touch the UI for what is actually visible, and never rebuild the
                // outline for an unchanged table of contents (rebuilding a ListView per keystroke is expensive).
                var state = args?["state"];
                if (settings.StatusBarOpen)
                {
                    var count = state?["wordCount"]?[settings.WordCountMethod switch
                    {
                        WordCountMethod.Characters => "character",
                        WordCountMethod.Paragraphs => "paragraph",
                        _ => "word",
                    }];
                    // Writing the status bar on every keystroke repaints the window; the count is only informative,
                    // so it is coalesced (a full repaint per character is the single most expensive thing here).
                    if (count != null)
                    {
                        pendingWordCount = Loc.Format("Words", count);
                        DispatcherQueue.TryEnqueue(StartWordCountTimer);
                    }
                }
                // Keep the last table of contents so switching to the outline shows it without waiting for an edit.
                latestToc = state?["toc"];
                if (settings.SidePaneOpen && settings.SidePanePage == 1 && !searchOpen)
                {
                    var tocJson = latestToc?.ToJsonString();
                    if (tocJson != null && tocJson != lastTocJson)
                    {
                        lastTocJson = tocJson;
                        var items = OutlineItem.FromToc(latestToc);
                        DispatcherQueue.TryEnqueue(() => { OutlineList.ItemsSource = items; MarkOutlineCurrent(); });
                    }
                }
                break;
            case "OutlineCurrent":
                // The editor reports the heading the reader is at as the page scrolls (both edit and reading
                // mode). Mark it in the outline and keep that row in view. A slug from a stale load, or one not
                // in the current table of contents, simply finds no match and is ignored.
                if (args?["loadId"] == null) break;
                var curSlug = args?["slug"]?.GetValue<string>();
                DispatcherQueue.TryEnqueue(() => { currentOutlineSlug = curSlug; MarkOutlineCurrent(); });
                break;
            case "OpenURI":
                var uri = args?["uri"]?.GetValue<string>();
                if (uri != null) DispatcherQueue.TryEnqueue(async () => { try { await Launcher.LaunchUriAsync(new Uri(uri)); } catch { } });
                break;
            case "FilesDropped":
                var dropped = args?["paths"]?.AsArray().Select(p => p?.GetValue<string>()).Where(p => p != null).Select(p => p!).ToList();
                if (dropped is { Count: > 0 }) DispatcherQueue.TryEnqueue(async () => await OpenDroppedAsync(dropped));
                break;
            case "ClipboardImageRequest":
                DispatcherQueue.TryEnqueue(async () => await InsertClipboardImageAsync());
                break;
            case "ClipboardPasteRequest":
                // Some WebKit/X11 combinations expose no DataTransferItem entries. Inspecting the native
                // clipboard lets the host distinguish an image from text and keeps text on the repair path.
                DispatcherQueue.TryEnqueue(async () => await PasteClipboardContentAsync());
                break;
            case "ReplaceImageRequest":
                // The image toolbar's edit button: pick a file, put it where the settings say, and hand the new
                // path back to the editor, which swaps the src of the image that is selected.
                DispatcherQueue.TryEnqueue(async () =>
                {
                    var picked = await PickImageFileAsync();
                    if (picked == null || document == null) return;
                    try
                    {
                        var link = ImagePaths.PlaceImage(picked, document.FilePath, settings);
                        await Post("ImageEditToolbarClick", new { type = "updateImage", attrName = "src", attrValue = link });
                    }
                    catch (Exception ex)
                    {
                        Services.Log.Error("replace image", ex);
                    }
                });
                break;
            case "ClipboardSetText":
                var copyText = args?["text"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(copyText)) DispatcherQueue.TryEnqueue(() =>
                {
                    var package = new DataPackage();
                    package.SetText(copyText);
                    try { Clipboard.SetContent(package); } catch (Exception ex) { Services.Log.Error("copy to clipboard", ex); }
                });
                break;
            case "ClipboardTextRequest":
                // Paste from the page's context menu: WebKit refuses execCommand('paste'), so the host reads the
                // clipboard and hands the text to the editor's own paste handler.
                DispatcherQueue.TryEnqueue(async () => await PasteClipboardTextAsync());
                break;
            case "ImagePasted":
                var dataUrl = args?["dataUrl"]?.GetValue<string>();
                if (dataUrl != null) DispatcherQueue.TryEnqueue(async () => await InsertPastedImageAsync(dataUrl));
                break;
            // The editor asks the host to draw these; the page draws them instead (a host popup would end up behind
            // the native web view), so they go straight back with the arguments reassembled.
            case "OpenFormatPicker":
            case "OpenImageToolbar":
            case "OpenFrontMenu":
            case "OpenTableTools":
            case "OpenToolTip":
                var floatArgs = args?.DeepClone();
                DispatcherQueue.TryEnqueue(async () => await Post("ShowFloat", new { kind = name, args = floatArgs }));
                break;
            case "VimCommand":
                // Vim's :w, :q, :wq/:x in source mode, as the File menu would (closing asks about unsaved changes).
                var command = args?["command"]?.GetValue<string>();
                DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        if (command is "write" or "writeQuit" && document != null && !await document.SaveAsync()) return;
                        if (command is "quit" or "writeQuit" && tabs != null) await tabs.CloseTabAsync(tabs.ActiveTab);
                    }
                    catch (Exception ex) { Services.Log.Write($"vim {command}: {ex.Message}"); }
                });
                break;
            case "Shortcut":
                var key = args?["key"]?.GetValue<string>() ?? "";
                var ctrl = args?["ctrl"]?.GetValue<bool>() ?? false;
                var shift = args?["shift"]?.GetValue<bool>() ?? false;
                var alt = args?["alt"]?.GetValue<bool>() ?? false;
                DispatcherQueue.TryEnqueue(async () => await HandleShortcutAsync(key, ctrl, shift, alt));
                break;
        }
    }

    // ---- images and drops ---------------------------------------------------------------------------------------

    /// <summary>Documents are opened as tabs; images are stored next to the document and linked.</summary>
    /// <summary>Opens a file handed over by another launch of the program (see <see cref="Services.SingleInstance"/>).</summary>
    public async Task OpenExternalFileAsync(string path)
    {
        if (tabs == null || !File.Exists(path)) return;
        try
        {
            await tabs.OpenFileAsync(path);
        }
        catch (Exception ex)
        {
            Services.Log.Error("open handed-over file", ex);
        }
    }

    private async Task OpenDroppedAsync(IReadOnlyList<string> paths)
    {
        if (tabs == null || document == null) return;
        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    SetWorkFolder(path, explicitChoice: true);
                    settings.SidePaneOpen = true;
                    settings.SidePanePage = 0;
                    ApplySidePane();
                }
                else if (ImagePaths.IsImageFile(path))
                {
                    await InsertImageFileAsync(path);
                }
                else if (File.Exists(path))
                {
                    await tabs.OpenFileAsync(path);
                }
            }
            catch (Exception ex)
            {
                Services.Log.Error($"drop {path}", ex);
                await ShowErrorAsync(Loc.Get("Error"), ex.Message);
            }
        }
    }

    private async Task InsertImageFileAsync(string path)
    {
        if (document == null) return;
        try
        {
            var link = settings.ImageAction == ImageInsertAction.Upload
                ? await UploadOrKeepAsync(path)
                : ImagePaths.PlaceImage(path, document.FilePath, settings);
            await PostInsertImage(link, Path.GetFileNameWithoutExtension(path));
            SetStatus(link);
        }
        catch (Exception ex)
        {
            Services.Log.Error("insert image", ex);
            await ShowErrorAsync(Loc.Get("Error"), ex.Message);
        }
    }

    /// <summary>Routes a paste that WebKit could not inspect through the native clipboard.</summary>
    private async Task PasteClipboardContentAsync()
    {
        try
        {
            var view = Clipboard.GetContent();
            if (view == null) return;
            if (view.Contains(StandardDataFormats.Bitmap) ||
                view.AvailableFormats.Any(f => f.StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
                await InsertClipboardImageAsync();
            else
                await PasteClipboardTextAsync();
        }
        catch (Exception ex)
        {
            Services.Log.Error("paste from native clipboard", ex);
        }
    }

    /// <summary>Reads an image from the system clipboard (the page could not) and inserts it.</summary>
    private async Task InsertClipboardImageAsync()
    {
        if (document == null) return;
        try
        {
            var view = Clipboard.GetContent();
            if (view == null) return;
            byte[]? bytes = null;
            var extension = ".png";
            if (view.Contains(StandardDataFormats.Bitmap))
            {
                var reference = await view.GetBitmapAsync();
                using var stream = await reference.OpenReadAsync();
                bytes = await ReadAllAsync(stream);
            }
            else
            {
                // X11 clipboards hand out the raw MIME type instead of the WinRT bitmap format.
                var format = view.AvailableFormats.FirstOrDefault(f => f.StartsWith("image/", StringComparison.OrdinalIgnoreCase));
                if (format == null)
                {
                    Services.Log.Write($"clipboard has no image (formats: {string.Join(", ", view.AvailableFormats)})");
                    return;
                }
                extension = format switch { "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/webp" => ".webp", "image/bmp" => ".bmp", _ => ".png" };
                var data = await view.GetDataAsync(format);
                bytes = data switch
                {
                    byte[] raw => raw,
                    Windows.Storage.Streams.IRandomAccessStream stream => await ReadAllAsync(stream),
                    Windows.Storage.Streams.IBuffer buffer => buffer.ToArray(),
                    string text when text.StartsWith("data:", StringComparison.Ordinal) => ImagePaths.DecodeDataUrl(text)?.bytes,
                    _ => null,
                };
                if (bytes == null)
                {
                    Services.Log.Write($"clipboard image format {format} returned {data?.GetType().Name ?? "null"}");
                    return;
                }
            }
            string link;
            if (settings.ImageAction == ImageInsertAction.Upload)
            {
                // Uploaded from a file of its own, which goes again once the picture is online.
                var temp = Path.Combine(Path.GetTempPath(), $"typedown-clipboard-{DateTime.Now:yyyyMMdd-HHmmss}{extension}");
                await File.WriteAllBytesAsync(temp, bytes);
                try { link = await UploadOrKeepAsync(temp, keepOnFailure: () => ImagePaths.SaveImageBytes(bytes, extension, document.FilePath, settings)); }
                finally { try { File.Delete(temp); } catch { } }
            }
            else link = ImagePaths.SaveImageBytes(bytes, extension, document.FilePath, settings);
            await PostInsertImage(link, "image");
            SetStatus(link);
        }
        catch (Exception ex)
        {
            Services.Log.Error("clipboard image", ex);
        }
    }

    private static async Task<byte[]> ReadAllAsync(Windows.Storage.Streams.IRandomAccessStream stream)
    {
        using var input = stream.AsStreamForRead();
        using var output = new MemoryStream(stream.Size > int.MaxValue ? 0 : (int)stream.Size);
        await input.CopyToAsync(output);
        return output.ToArray();
    }

    private async Task InsertPastedImageAsync(string dataUrl)
    {
        if (document == null) return;
        try
        {
            var decoded = ImagePaths.DecodeDataUrl(dataUrl);
            if (decoded == null) return;
            var link = ImagePaths.SaveImageBytes(decoded.Value.bytes, decoded.Value.extension, document.FilePath, settings);
            await PostInsertImage(link, "image");
            SetStatus(link);
        }
        catch (Exception ex)
        {
            Services.Log.Error("paste image", ex);
            await ShowErrorAsync(Loc.Get("Error"), ex.Message);
        }
    }

    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg" };

    /// <summary>The editor's insertImage takes {alt, src, title} — a bare string inserts an empty image.</summary>
    private Task PostInsertImage(string link, string alt) =>
        Post("InsertImage", new { src = settings.EncodeImageLinks ? ImagePaths.EncodeLink(link) : link, alt, title = "" });

    private async Task InsertImageDialogAsync()
    {
        var path = await PickImageFileAsync();
        if (path != null) await InsertImageFileAsync(path);
    }

    /// <summary>Asks for an image file with whichever picker works on this platform.</summary>
    private async Task<string?> PickImageFileAsync()
    {
        string? path;
        if (UseBuiltInPicker)
        {
            path = await ShowBuiltInPickerAsync(FilePickerDialog.PickerMode.OpenFile, null, ImageExtensions);
        }
        else
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            foreach (var ext in ImageExtensions) picker.FileTypeFilter.Add(ext);
            InitPicker(picker);
            path = (await picker.PickSingleFileAsync())?.Path;
        }
        return path;
    }

    // ---- menus ------------------------------------------------------------------------------------------------------

    private MenuFlyoutSubItem? recentMenu;

    private readonly List<System.ComponentModel.PropertyChangedEventHandler> menuToggleHandlers = new();

    /// <summary>The theme entries of the View menu, so the tick can move without rebuilding the menu.</summary>
    private readonly List<(RadioMenuFlyoutItem Item, string? CustomId, AppTheme? BuiltIn)> themeMenuItems = new();

    /// <summary>
    /// The editor is a WebKitGTK view of its own inside the window, and it paints its own background — white —
    /// in whatever a resize or a re-render exposes before the page is drawn again. Painted in the colour the
    /// page itself uses, that moment stops showing up as a white band.
    /// </summary>
    private void ApplyEditorWindowBackground()
    {
        var theme = Services.ThemeFiles.Find(settings.CustomTheme);
        // A theme's "background" is the editor's own colour (see docs/custom-theme.md); without one the built-in
        // theme's editor background is what the web view will paint anyway.
        var colour = Brush(theme?.Background) as SolidColorBrush;
        var (r, g, b) = colour is null
            ? EffectiveTheme switch
            {
                AppTheme.Black => ((byte)0, (byte)0, (byte)0),
                AppTheme.Dark => ((byte)0x27, (byte)0x27, (byte)0x27),
                AppTheme.System when IsDarkTheme => ((byte)0x27, (byte)0x27, (byte)0x27),
                _ => ((byte)0xf9, (byte)0xf9, (byte)0xf9),
            }
            : (colour.Color.R, colour.Color.G, colour.Color.B);
        Services.WebViewBackground.Apply(EditorView, r, g, b);
    }

    /// <summary>
    /// Gives a dialog the colours of the custom theme, so that opening one over a themed window does not drop
    /// back to the built-in palette. A theme that names no colours leaves the dialog as it is; the accent is the
    /// application's (<see cref="ApplyAccentResources"/>).
    /// </summary>
    private void PaintFromTheme(ContentDialog dialog)
    {
        var theme = Services.ThemeFiles.Find(settings.CustomTheme);
        if (theme == null) return;
        var background = Brush(theme.Background) ?? Brush(theme.Surface);
        var foreground = Brush(theme.Foreground) ?? Readable(theme.Surface ?? theme.Background);
        if (background != null) dialog.Background = background;
        if (foreground != null) dialog.Foreground = foreground;
    }

    /// <summary>The settings dialog open while the theme changes takes the new theme, colours and accent.</summary>
    private void RepaintOpenDialog()
    {
        if (settingsDialog is not { } dialog) return;
        dialog.ClearValue(Control.BackgroundProperty);
        dialog.ClearValue(Control.ForegroundProperty);
        PaintFromTheme(dialog);
        dialog.RequestedTheme = DialogTheme;
        Retheme(dialog);
    }

    /// <summary>Through the other theme and back: every theme resource below the element is looked up again.</summary>
    private static void Retheme(FrameworkElement element)
    {
        var requested = element.RequestedTheme;
        element.RequestedTheme = element.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        element.RequestedTheme = requested;
    }

    private static readonly string[] AccentBrushKeys =
        { "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush", "AccentControlElevationBorderBrush" };
    private static readonly string[] OnAccentBrushKeys = { "TextOnAccentFillColorPrimaryBrush", "TextOnAccentFillColorSecondaryBrush" };
    private static readonly string[] AccentColorKeys =
        { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3" };

    /// <summary>
    /// The custom theme's accent for every control, in the application's resources: a list's selected item looks
    /// its accent up there once its state changes, whatever its dialog says (a dialog-only accent showed the theme's
    /// colour until the pointer moved, then the system blue). A theme without an accent takes them out again.
    /// </summary>
    private static void ApplyAccentResources(Services.CustomTheme? theme)
    {
        var resources = Application.Current.Resources;
        foreach (var key in AccentBrushKeys.Concat(OnAccentBrushKeys).Concat(AccentColorKeys)) resources.Remove(key);
        if (ParseAccent(theme?.Accent) is not { } accent) return;
        Windows.UI.Color Shade(double amount) => amount >= 0
            ? Windows.UI.Color.FromArgb(255, (byte)(accent.Item1 + (255 - accent.Item1) * amount), (byte)(accent.Item2 + (255 - accent.Item2) * amount), (byte)(accent.Item3 + (255 - accent.Item3) * amount))
            : Windows.UI.Color.FromArgb(255, (byte)(accent.Item1 * (1 + amount)), (byte)(accent.Item2 * (1 + amount)), (byte)(accent.Item3 * (1 + amount)));
        var shades = new[] { 0, .2, .4, .6, -.2, -.4, -.6 };
        for (var i = 0; i < AccentColorKeys.Length; i++) resources[AccentColorKeys[i]] = Shade(shades[i]);
        var accentBrush = new SolidColorBrush(Shade(0));
        resources["AccentFillColorDefaultBrush"] = accentBrush;
        resources["AccentFillColorSecondaryBrush"] = new SolidColorBrush(Windows.UI.Color.FromArgb(230, (byte)accent.Item1, (byte)accent.Item2, (byte)accent.Item3));
        resources["AccentFillColorTertiaryBrush"] = new SolidColorBrush(Windows.UI.Color.FromArgb(204, (byte)accent.Item1, (byte)accent.Item2, (byte)accent.Item3));
        resources["AccentControlElevationBorderBrush"] = accentBrush;
        if (Readable($"#{accent.Item1:x2}{accent.Item2:x2}{accent.Item3:x2}") is { } onAccent)
            foreach (var key in OnAccentBrushKeys) resources[key] = onAccent;
    }

    /// <summary>Opens the document that explains the theme format, which ships next to the app.</summary>
    internal static void OpenThemeDocument()
    {
        try
        {
            Services.ThemeFiles.EnsureFolder();
            var path = File.Exists(Services.ThemeFiles.DocumentPath)
                ? Services.ThemeFiles.DocumentPath
                : System.IO.Path.Combine(Services.ThemeFiles.BundledFolder, "custom-theme.md");
            if (File.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Services.Log.Error("open theme document", ex);
        }
    }

    private void UpdateThemeChecks()
    {
        var custom = settings.CustomTheme;
        foreach (var (item, id, builtIn) in themeMenuItems)
            item.IsChecked = id != null
                ? id == custom
                : string.IsNullOrEmpty(custom) && builtIn == settings.Theme;
    }

    /// <summary>Drops every subscription this page holds on the shared settings.</summary>
    private void DetachFromSettings()
    {
        settings.PropertyChanged -= OnSettingChanged;
        foreach (var handler in menuToggleHandlers) settings.PropertyChanged -= handler;
        menuToggleHandlers.Clear();
        Services.X11Window.WheelScrolled -= OnNativeWheel;
    }

    private void BuildMenus()
    {
        foreach (var handler in menuToggleHandlers) settings.PropertyChanged -= handler;
        menuToggleHandlers.Clear();
        KeyboardAccelerators.Clear(); // rebuilt below from the current bindings
        AddTabNumberAccelerators();
        commandActions.Clear();
        MainMenu.Items.Clear();
        var file = new MenuBarItem { Title = Loc.Get("File") };
        file.Items.Add(Item("New", async () => { if (tabs != null) await tabs.NewTabAsync(); }, ShortcutCommand.NewTab));
        file.Items.Add(Item("NewWindow", () => NewWindow(), ShortcutCommand.NewWindow));
        file.Items.Add(Item("Open", async () => await OpenFileDialogAsync(), ShortcutCommand.Open));
        file.Items.Add(Item("OpenFolder", async () => await OpenFolderDialogAsync(), ShortcutCommand.OpenFolder));
        recentMenu = new MenuFlyoutSubItem { Text = Loc.Get("Recent") };
        file.Items.Add(recentMenu);
        FillRecent();
        file.Items.Add(new MenuFlyoutSeparator());
        file.Items.Add(Item("Save", async () => { if (document != null) await document.SaveAsync(); }, ShortcutCommand.Save));
        file.Items.Add(Item("SaveAs", async () => { if (document != null) await document.SaveAsAsync(); }, ShortcutCommand.SaveAs));
        file.Items.Add(Item("ImportHtml", async () => await ImportHtmlAsync()));
        file.Items.Add(Item("ExportHtml", async () => await ExportHtmlAsync(), ShortcutCommand.ExportHtml));
        file.Items.Add(Item("ExportPdf", async () => await ExportPdfAsync(), ShortcutCommand.ExportPdf));
        file.Items.Add(Item("ExportImage", async () => await ExportImageAsync()));
        file.Items.Add(Item("PrintPdf", async () => await PrintAsync(), ShortcutCommand.Print));
        file.Items.Add(Item("UploadLocalImages", async () => await UploadLocalImagesAsync(), ShortcutCommand.UploadLocalImages));
        file.Items.Add(Item("ShareHedgeDoc", async () => await ShareToHedgeDocAsync(), ShortcutCommand.ShareHedgeDoc));
        file.Items.Add(new MenuFlyoutSeparator());
        file.Items.Add(Item("Settings", async () => await ShowSettingsAsync(), ShortcutCommand.Settings));
        file.Items.Add(Item("CloseTab", async () => { if (tabs != null) await tabs.CloseTabAsync(tabs.ActiveTab); }, ShortcutCommand.CloseTab));
        file.Items.Add(Item("Exit", async () => await ExitAsync(quit: true), ShortcutCommand.Exit));
        MainMenu.Items.Add(file);

        var edit = new MenuBarItem { Title = Loc.Get("Edit") };
        // Undo lives in the shell: the editor rebuilds its own DOM, so the browser's undo cannot be used.
        undoMenuItem = Item("Undo", async () => { if (document != null) await document.UndoAsync(); }, ShortcutCommand.Undo);
        redoMenuItem = Item("Redo", async () => { if (document != null) await document.RedoAsync(); }, ShortcutCommand.Redo);
        edit.Items.Add(undoMenuItem);
        edit.Items.Add(redoMenuItem);
        WireHistoryItems();
        edit.Items.Add(new MenuFlyoutSeparator());
        edit.Items.Add(Item("Cut", async () => await Post("Cut", new { type = "normal", copyInfo = (object?)null })));
        edit.Items.Add(Item("Copy", async () => await Post("Copy", new { type = "normal", copyInfo = (object?)null })));
        edit.Items.Add(Item("Paste", async () => await PasteClipboardTextAsync()));
        edit.Items.Add(Item("PasteAsPlainText", async () => await PasteAsPlainTextAsync()));
        edit.Items.Add(Item("DeleteSelection", async () => await Post("DeleteSelection", null)));
        edit.Items.Add(new MenuFlyoutSeparator());
        edit.Items.Add(Item("CopyAsPlainText", async () => await Post("Copy", new { type = "copyAsPlainText", copyInfo = (object?)null })));
        edit.Items.Add(Item("CopyAsMarkdown", async () => await Post("Copy", new { type = "copyAsMarkdown", copyInfo = (object?)null })));
        edit.Items.Add(Item("CopyAsHtml", async () => await Post("Copy", new { type = "copyAsHtml", copyInfo = (object?)null })));
        edit.Items.Add(new MenuFlyoutSeparator());
        edit.Items.Add(Item("Find", () => ShowFind(true), ShortcutCommand.Find));
        edit.Items.Add(Item("FindNext", async () => await Post("Find", new { action = "next" }), ShortcutCommand.FindNext));
        edit.Items.Add(Item("FindPrevious", async () => await Post("Find", new { action = "prev" }), ShortcutCommand.FindPrevious));
        edit.Items.Add(Item("Replace", () => ShowFind(true, replace: true), ShortcutCommand.Replace));
        edit.Items.Add(new MenuFlyoutSeparator());
        edit.Items.Add(Item("SearchInFolder", () => OnSearchButtonClick(this, new RoutedEventArgs()), ShortcutCommand.SearchInFolder));
        edit.Items.Add(new MenuFlyoutSeparator());
        edit.Items.Add(Item("SelectAll", async () => await Post("SelectAll", null), ShortcutCommand.SelectAll));
        MainMenu.Items.Add(edit);

        var paragraph = new MenuBarItem { Title = Loc.Get("Paragraph") };
        var heading = new MenuFlyoutSubItem { Text = Loc.Get("Heading") };
        for (var i = 1; i <= 6; i++)
        {
            var level = i;
            heading.Items.Add(new MenuFlyoutItem { Text = Loc.Format("HeadingN", level) }.OnClick(async () => await Post("UpdateParagraph", $"heading {level}")));
        }
        paragraph.Items.Add(heading);
        paragraph.Items.Add(Item("ParagraphPlain", async () => await Post("UpdateParagraph", "paragraph")));
        paragraph.Items.Add(Item("IncreaseHeading", async () => await Post("UpdateParagraph", "upgrade heading")));
        paragraph.Items.Add(Item("DecreaseHeading", async () => await Post("UpdateParagraph", "degrade heading")));
        paragraph.Items.Add(new MenuFlyoutSeparator());
        paragraph.Items.Add(Item("Table", async () => await InsertTableAsync()));
        paragraph.Items.Add(Item("CodeFences", async () => await Post("UpdateParagraph", "pre")));
        paragraph.Items.Add(Item("MathBlock", async () => await Post("UpdateParagraph", "mathblock")));
        paragraph.Items.Add(Item("Quote", async () => await Post("UpdateParagraph", "blockquote")));
        paragraph.Items.Add(Item("QuoteIncrease", async () => await Post("UpdateParagraph", "blockquote-increase")));
        paragraph.Items.Add(Item("QuoteDecrease", async () => await Post("UpdateParagraph", "blockquote-decrease")));
        paragraph.Items.Add(new MenuFlyoutSeparator());
        paragraph.Items.Add(Item("OrderedList", async () => await Post("UpdateParagraph", "ol-order")));
        paragraph.Items.Add(Item("UnorderedList", async () => await Post("UpdateParagraph", "ul-bullet")));
        paragraph.Items.Add(Item("TaskList", async () => await Post("UpdateParagraph", "ul-task")));
        paragraph.Items.Add(new MenuFlyoutSeparator());
        var chart = new MenuFlyoutSubItem { Text = Loc.Get("Chart") };
        foreach (var (key, type) in new[] { ("Mermaid", "mermaid"), ("FlowChart", "flowchart"), ("Sequence", "sequence"), ("VegaLite", "vega-lite"), ("PlantUml", "plantuml") })
            chart.Items.Add(Item(key, async () => await Post("UpdateParagraph", type)));
        paragraph.Items.Add(chart);
        paragraph.Items.Add(Item("HorizontalLine", async () => await Post("UpdateParagraph", "hr")));
        paragraph.Items.Add(Item("Toc", async () => await Post("UpdateParagraph", "toc")));
        paragraph.Items.Add(Item("FrontMatter", async () => await Post("UpdateParagraph", "front-matter")));
        paragraph.Items.Add(Item("Footnote", async () => await Post("UpdateParagraph", "footnote")));
        paragraph.Items.Add(Item("LinkReference", async () => await Post("UpdateParagraph", "linkref")));
        paragraph.Items.Add(new MenuFlyoutSeparator());
        paragraph.Items.Add(Item("InsertBefore", async () => await Post("InsertParagraph", "before")));
        paragraph.Items.Add(Item("InsertAfter", async () => await Post("InsertParagraph", "after")));
        MainMenu.Items.Add(paragraph);

        var format = new MenuBarItem { Title = Loc.Get("Format") };
        foreach (var (key, type) in new[] { ("Strong", "strong"), ("Emphasis", "em"), ("Underline", "u"), ("InlineCode", "inline_code"), ("InlineMath", "inline_math"), ("Strikethrough", "del"), ("Highlight", "mark"), ("Hyperlink", "link"), ("Image", "image") })
            format.Items.Add(Item(key, async () => await Post("Format", type)));
        format.Items.Add(Item("InsertImage", async () => await InsertImageDialogAsync(), ShortcutCommand.InsertImage));
        format.Items.Add(new MenuFlyoutSeparator());
        format.Items.Add(Item("ClearFormat", async () => await Post("Format", "clear")));
        MainMenu.Items.Add(format);

        var view = new MenuBarItem { Title = Loc.Get("View") };
        view.Items.Add(Toggle("SourceCode", () => settings.SourceCode, v => settings.SourceCode = v, ShortcutCommand.SourceCode));
        // Focus and typewriter mode both follow the caret, so they mean nothing in reading mode (no caret) or in
        // source mode (a plain text editor): grey them out rather than let them look enabled and do nothing.
        var focusItem = Toggle("FocusMode", () => settings.FocusMode, v => settings.FocusMode = v);
        var typewriterItem = Toggle("Typewriter", () => settings.Typewriter, v => settings.Typewriter = v);
        void UpdateModeAvailability()
        {
            var caretModes = !settings.ReadOnly && !settings.SourceCode;
            focusItem.IsEnabled = caretModes;
            typewriterItem.IsEnabled = caretModes;
        }
        UpdateModeAvailability();
        void OnModeChanged(object? _, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(AppSettings.ReadOnly) or nameof(AppSettings.SourceCode))
                DispatcherQueue.TryEnqueue(UpdateModeAvailability);
        }
        settings.PropertyChanged += OnModeChanged;
        menuToggleHandlers.Add(OnModeChanged);
        view.Items.Add(focusItem);
        view.Items.Add(typewriterItem);
        view.Items.Add(Toggle("ReadOnly", () => settings.ReadOnly, v => settings.ReadOnly = v, ShortcutCommand.ReadingMode));
        view.Items.Add(new MenuFlyoutSeparator());
        view.Items.Add(Toggle("SidePane", () => settings.SidePaneOpen, v => settings.SidePaneOpen = v, ShortcutCommand.SidePane));
        view.Items.Add(Toggle("StatusBar", () => settings.StatusBarOpen, v => settings.StatusBarOpen = v));
        view.Items.Add(Item("FullScreen", ToggleFullScreen, ShortcutCommand.FullScreen));
        view.Items.Add(new MenuFlyoutSeparator());
        view.Items.Add(Item("NextTab", async () => { if (tabs != null) await tabs.SwitchRelativeAsync(1); }, ShortcutCommand.NextTab));
        view.Items.Add(Item("PreviousTab", async () => { if (tabs != null) await tabs.SwitchRelativeAsync(-1); }, ShortcutCommand.PreviousTab));
        view.Items.Add(Item("LastUsedTab", async () => { if (tabs != null) await tabs.SwitchToLastUsedAsync(); }));
        view.Items.Add(new MenuFlyoutSeparator());
        var theme = new MenuFlyoutSubItem { Text = Loc.Get("Theme") };
        themeMenuItems.Clear();
        foreach (var t in Enum.GetValues<AppTheme>())
        {
            var value = t;
            var item = new RadioMenuFlyoutItem { Text = Loc.Get("Theme" + t), GroupName = "theme" };
            item.Click += (_, _) => { settings.CustomTheme = ""; settings.Theme = value; };
            themeMenuItems.Add((item, null, value));
            theme.Items.Add(item);
        }
        // Themes from the themes folder, in the same group as the built-in ones (see docs/custom-theme.md).
        var customThemes = Services.ThemeFiles.List();
        if (customThemes.Count > 0)
        {
            theme.Items.Add(new MenuFlyoutSeparator());
            foreach (var custom in customThemes)
            {
                var id = custom.Id;
                var item = new RadioMenuFlyoutItem { Text = Services.ThemeFiles.DisplayName(custom, customThemes), GroupName = "theme" };
                // One choice, not two: the theme also sets the built-in one it builds on, so the window, the
                // editor and the theme's own CSS can never end up disagreeing about light or dark.
                item.Click += (_, _) => { settings.Theme = custom.Base; settings.CustomTheme = id; };
                themeMenuItems.Add((item, id, null));
                theme.Items.Add(item);
            }
        }
        theme.Items.Add(new MenuFlyoutSeparator());
        var designer = new MenuFlyoutItem { Text = Loc.Get("ThemeDesigner") };
        designer.Click += async (_, _) =>
        {
            try { await Services.ThemeFiles.OpenDesignerAsync(settings.CustomTheme); }
            catch (Exception ex) { Services.Log.Error("open theme designer", ex); }
        };
        theme.Items.Add(designer);
        // Picks up a theme that was just added or edited, without restarting. Rebuilding the menus has to wait
        // until this click is over: the menu would otherwise be torn down while it is still on screen.
        var themeDoc = new MenuFlyoutItem { Text = Loc.Get("ThemeDocument") };
        themeDoc.Click += (_, _) => OpenThemeDocument();
        theme.Items.Add(themeDoc);
        var reload = new MenuFlyoutItem { Text = Loc.Get("ReloadThemes") };
        reload.Click += (_, _) => DispatcherQueue.TryEnqueue(() => { BuildMenus(); ApplyTheme(post: true); });
        theme.Items.Add(reload);
        view.Items.Add(theme);
        UpdateThemeChecks();
        MainMenu.Items.Add(view);

        var help = new MenuBarItem { Title = Loc.Get("Help") };
        help.Items.Add(Item("About", async () => await ShowAboutAsync()));
        MainMenu.Items.Add(help);
    }

    private Action? historyChangedHandler;
    private MenuFlyoutItem? undoMenuItem;
    private MenuFlyoutItem? redoMenuItem;

    /// <summary>
    /// Greys out undo and redo when there is nothing to undo or redo. The menus are built before the document
    /// exists, so this is called again once it does, and on every rebuild after a language change.
    /// </summary>
    private void WireHistoryItems()
    {
        if (document == null || undoMenuItem == null || redoMenuItem == null) return;
        void Update()
        {
            undoMenuItem.IsEnabled = document.History.Undoable;
            redoMenuItem.IsEnabled = document.History.Redoable;
        }
        Update();
        if (historyChangedHandler != null) document.HistoryChanged -= historyChangedHandler;
        historyChangedHandler = () => DispatcherQueue.TryEnqueue(Update);
        document.HistoryChanged += historyChangedHandler;
    }

    private MenuFlyoutItem Item(string key, Action action, ShortcutCommand? command = null)
    {
        var item = new MenuFlyoutItem { Text = Loc.Get(key) };
        item.Click += (_, _) => action();
        if (command != null) BindShortcut(item, command.Value, action);
        return item;
    }

    /// <summary>Shows the binding on the menu item and registers the matching page accelerator.</summary>
    private void BindShortcut(MenuFlyoutItemBase item, ShortcutCommand command, Action action)
    {
        commandActions[command] = action;
        var shortcut = settings.Shortcuts.Get(command);
        if (item is MenuFlyoutItem menuItem) menuItem.KeyboardAcceleratorTextOverride = shortcut.ToString();
        if (shortcut.ToAccelerator() is not { } accelerator) return;
        // Page-scoped: works while the shell has focus. Inside the editor (a native web view) uno-bridge.js
        // forwards the key press instead — see OnEditorMessage("Shortcut").
        var keyboardAccelerator = new KeyboardAccelerator { Key = accelerator.key, Modifiers = accelerator.modifiers };
        keyboardAccelerator.Invoked += (_, e) => { e.Handled = true; action(); };
        KeyboardAccelerators.Add(keyboardAccelerator);
    }

    private readonly Dictionary<ShortcutCommand, Action> commandActions = new();

    private ToggleMenuFlyoutItem Toggle(string key, Func<bool> get, Action<bool> set, ShortcutCommand? command = null)
    {
        var item = new ToggleMenuFlyoutItem { Text = Loc.Get(key), IsChecked = get() };
        item.Click += (_, _) => set(item.IsChecked);
        void Flip() { set(!get()); item.IsChecked = get(); }
        if (command != null) BindShortcut(item, command.Value, Flip);
        void OnChanged(object? _, System.ComponentModel.PropertyChangedEventArgs __) => DispatcherQueue.TryEnqueue(() => item.IsChecked = get());
        settings.PropertyChanged += OnChanged;
        menuToggleHandlers.Add(OnChanged); // dropped when the menus are rebuilt (see BuildMenus)
        return item;
    }

    private void FillRecent()
    {
        if (recentMenu == null) return;
        recentMenu.Items.Clear();
        if (settings.RecentFiles.Count == 0)
        {
            recentMenu.Items.Add(new MenuFlyoutItem { Text = Loc.Get("NoRecent"), IsEnabled = false });
            return;
        }
        foreach (var path in settings.RecentFiles)
        {
            var p = path;
            recentMenu.Items.Add(new MenuFlyoutItem { Text = Path.GetFileName(p) + "  —  " + Path.GetDirectoryName(p) }.OnClick(async () => { if (tabs != null) await tabs.OpenFileAsync(p); }));
        }
        recentMenu.Items.Add(new MenuFlyoutSeparator());
        recentMenu.Items.Add(Item("ClearRecent", () => settings.RecentFiles = new()));
    }

    /// <summary>
    /// Alt+1..9 for the tabs. They are not bindings the user can change, so they are added straight to the
    /// window rather than hung off a menu entry; the editor forwards the same keys when it has the focus.
    /// </summary>
    private void AddTabNumberAccelerators()
    {
        var last = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Number0, Modifiers = Windows.System.VirtualKeyModifiers.Menu };
        last.Invoked += (sender, e) => { e.Handled = true; if (tabs != null) _ = tabs.SwitchToLastUsedAsync(); };
        KeyboardAccelerators.Add(last);
        for (var n = 1; n <= 9; n++)
        {
            var index = n == 9 ? int.MaxValue : n - 1;
            var accelerator = new KeyboardAccelerator
            {
                Key = (Windows.System.VirtualKey)((int)Windows.System.VirtualKey.Number0 + n),
                Modifiers = Windows.System.VirtualKeyModifiers.Menu,
            };
            accelerator.Invoked += (sender, e) =>
            {
                e.Handled = true;
                if (tabs != null) _ = tabs.SwitchToIndexAsync(index);
            };
            KeyboardAccelerators.Add(accelerator);
        }
    }

    private async Task HandleShortcutAsync(string key, bool ctrl, bool shift, bool alt = false)
    {
        if (tabs == null || document == null) return;
        // Bindings first: the editor forwards the keys the map asked for.
        if (settings.Shortcuts.Find(key, ctrl, shift, alt) is { } command && commandActions.TryGetValue(command, out var action))
        {
            action();
            return;
        }
        switch ((key.ToLowerInvariant(), ctrl, shift))
        {
            case ("s", true, false): await document.SaveAsync(); break;
            case ("s", true, true): await document.SaveAsAsync(); break;
            case ("o", true, false): await OpenFileDialogAsync(); break;
            case ("o", true, true): await OpenFolderDialogAsync(); break;
            case ("n", true, false): await tabs.NewTabAsync(); break;
            case ("w", true, false): await tabs.CloseTabAsync(tabs.ActiveTab); break;
            case ("p", true, false): await PrintAsync(); break;
            case ("f", true, false): ShowFind(true); break;
            case ("f", true, true): OnSearchButtonClick(this, new RoutedEventArgs()); break;
            case ("b", true, true): settings.SidePaneOpen = !settings.SidePaneOpen; break;
            case ("r", true, true): settings.ReadOnly = !settings.ReadOnly; break;
            case ("/", true, false): settings.SourceCode = !settings.SourceCode; break;
            case (",", true, false): await ShowSettingsAsync(); break;
            case ("tab", true, false): await tabs.SwitchRelativeAsync(1); break;
            case ("tab", true, true): await tabs.SwitchRelativeAsync(-1); break;
            // Alt+1..9 picks a tab by position, 9 being the last one however many there are. Alt rather than
            // Ctrl because Ctrl+1..6 sets the heading level in the Windows edition, as it does in MarkText.
            case (_, false, false) when alt && key.Length == 1 && key[0] is >= '1' and <= '9':
                await tabs.SwitchToIndexAsync(key[0] == '9' ? int.MaxValue : key[0] - '1');
                break;
            // Alt+0 goes back to the tab used before this one, so two tabs out of many can be swapped between.
            case ("0", false, false) when alt: await tabs.SwitchToLastUsedAsync(); break;
        }
    }

    // ---- files & folders --------------------------------------------------------------------------------------------

    private async Task OpenFileDialogAsync()
    {
        var path = await PickOpenFileAsync();
        if (path != null && tabs != null) await tabs.OpenFileAsync(path);
    }

    private async Task OpenFolderDialogAsync()
    {
        string? path;
        if (UseBuiltInPicker)
        {
            path = await ShowBuiltInPickerAsync(FilePickerDialog.PickerMode.Folder);
        }
        else
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            InitPicker(picker);
            path = (await picker.PickSingleFolderAsync())?.Path;
        }
        if (path == null) return;
        SetWorkFolder(path, explicitChoice: true);
        settings.SidePaneOpen = true;
        settings.SidePanePage = 0;
        ApplySidePane();
    }

    private bool folderIsExplicit;

    private void SetWorkFolder(string folder, bool explicitChoice = false)
    {
        workFolder = folder;
        folderIsExplicit = explicitChoice || folderIsExplicit;
        settings.LastFolder = folder;
        folderRoot = new FolderItem(folder, true);
        folderRoot.EnsureChildren();
        FileTree.ItemsSource = folderRoot.Children;
        SidePaneTitle.Text = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        SidePaneStatus.Text = folderRoot.Children.Count == 0 ? Loc.Get("NoResults") : "";
    }

    private void OnFileOpened(string path)
    {
        settings.AddRecent(path);
        FillRecent();
        FollowDocumentFolder(path);
    }

    /// <summary>
    /// The file tree follows the document in front of you — switching to a tab from another folder moves the
    /// tree there too, which opening a file has always done. A folder opened on purpose stays put: that is a
    /// workspace the tabs move around in, not a place the tree should be dragged away from.
    /// </summary>
    private void FollowDocumentFolder(string? path)
    {
        if (string.IsNullOrEmpty(path) || folderIsExplicit) return;
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return;
        if (workFolder == null || !dir.StartsWith(workFolder, Services.PathIdentity.Comparison))
            SetWorkFolder(dir);
    }

    private void OnFileTreeItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not FolderItem item || item.IsFolder) return;
        pendingSingleClick?.Cancel();
        var cts = pendingSingleClick = new CancellationTokenSource();
        _ = Task.Delay(250, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            DispatcherQueue.TryEnqueue(async () => { if (tabs != null) await tabs.OpenFileAsync(item.FullPath, preview: true); });
        }, TaskScheduler.Default);
    }

    private async void OnFileTreeDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        pendingSingleClick?.Cancel();
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FolderItem item && !item.IsFolder && tabs != null)
            await tabs.OpenFileAsync(item.FullPath, preview: false);
    }

    private async void OnOpenFolderClick(object sender, RoutedEventArgs e) => await OpenFolderDialogAsync();

    private void OnFileTreeRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var item = (e.OriginalSource as FrameworkElement)?.DataContext as FolderItem;
        var target = item ?? (workFolder != null ? folderRoot : null);
        if (target == null) return;
        var folder = target.IsFolder ? target.FullPath : Path.GetDirectoryName(target.FullPath)!;
        var menu = new MenuFlyout();
        void Add(string key, Action action) { var i = new MenuFlyoutItem { Text = Loc.Get(key) }; i.Click += (_, _) => action(); menu.Items.Add(i); }
        if (!target.IsFolder)
        {
            Add("Open", async () => { if (tabs != null) await tabs.OpenFileAsync(target.FullPath); });
            Add("OpenInNewWindow", () => NewWindow(target.FullPath));
        }
        Add("NewFileHere", async () => await CreateInFolderAsync(folder, file: true));
        Add("NewFolderHere", async () => await CreateInFolderAsync(folder, file: false));
        menu.Items.Add(new MenuFlyoutSeparator());
        if (item != null)
        {
            Add("Rename", async () => await RenameAsync(item));
            Add("Delete", async () => await DeleteAsync(item));
            Add("CopyPath", () => { var p = new DataPackage(); p.SetText(item.FullPath); Clipboard.SetContent(p); });
        }
        Add("RevealInFileManager", () => OpenPath(folder));
        Add("Refresh", () => RefreshTree());
        menu.ShowAt((FrameworkElement)sender, e.GetPosition((UIElement)sender));
        e.Handled = true;
    }

    private void RefreshTree()
    {
        if (workFolder == null) return;
        SetWorkFolder(workFolder, folderIsExplicit);
    }

    private async Task<string?> AskNameAsync(string titleKey, string initial)
    {
        var box = new TextBox { Text = initial, SelectionStart = 0, SelectionLength = Path.GetFileNameWithoutExtension(initial).Length };
        var dialog = new ContentDialog
        {
            Title = Loc.Get(titleKey),
            Content = box,
            PrimaryButtonText = Loc.Get("OK"),
            CloseButtonText = Loc.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = DialogTheme,
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return null;
        var name = box.Text.Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private async Task CreateInFolderAsync(string folder, bool file)
    {
        var name = await AskNameAsync(file ? "NewFileHere" : "NewFolderHere", file ? "untitled.md" : "folder");
        if (name == null) return;
        try
        {
            var path = Path.Combine(folder, name);
            if (file)
            {
                if (!File.Exists(path)) await SafeFile.WriteAllTextAtomicAsync(path, "");
                RefreshTree();
                if (tabs != null) await tabs.OpenFileAsync(path);
            }
            else
            {
                Directory.CreateDirectory(path);
                RefreshTree();
            }
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(Loc.Get("Error"), ex.Message);
        }
    }

    private async Task RenameAsync(FolderItem item)
    {
        var name = await AskNameAsync("Rename", item.Name);
        if (name == null || name == item.Name) return;
        try
        {
            var target = Path.Combine(Path.GetDirectoryName(item.FullPath)!, name);
            if (item.IsFolder) Directory.Move(item.FullPath, target);
            else File.Move(item.FullPath, target);
            // A renamed open document keeps its tab: point it at the new path.
            if (!item.IsFolder && tabs?.FindByPath(item.FullPath) is { } tab) tab.FilePath = target;
            RefreshTree();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(Loc.Get("Error"), ex.Message);
        }
    }

    private async Task DeleteAsync(FolderItem item)
    {
        if (!await ConfirmAsync(Loc.Get("Delete"), Loc.Format("DeleteConfirm", item.Name), Loc.Get("Delete"), Loc.Get("Cancel"))) return;
        try
        {
            if (item.IsFolder) Directory.Delete(item.FullPath, recursive: true);
            else File.Delete(item.FullPath);
            RefreshTree();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(Loc.Get("Error"), ex.Message);
        }
    }

    // ---- side pane width ------------------------------------------------------------------------------------------

    private bool resizingSidePane;
    private double resizeStartX, resizeStartWidth;

    private void OnSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        resizingSidePane = true;
        resizeStartX = e.GetCurrentPoint(this).Position.X;
        resizeStartWidth = SidePaneColumn.Width.Value;
        SidePaneSplitter.CapturePointer(e.Pointer);
    }

    private void OnSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!resizingSidePane) return;
        var width = Math.Clamp(resizeStartWidth + (e.GetCurrentPoint(this).Position.X - resizeStartX), 160, Math.Max(200, ActualWidth - 320));
        SidePaneColumn.Width = new GridLength(width);
    }

    private void OnSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!resizingSidePane) return;
        resizingSidePane = false;
        SidePaneSplitter.ReleasePointerCapture(e.Pointer);
        settings.SidePaneWidth = SidePaneColumn.Width.Value;
    }

    private void OnSplitterPointerEntered(object sender, PointerRoutedEventArgs e) =>
        ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);

    private void OnSplitterPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!resizingSidePane) ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
    }

    private async void OnOutlineItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is OutlineItem item) await Post("ScrollTo", new { slug = item.Slug });
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        searchCts?.Cancel();
        var query = SearchBox.Text.Trim();
        var root = workFolder;
        if (query.Length == 0 || root == null) { SearchResults.ItemsSource = null; SearchStatus.Text = root == null ? Loc.Get("NoFolder") : ""; return; }
        var cts = searchCts = new CancellationTokenSource();
        var results = new System.Collections.ObjectModel.ObservableCollection<SearchResultItem>();
        SearchResults.ItemsSource = results;
        SearchStatus.Text = "…";
        _ = Task.Run(async () =>
        {
            await Task.Delay(300, cts.Token);
            try
            {
                var (files, hits) = FolderSearch.Run(root, query, item => DispatcherQueue.TryEnqueue(() => { if (!cts.IsCancellationRequested) results.Add(item); }), cts.Token);
                DispatcherQueue.TryEnqueue(() => { if (!cts.IsCancellationRequested) SearchStatus.Text = hits == 0 ? Loc.Get("NoResults") : Loc.Format("Results", files, hits); });
            }
            catch (OperationCanceledException) { }
        }, cts.Token);
    }

    private async void OnSearchResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SearchResultItem item || tabs == null) return;
        var query = SearchBox.Text.Trim();
        await tabs.OpenFileAsync(item.FullPath, preview: true);
        await Task.Delay(400);
        ShowFind(true, query);
    }

    // ---- tabs ------------------------------------------------------------------------------------------------------

    private void UpdateTabBar()
    {
        if (tabs == null) return;
        TabBar.Visibility = tabs.Tabs.Count > 1 || settings.AlwaysShowTabBar ? Visibility.Visible : Visibility.Collapsed;
        suppressTabSelection = true;
        try { TabBar.SelectedItem = tabs.ActiveTab; }
        finally { suppressTabSelection = false; }
        UpdateTitle();
    }

    private async void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressTabSelection || tabs == null) return;
        if (TabBar.SelectedItem is DocumentTab tab && tab != tabs.ActiveTab) await tabs.SwitchToAsync(tab);
    }

    private async void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is DocumentTab tab && tabs != null) await tabs.CloseTabAsync(tab);
    }

    private async void OnAddTabClick(TabView sender, object args)
    {
        if (tabs != null) await tabs.NewTabAsync();
    }

    // ---- find (the bar itself lives in the page: uno-bridge.js) ------------------------------------------------

    private void ShowFind(bool show, string? text = null, bool replace = false) =>
        _ = show ? Post("ShowFind", new { value = text, opt = SearchOptions(), replace }) : Post("HideFind", null);

    /// <summary>Search options the in-page find bar passes to the editor (see Settings → Find).</summary>
    private object SearchOptions() => new
    {
        searchIsCaseSensitive = settings.FindCaseSensitive,
        searchIsWholeWord = settings.FindWholeWord,
        searchIsRegexp = settings.FindRegex,
    };

    // ---- side pane, theme, settings -----------------------------------------------------------------------------------

    private void OnSideNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (updatingSideNav || args.SelectedItem is not NavigationViewItem item) return;
        settings.SidePanePage = int.Parse((string)item.Tag);
        ApplySidePane();
        if (settings.SidePanePage == 1) RefreshOutline();
    }

    private bool updatingSideNav;

    /// <summary>Folder search takes over the pane (and comes back with its close button), as on Windows.</summary>
    private void OnSearchButtonClick(object sender, RoutedEventArgs e)
    {
        settings.SidePaneOpen = true;
        searchOpen = true;
        ApplySidePane();
        // Focus only sticks once the panel has been realized and laid out.
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(80);
            SearchBox.Focus(FocusState.Programmatic);
        });
    }

    private void OnSearchCloseClick(object sender, RoutedEventArgs e)
    {
        searchOpen = false;
        ApplySidePane();
    }

    private bool searchOpen;

    private void ApplyStatusBar() => StatusBar.Visibility = settings.StatusBarOpen ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Reading mode ignores every keystroke, which looks like the editor has stopped responding unless
    /// something says otherwise. The badge says it and is the way out; the title says it too, because the
    /// status bar can be switched off.
    /// </summary>
    private void ApplyReadOnlyBadge()
    {
        if (ReadOnlyBadge == null) return;
        ReadOnlyBadge.Content = Loc.Get("ReadOnly");
        ReadOnlyBadge.Visibility = settings.ReadOnly ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnLeaveReadOnlyClick(object sender, RoutedEventArgs e) => settings.ReadOnly = false;

    private void ApplySidePane()
    {
        var open = settings.SidePaneOpen;
        SidePane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SidePaneSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SidePaneColumn.Width = open ? new GridLength(Math.Max(180, settings.SidePaneWidth)) : new GridLength(0);
        var page = Math.Clamp(settings.SidePanePage, 0, 1);
        updatingSideNav = true;
        try
        {
            SideNav.SelectedItem = page == 0 ? FilesNavItem : OutlineNavItem;
        }
        finally
        {
            updatingSideNav = false;
        }
        SearchPanel.Visibility = searchOpen ? Visibility.Visible : Visibility.Collapsed;
        SideNav.Visibility = searchOpen ? Visibility.Collapsed : Visibility.Visible;
        FileTree.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        OutlineList.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        SidePaneTitle.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        OpenFolderButton.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (page == 0 && workFolder == null) SidePaneStatus.Text = Loc.Get("NoFolder");
    }

    private bool IsDarkTheme => settings.Theme switch
    {
        AppTheme.Light => false,
        AppTheme.Dark or AppTheme.Black => true,
        _ => Application.Current.RequestedTheme == ApplicationTheme.Dark,
    };

    /// <summary>The built-in theme in force: a custom theme names the one it builds on.</summary>
    private AppTheme EffectiveTheme
    {
        get
        {
            var custom = Services.ThemeFiles.Find(settings.CustomTheme);
            return custom?.Base ?? settings.Theme;
        }
    }

    private object ThemePayload()
    {
        var effective = EffectiveTheme;
        var name = effective == AppTheme.Black ? "Black" : (effective == AppTheme.Dark || (effective == AppTheme.System && IsDarkTheme)) ? "Dark" : "Light";
        var bg = name switch { "Black" => (0, 0, 0), "Dark" => (32, 32, 32), _ => (249, 249, 249) };
        var accent = ParseAccent(Services.ThemeFiles.Find(settings.CustomTheme)?.Accent) ?? (0, 120, 212);
        // Dictionary keys keep their case (the editor reads background.R/G/B/A but accentColor.r/g/b/a).
        return new { theme = name, accentColor = new { r = accent.Item1, g = accent.Item2, b = accent.Item3, a = 1 }, background = new Dictionary<string, int> { ["R"] = bg.Item1, ["G"] = bg.Item2, ["B"] = bg.Item3, ["A"] = 1 } };
    }

    /// <summary>
    /// Paints the window around the editor from the theme. A theme that says nothing about these keeps the
    /// colours of the built-in theme it builds on, and clearing them puts those colours back.
    /// </summary>
    private void ApplyShellColours(Services.CustomTheme? theme)
    {
        var background = Brush(theme?.Background);
        var surface = Brush(theme?.Surface) ?? background;
        var border = Brush(theme?.Border);
        // A theme may paint the panels and say nothing about text. The built-in theme's text colour is then the
        // one in force, and a light panel under a dark theme (or the other way round) leaves it unreadable — so
        // the text colour follows the panel it sits on when the theme does not name one.
        var foreground = Brush(theme?.Foreground) ?? Readable(theme?.Surface ?? theme?.Background);

        // The panels carry their built-in colour in the markup, so "no colour from the theme" has to put that
        // colour back rather than clear the property — the probes hold the right one for the current theme.
        var panelDefault = ThemePanelProbe.Background;
        var borderDefault = ThemePanelProbe.BorderBrush;
        Set(this, background ?? ThemePageProbe.Background, ApplyTo.Background);
        Set(SidePane, surface ?? panelDefault, ApplyTo.Background);
        Set(SearchPanel, surface ?? panelDefault, ApplyTo.Background);
        Set(StatusBar, surface ?? panelDefault, ApplyTo.Background);
        Set(SidePane, border ?? borderDefault, ApplyTo.Border);
        // These two have no colour of their own in the markup: clearing gives them their control style back.
        Set(MainMenu, surface, ApplyTo.Background);
        Set(TabBar, surface, ApplyTo.Background);
        ApplyTabColours(theme);
        Set(StatusBar, foreground, ApplyTo.Foreground);
        Set(MainMenu, foreground, ApplyTo.Foreground);
        Set(SidePane, foreground, ApplyTo.Foreground);
    }

    /// <summary>
    /// The editor CSS cannot reach the native TabView. Inactive tabs use the panel surface and the selected tab
    /// joins the editor background; hover, press, close and add-button colours are derived from the same palette.
    /// </summary>
    private void ApplyTabColours(Services.CustomTheme? theme)
    {
        var editor = Brush(theme?.Background);
        var surface = Brush(theme?.Surface) ?? editor;
        var foreground = Brush(theme?.Foreground) ?? Readable(theme?.Surface ?? theme?.Background);
        var border = Brush(theme?.Border);
        var accent = Brush(theme?.Accent);
        var inactiveForeground = WithOpacity(foreground, .68);
        var disabledForeground = WithOpacity(foreground, .40);
        var hover = Blend(surface, foreground, .08);
        var pressed = Blend(surface, foreground, .14);
        var buttonHover = WithOpacity(foreground, .10);
        var buttonPressed = WithOpacity(foreground, .16);

        SetTabBrush("TabViewItemHeaderBackground", surface);
        SetTabBrush("TabViewItemHeaderBackgroundSelected", editor ?? surface);
        SetTabBrush("TabViewItemHeaderBackgroundPointerOver", hover);
        SetTabBrush("TabViewItemHeaderBackgroundPressed", pressed);
        SetTabBrush("TabViewItemHeaderBackgroundDisabled", surface);
        SetTabBrush("TabViewItemHeaderForeground", inactiveForeground);
        SetTabBrush("TabViewItemHeaderForegroundSelected", foreground);
        SetTabBrush("TabViewItemHeaderForegroundPointerOver", foreground);
        SetTabBrush("TabViewItemHeaderForegroundPressed", foreground);
        SetTabBrush("TabViewItemHeaderForegroundDisabled", disabledForeground);
        SetTabBrush("TabViewItemIconForeground", inactiveForeground);
        SetTabBrush("TabViewItemIconForegroundSelected", foreground);
        SetTabBrush("TabViewItemIconForegroundPointerOver", foreground);
        SetTabBrush("TabViewItemIconForegroundPressed", foreground);
        SetTabBrush("TabViewItemSeparator", border);
        SetTabBrush("TabViewBorderBrush", border);
        SetTabBrush("TabViewItemBorderBrush", border);
        SetTabBrush("TabViewSelectedItemBorderBrush", accent ?? border);
        SetTabResource("TabViewSelectedItemBorderThickness", accent == null ? null : new Thickness(0, 2, 0, 0));

        SetTabButtonBrushes("TabViewButton", surface, hover, pressed, foreground, disabledForeground, border);
        SetTabButtonBrushes("TabViewScrollButton", surface, hover, pressed, foreground, disabledForeground, border);
        SetTabBrush("TabViewItemHeaderCloseButtonForeground", inactiveForeground);
        SetTabBrush("TabViewItemHeaderCloseButtonForegroundPointerOver", foreground);
        SetTabBrush("TabViewItemHeaderCloseButtonForegroundPressed", foreground);
        SetTabBrush("TabViewItemHeaderPointerOverCloseButtonForeground", foreground);
        SetTabBrush("TabViewItemHeaderPressedCloseButtonForeground", foreground);
        SetTabBrush("TabViewItemHeaderSelectedCloseButtonForeground", foreground);
        SetTabBrush("TabViewItemHeaderDisabledCloseButtonForeground", disabledForeground);
        var transparent = surface == null ? null : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        SetTabBrush("TabViewItemHeaderCloseButtonBackground", transparent);
        SetTabBrush("TabViewItemHeaderPointerOverCloseButtonBackground", buttonHover);
        SetTabBrush("TabViewItemHeaderPressedCloseButtonBackground", buttonPressed);
        SetTabBrush("TabViewItemHeaderSelectedCloseButtonBackground", transparent);
        SetTabBrush("TabViewItemHeaderDisabledCloseButtonBackground", transparent);
    }

    private void SetTabButtonBrushes(string prefix, Brush? background, Brush? hover, Brush? pressed,
        Brush? foreground, Brush? disabledForeground, Brush? border)
    {
        SetTabBrush(prefix + "Background", background);
        SetTabBrush(prefix + "BackgroundPointerOver", hover);
        SetTabBrush(prefix + "BackgroundPressed", pressed);
        SetTabBrush(prefix + "BackgroundDisabled", background);
        SetTabBrush(prefix + "Foreground", foreground);
        SetTabBrush(prefix + "ForegroundPointerOver", foreground);
        SetTabBrush(prefix + "ForegroundPressed", foreground);
        SetTabBrush(prefix + "ForegroundDisabled", disabledForeground);
        SetTabBrush(prefix + "BorderBrush", border);
        SetTabBrush(prefix + "BorderBrushPointerOver", border);
        SetTabBrush(prefix + "BorderBrushPressed", border);
        SetTabBrush(prefix + "BorderBrushDisabled", border);
    }

    private void SetTabBrush(string key, Brush? brush) => SetTabResource(key, brush);

    private void SetTabResource(string key, object? value)
    {
        if (value == null) TabBar.Resources.Remove(key);
        else TabBar.Resources[key] = value;
    }

    private static Brush? WithOpacity(Brush? brush, double opacity)
    {
        if (brush is not SolidColorBrush solid) return brush;
        var colour = solid.Color;
        return new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(255 * opacity), colour.R, colour.G, colour.B));
    }

    private static Brush? Blend(Brush? background, Brush? foreground, double amount)
    {
        if (background is not SolidColorBrush bg || foreground is not SolidColorBrush fg) return background;
        byte Mix(byte from, byte to) => (byte)Math.Round(from + (to - from) * amount);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255,
            Mix(bg.Color.R, fg.Color.R), Mix(bg.Color.G, fg.Color.G), Mix(bg.Color.B, fg.Color.B)));
    }

    private enum ApplyTo { Background, Foreground, Border }

    private static void Set(FrameworkElement? element, Brush? brush, ApplyTo what)
    {
        if (element == null) return;
        // A null brush clears the local value, so the resource from the built-in theme applies again.
        var property = what switch
        {
            ApplyTo.Background when element is Panel => Panel.BackgroundProperty,
            ApplyTo.Background when element is Control => Control.BackgroundProperty,
            ApplyTo.Foreground when element is Control => Control.ForegroundProperty,
            ApplyTo.Foreground when element is TextBlock => TextBlock.ForegroundProperty,
            ApplyTo.Border when element is Control => Control.BorderBrushProperty,
            ApplyTo.Border when element is Grid => Grid.BorderBrushProperty,
            _ => null,
        };
        if (property == null) return;
        // Clearing goes back to the style, where the theme resource lives, so the built-in light/dark colours
        // come back and keep following the system. (Setting these in the markup instead would make the clear
        // wipe the colour altogether — which is how the menu bar ended up a white strip in the dark themes.)
        if (brush == null) element.ClearValue(property);
        else element.SetValue(property, brush);
    }

    /// <summary>Black or white, whichever can be read on the given colour; null when there is no colour.</summary>
    private static Brush? Readable(string? colour)
    {
        var rgb = ParseAccent(colour);
        if (rgb == null) return null;
        var luminance = (0.299 * rgb.Value.Item1 + 0.587 * rgb.Value.Item2 + 0.114 * rgb.Value.Item3) / 255;
        var tone = luminance > 0.55 ? (byte)26 : (byte)240;
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, tone, tone, tone));
    }

    private static Brush? Brush(string? colour)
    {
        var rgb = ParseAccent(colour);
        return rgb == null ? null : new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)rgb.Value.Item1, (byte)rgb.Value.Item2, (byte)rgb.Value.Item3));
    }

    /// <summary>"#268bd2" or "#26d" from a theme's metadata.</summary>
    private static (int, int, int)? ParseAccent(string? value)
    {
        var text = value?.Trim().TrimStart('#');
        if (string.IsNullOrEmpty(text)) return null;
        try
        {
            if (text.Length == 3)
                return (Convert.ToInt32($"{text[0]}{text[0]}", 16), Convert.ToInt32($"{text[1]}{text[1]}", 16), Convert.ToInt32($"{text[2]}{text[2]}", 16));
            if (text.Length >= 6)
                return (Convert.ToInt32(text[..2], 16), Convert.ToInt32(text.Substring(2, 2), 16), Convert.ToInt32(text.Substring(4, 2), 16));
        }
        catch
        {
            // a theme with a broken accent keeps the default one
        }
        return null;
    }

    private void ApplyTheme(bool post)
    {
        var effective = EffectiveTheme;
        var theme = Services.ThemeFiles.Find(settings.CustomTheme);
        ApplyAccentResources(theme);
        if (window?.Content is FrameworkElement root)
            root.RequestedTheme = effective == AppTheme.System ? ElementTheme.Default
                : effective == AppTheme.Light ? ElementTheme.Light : ElementTheme.Dark;
        ApplyShellColours(theme);
        if (post && window?.Content is FrameworkElement shown)
        {
            // A template looks its theme resources up when it is applied and again only when the element's theme
            // changes; brushes changed since (the tab colours, the accent) are not looked up again. The selected tab
            // then kept the previous theme's colour, and a list in a dialog showed the new accent in one state and
            // the old one in the next. Going through the other theme and back makes every element look them up.
            Retheme(shown);
            RepaintOpenDialog();
        }
        ApplyEditorWindowBackground();
        if (!post) return;
        _ = Post("ThemeChanged", ThemePayload());
        // the theme's own CSS rides on the settings channel, next to the user's custom CSS
        _ = Post("SettingsChanged", new Dictionary<string, object?> { ["themeCss"] = Services.ThemeFiles.Read(settings.CustomTheme) });
    }

    private void OnSettingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        var name = e.PropertyName ?? "";
        DispatcherQueue.TryEnqueue(() =>
        {
            if (AppSettings.IsEditorOption(name))
            {
                var value = typeof(AppSettings).GetProperty(name)!.GetValue(settings);
                _ = Post("SettingsChanged", new Dictionary<string, object?> { [char.ToLowerInvariant(name[0]) + name[1..]] = value });
            }
            switch (name)
            {
                case nameof(AppSettings.Theme):
                case nameof(AppSettings.CustomTheme): ApplyTheme(post: true); UpdateThemeChecks(); break;
                case nameof(AppSettings.SidePaneOpen) or nameof(AppSettings.SidePanePage) or nameof(AppSettings.SidePaneWidth):
                    ApplySidePane();
                    if (settings.SidePaneOpen && settings.SidePanePage == 1) RefreshOutline();
                    break;
                case nameof(AppSettings.AlwaysShowTabBar): UpdateTabBar(); break;
                case nameof(AppSettings.StatusBarOpen): ApplyStatusBar(); break;
                case nameof(AppSettings.ReadOnly): ApplyReadOnlyBadge(); UpdateTitle(); break;
                case nameof(AppSettings.WordCountMethod): lastTocJson = null; break;
                case nameof(AppSettings.RecentFiles): FillRecent(); break;
                case nameof(AppSettings.Language): Loc.Apply(settings.Language); ApplyStrings(); BuildMenus(); UpdateTitle(); _ = PostContextMenuStrings(); break;
                case nameof(AppSettings.Shortcuts): BuildMenus(); _ = PostShortcutMap(); break;
            }
        });
    }

    private void ApplyStrings()
    {
        FilesNavItem.Content = Loc.Get("Files");
        OutlineNavItem.Content = Loc.Get("Outline");
        ToolTipService.SetToolTip(SearchButton, Loc.Get("SearchInFolder"));
        SearchBox.PlaceholderText = Loc.Get("SearchPlaceholder");
        OpenFolderButton.Content = Loc.Get("OpenFolder");
        ApplyReadOnlyBadge();
    }

    /// <summary>
    /// Dialogs follow the theme in force, custom ones included: a dark theme with the app set to light would
    /// otherwise open a light settings dialog over a dark window.
    /// </summary>
    private ElementTheme DialogTheme => EffectiveTheme switch
    {
        AppTheme.System => IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light,
        AppTheme.Light => ElementTheme.Light,
        _ => ElementTheme.Dark,
    };

    /// <summary>The settings dialog, while it is up: the shortcut that opens it closes it again.</summary>
    private SettingsDialog? settingsDialog;

    /// <summary>
    /// Shows a dialog and gives the keyboard back to the editor when it closes: a dialog takes the keyboard into the
    /// app's own window, and without this keys went nowhere until the reader clicked into the document.
    /// </summary>
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        openDialogs++;
        Services.WebViewCaret.Release(EditorView, NativeWindow());
        try { return await Services.DialogKeys.CloseOnEscape(dialog).ShowAsync(); }
        finally
        {
            openDialogs--;
            Services.WebViewCaret.Focus(EditorView);
        }
    }

    private async Task ShowSettingsAsync()
    {
        if (settingsDialog != null)
        {
            settingsDialog.Hide();
            settingsDialog = null;
            return;
        }
        var dialog = new SettingsDialog(settings) { XamlRoot = XamlRoot, RequestedTheme = DialogTheme };
        PaintFromTheme(dialog);
        // A dialog takes the keyboard with it, so the shortcut has to be on the dialog as well for the second
        // press to close what the first one opened.
        if (settings.Shortcuts.Get(ShortcutCommand.Settings).ToAccelerator() is { } accelerator)
        {
            var close = new KeyboardAccelerator { Key = accelerator.key, Modifiers = accelerator.modifiers };
            close.Invoked += (sender, e) => { e.Handled = true; dialog.Hide(); };
            dialog.KeyboardAccelerators.Add(close);
        }
        settingsDialog = dialog;
        try
        {
            await ShowDialogAsync(dialog);
        }
        finally
        {
            if (settingsDialog == dialog) settingsDialog = null;
        }
    }

    private async Task InsertTableAsync()
    {
        var rows = new NumberBox { Header = "Rows", Value = 3, Minimum = 1, Maximum = 50, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var cols = new NumberBox { Header = "Columns", Value = 3, Minimum = 1, Maximum = 20, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var dialog = new ContentDialog { Title = Loc.Get("Table"), Content = new StackPanel { Spacing = 8, Children = { rows, cols } }, PrimaryButtonText = Loc.Get("OK"), CloseButtonText = Loc.Get("Cancel"), XamlRoot = XamlRoot, RequestedTheme = DialogTheme };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
            await Post("InsertTable", new { rows = (int)rows.Value, columns = (int)cols.Value });
    }

    /// <summary>
    /// Reads an HTML file and hands it to the editor, which turns it into Markdown and puts it in the current
    /// document as an ordinary edit — so it can be undone and has to be saved.
    /// </summary>
    private async Task ImportHtmlAsync()
    {
        if (document == null) return;
        string? path;
        if (UseBuiltInPicker)
            path = await ShowBuiltInPickerAsync(FilePickerDialog.PickerMode.OpenFile, null, new[] { ".html", ".htm" });
        else
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".html");
            picker.FileTypeFilter.Add(".htm");
            InitPicker(picker);
            path = (await picker.PickSingleFileAsync())?.Path;
        }
        if (path == null) return;
        try
        {
            var text = await File.ReadAllTextAsync(path);
            await Post("ImportFile", new { type = "html", text });
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(Loc.Get("ImportHtml"), ex.Message);
        }
    }

    /// <summary>The document model asking for a line in the status bar (see IHostUi).</summary>
    public void ShowStatus(string message) => DispatcherQueue.TryEnqueue(() => SetStatus(message));

    private async Task ExportHtmlAsync()
    {
        if (document == null) return;
        var suggested = Path.GetFileNameWithoutExtension(document.FileName) + ".html";
        string? path;
        if (UseBuiltInPicker)
        {
            path = await ShowBuiltInPickerAsync(FilePickerDialog.PickerMode.SaveFile, suggested, new[] { ".html", ".htm" });
        }
        else
        {
            var picker = new FileSavePicker { SuggestedFileName = suggested };
            picker.FileTypeChoices.Add("HTML", new List<string> { ".html" });
            InitPicker(picker);
            path = (await picker.PickSaveFileAsync())?.Path;
        }
        if (path == null) return;
        await Post("Export", new { type = "html", context = new { filePath = path }, basePath = document.BasePath, title = Path.GetFileNameWithoutExtension(document.FileName), options = new { } });
    }

    private readonly Dictionary<string, Func<string, Task>> exportContinuations = new();

    /// <summary>Renders the document to a temporary HTML file and then does something else with it.</summary>
    private async Task ExportThroughHtmlAsync(Func<string, Task> then)
    {
        if (document == null) return;
        var path = Path.Combine(Path.GetTempPath(), $"typedown-export-{Guid.NewGuid():N}.html");
        lock (exportContinuations) exportContinuations[path] = then;
        await Post("Export", new { type = "html", context = new { filePath = path, print = true }, basePath = document.BasePath, title = Path.GetFileNameWithoutExtension(document.FileName), options = new { } });
    }

    /// <summary>Writes the document to a PDF through WebKitGTK's printer, with no dialog in the way.</summary>
    private async Task ExportPdfAsync()
    {
        if (document == null) return;
        var suggested = Path.GetFileNameWithoutExtension(document.FileName) + ".pdf";
        string? target;
        if (UseBuiltInPicker)
            target = await ShowBuiltInPickerAsync(FilePickerDialog.PickerMode.SaveFile, suggested, new[] { ".pdf" });
        else
            target = await PickSaveFileAsync(suggested);
        if (target == null) return;
        if (!target.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) target += ".pdf";
        if (!WebKitExport.Available)
        {
            await ShowErrorAsync(Loc.Get("ExportPdf"), Loc.Get("PdfUnavailable"));
            return;
        }
        SetStatus(Loc.Get("Exporting"));
        await ExportThroughHtmlAsync(async html =>
        {
            var ok = await WebKitExport.ExportPdfAsync(html, target!);
            try { File.Delete(html); } catch { }
            if (ok)
            {
                SetStatus(Loc.Format("Exported", target!));
                if (settings.OpenFolderAfterExport) OpenContainingFolder(target!);
            }
            else
            {
                await ShowErrorAsync(Loc.Get("ExportPdf"), Loc.Get("PdfFailed"));
            }
        });
    }

    /// <summary>Writes the document to a PNG: the whole page in one picture, however long it is.</summary>
    private async Task ExportImageAsync()
    {
        if (document == null) return;
        var suggested = Path.GetFileNameWithoutExtension(document.FileName) + ".png";
        string? target;
        if (UseBuiltInPicker)
            target = await ShowBuiltInPickerAsync(FilePickerDialog.PickerMode.SaveFile, suggested, new[] { ".png" });
        else
            target = await PickSaveFileAsync(suggested);
        if (target == null) return;
        if (!target.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) target += ".png";
        if (!WebKitExport.Available)
        {
            await ShowErrorAsync(Loc.Get("ExportImage"), Loc.Get("PdfUnavailable"));
            return;
        }
        SetStatus(Loc.Get("Exporting"));
        await ExportThroughHtmlAsync(async html =>
        {
            var ok = await WebKitExport.ExportImageAsync(html, target!);
            try { File.Delete(html); } catch { }
            if (ok)
            {
                SetStatus(Loc.Format("Exported", target!));
                if (settings.OpenFolderAfterExport) OpenContainingFolder(target!);
            }
            else
            {
                await ShowErrorAsync(Loc.Get("ExportImage"), Loc.Get("ImageFailed"));
            }
        });
    }

    /// <summary>
    /// Prints through WebKitGTK's own print dialog. Where that is not available the document is opened in the
    /// browser instead, whose print dialog can do the same job.
    /// </summary>
    private async Task PrintAsync()
    {
        if (document == null) return;
        await ExportThroughHtmlAsync(async html =>
        {
            if (WebKitExport.Available && await WebKitExport.PrintAsync(html))
            {
                try { File.Delete(html); } catch { }
                return;
            }
            OpenPath(html); // the browser's print dialog does the printing / "save as PDF"
            SetStatus(Loc.Get("PrintOpened"));
        });
    }

    private async Task ShareToHedgeDocAsync()
    {
        if (document == null) return;
        if (string.IsNullOrWhiteSpace(settings.HedgeDocServer))
        {
            await ShowErrorAsync(Loc.Get("ShareHedgeDoc"), Loc.Get("NotConfigured"));
            await ShowSettingsAsync();
            return;
        }
        try
        {
            // HedgeDoc 1.x cannot update a note over HTTP, so re-sharing an unchanged document hands back the
            // previous links and a changed one asks before creating a new note (and a new link).
            if (!await document.FlushContentAsync())
            {
                await ShowErrorAsync(Loc.Get("Error"), Loc.Get("EditorNotResponding"));
                return;
            }
            // Local pictures are files on this computer: the note's readers would not see them.
            if (automationWindow != null && (await new ImageBatchUpload(settings, automationWindow).LocalImagesOfDocumentAsync()).Count is var local and > 0)
            {
                var ask = new ContentDialog { Title = Loc.Get("ShareHedgeDoc"), Content = new TextBlock { Text = string.Format(Loc.Get("UploadHedgeDocPrompt"), local), TextWrapping = TextWrapping.Wrap }, PrimaryButtonText = Loc.Get("UploadFirst"), SecondaryButtonText = Loc.Get("ShareAnyway"), CloseButtonText = Loc.Get("Cancel"), XamlRoot = XamlRoot, RequestedTheme = DialogTheme };
                var answer = await ShowDialogAsync(ask);
                if (answer == ContentDialogResult.None) return;
                if (answer == ContentDialogResult.Primary && !await UploadLocalImagesAsync()) return;
                if (!await document.FlushContentAsync()) return;
            }
            var markdown = document.Markdown;
            var hash = SafeFile.Hash(markdown);
            var previous = HedgeDocShareMemory.Get(document.FilePath);
            if (previous != null)
            {
                var previousResult = new HedgeDocShareResult { NoteUrl = previous.NoteUrl, PublishedUrl = previous.PublishedUrl };
                if (previous.ContentHash == hash)
                {
                    await ShowShareResultAsync(previousResult, Loc.Get("HedgeDocUnchanged"));
                    return;
                }
                var dialog = new ContentDialog { Title = Loc.Get("Shared"), Content = new TextBlock { Text = Loc.Get("HedgeDocChangedPrompt"), TextWrapping = TextWrapping.Wrap }, PrimaryButtonText = Loc.Get("ReUpload"), SecondaryButtonText = Loc.Get("UseOldLink"), CloseButtonText = Loc.Get("Cancel"), XamlRoot = XamlRoot, RequestedTheme = DialogTheme };
                var choice = await ShowDialogAsync(dialog);
                if (choice == ContentDialogResult.Secondary) { await ShowShareResultAsync(previousResult, null); return; }
                if (choice != ContentDialogResult.Primary) return;
            }
            SetStatus("HedgeDoc…");
            var result = await HedgeDocService.ShareAsync(settings.HedgeDocServer, markdown, settings.HedgeDocEmail, settings.HedgeDocPassword, settings.HedgeDocPublishReadOnly);
            HedgeDocShareMemory.Set(document.FilePath, result, hash);
            await ShowShareResultAsync(result, null);
            SetStatus(result.ShareUrl);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(Loc.Get("Error"), ex.Message);
        }
    }

    /// <summary>Shows the HedgeDoc links (read-only + editable) with copy / open-in-browser, optionally under a note.</summary>
    private async Task ShowShareResultAsync(HedgeDocShareResult result, string? note)
    {
        var panel = new StackPanel { Spacing = 8, MinWidth = 420 };
        if (note != null) panel.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap });
        if (result.PublishedUrl != null) { panel.Children.Add(new TextBlock { Text = Loc.Get("ReadOnlyLink") }); panel.Children.Add(new TextBox { Text = result.PublishedUrl, IsReadOnly = true }); }
        panel.Children.Add(new TextBlock { Text = Loc.Get("EditLink") });
        panel.Children.Add(new TextBox { Text = result.NoteUrl, IsReadOnly = true });
        if (result.PublishedUrl == null) panel.Children.Add(new TextBlock { Text = Loc.Get("EditableWarning"), Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog { Title = Loc.Get("Shared"), Content = panel, PrimaryButtonText = Loc.Get("CopyLink"), SecondaryButtonText = Loc.Get("OpenInBrowser"), CloseButtonText = Loc.Get("Close"), XamlRoot = XamlRoot, RequestedTheme = DialogTheme };
        var choice = await ShowDialogAsync(dialog);
        if (choice == ContentDialogResult.Primary) { var p = new DataPackage(); p.SetText(result.ShareUrl); Clipboard.SetContent(p); }
        else if (choice == ContentDialogResult.Secondary) await Launcher.LaunchUriAsync(new Uri(result.ShareUrl));
    }

    // ---- window ----------------------------------------------------------------------------------------------------

    private void HookWindowClosing()
    {
        try
        {
            if (window?.AppWindow is { } appWindow)
                appWindow.Closing += (_, args) =>
                {
                    if (closing) return;
                    args.Cancel = true;
                    DispatcherQueue.TryEnqueue(async () => await ExitAsync());
                };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Typedown.Uno] AppWindow.Closing unavailable: {ex.Message}");
        }
    }

    /// <summary>
    /// Closes this window. <paramref name="quit"/> (File > Exit) also ends the program when this is its last window,
    /// which closing the window alone no longer does on macOS (Services.MacKeepRunning).
    /// </summary>
    private async Task ExitAsync(bool quit = false)
    {
        if (closing) return;
        if (tabs != null && !await tabs.AskToSaveAllAsync()) return;
        closing = true;
        tabs?.SaveSession(workFolder);
        await Task.WhenAll(settings.FlushAsync(), SessionMemory.FlushAsync(), CursorMemory.FlushAsync(), HedgeDocShareMemory.FlushAsync());
        document?.Dispose();
        // Closing the window is enough: the app exits once the last one is gone (except on macOS, see above).
        var last = App.Windows.Count <= 1;
        if (window != null) window.Close();
        else Application.Current.Exit();
        if (quit && last && Services.MacKeepRunning.Active) Application.Current.Exit();
    }

    /// <summary>Opens another window, optionally with a file already loaded.</summary>
    private void NewWindow(string? file = null) => App.CreateWindow(file);

    private void RegisterForAutomation()
    {
        automationWindow = new Automation.AutomationWindow
        {
            Document = document!,
            Tabs = tabs!,
            StartupReady = automationStartup.Task,
            IsVisible = () => window?.Visible ?? true,
            Activate = () => window?.Activate(),
            Window = () => window,
            EditorSize = () => (EditorView.ActualWidth, EditorView.ActualHeight),
            SetConnected = connected => { automationConnected = connected; UpdateTitle(); },
            ShowNotice = notice => { automationNotice = notice; UpdateTitle(); },
            Notice = () => automationNotice,
        };
        Automation.AutomationWindows.Register(automationWindow, DispatcherQueue);
        Automation.AutomationRuntime.Initialize(settings);
    }

    private void UpdateTitle()
    {
        var title = document?.Title ?? "Typedown";
        if (settings.ReadOnly) title += " \u00b7 " + Loc.Get("ReadOnly");
        // Neither can be turned off by a client: the reader always sees that a program is connected or just wrote.
        if (automationNotice != null) title += " \u00b7 " + automationNotice;
        else if (automationConnected) title += " \u00b7 " + Loc.Get("AutomationConnected");
        if (window != null) window.Title = title;
        // Uno publishes the title as Latin-1 and, under a window manager, not at all after the window is up;
        // non-ASCII titles need UTF-8, and each window gets its own. Pass the full title (with the reading-mode
        // suffix) so it reaches the native title bar too, not just document.Title.
        PublishNativeChrome(title);
        StatusText.Text = document?.FilePath ?? Loc.Get("Untitled");
    }

    /// <summary>
    /// About: what this build is, and the versions a bug report needs — the editor bundle, Uno, the .NET
    /// runtime and the system web engine all change what the same document looks like. One button puts the
    /// whole block on the clipboard so it can be pasted into an issue.
    /// </summary>
    private async Task ShowAboutAsync()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = Loc.Get("AboutText"), TextWrapping = TextWrapping.Wrap });
        var details = new StackPanel { Spacing = 2 };
        foreach (var (label, value) in Services.AppInfo.Lines())
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var name = new TextBlock { Text = label, Opacity = 0.7 };
            var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetColumn(text, 1);
            row.Children.Add(name);
            row.Children.Add(text);
            details.Children.Add(row);
        }
        panel.Children.Add(details);
        panel.Children.Add(new TextBlock { Text = Loc.Get("AboutIssue"), Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new HyperlinkButton { Content = Loc.Get("ReportIssue"), NavigateUri = new Uri("https://github.com/flintt/Typedown-Uno/issues"), Padding = new Thickness(0) });
        var dialog = new ContentDialog
        {
            Title = Loc.Get("About"),
            Content = panel,
            PrimaryButtonText = Loc.Get("CopyInfo"),
            CloseButtonText = Loc.Get("OK"),
            XamlRoot = XamlRoot,
            RequestedTheme = DialogTheme,
        };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
        {
            var package = new DataPackage();
            package.SetText(Services.AppInfo.Summary());
            Clipboard.SetContent(package);
            SetStatus(Loc.Get("Copied"));
        }
    }

    /// <summary>
    /// The X11 window backing this page. Looking it up can fail while the window is still coming up (and it
    /// always failed on a normal desktop before the search became recursive), so it is retried until found.
    /// </summary>
    private IntPtr NativeWindow()
    {
        if (nativeWindow == IntPtr.Zero) nativeWindow = Services.X11Window.FindByTitle(options.Marker);
        return nativeWindow;
    }

    private DateTime lastXamlWheel;

    /// <summary>
    /// Scrolling with the wheel over the shell (the file tree, the outline, a dialog) does nothing in a VNC
    /// session: Uno reads the wheel from XInput2 scroll valuators, which a pointer driven through XTEST does
    /// not have. <see cref="Services.X11Window.ListenForWheel"/> reports the legacy wheel buttons instead, and
    /// what arrives here is applied to the scroll viewer under the pointer — but only while Uno itself is not
    /// delivering wheel events, so a normal mouse keeps its own (smooth) scrolling and never scrolls twice.
    /// </summary>
    private void HookWheelFallback()
    {
        AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, _) => lastXamlWheel = DateTime.UtcNow), true);
        Services.X11Window.WheelScrolled += OnNativeWheel;
    }

    private void OnNativeWheel(IntPtr source, int x, int y, int notches)
    {
        if (source != nativeWindow) return;
        DispatcherQueue.TryEnqueue(async () =>
        {
            // Give Uno's own wheel handling a moment: when it delivers the notch (a mouse with a scroll axis)
            // this fallback stays out of the way. The suppression is per notch rather than permanent, so one
            // stray wheel event cannot switch the fallback off for the rest of the session.
            await Task.Delay(60);
            if ((DateTime.UtcNow - lastXamlWheel).TotalMilliseconds < 400) return;
            var scale = XamlRoot?.RasterizationScale ?? 1;
            if (scale <= 0) scale = 1;
            var point = new Windows.Foundation.Point(x / scale, y / scale);
            if (WheelOverTabs(point, notches)) return;
            var scroller = ScrollerAt(point);
            // three lines per notch, the usual step for a ScrollViewer
            scroller?.ChangeView(null, scroller.VerticalOffset - notches * 48, null, true);
        });
    }

    private void OnTabBarWheel(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(TabBar).Properties.MouseWheelDelta;
        if (tabs == null || delta == 0) return;
        e.Handled = true;
        _ = tabs.SwitchRelativeAsync(delta > 0 ? -1 : 1);
    }

    /// <summary>
    /// handledEventsToo: once there are more tabs than fit, the strip's own scroll viewer takes the wheel and
    /// marks it handled, so a plain handler never sees it and the wheel only slid the strip sideways.
    /// </summary>
    private void HookTabBarWheel() =>
        TabBar.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnTabBarWheel), true);

    /// <summary>
    /// The wheel over the tab strip moves between tabs rather than scrolling anything: up goes to the tab on
    /// the left, down to the one on the right. This is the fallback path for X11, where the wheel does not
    /// reach XAML at all (see <see cref="OnNativeWheel"/>); <see cref="OnTabBarWheel"/> is the same thing for
    /// the platforms where it does.
    /// </summary>
    private bool WheelOverTabs(Windows.Foundation.Point point, int notches)
    {
        if (tabs == null || TabBar.Visibility != Visibility.Visible || notches == 0) return false;
        if (XamlRoot?.Content is not UIElement reference || !Contains(TabBar, point, reference)) return false;
        _ = tabs.SwitchRelativeAsync(notches > 0 ? -1 : 1);
        return true;
    }

    /// <summary>
    /// The innermost scrollable ScrollViewer under a point. Hit testing through FindElementsInHostCoordinates
    /// comes back empty here, so the visual tree is walked instead and each scroll viewer's bounds are mapped
    /// into window coordinates. Open dialogs and flyouts live in their own popups and are checked first.
    /// </summary>
    private ScrollViewer? ScrollerAt(Windows.Foundation.Point point)
    {
        if (XamlRoot?.Content is not UIElement reference) return null;
        var roots = new List<DependencyObject>();
        foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot))
            if (popup.Child != null) roots.Add(popup.Child);
        roots.Add(this);
        foreach (var root in roots)
        {
            var found = DeepestScroller(root, point, reference);
            if (found != null) return found;
        }
        return null;
    }

    private static ScrollViewer? DeepestScroller(DependencyObject root, Windows.Foundation.Point point, UIElement reference)
    {
        ScrollViewer? best = null;
        var bestDepth = -1;
        void Walk(DependencyObject node, int depth)
        {
            if (depth > 32) return;
            // A hidden branch keeps its layout: the file tree and the outline sit on top of each other and only
            // one is visible, so without this the hidden one's scroll viewer could win the hit test and the
            // wheel would appear to do nothing.
            if (node is UIElement { Visibility: Visibility.Collapsed }) return;
            if (node is ScrollViewer { ScrollableHeight: > 0 } scroller && depth > bestDepth && Contains(scroller, point, reference))
            {
                best = scroller;
                bestDepth = depth;
            }
            var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++) Walk(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i), depth + 1);
        }
        Walk(root, 0);
        return best;
    }

    private static bool Contains(FrameworkElement element, Windows.Foundation.Point point, UIElement reference)
    {
        try
        {
            var origin = element.TransformToVisual(reference).TransformPoint(new Windows.Foundation.Point(0, 0));
            return point.X >= origin.X && point.X <= origin.X + element.ActualWidth
                && point.Y >= origin.Y && point.Y <= origin.Y + element.ActualHeight;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Publishes the UTF-8 title and, once, the app icon on this window.</summary>
    private void PublishNativeChrome(string? title = null)
    {
        var handle = NativeWindow();
        if (handle == IntPtr.Zero) return;
        Services.X11Window.SetTitle(handle, title ?? document?.Title ?? "Typedown");
        // The handle can arrive late, so the wheel listener is started here rather than once at load time.
        Services.X11Window.ListenForWheel(handle);
        if (nativeIconApplied) return;
        nativeIconApplied = true;
        Services.X11Window.SetIcon(handle, Path.Combine(AppContext.BaseDirectory, "Assets", "typedown.png"));
    }

    private static void OpenContainingFolder(string path) => OpenPath(Path.GetDirectoryName(path));

    private static void OpenPath(string? path)
    {
        if (path == null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Services.Log.Error($"open {path} failed", ex);
        }
    }

    private void SetStatus(string text)
    {
        _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text);
        Services.Log.Write(text);
    }

    // ---- IHostUi ------------------------------------------------------------------------------------------------------

    private static void InitPicker(object picker)
    {
#if WINDOWS
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
#endif
    }

    /// <summary>
    /// Linux gets the app's own chooser: the platform pickers there go through the XDG desktop portal, which is
    /// absent on many desktops, and then the dialog simply never appears. Windows and macOS keep the native one.
    /// </summary>
    private bool fullScreen;

    /// <summary>Full screen through the window presenter; not every platform backend has one.</summary>
    private void ToggleFullScreen()
    {
        try
        {
            var appWindow = window?.AppWindow;
            if (appWindow == null) return;
            fullScreen = !fullScreen;
            appWindow.SetPresenter(fullScreen
                ? Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen
                : Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
        }
        catch (Exception ex)
        {
            Services.Log.Error("full screen", ex);
        }
    }

    private static bool UseBuiltInPicker => OperatingSystem.IsLinux();

    private string PickerStartDirectory => workFolder
        ?? (document?.FilePath != null ? Path.GetDirectoryName(document.FilePath) : null)
        ?? settings.LastFolder
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private async Task<string?> ShowBuiltInPickerAsync(FilePickerDialog.PickerMode mode, string? suggestedName = null, IEnumerable<string>? extensions = null)
    {
        var dialog = new FilePickerDialog(mode, PickerStartDirectory, suggestedName, extensions) { XamlRoot = XamlRoot, RequestedTheme = DialogTheme };
        await ShowDialogAsync(dialog);
        return dialog.SelectedPath;
    }

    public async Task<string?> PickOpenFileAsync()
    {
        if (UseBuiltInPicker) return await ShowBuiltInPickerAsync(FilePickerDialog.PickerMode.OpenFile, null, FolderItem.MarkdownExtensions);
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var ext in FolderItem.MarkdownExtensions) picker.FileTypeFilter.Add(ext);
        InitPicker(picker);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task<string?> PickSaveFileAsync(string? suggestedName)
    {
        if (UseBuiltInPicker) return await ShowBuiltInPickerAsync(FilePickerDialog.PickerMode.SaveFile, suggestedName ?? "Untitled.md");
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = suggestedName ?? "Untitled.md" };
        picker.FileTypeChoices.Add("Markdown", new List<string> { ".md" });
        picker.FileTypeChoices.Add("Text", new List<string> { ".txt" });
        InitPicker(picker);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    public async Task<DocumentViewModel.AskResult> AskSaveAsync(string fileName)
    {
        var dialog = new ContentDialog
        {
            Title = Loc.Get("SaveChanges"),
            Content = Loc.Format("HasUnsaved", fileName),
            PrimaryButtonText = Loc.Get("Save"),
            SecondaryButtonText = Loc.Get("DontSave"),
            CloseButtonText = Loc.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot, RequestedTheme = DialogTheme,
        };
        return await ShowDialogAsync(dialog) switch
        {
            ContentDialogResult.Primary => DocumentViewModel.AskResult.Yes,
            ContentDialogResult.Secondary => DocumentViewModel.AskResult.No,
            _ => DocumentViewModel.AskResult.Cancel,
        };
    }

    public async Task<bool> ConfirmAsync(string title, string message, string yes, string no)
    {
        var dialog = new ContentDialog { Title = title, Content = message, PrimaryButtonText = yes, CloseButtonText = no, XamlRoot = XamlRoot, RequestedTheme = DialogTheme };
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    /// <summary>
    /// An image inserted while its action is Upload: its web address, or - when the upload fails - the error is shown
    /// and the picture goes in as it would without uploading (copied to the images folder).
    /// </summary>
    private async Task<string> UploadOrKeepAsync(string path, Func<string>? keepOnFailure = null)
    {
        try
        {
            SetStatus(Loc.Get("UploadImagesTitle") + "…");
            return (await ImageUploader.UploadAsync(settings, path, document?.FilePath)).url;
        }
        catch (Exception ex)
        {
            Services.Log.Error("upload image", ex);
            await ShowErrorAsync(Loc.Get("Error"), ex.Message);
            return keepOnFailure?.Invoke() ?? ImagePaths.PlaceImage(path, document?.FilePath, settings);
        }
    }

    /// <summary>
    /// File > Upload local images, with a progress dialog that can cancel and a dialog with the result. False when
    /// nothing was uploaded because there is no configuration or the person cancelled.
    /// </summary>
    private async Task<bool> UploadLocalImagesAsync()
    {
        if (automationWindow == null || document == null) return false;
        var title = Loc.Get("UploadImagesTitle");
        try
        {
            var batch = new ImageBatchUpload(settings, automationWindow);
            if ((await batch.LocalImagesOfDocumentAsync()).Count == 0)
            {
                await ShowErrorAsync(title, Loc.Get("UploadNone"));
                return true;
            }
            if (!ImageUploader.IsConfigured(settings))
            {
                var open = new ContentDialog { Title = title, Content = new TextBlock { Text = Loc.Get("UploadNoConfig"), TextWrapping = TextWrapping.Wrap }, PrimaryButtonText = Loc.Get("UploadOpenSettings"), CloseButtonText = Loc.Get("Cancel"), XamlRoot = XamlRoot, RequestedTheme = DialogTheme };
                if (await ShowDialogAsync(open) == ContentDialogResult.Primary) await ShowSettingsAsync();
                return false;
            }

            var status = new TextBlock { Text = string.Format(Loc.Get("UploadProgress"), 1, "…"), TextWrapping = TextWrapping.Wrap };
            var bar = new ProgressBar { IsIndeterminate = true, Margin = new Thickness(0, 12, 0, 0), MinWidth = 320 };
            var panel = new StackPanel();
            panel.Children.Add(status);
            panel.Children.Add(bar);
            var progressDialog = new ContentDialog { Title = title, Content = panel, CloseButtonText = Loc.Get("Cancel"), XamlRoot = XamlRoot, RequestedTheme = DialogTheme };
            using var cancel = new CancellationTokenSource();
            var finished = false;
            // Hidden before it has opened, a dialog would open anyway and wait for a click: the uploads (instant when
            // the history knows every picture) start once it is on screen.
            var opened = new TaskCompletionSource();
            progressDialog.Opened += (_, _) => opened.TrySetResult();
            var shown = ShowDialogAsync(progressDialog);
            await Task.WhenAny(opened.Task, Task.Delay(3000));
            _ = shown.ContinueWith(_ => { if (!finished) cancel.Cancel(); }, TaskScheduler.FromCurrentSynchronizationContext());
            var progress = new Progress<(int done, int total)>(p =>
            {
                status.Text = string.Format(Loc.Get("UploadProgress"), Math.Min(p.done + 1, p.total), p.total);
                bar.IsIndeterminate = false;
                bar.Maximum = Math.Max(p.total, 1);
                bar.Value = p.done;
            });
            ImageBatchUpload.Result result;
            try { result = await batch.RunAsync(progress, cancel.Token); }
            finally
            {
                finished = true;
                progressDialog.Hide();
                await shown;
            }
            if (result.NoConfig) return false;

            var summary = string.Format(Loc.Get(result.Cancelled ? "UploadCancelled" : "UploadDone"), result.Uploaded, result.Files);
            if (result.Reused > 0) summary += " " + string.Format(Loc.Get("UploadReused"), result.Reused);
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap });
            if (result.Failures.Count > 0)
            {
                content.Children.Add(new TextBlock { Text = Loc.Get("UploadNotUploaded"), TextWrapping = TextWrapping.Wrap });
                content.Children.Add(new ScrollViewer
                {
                    MaxHeight = 240,
                    Content = new TextBlock { Text = string.Join("\n", result.Failures.Select(f => $"{f.Address}: {f.Reason}")), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                });
            }
            await ShowDialogAsync(new ContentDialog { Title = title, Content = content, CloseButtonText = Loc.Get("OK"), XamlRoot = XamlRoot, RequestedTheme = DialogTheme });
            SetStatus(summary);
            return !result.Cancelled;
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(Loc.Get("Error"), ex.Message);
            return false;
        }
    }

    public async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new ContentDialog { Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, CloseButtonText = Loc.Get("OK"), XamlRoot = XamlRoot, RequestedTheme = DialogTheme };
        await ShowDialogAsync(dialog);
    }
}

internal static class MenuExtensions
{
    public static MenuFlyoutItem OnClick(this MenuFlyoutItem item, Action action)
    {
        item.Click += (_, _) => action();
        return item;
    }
}
