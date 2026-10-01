using System.Runtime.InteropServices;

namespace Typedown.Uno.Services;

/// <summary>
/// Makes the editor draw its caret.
///
/// Uno puts the WebKitGTK view in a GTK window of its own, override-redirect and reparented into the app's
/// window, so the window manager never manages it and GTK never marks it active. WebKit accepts keys in an
/// inactive window but paints no caret in it: on every real desktop the reader typed into a document that
/// showed no cursor. Under a bare Xvfb there is no window manager, GTK activates the window itself, and the
/// caret showed — which is why this was never seen here. The GTK window is told it is active with a
/// synthetic focus-in, and told it is not when the app's window is deactivated, on the GTK thread.
/// </summary>
public static class WebViewCaret
{
    private const string LibGtk = "libgtk-3.so.0";
    private const string LibGdk = "libgdk-3.so.0";
    private const string LibGLib = "libglib-2.0.so.0";
    private const string LibGObject = "libgobject-2.0.so.0";

    private const int GdkFocusChange = 12;

    [DllImport(LibGtk)] private static extern IntPtr gtk_widget_get_toplevel(IntPtr widget);
    [DllImport(LibGtk)] private static extern IntPtr gtk_widget_get_window(IntPtr widget);
    [DllImport(LibGtk)] private static extern void gtk_widget_send_focus_change(IntPtr widget, IntPtr evt);
    [DllImport(LibGtk)] private static extern void gtk_widget_grab_focus(IntPtr widget);
    [DllImport(LibGtk)] private static extern bool gtk_window_is_active(IntPtr window);
    [DllImport(LibGdk)] private static extern IntPtr gdk_event_new(int type);
    [DllImport(LibGdk)] private static extern void gdk_event_free(IntPtr evt);
    [DllImport(LibGObject)] private static extern IntPtr g_object_ref(IntPtr obj);
    [DllImport(LibGLib)] private static extern uint g_idle_add(GSourceFunc function, IntPtr data);

    [DllImport(LibGdk)] private static extern IntPtr gdk_window_get_display(IntPtr window);
    [DllImport(LibGdk)] private static extern IntPtr gdk_x11_display_get_xdisplay(IntPtr display);
    [DllImport(LibGdk)] private static extern ulong gdk_x11_window_get_xid(IntPtr window);
    [DllImport(LibGdk)] private static extern void gdk_x11_display_error_trap_push(IntPtr display);
    [DllImport(LibGdk)] private static extern void gdk_x11_display_error_trap_pop_ignored(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XGetInputFocus(IntPtr display, out ulong focus, out int revertTo);
    [DllImport("libX11.so.6")] private static extern int XSetInputFocus(IntPtr display, ulong window, int revertTo, ulong time);
    [DllImport("libX11.so.6")] private static extern int XFlush(IntPtr display);

    private const int RevertToParent = 2;

    private delegate bool GSourceFunc(IntPtr data);

    // Called from the GTK main loop, like pending.
    private static GSourceFunc? pendingFocus;
    private static GSourceFunc? pendingXid;

    // The X window of each web view's GTK window, found on the GTK thread (WindowOf).
    private static readonly Dictionary<IntPtr, ulong> xids = new();

    /// <summary>
    /// The X window of the web view's own GTK window - the one that has the keyboard when the editor has it - or 0
    /// until it is known (it is looked up on the GTK thread the first time it is asked for).
    /// </summary>
    public static ulong WindowOf(object? webViewControl)
    {
        if (!OperatingSystem.IsLinux() || webViewControl == null) return 0;
        try
        {
            var view = WebViewBackground.FindWebKitHandle(webViewControl);
            if (view == IntPtr.Zero) return 0;
            lock (xids) if (xids.TryGetValue(view, out var known)) return known;
            pendingXid = _ =>
            {
                try
                {
                    var window = gtk_widget_get_toplevel(view);
                    var gdkWindow = window == IntPtr.Zero ? IntPtr.Zero : gtk_widget_get_window(window);
                    if (gdkWindow != IntPtr.Zero) lock (xids) xids[view] = gdk_x11_window_get_xid(gdkWindow);
                }
                catch (Exception ex)
                {
                    Log.Error("web view window (gtk thread)", ex);
                }
                return false;
            };
            g_idle_add(pendingXid, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Error("web view window", ex);
        }
        return 0;
    }

    /// <summary>
    /// Gives the keyboard back to the editor, as a click in it does. A dialog takes the keyboard into the app's own
    /// window, and nothing hands it back when the dialog closes: the web view's window is override-redirect, so the
    /// window manager does not give it the focus and a focus request through GDK goes to the window manager, which
    /// ignores it. Until the reader clicked, keys went nowhere - neither typing nor Ctrl+, reached the page.
    /// </summary>
    public static void Focus(object? webViewControl)
    {
        if (!OperatingSystem.IsLinux() || webViewControl == null) return;
        try
        {
            var view = WebViewBackground.FindWebKitHandle(webViewControl);
            if (view == IntPtr.Zero) return;
            pendingFocus = _ =>
            {
                try
                {
                    var window = gtk_widget_get_toplevel(view);
                    var gdkWindow = window == IntPtr.Zero ? IntPtr.Zero : gtk_widget_get_window(window);
                    if (gdkWindow == IntPtr.Zero) return false;
                    // A window that is not viewable at this moment (a window closing as it opens) answers with BadMatch,
                    // and GTK's handler for an X error ends the process: the request goes out inside GDK's error trap.
                    var gdkDisplay = gdk_window_get_display(gdkWindow);
                    var display = gdk_x11_display_get_xdisplay(gdkDisplay);
                    gdk_x11_display_error_trap_push(gdkDisplay);
                    XSetInputFocus(display, gdk_x11_window_get_xid(gdkWindow), RevertToParent, 0);
                    XFlush(display);
                    gdk_x11_display_error_trap_pop_ignored(gdkDisplay);
                    if (!gtk_window_is_active(window))
                    {
                        var evt = gdk_event_new(GdkFocusChange);
                        Marshal.WriteIntPtr(evt, IntPtr.Size, g_object_ref(gdkWindow)); // freed with the event
                        Marshal.WriteByte(evt, 2 * IntPtr.Size, 1);
                        Marshal.WriteInt16(evt, 2 * IntPtr.Size + 2, 1);
                        gtk_widget_send_focus_change(window, evt);
                        gdk_event_free(evt);
                    }
                    gtk_widget_grab_focus(view);
                    Log.Write("web view given the keyboard");
                }
                catch (Exception ex)
                {
                    Log.Error("web view focus (gtk thread)", ex);
                }
                return false;
            };
            g_idle_add(pendingFocus, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Error("web view focus", ex);
        }
    }

    private static GSourceFunc? pendingRelease;

    /// <summary>
    /// Takes the keyboard from the web view for the app's own window, where a dialog opening in it reads its keys. The
    /// X focus stays wherever it was when the dialog opened: with the editor typed in, keys went on into the document
    /// under the dialog - a letter changed it, Escape never reached the dialog. Left alone if the web view does not
    /// have the keyboard (a field of the page, another app).
    /// </summary>
    public static void Release(object? webViewControl, IntPtr appWindow)
    {
        if (!OperatingSystem.IsLinux() || webViewControl == null || appWindow == IntPtr.Zero) return;
        try
        {
            var view = WebViewBackground.FindWebKitHandle(webViewControl);
            if (view == IntPtr.Zero) return;
            pendingRelease = _ =>
            {
                try
                {
                    var window = gtk_widget_get_toplevel(view);
                    var gdkWindow = window == IntPtr.Zero ? IntPtr.Zero : gtk_widget_get_window(window);
                    if (gdkWindow == IntPtr.Zero) return false;
                    var gdkDisplay = gdk_window_get_display(gdkWindow);
                    var display = gdk_x11_display_get_xdisplay(gdkDisplay);
                    XGetInputFocus(display, out var focus, out int revertTo);
                    if (focus != gdk_x11_window_get_xid(gdkWindow)) return false;
                    // As in Focus: an X error (the app window going away) would otherwise end the process.
                    gdk_x11_display_error_trap_push(gdkDisplay);
                    XSetInputFocus(display, (ulong)appWindow.ToInt64(), RevertToParent, 0);
                    XFlush(display);
                    gdk_x11_display_error_trap_pop_ignored(gdkDisplay);
                    Log.Write("keyboard taken from the web view for a dialog");
                }
                catch (Exception ex)
                {
                    Log.Error("web view release (gtk thread)", ex);
                }
                return false;
            };
            g_idle_add(pendingRelease, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Error("web view release", ex);
        }
    }

    // Called from the GTK main loop, so it has to outlive the call that queued it.
    private static GSourceFunc? pending;

    /// <summary>Tells the web view's GTK window whether the app is active, so the caret is drawn or not.</summary>
    public static void SetActive(object? webViewControl, bool active)
    {
        if (!OperatingSystem.IsLinux() || webViewControl == null) return;
        try
        {
            var view = WebViewBackground.FindWebKitHandle(webViewControl);
            if (view == IntPtr.Zero) return;
            pending = _ =>
            {
                try
                {
                    var window = gtk_widget_get_toplevel(view);
                    var gdkWindow = window == IntPtr.Zero ? IntPtr.Zero : gtk_widget_get_window(window);
                    if (gdkWindow == IntPtr.Zero) return false;
                    if (gtk_window_is_active(window) == active) return false;
                    // GdkEventFocus: { GdkEventType type; GdkWindow* window; gint8 send_event; gint16 in; }
                    var evt = gdk_event_new(GdkFocusChange);
                    Marshal.WriteIntPtr(evt, IntPtr.Size, g_object_ref(gdkWindow)); // freed with the event
                    Marshal.WriteByte(evt, 2 * IntPtr.Size, 1);
                    Marshal.WriteInt16(evt, 2 * IntPtr.Size + 2, (short)(active ? 1 : 0));
                    gtk_widget_send_focus_change(window, evt);
                    gdk_event_free(evt);
                    if (active) gtk_widget_grab_focus(view);
                    Log.Write($"web view window marked {(active ? "active" : "inactive")}: caret {(active ? "shown" : "hidden")}");
                }
                catch (Exception ex)
                {
                    Log.Error("web view caret (gtk thread)", ex);
                }
                return false;
            };
            g_idle_add(pending, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Error("web view caret", ex);
        }
    }
}
