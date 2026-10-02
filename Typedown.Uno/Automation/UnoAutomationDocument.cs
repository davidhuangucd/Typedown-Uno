using System.Text.Json.Nodes;
using Typedown.Automation;
using Typedown.Uno.Services;
using Typedown.Uno.ViewModels;

namespace Typedown.Uno.Automation;

/// <summary>
/// A tab as the edit coordinator sees it (Typedown.Automation.IEditableDocument), the Uno counterpart of the Windows
/// edition's AutomationDocument. Every member runs on the window's UI thread. The tab shown is edited through the
/// editor page; a background tab only has its snapshot.
/// </summary>
public sealed class UnoAutomationDocument : IEditableDocument
{
    public const int FlushTimeoutMs = 2000;
    public const int ApplyTimeoutMs = 10000;
    public const int ReloadTimeoutMs = 15000;

    private readonly AutomationWindow window;
    private readonly DocumentTab tab;
    private int appliedLoadId;

    public UnoAutomationDocument(AutomationWindow window, DocumentTab tab)
    {
        this.window = window;
        this.tab = tab;
    }

    private DocumentViewModel Live => window.Document;

    public string DocumentId => tab.DocumentId;
    public bool IsActive => window.IsActive(tab);
    public long Revision => IsActive ? Live.Revision : tab.Revision;
    public string Text => IsActive ? Live.Markdown : tab.Markdown ?? "";
    public bool Saved => IsActive ? Live.Saved : tab.Saved;

    public async Task<bool> FlushAsync(CancellationToken cancellationToken) =>
        await Live.WaitForLoadAsync(ReloadTimeoutMs) && await Live.FlushContentAsync(FlushTimeoutMs);

    private sealed record State(string Text, JsonNode? Cursor, double? ScrollTop);

    public object CaptureState() => new State(Live.Markdown, Live.Cursor?.DeepClone(), Live.ScrollTop);

    public async Task<ApplyReply> ApplyInEditorAsync(ApplyCommand command, CancellationToken cancellationToken)
    {
        Live.HoldEditorReports();
        appliedLoadId = Live.LoadId;
        var reply = await Live.ApplyDocumentEditAsync(command.OperationId, command.TargetRevision, command.BaseContentHash, command.Text, command.ScrollToChange, ApplyTimeoutMs);
        if (reply == null) throw new TimeoutException("The editor did not answer ApplyDocumentEdit.");
        switch (reply["outcome"]?.GetValue<string>())
        {
            case "conflict":
                // Nothing was applied: whatever the reader typed is theirs, as usual.
                Live.ReleaseEditorReports(apply: true);
                return new ApplyReply(ApplyOutcome.Conflict);
            case "applied":
                var n = reply["normalization"];
                if (n == null) return new ApplyReply(ApplyOutcome.Failed);
                var normalized = n["normalizedHash"];
                var normalization = new NormalizationInfo(
                    n["pendingNormalization"]?.GetValue<string>() ?? PendingNormalization.Unknown,
                    n["sourceHash"]?.GetValue<string>() ?? "",
                    normalized != null && normalized.GetValueKind() == System.Text.Json.JsonValueKind.String ? normalized.GetValue<string>() : null,
                    (n["reasons"] as JsonArray)?.Select(r => r?.GetValue<string>() ?? "").ToList() ?? new List<string>(),
                    n["classifierVersion"]?.GetValue<int>() ?? 0);
                return new ApplyReply(ApplyOutcome.Applied, reply["sourceHash"]?.GetValue<string>(), normalization);
            default:
                return new ApplyReply(ApplyOutcome.Failed);
        }
    }

    public bool EditorStillHoldsEdit() => !IsActive || Live.LoadId == appliedLoadId;

    public async Task<string?> RestoreAsync(object captured, CancellationToken cancellationToken)
    {
        var state = (State)captured;
        Live.ReleaseEditorReports(apply: false);
        // The host never took the candidate in; a tab left meanwhile already holds the text from before.
        if (!IsActive) return DocumentText.ContentHash(tab.Markdown ?? "");
        var confirmed = await Live.ReloadAsync(state.Text, state.Cursor, state.ScrollTop, ReloadTimeoutMs);
        return confirmed == null ? null : DocumentText.ContentHash(confirmed);
    }

    public void Commit(string text, long revision, string operationId)
    {
        if (IsActive)
        {
            Live.CommitAutomationText(text, revision);
        }
        else
        {
            tab.Markdown = text;
            tab.CurrentHash = SafeFile.Hash(text);
            tab.Saved = tab.FileHash == tab.CurrentHash;
            tab.IsDirty = !tab.Saved;
            tab.Revision = revision;
            if (tab.History != null)
            {
                tab.History.CommitPending();
                tab.History.ContentChange(text);
                tab.History.CommitPending();
            }
            else
            {
                // Never shown yet: its history starts here (the editor's first FileLoaded would otherwise start it).
                tab.History = new ContentHistory();
                tab.History.Init(text);
            }
            // Shown later, the editor's first FileLoaded must not take this text as the saved baseline.
            tab.FileLoaded = true;
            // A background tab has no autosave of its own until shown: keep a crash backup of the edit.
            if (!tab.Saved) _ = AutoBackup.BackupAsync(tab.FilePath, tab.DocumentId, text);
        }
        tab.IsPreview = false; // an edited tab is no longer a throwaway preview
    }

    public void EndEdit() => Live.ReleaseEditorReports(apply: IsActive);

    /// <summary>Only the document shown is saved in this version; a background tab is focused first.</summary>
    public Task<bool> SaveAsync(CancellationToken cancellationToken) =>
        IsActive ? Live.SaveToExistingPathAsync() : Task.FromResult(false);
}
