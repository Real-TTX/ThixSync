using ThixSync.Core.Business;
using ThixSync.Core.Business.Engine;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;

namespace ThixSync.Tests;

/// <summary>
/// What the engine tests share: an own data folder (<see cref="TestData"/>), the in-memory storage for connections of type S3, short
/// retry waits, and helpers to make connections, jobs and runs.
/// </summary>
public abstract class EngineTestBase : IDisposable
{
    internal static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TestData _data = new();

    protected EngineTestBase()
    {
        MemoryProvider.Drives.Clear();
        StorageProviders.OverrideForTests(ConnectionKind.S3, new MemoryProvider());
        RetryRunner.BaseDelay = TimeSpan.FromMilliseconds(5);
    }

    internal TestData Data => _data;

    public void Dispose()
    {
        SchedulerService.Stop();
        JobQueue.LimitForTests = null;
        BandwidthThrottle.LimitForTests = null;
        PowerStatus.OnBatteryForTests = null;
        RetryRunner.BaseDelay = TimeSpan.FromMilliseconds(500);
        AppClock.Pin(null);
        MemoryProvider.Drives.Clear();
        _data.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A point in time <paramref name="minutes"/> after the start of the test world.</summary>
    internal static DateTimeOffset At(int minutes) => Start.AddMinutes(minutes);

    /// <summary>A drive with the given features, reachable through a connection named like it.</summary>
    internal static MemoryDrive Drive(string name, StorageFeatures features = StorageFeatures.SetModifiedTime | StorageFeatures.Move | StorageFeatures.RealFolders,
        string? hashKind = null)
    {
        var drive = new MemoryDrive(features, hashKind);
        MemoryProvider.Drives[name] = drive;
        return drive;
    }

    internal static Connection MemoryConnection(string bucket) => ConnectionService.Save(new Connection
    {
        Name = bucket,
        Kind = ConnectionKind.S3,
        Settings = new Dictionary<string, string> { [ConnectionFields.Bucket] = bucket, [ConnectionFields.AccessKeyId] = "key" },
    }, "secret");

    /// <summary>A job between two memory drives "quelle" and "ziel" (made when missing).</summary>
    internal static SyncJob MemoryJob(SyncMode mode, Func<JobOptions, JobOptions>? options = null, JobFilter? filter = null, string name = "Auftrag",
        string source = "quelle", string target = "ziel")
    {
        if (!MemoryProvider.Drives.ContainsKey(source))
        {
            Drive(source);
        }

        if (!MemoryProvider.Drives.ContainsKey(target))
        {
            Drive(target);
        }

        var sourceConnection = ConnectionService.List().FirstOrDefault(connection => connection.Name == source) ?? MemoryConnection(source);
        var targetConnection = ConnectionService.List().FirstOrDefault(connection => connection.Name == target) ?? MemoryConnection(target);
        return JobService.Save(new SyncJob
        {
            Name = name,
            Mode = mode,
            SourceConnectionId = sourceConnection.Id,
            TargetConnectionId = targetConnection.Id,
            Pairs = [new FolderPair()],
            Options = (options ?? (value => value))(new JobOptions { KeepVersions = false }),
            Filter = filter ?? new JobFilter(),
        });
    }

    internal static SyncJob Change(SyncJob job, Func<SyncJob, SyncJob> change) => JobService.Save(change(JobService.Get(job.Id)));

    internal static Task<SyncRun> RunAsync(SyncJob job, RunRequest? request = null, CancellationToken cancellationToken = default) =>
        JobRunner.RunAsync(job.Id, request ?? new RunRequest(), cancellationToken);

    internal static List<RunEntry> Entries(SyncRun run) => SyncStore.LoadEntries(run.Id, 100_000);

    internal static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var limit = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (!condition())
        {
            if (DateTime.UtcNow > limit)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            await Task.Delay(20);
        }
    }
}
