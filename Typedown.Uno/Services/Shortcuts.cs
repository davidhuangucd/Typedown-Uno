using System.Text.Json;
using Windows.System;

namespace Typedown.Uno.Services;

/// <summary>A shell command that can be bound to a key.</summary>
public enum ShortcutCommand
{
    NewTab, NewWindow, Open, OpenFolder, Save, SaveAs, ExportHtml, ExportPdf, Print, ShareHedgeDoc, UploadLocalImages, Settings, CloseTab, Exit,
    Undo, Redo, Find, FindNext, FindPrevious, Replace, SearchInFolder, SelectAll,
    NextTab, PreviousTab, SidePane, ReadingMode, SourceCode, InsertImage, FullScreen,
}

/// <summary>A key binding: modifiers plus one key, stored and displayed as "Ctrl+Shift+S".</summary>
public sealed record Shortcut(bool Ctrl, bool Shift, bool Alt, string Key)
{
    public static readonly Shortcut None = new(false, false, false, "");

    public bool IsEmpty => string.IsNullOrEmpty(Key);

    public override string ToString() =>
        IsEmpty ? "" : (Ctrl ? "Ctrl+" : "") + (Alt ? "Alt+" : "") + (Shift ? "Shift+" : "") + Display(Key);

    private static string Display(string key) => key switch
    {
        "," => ",", "/" => "/", "tab" => "Tab", "escape" => "Esc",
        _ => key.Length == 1 ? key.ToUpperInvariant() : char.ToUpperInvariant(key[0]) + key[1..],
    };

    public static Shortcut Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return None;
        bool ctrl = false, shift = false, alt = false;
        var key = "";
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "shift": shift = true; break;
                case "alt": alt = true; break;
                default: key = Normalize(raw); break;
            }
        }
        return key.Length == 0 ? None : new Shortcut(ctrl, shift, alt, key);
    }

    private static string Normalize(string key) => key.ToLowerInvariant() switch
    {
        "esc" => "escape",
        "del" => "delete",
        var other => other,
    };

    /// <summary>The key as reported by the editor bridge (a browser KeyboardEvent.key, lower-cased).</summary>
    public bool Matches(string key, bool ctrl, bool shift, bool alt) =>
        !IsEmpty && Ctrl == ctrl && Shift == shift && Alt == alt && string.Equals(Key, Normalize(key), StringComparison.OrdinalIgnoreCase);

    /// <summary>The XAML accelerator for the shell menus; null when the key has no VirtualKey.</summary>
    public (VirtualKey key, VirtualKeyModifiers modifiers)? ToAccelerator()
    {
        if (IsEmpty) return null;
        VirtualKey? virtualKey = Key switch
        {
            "," => (VirtualKey)188,
            "/" => (VirtualKey)191,
            "tab" => VirtualKey.Tab,
            "escape" => VirtualKey.Escape,
            "f3" => VirtualKey.F3,
            _ when Key.Length == 1 && char.IsLetterOrDigit(Key[0]) => Enum.TryParse<VirtualKey>(Key.ToUpperInvariant(), out var parsed) ? parsed : null,
            _ when Key.StartsWith('f') && int.TryParse(Key[1..], out var n) && n is >= 1 and <= 12 => (VirtualKey)((int)VirtualKey.F1 + n - 1),
            _ => null,
        };
        if (virtualKey == null) return null;
        var modifiers = (Ctrl ? VirtualKeyModifiers.Control : VirtualKeyModifiers.None)
            | (Shift ? VirtualKeyModifiers.Shift : VirtualKeyModifiers.None)
            | (Alt ? VirtualKeyModifiers.Menu : VirtualKeyModifiers.None);
        return (virtualKey.Value, modifiers);
    }
}

/// <summary>
/// Key bindings for the shell commands, overridable per command and persisted with the settings
/// (the Windows edition has a configurable shortcut per menu item; this is the same idea).
/// </summary>
public sealed class ShortcutMap
{
    private static readonly Dictionary<ShortcutCommand, Shortcut> Defaults = new()
    {
        [ShortcutCommand.NewTab] = Shortcut.Parse("Ctrl+N"),
        [ShortcutCommand.NewWindow] = Shortcut.Parse("Ctrl+Shift+N"),
        [ShortcutCommand.Open] = Shortcut.Parse("Ctrl+O"),
        [ShortcutCommand.OpenFolder] = Shortcut.Parse("Ctrl+Shift+O"),
        [ShortcutCommand.Save] = Shortcut.Parse("Ctrl+S"),
        [ShortcutCommand.SaveAs] = Shortcut.Parse("Ctrl+Shift+S"),
        [ShortcutCommand.ExportHtml] = Shortcut.None,
        [ShortcutCommand.ExportPdf] = Shortcut.None,
        [ShortcutCommand.Print] = Shortcut.Parse("Ctrl+P"),
        [ShortcutCommand.ShareHedgeDoc] = Shortcut.None,
        [ShortcutCommand.UploadLocalImages] = Shortcut.None,
        [ShortcutCommand.Settings] = Shortcut.Parse("Ctrl+,"),
        [ShortcutCommand.CloseTab] = Shortcut.Parse("Ctrl+W"),
        [ShortcutCommand.Exit] = Shortcut.Parse("Ctrl+Q"),
        [ShortcutCommand.Undo] = Shortcut.Parse("Ctrl+Z"),
        [ShortcutCommand.Redo] = Shortcut.Parse("Ctrl+Y"),
        [ShortcutCommand.Find] = Shortcut.Parse("Ctrl+F"),
        [ShortcutCommand.FindNext] = Shortcut.Parse("F3"),
        [ShortcutCommand.FindPrevious] = Shortcut.Parse("Shift+F3"),
        [ShortcutCommand.Replace] = Shortcut.Parse("Ctrl+H"),
        [ShortcutCommand.SearchInFolder] = Shortcut.Parse("Ctrl+Shift+F"),
        [ShortcutCommand.SelectAll] = Shortcut.Parse("Ctrl+A"),
        [ShortcutCommand.NextTab] = Shortcut.Parse("Ctrl+Tab"),
        [ShortcutCommand.PreviousTab] = Shortcut.Parse("Ctrl+Shift+Tab"),
        [ShortcutCommand.SidePane] = Shortcut.Parse("Ctrl+Shift+B"),
        [ShortcutCommand.ReadingMode] = Shortcut.Parse("Ctrl+Shift+R"),
        [ShortcutCommand.SourceCode] = Shortcut.Parse("Ctrl+/"),
        [ShortcutCommand.InsertImage] = Shortcut.None,
        [ShortcutCommand.FullScreen] = Shortcut.Parse("F11"),
    };

    /// <summary>Overrides only; the defaults stay implicit so a later default change reaches existing users.</summary>
    public Dictionary<string, string> Overrides { get; set; } = new();

    public Shortcut Get(ShortcutCommand command) =>
        Overrides.TryGetValue(command.ToString(), out var text) ? Shortcut.Parse(text) : Defaults[command];

    public static Shortcut Default(ShortcutCommand command) => Defaults[command];

    public void Set(ShortcutCommand command, Shortcut shortcut)
    {
        if (shortcut == Default(command)) Overrides.Remove(command.ToString());
        else Overrides[command.ToString()] = shortcut.ToString();
    }

    public void Reset(ShortcutCommand command) => Overrides.Remove(command.ToString());

    /// <summary>The command bound to a key press coming from the editor bridge, if any.</summary>
    public ShortcutCommand? Find(string key, bool ctrl, bool shift, bool alt)
    {
        foreach (var command in Enum.GetValues<ShortcutCommand>())
            if (Get(command).Matches(key, ctrl, shift, alt))
                return command;
        return null;
    }

    /// <summary>Keys the bridge must forward from the web view (it cannot know the bindings otherwise).</summary>
    public string ForwardedKeysJson()
    {
        var list = Enum.GetValues<ShortcutCommand>()
            .Select(command => (command, shortcut: Get(command)))
            .Where(x => !x.shortcut.IsEmpty)
            .Select(x => new
            {
                key = x.shortcut.Key,
                ctrl = x.shortcut.Ctrl,
                shift = x.shortcut.Shift,
                alt = x.shortcut.Alt,
                // the find bar lives in the page, so it opens there instead of being sent to the shell
                find = x.command == ShortcutCommand.Find,
            });
        return JsonSerializer.Serialize(list);
    }
}
