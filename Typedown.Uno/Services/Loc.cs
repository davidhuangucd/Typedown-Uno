using System.Globalization;

namespace Typedown.Uno.Services;

/// <summary>UI strings (English and Simplified Chinese). Menus are built in code, so a dictionary is enough.</summary>
public static class Loc
{
    private static readonly Dictionary<string, string> En = new()
    {
        ["File"] = "File", ["New"] = "New", ["NewTab"] = "New tab", ["Open"] = "Open…", ["OpenFolder"] = "Open folder…", ["Recent"] = "Open recent",
        ["NoRecent"] = "(none)", ["ClearRecent"] = "Clear list", ["Save"] = "Save", ["SaveAs"] = "Save as…", ["ExportHtml"] = "Export HTML…",
        ["ShareHedgeDoc"] = "Share to HedgeDoc…", ["Settings"] = "Settings…", ["CloseTab"] = "Close tab", ["Exit"] = "Exit",
        ["Edit"] = "Edit", ["Find"] = "Find…", ["FindNext"] = "Find next", ["FindPrevious"] = "Find previous", ["SelectAll"] = "Select all",
        ["Undo"] = "Undo", ["Redo"] = "Redo", ["Cut"] = "Cut", ["Copy"] = "Copy", ["Paste"] = "Paste",
        ["PasteAsPlainText"] = "Paste as plain text", ["DeleteSelection"] = "Delete", ["CopyAsPlainText"] = "Copy as plain text",
        ["CopyAsMarkdown"] = "Copy as Markdown", ["CopyAsHtml"] = "Copy as HTML", ["Replace"] = "Replace…",
        ["CmdUndo"] = "Undo", ["CmdRedo"] = "Redo", ["CmdReplace"] = "Replace",
        ["ReplaceWith"] = "Replace with", ["ReplaceAction"] = "Replace", ["ReplaceAll"] = "Replace all",
        ["LargeDocumentSourceMode"] = "A {0}k-character document opens in source mode; the View menu switches back.", ["ExportPdf"] = "Export PDF…", ["ExportImage"] = "Export picture…", ["ImageFailed"] = "The picture could not be written.", ["ImportHtml"] = "Import HTML…", ["CmdExportPdf"] = "Export PDF", ["Exporting"] = "Exporting…",
        ["PdfFailed"] = "The PDF could not be written.", ["PdfUnavailable"] = "PDF export needs WebKitGTK, which is not available here.",
        ["Paragraph"] = "Paragraph", ["Heading"] = "Heading", ["HeadingN"] = "Heading {0}", ["ParagraphPlain"] = "Paragraph", ["IncreaseHeading"] = "Increase heading level",
        ["DecreaseHeading"] = "Decrease heading level", ["Table"] = "Table…", ["CodeFences"] = "Code fences", ["MathBlock"] = "Math block", ["Quote"] = "Quote",
        ["QuoteIncrease"] = "Increase quote level", ["QuoteDecrease"] = "Decrease quote level", ["OrderedList"] = "Ordered list", ["UnorderedList"] = "Unordered list",
        ["TaskList"] = "Task list", ["InsertBefore"] = "Insert paragraph before", ["InsertAfter"] = "Insert paragraph after", ["Chart"] = "Diagram", ["Mermaid"] = "Mermaid",
        ["FlowChart"] = "Flowchart", ["Sequence"] = "Sequence diagram", ["VegaLite"] = "Vega-Lite", ["PlantUml"] = "PlantUML", ["HorizontalLine"] = "Horizontal line",
        ["Toc"] = "Table of contents", ["FrontMatter"] = "YAML front matter", ["Footnote"] = "Footnote", ["LinkReference"] = "Link reference",
        ["Format"] = "Format", ["Strong"] = "Bold", ["Emphasis"] = "Italic", ["Underline"] = "Underline", ["InlineCode"] = "Inline code", ["InlineMath"] = "Inline math",
        ["Strikethrough"] = "Strikethrough", ["Highlight"] = "Highlight", ["Hyperlink"] = "Link", ["Image"] = "Image", ["ClearFormat"] = "Clear format",
        ["View"] = "View", ["NextTab"] = "Next tab", ["PreviousTab"] = "Previous tab", ["LastUsedTab"] = "Last used tab", ["FullScreen"] = "Full screen", ["CmdFullScreen"] = "Full screen", ["SourceCode"] = "Source code mode", ["FocusMode"] = "Focus mode", ["Typewriter"] = "Typewriter mode", ["ReadOnly"] = "Reading mode",
        ["SidePane"] = "Side pane", ["Theme"] = "Theme", ["ThemeSystem"] = "System", ["ThemeLight"] = "Light", ["ThemeDark"] = "Dark", ["ThemeBlack"] = "Black",
        ["Help"] = "Help", ["About"] = "About",
        ["Files"] = "Files", ["Outline"] = "Outline", ["Search"] = "Search", ["SearchPlaceholder"] = "Search in folder…", ["NoFolder"] = "No folder open",
        ["NoHeadings"] = "No headings", ["NoResults"] = "No results", ["Results"] = "{1} matches in {0} files",
        ["Untitled"] = "Untitled", ["Words"] = "{0} words", ["SaveChanges"] = "Save changes?", ["HasUnsaved"] = "{0} has unsaved changes.",
        ["DontSave"] = "Don't save", ["Cancel"] = "Cancel", ["OK"] = "OK", ["FileChanged"] = "File changed on disk", ["ReloadPrompt"] = "{0} was modified outside Typedown. Reload it and lose your changes?",
        ["Reload"] = "Reload", ["KeepMine"] = "Keep mine", ["CannotOpen"] = "Cannot open file", ["CannotSave"] = "Cannot save file", ["Error"] = "Error",
        ["FindPlaceholder"] = "Find", ["Close"] = "Close",
        ["FilesSection"] = "Files", ["StartupAction"] = "On startup", ["StartupNewFile"] = "New empty document", ["StartupOpenLast"] = "Open the last file", ["StartupRestoreSession"] = "Restore all documents of the last session",
        ["FolderStartup"] = "Folder on startup", ["FolderStartupNone"] = "None", ["FolderStartupOpenLast"] = "Last folder", ["FolderStartupOpenFixed"] = "A fixed folder",
        ["StartupFolder"] = "Fixed folder", ["Browse"] = "Browse…", ["OpenFilesInNewTab"] = "Open files in a new tab", ["StatusBar"] = "Show the status bar",
        ["WordCountMethod"] = "Status bar count", ["CountWords"] = "Words", ["CountCharacters"] = "Characters", ["CountParagraphs"] = "Paragraphs",
        ["AutoReload"] = "Reload when the file changes on disk", ["AutoReloadDescription"] = "Reload even when there are unsaved changes", ["AskBeforeReload"] = "Ask before reloading",
        ["OpenFolderAfterExport"] = "Open the folder after exporting", ["TrimCodeBlock"] = "Trim unnecessary empty lines in code blocks",
        ["AutoPairBracket"] = "Auto-pair brackets", ["AutoPairQuote"] = "Auto-pair quotes", ["AutoPairMarkdown"] = "Auto-pair Markdown syntax",
        ["TextDirection"] = "Text direction", ["Dirauto"] = "Automatic", ["Dirltr"] = "Left to right", ["Dirrtl"] = "Right to left",
        ["FontFamily"] = "Font family", ["FontFamilyPlaceholder"] = "Empty = default font", ["CustomCss"] = "Custom CSS",
        ["FindSection"] = "Find", ["FindCaseSensitive"] = "Match case", ["FindWholeWord"] = "Whole word", ["FindRegex"] = "Regular expression",
        ["TestConnection"] = "Test connection", ["Test"] = "Test",
        ["InsertImage"] = "Insert image…", ["PrintPdf"] = "Print…", ["PrintOpened"] = "Opened in the browser — use its print dialog to save as PDF",
        ["NewFileHere"] = "New file here", ["NewFolderHere"] = "New folder here", ["Rename"] = "Rename", ["Delete"] = "Delete", ["CopyPath"] = "Copy path",
        ["RevealInFileManager"] = "Show in file manager", ["Refresh"] = "Refresh", ["DeleteConfirm"] = "Delete {0}? This cannot be undone.",
        ["ImageSection"] = "Images", ["ImageAction"] = "When inserting an image", ["ImageCopy"] = "Copy next to the document", ["ImageKeep"] = "Keep the original path",
        ["ImageCopyPath"] = "Image folder", ["ImageCopyPathHint"] = "supports ${filename} ${filedir} ${year} ${month} ${day}",
        ["PreferRelativeImagePaths"] = "Use relative paths", ["EncodeImageLinks"] = "Escape spaces and brackets in links",
        ["FileName"] = "File name", ["FolderName"] = "Folder",
        ["ShortcutSection"] = "Shortcuts", ["ShortcutHint"] = "Click a box and press the combination; Esc clears it, ↺ restores the default", ["ShortcutNone"] = "(none)", ["ShortcutReset"] = "Restore the default",
        ["CmdNewTab"] = "New tab", ["CmdOpen"] = "Open file", ["CmdOpenFolder"] = "Open folder", ["CmdSave"] = "Save", ["CmdSaveAs"] = "Save as",
        ["CmdExportHtml"] = "Export HTML", ["CmdPrint"] = "Print", ["CmdShareHedgeDoc"] = "Share to HedgeDoc", ["CmdSettings"] = "Settings",
        ["CmdCloseTab"] = "Close tab", ["CmdExit"] = "Exit", ["CmdFind"] = "Find", ["CmdFindNext"] = "Find next", ["CmdFindPrevious"] = "Find previous",
        ["CmdSearchInFolder"] = "Search in folder", ["CmdSelectAll"] = "Select all", ["CmdNextTab"] = "Next tab", ["CmdPreviousTab"] = "Previous tab",
        ["CmdSidePane"] = "Side pane", ["CmdReadingMode"] = "Reading mode", ["CmdSourceCode"] = "Source code mode", ["CmdInsertImage"] = "Insert image",
        ["SearchInFolder"] = "Search in folder",
        ["NewWindow"] = "New window", ["OpenInNewWindow"] = "Open in a new window", ["CmdNewWindow"] = "New window",
        ["SettingsTitle"] = "Settings", ["General"] = "General", ["Language"] = "Language", ["LangSystem"] = "System", ["Appearance"] = "Appearance", ["Editor"] = "Editor",
        ["FontSize"] = "Font size", ["LineHeight"] = "Line height", ["EditorWidth"] = "Editor width", ["TabSize"] = "Tab size", ["ListIndentation"] = "List indentation",
        ["TableAlign"] = "Align table columns", ["LooseList"] = "Loose list items", ["ParagraphMarker"] = "Show paragraph markers", ["Spellcheck"] = "Spell check",
        ["AutoSave"] = "Auto save", ["RememberPosition"] = "Remember caret and scroll position", ["AlwaysShowTabBar"] = "Always show the tab bar",
        ["HedgeDoc"] = "HedgeDoc", ["Server"] = "Server", ["Email"] = "Email (optional)", ["Password"] = "Password", ["PublishReadOnly"] = "Also publish a read-only link",
        ["Shared"] = "Shared to HedgeDoc", ["ReadOnlyLink"] = "Read-only link", ["EditLink"] = "Editable link", ["CopyLink"] = "Copy link", ["OpenInBrowser"] = "Open in browser",
        ["NotConfigured"] = "Set the HedgeDoc server address in Settings first.", ["EditableWarning"] = "Anyone with this link can edit the note.",
        ["Exported"] = "Exported: {0}", ["AboutText"] = "Typedown (Uno Platform edition)\nCross-platform Markdown editor; editing engine from MarkText/Muya.",
        ["AboutEditor"] = "Editor engine", ["AboutWebEngine"] = "Web engine", ["AboutSystem"] = "System", ["AboutSession"] = "Desktop session",
        ["CopyInfo"] = "Copy details", ["Copied"] = "Copied", ["AboutIssue"] = "Please include these details in a bug report.", ["ReportIssue"] = "Report an issue on GitHub",
        ["Copy"] = "Copy", ["Cut"] = "Cut", ["Paste"] = "Paste", ["ThemeFolder"] = "Theme folder", ["ReloadThemes"] = "Reload themes",
        ["ThemeDocument"] = "How to write a theme",
        ["Duplicate"] = "Duplicate", ["DeleteParagraph"] = "Delete paragraph",
        ["InsertRowAbove"] = "Insert row above", ["InsertRowBelow"] = "Insert row below", ["DeleteRow"] = "Delete row",
        ["InsertColumnLeft"] = "Insert column left", ["InsertColumnRight"] = "Insert column right", ["DeleteColumn"] = "Delete column",
    };

    /// <summary>
    /// The language in use, one of the keys of <see cref="LocaleTables.All"/> or "en". English is built in
    /// above and is the fallback for anything a table does not carry, so a half-finished translation shows
    /// English rather than a key.
    /// </summary>
    public static string Language { get; private set; } = "en";

    private static Dictionary<string, string>? table;

    public static void Apply(string? setting)
    {
        var name = string.IsNullOrEmpty(setting) ? CultureInfo.CurrentUICulture.Name : setting!;
        Language = Match(name);
        table = LocaleTables.All.TryGetValue(Language, out var found) ? found : null;
    }

    /// <summary>Maps a culture name ("de-AT", "zh-TW", "pt-BR") onto one of the tables.</summary>
    private static string Match(string name)
    {
        if (LocaleTables.All.ContainsKey(name)) return name;
        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return name.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                || name.Contains("TW", StringComparison.OrdinalIgnoreCase)
                || name.Contains("HK", StringComparison.OrdinalIgnoreCase)
                || name.Contains("MO", StringComparison.OrdinalIgnoreCase) ? "zh-Hant" : "zh-Hans";
        var prefix = name.Split('-')[0];
        return LocaleTables.All.ContainsKey(prefix) ? prefix : "en";
    }

    public static string Get(string key) =>
        table != null && table.TryGetValue(key, out var value) ? value : En.TryGetValue(key, out var fallback) ? fallback : key;

    public static string Format(string key, params object[] args) => string.Format(Get(key), args);
}
