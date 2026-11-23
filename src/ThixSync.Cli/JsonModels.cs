using ThixSync.Core.Business;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;

namespace ThixSync.Cli;

// The JSON of --json: a stable contract for scripts (camelCase, enums as words). Add fields, never rename or remove one.

/// <summary>A job (<c>list --json</c>).</summary>
internal sealed record JobJson(
    Guid Id,
    string Name,
    SyncMode Mode,
    bool Paused,
    string Schedule,
    DateTimeOffset? NextRun,
    string RunsOnDevice,
    ConnectionRefJson Source,
    ConnectionRefJson Target,
    IReadOnlyList<PairJson> Pairs,
    RunJson? LastRun)
{
    public static JobJson Of(SyncJob job, SyncRun? lastRun, DateTimeOffset now) => new(
        job.Id,
        job.Name,
        job.Mode,
        job.IsPaused,
        JobService.DescribeSchedule(job.Schedule),
        JobService.NextRun(job, now),
        job.RunsOnDevice,
        ConnectionRefJson.Of(job.SourceConnectionId),
        ConnectionRefJson.Of(job.TargetConnectionId),
        [.. job.Pairs.Select(pair => new PairJson(pair.SourcePath, pair.TargetPath))],
        lastRun is null ? null : RunJson.Of(lastRun));
}

/// <summary>A connection a job names (its name null when it is gone).</summary>
internal sealed record ConnectionRefJson(Guid Id, string? Name)
{
    public static ConnectionRefJson Of(Guid id) => new(id, ConnectionService.Find(id)?.Name);
}

internal sealed record PairJson(string Source, string Target);

/// <summary>A run (<c>runs --json</c>, the result of <c>run --json</c>).</summary>
internal sealed record RunJson(
    Guid Id,
    Guid JobId,
    string JobName,
    RunTrigger Trigger,
    RunOutcome Outcome,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    double DurationSeconds,
    long FilesScanned,
    long FilesCopied,
    long FilesDeleted,
    long FilesMoved,
    long FoldersCreated,
    long BytesCopied,
    long Conflicts,
    long Errors,
    string Summary,
    string DeviceName)
{
    public static RunJson Of(SyncRun run) => new(
        run.Id, run.JobId, run.JobName, run.Trigger, run.Outcome, run.StartedAt, run.FinishedAt, Math.Round(run.Duration.TotalSeconds, 3),
        run.FilesScanned, run.FilesCopied, run.FilesDeleted, run.FilesMoved, run.FoldersCreated, run.BytesCopied, run.Conflicts, run.Errors,
        run.Summary, run.DeviceName);
}

/// <summary>A line of the protocol of a run (<c>log --json</c>).</summary>
internal sealed record EntryJson(DateTimeOffset At, RunEntryLevel Level, PlanAction? Action, string Path, long Bytes, string Message)
{
    public static EntryJson Of(RunEntry entry) => new(entry.At, entry.Level, entry.Action, entry.Path, entry.Bytes, entry.Message);
}

/// <summary>A run with its lines (<c>log --json</c>).</summary>
internal sealed record RunLogJson(RunJson Run, IReadOnlyList<EntryJson> Entries);

/// <summary>A connection (<c>connections --json</c>). Never a secret – only whether this computer keeps one.</summary>
internal sealed record ConnectionJson(
    Guid Id,
    string Name,
    ConnectionKind Kind,
    string Type,
    ConnectionGroup Group,
    string Address,
    string DeviceName,
    bool HasSecret,
    bool UsableHere,
    string Reason)
{
    public static ConnectionJson Of(Connection connection)
    {
        var usable = ConnectionService.IsUsableHere(connection, out var reason);
        return new ConnectionJson(connection.Id, connection.Name, connection.Kind, ConnectionCatalog.Get(connection.Kind).Name,
            ConnectionCatalog.Get(connection.Kind).Group, ConnectionService.Address(connection), connection.DeviceName, connection.HasSecret,
            usable, reason);
    }
}

/// <summary>The result of <c>test --json</c>.</summary>
internal sealed record TestJson(Guid Id, string Name, bool Success, string Message, int ExitCode);

/// <summary>The progress of a running job (<c>status --json</c>).</summary>
internal sealed record ProgressJson(
    Guid JobId,
    string? JobName,
    Guid RunId,
    RunPhase Phase,
    double Fraction,
    long FilesDone,
    long FilesTotal,
    long BytesDone,
    long BytesTotal,
    double BytesPerSecond,
    double FilesPerSecond,
    long Errors,
    double ElapsedSeconds,
    double? RemainingSeconds,
    string Text)
{
    public static ProgressJson Of(RunProgress progress) => new(
        progress.JobId, JobService.Find(progress.JobId)?.Name, progress.RunId, progress.Phase, Math.Round(progress.Fraction, 4),
        progress.FilesDone, progress.FilesTotal, progress.BytesDone, progress.BytesTotal, Math.Round(progress.BytesPerSecond),
        Math.Round(progress.FilesPerSecond, 1), progress.Errors, Math.Round(progress.Elapsed.TotalSeconds, 1),
        progress.Remaining is { } remaining ? Math.Round(remaining.TotalSeconds, 1) : null, progress.Text);
}

/// <summary>A step of a plan (the preview).</summary>
internal sealed record PlanItemJson(int PairIndex, PlanAction Action, string Path, string? FromPath, bool IsFolder, long Bytes, string Reason)
{
    public static PlanItemJson Of(PlanItem item) => new(item.PairIndex, item.Action, item.Path, item.FromPath, item.IsFolder, item.Bytes, item.Reason);
}

/// <summary>The plan of a preview: totals, the number of steps per action and the steps.</summary>
internal sealed record PlanJson(
    long SourceFiles,
    long TargetFiles,
    long SourceBytes,
    long TargetBytes,
    long Skipped,
    long BytesToCopy,
    int OpenConflicts,
    IReadOnlyDictionary<PlanAction, int> Counts,
    IReadOnlyList<PlanItemJson> Items)
{
    public static PlanJson Of(SyncPlan plan) => new(
        plan.SourceFiles, plan.TargetFiles, plan.SourceBytes, plan.TargetBytes, plan.Skipped, plan.BytesToCopy, plan.OpenConflicts,
        plan.Items.GroupBy(item => item.Action).OrderBy(group => group.Key).ToDictionary(group => group.Key, group => group.Count()),
        [.. plan.Items.Where(item => item.Action != PlanAction.InStep).Select(PlanItemJson.Of)]);
}

/// <summary>The result of <c>run --json</c>: the run, the exit code, and with <c>--preview</c> the plan.</summary>
internal sealed record RunResultJson(RunJson Run, int ExitCode, PlanJson? Plan);

/// <summary>The report of <c>import --json</c>.</summary>
internal sealed record ImportJson(int Imported, int Skipped, int Failed, IReadOnlyList<ImportItem> Items);
