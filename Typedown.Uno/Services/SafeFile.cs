using System.Text;

namespace Typedown.Uno.Services;

/// <summary>Atomic text write: temp file in the same directory, flushed, then replaced over the target.</summary>
public static class SafeFile
{
    public static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static Task WriteAllTextAtomicAsync(string path, string text, Encoding? encoding = null)
        => WriteAllBytesAtomicAsync(path, (encoding ?? Utf8NoBom).GetBytes(text));

    /// <summary>The same atomic write for content whose bytes the caller has already decided on.</summary>
    public static async Task WriteAllBytesAtomicAsync(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(string.IsNullOrEmpty(directory) ? "." : directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var keepTemp = false;
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes);
                stream.Flush(flushToDisk: true);
            }
            try
            {
                if (File.Exists(path))
                    File.Replace(tempPath, path, null, ignoreMetadataErrors: true);
                else
                    File.Move(tempPath, path);
            }
            catch (PlatformNotSupportedException)
            {
                // Some virtual file systems have no replace primitive. Copying the completed temporary file is
                // the only available fallback there.
                File.Copy(tempPath, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Sync clients and virus scanners commonly hold the destination for a short time. Retry the
                // atomic operation; if it remains locked, retain the complete temp file as a recovery copy and
                // report failure instead of risking a partial in-place overwrite.
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    await Task.Delay(75);
                    try
                    {
                        if (File.Exists(path))
                            File.Replace(tempPath, path, null, ignoreMetadataErrors: true);
                        else
                            File.Move(tempPath, path);
                        return;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                keepTemp = true;
                throw;
            }
        }
        finally
        {
            if (!keepTemp)
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    /// <summary>Stable 64-bit content hash (FNV-1a over UTF-16 code units), used for saved/changed comparisons.</summary>
    public static ulong Hash(string? text)
    {
        const ulong offset = 14695981039346656037UL, prime = 1099511628211UL;
        var hash = offset;
        if (text == null) return hash;
        foreach (var c in text)
        {
            hash ^= (byte)c; hash *= prime;
            hash ^= (byte)(c >> 8); hash *= prime;
        }
        return hash;
    }
}
