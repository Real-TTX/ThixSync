using System.Collections.Concurrent;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;

namespace ThixSync.Core.Business.Engine;

/// <summary>Everything the workers of one run share: the choices, the meter, the protocol, the state, the lanes and the failure.</summary>
internal sealed class RunContext(CancellationTokenSource cancellation)
{
    private StorageException? _failure;

    public required PlanSettings Settings { get; init; }

    public required TransferMeter Meter { get; init; }

    public required RunLog Log { get; init; }

    public required StateBook State { get; init; }

    public required RetryRunner Retry { get; init; }

    public required Versions Versions { get; init; }

    /// <summary>Workers of the lane for small files (and of folders, moves and deletes).</summary>
    public required int Degree { get; init; }

    /// <summary>Workers of the lane for large files.</summary>
    public required int LargeDegree { get; init; }

    /// <summary>Files up to this size go through the lane for small files.</summary>
    public required long SmallFileBytes { get; init; }

    public bool Verify => Settings.Options.VerifyAfterCopy;

    public bool KeepVersions => Settings.KeepVersions;

    /// <summary>The limiter of the small lane (at most <see cref="Degree"/>, less while a storage asks to slow down).</summary>
    public required AdaptiveLimiter SmallLane { get; init; }

    /// <summary>The limiter of the large lane (at most <see cref="LargeDegree"/>).</summary>
    public required AdaptiveLimiter LargeLane { get; init; }

    public RunCounters Counters { get; } = new();

    /// <summary>The pairs and sides that got versions in this run (pruned after the run).</summary>
    public ConcurrentDictionary<(int PairIndex, Side Side), bool> VersionsWritten { get; } = new();

    public CancellationToken Token => cancellation.Token;

    /// <summary>Why the run stopped as a whole (null: it did not).</summary>
    public StorageException? Failure => Volatile.Read(ref _failure);

    /// <summary>Stops the whole run (sign-in refused, full, not reachable): the first reason is kept.</summary>
    public void Fail(StorageException exception)
    {
        Interlocked.CompareExchange(ref _failure, exception, null);
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public static StateRow FileRow(int pairIndex, string path, in StorageEntry source, in StorageEntry target) =>
        new(pairIndex, path, false, source.Size, source.Modified, source.Hash, target.Size, target.Modified, target.Hash);

    public static StateRow FolderRow(int pairIndex, string path) => new(pairIndex, path, true, 0, default, null, 0, default, null);
}
