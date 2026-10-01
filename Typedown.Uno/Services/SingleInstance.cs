using System.Net.Sockets;
using System.Text;

namespace Typedown.Uno.Services;

/// <summary>
/// One process per user. Opening a document from the file manager starts the program again, and without this
/// every one of those launches put up a window of its own — the "open files in a tab" setting never had a say,
/// because the running instance never heard about the file. A second launch now hands its arguments to the
/// first over a socket and exits.
/// </summary>
public static class SingleInstance
{
    private static Socket? listener;

    /// <summary>Files another launch asked to open (empty when it was started with no arguments).</summary>
    public static event Action<IReadOnlyList<string>>? FilesRequested;

    private static string SocketPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath(),
        $"typedown-{Environment.UserName}.sock");

    /// <summary>
    /// Hands the arguments to an instance that is already running. True means this process is done and should
    /// exit; false means nobody was listening and this process is the one that serves.
    /// </summary>
    public static bool HandOff(IReadOnlyList<string> files)
    {
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(SocketPath));
            client.Send(Encoding.UTF8.GetBytes(string.Join("\n", files)));
            Log.Write($"handed {files.Count} file(s) to the running instance");
            return true;
        }
        catch
        {
            return false; // nothing listening, or a socket left behind by a crash
        }
    }

    /// <summary>Starts serving hand-offs. Safe to call when the socket of a crashed instance is still around.</summary>
    public static void Listen()
    {
        try
        {
            var path = SocketPath;
            if (File.Exists(path)) File.Delete(path); // HandOff already proved nobody answers on it
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(4);
            new Thread(Accept) { IsBackground = true, Name = "single-instance" }.Start();
            ExitCleanup.Add("single-instance socket", Cleanup);
        }
        catch (Exception ex)
        {
            Log.Error("single instance listener", ex);
        }
    }

    private static void Accept()
    {
        while (listener != null)
        {
            try
            {
                using var client = listener.Accept();
                var buffer = new byte[64 * 1024];
                var read = client.Receive(buffer);
                var text = Encoding.UTF8.GetString(buffer, 0, read);
                FilesRequested?.Invoke(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            }
            catch (Exception ex)
            {
                if (listener == null) return;
                Log.Error("single instance accept", ex);
                Thread.Sleep(200);
            }
        }
    }

    private static void Cleanup()
    {
        try
        {
            listener?.Dispose();
            listener = null;
            if (File.Exists(SocketPath)) File.Delete(SocketPath);
        }
        catch
        {
            // leaving the socket behind is harmless: the next start deletes it
        }
    }
}
