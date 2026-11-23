using System.Collections.Concurrent;
using System.Diagnostics;
using ThixSync.Core.Models;

namespace ThixSync.Core.Business.Engine;

/// <summary>
/// The live numbers of one run: lock-free counters the workers bump (bytes as they flow, files as they finish), the transfers in
/// flight, and – sampled a few times a second by <see cref="Tick"/> – the speed (bytes and files per second over the last five
/// seconds), the speed of every second of the last minute and the time still needed. <see cref="Snapshot"/> makes the immutable
/// <see cref="RunProgress"/> the UI, the tray and the CLI show.
/// </summary>
internal sealed class TransferMeter(Guid jobId, Guid runId)
{
    private const double WindowSeconds = 5;
    private const int HistoryLength = 60;
    private const int SmallSamples = 8;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<long, ActiveSlot> _active = new();
    private readonly Lock _speedGate = new();
    private readonly Queue<(double Seconds, long Bytes, long Files)> _window = new();
    private readonly Queue<double> _history = new();

    private long _scannedSource;
    private long _scannedTarget;
    private long _filesTotal;
    private long _filesDone;
    private long _bytesTotal;
    private long _bytesDone;
    private long _errors;
    private long _nextSlot;
    private int _workers;
    private int _peakWorkers;
    private int _phase;
    private int _waiting;
    private double _bytesPerSecond;
    private double _filesPerSecond;
    private double _fraction;
    private double _secondMark = -1;
    private long _secondBytes;

    public Guid JobId { get; } = jobId;

    public Guid RunId { get; } = runId;

    public RunPhase Phase
    {
        get => (RunPhase)Volatile.Read(ref _phase);
        set => Volatile.Write(ref _phase, (int)value);
    }

    /// <summary>The run waits in line for a free place (<see cref="AppSettings.MaxConcurrentJobs"/>).</summary>
    public bool IsWaiting
    {
        get => Volatile.Read(ref _waiting) != 0;
        set => Volatile.Write(ref _waiting, value ? 1 : 0);
    }

    /// <summary>The most operations that ran at the same time.</summary>
    public int PeakWorkers => Volatile.Read(ref _peakWorkers);

    public long FilesDone => Interlocked.Read(ref _filesDone);

    public long BytesDone => Interlocked.Read(ref _bytesDone);

    public TimeSpan Elapsed => _clock.Elapsed;

    public void Scanned(Side side)
    {
        if (side == Side.Source)
        {
            Interlocked.Increment(ref _scannedSource);
        }
        else
        {
            Interlocked.Increment(ref _scannedTarget);
        }
    }

    public void SetTotals(long files, long bytes)
    {
        Interlocked.Exchange(ref _filesTotal, files);
        Interlocked.Exchange(ref _bytesTotal, bytes);
    }

    public void AddBytes(long bytes) => Interlocked.Add(ref _bytesDone, bytes);

    public void FileDone() => Interlocked.Increment(ref _filesDone);

    public void Error() => Interlocked.Increment(ref _errors);

    public void WorkerStarted()
    {
        var now = Interlocked.Increment(ref _workers);
        var peak = Volatile.Read(ref _peakWorkers);
        while (now > peak && Interlocked.CompareExchange(ref _peakWorkers, now, peak) != peak)
        {
            peak = Volatile.Read(ref _peakWorkers);
        }
    }

    public void WorkerEnded() => Interlocked.Decrement(ref _workers);

    /// <summary>A transfer starts (shown in the list of active transfers until <see cref="End"/>).</summary>
    public ActiveSlot Begin(string path, long bytes, PlanAction action, bool isLarge)
    {
        var slot = new ActiveSlot(this, Interlocked.Increment(ref _nextSlot), path, bytes, action, isLarge);
        _active[slot.Id] = slot;
        return slot;
    }

    /// <summary>A transfer ended; when it failed, its bytes are taken back (they will flow again on a retry).</summary>
    public void End(ActiveSlot slot, bool failed)
    {
        _active.TryRemove(slot.Id, out _);
        if (failed)
        {
            Interlocked.Add(ref _bytesDone, -slot.Done);
        }
    }

    /// <summary>Takes a sample (a few times a second): the speed over the last seconds and the speed of each whole second.</summary>
    public void Tick()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var bytes = BytesDone;
        var files = FilesDone;
        lock (_speedGate)
        {
            _window.Enqueue((now, bytes, files));
            while (_window.Count > 2 && now - _window.Peek().Seconds > WindowSeconds)
            {
                _window.Dequeue();
            }

            var first = _window.Peek();
            var span = now - first.Seconds;
            if (span >= 0.2)
            {
                _bytesPerSecond = Math.Max(0, (bytes - first.Bytes) / span);
                _filesPerSecond = Math.Max(0, (files - first.Files) / span);
            }

            if (Phase != RunPhase.Transferring)
            {
                return;
            }

            if (_secondMark < 0)
            {
                _secondMark = now;
                _secondBytes = bytes;
                return;
            }

            if (now - _secondMark >= 1)
            {
                _history.Enqueue(Math.Max(0, (bytes - _secondBytes) / (now - _secondMark)));
                while (_history.Count > HistoryLength)
                {
                    _history.Dequeue();
                }

                _secondMark = now;
                _secondBytes = bytes;
            }
        }
    }

    /// <summary>The progress as it is now (immutable).</summary>
    public RunProgress Snapshot()
    {
        var phase = Phase;
        var filesTotal = Interlocked.Read(ref _filesTotal);
        var bytesTotal = Interlocked.Read(ref _bytesTotal);
        var filesDone = Math.Min(FilesDone, filesTotal);
        var bytesDone = Math.Clamp(BytesDone, 0, bytesTotal);
        double bytesPerSecond;
        double filesPerSecond;
        double fraction;
        IReadOnlyList<double> history;
        lock (_speedGate)
        {
            bytesPerSecond = _bytesPerSecond;
            filesPerSecond = _filesPerSecond;
            history = [.. _history];
            fraction = phase < RunPhase.Transferring ? 0 : FractionOf(filesDone, filesTotal, bytesDone, bytesTotal);
            fraction = _fraction = Math.Max(_fraction, fraction);
        }

        var active = _active.Values.ToList();
        var shown = active.Where(slot => slot.IsLarge)
            .Concat(active.Where(slot => !slot.IsLarge).Take(SmallSamples))
            .OrderBy(slot => slot.Id)
            .Select(slot => new ActiveTransfer(slot.Path, slot.Bytes, Math.Min(slot.Done, slot.Bytes), slot.Action))
            .ToList();

        return new RunProgress
        {
            JobId = JobId,
            RunId = RunId,
            Phase = phase,
            FilesScannedSource = Interlocked.Read(ref _scannedSource),
            FilesScannedTarget = Interlocked.Read(ref _scannedTarget),
            FilesTotal = filesTotal,
            FilesDone = filesDone,
            BytesTotal = bytesTotal,
            BytesDone = bytesDone,
            Errors = Interlocked.Read(ref _errors),
            BytesPerSecond = bytesPerSecond,
            FilesPerSecond = filesPerSecond,
            SpeedHistory = history,
            Active = shown,
            Workers = Math.Max(0, Volatile.Read(ref _workers)),
            Elapsed = _clock.Elapsed,
            Remaining = phase == RunPhase.Transferring ? Remaining(filesTotal - filesDone, bytesTotal - bytesDone, filesPerSecond, bytesPerSecond) : null,
            Fraction = fraction,
            Text = TextOf(phase, filesDone, filesTotal, bytesPerSecond, filesPerSecond),
        };
    }

    private static double FractionOf(long filesDone, long filesTotal, long bytesDone, long bytesTotal)
    {
        if (filesTotal == 0 && bytesTotal == 0)
        {
            return 1;
        }

        var files = filesTotal == 0 ? 1 : (double)filesDone / filesTotal;
        if (bytesTotal == 0)
        {
            return Math.Clamp(files, 0, 1);
        }

        // bytes and files weighted alike: many small files move the bar as much as a few large ones
        return Math.Clamp((0.5 * files) + (0.5 * bytesDone / bytesTotal), 0, 1);
    }

    /// <summary>The time still needed: the slower of "remaining bytes at the byte rate" and "remaining files at the file rate".</summary>
    private static TimeSpan? Remaining(long filesLeft, long bytesLeft, double filesPerSecond, double bytesPerSecond)
    {
        if (filesLeft <= 0 && bytesLeft <= 0)
        {
            return TimeSpan.Zero;
        }

        double seconds = 0;
        if (bytesLeft > 0)
        {
            if (bytesPerSecond <= 0)
            {
                return null;
            }

            seconds = bytesLeft / bytesPerSecond;
        }

        if (filesLeft > 0)
        {
            if (filesPerSecond <= 0)
            {
                return null;
            }

            seconds = Math.Max(seconds, filesLeft / filesPerSecond);
        }

        return seconds > TimeSpan.MaxValue.TotalSeconds / 2 ? null : TimeSpan.FromSeconds(Math.Ceiling(seconds));
    }

    private string TextOf(RunPhase phase, long filesDone, long filesTotal, double bytesPerSecond, double filesPerSecond) => phase switch
    {
        RunPhase.Connecting when IsWaiting => "Wartet auf einen freien Platz …",
        RunPhase.Connecting => "Verbinde …",
        RunPhase.Scanning => $"Durchsuche Quelle und Ziel … {Format.Files(Interlocked.Read(ref _scannedSource) + Interlocked.Read(ref _scannedTarget))}",
        RunPhase.Comparing => "Vergleiche …",
        RunPhase.Transferring => $"Kopiere {Format.Count(filesDone)} von {Format.Files(filesTotal)} · {Format.Speed(bytesPerSecond)} · {Format.Rate(filesPerSecond)} Dateien/s",
        _ => "Schließe ab …",
    };

    /// <summary>A transfer in flight; <see cref="Add"/> is called by the stream as bytes flow.</summary>
    internal sealed class ActiveSlot(TransferMeter meter, long id, string path, long bytes, PlanAction action, bool isLarge)
    {
        private long _done;

        public long Id { get; } = id;

        public string Path { get; } = path;

        public long Bytes { get; } = bytes;

        public PlanAction Action { get; } = action;

        public bool IsLarge { get; } = isLarge;

        public long Done => Interlocked.Read(ref _done);

        public void Add(long bytes)
        {
            Interlocked.Add(ref _done, bytes);
            meter.AddBytes(bytes);
        }
    }
}
