namespace Typedown.Uno.Services;

/// <summary>
/// Crash backups of unsaved edits (ported from Typedown). While a document is dirty its text is written to a
/// file under the app data folder; on the next open the backup is offered for recovery, then discarded once the
/// document is saved. This is the last line of defence when auto-save is off, the file has no path yet, or a
/// save fails — a crash then loses nothing. Backups are keyed by the document's path (an untitled document uses
/// the empty path), and the write is atomic so a crash mid-backup cannot leave half a file.
/// </summary>
public static class AutoBackup
{
    private static string BackupFolder
    {
        get
        {
            var folder = Path.Combine(CursorMemory.DataFolder, "Backup");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    /// <summary>Backup file for a source path: a hash of the path keeps names unique, the filename aids diagnosis.</summary>
    private static string PathFor(string? sourcePath)
    {
        sourcePath ??= "";
        var hash = SafeFile.Hash(sourcePath).ToString("x");
        var name = Path.GetFileName(sourcePath);
        return Path.Combine(BackupFolder, $"{hash}_{name}");
    }

    public static async Task<bool> BackupAsync(string? path, string markdown)
    {
        try
        {
            await SafeFile.WriteAllTextAtomicAsync(PathFor(path), markdown, SafeFile.Utf8NoBom);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string? Read(string? path)
    {
        try
        {
            var file = PathFor(path);
            return File.Exists(file) ? File.ReadAllText(file, SafeFile.Utf8NoBom) : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Delete(string? path)
    {
        try
        {
            var file = PathFor(path);
            if (File.Exists(file)) File.Delete(file);
        }
        catch
        {
            // A backup we cannot delete is harmless; it is discarded on the next successful save or recovery.
        }
    }
}
