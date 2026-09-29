using System.Text.Json;

namespace Typedown.Uno.Services;

/// <summary>Documents open when the window closed, restored as tabs on the next start (setting RestoreSession).</summary>
public static class SessionMemory
{
    public class DocumentEntry
    {
        public string? FilePath { get; set; }
        public string DocumentId { get; set; } = Guid.NewGuid().ToString("N");
    }

    public class Session
    {
        public List<DocumentEntry> Documents { get; set; } = new();
        /// <summary>Legacy v1.1.3 representation, read once and replaced by <see cref="Documents"/>.</summary>
        public List<string> Files { get; set; } = new();
        public int ActiveIndex { get; set; }
        public string? Folder { get; set; }
        public DateTime Time { get; set; }
    }

    private static string StorePath => Path.Combine(CursorMemory.DataFolder, "session.json");
    private static readonly SerializedFileWriter writer = new(StorePath);

    public static Session? Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return null;
            var session = JsonSerializer.Deserialize<Session>(File.ReadAllText(StorePath));
            if (session == null) return null;
            session.Documents ??= new();
            session.Files ??= new();
            if (session.Documents.Count == 0 && session.Files.Count > 0)
                session.Documents = session.Files.Select(path => new DocumentEntry { FilePath = path }).ToList();
            var activeId = session.ActiveIndex >= 0 && session.ActiveIndex < session.Documents.Count
                ? session.Documents[session.ActiveIndex].DocumentId : null;
            session.Documents = session.Documents.Where(d => d.FilePath == null || File.Exists(d.FilePath)).ToList();
            session.ActiveIndex = Math.Max(0, activeId == null ? 0 : session.Documents.FindIndex(d => d.DocumentId == activeId));
            return session;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(IEnumerable<DocumentEntry> documents, int activeIndex, string? folder)
    {
        try
        {
            var list = documents.ToList();
            writer.Queue(JsonSerializer.Serialize(new Session { Documents = list, ActiveIndex = Math.Clamp(activeIndex, 0, Math.Max(0, list.Count - 1)), Folder = folder, Time = DateTime.UtcNow }));
        }
        catch
        {
        }
    }

    public static Task FlushAsync() => writer.FlushAsync();
}
