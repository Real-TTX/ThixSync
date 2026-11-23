namespace ThixSync.Core.Models;

/// <summary>How a job brings source and target together. The numbers are stored: never change or reuse one.</summary>
public enum SyncMode
{
    /// <summary>Changes on both sides go to the other side (new, changed, deleted, renamed); both changed = conflict.</summary>
    TwoWay = 0,

    /// <summary>The target becomes an exact copy of the source: what is only on the target is deleted (into the versions, if kept).</summary>
    Mirror = 1,

    /// <summary>New and changed files go from the source to the target; nothing is deleted there; old versions are kept.</summary>
    Backup = 2,
}

/// <summary>What happens when a file changed on both sides since the last run.</summary>
public enum ConflictPolicy
{
    /// <summary>Nothing is copied; the conflict waits for the user (Details: resolve).</summary>
    Ask = 0,

    /// <summary>The newer file wins (the older one goes into the versions, if kept).</summary>
    NewerWins = 1,

    SourceWins = 2,

    TargetWins = 3,

    /// <summary>Both stay: the losing side's file is renamed "name (Konflikt GERÄT 2026-11-23 10-24).ext" and copied too.</summary>
    KeepBoth = 4,
}

/// <summary>When a job runs by itself.</summary>
public enum ScheduleKind
{
    /// <summary>Only when the user (or the CLI) starts it.</summary>
    Manual = 0,

    /// <summary>Every <see cref="JobSchedule.IntervalMinutes"/> minutes.</summary>
    Interval = 1,

    /// <summary>Every day at <see cref="JobSchedule.TimeOfDay"/>.</summary>
    Daily = 2,

    /// <summary>On <see cref="JobSchedule.Days"/> at <see cref="JobSchedule.TimeOfDay"/>.</summary>
    Weekly = 3,
}

/// <summary>The schedule of a job. <see cref="RunOnChange"/> and <see cref="RunAtStartup"/> come on top of any kind.</summary>
public sealed record JobSchedule
{
    public ScheduleKind Kind { get; init; } = ScheduleKind.Manual;

    public int IntervalMinutes { get; init; } = 60;

    /// <summary>Local time of day (Daily, Weekly).</summary>
    public TimeOnly TimeOfDay { get; init; } = new(20, 0);

    /// <summary>The days of a weekly schedule.</summary>
    public IReadOnlyList<DayOfWeek> Days { get; init; } = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    /// <summary>Run when something changes: local folders are watched, remote ones are looked at every <see cref="PollMinutes"/>.</summary>
    public bool RunOnChange { get; init; }

    /// <summary>How long a change has to be quiet before the run starts (many small changes become one run).</summary>
    public int ChangeDelaySeconds { get; init; } = 10;

    /// <summary>How often a remote side is checked for changes when <see cref="RunOnChange"/> is on.</summary>
    public int PollMinutes { get; init; } = 15;

    /// <summary>Run once when ThixSync starts (sign-in).</summary>
    public bool RunAtStartup { get; init; }

    /// <summary>A run missed while the computer was off is made up at the next start.</summary>
    public bool CatchUpMissed { get; init; } = true;
}

/// <summary>Which files a job looks at. Patterns: <c>*.tmp</c>, <c>node_modules/</c> (a folder), <c>Fotos/**/*.jpg</c>.</summary>
public sealed record JobFilter
{
    /// <summary>Only these (empty: everything).</summary>
    public IReadOnlyList<string> Include { get; init; } = [];

    /// <summary>Never these.</summary>
    public IReadOnlyList<string> Exclude { get; init; } = DefaultExcludes;

    public bool SkipHidden { get; init; }

    public bool SkipSystemFiles { get; init; } = true;

    /// <summary>Files larger than this are left out (0: no limit).</summary>
    public long MaxFileSizeBytes { get; init; }

    /// <summary>Files older than this many days are left out (0: no limit).</summary>
    public int MaxAgeDays { get; init; }

    public static readonly IReadOnlyList<string> DefaultExcludes =
        ["Thumbs.db", "desktop.ini", ".DS_Store", "~$*", "*.tmp", "*.partial", ".thixsync/", "$RECYCLE.BIN/", "System Volume Information/"];
}

/// <summary>How a job copies.</summary>
public sealed record JobOptions
{
    public ConflictPolicy Conflicts { get; init; } = ConflictPolicy.KeepBoth;

    /// <summary>Deletions are carried over (TwoWay, Mirror). Backup never deletes.</summary>
    public bool PropagateDeletes { get; init; } = true;

    /// <summary>Old versions of overwritten and deleted files are kept in <c>.thixsync/versions</c> on the side they were taken from.</summary>
    public bool KeepVersions { get; init; } = true;

    /// <summary>How long versions are kept (0: for ever).</summary>
    public int KeepVersionsDays { get; init; } = 30;

    /// <summary>How many versions of one file are kept at most (0: no limit).</summary>
    public int KeepVersionsCount { get; init; } = 10;

    /// <summary>Files copied at the same time (0: automatic – what source and target handle best).</summary>
    public int Parallelism { get; init; }

    /// <summary>A file renamed or moved on one side is renamed on the other too (instead of copied again and deleted).</summary>
    public bool DetectMoves { get; init; } = true;

    /// <summary>After copying, the content is compared with a checksum.</summary>
    public bool VerifyAfterCopy { get; init; }

    /// <summary>Compare the content (checksum) and not only size and time when both sides have a file and no state says what happened.</summary>
    public bool CompareContent { get; init; }

    /// <summary>A run that would delete more than this share of the files of a side stops and asks first (0: never asks).</summary>
    public int SafetyDeletePercent { get; init; } = 50;

    /// <summary>Changes of the time stamp within this many seconds count as equal (FAT, FTP and some clouds round).</summary>
    public int TimeToleranceSeconds { get; init; } = 2;

    /// <summary>Copy the time stamp to the target (where the target can keep it).</summary>
    public bool PreserveModifiedTime { get; init; } = true;

    /// <summary>A folder is created on the target when the source has it, even when empty.</summary>
    public bool CopyEmptyFolders { get; init; } = true;
}

/// <summary>One folder of the source that is kept in step with one folder of the target (a job has one or more).</summary>
public sealed record FolderPair
{
    /// <summary>The folder on the source connection ('/'-separated below its base; empty: the base itself).</summary>
    public string SourcePath { get; init; } = "";

    public string TargetPath { get; init; } = "";
}

/// <summary>
/// A sync job (the page "Synchronisationen"): a source connection and a target connection, one or more folder pairs, the mode,
/// filter, options and schedule. Jobs are kept the same on every device of a cloud account; a job runs by itself only on the
/// computer in <see cref="RunsOnDevice"/> (empty: the computer it was made on), the others can start it by hand when they reach
/// both sides.
/// </summary>
public sealed record SyncJob
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = "";

    public string Description { get; init; } = "";

    public SyncMode Mode { get; init; } = SyncMode.TwoWay;

    public Guid SourceConnectionId { get; init; }

    public Guid TargetConnectionId { get; init; }

    public IReadOnlyList<FolderPair> Pairs { get; init; } = [];

    public JobFilter Filter { get; init; } = new();

    public JobOptions Options { get; init; } = new();

    public JobSchedule Schedule { get; init; } = new();

    /// <summary>A paused job does not run by itself.</summary>
    public bool IsPaused { get; init; }

    /// <summary>The computer that runs the job by itself (schedule, changes).</summary>
    public string RunsOnDevice { get; init; } = "";

    public DateTimeOffset CreateDate { get; init; }

    public DateTimeOffset UpdateDate { get; init; }

    public SyncState SyncState { get; init; }
}
