using ThixSync.Core.Models;

namespace ThixSync.Core.Business;

/// <summary>
/// Starts the jobs of this computer by themselves: by schedule (interval, daily, weekly, missed runs at start), at start, and on
/// changes (local folders watched, remote sides checked every few minutes). Only jobs that run here (<see cref="JobService.RunsHere"/>)
/// and are not paused.
///
/// CONTRACT – implemented by the engine (Business/Engine); keep the public members.
/// </summary>
public static partial class SchedulerService
{
    /// <summary>Starts the scheduler (app start). Safe to call twice.</summary>
    public static partial void Start();

    /// <summary>Stops the scheduler and the watchers (exit, tests).</summary>
    public static partial void Stop();

    public static partial bool IsRunning { get; }

    /// <summary>Pauses every automatic run until <see cref="ResumeAll"/> (tray: "Alle pausieren").</summary>
    public static partial void PauseAll();

    public static partial void ResumeAll();

    public static partial bool IsPausedAll { get; }

    /// <summary>When the job runs next by itself (null: not by time).</summary>
    public static partial DateTimeOffset? NextRunOf(SyncJob job);
}
