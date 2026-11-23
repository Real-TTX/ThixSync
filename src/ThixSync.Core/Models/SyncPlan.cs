namespace ThixSync.Core.Models;

/// <summary>What a run does with one path. The numbers are stored in the protocol: never change or reuse one.</summary>
public enum PlanAction
{
    /// <summary>Copy the file from the source to the target (new or changed).</summary>
    CopyToTarget = 0,

    /// <summary>Copy the file from the target to the source (two-way only).</summary>
    CopyToSource = 1,

    DeleteOnTarget = 2,

    DeleteOnSource = 3,

    CreateFolderOnTarget = 4,

    CreateFolderOnSource = 5,

    /// <summary>A file was renamed/moved on the source: move it on the target too (<see cref="PlanItem.FromPath"/> → Path).</summary>
    MoveOnTarget = 6,

    MoveOnSource = 7,

    /// <summary>Changed on both sides (or changed on one, deleted on the other): waits for the user or the policy.</summary>
    Conflict = 8,

    /// <summary>Both sides have the same file: only the state is written (first run, same content).</summary>
    InStep = 9,
}

/// <summary>Which side of a conflict the user keeps.</summary>
public enum ConflictChoice
{
    KeepSource = 0,
    KeepTarget = 1,
    KeepBoth = 2,
    Skip = 3,
}

/// <summary>What one side has at a path (null fields: the side has nothing there).</summary>
public sealed record FileFacts(long Size, DateTimeOffset Modified, bool IsFolder);

/// <summary>One step of a plan.</summary>
public sealed record PlanItem
{
    /// <summary>The index of the folder pair of the job.</summary>
    public int PairIndex { get; init; }

    public PlanAction Action { get; init; }

    /// <summary>The path relative to the folder pair ('/').</summary>
    public string Path { get; init; } = "";

    /// <summary>The old path of a move.</summary>
    public string? FromPath { get; init; }

    public bool IsFolder { get; init; }

    /// <summary>The bytes this step moves over the wire (0 for deletes, folders, moves).</summary>
    public long Bytes { get; init; }

    public FileFacts? Source { get; init; }

    public FileFacts? Target { get; init; }

    /// <summary>Why (German, for the preview): "Neu in der Quelle", "Im Ziel geändert", "Auf beiden Seiten geändert" ...</summary>
    public string Reason { get; init; } = "";

    /// <summary>For a conflict: what the user chose (null: still open).</summary>
    public ConflictChoice? Resolution { get; init; }
}

/// <summary>The result of an analysis: everything a run would do, with the totals for the preview.</summary>
public sealed record SyncPlan
{
    public Guid JobId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public IReadOnlyList<PlanItem> Items { get; init; } = [];

    public long SourceFiles { get; init; }

    public long TargetFiles { get; init; }

    public long SourceBytes { get; init; }

    public long TargetBytes { get; init; }

    /// <summary>Files left out by the filter or not readable.</summary>
    public long Skipped { get; init; }

    public int Count(PlanAction action) => Items.Count(item => item.Action == action);

    public long BytesToCopy => Items.Where(item => item.Action is PlanAction.CopyToTarget or PlanAction.CopyToSource).Sum(item => item.Bytes);

    public int OpenConflicts => Items.Count(item => item.Action == PlanAction.Conflict && item.Resolution is null);

    /// <summary>Nothing to do (only InStep items).</summary>
    public bool IsInStep => Items.All(item => item.Action == PlanAction.InStep);
}

/// <summary>The phase a running job is in.</summary>
public enum RunPhase
{
    Connecting = 0,
    Scanning = 1,
    Comparing = 2,
    Transferring = 3,
    Finishing = 4,
}

/// <summary>A file being copied right now (the list "Aktive Übertragungen").</summary>
public sealed record ActiveTransfer(string Path, long Bytes, long Done, PlanAction Action);

/// <summary>
/// The live state of a running job (the progress card, the activity tab, the tray, the CLI). A new snapshot is published several
/// times a second; it is immutable.
/// </summary>
public sealed record RunProgress
{
    public Guid JobId { get; init; }

    public Guid RunId { get; init; }

    public RunPhase Phase { get; init; }

    public long FilesScannedSource { get; init; }

    public long FilesScannedTarget { get; init; }

    public long FilesTotal { get; init; }

    public long FilesDone { get; init; }

    public long BytesTotal { get; init; }

    public long BytesDone { get; init; }

    public long Errors { get; init; }

    /// <summary>Bytes per second, smoothed over the last seconds.</summary>
    public double BytesPerSecond { get; init; }

    /// <summary>Files per second, smoothed (what matters with many small files).</summary>
    public double FilesPerSecond { get; init; }

    /// <summary>The recent speed in bytes per second, one value per second, oldest first (the small graph).</summary>
    public IReadOnlyList<double> SpeedHistory { get; init; } = [];

    /// <summary>Files being copied right now (as many as there are workers).</summary>
    public IReadOnlyList<ActiveTransfer> Active { get; init; } = [];

    /// <summary>Workers copying at the moment.</summary>
    public int Workers { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>Time still needed (null: not known yet).</summary>
    public TimeSpan? Remaining { get; init; }

    /// <summary>0..1 (bytes and files weighted, so many small files move the bar too).</summary>
    public double Fraction { get; init; }

    /// <summary>The one line for the UI: "Vergleiche …", "Kopiere 1.204 von 8.311 Dateien" ...</summary>
    public string Text { get; init; } = "";
}
