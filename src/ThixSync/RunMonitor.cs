using Avalonia.Threading;
using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;

namespace ThixSync;

/// <summary>
/// What runs right now, for every page of the window (the cards, the activity tab, the title bar, the tray): the progress of
/// <see cref="JobRunner"/> on the UI thread. The runner publishes several times a second from any thread; this keeps the newest
/// snapshot per job and tells the pages at most four times a second (<see cref="Changed"/>), and at once when a run starts or ends
/// (<see cref="RunsChanged"/>). Starting and stopping runs from the UI goes through here too: an error (also of an engine that is not
/// there yet) comes back as a message, never as a crash.
/// </summary>
internal static class RunMonitor
{
    private static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(250);
    private static readonly Dictionary<Guid, RunProgress> Progress = [];
    private static readonly HashSet<Guid> Starting = [];
    private static readonly DispatcherTimer Timer = new() { Interval = Throttle };
    private static bool _isStarted;
    private static bool _isDirty;

    /// <summary>New numbers of running jobs (UI thread, at most every 250 ms).</summary>
    public static event EventHandler? Changed;

    /// <summary>A run started or ended (UI thread): lists and menus are drawn again.</summary>
    public static event EventHandler? RunsChanged;

    /// <summary>The progress of every running job.</summary>
    public static IReadOnlyList<RunProgress> Running => [.. Progress.Values.OrderBy(progress => progress.JobId)];

    /// <summary>Whether a job runs (or was just started from here and has not reported yet).</summary>
    public static bool IsRunning(Guid jobId) => Progress.ContainsKey(jobId) || Starting.Contains(jobId) || JobRunner.IsRunning(jobId);

    public static RunProgress? ProgressOf(Guid jobId) => Progress.GetValueOrDefault(jobId);

    /// <summary>The speed of all running jobs together.</summary>
    public static double TotalBytesPerSecond => Progress.Values.Sum(progress => progress.BytesPerSecond);

    /// <summary>Listens to the runner (app start; safe to call twice).</summary>
    public static void Start()
    {
        if (_isStarted)
        {
            return;
        }

        _isStarted = true;
        JobRunner.ProgressChanged += (_, progress) => Dispatcher.UIThread.Post(() =>
        {
            Progress[progress.JobId] = progress;
            _isDirty = true;
        });
        JobRunner.RunStarted += (_, run) => Dispatcher.UIThread.Post(() =>
        {
            Starting.Remove(run.JobId);
            RunsChanged?.Invoke(null, EventArgs.Empty);
        });
        JobRunner.RunFinished += (_, run) => Dispatcher.UIThread.Post(() => Forget(run.JobId));
        Timer.Tick += (_, _) =>
        {
            if (!_isDirty)
            {
                return;
            }

            _isDirty = false;
            Changed?.Invoke(null, EventArgs.Empty);
        };
        Timer.Start();
        foreach (var progress in SafeRunning())
        {
            Progress[progress.JobId] = progress;
        }
    }

    /// <summary>
    /// Runs a job (or analyses it) in the background and returns the finished run, or the message of what went wrong (the job cannot
    /// run here, it runs already, the engine failed). Awaited on the UI thread, the result arrives there.
    /// </summary>
    public static async Task<(SyncRun? Run, string? Error)> RunAsync(Guid jobId, RunRequest request)
    {
        Start();
        if (JobService.Find(jobId) is not { } job)
        {
            return (null, "Diesen Auftrag gibt es nicht mehr.");
        }

        if (!JobService.CanRunHere(job, out var reason))
        {
            return (null, $"„{job.Name}“ kann auf diesem Computer nicht laufen. {reason}");
        }

        Starting.Add(jobId);
        RunsChanged?.Invoke(null, EventArgs.Empty);
        try
        {
            var run = await Task.Run(() => JobRunner.RunAsync(jobId, request));
            return (run, null);
        }
        catch (BusinessException exception)
        {
            return (null, exception.Message);
        }
        catch (NotImplementedException)
        {
            return (null, "Die Synchronisation ist in dieser Version noch nicht verfügbar.");
        }
        catch (OperationCanceledException)
        {
            return (null, "Abgebrochen.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or HttpRequestException)
        {
            Log.Error($"Run of job {jobId} failed", exception);
            return (null, exception.Message);
        }
        finally
        {
            Forget(jobId);
        }
    }

    /// <summary>Stops a running job ("Anhalten").</summary>
    public static void Cancel(Guid jobId)
    {
        try
        {
            JobRunner.Cancel(jobId);
        }
        catch (NotImplementedException)
        {
        }

        Forget(jobId);
    }

    /// <summary>The snapshots and the tests show a running job without an engine.</summary>
    internal static void Show(RunProgress progress)
    {
        Progress[progress.JobId] = progress;
        RunsChanged?.Invoke(null, EventArgs.Empty);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Forgets every shown progress (snapshots, tests).</summary>
    internal static void Clear()
    {
        Progress.Clear();
        Starting.Clear();
        RunsChanged?.Invoke(null, EventArgs.Empty);
    }

    private static void Forget(Guid jobId)
    {
        var removed = Progress.Remove(jobId) | Starting.Remove(jobId);
        if (removed)
        {
            RunsChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    private static IReadOnlyList<RunProgress> SafeRunning()
    {
        try
        {
            return JobRunner.Running();
        }
        catch (NotImplementedException)
        {
            return [];
        }
    }
}
