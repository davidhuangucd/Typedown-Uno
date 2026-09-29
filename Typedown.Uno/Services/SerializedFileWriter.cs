namespace Typedown.Uno.Services;

/// <summary>
/// Writes coherent snapshots in order. When changes arrive faster than the disk can accept them, only the
/// newest pending snapshot is kept; an older write can therefore never finish after a newer one.
/// </summary>
public sealed class SerializedFileWriter
{
    private readonly object sync = new();
    private readonly string path;
    private readonly Func<string, string, Task> writer;
    private string? pending;
    private bool running;
    private Task writerTask = Task.CompletedTask;

    public Exception? LastWriteError { get; private set; }

    public SerializedFileWriter(string path, Func<string, string, Task>? writer = null)
    {
        this.path = path ?? throw new ArgumentNullException(nameof(path));
        this.writer = writer ?? ((target, text) => SafeFile.WriteAllTextAtomicAsync(target, text));
    }

    public void Queue(string snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (sync)
        {
            pending = snapshot;
            if (running) return;
            running = true;
            writerTask = Task.Run(DrainAsync);
        }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            string snapshot;
            lock (sync)
            {
                if (pending == null)
                {
                    running = false;
                    return;
                }
                snapshot = pending;
                pending = null;
            }

            try
            {
                await writer(path, snapshot);
                lock (sync) LastWriteError = null;
            }
            catch (Exception ex)
            {
                lock (sync) LastWriteError = ex;
                Console.Error.WriteLine($"write {Path.GetFileName(path)} failed: {ex.Message}");
            }
        }
    }

    /// <summary>Waits until all snapshots queued before or during the call reach a terminal write.</summary>
    public async Task FlushAsync()
    {
        while (true)
        {
            Task pendingTask;
            lock (sync) pendingTask = writerTask;
            await pendingTask;
            lock (sync)
            {
                if (!running && pending == null) return;
            }
        }
    }
}
