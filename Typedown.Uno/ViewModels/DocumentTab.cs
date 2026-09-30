using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Typedown.Uno.Services;

namespace Typedown.Uno.ViewModels;

/// <summary>
/// One open document. There is a single editor per window, so an inactive tab holds a snapshot of its document
/// state; switching tabs writes the live state into the old tab and restores the new one (ported from Typedown).
/// </summary>
public sealed class DocumentTab : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Stable across session restore; distinguishes crash backups for multiple untitled tabs.</summary>
    public string DocumentId { get; set; } = Guid.NewGuid().ToString("N");

    private string? filePath;
    public string? FilePath { get => filePath; set { filePath = value; OnPropertyChanged(); OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(DisplayTitle)); OnPropertyChanged(nameof(ToolTip)); } }

    private bool isDirty;
    public bool IsDirty { get => isDirty; set { if (isDirty == value) return; isDirty = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayTitle)); } }

    private bool isPreview;
    /// <summary>Opened by a single click in the file tree: the next single click replaces it instead of adding a tab.</summary>
    public bool IsPreview { get => isPreview; set { if (isPreview == value) return; isPreview = value; OnPropertyChanged(); } }

    // snapshot of the editor state while the tab is inactive
    public string Markdown { get; set; } = DocumentViewModel.DefaultMarkdown;
    public ulong FileHash { get; set; } = SafeFile.Hash(DocumentViewModel.DefaultMarkdown);
    public ulong CurrentHash { get; set; } = SafeFile.Hash(DocumentViewModel.DefaultMarkdown);
    public ulong DiskHash { get; set; }
    public bool Saved { get; set; } = true;
    public bool FileLoaded { get; set; }
    public TextFileFormat? FileFormat { get; set; }
    public ContentHistory? History { get; set; }
    public JsonNode? Cursor { get; set; }
    public double? ScrollTop { get; set; }
    /// <summary>Advances only when the text really changes (docs/automation-fixtures/document-identity.json).</summary>
    public long Revision { get; set; }

    public string Title => FilePath == null ? Loc.Get("Untitled") : Path.GetFileName(FilePath);
    public string DisplayTitle => IsDirty ? Title + " •" : Title;
    public string ToolTip => FilePath ?? Loc.Get("Untitled");

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
