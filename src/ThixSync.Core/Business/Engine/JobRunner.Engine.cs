using ThixSync.Core.Business.Engine;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;

namespace ThixSync.Core.Business;

/// <summary>The engine behind the contract of <see cref="JobRunner"/> (Business/JobRunner.cs).</summary>
public static partial class JobRunner
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<Guid, RunSlot> Slots = [];
    private static readonly Dictionary<Guid, KeptPlan> Plans = [];
    private static int _subscribed;
    private static int _initialized;

    public static partial bool IsRunning(Guid jobId)
    {
        lock (Gate)
        {
            return Slots.ContainsKey(jobId);
        }
    }

    public static partial RunProgress? ProgressOf(Guid jobId)
    {
        lock (Gate)
        {
            return Slots.TryGetValue(jobId, out var slot) ? slot.Progress : null;
        }
    }

    public static partial IReadOnlyList<RunProgress> Running()
    {
        lock (Gate)
        {
            return [.. Slots.Values.Select(slot => slot.Progress).OfType<RunProgress>()];
        }
    }

    public static async partial Task<SyncRun> RunAsync(Guid jobId, RunRequest request, CancellationToken cancellationToken)
    {
        var slot = Begin(jobId, request);
        using var registration = cancellationToken.Register(slot.Cancel);
        return await ExecuteAsync(slot);
    }

    public static partial bool Start(Guid jobId, RunRequest request)
    {
        RunSlot slot;
        try
        {
            slot = Begin(jobId, request);
        }
        catch (BusinessException)
        {
            return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteAsync(slot);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Log.Error($"A background run of job {jobId:D} failed", exception);
            }
        });
        return true;
    }

    public static partial void Cancel(Guid jobId)
    {
        RunSlot? slot;
        lock (Gate)
        {
            Slots.TryGetValue(jobId, out slot);
        }

        slot?.Cancel();
    }

    public static partial SyncPlan? LastPlan(Guid jobId)
    {
        EnsureSubscribed();
        lock (Gate)
        {
            return Plans.TryGetValue(jobId, out var kept) ? kept.Plan : null;
        }
    }

    public static partial void Resolve(Guid jobId, int pairIndex, string path, ConflictChoice choice)
    {
        if (!Enum.IsDefined(choice))
        {
            throw new BusinessException("Diese Auswahl gibt es nicht.");
        }

        lock (Gate)
        {
            if (!Plans.TryGetValue(jobId, out var kept))
            {
                throw new BusinessException("Es gibt keine aktuelle Analyse dieses Auftrags.");
            }

            var items = kept.Plan.Items.ToList();
            var index = items.FindIndex(item => item.Action == PlanAction.Conflict && item.PairIndex == pairIndex
                && string.Equals(item.Path, path, StringComparison.Ordinal));
            if (index < 0)
            {
                index = items.FindIndex(item => item.Action == PlanAction.Conflict && item.PairIndex == pairIndex
                    && string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
            }

            if (index < 0)
            {
                throw new BusinessException("Diesen Konflikt gibt es in der Analyse nicht (mehr).");
            }

            var item = items[index];
            items[index] = item with
            {
                Resolution = choice,
                Bytes = Planner.BytesOf(choice, item.Source?.Size ?? 0, item.Target?.Size ?? 0),
            };
            Plans[jobId] = kept with { Plan = kept.Plan with { Items = items } };
        }
    }

    public static partial void ForgetState(Guid jobId)
    {
        RunSlot? slot;
        lock (Gate)
        {
            Plans.Remove(jobId);
            Slots.TryGetValue(jobId, out slot);
        }

        if (slot is not null)
        {
            // the run still holds the state: it goes when the run ended
            slot.ForgetStateAtEnd = true;
            slot.Cancel();
            return;
        }

        JobStateStore.Delete(jobId);
        JobLock.Delete(jobId);
    }

    /// <summary>Once at app start: runs that were still "running" when ThixSync ended become Cancelled.</summary>
    internal static void Initialize()
    {
        EnsureSubscribed();
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            return;
        }

        try
        {
            SyncStore.CloseInterruptedRuns(AppClock.UtcNow);
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            Log.Error("Interrupted runs could not be closed", exception);
        }
    }

    /// <summary>Keeps the plan of an analysis (or of a run that stopped for a confirmation or left conflicts open).</summary>
    internal static void KeepPlan(SyncJob job, SyncPlan plan)
    {
        EnsureSubscribed();
        lock (Gate)
        {
            Plans[job.Id] = new KeptPlan(plan, job.UpdateDate);
        }
    }

    internal static void DropPlan(Guid jobId)
    {
        lock (Gate)
        {
            Plans.Remove(jobId);
        }
    }

    /// <summary>The checks before a run (throws <see cref="BusinessException"/>) and its place among the running jobs.</summary>
    private static RunSlot Begin(Guid jobId, RunRequest request)
    {
        EnsureSubscribed();
        var job = JobService.Get(jobId);
        if (!JobService.CanRunHere(job, out var reason))
        {
            throw new BusinessException($"„{job.Name}“ kann auf diesem Computer nicht laufen: {reason}");
        }

        AccessPolicy.EnsureConnectionKindAllowed(ConnectionService.Get(job.SourceConnectionId).Kind);
        AccessPolicy.EnsureConnectionKindAllowed(ConnectionService.Get(job.TargetConnectionId).Kind);
        if (job.Pairs.Count == 0)
        {
            throw new BusinessException($"„{job.Name}“ hat kein Ordnerpaar.");
        }

        if (request.UseLastPlan && LastPlan(jobId) is null)
        {
            throw new BusinessException("Es gibt keine aktuelle Analyse dieses Auftrags – bitte zuerst analysieren.");
        }

        if (IsRunning(jobId))
        {
            throw new BusinessException($"„{job.Name}“ läuft bereits.");
        }

        var fileLock = JobLock.TryAcquire(jobId)
            ?? throw new BusinessException($"„{job.Name}“ läuft bereits (in einem anderen ThixSync-Fenster oder in der Kommandozeile).");
        var slot = new RunSlot(job, request, fileLock);
        lock (Gate)
        {
            if (Slots.TryAdd(jobId, slot))
            {
                return slot;
            }
        }

        slot.Dispose();
        throw new BusinessException($"„{job.Name}“ läuft bereits.");
    }

    private static async Task<SyncRun> ExecuteAsync(RunSlot slot)
    {
        try
        {
            slot.Meter.IsWaiting = true;
            slot.Progress = slot.Meter.Snapshot();
            OnProgress(slot.Progress);
            try
            {
                await JobQueue.EnterAsync(slot.Cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return CancelledWhileWaiting(slot);
            }

            slot.Meter.IsWaiting = false;
            try
            {
                return await new SyncSession(slot).RunAsync();
            }
            finally
            {
                JobQueue.Exit();
            }
        }
        finally
        {
            lock (Gate)
            {
                Slots.Remove(slot.Job.Id);
            }

            var forget = slot.ForgetStateAtEnd;
            slot.Dispose();
            if (forget)
            {
                JobStateStore.Delete(slot.Job.Id);
                JobLock.Delete(slot.Job.Id);
            }
        }
    }

    private static SyncRun CancelledWhileWaiting(RunSlot slot)
    {
        var now = AppClock.UtcNow;
        var run = new SyncRun
        {
            Id = slot.RunId,
            JobId = slot.Job.Id,
            JobName = slot.Job.Name,
            Trigger = slot.Request.Trigger,
            Outcome = RunOutcome.Cancelled,
            StartedAt = now,
            FinishedAt = now,
            Summary = "Abgebrochen, bevor der Auftrag an der Reihe war",
            DeviceName = AppInfo.MachineName,
        };
        SyncStore.SaveRun(run);
        ProtocolService.Notify(run);
        OnRunFinished(run);
        return run;
    }

    /// <summary>A kept plan belongs to the job as it was: a changed (or deleted) job drops it.</summary>
    private static void EnsureSubscribed()
    {
        if (Interlocked.Exchange(ref _subscribed, 1) == 1)
        {
            return;
        }

        JobService.Changed += (_, _) => DropChangedPlans();
        SettingsService.Changed += (_, _) => JobQueue.Wake();
    }

    private static void DropChangedPlans()
    {
        List<(Guid JobId, DateTimeOffset UpdateDate)> kept;
        lock (Gate)
        {
            kept = [.. Plans.Select(plan => (plan.Key, plan.Value.JobUpdateDate))];
        }

        foreach (var (jobId, updateDate) in kept)
        {
            if (JobService.Find(jobId)?.UpdateDate == updateDate)
            {
                continue;
            }

            lock (Gate)
            {
                if (Plans.TryGetValue(jobId, out var plan) && plan.JobUpdateDate == updateDate)
                {
                    Plans.Remove(jobId);
                }
            }
        }
    }

    private sealed record KeptPlan(SyncPlan Plan, DateTimeOffset JobUpdateDate);
}
