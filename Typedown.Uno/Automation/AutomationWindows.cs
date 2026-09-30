using Microsoft.UI.Dispatching;
using Typedown.Automation;
using Typedown.Uno.ViewModels;

namespace Typedown.Uno.Automation;

/// <summary>What the automation host needs from one window (MainPage provides it when the window has started).</summary>
public sealed class AutomationWindow
{
    public required DocumentViewModel Document { get; init; }
    public required TabsViewModel Tabs { get; init; }
    /// <summary>Completes when the startup documents are in place (a session restore would otherwise replace a write).</summary>
    public required Task StartupReady { get; init; }
    public required Func<bool> IsVisible { get; init; }
    public required Action Activate { get; init; }
    /// <summary>The title marker: a client is connected.</summary>
    public required Action<bool> SetConnected { get; init; }
    /// <summary>The title notice after a write ("{client} edited {document}").</summary>
    public required Action<string?> ShowNotice { get; init; }
    /// <summary>The notice now shown, so that clearing an old one leaves a newer one in place.</summary>
    public required Func<string?> Notice { get; init; }

    public void SetNotice(string? notice, string? onlyIf = null)
    {
        if (onlyIf != null && Notice() != onlyIf) return;
        ShowNotice(notice);
    }

    public DocumentTab? FindTab(string documentId) => Tabs.Tabs.FirstOrDefault(t => t.DocumentId == documentId);
    public bool IsActive(DocumentTab tab) => Tabs.ActiveTab == tab;
}

/// <summary>
/// The windows the automation API can address. Every window registers once it has started and unregisters when it
/// closes; the service reaches a window's documents only through its dispatcher queue.
/// </summary>
public static class AutomationWindows
{
    public static WindowRegistry<AutomationWindow> Registry { get; } = new();

    public static string Register(AutomationWindow window, DispatcherQueue queue) => Registry.Register(window, new QueueDispatcher(queue));

    public static void Unregister(AutomationWindow window) => Registry.Unregister(window);

    public static async Task<(RegisteredWindow<AutomationWindow> Window, DocumentTab Tab)?> FindDocumentAsync(string documentId)
    {
        var found = await Registry.FindAsync(w => w.FindTab(documentId));
        return found == null ? null : (found.Value.Window, found.Value.Value);
    }

    private sealed class QueueDispatcher : IUiDispatcher
    {
        private readonly DispatcherQueue queue;

        public QueueDispatcher(DispatcherQueue queue) => this.queue = queue;

        public Task<T> InvokeAsync<T>(Func<T> work)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!queue.TryEnqueue(() =>
            {
                try { done.SetResult(work()); }
                catch (Exception e) { done.SetException(e); }
            }))
                done.SetException(new InvalidOperationException("The window's dispatcher has shut down."));
            return done.Task;
        }
    }
}
