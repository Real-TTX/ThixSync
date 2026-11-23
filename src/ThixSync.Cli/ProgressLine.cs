using ThixSync.Core.Models;

namespace ThixSync.Cli;

/// <summary>
/// The progress of a run as text. In a terminal one line that rewrites itself ("\r"): phase, bar, percent, files x/y, bytes, MB/s,
/// Dateien/s, time left – what does not fit the width is left out, the least important first. Redirected (log file, pipe) a plain
/// line now and then (<see cref="PlainInterval"/>) and at every new phase.
/// </summary>
internal sealed class ProgressLine(CliHost host)
{
    public static readonly TimeSpan PlainInterval = TimeSpan.FromSeconds(5);

    private const int BarWidth = 20;

    private readonly Lock _gate = new();
    private int _lastLength;
    private RunPhase? _lastPhase;
    private DateTimeOffset _lastPlain = DateTimeOffset.MinValue;
    private bool _closed;

    /// <summary>Shows a new progress (any thread).</summary>
    public void Show(RunProgress progress)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            if (host.IsTerminal)
            {
                var line = Render(progress, host.Width() - 1);
                host.Out.Write("\r" + line.PadRight(_lastLength));
                host.Out.Flush();
                _lastLength = line.Length;
                return;
            }

            var now = host.Now();
            if (progress.Phase == _lastPhase && now - _lastPlain < PlainInterval)
            {
                return;
            }

            _lastPhase = progress.Phase;
            _lastPlain = now;
            host.Out.WriteLine($"[{CliFormat.Time(now)}] {Render(progress, int.MaxValue, withBar: false)}");
        }
    }

    /// <summary>Clears the line in a terminal; nothing is shown afterwards (the summary follows).</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            if (host.IsTerminal && _lastLength > 0)
            {
                host.Out.Write("\r" + new string(' ', _lastLength) + "\r");
                host.Out.Flush();
                _lastLength = 0;
            }
        }
    }

    /// <summary>
    /// The progress in at most <paramref name="width"/> characters, e.g.
    /// "Übertragen · [████████░░░░░░░░░░░░] 42 % · 1.204/8.311 Dateien · 3,2 GB/7,1 GB · 48,2 MB/s · 120 Dateien/s · noch 2 min 15 s".
    /// </summary>
    public static string Render(RunProgress progress, int width, bool withBar = true)
    {
        // (text, rank): when the line is too wide the part with the highest rank goes first; the phase always stays
        var parts = new List<(string Text, int Rank)> { (CliFormat.Phase(progress.Phase), 0) };
        if (progress.Phase is RunPhase.Connecting or RunPhase.Scanning or RunPhase.Comparing && progress.FilesTotal == 0)
        {
            parts.Add(($"Quelle {CliFormat.Count(progress.FilesScannedSource)} · Ziel {CliFormat.Count(progress.FilesScannedTarget)} Dateien", 1));
            parts.Add((CliFormat.Duration(progress.Elapsed), 2));
            return Fit(parts, width);
        }

        parts.Add((withBar ? $"{Bar(progress.Fraction)} {CliFormat.Percent(progress.Fraction)}" : CliFormat.Percent(progress.Fraction), 1));
        parts.Add(($"{CliFormat.Count(progress.FilesDone)}/{CliFormat.Count(progress.FilesTotal)} Dateien", 2));
        if (progress.BytesTotal > 0)
        {
            parts.Add(($"{CliFormat.Bytes(progress.BytesDone)}/{CliFormat.Bytes(progress.BytesTotal)}", 6));
        }

        parts.Add((CliFormat.Speed(progress.BytesPerSecond), 5));
        parts.Add((CliFormat.FilesPerSecond(progress.FilesPerSecond), 7));
        if (progress.Remaining is { } remaining)
        {
            parts.Add(($"noch {CliFormat.Duration(remaining)}", 3));
        }

        if (progress.Errors > 0)
        {
            parts.Add(($"{CliFormat.Count(progress.Errors)} Fehler", 4));
        }

        return Fit(parts, width);
    }

    /// <summary>"[████████░░░░░░░░░░░░]".</summary>
    public static string Bar(double fraction)
    {
        var filled = (int)Math.Round(Math.Clamp(fraction, 0, 1) * BarWidth);
        return $"[{new string('█', filled)}{new string('░', BarWidth - filled)}]";
    }

    private static string Fit(List<(string Text, int Rank)> parts, int width)
    {
        var line = string.Join(" · ", parts.Select(part => part.Text));
        while (line.Length > width && parts.Count > 1)
        {
            parts.Remove(parts.MaxBy(part => part.Rank));
            line = string.Join(" · ", parts.Select(part => part.Text));
        }

        return line.Length <= width ? line : line[..Math.Max(0, width)];
    }
}
