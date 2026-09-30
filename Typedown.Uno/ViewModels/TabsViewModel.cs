using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Typedown.Uno.Services;

namespace Typedown.Uno.ViewModels;

/// <summary>Document tabs of one window over a single live <see cref="DocumentViewModel"/> (ported from Typedown).</summary>
public sealed class TabsViewModel : INotifyPropertyChanged
{
    private readonly DocumentViewModel document;
    private readonly AppSettings settings;
    private bool switching;

    public ObservableCollection<DocumentTab> Tabs { get; } = new();

    private DocumentTab activeTab;
    public DocumentTab ActiveTab { get => activeTab; private set { activeTab = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? TabsChanged;

    public TabsViewModel(DocumentViewModel document, AppSettings settings)
    {
        this.document = document;
        this.settings = settings;
        activeTab = new DocumentTab();
        activeTab.DocumentId = document.DocumentId;
        Tabs.Add(activeTab);
        document.PropertyChanged += (_, e) =>
        {
            if (switching) return;
            if (e.PropertyName == nameof(DocumentViewModel.FilePath)) ActiveTab.FilePath = document.FilePath;
            if (e.PropertyName == nameof(DocumentViewModel.Saved))
            {
                ActiveTab.IsDirty = !document.Saved;
                if (ActiveTab.IsDirty) ActiveTab.IsPreview = false;
            }
        };
        Tabs.CollectionChanged += (_, _) => TabsChanged?.Invoke();
    }

    public DocumentTab? FindByPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        return Tabs.FirstOrDefault(t => PathIdentity.Same(t.FilePath, path));
    }

    /// <summary>Opens a file: an already open one becomes active; otherwise it gets a tab (reusing a blank or preview tab).</summary>
    public async Task<bool> OpenFileAsync(string path, bool preview = false)
    {
        if (FindByPath(path) is { } existing)
        {
            await SwitchToAsync(existing);
            if (!preview) existing.IsPreview = false;
            return true;
        }
        // "Open files in a new tab" off: an open replaces the current document whenever it has nothing to lose.
        var reuse = document.IsBlank || (preview && document.Saved && ActiveTab.IsPreview) || (!settings.OpenFilesInNewTab && document.Saved);
        DocumentTab? started = null;
        if (!reuse) started = BeginNewTab();
        var ok = await document.LoadAsync(path);
        if (!ok && started != null) AbortNewTab(started);
        if (ok)
        {
            ActiveTab.IsPreview = preview;
            settings.AddRecent(document.FilePath!);
        }
        return ok;
    }

    public async Task NewTabAsync()
    {
        if (document.IsBlank) return;
        BeginNewTab();
        await document.ResetToUntitledAsync(ActiveTab.DocumentId, discardCurrent: false);
    }

    private DocumentTab BeginNewTab(string? documentId = null)
    {
        document.Capture(ActiveTab);
        var tab = new DocumentTab { DocumentId = documentId ?? Guid.NewGuid().ToString("N") };
        Tabs.Add(tab);
        ActiveTab = tab;
        document.PrepareNewTab(tab.DocumentId);
        return tab;
    }

    private void AbortNewTab(DocumentTab tab)
    {
        if (!Tabs.Contains(tab) || tab != ActiveTab) return;
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        var back = Tabs[Math.Max(0, index - 1)];
        ActiveTab = back;
        _ = document.Restore(back);
    }

    /// <summary>The tab that was active before this one, for the shortcut that jumps back and forth.</summary>
    private DocumentTab? previousTab;

    /// <summary>
    /// Back to the tab used before this one — two tabs out of many, switched between the way Alt+Tab switches
    /// between two windows. Falls back to the next tab when there is no history yet.
    /// </summary>
    public Task SwitchToLastUsedAsync()
    {
        if (previousTab != null && previousTab != ActiveTab && Tabs.Contains(previousTab))
            return SwitchToAsync(previousTab);
        return SwitchRelativeAsync(1);
    }

    public async Task SwitchToAsync(DocumentTab tab)
    {
        if (tab == ActiveTab || !Tabs.Contains(tab)) return;
        // Bring the outgoing document's latest edits in and let its pending change reports resolve before we
        // snapshot it and load the next one, or a throttled MarkdownChange for the tab being left can arrive
        // after the switch and land on the tab now shown — one document's text crossing into another.
        if (!await document.FlushContentAsync())
        {
            document.ShowStatus(Loc.Get("EditorNotResponding"));
            OnPropertyChanged(nameof(ActiveTab)); // reset a TabView selection that already moved visually
            return;
        }
        switching = true;
        try
        {
            document.Capture(ActiveTab);
            previousTab = ActiveTab;
            ActiveTab = tab;
            await document.Restore(tab);
        }
        finally
        {
            switching = false;
        }
    }

    /// <summary>
    /// The tab at a position, counted from zero; a position past the last tab means the last one, which is what
    /// the modifier+9 shortcut is for. Nothing happens when that tab is already the active one.
    /// </summary>
    public Task SwitchToIndexAsync(int index)
    {
        if (Tabs.Count == 0) return Task.CompletedTask;
        var tab = index >= Tabs.Count ? Tabs[^1] : Tabs[Math.Max(index, 0)];
        return tab == ActiveTab ? Task.CompletedTask : SwitchToAsync(tab);
    }

    public Task SwitchRelativeAsync(int delta)
    {
        if (Tabs.Count < 2) return Task.CompletedTask;
        var index = (Tabs.IndexOf(ActiveTab) + delta + Tabs.Count) % Tabs.Count;
        return SwitchToAsync(Tabs[index]);
    }

    /// <summary>Closes a tab after the save prompt; closing the last tab leaves an empty untitled document.</summary>
    public async Task<bool> CloseTabAsync(DocumentTab tab)
    {
        if (!Tabs.Contains(tab)) return false;
        if (tab != ActiveTab) await SwitchToAsync(tab);
        if (!await document.AskToSaveAsync()) return false;
        if (Tabs.Count == 1)
        {
            await document.ResetToUntitledAsync(tab.DocumentId);
            tab.FilePath = null;
            tab.IsDirty = false;
            return true;
        }
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        var next = Tabs[Math.Min(index, Tabs.Count - 1)];
        switching = true;
        try
        {
            ActiveTab = next;
            await document.Restore(next);
        }
        finally
        {
            switching = false;
        }
        return true;
    }

    /// <summary>Save prompt for every dirty tab; false when the user cancelled.</summary>
    public async Task<bool> AskToSaveAllAsync()
    {
        foreach (var tab in Tabs.ToList())
        {
            var dirty = tab == ActiveTab ? !document.Saved : !tab.Saved;
            if (!dirty) continue;
            await SwitchToAsync(tab);
            if (!await document.AskToSaveAsync()) return false;
        }
        return true;
    }

    public void SaveSession(string? folder)
    {
        var documents = Tabs.Select(t => new SessionMemory.DocumentEntry
        {
            FilePath = t == ActiveTab ? document.FilePath : t.FilePath,
            DocumentId = t == ActiveTab ? document.DocumentId : t.DocumentId,
        }).ToList();
        var activeId = document.DocumentId;
        documents = documents.Where(d => d.FilePath != null || AutoBackup.Exists(null, d.DocumentId)).ToList();
        SessionMemory.Save(documents, Math.Max(0, documents.FindIndex(d => d.DocumentId == activeId)), folder);
    }

    /// <summary>Reopens the last session's files as tabs (before the editor is up: content is staged only).</summary>
    public async Task<string?> RestoreSessionAsync()
    {
        var session = SessionMemory.Load();
        if (session == null || session.Documents.Count == 0) return session?.Folder;
        var opened = 0;
        foreach (var entry in session.Documents)
        {
            if (opened > 0) BeginNewTab(entry.DocumentId);
            else document.PrepareNewTab(entry.DocumentId);
            bool ok;
            if (entry.FilePath != null)
            {
                ok = await document.LoadAsync(entry.FilePath);
            }
            else
            {
                var backup = AutoBackup.Read(null, entry.DocumentId);
                await document.ResetToUntitledAsync(entry.DocumentId, discardCurrent: false, recoveredText: backup);
                ok = true;
            }
            if (ok) opened++;
            else if (opened > 0) AbortNewTab(ActiveTab);
        }
        var active = session.ActiveIndex < session.Documents.Count
            ? Tabs.FirstOrDefault(t => t.DocumentId == session.Documents[session.ActiveIndex].DocumentId)
            : null;
        if (active != null && active != ActiveTab) await SwitchToAsync(active);
        return session.Folder;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
