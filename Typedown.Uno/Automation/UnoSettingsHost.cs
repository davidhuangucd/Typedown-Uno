using Newtonsoft.Json.Linq;
using Typedown.Automation;
using Typedown.Uno.Services;

namespace Typedown.Uno.Automation;

/// <summary>
/// The external settings on the Uno edition (Typedown.Automation.ISettingsHost), mapped as the Windows repository's
/// docs/automation-fixtures/settings-map.json records under "uno": one AppSettings shared by every window, changed with
/// the same setters the settings dialog uses, then saved, so success means saved.
/// </summary>
public sealed class UnoSettingsHost : ISettingsHost
{
    private static readonly string[] BuiltIn = { "system", "light", "dark", "black" };
    private long revision;

    // The AppSettings properties behind the exposed settings (settings-map.json, "uno"): only their changes advance
    // settingsRevision. The rest - the session, recent files, window state - changed it too, and a client's next
    // settings.set was refused as stale with none of its settings changed.
    private static readonly ISet<string> Counted = SettingsCatalog.Load().StoredProperties("uno");

    public UnoSettingsHost(AppSettings settings)
    {
        Settings = settings;
        settings.PropertyChanged += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.PropertyName) || Counted.Contains(e.PropertyName)) Interlocked.Increment(ref revision);
        };
    }

    private AppSettings Settings { get; }

    public long Revision => Interlocked.Read(ref revision);

    private static readonly string[] Original = { "appearance.theme", "editor.fontSize", "editor.lineHeight", "editor.textDirection" };
    private static readonly string[] Indentations = { "1", "2", "4", "tab" };

    /// <summary>
    /// The other exposed settings this edition has (settings-map.json, "uno"), one line each: how the external value
    /// is read from and written to AppSettings, the same properties the settings dialog sets. A value this edition
    /// cannot take throws setting_invalid naming the reason.
    /// </summary>
    private static readonly Dictionary<string, (Func<AppSettings, JToken> get, Action<AppSettings, JToken> set)> Simple = new()
    {
        ["ui.language"] = (s => string.IsNullOrEmpty(s.Language) ? "system" : s.Language, (s, v) =>
        {
            var code = (string)v!;
            if (code == "system") { s.Language = ""; return; }
            if (code != "en" && !LocaleTables.All.ContainsKey(code)) throw Unsupported("ui.language", "unsupportedLanguage");
            s.Language = code;
        }),
        ["editor.fontFamily"] = (s => s.FontFamily ?? "", (s, v) => s.FontFamily = (string)v!),
        ["editor.areaWidth"] = (s => s.EditorAreaWidth, (s, v) => s.EditorAreaWidth = (string)v!),
        ["editor.tabSize"] = (s => s.TabSize, (s, v) => s.TabSize = (int)(long)v),
        ["editor.paragraphMarkers"] = (s => s.ShowParagraphMarker, (s, v) => s.ShowParagraphMarker = (bool)v),
        ["editor.autoPairBrackets"] = (s => s.AutoPairBracket, (s, v) => s.AutoPairBracket = (bool)v),
        ["editor.autoPairQuotes"] = (s => s.AutoPairQuote, (s, v) => s.AutoPairQuote = (bool)v),
        ["editor.autoPairMarkdown"] = (s => s.AutoPairMarkdownSyntax, (s, v) => s.AutoPairMarkdownSyntax = (bool)v),
        ["markdown.alignTableColumns"] = (s => s.TableAlignColumns, (s, v) => s.TableAlignColumns = (bool)v),
        ["markdown.listIndentation"] = (s => s.ListIndentation, (s, v) =>
        {
            if (!Indentations.Contains((string)v!)) throw Unsupported("markdown.listIndentation", "unsupportedValue");
            s.ListIndentation = (string)v!;
        }),
        ["markdown.looseListItems"] = (s => s.PreferLooseListItem, (s, v) => s.PreferLooseListItem = (bool)v),
        ["markdown.trimCodeBlockBlankLines"] = (s => s.TrimUnnecessaryCodeBlockEmptyLines, (s, v) => s.TrimUnnecessaryCodeBlockEmptyLines = (bool)v),
        ["spellcheck.enabled"] = (s => s.SpellcheckEnabled, (s, v) => s.SpellcheckEnabled = (bool)v),
        ["tabs.alwaysShow"] = (s => s.AlwaysShowTabBar, (s, v) => s.AlwaysShowTabBar = (bool)v),
        ["status.wordCount"] = (s => s.WordCountMethod switch { WordCountMethod.Characters => "characters", WordCountMethod.Paragraphs => "paragraphs", _ => "words" },
            (s, v) => s.WordCountMethod = (string)v! switch { "characters" => WordCountMethod.Characters, "paragraphs" => WordCountMethod.Paragraphs, _ => WordCountMethod.Words }),
        ["images.preferRelativePaths"] = (s => s.PreferRelativeImagePaths, (s, v) => s.PreferRelativeImagePaths = (bool)v),
    };

    private static AutomationException Unsupported(string key, string reason) =>
        new(AutomationErrorKind.setting_invalid, $"This value of '{key}' is not available here: {reason}.",
            new Dictionary<string, object?> { ["key"] = key, ["reason"] = reason });

    public bool Supports(string key) => Original.Contains(key) || Simple.ContainsKey(key);

    public Task<JToken> GetAsync(string key, CancellationToken cancellationToken)
    {
        var s = Settings;
        if (Simple.TryGetValue(key, out var simple)) return Task.FromResult(simple.get(s));
        JToken value = key switch
        {
            "appearance.theme" => string.IsNullOrEmpty(s.CustomTheme)
                ? new JObject { ["kind"] = "builtIn", ["id"] = BuiltIn[Math.Clamp((int)s.Theme, 0, BuiltIn.Length - 1)] }
                : new JObject { ["kind"] = "custom", ["id"] = s.CustomTheme },
            "editor.fontSize" => s.FontSize,
            "editor.lineHeight" => Math.Round(s.LineHeight, 6),
            "editor.textDirection" => s.TextDirection,
            _ => throw new AutomationException(AutomationErrorKind.setting_not_exposed, $"'{key}' is not an external setting."),
        };
        return Task.FromResult(value);
    }

    public async Task<long> SetAsync(string key, JToken value, CancellationToken cancellationToken)
    {
        // Resolve a custom theme before anything changes: an unknown one changes nothing.
        CustomTheme? custom = null;
        if (key == "appearance.theme" && (string?)value["kind"] == "custom")
            custom = ThemeFiles.Find((string?)value["id"])
                ?? throw new AutomationException(AutomationErrorKind.setting_invalid, "No custom theme with that id.",
                    new Dictionary<string, object?> { ["key"] = key, ["reason"] = "unknownTheme" });

        var window = AutomationWindows.Registry.Snapshot().FirstOrDefault()
            ?? throw new AutomationException(AutomationErrorKind.editor_not_ready, "Typedown has no open window to apply settings in.");
        // On a window's UI thread, like the settings dialog: the change handlers update every window from there.
        await AutomationWindows.Registry.OnWindowAsync(window.WindowId, _ =>
        {
            var s = Settings;
            switch (key)
            {
                case "appearance.theme":
                    if (custom != null)
                    {
                        s.Theme = custom.Base;
                        s.CustomTheme = custom.Id;
                    }
                    else
                    {
                        s.CustomTheme = "";
                        s.Theme = (AppTheme)Array.IndexOf(BuiltIn, (string?)value["id"]);
                    }
                    break;
                case "editor.fontSize": s.FontSize = (int)(long)value; break;
                case "editor.lineHeight": s.LineHeight = (double)value; break;
                case "editor.textDirection": s.TextDirection = (string)value!; break;
                case var other when Simple.TryGetValue(other, out var simple): simple.set(s, value); break;
                default: throw new AutomationException(AutomationErrorKind.setting_not_exposed, $"'{key}' is not an external setting.");
            }
            return true;
        });

        await Settings.FlushAsync();
        if (Settings.LastSaveError != null)
            throw new AutomationException(AutomationErrorKind.persistence_failed, "The setting is applied but could not be saved.",
                new Dictionary<string, object?> { ["settingsRevision"] = Revision, ["applied"] = true });
        return Revision;
    }
}
