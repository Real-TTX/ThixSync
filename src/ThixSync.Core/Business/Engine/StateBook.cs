using ThixSync.Core.Infrastructure;

namespace ThixSync.Core.Business.Engine;

/// <summary>
/// The state of a job while a run changes it: the rows of every pair in memory (keyed like the listings) and the changes not yet
/// written. Workers record each finished path here; <see cref="Flush"/> writes the changes in one transaction (every few thousand
/// paths, every 30 seconds and at the end), so a cancelled or crashed run keeps what it finished.
/// </summary>
internal sealed class StateBook(IReadOnlyList<PairContext> pairs, JobStateStore store)
{
    private readonly Lock _gate = new();
    private readonly Lock _flushGate = new();

    // (pair, exact path as stored) → the row to write, or null to delete it
    private readonly Dictionary<(int PairIndex, string Path), StateRow?> _pending = [];

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    public void Set(StateRow row)
    {
        var rows = pairs[row.PairIndex].State;
        lock (_gate)
        {
            if (rows.Remove(row.Path, out var old) && !string.Equals(old.Path, row.Path, StringComparison.Ordinal))
            {
                _pending[(row.PairIndex, old.Path)] = null; // the case of the name changed: the old row goes
            }

            rows[row.Path] = row;
            _pending[(row.PairIndex, row.Path)] = row;
        }
    }

    public void Remove(int pairIndex, string path)
    {
        var rows = pairs[pairIndex].State;
        lock (_gate)
        {
            if (rows.Remove(path, out var old))
            {
                _pending[(pairIndex, old.Path)] = null;
            }
        }
    }

    /// <summary>
    /// Removes a row and writes that at once, before a file leaves its place for a moment (moved into the versions while the new
    /// one is written): should ThixSync end right then, the next run sees a new file, not a deleted one. Returns the row to put back.
    /// </summary>
    public StateRow? Park(int pairIndex, string path)
    {
        var rows = pairs[pairIndex].State;
        StateRow? old;
        lock (_gate)
        {
            if (!rows.Remove(path, out old))
            {
                return null;
            }

            _pending[(pairIndex, old.Path)] = null;
        }

        Flush();
        return old;
    }

    /// <summary>Writes the pending changes (one transaction). A failure keeps them for the next try.</summary>
    public void Flush()
    {
        lock (_flushGate)
        {
            List<KeyValuePair<(int PairIndex, string Path), StateRow?>> changes;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    return;
                }

                changes = [.. _pending];
                _pending.Clear();
            }

            var upserts = changes.Where(change => change.Value is not null).Select(change => change.Value!).ToList();
            var deletes = changes.Where(change => change.Value is null).Select(change => change.Key).ToList();
            try
            {
                store.Save(upserts, deletes);
            }
            catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException)
            {
                Log.Error("The job state could not be saved", exception);
                lock (_gate)
                {
                    foreach (var (key, row) in changes)
                    {
                        _pending.TryAdd(key, row); // newer changes of the same path win
                    }
                }
            }
        }
    }
}
