namespace ThixSync.Core.Models;

/// <summary>Why a run started.</summary>
public enum RunTrigger
{
    Manual = 0,
    Schedule = 1,
    Change = 2,
    Startup = 3,
    Cli = 4,
}

/// <summary>How a run ended (Running while it runs).</summary>
public enum RunOutcome
{
    Running = 0,

    /// <summary>Everything was done; both sides are in step.</summary>
    Success = 1,

    /// <summary>Done, but some files were skipped or conflicts wait for the user.</summary>
    Warnings = 2,

    /// <summary>The run could not reach a side or stopped with an error.</summary>
    Failed = 3,

    Cancelled = 4,

    /// <summary>Only analysed (preview), nothing copied.</summary>
    Analysed = 5,

    /// <summary>Stopped before copying: it would delete more than the safety limit allows.</summary>
    NeedsConfirmation = 6,
}

/// <summary>A run of a job (the page "Protokolle"). Kept on this computer (not synced).</summary>
public sealed record SyncRun
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid JobId { get; init; }

    /// <summary>The name of the job at the time of the run (the job may be renamed or deleted later).</summary>
    public string JobName { get; init; } = "";

    public RunTrigger Trigger { get; init; }

    public RunOutcome Outcome { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    public long FilesCopied { get; init; }

    public long FilesDeleted { get; init; }

    public long FilesMoved { get; init; }

    public long FoldersCreated { get; init; }

    public long BytesCopied { get; init; }

    public long Conflicts { get; init; }

    public long Errors { get; init; }

    /// <summary>Files looked at on both sides together.</summary>
    public long FilesScanned { get; init; }

    /// <summary>The one line that says what happened (German, for the list).</summary>
    public string Summary { get; init; } = "";

    public string DeviceName { get; init; } = "";

    public TimeSpan Duration => (FinishedAt ?? StartedAt) - StartedAt;
}

/// <summary>The kind of a line in the protocol of a run.</summary>
public enum RunEntryLevel
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>One line of the protocol of a run (what was copied, deleted, what failed and why).</summary>
public sealed record RunEntry
{
    public DateTimeOffset At { get; init; }

    public RunEntryLevel Level { get; init; }

    /// <summary>The action (null for a general message).</summary>
    public PlanAction? Action { get; init; }

    /// <summary>The path relative to the folder pair ('/').</summary>
    public string Path { get; init; } = "";

    public long Bytes { get; init; }

    public string Message { get; init; } = "";
}
