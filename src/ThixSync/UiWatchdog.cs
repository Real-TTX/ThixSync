using System.Diagnostics;
using Avalonia.Threading;
using ThixSync.Core.Infrastructure;

namespace ThixSync;

/// <summary>
/// Notices when the UI thread stops answering ("reagiert nicht"): a background thread knocks every second; after
/// <see cref="HangThreshold"/> without an answer it writes a hang report (Windows: with a memory dump of that moment,
/// <see cref="ProblemReports.WriteHang"/>), and when the UI comes back it adds how long it was gone. Sleep and hibernation
/// do not count (the watchdog itself was asleep then).
/// </summary>
internal static class UiWatchdog
{
    /// <summary>Windows marks a window as not responding after 5 s; a little later the dump still catches the hang.</summary>
    public static readonly TimeSpan HangThreshold = TimeSpan.FromSeconds(8);

    private static readonly TimeSpan KnockInterval = TimeSpan.FromSeconds(1);
    private static long _lastAnswerTicks = Stopwatch.GetTimestamp();
    private static Thread? _thread;
    private static volatile bool _stopped;

    /// <summary>ThixSync is ending (quit, update, uninstall): the UI stops answering on purpose, that is no hang.</summary>
    public static void Stop() => _stopped = true;

    public static void Start()
    {
        if (_thread is not null || Debugger.IsAttached)
        {
            return;
        }

        _thread = new Thread(Watch) { IsBackground = true, Name = "ThixSync UI watchdog" };
        _thread.Start();
    }

    private static void Watch()
    {
        var lastLoop = Stopwatch.GetTimestamp();
        string? openReport = null;
        var hangStarted = 0L;
        while (!_stopped)
        {
            Thread.Sleep(KnockInterval);
            if (_stopped)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();

            // The computer slept: nobody could answer, start over.
            if (Stopwatch.GetElapsedTime(lastLoop, now) > TimeSpan.FromSeconds(5))
            {
                Interlocked.Exchange(ref _lastAnswerTicks, now);
            }

            lastLoop = now;
            Dispatcher.UIThread.Post(() => Interlocked.Exchange(ref _lastAnswerTicks, Stopwatch.GetTimestamp()), DispatcherPriority.Send);

            var silentFor = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastAnswerTicks), now);
            if (silentFor >= HangThreshold && hangStarted == 0)
            {
                hangStarted = Interlocked.Read(ref _lastAnswerTicks);
                Log.Warning($"The UI thread has not answered for {silentFor.TotalSeconds:0} s");
                openReport = ProblemReports.WriteHang(silentFor);
            }
            else if (silentFor < KnockInterval * 2 && hangStarted != 0)
            {
                var lasted = Stopwatch.GetElapsedTime(hangStarted, now);
                Log.Info($"The UI thread answers again after {lasted.TotalSeconds:0} s");
                if (openReport is not null)
                {
                    ProblemReports.Append(openReport, $"{Environment.NewLine}Danach reagierte ThixSync wieder (nach insgesamt {lasted.TotalSeconds:0} Sekunden).");
                }

                hangStarted = 0;
                openReport = null;
            }
        }
    }
}
