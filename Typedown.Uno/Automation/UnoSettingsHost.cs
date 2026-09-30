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

    public UnoSettingsHost(AppSettings settings)
    {
        Settings = settings;
        settings.PropertyChanged += (_, _) => Interlocked.Increment(ref revision);
    }

    private AppSettings Settings { get; }

    public long Revision => Interlocked.Read(ref revision);

    public Task<JToken> GetAsync(string key, CancellationToken cancellationToken)
    {
        var s = Settings;
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
