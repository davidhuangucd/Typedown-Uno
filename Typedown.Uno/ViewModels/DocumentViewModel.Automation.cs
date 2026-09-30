using System.Text.Json.Nodes;
using Typedown.Uno.Services;

namespace Typedown.Uno.ViewModels;

// What the local automation API needs from the live document (Automation/UnoAutomationHost.cs), the same steps the
// Windows edition's EditorViewModel/FileViewModel provide: the revision, holding the reader's reports while a write
// is in flight, the page's ApplyDocumentEdit, a restoring reload that keeps the history, and the commit.
public sealed partial class DocumentViewModel
{
    /// <summary>Advances only when the text really changes (docs/automation-fixtures/document-identity.json).</summary>
    public long Revision { get; private set; }

    /// <summary>Raised on the UI thread after a real change of the text (typing, undo, reload, automation).</summary>
    public event Action? RevisionChanged;

    // ---- the reader's reports while a write is in flight -------------------------------------------------------

    private bool holdingReports;
    private JsonNode? heldReport;

    /// <summary>While a write is applied and committed, the reader's typing waits here and becomes the next revision.</summary>
    public void HoldEditorReports()
    {
        holdingReports = true;
        heldReport = null;
    }

    /// <summary><paramref name="apply"/>: the held typing still belongs to this document (it is dropped after a failed write).</summary>
    public void ReleaseEditorReports(bool apply)
    {
        holdingReports = false;
        var held = heldReport;
        heldReport = null;
        if (apply && held != null) OnEditorMessage("MarkdownChange", held);
    }

    private bool HoldReport(JsonNode? args)
    {
        if (!holdingReports) return false;
        heldReport = args?.DeepClone();
        return true;
    }

    // ---- replies the page sends to automation requests ---------------------------------------------------------

    private readonly Dictionary<string, TaskCompletionSource<JsonNode?>> replyWaiters = new();

    private void OnAutomationReply(string name, JsonNode? args)
    {
        var key = name == "DocumentEditApplied" ? "edit:" + args?["operationId"]?.GetValue<string>() : name + ":" + args?["token"]?.ToString();
        TaskCompletionSource<JsonNode?>? waiter;
        lock (replyWaiters) replyWaiters.TryGetValue(key, out waiter);
        waiter?.TrySetResult(args?.DeepClone());
    }

    private async Task<JsonNode?> AskPageAsync(string key, string message, object payload, int timeoutMs)
    {
        var waiter = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (replyWaiters) replyWaiters[key] = waiter;
        try
        {
            await transport.PostMessage(message, payload);
            return await Task.WhenAny(waiter.Task, Task.Delay(timeoutMs)) == waiter.Task ? waiter.Task.Result : null;
        }
        finally
        {
            lock (replyWaiters) replyWaiters.Remove(key);
        }
    }

    private int automationToken;

    /// <summary>The page's DocumentEditApplied reply, or null when it did not answer in time.</summary>
    public Task<JsonNode?> ApplyDocumentEditAsync(string operationId, long targetRevision, string baseContentHash, string text, int timeoutMs) =>
        AskPageAsync("edit:" + operationId, "ApplyDocumentEdit", new { operationId, targetRevision, baseContentHash, text, loadId = LoadId }, timeoutMs);

    /// <summary>What the first visual edit would do to the text shown now; null when the page did not answer.</summary>
    public Task<JsonNode?> QueryNormalizationAsync(int timeoutMs)
    {
        var token = ++automationToken;
        return AskPageAsync("NormalizationReport:" + token, "QueryNormalization", new { token }, timeoutMs);
    }

    /// <summary>True once the page has drawn two more frames (awaitPresentation).</summary>
    public async Task<bool> AwaitPageFramesAsync(int timeoutMs)
    {
        var token = ++automationToken;
        return await AskPageAsync("PresentationFrames:" + token, "AwaitPresentation", new { token }, timeoutMs) != null;
    }

    // ---- restoring reload --------------------------------------------------------------------------------------

    private int automationReloadId = -1;
    private TaskCompletionSource<string?>? automationReload;

    /// <summary>
    /// Reloads the text through the normal LoadFile flow under a new load id, keeping the history, the saved baseline
    /// and the revision; returns the text the page confirmed, or null when it did not in time.
    /// </summary>
    public async Task<string?> ReloadAsync(string text, JsonNode? cursor, double? scrollTop, int timeoutMs)
    {
        if (!EditorReady) return null;
        var waiter = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        automationReload = waiter;
        // Known before posting: the page may answer while the post is still being awaited.
        automationReloadId = LoadId + 1;
        Markdown = text;
        await PostLoadFile(text, cursor, scrollTop);
        return await Task.WhenAny(waiter.Task, Task.Delay(timeoutMs)) == waiter.Task ? waiter.Task.Result : null;
    }

    private bool CompleteAutomationReload(JsonNode? args)
    {
        if (automationReload == null || args?["loadId"]?.GetValue<int>() != automationReloadId) return false;
        FileLoaded = true;
        var text = args?["text"]?.GetValue<string>() ?? "";
        Markdown = text;
        CurrentHash = SafeFile.Hash(text);
        Saved = FileHash == CurrentHash;
        var waiter = automationReload;
        automationReload = null;
        automationReloadId = -1;
        waiter.TrySetResult(text);
        return true;
    }

    /// <summary>Waits until the page has confirmed the current load (a write right after an open must not race it).</summary>
    public async Task<bool> WaitForLoadAsync(int timeoutMs)
    {
        for (var waited = 0; !FileLoaded; waited += 25)
        {
            if (waited >= timeoutMs || !EditorReady) return FileLoaded;
            await Task.Delay(25);
        }
        return true;
    }

    // ---- commit ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Takes an automation write in as one step: the text, one undo step, the unsaved state and the revision; then the
    /// usual save or crash backup.
    /// </summary>
    public void CommitAutomationText(string text, long revision)
    {
        Markdown = text;
        CurrentHash = SafeFile.Hash(text);
        Saved = FileHash == CurrentHash;
        History.CommitPending();
        History.ContentChange(text);
        History.CommitPending();
        Revision = revision;
        RevisionChanged?.Invoke();
        if (!Saved) ScheduleSaveOrBackup();
    }

    /// <summary>Saves to the document's own path (never asks for one); false when it has none or the write failed.</summary>
    public Task<bool> SaveToExistingPathAsync() => FilePath == null ? Task.FromResult(false) : WriteAsync(FilePath, quiet: true);

    /// <summary>After a file was read into a reused blank or preview tab: it is a new document (document-identity.json).</summary>
    public void BecomeNewDocument(string documentId)
    {
        DocumentId = documentId;
        Revision = 0;
    }
}
