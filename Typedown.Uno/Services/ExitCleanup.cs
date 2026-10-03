using System.Runtime.InteropServices;

namespace Typedown.Uno.Services;

/// <summary>
/// What has to be undone when the process ends (sockets and their files), run once whichever way it ends.
///
/// A normal exit runs it from ProcessExit. A SIGTERM (the session ending, kill, a service manager) runs it and then ends
/// the process at once with _exit, without exit()'s teardown of the native libraries: with Mesa's software renderer,
/// Uno's render thread can be compiling a shader in LLVM at that moment while exit() destroys LLVM's global state,
/// and the process ended in a segfault in that thread (1 in 20 under gdb, more often when the X server was going too).
/// </summary>
public static class ExitCleanup
{
    private static readonly List<(string name, Action action)> actions = new();
    private static int ran;
    private static PosixSignalRegistration? terminate;

    static ExitCleanup()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Run();
    }

    /// <summary>Adds something to undo when the process ends.</summary>
    public static void Add(string name, Action action)
    {
        lock (actions) actions.Add((name, action));
    }

    /// <summary>Called once at startup: a SIGTERM cleans up and ends the process with 143, as the signal itself would.</summary>
    public static void HandleTerminate()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        try
        {
            terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                Log.Write("SIGTERM: cleaning up and ending the process");
                Run();
                ExitNow(128 + 15);
            });
        }
        catch (Exception ex)
        {
            Log.Error("SIGTERM handler", ex);
        }
    }

    private static void Run()
    {
        if (Interlocked.Exchange(ref ran, 1) == 1) return;
        (string name, Action action)[] list;
        lock (actions) list = actions.ToArray();
        foreach (var (name, action) in list)
        {
            try { action(); }
            catch (Exception ex) { Log.Error("exit cleanup: " + name, ex); }
        }
    }

    /// <summary>
    /// _exit from the C library. macOS has none called "libc" - it is libSystem - so the import failed there, the
    /// handler had already cancelled the signal's own termination, and the process logged the SIGTERM and kept running.
    /// Should the native call fail all the same, the runtime's exit ends the process (with its teardown, which is what
    /// _exit avoids on Linux, but a process that stays up after a SIGTERM is worse).
    /// </summary>
    private static void ExitNow(int status)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) _exitMac(status);
            else _exitLinux(status);
        }
        catch (Exception ex)
        {
            Log.Error("SIGTERM: _exit", ex);
        }
        Environment.Exit(status);
    }

    [DllImport("libc", EntryPoint = "_exit")]
    private static extern void _exitLinux(int status);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_exit")]
    private static extern void _exitMac(int status);
}
