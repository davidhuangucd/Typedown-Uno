using System.Runtime.InteropServices;
using Typedown.Automation;
using Typedown.Uno.Services;

namespace Typedown.Uno.Automation;

/// <summary>
/// Local automation for the Uno edition (the Windows repository's docs/automation-api-spec.md). Off by default: the
/// "Allow local automation" setting opens the private Unix socket, and turning it off closes it and every connection.
/// While a client is connected the window title says so, and a write names the client and the document there briefly.
/// On Windows the native edition serves the API; this build does not.
/// </summary>
public static class AutomationRuntime
{
    private const int NoticeMs = 4000;

    private static AutomationServer? server;
    private static readonly object gate = new();
    private static Task applying = Task.CompletedTask;

    public static bool Supported => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>The socket this instance listens on (or would).</summary>
    public static string SocketPath => UnixSocketListener.DefaultPath(BuildTypes.Application);

    public static void Initialize(AppSettings settings)
    {
        if (!Supported || server != null) return;
        var info = new ServerInfo
        {
            Version = Services.AppInfo.Version,
            Platform = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos" : "linux",
            BuildType = BuildTypes.Application,
            MaxMessageBytes = 32L * 1024 * 1024,
        };
        var host = new UnoAutomationHost(info.Version);
        var settingsHost = new UnoSettingsHost(settings);
        var catalog = SettingsCatalog.Load();
        var path = SocketPath;
        server = new AutomationServer(
            () => UnixSocketListener.Open(path),
            () =>
            {
                var methods = DocumentMethods.AddTo(new MethodTable(BuildTypes.Application), host, info.InstanceId, OnWrite);
                SettingsMethods.AddTo(methods, settingsHost, catalog);
                return new AutomationSession(info, methods);
            },
            info.MaxMessageBytes);
        server.ActivityChanged += OnActivityChanged;
        server.ListenerFailed += e => Log.Write($"automation: the socket could not listen: {e.Message}");
        settings.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppSettings.AllowLocalAutomation)) Apply(settings.AllowLocalAutomation); };
        // A clean exit closes the socket and removes its file (a crash leaves it; the next start replaces it).
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { server?.StopAsync().Wait(2000); } catch { }
        };
        Apply(settings.AllowLocalAutomation);
    }

    private static void Apply(bool on)
    {
        var s = server;
        if (s == null) return;
        lock (gate)
            applying = applying.ContinueWith(async _ =>
            {
                try
                {
                    if (on && !s.IsRunning) { s.Start(); Log.Write("automation: listening on " + SocketPath); }
                    else if (!on && s.IsRunning) { await s.StopAsync(); Log.Write("automation: stopped"); }
                }
                catch (Exception e) { Log.Write($"automation: {e.Message}"); }
            }, TaskScheduler.Default).Unwrap();
    }

    public static Task ShutdownAsync() => server?.StopAsync() ?? Task.CompletedTask;

    private static void OnActivityChanged(AutomationActivity activity)
    {
        var connected = activity.ConnectionCount > 0;
        foreach (var window in AutomationWindows.Registry.Snapshot())
            _ = AutomationWindows.Registry.OnWindowAsync(window.WindowId, w => { w.SetConnected(connected); return true; })
                .ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    private static async void OnWrite(string client, string documentId)
    {
        try
        {
            var found = await AutomationWindows.FindDocumentAsync(documentId);
            if (found == null) return;
            var (window, tab) = found.Value;
            var notice = Loc.Format("AutomationWrote", client, tab.Title);
            await AutomationWindows.Registry.OnWindowAsync(window.WindowId, w => { w.SetNotice(notice); return true; });
            await Task.Delay(NoticeMs);
            await AutomationWindows.Registry.OnWindowAsync(window.WindowId, w => { w.SetNotice(null, notice); return true; });
        }
        catch (Exception e)
        {
            Log.Write($"automation: write notice: {e.Message}");
        }
    }
}
