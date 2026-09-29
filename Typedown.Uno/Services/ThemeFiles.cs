using System.Text.RegularExpressions;
using System.Diagnostics;

namespace Typedown.Uno.Services;

/// <summary>
/// A custom theme is one CSS file in the themes folder. Its first comment block carries the metadata (the name
/// to show, which built-in theme it builds on, an optional accent colour); everything after it is applied to
/// the editor on top of that built-in theme, so a theme only has to state what it changes.
/// See docs/custom-theme.md for the format and the variables.
/// </summary>
/// <param name="Background">Window surface behind the editor, from the metadata; null keeps the built-in one.</param>
/// <param name="Surface">Panels: side pane, tab bar, status bar.</param>
/// <param name="Foreground">Text in the shell.</param>
/// <param name="Border">Separators between the panels.</param>
public sealed record CustomTheme(string Id, string Name, AppTheme Base, string? Accent, string? Author, string Path,
    string? Background = null, string? Surface = null, string? Foreground = null, string? Border = null);

public static class ThemeFiles
{
    public static string Folder => System.IO.Path.Combine(CursorMemory.DataFolder, "themes");

    /// <summary>The themes that ship with the app, next to the executable and never written to.</summary>
    public static string BundledFolder => System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Themes");

    public static string DesignerTemplatePath => System.IO.Path.Combine(BundledFolder, "theme-designer.html");
    public static string DesignerPath => System.IO.Path.Combine(CursorMemory.DataFolder, "theme-designer.html");
    private const long MaxDesignerThemeBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Every readable theme, by file name: the ones that ship with the app first, then the user's folder. A file
    /// in the user's folder takes the place of a bundled one with the same name, which is how a bundled theme is
    /// edited — copy it over, change it, and it keeps its place in the list.
    /// </summary>
    public static IReadOnlyList<CustomTheme> List()
    {
        var themes = new Dictionary<string, CustomTheme>(StringComparer.OrdinalIgnoreCase);
        Collect(BundledFolder, themes);
        Collect(Folder, themes);
        return themes.Values.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void Collect(string folder, Dictionary<string, CustomTheme> themes)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var path in Directory.EnumerateFiles(folder, "*.css").OrderBy(p => p))
            {
                try
                {
                    var theme = Parse(path, File.ReadAllText(path));
                    themes[theme.Id] = theme;
                }
                catch (Exception ex)
                {
                    Log.Error($"theme {System.IO.Path.GetFileName(path)}", ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("theme folder", ex);
        }
    }

    /// <summary>
    /// What to call a theme in a list. Two files may carry the same name — copying a bundled theme to edit it
    /// under another file name is the usual way — so the file name comes along to tell them apart.
    /// </summary>
    public static string DisplayName(CustomTheme theme, IReadOnlyList<CustomTheme> all) =>
        all.Count(t => t.Name == theme.Name) > 1 ? $"{theme.Name} ({theme.Id})" : theme.Name;

    public static CustomTheme? Find(string? id) =>
        string.IsNullOrEmpty(id) ? null : List().FirstOrDefault(t => t.Id == id);

    /// <summary>The CSS to hand the editor, or an empty string when the theme has gone missing.</summary>
    public static string Read(string? id)
    {
        var theme = Find(id);
        if (theme == null) return "";
        try
        {
            return File.ReadAllText(theme.Path);
        }
        catch (Exception ex)
        {
            Log.Error("read theme", ex);
            return "";
        }
    }

    /// <summary>
    /// Browsers cannot enumerate an arbitrary local folder from a file page. The app reads the trusted theme
    /// locations, embeds their current CSS without local paths, and opens the generated offline snapshot.
    /// </summary>
    public static async Task OpenDesignerAsync(string? selectedId)
    {
        var path = await PrepareDesignerAsync(selectedId);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static async Task<string> PrepareDesignerAsync(string? selectedId)
    {
        EnsureFolder();
        if (!File.Exists(DesignerTemplatePath))
            throw new FileNotFoundException("The packaged theme designer is missing.", DesignerTemplatePath);

        var entries = new List<ThemeDesignerEntry>();
        foreach (var theme in List())
        {
            try
            {
                var info = new FileInfo(theme.Path);
                if (!info.Exists || info.Length > MaxDesignerThemeBytes)
                {
                    Log.Write($"theme designer skipped {info.Name}: missing or larger than {MaxDesignerThemeBytes} bytes");
                    continue;
                }
                entries.Add(new ThemeDesignerEntry
                {
                    Id = theme.Id,
                    Name = theme.Name,
                    FileName = info.Name,
                    Source = SameFolder(info.DirectoryName, BundledFolder) ? "bundled" : "user",
                    Css = await File.ReadAllTextAsync(theme.Path),
                });
            }
            catch (Exception ex)
            {
                Log.Error($"theme designer read {theme.Id}", ex);
            }
        }

        var template = await File.ReadAllTextAsync(DesignerTemplatePath);
        var html = ThemeDesignerPage.EmbedCatalog(template, new ThemeDesignerCatalog { SelectedId = selectedId, Themes = entries });
        await SafeFile.WriteAllTextAtomicAsync(DesignerPath, html);
        return DesignerPath;
    }

    private static bool SameFolder(string? left, string right)
    {
        if (string.IsNullOrEmpty(left)) return false;
        return string.Equals(System.IO.Path.GetFullPath(left).TrimEnd(System.IO.Path.DirectorySeparatorChar),
            System.IO.Path.GetFullPath(right).TrimEnd(System.IO.Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The theme document that ships with the app, in the themes folder where it is looked for.</summary>
    public static string DocumentPath => System.IO.Path.Combine(Folder, "custom-theme.md");

    /// <summary>
    /// Creates the folder and, the first time, an example theme to start from and the document that explains
    /// the format — a theme folder with nothing in it says nothing about how to fill it.
    /// </summary>
    public static void EnsureFolder()
    {
        try
        {
            if (!Directory.Exists(Folder))
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(System.IO.Path.Combine(Folder, "example.css"), Example);
            }
            var shipped = System.IO.Path.Combine(BundledFolder, "custom-theme.md");
            if (File.Exists(shipped) && (!File.Exists(DocumentPath) || File.GetLastWriteTimeUtc(shipped) > File.GetLastWriteTimeUtc(DocumentPath)))
                File.Copy(shipped, DocumentPath, true);
        }
        catch (Exception ex)
        {
            Log.Error("create theme folder", ex);
        }
    }

    private static CustomTheme Parse(string path, string css)
    {
        var id = System.IO.Path.GetFileNameWithoutExtension(path);
        var header = Regex.Match(css, @"/\*(.*?)\*/", RegexOptions.Singleline);
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (header.Success)
            foreach (Match m in Regex.Matches(header.Groups[1].Value, @"^[\s*]*([A-Za-z]+)\s*:\s*(.+?)\s*$", RegexOptions.Multiline))
                meta[m.Groups[1].Value] = m.Groups[2].Value;
        var baseTheme = meta.TryGetValue("base", out var b) ? b.Trim().ToLowerInvariant() switch
        {
            "dark" => AppTheme.Dark,
            "black" => AppTheme.Black,
            _ => AppTheme.Light,
        } : AppTheme.Light;
        string? Value(string key) => meta.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
        return new CustomTheme(
            id,
            Value("name") ?? id,
            baseTheme,
            Value("accent"),
            Value("author"),
            path,
            Value("background"),
            Value("surface"),
            Value("foreground"),
            Value("border"));
    }

    private const string Example = """
/* Typedown theme
 * name: Example
 * base: light
 * accent: #268bd2
 * author: you
 *
 * These four colour the window around the editor (side pane, tab bar, status bar, separators). Leave them
 * out and the window keeps the colours of the built-in theme named by "base".
 * background: #fdf6e3
 * surface: #f2ead7
 * foreground: #073642
 * border: #e0dbc8
 *
 * Every declaration below overrides the built-in theme named by "base", so a theme only states what it
 * changes. The full list of variables is in docs/custom-theme.md.
 */
:root {
  --editorBgColor: #fdf6e3;
  --editorColor: #073642;
  --codeBgColor: #eee8d5;
  --codeBlockBgColor: #eee8d5;
  --tableBorderColor: #e0dbc8;
  --floatBgColor: #fdf6e3;
  --itemBgColor: #eee8d5;
}
""";
}
