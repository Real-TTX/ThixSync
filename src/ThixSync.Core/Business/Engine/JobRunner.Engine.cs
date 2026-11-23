using ThixSync.Core.Models;

namespace ThixSync.Core.Business;

// PLACEHOLDER – replaced by the engine implementation.
public static partial class JobRunner
{
    public static partial bool IsRunning(Guid jobId) => false;

    public static partial RunProgress? ProgressOf(Guid jobId) => null;

    public static partial IReadOnlyList<RunProgress> Running() => [];

    public static partial Task<SyncRun> RunAsync(Guid jobId, RunRequest request, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public static partial bool Start(Guid jobId, RunRequest request) => false;

    public static partial void Cancel(Guid jobId)
    {
    }

    public static partial SyncPlan? LastPlan(Guid jobId) => null;

    public static partial void Resolve(Guid jobId, int pairIndex, string path, ConflictChoice choice)
    {
    }

    public static partial void ForgetState(Guid jobId)
    {
    }
}
