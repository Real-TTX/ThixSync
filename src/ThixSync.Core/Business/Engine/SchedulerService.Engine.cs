using ThixSync.Core.Models;

namespace ThixSync.Core.Business;

// PLACEHOLDER – replaced by the engine implementation.
public static partial class SchedulerService
{
    public static partial void Start()
    {
    }

    public static partial void Stop()
    {
    }

    public static partial bool IsRunning => false;

    public static partial void PauseAll()
    {
    }

    public static partial void ResumeAll()
    {
    }

    public static partial bool IsPausedAll => false;

    public static partial DateTimeOffset? NextRunOf(SyncJob job) => JobService.NextRun(job, Infrastructure.AppClock.UtcNow);
}
