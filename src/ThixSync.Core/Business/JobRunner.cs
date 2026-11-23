using ThixSync.Core.Models;

namespace ThixSync.Core.Business;

/// <summary>Choices for one run.</summary>
public sealed record RunRequest
{
    public RunTrigger Trigger { get; init; } = RunTrigger.Manual;

    /// <summary>Only analyse (preview): nothing is copied, the plan is kept (<see cref="JobRunner.LastPlan"/>).</summary>
    public bool AnalyseOnly { get; init; }

    /// <summary>The user confirmed the deletions above the safety limit.</summary>
    public bool ConfirmDeletes { get; init; }

    /// <summary>Carry out the kept plan of the last analysis (with the conflicts the user resolved) instead of analysing again.</summary>
    public bool UseLastPlan { get; init; }
}

/// <summary>
/// Runs jobs: analyse (scan both sides, compare with the state of the last run, plan) and carry out the plan with many workers
/// (small files in a fast lane, large ones in pieces), with progress, speed, protocol and state. One run per job at a time, at most
/// <see cref="AppSettings.MaxConcurrentJobs"/> jobs at once (the others wait in line). The UI, the tray, the scheduler and the CLI use
/// only this class.
///
/// CONTRACT – implemented by the engine (Business/Engine): the members below are the public surface; keep them.
/// </summary>
public static partial class JobRunner
{
    /// <summary>A run started (any thread).</summary>
    public static event EventHandler<SyncRun>? RunStarted;

    /// <summary>A run ended; the run carries the outcome and the counts (any thread).</summary>
    public static event EventHandler<SyncRun>? RunFinished;

    /// <summary>New progress of a running job, a few times a second (any thread).</summary>
    public static event EventHandler<RunProgress>? ProgressChanged;

    /// <summary>Whether a job is running or waiting in line.</summary>
    public static partial bool IsRunning(Guid jobId);

    /// <summary>The progress of a running job (null: not running).</summary>
    public static partial RunProgress? ProgressOf(Guid jobId);

    /// <summary>The progress of every running job.</summary>
    public static partial IReadOnlyList<RunProgress> Running();

    /// <summary>
    /// Runs a job and waits for the end. Throws <see cref="BusinessException"/> when the job cannot run here (connections, policy) or
    /// already runs; storage problems end the run as Failed (in the returned run and the protocol), not as an exception.
    /// </summary>
    public static partial Task<SyncRun> RunAsync(Guid jobId, RunRequest request, CancellationToken cancellationToken = default);

    /// <summary>Starts a run in the background (UI, tray, scheduler). False when it already runs or cannot run here.</summary>
    public static partial bool Start(Guid jobId, RunRequest request);

    /// <summary>Stops a running job (the files already copied stay; the state remembers them).</summary>
    public static partial void Cancel(Guid jobId);

    /// <summary>The plan of the last analysis of a job on this computer (null: none, or the job changed since).</summary>
    public static partial SyncPlan? LastPlan(Guid jobId);

    /// <summary>Resolves a conflict of the kept plan (the next <see cref="RunRequest.UseLastPlan"/> run carries it out).</summary>
    public static partial void Resolve(Guid jobId, int pairIndex, string path, ConflictChoice choice);

    /// <summary>Forgets the state of a job (it was deleted; the next run compares both sides like a first run).</summary>
    public static partial void ForgetState(Guid jobId);

    internal static void OnRunStarted(SyncRun run) => RunStarted?.Invoke(null, run);

    internal static void OnRunFinished(SyncRun run) => RunFinished?.Invoke(null, run);

    internal static void OnProgress(RunProgress progress) => ProgressChanged?.Invoke(null, progress);
}
