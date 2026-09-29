using System.Text.Json;

namespace Typedown.Uno.Services;

/// <summary>
/// Remembers which HedgeDoc note a local file was shared to (ported from Typedown), so re-sharing an unchanged
/// document hands back the same links instead of creating another note — HedgeDoc 1.x has no HTTP API to update
/// an existing note, so every upload is a new link. Kept as a JSON file under the app data folder.
/// </summary>
public static class HedgeDocShareMemory
{
    public sealed class Entry
    {
        public string NoteUrl { get; set; } = "";
        public string? PublishedUrl { get; set; }
        public ulong ContentHash { get; set; }
        public DateTime Time { get; set; }
    }

    private const int MaxEntries = 500;
    private static readonly object sync = new();
    private static readonly SerializedFileWriter writer = new(StorePath);
    private static Dictionary<string, Entry>? entries;

    private static string StorePath => Path.Combine(CursorMemory.DataFolder, "hedgedoc-shares.json");

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

    public static Entry? Get(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        lock (sync)
        {
            EnsureLoaded();
            return entries!.TryGetValue(Normalize(path), out var e) ? e : null;
        }
    }

    public static void Set(string? path, HedgeDocShareResult result, ulong contentHash)
    {
        if (string.IsNullOrEmpty(path)) return;
        Dictionary<string, Entry> snapshot;
        lock (sync)
        {
            EnsureLoaded();
            entries![Normalize(path)] = new Entry { NoteUrl = result.NoteUrl, PublishedUrl = result.PublishedUrl, ContentHash = contentHash, Time = DateTime.UtcNow };
            if (entries.Count > MaxEntries)
                foreach (var key in entries.OrderBy(x => x.Value.Time).Take(entries.Count - MaxEntries).Select(x => x.Key).ToList())
                    entries.Remove(key);
            snapshot = new(entries);
        }
        writer.Queue(JsonSerializer.Serialize(snapshot));
    }

    public static Task FlushAsync() => writer.FlushAsync();

    private static string Normalize(string path) => Path.GetFullPath(path);
}
