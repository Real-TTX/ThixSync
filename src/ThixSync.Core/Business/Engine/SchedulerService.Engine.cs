using ThixSync.Core.Business.Engine;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;

namespace ThixSync.Core.Business;

/// <summary>
/// The engine behind the contract of <see cref="SchedulerService"/> (Business/SchedulerService.cs): a background loop that looks
/// every 30 seconds (and at once when a job changed) which jobs of this computer are due – by schedule, at start, after changes of
/// their local folders (watched) or of their remote sides (checked every few minutes, with the storage's change feed when it has
/// one) – and starts them through <see cref="JobRunner"/>.
/// </summary>
public static partial class SchedulerService
{
    private static readonly TimeSpan LoopInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BusyInterval = TimeSpan.FromSeconds(1);
    private static readonly Lock Gate = new();
    private static readonly Dictionary<Guid, DateTimeOffset> LastChecked = [];
    private static readonly Dictionary<Guid, DateTimeOffset> LastPolled = [];
    private static readonly HashSet<Guid> StartedAtStartup = [];
    private static readonly HashSet<Guid> Polling = [];
    private static SemaphoreSlim? _wake;
    private static CancellationTokenSource? _stop;
    private static Task? _loop;
    private static ChangeWatchers? _watchers;
    private static DateTimeOffset _startedAt;
    private static DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;
    private static volatile bool _pausedAll;

    public static partial void Start()
    {
        lock (Gate)
        {
            if (_loop is not null)
            {
                return;
            }

            JobRunner.Initialize();
            _startedAt = AppClock.UtcNow;
            LastChecked.Clear();
            LastPolled.Clear();
            StartedAtStartup.Clear();
            _wake = new SemaphoreSlim(0, 1);
            _stop = new CancellationTokenSource();
            _watchers = new ChangeWatchers();
            JobService.Changed += OnJobsChanged;
            var stop = _stop.Token;
            _loop = Task.Run(() => LoopAsync(stop));
        }
    }

    public static partial void Stop()
    {
        Task? loop;
        lock (Gate)
        {
            if (_loop is null)
            {
                return;
            }

            JobService.Changed -= OnJobsChanged;
            _stop!.Cancel();
            loop = _loop;
            _loop = null;
        }

        try
        {
            loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        lock (Gate)
        {
            _watchers?.Dispose();
            _watchers = null;
            _stop?.Dispose();
            _stop = null;
        }
    }

    public static partial bool IsRunning
    {
        get
        {
            lock (Gate)
            {
                return _loop is not null;
            }
        }
    }

    public static partial void PauseAll() => _pausedAll = true;

    public static partial void ResumeAll()
    {
        _pausedAll = false;
        Wake();
    }

    public static partial bool IsPausedAll => _pausedAll;

    public static partial DateTimeOffset? NextRunOf(SyncJob job)
    {
        if (job.IsPaused || !JobService.RunsHere(job))
        {
            return null;
        }

        var now = AppClock.UtcNow;
        if (job.Schedule.Kind != ScheduleKind.Interval)
        {
            return JobService.NextRun(job, now);
        }

        var last = SyncStore.LatestRunPerJob().TryGetValue(job.Id, out var run) ? run.StartedAt : (DateTimeOffset?)null;
        var next = (last ?? job.UpdateDate).AddMinutes(Math.Max(1, job.Schedule.IntervalMinutes));
        return next < now ? now : next;
    }

    /// <summary>
    /// Whether the schedule of a job wants a run now. Interval: a full interval since the last run started (or since the job was
    /// set up; without making up missed runs at least one interval after the app started). Daily/Weekly: a scheduled time lies after
    /// <paramref name="since"/> (the last look, or at start the last run when missed runs are made up) and not after <paramref name="now"/>.
    /// </summary>
    internal static bool IsDue(SyncJob job, DateTimeOffset since, DateTimeOffset now, DateTimeOffset? lastRunStart, DateTimeOffset appStart)
    {
        if (job.IsPaused)
        {
            return false;
        }

        switch (job.Schedule.Kind)
        {
            case ScheduleKind.Interval:
                var reference = lastRunStart ?? job.UpdateDate;
                if (!job.Schedule.CatchUpMissed && appStart > reference)
                {
                    reference = appStart; // a run missed while ThixSync was not running is not made up
                }

                return now >= reference.AddMinutes(Math.Max(1, job.Schedule.IntervalMinutes));
            case ScheduleKind.Daily:
            case ScheduleKind.Weekly:
                return JobService.NextRun(job, since) is { } next && next <= now;
            default:
                return false;
        }
    }

    /// <summary>Where the look for a missed Daily/Weekly time starts at app start.</summary>
    internal static DateTimeOffset FirstReference(SyncJob job, DateTimeOffset appStart, DateTimeOffset? lastRunStart)
    {
        if (!job.Schedule.CatchUpMissed)
        {
            return appStart;
        }

        var reference = lastRunStart ?? job.UpdateDate;
        return reference > appStart ? appStart : reference;
    }

    /// <summary>Tests: one look at every job now (instead of waiting for the loop).</summary>
    internal static Task TickForTestsAsync(bool isFirst) => TickAsync(isFirst, CancellationToken.None);

    private static void OnJobsChanged(object? sender, EventArgs e) => Wake();

    private static void Wake()
    {
        try
        {
            _wake?.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task LoopAsync(CancellationToken cancellationToken)
    {
        var isFirst = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(isFirst, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Log.Error("The scheduler failed to look at the jobs", exception);
            }

            isFirst = false;
            var wait = _watchers?.HasPending == true ? BusyInterval : LoopInterval;
            try
            {
                await (_wake?.WaitAsync(wait, cancellationToken) ?? Task.Delay(wait, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    private static async Task TickAsync(bool isFirst, CancellationToken cancellationToken)
    {
        var now = AppClock.UtcNow;
        if (now - _lastCleanup >= TimeSpan.FromDays(1))
        {
            _lastCleanup = now;
            ProtocolService.Cleanup();
        }

        var jobs = JobService.List().Where(job => JobService.RunsHere(job) && !job.IsPaused).ToList();
        _watchers?.Update(jobs.Where(job => job.Schedule.RunOnChange));
        if (_pausedAll || (SettingsService.Get().PauseOnBattery && PowerStatus.IsOnBattery()))
        {
            return;
        }

        var latest = SyncStore.LatestRunPerJob();
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (JobRunner.IsRunning(job.Id) || !JobService.CanRunHere(job, out _))
            {
                continue;
            }

            var lastRunStart = latest.TryGetValue(job.Id, out var run) ? run.StartedAt : (DateTimeOffset?)null;
            var trigger = Decide(job, now, isFirst, lastRunStart);
            if (trigger is { } chosen)
            {
                JobRunner.Start(job.Id, new RunRequest { Trigger = chosen });
                continue;
            }

            if (job.Schedule.RunOnChange)
            {
                await PollRemoteSidesAsync(job, now, lastRunStart, cancellationToken);
            }
        }
    }

    private static RunTrigger? Decide(SyncJob job, DateTimeOffset now, bool isFirst, DateTimeOffset? lastRunStart)
    {
        var startup = false;
        DateTimeOffset since;
        lock (Gate)
        {
            if (job.Schedule.RunAtStartup && StartedAtStartup.Add(job.Id))
            {
                startup = true;
            }

            since = LastChecked.TryGetValue(job.Id, out var checkedAt) ? checkedAt : FirstReference(job, _startedAt, lastRunStart);
        }

        var due = IsDue(job, since, now, lastRunStart, _startedAt);
        lock (Gate)
        {
            LastChecked[job.Id] = now;
        }

        if (startup)
        {
            return RunTrigger.Startup;
        }

        if (due)
        {
            return RunTrigger.Schedule;
        }

        if (job.Schedule.RunOnChange && _watchers?.TakeDue(job.Id, TimeSpan.FromSeconds(Math.Max(1, job.Schedule.ChangeDelaySeconds))) == true)
        {
            return RunTrigger.Change;
        }

        return null;
    }

    /// <summary>
    /// Every <see cref="JobSchedule.PollMinutes"/> the sides of a job that are not on this computer are asked: through the change
    /// feed when the storage has one (and a cursor is kept), otherwise a normal run starts when the last one is older than that.
    /// The look happens in the background so a slow server never holds up the other jobs.
    /// </summary>
    private static Task PollRemoteSidesAsync(SyncJob job, DateTimeOffset now, DateTimeOffset? lastRunStart, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, job.Schedule.PollMinutes));
        lock (Gate)
        {
            var last = LastPolled.TryGetValue(job.Id, out var polled) ? polled : _startedAt;
            if (now - last < interval || !Polling.Add(job.Id))
            {
                return Task.CompletedTask;
            }

            LastPolled[job.Id] = now;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (await HasRemoteChangesAsync(job, now, lastRunStart, interval, cancellationToken))
                {
                    JobRunner.Start(job.Id, new RunRequest { Trigger = RunTrigger.Change });
                }
            }
            catch (Exception exception) when (exception is StorageException or BusinessException or IOException or HttpRequestException
                                               or Microsoft.Data.Sqlite.SqliteException or OperationCanceledException)
            {
                Log.Warning($"Remote sides of job {job.Id:D} could not be checked ({EngineText.ForLog(exception)})");
            }
            finally
            {
                lock (Gate)
                {
                    Polling.Remove(job.Id);
                }
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    private static async Task<bool> HasRemoteChangesAsync(SyncJob job, DateTimeOffset now, DateTimeOffset? lastRunStart, TimeSpan interval,
        CancellationToken cancellationToken)
    {
        var source = ConnectionService.Get(job.SourceConnectionId);
        var target = ConnectionService.Get(job.TargetConnectionId);
        var cursors = JobStateStore.Exists(job.Id) ? ReadCursors(job.Id) : [];
        var hasRemote = false;
        var needsRun = false;
        for (var index = 0; index < job.Pairs.Count && !needsRun; index++)
        {
            var pair = job.Pairs[index];
            foreach (var (connection, folder, side) in new[] { (source, pair.SourcePath, Side.Source), (target, pair.TargetPath, Side.Target) })
            {
                var storage = ConnectionService.Open(connection, folder);
                await using (storage)
                {
                    if (storage is ILocalStorage)
                    {
                        continue; // watched
                    }

                    hasRemote = true;
                    var cursor = cursors.GetValueOrDefault(index) is { } state ? side == Side.Source ? state.SourceCursor : state.TargetCursor : null;
                    if (storage is not IChangeFeed feed || cursor is null)
                    {
                        needsRun = lastRunStart is null || now - lastRunStart.Value >= interval;
                        break;
                    }

                    await storage.ConnectAsync(false, cancellationToken);
                    if (await feed.HasChangesAsync(cursor, cancellationToken))
                    {
                        needsRun = true;
                        break;
                    }
                }
            }
        }

        return hasRemote && needsRun;
    }

    private static Dictionary<int, PairState> ReadCursors(Guid jobId)
    {
        using var store = JobStateStore.Open(jobId);
        return store.LoadPairs();
    }
}
