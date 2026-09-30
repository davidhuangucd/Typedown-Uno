using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Typedown.Uno.Services;

namespace Typedown.Uno.ViewModels;

/// <summary>
/// The live document in the editor (ported from Typedown's FileViewModel/EditorViewModel): path, content, saved
/// state, load handshake, caret/scroll memory, external change detection, atomic save. Tabs snapshot and restore
/// this state (<see cref="Capture"/>/<see cref="Restore"/>). UI interactions are injected through <see cref="IHostUi"/>.
/// </summary>
public sealed partial class DocumentViewModel : INotifyPropertyChanged, IDisposable
{
    public enum AskResult { Yes, No, Cancel }

    public interface IHostUi
    {
        Task<string?> PickOpenFileAsync();
        Task<string?> PickSaveFileAsync(string? suggestedName);
        Task<AskResult> AskSaveAsync(string fileName);
        Task<bool> ConfirmAsync(string title, string message, string yes, string no);
        Task ShowErrorAsync(string title, string message);
        void ShowStatus(string message);
    }

    public static readonly string DefaultMarkdown = "\n";

    private readonly EditorTransport transport;
    private readonly IHostUi ui;
    private readonly AppSettings settings;
    private FileSystemWatcher? watcher;
    private bool handlingExternalChange;
    private readonly object watcherScheduleSync = new();
    private readonly SemaphoreSlim watcherCheckLock = new(1, 1);
    private readonly SemaphoreSlim saveLock = new(1, 1);
    private CancellationTokenSource? watcherCheckCts;
    private int watcherCheckRequested;
    private CancellationTokenSource? autoSaveCts;
    private ulong? recoveredDiskHash;
    private bool initialRecoveryDone;

    public string DocumentId { get; private set; } = Guid.NewGuid().ToString("N");

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Raised after a file was opened (path, preview); the shell updates recent files / the tree root.</summary>
    public event Action<string>? FileOpened;
    public event Action<Func<Task>>? RunOnUi;

    public void ShowStatus(string message) => ui.ShowStatus(message);

    private string? filePath;
    public string? FilePath { get => filePath; private set { filePath = value; OnPropertyChanged(); OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(FileName)); } }

    /// <summary>The byte shape the open file was read in; a save puts it back rather than imposing our own.</summary>
    public TextFileFormat FileFormat { get; private set; } = TextFileFormat.Default;

    /// <summary>Undo steps for the document in front of the user; each tab keeps its own.</summary>
    public ContentHistory History { get; private set; } = new();

    /// <summary>Raised whenever undo or redo becomes possible or impossible, including after a tab switch.</summary>
    public event Action? HistoryChanged;

    private void SetHistory(ContentHistory history)
    {
        History.Changed -= OnHistoryChanged;
        History = history;
        History.Changed += OnHistoryChanged;
        OnHistoryChanged();
    }

    private void OnHistoryChanged() => HistoryChanged?.Invoke();

    private bool saved = true;
    public bool Saved { get => saved; private set { if (saved == value) return; saved = value; OnPropertyChanged(); OnPropertyChanged(nameof(Title)); } }

    public string Markdown { get; private set; } = DefaultMarkdown;
    public ulong FileHash { get; private set; } = SafeFile.Hash(DefaultMarkdown);
    public ulong CurrentHash { get; private set; } = SafeFile.Hash(DefaultMarkdown);
    /// <summary>Hash of the raw text last seen on disk; the editor's normalized text can differ from it.</summary>
    public ulong DiskHash { get; private set; }
    public bool FileLoaded { get; private set; }
    public int LoadId { get; private set; }
    public JsonNode? Cursor { get; private set; }
    public double? ScrollTop { get; private set; }
    /// <summary>False until the editor page asked for its settings; before that content is only staged for GetSettings.</summary>
    public bool EditorReady { get; set; }

    public string FileName => FilePath == null ? Loc.Get("Untitled") : Path.GetFileName(FilePath);
    public string Title => (Saved ? "" : "• ") + FileName + " - Typedown";
    public bool IsBlank => FilePath == null && Saved && CurrentHash == SafeFile.Hash(DefaultMarkdown);

    public DocumentViewModel(EditorTransport transport, IHostUi ui, AppSettings settings)
    {
        this.transport = transport;
        this.ui = ui;
        this.settings = settings;
        transport.MessageReceived += OnEditorMessage;
        History.Changed += OnHistoryChanged;
    }

    // ---- editor reports -------------------------------------------------------------------------------------

    private bool IsStale(JsonNode? args)
    {
        var id = args?["loadId"];
        return id != null && id.GetValueKind() == System.Text.Json.JsonValueKind.Number && id.GetValue<int>() != LoadId;
    }

    private void OnEditorMessage(string name, JsonNode? args)
    {
        switch (name)
        {
            case "FileLoaded":
                if (IsStale(args)) return;
                ConfirmedLoadId = LoadId;
                if (FileLoaded) return;
                if (CompleteAutomationReload(args)) return;
                FileLoaded = true;
                var text = args?["text"]?.GetValue<string>() ?? "";
                // The page hands back the source it was given; anything else is a real change (document-identity.json).
                if (text != Markdown) Revision++;
                var loadedHash = SafeFile.Hash(text);
                Markdown = text;
                CurrentHash = loadedHash;
                var wasRecovery = recoveredDiskHash.HasValue;
                if (recoveredDiskHash is ulong diskBaseline)
                {
                    // Recovered from a crash backup: the saved baseline is what is on disk (or the empty document
                    // for an untitled one), so the recovered text stays unsaved until the reader saves it.
                    FileHash = diskBaseline;
                    recoveredDiskHash = null;
                    Saved = FileHash == CurrentHash;
                    if (!Saved) ScheduleSaveOrBackup();
                }
                else
                {
                    FileHash = loadedHash; // the editor's normalized form is the "saved" baseline
                    Saved = true;
                }
                History.Init(text);
                // Once the first document has loaded, offer to recover its crash backup (a file reopened after a
                // crash, or an untitled document being edited). Interactive opens do this inline in LoadAsync.
                if (!initialRecoveryDone)
                {
                    initialRecoveryDone = true;
                    if (!wasRecovery) RunOnUi?.Invoke(MaybeRecoverActiveAsync);
                }
                break;
            case "MarkdownChange":
                if (IsStale(args)) return;
                if (HoldReport(args)) return;
                var changed = args?["text"]?.GetValue<string>() ?? Markdown;
                if (changed != Markdown) Revision++;
                Markdown = changed;
                CurrentHash = SafeFile.Hash(Markdown);
                Saved = FileHash == CurrentHash;
                if (!applyingHistory) History.ContentChange(Markdown);
                if (!Saved) ScheduleSaveOrBackup();
                break;
            case "DocumentEditApplied":
            case "NormalizationReport":
            case "PresentationFrames":
                OnAutomationReply(name, args);
                break;
            case "ContentFlushed":
                {
                    var token = args?["token"]?.GetValue<int>() ?? 0;
                    TaskCompletionSource<bool>? waiter;
                    lock (flushWaiters) flushWaiters.TryGetValue(token, out waiter);
                    waiter?.TrySetResult(true);
                    break;
                }
            case "CursorChange":
                if (IsStale(args) || !FileLoaded) return;
                Cursor = args?["cursor"]?.DeepClone();
                if (!applyingHistory) History.CursorChange(Cursor);
                if (settings.RememberPosition) CursorMemory.SetCursor(FilePath, Cursor);
                break;
            case "OnScroll":
                if (!FileLoaded) return;
                // Only the page showing this load knows where the reader is. A report without a load id is a
                // freshly navigated page reporting its empty body's first layout as 0; one with another id is
                // the page that was, still talking. Either recorded a 0 that the next load then came back to.
                if (args?["loadId"] == null || IsStale(args)) return;
                var y = args?["scrollY"]?.GetValue<double?>();
                if (y == null) return;
                ScrollTop = y;
                if (settings.RememberPosition) CursorMemory.SetScroll(FilePath, y.Value);
                break;
        }
    }

    // The editor reports the text at most every 250 ms while typing, so anything that reads it as the document
    // asks for it to be brought up to date first. The wait is bounded: if the page does not answer, what we
    // already hold is written rather than nothing.
    private int flushToken;
    private readonly Dictionary<int, TaskCompletionSource<bool>> flushWaiters = new();

    /// <summary>Returns false when the editor did not answer before the timeout.</summary>
    public async Task<bool> FlushContentAsync(int timeoutMs = 500)
    {
        if (!EditorReady || !FileLoaded) return true;
        var token = ++flushToken;
        var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (flushWaiters) flushWaiters[token] = waiter;
        try
        {
            await transport.PostMessage("FlushContent", new { token });
            var completed = await Task.WhenAny(waiter.Task, Task.Delay(timeoutMs));
            return completed == waiter.Task;
        }
        finally
        {
            lock (flushWaiters) flushWaiters.Remove(token);
        }
    }

    private bool applyingHistory;

    public Task<bool> UndoAsync() => ApplyHistoryAsync(History.Undo());

    public Task<bool> RedoAsync() => ApplyHistoryAsync(History.Redo());

    /// <summary>
    /// Puts a remembered state back into the editor. The editor reports the change straight back to us, which
    /// would otherwise be recorded as a new step and make undo impossible to get out of, hence the flag.
    /// </summary>
    private async Task<bool> ApplyHistoryAsync(HistoryEntry? entry)
    {
        if (entry?.Text == null) return false;
        applyingHistory = true;
        try
        {
            if (entry.Text != Markdown) Revision++;
            Markdown = entry.Text;
            CurrentHash = SafeFile.Hash(Markdown);
            Saved = FileHash == CurrentHash;
            Cursor = entry.Cursor?.DeepClone();
            // Under a new load id: a report the page made before this (still on its way) is then recognizably older and
            // dropped, instead of writing the text just undone back over the history. SetMarkdown has no handshake,
            // so FileLoaded stays as it is.
            await transport.PostMessage("SetMarkdown", new { text = entry.Text, cursor = entry.Cursor, basePath = BasePath, loadId = ++LoadId });
            ConfirmedLoadId = LoadId;
            if (!Saved) ScheduleSaveOrBackup();
            return true;
        }
        finally
        {
            applyingHistory = false;
        }
    }

    private void ScheduleSaveOrBackup()
    {
        autoSaveCts?.Cancel();
        var cts = autoSaveCts = new CancellationTokenSource();
        _ = Task.Delay(1500, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            RunOnUi?.Invoke(SaveOrBackupAsync);
        }, TaskScheduler.Default);
    }

    // When auto-save is on and the document has a path, save it; otherwise — or if that save is refused or fails —
    // write a crash backup so an unsaved document (auto-save off, or still untitled) is not lost to a crash.
    private async Task SaveOrBackupAsync()
    {
        if (Saved) return;
        if (settings.AutoSave && FilePath != null && await WriteAsync(FilePath, quiet: true)) return;
        await FlushContentAsync();
        if (!Saved) await AutoBackup.BackupAsync(FilePath, DocumentId, Markdown);
    }

    // Offers to recover the active document's crash backup once it has first loaded (a reopened file, or an
    // untitled document). If accepted, the backup is loaded and left unsaved against the on-disk baseline.
    private async Task MaybeRecoverActiveAsync()
    {
        var path = FilePath;
        var backup = AutoBackup.Read(path, DocumentId);
        if (backup == null) return;
        var backupHash = SafeFile.Hash(backup);
        if (backupHash == FileHash) { AutoBackup.Delete(path, DocumentId); return; } // backup matches the saved content
        if (path == null && backupHash == SafeFile.Hash(DefaultMarkdown)) return; // nothing worth recovering
        if (await ui.ConfirmAsync(Loc.Get("RecoverTitle"), Loc.Get("RecoverContent"), Loc.Get("Restore"), Loc.Get("Delete")))
        {
            recoveredDiskHash = FileHash;
            await PostLoadFile(backup);
        }
        else
        {
            AutoBackup.Delete(path, DocumentId);
        }
    }

    // ---- content into the editor ---------------------------------------------------------------------------

    private Task PostLoadFile(string text, JsonNode? cursor = null, double? scrollTop = null)
    {
        FileLoaded = false;
        if (!EditorReady) return Task.CompletedTask; // the initial GetSettings reply carries the document
        return transport.PostMessage("LoadFile", new
        {
            text,
            basePath = BasePath,
            cursor = cursor ?? (settings.RememberPosition ? CursorMemory.GetCursor(FilePath) : null),
            scrollTop = scrollTop ?? (settings.RememberPosition ? CursorMemory.GetScroll(FilePath) : null),
            loadId = ++LoadId,
        });
    }

    public string BasePath => FilePath == null ? Environment.CurrentDirectory : Path.GetDirectoryName(FilePath)!;

    /// <summary>Payload part for the editor's initial GetSettings call.</summary>
    public object GetLoadPayload()
    {
        FileLoaded = false;
        return new
        {
            markdown = Markdown,
            basePath = BasePath,
            cursor = settings.RememberPosition ? CursorMemory.GetCursor(FilePath) : null,
            scrollTop = settings.RememberPosition ? CursorMemory.GetScroll(FilePath) : null,
            loadId = ++LoadId,
        };
    }

    // ---- tabs: snapshot / restore ------------------------------------------------------------------------------

    public void Capture(DocumentTab tab)
    {
        tab.DocumentId = DocumentId;
        tab.FilePath = FilePath;
        tab.FileFormat = FileFormat;
        tab.History = History;
        tab.Markdown = Markdown;
        tab.FileHash = FileHash;
        tab.CurrentHash = CurrentHash;
        tab.DiskHash = DiskHash;
        tab.Saved = Saved;
        tab.FileLoaded = FileLoaded;
        tab.Cursor = Cursor;
        tab.ScrollTop = ScrollTop;
        tab.IsDirty = !Saved;
        tab.Revision = Revision;
    }

    public async Task Restore(DocumentTab tab)
    {
        StopWatching();
        DocumentId = tab.DocumentId;
        FilePath = tab.FilePath;
        FileFormat = tab.FileFormat ?? TextFileFormat.Default;
        SetHistory(tab.History ??= new ContentHistory());
        Markdown = tab.Markdown;
        FileHash = tab.FileHash;
        CurrentHash = tab.CurrentHash;
        DiskHash = tab.DiskHash;
        Saved = tab.Saved;
        Cursor = tab.Cursor;
        ScrollTop = tab.ScrollTop;
        Revision = tab.Revision;
        // A tab shown before keeps its baseline (the handshake must not reset it); one loaded in the background
        // has never been through the editor and still needs it — PostLoadFile clears FileLoaded, so re-set after.
        await PostLoadFile(Markdown, tab.Cursor, tab.ScrollTop);
        FileLoaded = tab.FileLoaded;
        StartWatching();
        await CheckExternalChangeAsync();
    }

    /// <summary>Separates a new tab from the outgoing tab before a file is loaded into it.</summary>
    public void PrepareNewTab(string documentId)
    {
        StopWatching();
        DocumentId = documentId;
        Revision = 0;
        SetHistory(new ContentHistory());
    }

    // ---- commands ---------------------------------------------------------------------------------------------

    /// <summary>Makes the live document an empty untitled one (the caller decides about tabs / saving).</summary>
    public async Task ResetToUntitledAsync(string? documentId = null, bool discardCurrent = true, string? recoveredText = null)
    {
        if (discardCurrent) AutoBackup.Delete(FilePath, DocumentId);
        StopWatching();
        DocumentId = documentId ?? Guid.NewGuid().ToString("N");
        Revision = 0;
        SetHistory(new ContentHistory());
        FilePath = null;
        FileFormat = TextFileFormat.Default;
        Markdown = recoveredText ?? DefaultMarkdown;
        FileHash = SafeFile.Hash(DefaultMarkdown);
        CurrentHash = SafeFile.Hash(Markdown);
        DiskHash = 0;
        Cursor = null;
        ScrollTop = null;
        Saved = recoveredText == null || FileHash == CurrentHash;
        recoveredDiskHash = recoveredText == null ? null : FileHash;
        await PostLoadFile(Markdown);
    }

    /// <summary>
    /// Past this many characters the formatted view is not worth waiting for on this platform: WebKitGTK
    /// lays a document out in a way that grows far faster than the file does (measured on one machine:
    /// 50k characters 1.9 s, 100k 5.5 s, 300k 43 s, 1.5M 129 s — the same file in source mode: 2.9 s).
    /// So a large document opens in source mode, and the reader is told; the View menu switches back.
    /// </summary>
    private const int SourceModeAboveChars = 150000;

    private async Task SwitchToSourceForLargeDocumentAsync(string text)
    {
        if (settings.SourceCode || text.Length <= SourceModeAboveChars) return;
        settings.SourceCode = true;
        ui.ShowStatus(Loc.Format("LargeDocumentSourceMode", text.Length / 1000));
        // The settings change reaches the page through the dispatcher queue, after the LoadFile posted next: the page
        // then lays the whole document out in the formatted view first, which WebKitGTK does not finish for minutes.
        // Tell the page now, so the document arrives in source mode (the queued change repeats it harmlessly).
        if (EditorReady) await transport.PostMessage("SettingsChanged", new Dictionary<string, object?> { ["sourceCode"] = true });
    }

    public Task<string?> PickOpenAsync() => ui.PickOpenFileAsync();

    /// <summary>Reads the file into the live document; the saved baseline comes back through FileLoaded.</summary>
    public async Task<bool> LoadAsync(string path)
    {
        try
        {
            if (!File.Exists(path)) throw new FileNotFoundException(Loc.Get("CannotOpen"), path);
            var (text, format) = await TextFileFormat.ReadAsync(path);
            FileFormat = format;
            await SwitchToSourceForLargeDocumentAsync(text);
            StopWatching();
            FilePath = Path.GetFullPath(path);
            Markdown = text;
            FileHash = CurrentHash = SafeFile.Hash(text);
            DiskHash = FileHash;
            Cursor = null;
            ScrollTop = null;
            Saved = true;
            // A crash backup that differs from what is on disk means unsaved edits were lost when the app stopped;
            // offer to recover them. Only when the editor is up — at startup the first FileLoaded handles this.
            var toLoad = text;
            if (EditorReady)
            {
                var backup = AutoBackup.Read(FilePath, DocumentId);
                if (backup != null && SafeFile.Hash(backup) != FileHash)
                {
                    if (await ui.ConfirmAsync(Loc.Get("RecoverTitle"), Loc.Get("RecoverContent"), Loc.Get("Restore"), Loc.Get("Delete")))
                    {
                        toLoad = backup;
                        Markdown = backup;
                        recoveredDiskHash = FileHash;
                    }
                    else
                    {
                        AutoBackup.Delete(FilePath, DocumentId);
                    }
                }
            }
            await PostLoadFile(toLoad);
            StartWatching();
            FileOpened?.Invoke(FilePath);
            return true;
        }
        catch (Exception ex)
        {
            await ui.ShowErrorAsync(Loc.Get("CannotOpen"), ex.Message);
            return false;
        }
    }

    public async Task<bool> SaveAsync()
    {
        if (FilePath == null) return await SaveAsAsync();
        return await WriteAsync(FilePath);
    }

    public async Task<bool> SaveAsAsync()
    {
        var documentId = DocumentId;
        var path = await ui.PickSaveFileAsync(FilePath == null ? Loc.Get("Untitled") + ".md" : Path.GetFileName(FilePath));
        if (path == null || DocumentId != documentId) return false;
        var previous = FilePath;
        var target = Path.GetFullPath(path);
        StopWatching();
        FilePath = target;
        var ok = await WriteAsync(target);
        if (DocumentId != documentId || !string.Equals(FilePath, target, StringComparison.Ordinal)) return false;
        if (!ok) FilePath = previous;
        StartWatching();
        if (ok)
        {
            if (previous != target) AutoBackup.Delete(previous, documentId); // the old (or untitled) backup no longer applies
            FileOpened?.Invoke(target);
        }
        return ok;
    }

    private async Task<bool> WriteAsync(string path, bool quiet = false)
    {
        var documentId = DocumentId;
        await saveLock.WaitAsync();
        try
        {
            if (DocumentId != documentId || !string.Equals(FilePath, path, StringComparison.Ordinal)) return false;
            return await WriteCoreAsync(path, documentId, quiet);
        }
        finally
        {
            saveLock.Release();
        }
    }

    private async Task<bool> WriteCoreAsync(string path, string documentId, bool quiet)
    {
        try
        {
            if (!await FlushContentAsync())
                throw new IOException(Loc.Get("EditorNotResponding"));
            if (DocumentId != documentId || !string.Equals(FilePath, path, StringComparison.Ordinal)) return false;
            var text = Markdown;
            // A blank buffer over a file that has content is data loss unless the reader actually deleted the
            // text (an undoable edit): it happens when the editor is cleared programmatically — a stale/empty
            // load, a tab race, the document handed back blank — and Ctrl+Z cannot bring it back either. Refuse
            // it so the file on disk keeps its content and reopening recovers the document.
            if (string.IsNullOrWhiteSpace(text) && File.Exists(path) && new FileInfo(path).Length > 0
                && (!FileLoaded || !History.Undoable))
            {
                Services.Log.Write($"save skipped: blank buffer (len={text.Length}, loaded={FileLoaded}, undoable={History.Undoable}) while the file has {new FileInfo(path).Length} bytes");
                return false;
            }
            if (quiet && (FileFormat?.LossyDecode ?? false) && File.Exists(path))
            {
                Services.Log.Write($"auto-save skipped: {path} was decoded lossily; overwriting would replace its original bytes");
                return false;
            }
            var hash = SafeFile.Hash(text);
            handlingExternalChange = true; // our own write must not look like an external change
            await SafeFile.WriteAllBytesAtomicAsync(path, (FileFormat ?? TextFileFormat.Default).GetBytes(text));
            if (DocumentId != documentId || !string.Equals(FilePath, path, StringComparison.Ordinal)) return false;
            FileHash = hash;
            DiskHash = hash;
            Saved = CurrentHash == hash;
            if (Saved) AutoBackup.Delete(path, documentId); // the file now holds this content; the crash backup is stale
            CursorMemory.Flush();
            return true;
        }
        catch (Exception ex)
        {
            if (!quiet) await ui.ShowErrorAsync(Loc.Get("CannotSave"), ex.Message);
            return false;
        }
        finally
        {
            handlingExternalChange = false;
        }
    }

    /// <summary>Asks about unsaved changes; true when the caller may proceed.</summary>
    public async Task<bool> AskToSaveAsync()
    {
        var flushed = await FlushContentAsync();
        if (flushed && Saved) return true;
        if (settings.AutoSave && FilePath != null && await WriteAsync(FilePath, quiet: true)) return true;
        switch (await ui.AskSaveAsync(FileName))
        {
            case AskResult.Yes: return await SaveAsync();
            case AskResult.No:
                AutoBackup.Delete(FilePath, DocumentId);
                return true;
            default: return false;
        }
    }

    // ---- external changes ------------------------------------------------------------------------------------

    private void StartWatching()
    {
        if (FilePath == null) return;
        try
        {
            watcher = new FileSystemWatcher(Path.GetDirectoryName(FilePath)!, Path.GetFileName(FilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            watcher.Changed += OnFileEvent;
            watcher.Created += OnFileEvent;
            watcher.Renamed += OnFileEvent;
            watcher.Deleted += OnFileEvent;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;
        }
        catch
        {
            watcher = null;
        }
    }

    private void StopWatching()
    {
        lock (watcherScheduleSync)
        {
            watcherCheckCts?.Cancel();
            watcherCheckCts?.Dispose();
            watcherCheckCts = null;
            Interlocked.Exchange(ref watcherCheckRequested, 0);
        }
        watcher?.Dispose();
        watcher = null;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        ScheduleExternalCheck();
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        Services.Log.Error("file watcher", e.GetException());
        ScheduleExternalCheck();
    }

    /// <summary>
    /// Coalesces bursts without dropping an event that arrives while a save or reload is in progress. Watcher
    /// overflow is handled through the same full disk re-check, so the next known state is read from the file.
    /// </summary>
    private void ScheduleExternalCheck()
    {
        Interlocked.Exchange(ref watcherCheckRequested, 1);
        CancellationToken token;
        lock (watcherScheduleSync)
        {
            watcherCheckCts?.Cancel();
            watcherCheckCts?.Dispose();
            watcherCheckCts = new CancellationTokenSource();
            token = watcherCheckCts.Token;
        }
        RunOnUi?.Invoke(async () =>
        {
            try
            {
                await Task.Delay(200, token);
                await watcherCheckLock.WaitAsync(token);
                try
                {
                    while (Interlocked.Exchange(ref watcherCheckRequested, 0) != 0)
                    {
                        while (handlingExternalChange) await Task.Delay(50, token);
                        await CheckExternalChangeAsync();
                    }
                }
                finally { watcherCheckLock.Release(); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        });
    }

    public async Task CheckExternalChangeAsync()
    {
        var path = FilePath;
        var documentId = DocumentId;
        if (path == null || handlingExternalChange) return;
        handlingExternalChange = true;
        try
        {
            if (!File.Exists(path)) return;
            string text;
            TextFileFormat format;
            try { (text, format) = await TextFileFormat.ReadAsync(path); } catch { return; }
            if (DocumentId != documentId || !string.Equals(FilePath, path, StringComparison.Ordinal)) return;
            FileFormat = format; // whoever wrote it last decides the shape from now on
            var diskHash = SafeFile.Hash(text);
            if (diskHash == DiskHash || diskHash == FileHash) return; // nothing really changed
            if (Saved || settings.AutoReload)
            {
                await ApplyDiskTextAsync(text);
                return;
            }
            if (!settings.AskBeforeReload)
            {
                DiskHash = diskHash; // keep the unsaved buffer and stop asking about this revision
                return;
            }
            var reload = await ui.ConfirmAsync(Loc.Get("FileChanged"), Loc.Format("ReloadPrompt", FileName), Loc.Get("Reload"), Loc.Get("KeepMine"));
            if (DocumentId != documentId || !string.Equals(FilePath, path, StringComparison.Ordinal)) return;
            if (reload) await ApplyDiskTextAsync(text);
            else DiskHash = diskHash;
        }
        finally
        {
            handlingExternalChange = false;
        }
    }

    private async Task ApplyDiskTextAsync(string text)
    {
        if (text != Markdown) Revision++;
        Markdown = text;
        FileHash = CurrentHash = SafeFile.Hash(text);
        DiskHash = FileHash;
        Saved = true;
        await PostLoadFile(text, Cursor, ScrollTop);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        transport.MessageReceived -= OnEditorMessage;
        StopWatching();
        CursorMemory.Flush();
    }
}
