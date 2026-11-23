using ThixSync.Core.Infrastructure;

namespace ThixSync.Core.Business;

/// <summary>
/// Notices changes another process made to the database (the CLI paused a job, imported connections, a run of the CLI ended) and
/// tells the app through <see cref="ConnectionService.Changed"/>, <see cref="JobService.Changed"/> and <see cref="ProtocolService.ExternalChange"/>
/// (checked every two seconds; SQLite's data_version, no polling of the tables).
/// </summary>
public static class StoreWatcher
{
    private static readonly Lock Gate = new();
    private static Timer? _timer;
    private static long _lastVersion = -1;

    public static void Start()
    {
        lock (Gate)
        {
            _timer ??= new Timer(_ => Check(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _timer?.Dispose();
            _timer = null;
            _lastVersion = -1;
        }
    }

    /// <summary>One check (the timer; tests call it directly).</summary>
    internal static void Check()
    {
        long version;
        try
        {
            version = SyncStore.DataVersion();
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException)
        {
            Log.Warning($"The database could not be checked for changes: {exception.GetType().Name}");
            return;
        }

        if (Interlocked.Exchange(ref _lastVersion, version) is var previous && (previous == -1 || previous == version))
        {
            return;
        }

        ConnectionService.NotifyExternalChange();
        JobService.ChangedNow();
        ProtocolService.NotifyExternalChange();
    }
}
