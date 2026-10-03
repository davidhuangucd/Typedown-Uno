using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Typedown.Uno.Services;

/// <summary>
/// Persists secrets - the HedgeDoc password, the S3 secret access key - in the operating system's protected store,
/// each under an account name. Unsupported Linux desktops keep them for the current process only; plaintext is never
/// written to settings.json or to a fallback file.
/// </summary>
public static class CredentialStore
{
    private const string Service = "Typedown.Uno";
    public const string HedgeDoc = "HedgeDoc";
    public const string S3 = "S3";
    private static string WindowsPath(string account) =>
        Path.Combine(CursorMemory.DataFolder, account == HedgeDoc ? "credentials.dat" : $"credentials-{account}.dat");
    private static readonly string? SecretTool = FindExecutable("secret-tool");
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SerializedFileWriter> writers = new();

    private static SerializedFileWriter Writer(string account) =>
        writers.GetOrAdd(account, a => new SerializedFileWriter(a, (_, secret) => StoreCoreAsync(a, secret)));

    public static bool IsPersistentAvailable => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || SecretTool != null;
    public static Exception? LastWriteError => Writer(HedgeDoc).LastWriteError;

    public static string? Load(string account = HedgeDoc)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!File.Exists(WindowsPath(account))) return null;
                return Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(WindowsPath(account))));
            }
            if (OperatingSystem.IsMacOS()) return MacLoad(account);
            if (OperatingSystem.IsLinux() && SecretTool != null)
                return RunSecretToolAsync(new[] { "lookup", "application", Service, "credential", account }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"credential load failed: {ex.Message}");
        }
        return null;
    }

    public static void Queue(string password, string account = HedgeDoc) => Writer(account).Queue(password ?? "");

    public static Task FlushAsync() => Task.WhenAll(writers.Values.Select(w => w.FlushAsync()));

    private static async Task StoreCoreAsync(string account, string password)
    {
        if (OperatingSystem.IsWindows())
        {
            if (password.Length == 0)
            {
                try { if (File.Exists(WindowsPath(account))) File.Delete(WindowsPath(account)); } catch { }
                return;
            }
            await SafeFile.WriteAllBytesAtomicAsync(WindowsPath(account), Protect(Encoding.UTF8.GetBytes(password)));
            return;
        }
        if (OperatingSystem.IsMacOS())
        {
            MacStore(account, password);
            return;
        }
        if (OperatingSystem.IsLinux() && SecretTool != null)
        {
            if (password.Length == 0)
                await RunSecretToolAsync(new[] { "clear", "application", Service, "credential", account });
            else
                await RunSecretToolAsync(new[] { "store", $"--label=Typedown {account}", "application", Service, "credential", account }, password);
        }
    }

    private static string? FindExecutable(string name)
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (folder.Length == 0) continue;
            var candidate = Path.Combine(folder, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static async Task<string?> RunSecretToolAsync(IEnumerable<string> arguments, string? input = null)
    {
        var start = new ProcessStartInfo(SecretTool!)
        {
            UseShellExecute = false,
            RedirectStandardInput = input != null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("secret-tool could not be started");
        if (input != null)
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
        }
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExitAsync();
        if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(5))) != exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("secret-tool did not respond within 5 seconds");
        }
        await exited;
        var output = await outputTask;
        var error = await errorTask;
        // lookup/clear return 1 when there is no matching item; that is an empty result, not a storage failure.
        if (process.ExitCode != 0 && !(input == null && process.ExitCode == 1))
            throw new IOException(string.IsNullOrWhiteSpace(error) ? $"secret-tool exited with {process.ExitCode}" : error.Trim());
        return process.ExitCode == 0 ? output.TrimEnd('\r', '\n') : null;
    }

    // Windows DPAPI, scoped to the current user. Keeping the encrypted blob in app data lets atomic writes and
    // backups work normally while another Windows account cannot decrypt it.
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static byte[] Protect(byte[] bytes) => Dpapi(bytes, protect: true);
    private static byte[] Unprotect(byte[] bytes) => Dpapi(bytes, protect: false);

    private static byte[] Dpapi(byte[] bytes, bool protect)
    {
        var input = new DataBlob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            DataBlob output;
            var ok = protect
                ? CryptProtectData(ref input, Service, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1, out output);
            if (!ok) throw new IOException($"DPAPI failed ({Marshal.GetLastWin32Error()})");
            try
            {
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }

    // The Security framework APIs avoid placing a password on a command line, where another process could see it.
    private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ItemNotFound = -25300;

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, out uint passwordLength, out IntPtr passwordData, out IntPtr item);
    [DllImport(SecurityFramework)]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, uint passwordLength, byte[] password, out IntPtr item);
    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemModifyAttributesAndData(IntPtr item, IntPtr attributes, uint length, byte[] data);
    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemDelete(IntPtr item);
    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);
    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr value);

    private static string? MacLoad(string name)
    {
        var service = Encoding.UTF8.GetBytes(Service);
        var account = Encoding.UTF8.GetBytes(name);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service,
            (uint)account.Length, account, out var length, out var data, out var item);
        if (status == ItemNotFound) return null;
        if (status != 0) throw new IOException($"Keychain lookup failed ({status})");
        try
        {
            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }

    private static void MacStore(string name, string password)
    {
        var service = Encoding.UTF8.GetBytes(Service);
        var account = Encoding.UTF8.GetBytes(name);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service,
            (uint)account.Length, account, out _, out var oldData, out var item);
        if (status != 0 && status != ItemNotFound) throw new IOException($"Keychain lookup failed ({status})");
        if (status == 0) SecKeychainItemFreeContent(IntPtr.Zero, oldData);
        try
        {
            if (password.Length == 0)
            {
                if (status == 0 && SecKeychainItemDelete(item) != 0) throw new IOException("Keychain delete failed");
                return;
            }
            var bytes = Encoding.UTF8.GetBytes(password);
            var writeStatus = status == 0
                ? SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)bytes.Length, bytes)
                : SecKeychainAddGenericPassword(IntPtr.Zero, (uint)service.Length, service,
                    (uint)account.Length, account, (uint)bytes.Length, bytes, out item);
            if (writeStatus != 0) throw new IOException($"Keychain write failed ({writeStatus})");
        }
        finally
        {
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }
}
