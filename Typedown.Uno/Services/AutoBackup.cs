namespace Typedown.Uno.Services;

/// <summary>
/// Crash backups of unsaved edits (ported from Typedown). While a document is dirty its text is written to a
/// file under the app data folder; on the next open the backup is offered for recovery, then discarded once the
/// document is saved. This is the last line of defence when auto-save is off, the file has no path yet, or a
/// save fails — a crash then loses nothing. Saved files are keyed by path; each untitled tab has its own stable
/// document id, so two drafts cannot overwrite one another's backup.
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
    private static string PathFor(string? sourcePath, string documentId)
    {
        // Keep the v1.1.3 key for named files so an upgrade can still recover an existing crash backup.
        var key = sourcePath == null ? "untitled:" + documentId : Path.GetFullPath(sourcePath);
        var hash = SafeFile.Hash(key).ToString("x");
        var name = sourcePath == null ? $"untitled-{documentId}.md" : Path.GetFileName(sourcePath);
        return Path.Combine(BackupFolder, $"{hash}_{name}");
    }

    public static async Task<bool> BackupAsync(string? path, string documentId, string markdown)
    {
        try
        {
            await SafeFile.WriteAllTextAtomicAsync(PathFor(path, documentId), markdown, SafeFile.Utf8NoBom);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string? Read(string? path, string documentId)
    {
        try
        {
            var file = PathFor(path, documentId);
            if (path == null && !File.Exists(file)) ClaimLegacyUntitled(file);
            return File.Exists(file) ? File.ReadAllText(file, SafeFile.Utf8NoBom) : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool Exists(string? path, string documentId)
    {
        try { return File.Exists(PathFor(path, documentId)) || (path == null && File.Exists(LegacyUntitledPath)); }
        catch { return false; }
    }

    public static void Delete(string? path, string documentId)
    {
        try
        {
            var file = PathFor(path, documentId);
            if (File.Exists(file)) File.Delete(file);
            if (path == null && File.Exists(LegacyUntitledPath)) File.Delete(LegacyUntitledPath);
        }
        catch
        {
            // A backup we cannot delete is harmless; it is discarded on the next successful save or recovery.
        }
    }

    private static string LegacyUntitledPath => Path.Combine(BackupFolder, $"{SafeFile.Hash(""):x}_");

    /// <summary>The old build had one shared empty-path backup. The first new untitled document claims it.</summary>
    private static void ClaimLegacyUntitled(string destination)
    {
        try
        {
            if (!File.Exists(LegacyUntitledPath)) return;
            File.Move(LegacyUntitledPath, destination);
        }
        catch { }
    }
}
