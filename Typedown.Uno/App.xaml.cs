using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Uno.Resizetizer;

namespace Typedown.Uno;

public partial class App : Application
{
    /// <summary>
    /// Initializes the singleton application object. This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        ConfigureLinuxWebKit();
        Services.X11Window.InstallLibraryResolver();
        Services.Log.WriteStartup();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Services.Log.Write($"unhandled: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => Services.Log.Write($"unobserved: {e.Exception.Message}");
        this.InitializeComponent();
    }

    /// <summary>
    /// WebKitGTK >= 2.42 renders nothing (a blank white view) on systems without a usable DMABUF path — headless
    /// X servers, VMs and some integrated GPUs. Disabling that renderer is the documented workaround and costs
    /// nothing on machines where it would have worked.
    /// </summary>
    private static void ConfigureLinuxWebKit()
    {
        if (!OperatingSystem.IsLinux()) return;
        if (Environment.GetEnvironmentVariable("WEBKIT_DISABLE_DMABUF_RENDERER") == null)
            Environment.SetEnvironmentVariable("WEBKIT_DISABLE_DMABUF_RENDERER", "1");
        if (Environment.GetEnvironmentVariable("GDK_BACKEND") == null)
            Environment.SetEnvironmentVariable("GDK_BACKEND", "x11"); // the GTK web view needs X11 even on Wayland
    }

    /// <summary>The first window; kept for the platform pickers that need a window handle on Windows.</summary>
    public static Window? MainWindow { get; private set; }

    private static readonly List<Window> windows = new();

    /// <summary>The UI thread's queue; on macOS it outlives every window (see Services.MacKeepRunning).</summary>
    private static Microsoft.UI.Dispatching.DispatcherQueue? uiQueue;

    public static IReadOnlyList<Window> Windows => windows;

    /// <summary>Looks up the window a page belongs to (a page only knows its XamlRoot).</summary>
    public static Window? WindowFor(XamlRoot? root) =>
        root == null ? null : windows.FirstOrDefault(w => ReferenceEquals(w.Content?.XamlRoot, root));

    /// <summary>
    /// Opens a window with its own editor, tabs and document. The title starts as a unique marker so the page
    /// can find its X11 window among the others (Uno exposes no native handle).
    /// </summary>
    public static Window CreateWindow(string? initialFile = null, bool restoreSession = false, IReadOnlyList<string>? moreFiles = null)
    {
        var window = new Window { Title = $"Typedown-{Guid.NewGuid():N}" };
        var frame = new Frame();
        window.Content = frame;
        frame.NavigationFailed += OnNavigationFailedStatic;
        frame.Navigate(typeof(MainPage), new MainPage.StartupOptions(initialFile, restoreSession, window.Title, moreFiles));
        windows.Add(window);
        window.Closed += (_, _) =>
        {
            windows.Remove(window);
            if (windows.Count == 0 && !Services.MacKeepRunning.Active) Current.Exit();
        };
        window.SetWindowIcon();
        window.Activate();
        MainWindow ??= window;
        return window;
    }

    private static void OnNavigationFailedStatic(object sender, NavigationFailedEventArgs e) =>
        throw new InvalidOperationException($"Failed to load {e.SourcePageType.FullName}: {e.Exception}");

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        uiQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var files = Environment.GetCommandLineArgs().Skip(1)
            .Where(a => !a.StartsWith('-') && System.IO.File.Exists(a))
            .ToList();
        // macOS: files opened from Finder come as an Apple Event, which arrives before this point at launch.
        files.AddRange(Services.MacOpenDocuments.Attach(OnFilesFromAnotherLaunch));
        // Opening a document from the file manager starts the program again. If one is already running it takes
        // the file and this process is done; otherwise this one becomes the instance that serves.
        if (Services.SingleInstance.HandOff(files))
        {
            Environment.Exit(0);
            return;
        }
        Services.ThemeFiles.EnsureFolder();
        Services.SingleInstance.FilesRequested += OnFilesFromAnotherLaunch;
        Services.SingleInstance.Listen();
        CreateWindow(files.FirstOrDefault(), restoreSession: files.Count == 0, moreFiles: files.Skip(1).ToList());
        // A click on the Dock icon while the program runs without a window opens one, as a fresh start would.
        Services.MacKeepRunning.Attach(() => uiQueue?.TryEnqueue(() => { if (windows.Count == 0) CreateWindow(restoreSession: true); }));
    }

    /// <summary>
    /// Another launch handed us its arguments. With "open files in a tab" the file joins the window that is
    /// already open, which is the whole point of the setting; without it, it gets a window of its own. A launch
    /// with no file at all just brings the existing window forward. On macOS the program may be running with no
    /// window at all (Services.MacKeepRunning); then the files get a new one, as at a fresh start.
    /// </summary>
    private static void OnFilesFromAnotherLaunch(IReadOnlyList<string> files)
    {
        uiQueue?.TryEnqueue(async () =>
        {
            var window = windows.LastOrDefault();
            if (window == null)
            {
                CreateWindow(files.FirstOrDefault(), restoreSession: files.Count == 0, moreFiles: files.Skip(1).ToList());
                return;
            }
            var page = (window.Content as Frame)?.Content as MainPage;
            foreach (var file in files)
            {
                if (page != null && Services.AppSettings.Current.OpenFilesInNewTab)
                    await page.OpenExternalFileAsync(file);
                else
                    CreateWindow(file);
            }
            window.Activate();
        });
    }

    /// <summary>
    /// Invoked when Navigation to a certain page fails
    /// </summary>
    /// <param name="sender">The Frame which failed navigation</param>
    /// <param name="e">Details about the navigation failure</param>
    void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
    {
        throw new InvalidOperationException($"Failed to load {e.SourcePageType.FullName}: {e.Exception}");
    }

    /// <summary>
    /// Configures global Uno Platform logging
    /// </summary>
    public static void InitializeLogging()
    {
#if DEBUG
        // Logging is disabled by default for release builds, as it incurs a significant
        // initialization cost from Microsoft.Extensions.Logging setup. If startup performance
        // is a concern for your application, keep this disabled. If you're running on the web or
        // desktop targets, you can use URL or command line parameters to enable it.
        //
        // For more performance documentation: https://platform.uno/docs/articles/Uno-UI-Performance.html

        var factory = LoggerFactory.Create(builder =>
        {
#if __WASM__
            builder.AddProvider(new global::Uno.Extensions.Logging.WebAssembly.WebAssemblyConsoleLoggerProvider());
#elif __IOS__
            builder.AddProvider(new global::Uno.Extensions.Logging.OSLogLoggerProvider());

            // Log to the Visual Studio Debug console
            builder.AddConsole();
#else
            builder.AddConsole();
#endif

            // Exclude logs below this level
            builder.SetMinimumLevel(LogLevel.Information);

            // Default filters for Uno Platform namespaces
            builder.AddFilter("Uno", LogLevel.Warning);
            builder.AddFilter("Windows", LogLevel.Warning);
            builder.AddFilter("Microsoft", LogLevel.Warning);

            // Generic Xaml events
            // builder.AddFilter("Microsoft.UI.Xaml", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.VisualStateGroup", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.StateTriggerBase", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.UIElement", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.FrameworkElement", LogLevel.Trace );

            // Layouter specific messages
            // builder.AddFilter("Microsoft.UI.Xaml.Controls", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.Controls.Layouter", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.Controls.Panel", LogLevel.Debug );

            // builder.AddFilter("Windows.Storage", LogLevel.Debug );

            // Binding related messages
            // builder.AddFilter("Microsoft.UI.Xaml.Data", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.Data", LogLevel.Debug );

            // Binder memory references tracking
            // builder.AddFilter("Uno.UI.DataBinding.BinderReferenceHolder", LogLevel.Debug );

            // DevServer and HotReload related
            // builder.AddFilter("Uno.UI.RemoteControl", LogLevel.Information);

            // Debug JS interop
            // builder.AddFilter("Uno.Foundation.WebAssemblyRuntime", LogLevel.Debug );
        });

        global::Uno.Extensions.LogExtensionPoint.AmbientLoggerFactory = factory;

#if HAS_UNO
        global::Uno.UI.Adapter.Microsoft.Extensions.Logging.LoggingAdapter.Initialize();
#endif
#endif
    }
}
