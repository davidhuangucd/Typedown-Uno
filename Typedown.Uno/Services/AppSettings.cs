using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Typedown.Uno.Services;

public enum AppTheme { System, Light, Dark, Black }

/// <summary>What to open at startup when no file was passed on the command line.</summary>
public enum FileStartupAction { NewFile, OpenLast, RestoreSession }

/// <summary>Which folder the side pane starts on.</summary>
public enum FolderStartupAction { None, OpenLast, OpenFixed }

public enum WordCountMethod { Words, Characters, Paragraphs }

/// <summary>What happens to an image that is inserted, pasted or dropped into a document.</summary>
public enum ImageInsertAction { CopyToFolder, KeepPath }

/// <summary>
/// User settings, persisted as JSON under the app data folder. Changing a property raises PropertyChanged and
/// schedules a save; the editor-facing subset is pushed to the WebView through "SettingsChanged".
/// </summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    private static readonly string StorePath = Path.Combine(CursorMemory.DataFolder, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };
    private static readonly SerializedFileWriter StoreWriter = new(StorePath);

    public static AppSettings Current { get; } = Load();

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- appearance ----
    private AppTheme theme = AppTheme.System;
    public AppTheme Theme { get => theme; set => Set(ref theme, value); }

    private string customTheme = "";
    /// <summary>
    /// File name (without .css) of a theme in the themes folder, or empty for the built-in theme in
    /// <see cref="Theme"/>. A custom theme brings its own base theme along, so this wins when it is set.
    /// </summary>
    public string CustomTheme { get => customTheme; set => Set(ref customTheme, value); }

    private string language = "";
    /// <summary>"" = follow the system, otherwise "zh-Hans" / "en".</summary>
    public string Language { get => language; set => Set(ref language, value); }

    private bool sidePaneOpen;
    public bool SidePaneOpen { get => sidePaneOpen; set => Set(ref sidePaneOpen, value); }

    private double sidePaneWidth = 260;
    public double SidePaneWidth { get => sidePaneWidth; set => Set(ref sidePaneWidth, value); }

    private int sidePanePage;
    public int SidePanePage { get => sidePanePage; set => Set(ref sidePanePage, value); }

    private bool alwaysShowTabBar;
    public bool AlwaysShowTabBar { get => alwaysShowTabBar; set => Set(ref alwaysShowTabBar, value); }

    // ---- editor (names match the editor's option keys) ----
    private int fontSize = 16;
    [EditorOption] public int FontSize { get => fontSize; set => Set(ref fontSize, value); }

    private double lineHeight = 1.6;
    [EditorOption] public double LineHeight { get => lineHeight; set => Set(ref lineHeight, value); }

    private string editorAreaWidth = "1200px";
    [EditorOption] public string EditorAreaWidth { get => editorAreaWidth; set => Set(ref editorAreaWidth, value); }

    private string fontFamily = "";
    [EditorOption] public string FontFamily { get => fontFamily; set => Set(ref fontFamily, value); }

    private int tabSize = 4;
    [EditorOption] public int TabSize { get => tabSize; set => Set(ref tabSize, value); }

    private string textDirection = "auto";
    [EditorOption] public string TextDirection { get => textDirection; set => Set(ref textDirection, value); }

    private bool sourceCode;
    /// <summary>
    /// Source mode shows the raw Markdown, reading mode only the rendered document: the two contradict each
    /// other, so turning one on turns the other off.
    /// </summary>
    [EditorOption] public bool SourceCode { get => sourceCode; set { Set(ref sourceCode, value); if (value && ReadOnly) ReadOnly = false; } }

    private bool focusMode;
    [EditorOption] public bool FocusMode { get => focusMode; set => Set(ref focusMode, value); }

    private bool typewriter;
    [EditorOption] public bool Typewriter { get => typewriter; set => Set(ref typewriter, value); }

    private bool readOnly;
    /// <summary>
    /// Reading mode: only the rendered document, no caret, no edits. Mutually exclusive with
    /// <see cref="SourceCode"/>; focus and typewriter mode follow the caret and are ignored while it is on.
    /// </summary>
    [EditorOption] public bool ReadOnly { get => readOnly; set { Set(ref readOnly, value); if (value && SourceCode) SourceCode = false; } }

    private bool showParagraphMarker = true;
    [EditorOption] public bool ShowParagraphMarker { get => showParagraphMarker; set => Set(ref showParagraphMarker, value); }

    private bool spellcheckEnabled;
    [EditorOption] public bool SpellcheckEnabled { get => spellcheckEnabled; set => Set(ref spellcheckEnabled, value); }

    private bool autoPairBracket = true;
    [EditorOption] public bool AutoPairBracket { get => autoPairBracket; set => Set(ref autoPairBracket, value); }

    private bool autoPairQuote = true;
    [EditorOption] public bool AutoPairQuote { get => autoPairQuote; set => Set(ref autoPairQuote, value); }

    private bool autoPairMarkdownSyntax = true;
    [EditorOption] public bool AutoPairMarkdownSyntax { get => autoPairMarkdownSyntax; set => Set(ref autoPairMarkdownSyntax, value); }

    private bool trimUnnecessaryCodeBlockEmptyLines = true;
    [EditorOption] public bool TrimUnnecessaryCodeBlockEmptyLines { get => trimUnnecessaryCodeBlockEmptyLines; set => Set(ref trimUnnecessaryCodeBlockEmptyLines, value); }

    private bool preferLooseListItem = true;
    [EditorOption] public bool PreferLooseListItem { get => preferLooseListItem; set => Set(ref preferLooseListItem, value); }

    private string listIndentation = "1";
    [EditorOption] public string ListIndentation { get => listIndentation; set => Set(ref listIndentation, value); }

    private bool tableAlignColumns = true;
    [EditorOption] public bool TableAlignColumns { get => tableAlignColumns; set => Set(ref tableAlignColumns, value); }

    private string customCss = "";
    [EditorOption] public string CustomCss { get => customCss; set => Set(ref customCss, value); }

    // ---- files ----
    private bool autoSave;
    public bool AutoSave { get => autoSave; set => Set(ref autoSave, value); }

    private bool rememberPosition = true;
    public bool RememberPosition { get => rememberPosition; set => Set(ref rememberPosition, value); }

    private FileStartupAction fileStartupAction = FileStartupAction.RestoreSession;
    public FileStartupAction FileStartupAction { get => fileStartupAction; set => Set(ref fileStartupAction, value); }

    private FolderStartupAction folderStartupAction = FolderStartupAction.OpenLast;
    public FolderStartupAction FolderStartupAction { get => folderStartupAction; set => Set(ref folderStartupAction, value); }

    private string? startupFolder;
    /// <summary>Used when <see cref="FolderStartupAction"/> is <see cref="FolderStartupAction.OpenFixed"/>.</summary>
    public string? StartupFolder { get => startupFolder; set => Set(ref startupFolder, value); }

    private bool openFilesInNewTab = true;
    public bool OpenFilesInNewTab { get => openFilesInNewTab; set => Set(ref openFilesInNewTab, value); }

    private bool autoReload;
    /// <summary>Reload a document changed on disk even when it has unsaved changes (no prompt).</summary>
    public bool AutoReload { get => autoReload; set => Set(ref autoReload, value); }

    private bool askBeforeReload = true;
    public bool AskBeforeReload { get => askBeforeReload; set => Set(ref askBeforeReload, value); }

    private bool statusBarOpen = true;
    public bool StatusBarOpen { get => statusBarOpen; set => Set(ref statusBarOpen, value); }

    private WordCountMethod wordCountMethod = WordCountMethod.Words;
    public WordCountMethod WordCountMethod { get => wordCountMethod; set => Set(ref wordCountMethod, value); }

    private bool openFolderAfterExport = true;
    public bool OpenFolderAfterExport { get => openFolderAfterExport; set => Set(ref openFolderAfterExport, value); }

    // ---- shortcuts ----
    private ShortcutMap shortcuts = new();
    public ShortcutMap Shortcuts { get => shortcuts; set => Set(ref shortcuts, value); }

    /// <summary>Called after a binding was edited in place (the map itself is mutable).</summary>
    public void NotifyShortcutsChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Shortcuts)));
        ScheduleSave();
    }

    // ---- images ----
    private ImageInsertAction imageAction = ImageInsertAction.CopyToFolder;
    public ImageInsertAction ImageAction { get => imageAction; set => Set(ref imageAction, value); }

    private string imageCopyPath = "./${filename}.assets";
    /// <summary>Folder template for copied images; supports ${filename} ${filedir} ${year} ${month} ${day}.</summary>
    public string ImageCopyPath { get => imageCopyPath; set => Set(ref imageCopyPath, value); }

    private bool preferRelativeImagePaths = true;
    public bool PreferRelativeImagePaths { get => preferRelativeImagePaths; set => Set(ref preferRelativeImagePaths, value); }

    private bool encodeImageLinks = true;
    public bool EncodeImageLinks { get => encodeImageLinks; set => Set(ref encodeImageLinks, value); }

    // find bar options (sent to the editor with every search)
    private bool findCaseSensitive;
    public bool FindCaseSensitive { get => findCaseSensitive; set => Set(ref findCaseSensitive, value); }

    private bool findWholeWord;
    public bool FindWholeWord { get => findWholeWord; set => Set(ref findWholeWord, value); }

    private bool findRegex;
    public bool FindRegex { get => findRegex; set => Set(ref findRegex, value); }

    private string? lastFolder;
    public string? LastFolder { get => lastFolder; set => Set(ref lastFolder, value); }

    private List<string> recentFiles = new();
    public List<string> RecentFiles { get => recentFiles; set => Set(ref recentFiles, value); }

    // ---- HedgeDoc ----
    private string hedgeDocServer = "";
    public string HedgeDocServer { get => hedgeDocServer; set => Set(ref hedgeDocServer, value); }

    private string hedgeDocEmail = "";
    public string HedgeDocEmail { get => hedgeDocEmail; set => Set(ref hedgeDocEmail, value); }

    private string hedgeDocPassword = "";
    [JsonIgnore]
    public string HedgeDocPassword
    {
        get => hedgeDocPassword;
        set
        {
            value ??= "";
            if (hedgeDocPassword == value) return;
            hedgeDocPassword = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HedgeDocPassword)));
            CredentialStore.Queue(value);
        }
    }

    private bool hedgeDocPublishReadOnly = true;
    public bool HedgeDocPublishReadOnly { get => hedgeDocPublishReadOnly; set => Set(ref hedgeDocPublishReadOnly, value); }

    // ---- window ----
    private double windowWidth = 1130, windowHeight = 700;
    public double WindowWidth { get => windowWidth; set => Set(ref windowWidth, value); }
    public double WindowHeight { get => windowHeight; set => Set(ref windowHeight, value); }

    public void AddRecent(string path)
    {
        var list = RecentFiles.Where(p => !string.Equals(p, path, PathIdentity.Comparison)).ToList();
        list.Insert(0, path);
        RecentFiles = list.Take(15).ToList();
    }

    /// <summary>The camelCase option object the editor expects in GetSettings.</summary>
    public Dictionary<string, object?> EditorOptions()
    {
        var dict = new Dictionary<string, object?>();
        foreach (var p in typeof(AppSettings).GetProperties())
            if (Attribute.IsDefined(p, typeof(EditorOptionAttribute)))
                dict[char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = p.GetValue(this);
        return dict;
    }

    public static bool IsEditorOption(string propertyName) =>
        typeof(AppSettings).GetProperty(propertyName) is { } p && Attribute.IsDefined(p, typeof(EditorOptionAttribute));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        ScheduleSave();
        return true;
    }

    private readonly object saveSync = new();
    private CancellationTokenSource? saveCts;

    private void ScheduleSave()
    {
        string snapshot;
        try { snapshot = JsonSerializer.Serialize(this, Json); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"settings snapshot failed: {ex.Message}");
            return;
        }

        CancellationTokenSource cts;
        lock (saveSync)
        {
            saveCts?.Cancel();
            saveCts?.Dispose();
            cts = saveCts = new CancellationTokenSource();
        }
        _ = QueueAfterDelayAsync(snapshot, cts.Token);
    }

    private static async Task QueueAfterDelayAsync(string snapshot, CancellationToken token)
    {
        try
        {
            await Task.Delay(500, token);
            StoreWriter.Queue(snapshot);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public void Save()
    {
        try
        {
            StoreWriter.Queue(JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"settings save failed: {ex.Message}");
        }
    }

    public async Task FlushAsync()
    {
        Save();
        await Task.WhenAll(StoreWriter.FlushAsync(), CredentialStore.FlushAsync());
    }

    [JsonIgnore]
    public Exception? LastSaveError => StoreWriter.LastWriteError;

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var source = File.ReadAllText(StorePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(source, Json) ?? new AppSettings();
                string? legacyPassword = null;
                try
                {
                    using var document = JsonDocument.Parse(source);
                    if (document.RootElement.TryGetProperty(nameof(HedgeDocPassword), out var value))
                        legacyPassword = value.GetString();
                }
                catch { }
                settings.hedgeDocPassword = CredentialStore.Load() ?? legacyPassword ?? "";
                if (!string.IsNullOrEmpty(legacyPassword))
                {
                    CredentialStore.Queue(settings.hedgeDocPassword);
                    StoreWriter.Queue(JsonSerializer.Serialize(settings, Json));
                }
                return settings;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"settings load failed: {ex.Message}");
        }
        return new AppSettings();
    }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class EditorOptionAttribute : Attribute { }
