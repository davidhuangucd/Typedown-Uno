using Microsoft.UI.Xaml.Media;
using Typedown.Automation;
using Typedown.Uno.Services;
using Typedown.Uno.ViewModels;

namespace Typedown.Uno.Automation;

/// <summary>
/// The Uno application behind the automation document methods: the same contract as the Windows edition's
/// WindowsAutomationHost (docs/automation-api-spec.md in the Windows repository). Every call runs on the dispatcher of
/// the window that holds the document; nothing here shows a dialog.
/// </summary>
public sealed class UnoAutomationHost : IAutomationHost, IViewHost
{
    /// <summary>The classifier version of the editor bundle (services/normalization.ts).</summary>
    public const int EditorClassifierVersion = 3;
    public const int QueryTimeoutMs = 2000;
    public const int StartupTimeoutMs = 30000;

    private readonly DocumentEditCoordinator coordinator;

    public UnoAutomationHost(string version)
    {
        Version = version;
        coordinator = new DocumentEditCoordinator(EditorClassifierVersion);
    }

    public string Version { get; }
    public int ClassifierVersion => EditorClassifierVersion;

    private static WindowRegistry<AutomationWindow> Registry => AutomationWindows.Registry;

    private static AutomationException DocumentNotFound(string documentId) =>
        new(AutomationErrorKind.document_not_found, "The document is closed or the id is not valid.", new Dictionary<string, object?> { ["documentId"] = documentId });

    private static AutomationException NotActive() =>
        new(AutomationErrorKind.editor_not_ready, "Only the document shown in its window can do this in this version; focus it first.",
            new Dictionary<string, object?> { ["reason"] = "notActive" });

    private static AutomationException SyncTimeout() =>
        new(AutomationErrorKind.content_sync_timeout, "Could not confirm the editor's latest text.");

    /// <summary>Runs work on a window once its startup documents are in place.</summary>
    private static async Task<T> OnWindow<T>(string windowId, Func<AutomationWindow, Task<T>> work) => await (await Registry.OnWindowAsync(windowId, async w =>
    {
        if (await Task.WhenAny(w.StartupReady, Task.Delay(StartupTimeoutMs)) != w.StartupReady)
            throw new AutomationException(AutomationErrorKind.editor_not_ready, "The window has not finished starting.");
        return await work(w);
    }));

    private static async Task<(RegisteredWindow<AutomationWindow> window, DocumentTab tab)> Find(string documentId)
    {
        var found = await AutomationWindows.FindDocumentAsync(documentId ?? "");
        return found ?? throw DocumentNotFound(documentId ?? "");
    }

    private static DocumentInfo Info(AutomationWindow w, string windowId, DocumentTab tab)
    {
        var active = w.IsActive(tab);
        var path = active ? w.Document.FilePath : tab.FilePath;
        var format = active ? w.Document.FileFormat : tab.FileFormat;
        return new DocumentInfo
        {
            DocumentId = tab.DocumentId,
            WindowId = windowId,
            Path = path,
            Title = path == null ? tab.Title : Path.GetFileName(path),
            Revision = active ? w.Document.Revision : tab.Revision,
            Saved = active ? w.Document.Saved : tab.Saved,
            Active = active,
            LineEnding = (format?.LineEnding) switch { "\r\n" => "crlf", "\r" => "cr", _ => "lf" },
        };
    }

    public async Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken cancellationToken)
    {
        var result = new List<WindowInfo>();
        foreach (var window in Registry.Snapshot())
        {
            try
            {
                result.Add(await Registry.OnWindowAsync(window.WindowId, w => new WindowInfo
                {
                    WindowId = window.WindowId,
                    Active = w.IsVisible() && window == Registry.Snapshot().LastOrDefault(),
                    DocumentCount = w.Tabs.Tabs.Count,
                    ActiveDocumentId = w.Tabs.ActiveTab?.DocumentId,
                }));
            }
            catch (AutomationException e) when (e.Kind == AutomationErrorKind.window_not_found) { }
        }
        return result;
    }

    public Task FocusWindowAsync(string windowId, CancellationToken cancellationToken) =>
        Registry.OnWindowAsync(windowId, w => { w.Activate(); return true; });

    public async Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(string? windowId, CancellationToken cancellationToken)
    {
        var windows = windowId == null ? Registry.Snapshot().Select(w => w.WindowId).ToList() : new List<string> { windowId };
        var result = new List<DocumentInfo>();
        foreach (var id in windows)
        {
            try
            {
                result.AddRange(await Registry.OnWindowAsync(id, w => w.Tabs.Tabs.Select(t => Info(w, id, t)).ToList()));
            }
            catch (AutomationException e) when (e.Kind == AutomationErrorKind.window_not_found && windowId == null) { }
        }
        return result;
    }

    public async Task<DocumentSnapshot> GetDocumentAsync(string documentId, bool latest, CancellationToken cancellationToken)
    {
        var (window, tab) = await Find(documentId);
        return await OnWindow(window.WindowId, async w =>
        {
            var active = w.IsActive(tab);
            if (latest && active && !(await w.Document.WaitForLoadAsync(UnoAutomationDocument.ReloadTimeoutMs) && await w.Document.FlushContentAsync(UnoAutomationDocument.FlushTimeoutMs)))
                throw SyncTimeout();
            if (!w.Tabs.Tabs.Contains(tab)) throw DocumentNotFound(documentId);
            var text = active ? w.Document.Markdown : tab.Markdown ?? "";
            var hash = DocumentText.ContentHash(text);
            var normalization = NormalizationInfo.NotEvaluated(hash, EditorClassifierVersion);
            if (active && w.IsActive(tab) && await w.Document.QueryNormalizationAsync(QueryTimeoutMs) is { } report
                && report["sourceHash"]?.GetValue<string>() == hash && report["normalization"] is System.Text.Json.Nodes.JsonObject n)
            {
                var normalized = n["normalizedHash"];
                normalization = new NormalizationInfo(
                    n["pendingNormalization"]?.GetValue<string>() ?? PendingNormalization.Unknown, hash,
                    normalized != null && normalized.GetValueKind() == System.Text.Json.JsonValueKind.String ? normalized.GetValue<string>() : null,
                    (n["reasons"] as System.Text.Json.Nodes.JsonArray)?.Select(r => r?.GetValue<string>() ?? "").ToList() ?? new List<string>(),
                    n["classifierVersion"]?.GetValue<int>() ?? EditorClassifierVersion);
            }
            return new DocumentSnapshot { Info = Info(w, window.WindowId, tab), Text = text, IsCurrent = latest || !active, Normalization = normalization };
        });
    }

    private static string TargetWindow(string? windowId)
    {
        if (windowId != null) return windowId;
        var windows = Registry.Snapshot();
        if (windows.Count == 0) throw new AutomationException(AutomationErrorKind.window_not_found, "Typedown has no open window.");
        return windows[^1].WindowId;
    }

    public async Task<DocumentInfo> OpenDocumentAsync(string path, string? windowId, Reveal reveal, CancellationToken cancellationToken)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception) { throw Params.Invalid("path", "invalid"); }
        // Checked here so the open flow never reaches its error dialog for a missing file.
        if (!File.Exists(full)) throw Params.Invalid("path", "notFound", "No file at that path.");
        foreach (var window in Registry.Snapshot())
        {
            DocumentInfo? open = null;
            try { open = await Registry.OnWindowAsync(window.WindowId, w => w.Tabs.FindByPath(full) is { } t ? Info(w, window.WindowId, t) : null); }
            catch (AutomationException e) when (e.Kind == AutomationErrorKind.window_not_found) { }
            if (open != null)
            {
                if (reveal == Reveal.Document) await FocusDocumentAsync(open.DocumentId, cancellationToken);
                return open;
            }
        }
        var target = TargetWindow(windowId);
        return await OnWindow(target, async w =>
        {
            if (!await w.Tabs.OpenFileAsync(full))
                throw new AutomationException(AutomationErrorKind.editor_not_ready, "The file could not be opened.");
            if (reveal == Reveal.Document) w.Activate();
            return Info(w, target, w.Tabs.ActiveTab);
        });
    }

    public Task<DocumentInfo> CreateDocumentAsync(string? windowId, Reveal reveal, CancellationToken cancellationToken)
    {
        var target = TargetWindow(windowId);
        return OnWindow(target, async w =>
        {
            if (w.Document.IsBlank)
            {
                // The blank tab is reused, as a person's "new" would; it becomes a new document (document-identity.json).
                var id = Guid.NewGuid().ToString("N");
                w.Document.BecomeNewDocument(id);
                w.Tabs.ActiveTab.DocumentId = id;
            }
            else
            {
                await w.Tabs.NewTabAsync();
            }
            if (reveal == Reveal.Document) w.Activate();
            return Info(w, target, w.Tabs.ActiveTab);
        });
    }

    public async Task<DocumentInfo> FocusDocumentAsync(string documentId, CancellationToken cancellationToken)
    {
        var (window, tab) = await Find(documentId);
        return await OnWindow(window.WindowId, async w =>
        {
            if (!w.IsActive(tab)) await w.Tabs.SwitchToAsync(tab);
            if (!w.IsActive(tab)) throw new AutomationException(AutomationErrorKind.editor_not_ready, "The tab could not be shown (the editor did not answer).");
            w.Activate();
            return Info(w, window.WindowId, tab);
        });
    }

    public async Task<EditResult> EditDocumentAsync(string documentId, EditRequest request, Reveal reveal, CancellationToken cancellationToken)
    {
        if (reveal == Reveal.Document) await FocusDocumentAsync(documentId, cancellationToken);
        var (window, tab) = await Find(documentId);
        return await OnWindow(window.WindowId, w => coordinator.EditAsync(new UnoAutomationDocument(w, tab), request, cancellationToken));
    }

    public async Task<(DocumentInfo info, string contentHash)> SaveDocumentAsync(string documentId, long? baseRevision, CancellationToken cancellationToken)
    {
        var (window, tab) = await Find(documentId);
        return await OnWindow(window.WindowId, async w =>
        {
            if (!w.IsActive(tab)) throw NotActive();
            var doc = w.Document;
            if (doc.FilePath == null) throw new AutomationException(AutomationErrorKind.path_required, "The document has no file yet; the API does not open a Save As dialog.");
            if (!(await doc.WaitForLoadAsync(UnoAutomationDocument.ReloadTimeoutMs) && await doc.FlushContentAsync(UnoAutomationDocument.FlushTimeoutMs))) throw SyncTimeout();
            if (baseRevision != null && baseRevision != doc.Revision)
                throw new AutomationException(AutomationErrorKind.revision_conflict, "The document changed since baseRevision.", new Dictionary<string, object?> { ["revision"] = doc.Revision });
            if (!await doc.SaveToExistingPathAsync())
                throw new AutomationException(AutomationErrorKind.save_failed, "Saving failed; the document stays unsaved.", new Dictionary<string, object?> { ["revision"] = doc.Revision });
            return (Info(w, window.WindowId, tab), DocumentText.ContentHash(doc.Markdown));
        });
    }

    public async Task<EditResult> UndoAsync(string documentId, long baseRevision, bool redo, bool save, Reveal reveal, CancellationToken cancellationToken)
    {
        if (reveal == Reveal.Document) await FocusDocumentAsync(documentId, cancellationToken);
        var (window, tab) = await Find(documentId);
        return await OnWindow(window.WindowId, async w =>
        {
            if (!w.IsActive(tab)) throw NotActive();
            var doc = w.Document;
            if (!(await doc.WaitForLoadAsync(UnoAutomationDocument.ReloadTimeoutMs) && await doc.FlushContentAsync(UnoAutomationDocument.FlushTimeoutMs))) throw SyncTimeout();
            if (doc.Revision != baseRevision)
                throw new AutomationException(AutomationErrorKind.revision_conflict, "The document changed since baseRevision.", new Dictionary<string, object?> { ["revision"] = doc.Revision });
            doc.History.CommitPending();
            if (redo) await doc.RedoAsync(); else await doc.UndoAsync();
            var saved = doc.Saved;
            if (save)
            {
                if (doc.FilePath == null) throw new AutomationException(AutomationErrorKind.path_required, "The document has no file yet.");
                if (!await doc.SaveToExistingPathAsync())
                    throw new AutomationException(AutomationErrorKind.save_failed, "Undone, but saving failed.", new Dictionary<string, object?> { ["applied"] = true, ["revision"] = doc.Revision });
                saved = true;
            }
            var hash = DocumentText.ContentHash(doc.Markdown);
            return new EditResult(Guid.NewGuid().ToString("N"), doc.Revision, hash, saved, NormalizationInfo.NotEvaluated(hash, EditorClassifierVersion));
        });
    }

    public async Task<Presentation> AwaitPresentationAsync(string documentId, int timeoutMs, CancellationToken cancellationToken)
    {
        var (window, tab) = await Find(documentId);
        return await OnWindow(window.WindowId, async w =>
        {
            var deadline = Task.Delay(timeoutMs);
            var frames = w.IsActive(tab) ? w.Document.AwaitPageFramesAsync(timeoutMs) : Task.FromResult(false);
            var rendered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<object> onRendering = (_, _) => rendered.TrySetResult(true);
            CompositionTarget.Rendering += onRendering;
            try
            {
                await Task.WhenAny(rendered.Task, deadline);
                var pageFramesPassed = await frames;
                return new Presentation
                {
                    WindowVisible = w.IsVisible(),
                    TabActive = w.IsActive(tab),
                    PageFramesPassed = pageFramesPassed,
                    HostRenderPassed = rendered.Task.IsCompleted,
                };
            }
            finally
            {
                CompositionTarget.Rendering -= onRendering;
            }
        });
    }

    public async Task DiscardDocumentAsync(string documentId, CancellationToken cancellationToken)
    {
        var (window, tab) = await Find(documentId);
        await OnWindow(window.WindowId, async w =>
        {
            if (w.Tabs.Tabs.Count > 1) await w.Tabs.CloseTabAsync(tab);
            coordinator.Forget(documentId);
            return true;
        });
    }

    private static ViewState ReadView(AutomationWindow w)
    {
        var settings = AppSettings.Current;
        var appWindow = w.Window()?.AppWindow;
        return new ViewState
        {
            Mode = settings.SourceCode ? "source" : settings.ReadOnly ? "reading" : "visual",
            SidePaneOpen = settings.SidePaneOpen,
            SidePanePage = settings.SidePanePage == 1 ? "outline" : "files",
            StatusBar = settings.StatusBarOpen,
            FocusMode = settings.FocusMode,
            Typewriter = settings.Typewriter,
            X = appWindow?.Position.X ?? 0,
            Y = appWindow?.Position.Y ?? 0,
            Width = appWindow?.Size.Width ?? 0,
            Height = appWindow?.Size.Height ?? 0,
            Maximized = appWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Maximized },
        };
    }

    public const int SettleTimeoutMs = 3000;

    /// <summary>
    /// Until the page shows the new layout: the web view is a native window of its own on X11, moved and resized after
    /// Uno's layout, and its page answers frames while it still has the old size (a screenshot then shows the side pane
    /// over stale editor pixels). A resized window is laid out only once the window manager confirms the new size, so
    /// an early probe finds the old page matching the old layout: done when the page's viewport has been the size the
    /// view is laid out at, and that size unchanged, for several probes in a row.
    /// </summary>
    private static async Task SettleAsync(AutomationWindow w)
    {
        const int steadyProbes = 4;
        var deadline = DateTime.UtcNow.AddMilliseconds(SettleTimeoutMs);
        var steady = 0;
        (double, double)? last = null;
        while (true)
        {
            var viewport = await w.Document.PageViewportAsync(QueryTimeoutMs);
            var (width, height) = w.EditorSize();
            var matches = viewport is { } v && Math.Abs(v.Width - width) <= 2 && Math.Abs(v.Height - height) <= 2;
            steady = matches && last == (width, height) ? steady + 1 : matches ? 1 : 0;
            last = (width, height);
            if (steady >= steadyProbes) return;
            if (DateTime.UtcNow > deadline)
            {
                Console.Error.WriteLine($"[Typedown.Uno] automation: the page viewport {viewport} did not reach {width}x{height}");
                return;
            }
            await Task.Delay(50);
        }
    }

    public Task<ViewState> GetViewAsync(string windowId, CancellationToken cancellationToken) =>
        Registry.OnWindowAsync(windowId, ReadView);

    /// <summary>
    /// Mode, side pane, status bar, focus and typewriter are the app's own settings (the menu changes the same ones,
    /// and they are remembered); bounds belong to this window. A mode switch waits until the editor shows the document
    /// again, and runs between - never during - automation edits of the window's active document.
    /// </summary>
    public async Task<ViewState> SetViewAsync(string windowId, ViewChange change, CancellationToken cancellationToken)
    {
        var documentId = await Registry.OnWindowAsync(windowId, w => w.Tabs.ActiveTab?.DocumentId ?? "");
        return await coordinator.ExclusiveAsync(documentId, () => OnWindow(windowId, async w =>
        {
            var settings = AppSettings.Current;
            var modeChanged = change.Mode != null && change.Mode != ReadView(w).Mode;
            if (modeChanged)
            {
                // Setting one on turns the other off (AppSettings); visual turns both off.
                if (change.Mode == "source") settings.SourceCode = true;
                else if (change.Mode == "reading") settings.ReadOnly = true;
                else { settings.SourceCode = false; settings.ReadOnly = false; }
            }
            if (change.SidePanePage != null) settings.SidePanePage = change.SidePanePage == "outline" ? 1 : 0;
            if (change.SidePaneOpen is bool open) settings.SidePaneOpen = open;
            if (change.StatusBar is bool statusBar) settings.StatusBarOpen = statusBar;
            if (change.FocusMode is bool focus) settings.FocusMode = focus;
            if (change.Typewriter is bool typewriter) settings.Typewriter = typewriter;
            if ((change.X != null || change.Y != null || change.Width != null || change.Height != null) && w.Window()?.AppWindow is { } appWindow)
            {
                if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: not Microsoft.UI.Windowing.OverlappedPresenterState.Restored } presenter)
                    presenter.Restore();
                if (change.Width != null || change.Height != null)
                    appWindow.Resize(new Windows.Graphics.SizeInt32 { Width = change.Width ?? appWindow.Size.Width, Height = change.Height ?? appWindow.Size.Height });
                if (change.X != null || change.Y != null)
                    appWindow.Move(new Windows.Graphics.PointInt32 { X = change.X ?? appWindow.Position.X, Y = change.Y ?? appWindow.Position.Y });
            }
            if (modeChanged && !await w.Document.WaitForLoadAsync(UnoAutomationDocument.ReloadTimeoutMs))
                throw new AutomationException(AutomationErrorKind.content_sync_timeout, "The editor did not show the document again after the mode switch.");
            await SettleAsync(w);
            return ReadView(w);
        }), cancellationToken);
    }
}
