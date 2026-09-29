using System.Text.Json;

namespace Typedown.Uno.Services;

public sealed class ThemeDesignerEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Source { get; set; } = "";
    public string Css { get; set; } = "";
}

public sealed class ThemeDesignerCatalog
{
    public int Version { get; set; } = 1;
    public string? SelectedId { get; set; }
    public IReadOnlyList<ThemeDesignerEntry> Themes { get; set; } = Array.Empty<ThemeDesignerEntry>();
}

/// <summary>Embeds a data-only theme snapshot into the packaged offline designer.</summary>
public static class ThemeDesignerPage
{
    public const string EmptyCatalog = "<script id=\"typedown-theme-catalog\" type=\"application/json\">[]</script>";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string EmbedCatalog(string template, ThemeDesignerCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(catalog);
        var first = template.IndexOf(EmptyCatalog, StringComparison.Ordinal);
        if (first < 0 || template.IndexOf(EmptyCatalog, first + EmptyCatalog.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidDataException("theme designer catalog placeholder is missing or duplicated");

        // System.Text.Json's default encoder escapes HTML-sensitive characters. A CSS file containing
        // </script> therefore cannot escape the application/json element and execute in the generated page.
        var json = JsonSerializer.Serialize(catalog, Json);
        var embedded = $"<script id=\"typedown-theme-catalog\" type=\"application/json\">{json}</script>";
        return template[..first] + embedded + template[(first + EmptyCatalog.Length)..];
    }
}
