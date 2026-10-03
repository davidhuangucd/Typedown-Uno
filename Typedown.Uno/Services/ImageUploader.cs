using System.Diagnostics;
using System.Text.Json;
using Typedown.Core.Services;

namespace Typedown.Uno.Services;

/// <summary>
/// Uploads an image as Settings > Images > Upload says: to an S3-compatible bucket (Services/Images/S3Uploader, the
/// Windows edition's code) or with a command of the person's own (run by /bin/sh with the image as $1; the last line
/// it prints is the address). A picture that went up before with the same settings gets its earlier address back
/// (Services/Images/UploadHistory).
/// </summary>
public static class ImageUploader
{
    public static UploadHistory History { get; } = new(Path.Combine(CursorMemory.DataFolder, "ImageUploadHistory.json"));

    private static readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(60) };

    public static bool IsConfigured(AppSettings settings) => settings.ImageUploadMethod switch
    {
        ImageUploadMethod.S3 => !string.IsNullOrWhiteSpace(settings.S3Endpoint) && !string.IsNullOrWhiteSpace(settings.S3Bucket),
        ImageUploadMethod.Command => !string.IsNullOrWhiteSpace(settings.ImageUploadCommand),
        _ => false,
    };

    /// <summary>The upload settings as a key for the history: where a picture goes (not the secret it is sent with).</summary>
    private static string Scope(AppSettings settings) => UploadHistory.Scope(settings.ImageUploadMethod.ToString(), JsonSerializer.Serialize(
        settings.ImageUploadMethod == ImageUploadMethod.S3
            ? new[] { settings.S3Endpoint, settings.S3Region, settings.S3Bucket, settings.S3AccessKey, settings.S3PathStyle.ToString(), settings.S3KeyPrefix, settings.S3PublicUrl }
            : new[] { settings.ImageUploadCommand }));

    /// <summary>The file's address: from the history, or uploaded now. Throws with a readable reason when it fails.</summary>
    public static async Task<(string url, bool reused)> UploadAsync(AppSettings settings, string filePath, string? documentPath, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured(settings)) throw new InvalidOperationException(Loc.Get("UploadNoConfig"));
        return await History.UploadAsync(Scope(settings), filePath, () => settings.ImageUploadMethod == ImageUploadMethod.S3
            ? UploadS3Async(settings, filePath, documentPath, cancellationToken)
            : RunCommandAsync(settings.ImageUploadCommand, filePath, cancellationToken));
    }

    /// <summary>Settings > Test upload: a small picture, always uploaded (not from the history); its address.</summary>
    public static async Task<string> TestAsync(AppSettings settings)
    {
        if (!IsConfigured(settings)) throw new InvalidOperationException(Loc.Get("UploadNoConfig"));
        // A 1x1 PNG.
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var file = Path.Combine(Path.GetTempPath(), "typedown-test.png");
        await File.WriteAllBytesAsync(file, png);
        try
        {
            return settings.ImageUploadMethod == ImageUploadMethod.S3
                ? await UploadS3Async(settings, file, null, CancellationToken.None)
                : await RunCommandAsync(settings.ImageUploadCommand, file, CancellationToken.None);
        }
        finally { try { File.Delete(file); } catch { } }
    }

    private static Task<string> UploadS3Async(AppSettings settings, string filePath, string? documentPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.S3AccessKey) || string.IsNullOrEmpty(settings.S3SecretKey))
            throw new InvalidOperationException(Loc.Get("S3Incomplete"));
        var target = new S3Uploader.Target
        {
            Endpoint = settings.S3Endpoint.Trim(),
            Region = settings.S3Region,
            Bucket = settings.S3Bucket.Trim(),
            AccessKey = settings.S3AccessKey.Trim(),
            SecretKey = settings.S3SecretKey,
            PathStyle = settings.S3PathStyle,
            KeyPrefix = ImagePaths.ExpandTemplate(settings.S3KeyPrefix, documentPath),
            PublicBaseUrl = settings.S3PublicUrl,
        };
        return S3Uploader.UploadAsync(client, target, filePath, cancellationToken);
    }

    private static async Task<string> RunCommandAsync(string command, string filePath, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-c", command, "typedown-upload", filePath }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("/bin/sh could not be started");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(Loc.Get("UploadCommandTimeout"));
        }
        var lines = (await output).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Loc.Get("UploadCommandFailed")} ({process.ExitCode}): {(await error).Trim()}");
        return lines.LastOrDefault() ?? throw new InvalidOperationException(Loc.Get("UploadNoAddress"));
    }
}
