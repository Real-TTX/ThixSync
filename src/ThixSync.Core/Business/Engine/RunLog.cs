using System.Collections.Concurrent;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;

namespace ThixSync.Core.Business.Engine;

/// <summary>
/// The protocol lines of a run: workers add them from any thread, <see cref="Flush"/> writes them in batches
/// (<see cref="SyncStore.AddEntries"/>, one transaction).
/// </summary>
internal sealed class RunLog(Guid runId)
{
    private readonly ConcurrentQueue<RunEntry> _queue = new();
    private readonly Lock _flushGate = new();

    public int Pending => _queue.Count;

    public void Info(PlanAction? action, string path, long bytes, string message) => Add(RunEntryLevel.Info, action, path, bytes, message);

    public void Warning(PlanAction? action, string path, string message) => Add(RunEntryLevel.Warning, action, path, 0, message);

    public void Error(PlanAction? action, string path, string message) => Add(RunEntryLevel.Error, action, path, 0, message);

    public void Add(RunEntry entry) => _queue.Enqueue(entry);

    public void Flush()
    {
        lock (_flushGate)
        {
            var batch = new List<RunEntry>(Math.Min(_queue.Count, 5000));
            while (_queue.TryDequeue(out var entry))
            {
                batch.Add(entry);
                if (batch.Count < 5000)
                {
                    continue;
                }

                Write(batch);
                batch.Clear();
            }

            Write(batch);
        }
    }

    private void Write(List<RunEntry> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            SyncStore.AddEntries(runId, batch);
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException)
        {
            Log.Error("Protocol lines could not be written", exception);
        }
    }

    private void Add(RunEntryLevel level, PlanAction? action, string path, long bytes, string message) =>
        _queue.Enqueue(new RunEntry { At = AppClock.UtcNow, Level = level, Action = action, Path = path, Bytes = bytes, Message = message });
}
