using System.Runtime.InteropServices;

namespace Typedown.Uno.Services;

/// <summary>
/// Printing, PDF export and picture export on Linux, straight through WebKitGTK; on macOS through
/// typedown-webkit-export, a helper in Typedown.app that does the same with WKWebView (packaging/macos-webkit-export.m).
///
/// Uno's web view exposes nothing of the kind, so the page is loaded into a second, offscreen WebKitGTK view
/// and that view's print operation is used: with a print dialog for "Print…", and with the settings pointed at
/// a file for "Export PDF", which is what GTK's own "Print to File" backend does.
///
/// Everything here has to run on the GTK main loop, hence the g_idle_add hop; the result comes back through a
/// TaskCompletionSource once WebKit says the operation finished.
/// </summary>
public static class WebKitExport
{
    private const string LibWebKit = "libwebkit2gtk-4.1.so";
    private const string LibGtk = "libgtk-3.so.0";
    private const string LibGLib = "libglib-2.0.so.0";
    private const string LibGObject = "libgobject-2.0.so.0";
    private const string LibCairo = "libcairo.so.2";

    [DllImport(LibGLib)] private static extern uint g_idle_add(GSourceFunc function, IntPtr data);
    [DllImport(LibGObject)] private static extern ulong g_signal_connect_data(IntPtr instance, string signal, Delegate handler, IntPtr data, IntPtr destroy, int flags);
    [DllImport(LibGObject)] private static extern void g_object_unref(IntPtr obj);
    [DllImport(LibGtk)] private static extern IntPtr gtk_offscreen_window_new();
    [DllImport(LibGtk)] private static extern void gtk_container_add(IntPtr container, IntPtr widget);
    [DllImport(LibGtk)] private static extern void gtk_widget_show_all(IntPtr widget);
    [DllImport(LibGtk)] private static extern void gtk_widget_destroy(IntPtr widget);
    [DllImport(LibGtk)] private static extern void gtk_widget_set_size_request(IntPtr widget, int width, int height);
    [DllImport(LibGtk)] private static extern IntPtr gtk_print_settings_new();
    [DllImport(LibGtk)] private static extern void gtk_print_settings_set(IntPtr settings, string key, string value);
    [DllImport(LibGtk)] private static extern void gtk_print_settings_set_printer(IntPtr settings, string printer);
    [DllImport(LibGtk)] private static extern void gtk_print_settings_set_print_pages(IntPtr settings, int pages);
    [DllImport(LibGtk)] private static extern void gtk_print_settings_set_n_copies(IntPtr settings, int copies);
    [DllImport(LibGtk)] private static extern IntPtr gtk_page_setup_new();
    [DllImport(LibGtk)] private static extern IntPtr gtk_paper_size_new(IntPtr name);
    [DllImport(LibGtk)] private static extern void gtk_page_setup_set_paper_size_and_default_margins(IntPtr setup, IntPtr paperSize);
    [DllImport(LibGtk)] private static extern void gtk_page_setup_set_top_margin(IntPtr setup, double margin, int unit);
    [DllImport(LibGtk)] private static extern void gtk_page_setup_set_bottom_margin(IntPtr setup, double margin, int unit);
    [DllImport(LibGtk)] private static extern void gtk_page_setup_set_left_margin(IntPtr setup, double margin, int unit);
    [DllImport(LibGtk)] private static extern void gtk_page_setup_set_right_margin(IntPtr setup, double margin, int unit);
    [DllImport(LibWebKit)] private static extern IntPtr webkit_web_view_new();
    [DllImport(LibWebKit)] private static extern void webkit_web_view_load_uri(IntPtr webView, string uri);
    [DllImport(LibWebKit)] private static extern IntPtr webkit_print_operation_new(IntPtr webView);
    [DllImport(LibWebKit)] private static extern void webkit_print_operation_set_print_settings(IntPtr op, IntPtr settings);
    [DllImport(LibWebKit)] private static extern void webkit_print_operation_set_page_setup(IntPtr op, IntPtr setup);
    [DllImport(LibWebKit)] private static extern void webkit_print_operation_print(IntPtr op);
    [DllImport(LibWebKit)] private static extern int webkit_print_operation_run_dialog(IntPtr op, IntPtr parent);
    [DllImport(LibWebKit)] private static extern void webkit_web_view_get_snapshot(IntPtr webView, int region, int options, IntPtr cancellable, GAsyncReadyCallback callback, IntPtr data);
    [DllImport(LibWebKit)] private static extern IntPtr webkit_web_view_get_snapshot_finish(IntPtr webView, IntPtr result, out IntPtr error);
    [DllImport(LibCairo)] private static extern int cairo_surface_write_to_png(IntPtr surface, string filename);
    [DllImport(LibCairo)] private static extern void cairo_surface_destroy(IntPtr surface);

    private delegate bool GSourceFunc(IntPtr data);
    private delegate void LoadChangedHandler(IntPtr webView, int loadEvent, IntPtr data);
    private delegate void PrintFinishedHandler(IntPtr op, IntPtr data);
    private delegate void PrintFailedHandler(IntPtr op, IntPtr error, IntPtr data);
    private delegate void GAsyncReadyCallback(IntPtr source, IntPtr result, IntPtr data);

    private const int LoadFinished = 3;      // WEBKIT_LOAD_FINISHED
    private const int UnitMillimetre = 3;    // GtkUnit: none, points, inch, mm
    private const int PagesAll = 0;          // GTK_PRINT_PAGES_ALL
    private const int SnapshotWholeDocument = 1; // WEBKIT_SNAPSHOT_REGION_FULL_DOCUMENT
    private const int CairoStatusSuccess = 0;

    public static bool Available => OperatingSystem.IsLinux() || MacHelper != null;

    /// <summary>Renders an HTML file to a PDF. Returns false if WebKitGTK could not do it.</summary>
    public static Task<bool> ExportPdfAsync(string htmlPath, string pdfPath, double marginMm = 12)
        => OperatingSystem.IsMacOS()
            ? RunMacHelperAsync(htmlPath, pdfPath, "pdf", htmlPath, pdfPath, Number(marginMm))
            : RunAsync(htmlPath, pdfPath, marginMm, dialog: false);

    /// <summary>Opens the system print dialog for an HTML file.</summary>
    public static Task<bool> PrintAsync(string htmlPath, double marginMm = 12)
        => OperatingSystem.IsMacOS()
            ? RunMacHelperAsync(htmlPath, null, "print", htmlPath, Number(marginMm))
            : RunAsync(htmlPath, null, marginMm, dialog: true);

    /// <summary>Contents/MacOS/typedown-webkit-export next to the launcher, when running from Typedown.app.</summary>
    private static string? MacHelper
    {
        get
        {
            if (!OperatingSystem.IsMacOS()) return null;
            // The published files are in Contents/Resources/app (packaging/build-macos-app.sh).
            var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "MacOS", "typedown-webkit-export"));
            return File.Exists(path) ? path : null;
        }
    }

    private static string Number(double value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Runs the macOS helper; true when it exits with 0 and, for a file, the file is there.</summary>
    private static async Task<bool> RunMacHelperAsync(string htmlPath, string? outputPath, params string[] args)
    {
        if (MacHelper is not { } helper || !File.Exists(htmlPath)) return false;
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(helper) { RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!;
            var errors = process.StandardError.ReadToEndAsync();
            // The print panel waits for the user; an export finishes in seconds.
            using var timeout = new CancellationTokenSource(outputPath == null ? TimeSpan.FromHours(1) : TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(); Log.Write($"typedown-webkit-export {args[0]} timed out"); return false; }
            var stderr = (await errors).Trim();
            if (process.ExitCode != 0) Log.Write($"typedown-webkit-export {args[0]} failed ({process.ExitCode}){(stderr.Length > 0 ? ": " + stderr : "")}");
            return process.ExitCode == 0 && (outputPath == null || File.Exists(outputPath));
        }
        catch (Exception ex)
        {
            Log.Error("typedown-webkit-export", ex);
            return false;
        }
    }

    // The delegates are called from the GTK main loop and must outlive this method, so they are held for the
    // duration of the operation rather than left to the collector.
    private static readonly List<object> alive = new();

    private static Task<bool> RunAsync(string htmlPath, string? pdfPath, double marginMm, bool dialog)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Available || !File.Exists(htmlPath))
        {
            done.TrySetResult(false);
            return done.Task;
        }

        GSourceFunc start = _ =>
        {
            IntPtr window = IntPtr.Zero;
            try
            {
                window = gtk_offscreen_window_new();
                var view = webkit_web_view_new();
                gtk_widget_set_size_request(view, 900, 1200); // a page-ish width, so media queries see a document
                gtk_container_add(window, view);
                gtk_widget_show_all(window);

                var closedWindow = window;
                LoadChangedHandler? onLoad = null;
                onLoad = (_, loadEvent, __) =>
                {
                    if (loadEvent != LoadFinished) return;
                    try
                    {
                        var op = webkit_print_operation_new(view);

                        if (marginMm > 0)
                        {
                            var setup = gtk_page_setup_new();
                            // A page setup with no paper size prints nothing at all — the operation reports
                            // itself finished and no file appears — so start from the default paper.
                            gtk_page_setup_set_paper_size_and_default_margins(setup, gtk_paper_size_new(IntPtr.Zero));
                            gtk_page_setup_set_top_margin(setup, marginMm, UnitMillimetre);
                            gtk_page_setup_set_bottom_margin(setup, marginMm, UnitMillimetre);
                            gtk_page_setup_set_left_margin(setup, marginMm, UnitMillimetre);
                            gtk_page_setup_set_right_margin(setup, marginMm, UnitMillimetre);
                            webkit_print_operation_set_page_setup(op, setup);
                        }

                        PrintFinishedHandler onFinished = (_, __) =>
                        {
                            Log.Write($"webkit print finished; target {(pdfPath == null ? "(dialog)" : pdfPath)} exists = {pdfPath != null && File.Exists(pdfPath)}");
                            done.TrySetResult(pdfPath == null || File.Exists(pdfPath));
                            Cleanup(closedWindow);
                        };
                        PrintFailedHandler onFailed = (_, error, ___) =>
                        {
                            Log.Write($"webkit print operation failed: {ErrorMessage(error)}");
                            done.TrySetResult(false);
                            Cleanup(closedWindow);
                        };
                        lock (alive) { alive.Add(onFinished); alive.Add(onFailed); }
                        g_signal_connect_data(op, "finished", onFinished, IntPtr.Zero, IntPtr.Zero, 0);
                        g_signal_connect_data(op, "failed", onFailed, IntPtr.Zero, IntPtr.Zero, 0);

                        if (pdfPath != null)
                        {
                            // GTK's own "print to a file" backend: the printer is the file writer, and the
                            // destination and format come from the settings.
                            var settings = gtk_print_settings_new();
                            // Fresh settings carry no page range, and WebKit refuses to print without one.
                            gtk_print_settings_set_print_pages(settings, PagesAll);
                            gtk_print_settings_set_n_copies(settings, 1);
                            gtk_print_settings_set_printer(settings, "Print to File");
                            gtk_print_settings_set(settings, "output-uri", new Uri(pdfPath).AbsoluteUri);
                            gtk_print_settings_set(settings, "output-file-format", "pdf");
                            webkit_print_operation_set_print_settings(op, settings);
                            webkit_print_operation_print(op);
                        }
                        else
                        {
                            // The dialog reports its own outcome; "print" means the operation is running and
                            // "finished" will follow, anything else means the user backed out.
                            var response = webkit_print_operation_run_dialog(op, IntPtr.Zero);
                            if (response != 1) // WEBKIT_PRINT_OPERATION_RESPONSE_PRINT
                            {
                                done.TrySetResult(false);
                                Cleanup(closedWindow);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("webkit print", ex);
                        done.TrySetResult(false);
                        Cleanup(closedWindow);
                    }
                };
                lock (alive) { alive.Add(onLoad); }
                g_signal_connect_data(view, "load-changed", onLoad, IntPtr.Zero, IntPtr.Zero, 0);
                webkit_web_view_load_uri(view, new Uri(htmlPath).AbsoluteUri);
            }
            catch (Exception ex)
            {
                Log.Error("webkit print setup", ex);
                done.TrySetResult(false);
                Cleanup(window);
            }
            return false; // once
        };

        lock (alive) { alive.Add(start); }
        g_idle_add(start, IntPtr.Zero);

        // A page that never finishes loading must not leave the caller waiting for ever.
        _ = Task.Delay(TimeSpan.FromSeconds(60)).ContinueWith(_ => done.TrySetResult(false));
        return done.Task;
    }

    /// <summary>Renders an HTML file to a PNG: the whole document, however long it is, in one picture.</summary>
    public static Task<bool> ExportImageAsync(string htmlPath, string pngPath, int width = 1000, int height = 900)
    {
        if (OperatingSystem.IsMacOS()) return RunMacHelperAsync(htmlPath, pngPath, "png", htmlPath, pngPath, width.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Available || !File.Exists(htmlPath))
        {
            done.TrySetResult(false);
            return done.Task;
        }

        GSourceFunc start = _ =>
        {
            IntPtr window = IntPtr.Zero;
            try
            {
                window = gtk_offscreen_window_new();
                var view = webkit_web_view_new();
                // The picture grows to the whole document; this height only decides how much empty space a
                // document shorter than one screen comes with (the exported page asks for a full screen).
                gtk_widget_set_size_request(view, width, height);
                gtk_container_add(window, view);
                gtk_widget_show_all(window);
                var closedWindow = window;

                LoadChangedHandler onLoad = (_, loadEvent, __) =>
                {
                    if (loadEvent != LoadFinished) return;
                    GAsyncReadyCallback onSnapshot = (source, result, ___) =>
                    {
                        var surface = IntPtr.Zero;
                        try
                        {
                            surface = webkit_web_view_get_snapshot_finish(source, result, out var error);
                            if (surface == IntPtr.Zero)
                            {
                                Log.Write($"webkit snapshot failed: {ErrorMessage(error)}");
                                done.TrySetResult(false);
                                return;
                            }
                            var status = cairo_surface_write_to_png(surface, pngPath);
                            if (status != CairoStatusSuccess) Log.Write($"writing the picture to {pngPath} failed with cairo status {status}");
                            done.TrySetResult(status == CairoStatusSuccess && File.Exists(pngPath));
                        }
                        catch (Exception ex)
                        {
                            Log.Error("webkit snapshot", ex);
                            done.TrySetResult(false);
                        }
                        finally
                        {
                            if (surface != IntPtr.Zero) cairo_surface_destroy(surface);
                            Cleanup(closedWindow);
                        }
                    };
                    lock (alive) { alive.Add(onSnapshot); }
                    // The page has just finished loading; giving the layout a moment keeps late images and
                    // web fonts from being left out of the picture.
                    g_timeout_add(400, () => { webkit_web_view_get_snapshot(view, SnapshotWholeDocument, 0, IntPtr.Zero, onSnapshot, IntPtr.Zero); return false; });
                };
                lock (alive) { alive.Add(onLoad); }
                g_signal_connect_data(view, "load-changed", onLoad, IntPtr.Zero, IntPtr.Zero, 0);
                webkit_web_view_load_uri(view, new Uri(htmlPath).AbsoluteUri);
            }
            catch (Exception ex)
            {
                Log.Error("webkit snapshot setup", ex);
                done.TrySetResult(false);
                Cleanup(window);
            }
            return false;
        };

        lock (alive) { alive.Add(start); }
        g_idle_add(start, IntPtr.Zero);
        _ = Task.Delay(TimeSpan.FromSeconds(60)).ContinueWith(_ => done.TrySetResult(false));
        return done.Task;
    }

    /// <summary>g_timeout_add with the delegate kept alive for as long as it can be called.</summary>
    private static void g_timeout_add(uint ms, Func<bool> action)
    {
        GSourceFunc wrapper = _ => action();
        lock (alive) { alive.Add(wrapper); }
        g_timeout_add(ms, wrapper, IntPtr.Zero);
    }

    [DllImport(LibGLib, EntryPoint = "g_timeout_add")] private static extern uint g_timeout_add(uint interval, GSourceFunc function, IntPtr data);

    /// <summary>The text of a GError (domain, code, then the message pointer).</summary>
    private static string ErrorMessage(IntPtr error)
    {
        if (error == IntPtr.Zero) return "(no error given)";
        try
        {
            var message = Marshal.ReadIntPtr(error, 8);
            return Marshal.PtrToStringUTF8(message) ?? "(empty)";
        }
        catch { return "(unreadable)"; }
    }

    private static void Cleanup(IntPtr window)
    {
        try { if (window != IntPtr.Zero) gtk_widget_destroy(window); } catch { }
        lock (alive) { alive.Clear(); }
    }
}
