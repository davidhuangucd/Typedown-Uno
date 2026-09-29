using System.Text.Json;
using System.Text.Json.Nodes;

namespace Typedown.Uno.Services;

/// <summary>
/// Caret and scroll position per document (ported from Typedown): the caret is what the editor reports
/// (CodeMirror-style {anchor:{line,ch}, focus:{line,ch}}), the scroll offset is what reading mode relies on.
/// Kept in memory and flushed to a JSON file under the app data folder.
/// </summary>
public static class CursorMemory
{
    private class Entry
    {
        public JsonNode? Cursor { get; set; }
        public double? ScrollY { get; set; }
        public DateTime Time { get; set; }
    }

    private const int MaxEntries = 500;
    private static readonly object sync = new();
    private static readonly SerializedFileWriter writer = new(StorePath);
    private static Dictionary<string, Entry>? entries;
    private static bool dirty;

    public static string DataFolder
    {
        get
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Typedown.Uno");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    private static string StorePath => Path.Combine(DataFolder, "cursors.json");

    private static void EnsureLoaded()
    {
        if (entries != null) return;
        try
        {
            entries = File.Exists(StorePath)
                ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(StorePath)) ?? new()
                : new();
        }
        catch
        {
            entries = new();
        }
    }

    public static JsonNode? GetCursor(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        lock (sync)
        {
            EnsureLoaded();
            return entries!.TryGetValue(Normalize(path), out var e) ? e.Cursor?.DeepClone() : null;
        }
    }

    public static double? GetScroll(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        lock (sync)
        {
            EnsureLoaded();
            return entries!.TryGetValue(Normalize(path), out var e) ? e.ScrollY : null;
        }
    }

    public static void SetCursor(string? path, JsonNode? cursor)
    {
        if (string.IsNullOrEmpty(path) || cursor == null) return;
        lock (sync)
        {
            EnsureLoaded();
            var key = Normalize(path);
            var scroll = entries!.TryGetValue(key, out var existing) ? existing.ScrollY : null;
            entries[key] = new Entry { Cursor = cursor.DeepClone(), ScrollY = scroll, Time = DateTime.UtcNow };
            dirty = true;
        }
    }

    public static void SetScroll(string? path, double scrollY)
    {
        if (string.IsNullOrEmpty(path)) return;
        lock (sync)
        {
            EnsureLoaded();
            var key = Normalize(path);
            var cursor = entries!.TryGetValue(key, out var existing) ? existing.Cursor : null;
            entries[key] = new Entry { Cursor = cursor, ScrollY = scrollY, Time = DateTime.UtcNow };
            dirty = true;
        }
    }

    public static void Flush()
    {
        Dictionary<string, Entry> snapshot;
        lock (sync)
        {
            if (!dirty || entries == null) return;
            if (entries.Count > MaxEntries)
                foreach (var key in entries.OrderBy(x => x.Value.Time).Take(entries.Count - MaxEntries).Select(x => x.Key).ToList())
                    entries.Remove(key);
            snapshot = new(entries);
            dirty = false;
        }
        writer.Queue(JsonSerializer.Serialize(snapshot));
    }

    public static async Task FlushAsync()
    {
        Flush();
        await writer.FlushAsync();
    }

    private static string Normalize(string path) => Path.GetFullPath(path);
}
