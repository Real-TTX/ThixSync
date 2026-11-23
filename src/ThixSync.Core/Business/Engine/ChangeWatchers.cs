using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;
using ThixSync.Core.Storage.Providers.Local;

namespace ThixSync.Core.Business.Engine;

/// <summary>
/// Watches the local folders of the jobs that run on changes (FileSystemWatcher with subfolders and a generous buffer) and tells when
/// a job's changes have been quiet long enough (<see cref="JobSchedule.ChangeDelaySeconds"/>). The versions folder and the temporary
/// files of a write are ignored; a lost event (buffer overflow) counts as a change.
/// </summary>
internal sealed class ChangeWatchers : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, JobWatch> _jobs = [];

    /// <summary>Watches exactly the given jobs (new ones get watchers, changed ones new watchers, others lose theirs).</summary>
    public void Update(IEnumerable<SyncJob> jobs)
    {
        var wanted = jobs.ToDictionary(job => job.Id);
        List<JobWatch> gone;
        List<SyncJob> added;
        lock (_gate)
        {
            gone = [.. _jobs.Values.Where(watch => !wanted.TryGetValue(watch.JobId, out var job) || job.UpdateDate != watch.Version)];
            foreach (var watch in gone)
            {
                _jobs.Remove(watch.JobId);
            }

            added = [.. wanted.Values.Where(job => !_jobs.ContainsKey(job.Id))];
        }

        foreach (var watch in gone)
        {
            watch.Dispose();
        }

        foreach (var job in added)
        {
            var watch = new JobWatch(job.Id, job.UpdateDate);
            foreach (var folder in LocalFoldersOf(job))
            {
                watch.Add(folder);
            }

            lock (_gate)
            {
                if (!_jobs.TryAdd(job.Id, watch))
                {
                    watch.Dispose();
                }
            }
        }
    }

    /// <summary>True (once) when the job had changes and they have been quiet for <paramref name="delay"/>.</summary>
    public bool TakeDue(Guid jobId, TimeSpan delay)
    {
        lock (_gate)
        {
            return _jobs.TryGetValue(jobId, out var watch) && watch.TakeDue(delay);
        }
    }

    /// <summary>Some job has changes that are not quiet yet (the scheduler looks again soon).</summary>
    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _jobs.Values.Any(watch => watch.HasPending);
            }
        }
    }

    /// <summary>Tests and the poll of remote sides: marks a job as changed now.</summary>
    public void Touch(Guid jobId)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue(jobId, out var watch))
            {
                watch.Touch();
            }
        }
    }

    public void Dispose()
    {
        List<JobWatch> all;
        lock (_gate)
        {
            all = [.. _jobs.Values];
            _jobs.Clear();
        }

        foreach (var watch in all)
        {
            watch.Dispose();
        }
    }

    /// <summary>The local root folders of a job's pairs (both sides; sides that are not on this computer are polled instead).</summary>
    private static List<string> LocalFoldersOf(SyncJob job)
    {
        var folders = new List<string>();
        var source = ConnectionService.Find(job.SourceConnectionId);
        var target = ConnectionService.Find(job.TargetConnectionId);
        foreach (var pair in job.Pairs)
        {
            foreach (var (connection, folder) in new[] { (source, pair.SourcePath), (target, pair.TargetPath) })
            {
                if (connection is null)
                {
                    continue;
                }

                try
                {
                    var storage = ConnectionService.Open(connection, folder);
                    if (storage is ILocalStorage local)
                    {
                        folders.Add(local.GetLocalPath(""));
                    }

                    storage.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception exception) when (exception is BusinessException or StorageException or IOException or ArgumentException)
                {
                    // not reachable here right now: nothing to watch
                }
            }
        }

        return folders;
    }

    private sealed class JobWatch(Guid jobId, DateTimeOffset version) : IDisposable
    {
        private readonly List<FileSystemWatcher> _watchers = [];
        private long _lastChange = long.MinValue;

        public Guid JobId { get; } = jobId;

        public DateTimeOffset Version { get; } = version;

        public bool HasPending => Interlocked.Read(ref _lastChange) != long.MinValue;

        public void Add(string folder)
        {
            if (!Directory.Exists(folder))
            {
                return;
            }

            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                watcher.Created += OnChanged;
                watcher.Changed += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnChanged;
                watcher.Error += (_, _) => Touch();
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception exception) when (exception is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
            {
                Log.Warning($"A folder cannot be watched for changes ({EngineText.ForLog(exception)})");
            }
        }

        public void Touch() => Interlocked.Exchange(ref _lastChange, Environment.TickCount64);

        public bool TakeDue(TimeSpan delay)
        {
            var last = Interlocked.Read(ref _lastChange);
            if (last == long.MinValue || Environment.TickCount64 - last < (long)delay.TotalMilliseconds)
            {
                return false;
            }

            return Interlocked.CompareExchange(ref _lastChange, long.MinValue, last) == last;
        }

        public void Dispose()
        {
            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }

            _watchers.Clear();
        }

        private void OnChanged(object sender, FileSystemEventArgs change)
        {
            var path = change.FullPath.Replace('\\', '/');
            var name = Path.GetFileName(path);
            if (name.StartsWith(LocalStorage.TempPrefix, StringComparison.Ordinal)
                || path.Contains("/.thixsync/", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/.thixsync", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Touch();
        }
    }
}
