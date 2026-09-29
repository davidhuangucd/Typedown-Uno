using System.Text;
using Typedown.Uno.Services;

var failures = new List<string>();

void Check(bool condition, string name)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {name}");
    if (!condition) failures.Add(name);
}

var root = Path.Combine(Path.GetTempPath(), "typedown-uno-reliability-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("XDG_DATA_HOME", root);
if (OperatingSystem.IsLinux())
{
    var fakeSecretTool = Path.Combine(root, "secret-tool");
    var fakeSecretFile = Path.Combine(root, "fake-secret");
    await File.WriteAllTextAsync(fakeSecretTool, """
#!/bin/sh
case "$1" in
  store) cat > "$TYPEDOWN_FAKE_SECRET" ;;
  lookup) [ -f "$TYPEDOWN_FAKE_SECRET" ] || exit 1; cat "$TYPEDOWN_FAKE_SECRET" ;;
  clear) unlink "$TYPEDOWN_FAKE_SECRET" 2>/dev/null || true ;;
esac
""");
    File.SetUnixFileMode(fakeSecretTool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    Environment.SetEnvironmentVariable("TYPEDOWN_FAKE_SECRET", fakeSecretFile);
    Environment.SetEnvironmentVariable("PATH", root + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
}
try
{
    var atomicPath = Path.Combine(root, "atomic.txt");
    await SafeFile.WriteAllTextAtomicAsync(atomicPath, "old");
    await SafeFile.WriteAllTextAtomicAsync(atomicPath, "new");
    Check(File.ReadAllText(atomicPath) == "new", "atomic write replaces complete content");
    Check(!Directory.EnumerateFiles(root, ".atomic.txt.*.tmp").Any(), "successful atomic write removes temp file");

    var impossibleTarget = Path.Combine(root, "target-directory");
    Directory.CreateDirectory(impossibleTarget);
    var replaceFailed = false;
    try { await SafeFile.WriteAllTextAtomicAsync(impossibleTarget, "recoverable"); }
    catch (IOException) { replaceFailed = true; }
    catch (UnauthorizedAccessException) { replaceFailed = true; }
    var recovery = Directory.EnumerateFiles(root, ".target-directory.*.tmp").ToList();
    Check(replaceFailed, "failed replace reports an error");
    Check(Directory.Exists(impossibleTarget), "failed replace leaves destination intact");
    Check(recovery.Count == 1 && File.ReadAllText(recovery[0]) == "recoverable", "failed replace keeps a complete recovery copy");

    var utf8Path = Path.Combine(root, "utf8.md");
    await File.WriteAllTextAsync(utf8Path, "中文\r\ntext", new UTF8Encoding(false));
    var (utf8Text, utf8Format) = await TextFileFormat.ReadAsync(utf8Path);
    Check(!utf8Format.LossyDecode, "valid UTF-8 is lossless");
    Check(utf8Text == "中文\ntext", "line endings normalize in memory");
    Check(utf8Format.GetBytes(utf8Text).SequenceEqual(await File.ReadAllBytesAsync(utf8Path)), "UTF-8 bytes and line endings round-trip");

    var legacyPath = Path.Combine(root, "legacy.md");
    await File.WriteAllBytesAsync(legacyPath, new byte[] { 0xD6, 0xD0, 0xCE, 0xC4 }); // GBK: 中文
    var (_, legacyFormat) = await TextFileFormat.ReadAsync(legacyPath);
    Check(legacyFormat.LossyDecode, "non-UTF-8 bytes are marked lossy");

    var writes = new List<string>();
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var active = 0;
    var maxActive = 0;
    async Task SlowWriter(string _, string value)
    {
        var now = Interlocked.Increment(ref active);
        maxActive = Math.Max(maxActive, now);
        lock (writes) writes.Add(value);
        if (value == "one")
        {
            firstStarted.TrySetResult();
            await gate.Task;
        }
        Interlocked.Decrement(ref active);
    }

    var queue = new SerializedFileWriter(Path.Combine(root, "queue.json"), SlowWriter);
    queue.Queue("one");
    await firstStarted.Task;
    queue.Queue("two");
    queue.Queue("three");
    gate.SetResult();
    await queue.FlushAsync();
    Check(maxActive == 1, "snapshot writes never overlap");
    Check(writes.SequenceEqual(new[] { "one", "three" }), "bursts coalesce to the newest pending snapshot");

    var attempts = 0;
    var errorQueue = new SerializedFileWriter(Path.Combine(root, "error.json"), (_, _) =>
    {
        attempts++;
        return attempts == 1 ? Task.FromException(new IOException("injected")) : Task.CompletedTask;
    });
    errorQueue.Queue("bad");
    await errorQueue.FlushAsync();
    Check(errorQueue.LastWriteError is IOException, "write error is observable");
    errorQueue.Queue("good");
    await errorQueue.FlushAsync();
    Check(errorQueue.LastWriteError == null, "later successful write clears the error");

    if (OperatingSystem.IsLinux())
    {
        CredentialStore.Queue("credential test value");
        await CredentialStore.FlushAsync();
        Check(CredentialStore.LastWriteError == null && CredentialStore.Load() == "credential test value",
            "Linux credential adapter stores and retrieves through Secret Service tooling");
        CredentialStore.Queue("");
        await CredentialStore.FlushAsync();
        Check(CredentialStore.Load() == null, "Linux credential adapter clears a stored password");
    }

    var hostileCss = "body{} </script><script>window.pwned=1</script>";
    var designer = ThemeDesignerPage.EmbedCatalog(ThemeDesignerPage.EmptyCatalog,
        new ThemeDesignerCatalog { SelectedId = "unsafe", Themes = new[] { new ThemeDesignerEntry { Id = "unsafe", Css = hostileCss } } });
    Check(!designer.Contains("</script><script>window.pwned", StringComparison.Ordinal), "theme catalog cannot break out of its JSON script element");
    var badPlaceholderRejected = false;
    try { ThemeDesignerPage.EmbedCatalog("missing", new ThemeDesignerCatalog()); }
    catch (InvalidDataException) { badPlaceholderRejected = true; }
    Check(badPlaceholderRejected, "theme designer rejects a missing catalog placeholder");

    var firstDraft = Guid.NewGuid().ToString("N");
    var secondDraft = Guid.NewGuid().ToString("N");
    Check(await AutoBackup.BackupAsync(null, firstDraft, "first draft"), "first untitled backup succeeds");
    Check(await AutoBackup.BackupAsync(null, secondDraft, "second draft"), "second untitled backup succeeds");
    Check(AutoBackup.Read(null, firstDraft) == "first draft" && AutoBackup.Read(null, secondDraft) == "second draft",
        "untitled tabs keep independent crash backups");

    var namedDocument = Path.Combine(root, "named.md");
    await AutoBackup.BackupAsync(namedDocument, Guid.NewGuid().ToString("N"), "named backup");
    var oldNamedBackup = Path.Combine(CursorMemory.DataFolder, "Backup", $"{SafeFile.Hash(Path.GetFullPath(namedDocument)):x}_{Path.GetFileName(namedDocument)}");
    Check(File.Exists(oldNamedBackup), "named backup path remains compatible with v1.1.3");

    var legacyUntitled = Path.Combine(CursorMemory.DataFolder, "Backup", $"{SafeFile.Hash(""):x}_");
    await SafeFile.WriteAllTextAtomicAsync(legacyUntitled, "legacy draft");
    var upgradedDraft = Guid.NewGuid().ToString("N");
    Check(AutoBackup.Read(null, upgradedDraft) == "legacy draft" && !File.Exists(legacyUntitled),
        "v1.1.3 untitled backup is claimed during migration");

    SessionMemory.Save(new[]
    {
        new SessionMemory.DocumentEntry { FilePath = null, DocumentId = firstDraft },
        new SessionMemory.DocumentEntry { FilePath = null, DocumentId = secondDraft },
    }, 1, root);
    await SessionMemory.FlushAsync();
    var session = SessionMemory.Load();
    Check(session?.Documents.Count == 2 && session.Documents[1].DocumentId == secondDraft && session.ActiveIndex == 1,
        "session preserves multiple untitled document ids and the active tab");
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} reliability check(s) failed: {string.Join(", ", failures)}");
    return 1;
}

Console.WriteLine("All reliability checks passed.");
return 0;
