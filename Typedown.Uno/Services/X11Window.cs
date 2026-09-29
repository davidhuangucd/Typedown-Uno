using System.Runtime.InteropServices;
using System.Text;

namespace Typedown.Uno.Services;

/// <summary>
/// Two things Uno's X11 backend does not do for us:
/// <list type="bullet">
/// <item>the window title is published as a Latin-1 <c>WM_NAME</c> only, so anything non-ASCII (a Chinese file
/// name, the default "未命名") shows up as mojibake and <c>_NET_WM_NAME</c> is missing entirely;</item>
/// <item>the GTK web view binds unversioned SONAMEs (<c>libgdk-3.so</c>, <c>libsoup-3.0.so</c>,
/// <c>libwebkit2gtk-4.1.so</c>) that only the -dev packages provide, so a plain desktop cannot load it.</item>
/// </list>
/// Both are fixed here so a deployed package needs no root-installed symlinks.
/// </summary>
public static class X11Window
{
    private const string LibX11 = "libX11.so.6";

    [DllImport(LibX11)] private static extern IntPtr XOpenDisplay(IntPtr display);
    [DllImport(LibX11)] private static extern int XCloseDisplay(IntPtr display);
    [DllImport(LibX11)] private static extern IntPtr XInternAtom(IntPtr display, string name, bool onlyIfExists);
    [DllImport(LibX11)] private static extern int XDefaultScreen(IntPtr display);
    [DllImport(LibX11)] private static extern IntPtr XRootWindow(IntPtr display, int screen);
    [DllImport(LibX11)] private static extern int XQueryTree(IntPtr display, IntPtr window, out IntPtr root, out IntPtr parent, out IntPtr children, out uint count);
    [DllImport(LibX11)] private static extern int XFree(IntPtr data);
    [DllImport(LibX11)] private static extern int XChangeProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr type, int format, int mode, byte[] data, int elements);
    [DllImport(LibX11)] private static extern int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr offset, IntPtr length, bool delete,
        IntPtr requestedType, out IntPtr actualType, out int actualFormat, out IntPtr items, out IntPtr bytesAfter, out IntPtr data);
    [DllImport(LibX11)] private static extern int XFlush(IntPtr display);

    private static IntPtr display;

    private static bool EnsureDisplay()
    {
        if (!OperatingSystem.IsLinux()) return false;
        if (display == IntPtr.Zero) display = XOpenDisplay(IntPtr.Zero);
        return display != IntPtr.Zero;
    }

    /// <summary>Windows already handed to a page, so a second window does not claim the first one's handle.</summary>
    private static readonly HashSet<IntPtr> claimed = new();

    /// <summary>
    /// This page's X11 window. Uno exposes no native handle, so the window is located in the X tree instead:
    /// every candidate carries the app's <c>WM_CLASS</c>, and a window is claimed only once so several windows
    /// keep their own handles. Windows are created with a unique title, which pins the match down when the
    /// title has survived; under a window manager Uno republishes its own title, so the marker cannot be
    /// relied on. The search is recursive because a reparenting window manager (any normal desktop) puts the
    /// window inside a frame instead of directly under the root.
    /// </summary>
    public static IntPtr FindByTitle(string marker)
    {
        if (!EnsureDisplay()) return IntPtr.Zero;
        try
        {
            var candidates = new List<IntPtr>();
            Collect(XRootWindow(display, XDefaultScreen(display)), 0, candidates);
            lock (claimed)
            {
                var free = candidates.Where(w => !claimed.Contains(w)).ToList();
                if (free.Count == 0) return IntPtr.Zero;
                var window = free.FirstOrDefault(w => !string.IsNullOrEmpty(marker) && ReadText(w, "WM_NAME").Contains(marker, StringComparison.Ordinal));
                if (window == IntPtr.Zero) window = free[^1]; // the most recently created one
                claimed.Add(window);
                return window;
            }
        }
        catch (Exception ex)
        {
            Log.Error("find window", ex);
        }
        return IntPtr.Zero;
    }

    /// <summary>Depth-limited walk of the X tree collecting the windows that carry the app's WM_CLASS.</summary>
    private static void Collect(IntPtr parent, int depth, List<IntPtr> found)
    {
        if (depth > 4) return;
        if (XQueryTree(display, parent, out _, out _, out var children, out var count) == 0 || children == IntPtr.Zero) return;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var child = Marshal.ReadIntPtr(children, i * IntPtr.Size);
                if (HasClass(child)) found.Add(child);
                else Collect(child, depth + 1, found);
            }
        }
        finally
        {
            XFree(children);
        }
    }

    private static string ReadTextOn(IntPtr connection, IntPtr candidate, string property)
    {
        if (XGetWindowProperty(connection, candidate, XInternAtom(connection, property, false), IntPtr.Zero, (IntPtr)64, false, IntPtr.Zero,
                out _, out _, out var items, out _, out var data) != 0 || data == IntPtr.Zero)
            return "";
        try
        {
            return Marshal.PtrToStringUTF8(data, (int)items) ?? "";
        }
        finally
        {
            XFree(data);
        }
    }

    private static string ReadText(IntPtr candidate, string property)
    {
        if (XGetWindowProperty(display, candidate, XInternAtom(display, property, false), IntPtr.Zero, (IntPtr)256, false, IntPtr.Zero,
                out _, out _, out var items, out _, out var data) != 0 || data == IntPtr.Zero)
            return "";
        try
        {
            return Marshal.PtrToStringUTF8(data, (int)items) ?? "";
        }
        finally
        {
            XFree(data);
        }
    }

    /// <summary>Publishes the title as UTF-8 on both WM_NAME and _NET_WM_NAME.</summary>
    public static void SetTitle(IntPtr window, string title)
    {
        if (!EnsureDisplay() || window == IntPtr.Zero) return;
        try
        {

            var bytes = Encoding.UTF8.GetBytes(title);
            var utf8 = XInternAtom(display, "UTF8_STRING", false);
            foreach (var name in new[] { "_NET_WM_NAME", "WM_NAME", "_NET_WM_ICON_NAME", "WM_ICON_NAME" })
                XChangeProperty(display, window, XInternAtom(display, name, false), utf8, 8, /* PropModeReplace */ 0, bytes, bytes.Length);
            XFlush(display);
        }
        catch (Exception ex)
        {
            Log.Error("set window title", ex);
        }
    }

    /// <summary>
    /// Publishes the app icon at several sizes as <c>_NET_WM_ICON</c>. Uno only sets a single small size, which
    /// looks blurry in docks and task switchers.
    /// </summary>
    public static void SetIcon(IntPtr window, string pngPath)
    {
        if (!EnsureDisplay() || window == IntPtr.Zero || !File.Exists(pngPath)) return;
        try
        {

            using var original = SkiaSharp.SKBitmap.Decode(pngPath);
            if (original == null) return;
            // One property write per size (PropModeAppend): a single request with every size would exceed the
            // X maximum request length and be dropped. 128 is the largest size that fits comfortably.
            var mode = 0; // PropModeReplace for the first chunk, PropModeAppend afterwards
            var icon = XInternAtom(display, "_NET_WM_ICON", false);
            var cardinal = XInternAtom(display, "CARDINAL", false);
            foreach (var size in new[] { 128, 64, 48, 32, 16 })
            {
                var payload = new List<long>();
                using var scaled = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(size, size, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul));
                using (var canvas = new SkiaSharp.SKCanvas(scaled))
                {
                    canvas.Clear(SkiaSharp.SKColors.Transparent);
                    canvas.DrawBitmap(original, new SkiaSharp.SKRect(0, 0, original.Width, original.Height), new SkiaSharp.SKRect(0, 0, size, size));
                }
                payload.Add(size);
                payload.Add(size);
                for (var y = 0; y < size; y++)
                    for (var x = 0; x < size; x++)
                    {
                        var color = scaled.GetPixel(x, y);
                        payload.Add((long)(uint)((color.Alpha << 24) | (color.Red << 16) | (color.Green << 8) | color.Blue));
                    }
                // format 32 means "long" in Xlib, i.e. 8 bytes per element on 64-bit.
                var bytes = new byte[payload.Count * sizeof(long)];
                Buffer.BlockCopy(payload.ToArray(), 0, bytes, 0, bytes.Length);
                XChangeProperty(display, window, icon, cardinal, 32, mode, bytes, payload.Count);
                mode = 2; // PropModeAppend
            }
            XFlush(display);
        }
        catch (Exception ex)
        {
            Log.Error("set window icon", ex);
        }
    }

    /// <summary>WM_CLASS of the shell window (the app id from the project); the web view uses a different one.</summary>
    private const string AppWindowClass = "uk.mingdan.typedown";

    private static bool HasClass(IntPtr candidate)
    {
        var wmClass = XInternAtom(display, "WM_CLASS", false);
        if (XGetWindowProperty(display, candidate, wmClass, IntPtr.Zero, (IntPtr)64, false, IntPtr.Zero,
                out _, out _, out var items, out _, out var data) != 0 || data == IntPtr.Zero)
            return false;
        try
        {
            var text = Marshal.PtrToStringUTF8(data, (int)items) ?? "";
            return text.Contains(AppWindowClass, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            XFree(data);
        }
    }

    // ---- mouse wheel ---------------------------------------------------------------------------------------
    // Uno's X11 backend reads scrolling from XInput2 scroll valuators, which only devices with a real scroll
    // axis have. A VNC session (and anything else driven through XTEST) has a pointer with buttons and nothing
    // else, so the wheel arrives as the legacy buttons 4-7 and the XAML layer never scrolls — the editor still
    // does, because the web view is a native GTK window that reads those buttons itself. Listening for them on
    // a connection of our own costs nothing and fixes the side pane; XInput2 lets several clients select the
    // same events, so Uno keeps receiving everything it did before.

    private const string LibXi = "libXi.so.6";
    private const int GenericEvent = 35;
    private const int XI_ButtonPress = 4;
    private const int XIAllMasterDevices = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct XIEventMask
    {
        public int deviceid;
        public int mask_len;
        public IntPtr mask;
    }

    [DllImport(LibX11)] private static extern int XQueryExtension(IntPtr display, string name, out int opcode, out int firstEvent, out int firstError);
    [DllImport(LibX11)] private static extern int XNextEvent(IntPtr display, byte[] eventData);
    [DllImport(LibX11)] private static extern bool XGetEventData(IntPtr display, byte[] cookie);
    [DllImport(LibX11)] private static extern void XFreeEventData(IntPtr display, byte[] cookie);
    [DllImport(LibXi)] private static extern int XIQueryVersion(IntPtr display, ref int major, ref int minor);
    [DllImport(LibXi)] private static extern int XISelectEvents(IntPtr display, IntPtr window, ref XIEventMask masks, int numMasks);

    /// <summary>A wheel notch: the window it happened on, the position in its pixels, and the notch count (up is positive).</summary>
    public static event Action<IntPtr, int, int, int>? WheelScrolled;

    private static readonly HashSet<IntPtr> wheelWindows = new();

    /// <summary>
    /// Starts the fallback wheel listener for <paramref name="window"/>; one per window, each on its own
    /// connection because the loop blocks. Safe to call more than once.
    /// </summary>
    public static void ListenForWheel(IntPtr window)
    {
        if (!OperatingSystem.IsLinux() || window == IntPtr.Zero) return;
        lock (wheelWindows)
        {
            if (!wheelWindows.Add(window)) return;
        }
        var thread = new Thread(() => WheelLoop(window)) { IsBackground = true, Name = "x11-wheel" };
        thread.Start();
    }

    private static void WheelLoop(IntPtr window)
    {
        // a connection of its own: the display used for titles and icons is touched from the UI thread
        var connection = XOpenDisplay(IntPtr.Zero);
        if (connection == IntPtr.Zero) return;
        try
        {
            if (XQueryExtension(connection, "XInputExtension", out var opcode, out _, out _) == 0) return;
            int major = 2, minor = 2;
            if (XIQueryVersion(connection, ref major, ref minor) != 0 /* Success */) return;

            var mask = Marshal.AllocHGlobal(4);
            try
            {
                for (var i = 0; i < 4; i++) Marshal.WriteByte(mask, i, 0);
                Marshal.WriteByte(mask, XI_ButtonPress >> 3, (byte)(1 << (XI_ButtonPress & 7)));
                var events = new XIEventMask { deviceid = XIAllMasterDevices, mask_len = 4, mask = mask };
                // Uno draws into a child window of the top-level one and selects the same events there, which
                // stops them propagating up, so the selection covers the children as well. The web view's own
                // window is left alone: WebKit already handles the wheel inside the document.
                void SelectOn(IntPtr target, int depth)
                {
                    XISelectEvents(connection, target, ref events, 1);
                    if (depth > 2) return;
                    if (XQueryTree(connection, target, out _, out _, out var children, out var count) == 0 || children == IntPtr.Zero) return;
                    try
                    {
                        for (var i = 0; i < count; i++)
                        {
                            var child = Marshal.ReadIntPtr(children, i * IntPtr.Size);
                            if (ReadTextOn(connection, child, "WM_NAME").StartsWith("Uno WebView", StringComparison.Ordinal)) continue;
                            SelectOn(child, depth + 1);
                        }
                    }
                    finally
                    {
                        XFree(children);
                    }
                }
                SelectOn(window, 0);
                XFlush(connection);

                var buffer = new byte[256]; // an XEvent is 192 bytes
                while (true)
                {
                    XNextEvent(connection, buffer);
                    if (BitConverter.ToInt32(buffer, 0) != GenericEvent) continue;
                    if (BitConverter.ToInt32(buffer, 32) != opcode) continue;
                    if (BitConverter.ToInt32(buffer, 36) != XI_ButtonPress) continue;
                    if (!XGetEventData(connection, buffer)) continue;
                    try
                    {
                        var data = MemoryMarshal.Read<IntPtr>(buffer.AsSpan(48));
                        if (data == IntPtr.Zero) continue;
                        // XIDeviceEvent: detail at 56, event_x/event_y (doubles) at 104/112
                        var button = Marshal.ReadInt32(data, 56);
                        var notches = button switch { 4 => 1, 5 => -1, _ => 0 };
                        if (notches == 0) continue;
                        var x = (int)BitConverter.Int64BitsToDouble(Marshal.ReadInt64(data, 104));
                        var y = (int)BitConverter.Int64BitsToDouble(Marshal.ReadInt64(data, 112));
                        WheelScrolled?.Invoke(window, x, y, notches);
                    }
                    finally
                    {
                        XFreeEventData(connection, buffer);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(mask);
            }
        }
        catch (Exception ex)
        {
            Log.Error("wheel listener", ex);
        }
    }

    /// <summary>
    /// Lets Uno's web view load GTK/WebKit through the versioned SONAMEs, so the unversioned symlinks from the
    /// -dev packages are not needed. Registered for every assembly that P/Invokes them.
    /// </summary>
    public static void InstallLibraryResolver()
    {
        if (!OperatingSystem.IsLinux()) return;
        AppDomain.CurrentDomain.AssemblyLoad += (_, e) => TryRegister(e.LoadedAssembly);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) TryRegister(assembly);
    }

    private static readonly Dictionary<string, string[]> Alternatives = new(StringComparer.OrdinalIgnoreCase)
    {
        ["libwebkit2gtk-4.1.so"] = new[] { "libwebkit2gtk-4.1.so.0" },
        ["libwebkit2gtk-4.0.so"] = new[] { "libwebkit2gtk-4.0.so.37", "libwebkit2gtk-4.1.so.0" },
        ["libjavascriptcoregtk-4.1.so"] = new[] { "libjavascriptcoregtk-4.1.so.0" },
        ["libgdk-3.so"] = new[] { "libgdk-3.so.0" },
        ["libgtk-3.so"] = new[] { "libgtk-3.so.0" },
        ["libsoup-3.0.so"] = new[] { "libsoup-3.0.so.0" },
        ["libgio-2.0.so"] = new[] { "libgio-2.0.so.0" },
        ["libglib-2.0.so"] = new[] { "libglib-2.0.so.0" },
        ["libgobject-2.0.so"] = new[] { "libgobject-2.0.so.0" },
        ["libcairo.so"] = new[] { "libcairo.so.2" },
        ["libpango-1.0.so"] = new[] { "libpango-1.0.so.0" },
        ["libpangocairo-1.0.so"] = new[] { "libpangocairo-1.0.so.0" },
        ["libgdk_pixbuf-2.0.so"] = new[] { "libgdk_pixbuf-2.0.so.0" },
        ["libatk-1.0.so"] = new[] { "libatk-1.0.so.0" },
    };

    private static readonly HashSet<string> registered = new();

    private static void TryRegister(System.Reflection.Assembly assembly)
    {
        var name = assembly.GetName().Name ?? "";
        // Only the assemblies that bind GTK/WebKit: Uno's own SkiaSharp/HarfBuzzSharp set their own resolvers
        // and a second one throws (which took the whole app down when the filter was wider).
        var wanted = name == "Uno.UI.WebView.Skia.X11"
            || (name.EndsWith("Sharp", StringComparison.Ordinal)
                && name is not ("SkiaSharp" or "HarfBuzzSharp"));
        if (!wanted) return;
        lock (registered)
        {
            if (!registered.Add(name)) return;
        }
        try
        {
            NativeLibrary.SetDllImportResolver(assembly, (library, _, _) =>
            {
                if (NativeLibrary.TryLoad(library, out var handle)) return handle;
                if (Alternatives.TryGetValue(library, out var candidates))
                    foreach (var candidate in candidates)
                        if (NativeLibrary.TryLoad(candidate, out handle))
                            return handle;
                return IntPtr.Zero;
            });
        }
        catch (InvalidOperationException)
        {
            // a resolver was already set for this assembly
        }
    }
}
