using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;

namespace ThixSync.Core.Business;

/// <summary>A page of a longer list (pagination below the table).</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, int PageIndex, int PageSize, int TotalCount)
{
    public int PageCount => PageSize <= 0 ? 1 : Math.Max(1, (TotalCount + PageSize - 1) / PageSize);
}

/// <summary>The filter of the protocol list (toolbar).</summary>
public enum RunFilterKind
{
    All = 0,
    Success = 1,
    Problems = 2,
    Failed = 3,
    Analyses = 4,
}

/// <summary>The sort orders of the protocol list.</summary>
public enum RunSort
{
    Newest = 0,
    Oldest = 1,
    Job = 2,
    Duration = 3,
    Bytes = 4,
}

/// <summary>The numbers of the tab "Übersicht".</summary>
public sealed record Overview
{
    public int Jobs { get; init; }

    public int ScheduledJobs { get; init; }

    public int PausedJobs { get; init; }

    public int Connections { get; init; }

    public int RunsLastWeek { get; init; }

    public long FilesCopiedLastWeek { get; init; }

    public long BytesCopiedLastWeek { get; init; }

    public int ProblemJobs { get; init; }

    /// <summary>Bytes copied per day, the last 14 days, oldest first (the bar chart).</summary>
    public IReadOnlyList<(DateOnly Day, long Bytes)> BytesPerDay { get; init; } = [];

    /// <summary>The newest runs (the list below).</summary>
    public IReadOnlyList<SyncRun> RecentRuns { get; init; } = [];
}

/// <summary>
/// The protocol (page "Protokolle", tab "Verlauf" of a job, tab "Übersicht"): runs and their lines, searched, filtered, sorted and
/// in pages. Old runs go after <see cref="AppSettings.KeepProtocolDays"/>.
/// </summary>
public static class ProtocolService
{
    public const int DefaultPageSize = 50;

    /// <summary>Raised when a run was written (any thread).</summary>
    public static event EventHandler<SyncRun>? Changed;

    public static Page<SyncRun> Query(string? search, RunFilterKind filter, RunSort sort, Guid? jobId, int pageIndex, int pageSize = DefaultPageSize)
    {
        // the protocol is small enough (runs, not lines) to filter in memory; the lines are paged in the database
        IEnumerable<SyncRun> runs = SyncStore.LoadRuns(jobId, 100_000);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            runs = runs.Where(run => run.JobName.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                || run.Summary.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                || run.DeviceName.Contains(text, StringComparison.CurrentCultureIgnoreCase));
        }

        runs = filter switch
        {
            RunFilterKind.Success => runs.Where(run => run.Outcome == RunOutcome.Success),
            RunFilterKind.Problems => runs.Where(run => run.Outcome is RunOutcome.Warnings or RunOutcome.Failed or RunOutcome.NeedsConfirmation),
            RunFilterKind.Failed => runs.Where(run => run.Outcome == RunOutcome.Failed),
            RunFilterKind.Analyses => runs.Where(run => run.Outcome == RunOutcome.Analysed),
            _ => runs,
        };

        runs = sort switch
        {
            RunSort.Oldest => runs.OrderBy(run => run.StartedAt),
            RunSort.Job => runs.OrderBy(run => run.JobName, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(run => run.StartedAt),
            RunSort.Duration => runs.OrderByDescending(run => run.Duration),
            RunSort.Bytes => runs.OrderByDescending(run => run.BytesCopied),
            _ => runs.OrderByDescending(run => run.StartedAt),
        };

        var all = runs.ToList();
        pageSize = Math.Clamp(pageSize, 1, 1000);
        pageIndex = Math.Clamp(pageIndex, 0, Math.Max(0, (all.Count - 1) / pageSize));
        return new Page<SyncRun>([.. all.Skip(pageIndex * pageSize).Take(pageSize)], pageIndex, pageSize, all.Count);
    }

    public static SyncRun? Find(Guid runId) => SyncStore.FindRun(runId);

    /// <summary>The lines of a run; <paramref name="onlyProblems"/> keeps warnings and errors; <paramref name="search"/> matches path and message.</summary>
    public static Page<RunEntry> Entries(Guid runId, string? search, bool onlyProblems, int pageIndex, int pageSize = 200)
    {
        IEnumerable<RunEntry> entries = SyncStore.LoadEntries(runId, SyncStore.MaxEntriesPerRun * 2);
        if (onlyProblems)
        {
            entries = entries.Where(entry => entry.Level != RunEntryLevel.Info);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            entries = entries.Where(entry => entry.Path.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                || entry.Message.Contains(text, StringComparison.CurrentCultureIgnoreCase));
        }

        var all = entries.ToList();
        pageSize = Math.Clamp(pageSize, 1, 2000);
        pageIndex = Math.Clamp(pageIndex, 0, Math.Max(0, (all.Count - 1) / pageSize));
        return new Page<RunEntry>([.. all.Skip(pageIndex * pageSize).Take(pageSize)], pageIndex, pageSize, all.Count);
    }

    /// <summary>The newest finished run of every job (the cards).</summary>
    public static IReadOnlyDictionary<Guid, SyncRun> LatestRuns() => SyncStore.LatestRunPerJob();

    public static Overview GetOverview()
    {
        var now = AppClock.UtcNow;
        var jobs = JobService.List();
        var latest = SyncStore.LatestRunPerJob();
        var recent = SyncStore.LoadRuns(null, 5000).Where(run => run.StartedAt >= now.AddDays(-14)).ToList();
        var week = recent.Where(run => run.StartedAt >= now.AddDays(-7) && run.Outcome != RunOutcome.Analysed).ToList();
        var today = DateOnly.FromDateTime(now.ToLocalTime().Date);
        var perDay = Enumerable.Range(0, 14)
            .Select(offset => today.AddDays(offset - 13))
            .Select(day => (day, recent.Where(run => DateOnly.FromDateTime(run.StartedAt.ToLocalTime().Date) == day).Sum(run => run.BytesCopied)))
            .ToList();

        return new Overview
        {
            Jobs = jobs.Count,
            ScheduledJobs = jobs.Count(job => !job.IsPaused && (job.Schedule.Kind != ScheduleKind.Manual || job.Schedule.RunOnChange)),
            PausedJobs = jobs.Count(job => job.IsPaused),
            Connections = ConnectionService.List().Count,
            RunsLastWeek = week.Count,
            FilesCopiedLastWeek = week.Sum(run => run.FilesCopied),
            BytesCopiedLastWeek = week.Sum(run => run.BytesCopied),
            ProblemJobs = jobs.Count(job => latest.TryGetValue(job.Id, out var run) && run.Outcome is RunOutcome.Failed or RunOutcome.Warnings or RunOutcome.NeedsConfirmation),
            BytesPerDay = perDay,
            RecentRuns = [.. recent.Take(10)],
        };
    }

    /// <summary>Removes runs older than the setting (called once a day and at start).</summary>
    public static int Cleanup()
    {
        var days = SettingsService.Get().KeepProtocolDays;
        return days <= 0 ? 0 : SyncStore.DeleteRunsBefore(AppClock.UtcNow.AddDays(-days));
    }

    /// <summary>The runner tells about a written run.</summary>
    internal static void Notify(SyncRun run) => Changed?.Invoke(null, run);
}
