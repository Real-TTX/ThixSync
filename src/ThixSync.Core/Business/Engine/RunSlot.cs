using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;

namespace ThixSync.Core.Business.Engine;

/// <summary>A job that runs or waits in line: its request, cancellation, meter and latest progress.</summary>
internal sealed class RunSlot : IDisposable
{
    private readonly IDisposable _fileLock;
    private RunProgress? _progress;
    private int _forget;

    public RunSlot(SyncJob job, RunRequest request, IDisposable fileLock)
    {
        _fileLock = fileLock;
        Job = job;
        Request = request;
        Meter = new TransferMeter(job.Id, RunId);
        Progress = Meter.Snapshot();
    }

    public SyncJob Job { get; }

    public RunRequest Request { get; }

    public Guid RunId { get; } = Guid.NewGuid();

    public CancellationTokenSource Cancellation { get; } = new();

    public TransferMeter Meter { get; }

    public RunProgress? Progress
    {
        get => Volatile.Read(ref _progress);
        set => Volatile.Write(ref _progress, value);
    }

    /// <summary>The state is to be deleted once the run ended (the job was deleted while it ran).</summary>
    public bool ForgetStateAtEnd
    {
        get => Volatile.Read(ref _forget) != 0;
        set => Volatile.Write(ref _forget, value ? 1 : 0);
    }

    public void Cancel()
    {
        try
        {
            Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        _fileLock.Dispose();
        Cancellation.Dispose();
    }
}

/// <summary>
/// The line of jobs: at most <see cref="AppSettings.MaxConcurrentJobs"/> run at the same time, the others wait in the order they came.
/// </summary>
internal static class JobQueue
{
    private static readonly Lock Gate = new();
    private static readonly LinkedList<TaskCompletionSource> Waiting = new();
    private static int _running;

    /// <summary>Tests: the number of jobs at once instead of the setting.</summary>
    internal static Func<int>? LimitForTests { get; set; }

    private static int Limit => Math.Max(1, LimitForTests?.Invoke() ?? SettingsService.Get().MaxConcurrentJobs);

    public static async Task EnterAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource waiter;
        LinkedListNode<TaskCompletionSource> node;
        lock (Gate)
        {
            if (_running < Limit)
            {
                _running++;
                return;
            }

            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            node = Waiting.AddLast(waiter);
        }

        using var registration = cancellationToken.Register(() =>
        {
            lock (Gate)
            {
                if (node.List is not null)
                {
                    Waiting.Remove(node);
                }
            }

            waiter.TrySetCanceled(cancellationToken);
        });
        await waiter.Task;
    }

    public static void Exit()
    {
        lock (Gate)
        {
            _running--;
            WakeLocked();
        }
    }

    /// <summary>The limit may have grown (settings): let waiting jobs in.</summary>
    public static void Wake()
    {
        lock (Gate)
        {
            WakeLocked();
        }
    }

    private static void WakeLocked()
    {
        while (_running < Limit && Waiting.First is { } first)
        {
            Waiting.RemoveFirst();
            if (first.Value.TrySetResult())
            {
                _running++;
            }
        }
    }
}

/// <summary>
/// The lock of a running job across processes (the app and the CLI may both run it): a file in the state folder that one process
/// at a time holds open.
/// </summary>
internal static class JobLock
{
    public static string FileOf(Guid jobId) => Path.Combine(AppPaths.StateDirectory, $"{jobId:D}.lock");

    /// <summary>The lock, or null when another process holds it.</summary>
    public static IDisposable? TryAcquire(Guid jobId)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.StateDirectory);
            return new FileStream(FileOf(jobId), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Delete(Guid jobId)
    {
        try
        {
            File.Delete(FileOf(jobId));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
