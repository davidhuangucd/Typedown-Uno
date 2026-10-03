using System.Runtime.InteropServices;

namespace Typedown.Uno.Services;

/// <summary>
/// Files opened from Finder on macOS (double-click, Open With, a drop on the Dock icon) reach the app as an
/// open-documents Apple Event, never on the command line, and Uno's application delegate (UNOApplicationDelegate in
/// libUnoNativeMac) does not implement application:openURLs:, so AppKit had nowhere to deliver them and the app came
/// up empty. This adds that method to the delegate's class at startup.
///
/// At launch AppKit delivers the event before the app's OnLaunched, so the files wait here until App attaches its
/// handler; afterwards (a file opened while the app runs) they go to that handler straight away. A launch from a
/// terminal still passes files on the command line and to a running instance over the single-instance socket.
/// </summary>
public static class MacOpenDocuments
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    private static readonly object gate = new();
    private static readonly List<string> pending = new();
    private static Action<IReadOnlyList<string>>? handler;
    private static OpenUrlsFn? openUrls; // the native side keeps a pointer to it for the life of the process

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OpenUrlsFn(IntPtr self, IntPtr cmd, IntPtr application, IntPtr urls);

    /// <summary>Called once from Main, before the host runs: teaches the application delegate to take files.</summary>
    public static void Install()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            // The class lives in Uno's native library, which the host would only load once it starts running; it
            // links @rpath/libSkiaSharp.dylib, which dyld can only resolve once that library is loaded.
            foreach (var lib in new[] { "libSkiaSharp.dylib", "libUnoNativeMac.dylib" })
                NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, lib), out _);
            var cls = objc_getClass("UNOApplicationDelegate");
            if (cls == IntPtr.Zero)
            {
                Log.Write("open documents: no UNOApplicationDelegate class, files from Finder are not handled");
                return;
            }
            openUrls = OnOpenUrls;
            // v@:@@ — returns void; self, _cmd, the NSApplication and the NSArray<NSURL *>
            if (!class_addMethod(cls, sel_registerName("application:openURLs:"),
                    Marshal.GetFunctionPointerForDelegate(openUrls), "v@:@@"))
                Log.Write("open documents: the delegate already implements application:openURLs:, left as it is");
        }
        catch (Exception ex)
        {
            Log.Error("open documents: install", ex);
        }
    }

    /// <summary>
    /// Hands over the files that arrived before the app was ready and sends later ones to <paramref name="onFiles"/>
    /// (called on the AppKit main thread).
    /// </summary>
    public static IReadOnlyList<string> Attach(Action<IReadOnlyList<string>> onFiles)
    {
        lock (gate)
        {
            handler = onFiles;
            var early = pending.ToList();
            pending.Clear();
            return early;
        }
    }

    private static void OnOpenUrls(IntPtr self, IntPtr cmd, IntPtr application, IntPtr urls)
    {
        try
        {
            var files = new List<string>();
            var count = (long)objc_msgSend_nuint(urls, sel_registerName("count"));
            for (long i = 0; i < count; i++)
            {
                var url = objc_msgSend_index(urls, sel_registerName("objectAtIndex:"), (nuint)i);
                if (objc_msgSend_bool(url, sel_registerName("isFileURL")) == 0) continue;
                var path = objc_msgSend(url, sel_registerName("path"));
                var text = Marshal.PtrToStringUTF8(objc_msgSend(path, sel_registerName("UTF8String")));
                if (!string.IsNullOrEmpty(text) && File.Exists(text)) files.Add(text);
            }
            Log.Write($"open documents: {files.Count} file(s) from the system");
            if (files.Count == 0) return;
            Action<IReadOnlyList<string>>? target;
            lock (gate)
            {
                target = handler;
                if (target == null) pending.AddRange(files);
            }
            target?.Invoke(files);
        }
        catch (Exception ex)
        {
            Log.Error("open documents", ex);
        }
    }

    [DllImport(ObjC)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjC)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjC)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool class_addMethod(IntPtr cls, IntPtr name, IntPtr imp, string types);

    [DllImport(ObjC)]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nuint objc_msgSend_nuint(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_index(IntPtr receiver, IntPtr selector, nuint index);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern byte objc_msgSend_bool(IntPtr receiver, IntPtr selector);
}
