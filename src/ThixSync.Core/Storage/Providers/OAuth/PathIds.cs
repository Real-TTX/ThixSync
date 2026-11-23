using System.Collections.Concurrent;

namespace ThixSync.Core.Storage.Providers.OAuth;

/// <summary>The id of a path as the cloud knows it.</summary>
internal readonly record struct CachedId(string Id, bool IsFolder);

/// <summary>
/// The path → id cache of an id-based cloud (Google Drive, Box), safe for many workers: filled by listings and lookups, changed by
/// writes, moves and deletes. "Complete" folders are those whose children are all in the cache (listed, or created by this storage): a
/// name that is missing there does not exist, without asking the cloud. A lock per path makes sure that several workers that need the
/// same missing folder create it once (two folders with the same name are possible on Drive).
/// </summary>
internal sealed class PathIds(StringComparer comparer)
{
    private readonly ConcurrentDictionary<string, CachedId> _ids = new(comparer);
    private readonly ConcurrentDictionary<string, byte> _complete = new(comparer);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(comparer);

    public bool TryGet(string path, out CachedId id) => _ids.TryGetValue(path, out id);

    public void Set(string path, string id, bool isFolder) => _ids[path] = new CachedId(id, isFolder);

    /// <summary>Forgets a path and everything below it.</summary>
    public void Remove(string path)
    {
        _ids.TryRemove(path, out _);
        _complete.TryRemove(path, out _);
        var prefix = path + "/";
        foreach (var key in _ids.Keys)
        {
            if (key.StartsWith(prefix, comparer == StringComparer.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            {
                _ids.TryRemove(key, out _);
                _complete.TryRemove(key, out _);
            }
        }
    }

    /// <summary>The cached id turned out to be wrong (gone, moved by someone else): forget it and do not trust its folder any more.</summary>
    public void Invalidate(string path)
    {
        Remove(path);
        _complete.TryRemove(StoragePath.ParentOf(path), out _);
    }

    public void MarkComplete(string folder) => _complete[folder] = 0;

    public bool IsComplete(string folder) => _complete.ContainsKey(folder);

    /// <summary>Holds the lock of one path (dispose the result to release it).</summary>
    public async Task<IDisposable> LockAsync(string path, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
