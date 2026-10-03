using Typedown.Automation;
using Typedown.Core.Utilities;
using Typedown.Uno.Automation;
using Typedown.Uno.ViewModels;

namespace Typedown.Uno.Services;

/// <summary>
/// File > Upload local images: every picture of the active document that is a file on this computer is uploaded once
/// (however often the text uses it), and the document's addresses of the uploaded ones change to their web addresses in
/// one edit, one undo step. What could not be uploaded keeps its address and is reported. Shows nothing itself. The same
/// as the Windows edition's ImageBatchUpload.
/// </summary>
public sealed class ImageBatchUpload
{
    public sealed record Failure(string Address, string Reason);

    public sealed class Result
    {
        public int Files { get; set; }
        public int Uploaded { get; set; }
        public int Reused { get; set; }
        public int Replaced { get; set; }
        public bool NoConfig { get; set; }
        public bool Cancelled { get; set; }
        public List<Failure> Failures { get; } = new();
    }

    private const int ApplyAttempts = 3;

    private readonly AppSettings settings;
    private readonly AutomationWindow window;

    public ImageBatchUpload(AppSettings settings, AutomationWindow window)
    {
        this.settings = settings;
        this.window = window;
    }

    /// <summary>The local images of a text: address -> the file it names (null when there is none).</summary>
    public static Dictionary<string, string?> LocalImages(string markdown, string? documentPath)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var reference in MarkdownImages.Find(markdown))
            if (MarkdownImages.IsLocal(reference.Address) && !result.ContainsKey(reference.Address))
                result[reference.Address] = Resolve(reference.Address, documentPath);
        return result;
    }

    /// <summary>The local images of the window's document, from the editor's latest text.</summary>
    public async Task<Dictionary<string, string?>> LocalImagesOfDocumentAsync()
    {
        await window.Document.FlushContentAsync();
        return LocalImages(window.Document.Markdown, window.Document.FilePath);
    }

    public async Task<Result> RunAsync(IProgress<(int done, int total)>? progress, CancellationToken cancellationToken)
    {
        var result = new Result();
        var tab = window.Tabs.ActiveTab;
        var documentPath = window.Document.FilePath;
        var images = await LocalImagesOfDocumentAsync();
        var files = images.Values.Where(f => f != null).Select(f => f!).Distinct().ToList();
        result.Files = files.Count + images.Count(i => i.Value == null);
        if (images.Count == 0) return result;
        if (!ImageUploader.IsConfigured(settings))
        {
            result.NoConfig = true;
            return result;
        }
        foreach (var missing in images.Where(i => i.Value == null))
            result.Failures.Add(new Failure(missing.Key, Loc.Get("UploadFileNotFound")));

        var uploaded = new Dictionary<string, string>();
        for (var i = 0; i < files.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                result.Cancelled = true;
                break;
            }
            progress?.Report((i, files.Count));
            try
            {
                var (url, reused) = await ImageUploader.UploadAsync(settings, files[i], documentPath, cancellationToken);
                if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException(Loc.Get("UploadNoAddress"));
                uploaded[files[i]] = url.Trim();
                if (reused) result.Reused++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                foreach (var address in images.Where(x => x.Value == files[i]).Select(x => x.Key))
                    result.Failures.Add(new Failure(address, ex.Message));
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                break;
            }
        }
        if (!result.Cancelled) progress?.Report((files.Count, files.Count));
        result.Uploaded = uploaded.Count;

        var replacements = images.Where(x => x.Value != null && uploaded.ContainsKey(x.Value)).ToDictionary(x => x.Key, x => uploaded[x.Value!], StringComparer.Ordinal);
        if (replacements.Count == 0) return result;
        try
        {
            if (!window.Tabs.Tabs.Contains(tab)) throw new InvalidOperationException(Loc.Get("UploadDocumentClosed"));
            await ApplyAsync(tab, replacements);
            result.Replaced = replacements.Count;
        }
        catch (Exception ex)
        {
            // The pictures are online but the text still has the old addresses: say where they went.
            Log.Write($"upload images: the document did not take the new addresses: {ex.Message}");
            var reason = ex is AutomationException ? Loc.Get("UploadApplyFailed") : ex.Message;
            foreach (var r in replacements)
                result.Failures.Add(new Failure(r.Key, $"{reason} {r.Value}"));
        }
        return result;
    }

    // The addresses are replaced in the latest text, so typing during the upload is kept; the edit is retried when the
    // window wrote to the document between the flush and the edit.
    private async Task ApplyAsync(DocumentTab tab, Dictionary<string, string> replacements)
    {
        for (var attempt = 1; ; attempt++)
        {
            var document = new UnoAutomationDocument(window, tab);
            try
            {
                await DocumentEdits.Coordinator.EditAsync(document, new EditRequest
                {
                    BaseRevision = document.Revision,
                    Edit = text => MarkdownImages.Replace(text, replacements),
                    AllowUnknown = true,
                }, CancellationToken.None);
                return;
            }
            catch (AutomationException ex) when (ex.Kind == AutomationErrorKind.revision_conflict && attempt < ApplyAttempts)
            {
                await Task.Delay(200);
            }
        }
    }

    private static string? Resolve(string address, string? documentPath)
    {
        var baseDir = documentPath == null ? Environment.CurrentDirectory : Path.GetDirectoryName(documentPath)!;
        foreach (var candidate in new[] { address, Unescape(address) }.Distinct())
        {
            if (candidate == null) continue;
            string path;
            if (candidate.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) continue;
                path = uri.LocalPath;
            }
            else path = Path.IsPathRooted(candidate) ? candidate : Path.Combine(baseDir, candidate);
            try
            {
                var full = Path.GetFullPath(path);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }

    private static string? Unescape(string address)
    {
        try { return Uri.UnescapeDataString(address); }
        catch { return null; }
    }
}
