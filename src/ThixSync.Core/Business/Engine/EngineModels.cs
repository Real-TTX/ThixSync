using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;

namespace ThixSync.Core.Business.Engine;

/// <summary>One side of a folder pair.</summary>
internal enum Side
{
    Source = 0,
    Target = 1,
}

/// <summary>What one side of a pair has (after the filter), keyed by path (case-insensitively when either side ignores case).</summary>
internal sealed class SideListing(StringComparer comparer)
{
    /// <summary>Files and folders the job looks at.</summary>
    public Dictionary<string, StorageEntry> Entries { get; } = new(comparer);

    /// <summary>Folders that could not be read: what is missing below them is unknown, never "deleted".</summary>
    public HashSet<string> Unreadable { get; } = new(comparer);

    /// <summary>Folders that hold something the job does not look at (filtered, unreadable): never deleted as a whole.</summary>
    public HashSet<string> Tainted { get; } = new(comparer);

    public long Files { get; set; }

    public long Bytes { get; set; }

    /// <summary>The change-feed cursor taken before the listing (null: none).</summary>
    public string? Cursor { get; set; }

    public bool TryGet(string key, out StorageEntry entry) => Entries.TryGetValue(key, out entry);

    /// <summary>The path as this side writes it (the case of the existing entry), or the key.</summary>
    public string PathOf(string key) => Entries.TryGetValue(key, out var entry) ? entry.Path : key;

    /// <summary>True when the path lies in (or is) a folder that could not be read.</summary>
    public bool IsInsideUnreadable(string path)
    {
        if (Unreadable.Count == 0)
        {
            return false;
        }

        if (Unreadable.Contains(path))
        {
            return true;
        }

        var lookup = Unreadable.GetAlternateLookup<ReadOnlySpan<char>>();
        for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
        {
            if (lookup.Contains(path.AsSpan(0, slash)))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>A folder planned for deletion on one side and how (decided by the planner).</summary>
/// <param name="Side">The side the folder is deleted on.</param>
/// <param name="Recursive">The folder goes with everything in it (everything inside is planned for deletion, nothing unknown inside).</param>
/// <param name="Deletable">Files that may be inside when the folder is deleted (each unchanged since the scan).</param>
/// <param name="Covered">Every path below the folder whose state goes with it.</param>
/// <param name="Files">How many files go with the folder.</param>
internal sealed record FolderDelete(Side Side, bool Recursive, HashSet<string> Deletable, List<string> Covered, int Files);

/// <summary>One folder pair while a run works on it: the opened storages, both listings, the state of the pair.</summary>
internal sealed class PairContext
{
    public required int Index { get; init; }

    public required FolderPair Pair { get; init; }

    public required string Fingerprint { get; init; }

    public PairState? Meta { get; set; }

    /// <summary>The state of the pair does not fit (new pair, other folders or connections): compare like a first run.</summary>
    public bool IsReset { get; set; }

    public IStorage? Source { get; set; }

    public IStorage? Target { get; set; }

    public StringComparer Comparer { get; set; } = StringComparer.Ordinal;

    public SideListing SourceListing { get; set; } = new(StringComparer.Ordinal);

    public SideListing TargetListing { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Paths with two entries that differ only in case on a side that ignores case: left out.</summary>
    public HashSet<string> Collisions { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The state of the pair (path → row) as loaded.</summary>
    public Dictionary<string, StateRow> State { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Folders the plan deletes, by path.</summary>
    public Dictionary<string, FolderDelete> FolderDeletes { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Why the pair could not be connected or scanned (null: fine). Such a pair is left out of the run.</summary>
    public string? Problem { get; set; }

    /// <summary>Files left out by the filter (both sides, by path).</summary>
    public long Skipped { get; set; }

    public bool IsUsable => Problem is null && Source is not null && Target is not null;

    public IStorage Storage(Side side) => (side == Side.Source ? Source : Target) ?? throw new InvalidOperationException("The pair is not connected.");

    public SideListing Listing(Side side) => side == Side.Source ? SourceListing : TargetListing;

    public static Side Other(Side side) => side == Side.Source ? Side.Target : Side.Source;

    /// <summary>The time tolerance of a side compared with its own earlier values.</summary>
    public TimeSpan Tolerance(Side side, int toleranceSeconds)
    {
        var coarse = Storage(side).Capabilities.Has(StorageFeatures.CoarseTime);
        return TimeSpan.FromSeconds(Math.Max(toleranceSeconds, coarse ? 2 : 0));
    }
}

/// <summary>One step of the plan with its pair, ready for a worker.</summary>
internal sealed record WorkItem(PairContext Pair, PlanItem Item)
{
    public int Depth => StoragePath.DepthOf(Item.Path);
}

/// <summary>Everything an analysis found: the plan, the pairs, the state changes that need no transfer, the warnings.</summary>
internal sealed class Analysis
{
    public required SyncPlan Plan { get; set; }

    public required IReadOnlyList<PairContext> Pairs { get; init; }

    /// <summary>Rows written without any transfer (folders both sides have).</summary>
    public List<StateRow> QuietRows { get; } = [];

    /// <summary>Rows of paths no side has any more, or the filter leaves out now.</summary>
    public List<(int PairIndex, string Path)> DroppedRows { get; } = [];

    /// <summary>Warnings of the scan and the comparison (collisions, unreadable folders, file vs folder).</summary>
    public List<RunEntry> Warnings { get; } = [];

    public PairContext PairOf(PlanItem item) => Pairs[item.PairIndex];
}

/// <summary>The numbers of a run (any thread).</summary>
internal sealed class RunCounters
{
    private long _filesCopied;
    private long _filesDeleted;
    private long _filesMoved;
    private long _foldersCreated;
    private long _bytesCopied;
    private long _conflicts;
    private long _errors;

    public long FilesCopied => Interlocked.Read(ref _filesCopied);

    public long FilesDeleted => Interlocked.Read(ref _filesDeleted);

    public long FilesMoved => Interlocked.Read(ref _filesMoved);

    public long FoldersCreated => Interlocked.Read(ref _foldersCreated);

    public long BytesCopied => Interlocked.Read(ref _bytesCopied);

    public long Conflicts => Interlocked.Read(ref _conflicts);

    public long Errors => Interlocked.Read(ref _errors);

    public void Copied(long bytes)
    {
        Interlocked.Increment(ref _filesCopied);
        Interlocked.Add(ref _bytesCopied, bytes);
    }

    public void Deleted(long files) => Interlocked.Add(ref _filesDeleted, files);

    public void Moved() => Interlocked.Increment(ref _filesMoved);

    public void FolderCreated() => Interlocked.Increment(ref _foldersCreated);

    public void Conflict() => Interlocked.Increment(ref _conflicts);

    public void Error() => Interlocked.Increment(ref _errors);
}

/// <summary>Small helpers for paths, entries and hashes.</summary>
internal static class EngineText
{
    /// <summary>The kind of a hash ("md5:ab12…" → "md5").</summary>
    public static string HashKindOf(string hash)
    {
        var colon = hash.IndexOf(':');
        return colon < 0 ? "" : hash[..colon];
    }

    /// <summary>True when both hashes are known and of the same kind (so they can be compared).</summary>
    public static bool AreComparable(string? first, string? second) =>
        first is not null && second is not null && string.Equals(HashKindOf(first), HashKindOf(second), StringComparison.OrdinalIgnoreCase);

    public static bool HashesEqual(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    public static FileFacts Facts(StorageEntry entry) => new(entry.Size, entry.Modified, entry.IsFolder);

    public static string SideName(Side side) => side == Side.Source ? "Quelle" : "Ziel";

    /// <summary>"in der Quelle" / "im Ziel".</summary>
    public static string InSide(Side side) => side == Side.Source ? "in der Quelle" : "im Ziel";

    /// <summary>An error for the log file: its kind only (messages of storages may name files and servers).</summary>
    public static string ForLog(Exception exception) => exception is Storage.StorageException storage
        ? $"{nameof(Storage.StorageException)} {storage.Kind}"
        : $"{exception.GetType().Name} 0x{exception.HResult:X8}";
}
