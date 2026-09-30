using System.Runtime.InteropServices;

namespace Typedown.Uno.Services;

/// <summary>
/// Whether two paths name the same file, the way the platform's file system decides: Linux file names are case
/// sensitive (a.md and A.md are two files), Windows and macOS (by default) are not; a symbolic link is the file it
/// points to, so one file never ends up in two tabs edited apart.
/// </summary>
public static class PathIdentity
{
    public static readonly StringComparison Comparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public static readonly StringComparer Comparer =
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>The full path of the file itself: links followed to the final target, when there is one.</summary>
    public static string Canonical(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch { return path; }
        try
        {
            if (new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true) is { } target) return Path.GetFullPath(target.FullName);
        }
        catch { }
        return full;
    }

    public static bool Same(string? a, string? b) =>
        a != null && b != null && string.Equals(Canonical(a), Canonical(b), Comparison);
}
