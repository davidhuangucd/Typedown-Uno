using System.Runtime.InteropServices;

namespace Typedown.Uno.Services;

/// <summary>
/// On macOS the program keeps running once its last window is closed, as Mac programs do, and quits with Cmd+Q.
/// Starting it is most of the wait before a document shows (the .NET runtime, Uno and the window take about a
/// second before the editor page even starts loading), so a file opened from Finder afterwards only waits for its
/// window and editor. Uno's application delegate (UNOApplicationDelegate in libUnoNativeMac) answers
/// applicationShouldTerminateAfterLastWindowClosed: with YES and has no applicationShouldHandleReopen:, so a click
/// on the Dock icon would find no window to show; this replaces the one and adds the other at startup.
/// </summary>
public static class MacKeepRunning
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    private static readonly object gate = new();
    private static Action? handler;
    private static bool reopenPending;
    // The native side keeps pointers to them for the life of the process.
    private static ShouldTerminateFn? shouldTerminate;
    private static ReopenFn? reopen;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ShouldTerminateFn(IntPtr self, IntPtr cmd, IntPtr application);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ReopenFn(IntPtr self, IntPtr cmd, IntPtr application, byte hasVisibleWindows);

    /// <summary>True once installed: closing the last window leaves the program running.</summary>
    public static bool Active { get; private set; }

    /// <summary>Called once from Main after <see cref="MacOpenDocuments.Install"/>, which loads Uno's native library.</summary>
    public static void Install()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var cls = objc_getClass("UNOApplicationDelegate");
            if (cls == IntPtr.Zero)
            {
                Log.Write("keep running: no UNOApplicationDelegate class, the program quits with its last window");
                return;
            }
            shouldTerminate = (_, _, _) => 0;
            reopen = OnReopen;
            // B@:@ — returns BOOL; self, _cmd, the NSApplication. Replaces Uno's, or adds it if Uno drops it.
            class_replaceMethod(cls, sel_registerName("applicationShouldTerminateAfterLastWindowClosed:"),
                Marshal.GetFunctionPointerForDelegate(shouldTerminate), "B@:@");
            // B@:@B — returns BOOL; self, _cmd, the NSApplication and whether a window is visible.
            if (!class_addMethod(cls, sel_registerName("applicationShouldHandleReopen:hasVisibleWindows:"),
                    Marshal.GetFunctionPointerForDelegate(reopen), "B@:@B"))
                Log.Write("keep running: the delegate already implements applicationShouldHandleReopen:, left as it is");
            Active = true;
        }
        catch (Exception ex)
        {
            Log.Error("keep running: install", ex);
        }
    }

    /// <summary>Sends a Dock click that finds no window to <paramref name="onReopen"/> (called on the AppKit main thread).</summary>
    public static void Attach(Action onReopen)
    {
        bool pending;
        lock (gate)
        {
            handler = onReopen;
            pending = reopenPending;
            reopenPending = false;
        }
        if (pending) onReopen();
    }

    private static byte OnReopen(IntPtr self, IntPtr cmd, IntPtr application, byte hasVisibleWindows)
    {
        try
        {
            if (hasVisibleWindows != 0) return 1;
            Action? target;
            lock (gate)
            {
                target = handler;
                if (target == null) reopenPending = true;
            }
            target?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("keep running: reopen", ex);
        }
        return 1;
    }

    [DllImport(ObjC)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjC)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjC)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool class_addMethod(IntPtr cls, IntPtr name, IntPtr imp, string types);

    [DllImport(ObjC)]
    private static extern IntPtr class_replaceMethod(IntPtr cls, IntPtr name, IntPtr imp, string types);
}
