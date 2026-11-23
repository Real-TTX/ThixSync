using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ThixSync.Core.Infrastructure;

/// <summary>A crash or a hang, written to the log folder; the app shows it on its next start.</summary>
public sealed record ProblemReport(string FilePath, DateTime Time, bool IsHang, string Text);

/// <summary>
/// Makes crashes and hangs visible instead of letting ThixSync vanish:
///  - an unhandled exception on any thread is written to logs/crash-*.txt (version, system, thread, the whole exception)
///    and, on Windows, shown at once in a message box (Ctrl+C there copies the text);
///  - a hang (the UI thread does not answer, App's watchdog) goes to logs/hang-*.txt, on Windows with a memory dump
///    hang-*.dmp next to it (only the newest one is kept: it holds the memory of the process, the open analyses included,
///    and stays on this computer unless the user passes it on);
///  - the next start shows every report that has not been shown yet (<see cref="TakeUnseen"/>).
/// Reports name exceptions and stacks, never the contents of files.
/// </summary>
public static class ProblemReports
{
    private const string SeenMarker = "reports-seen.txt";
    private const int KeptReports = 20;
    private static int _installed;

    /// <summary>Called first thing in Main: from then on no exception ends ThixSync without a report.</summary>
    public static void Install(bool showMessageBox)
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var exception = e.ExceptionObject as Exception;
            var text = exception?.ToString() ?? e.ExceptionObject?.ToString() ?? "unknown error";
            Log.Error("Crash", exception);
            var path = Write("crash", "ThixSync wurde wegen eines unerwarteten Fehlers beendet.", text);
            if (showMessageBox && OperatingSystem.IsWindows())
            {
                ShowMessageBox(exception, path);
                MarkSeen(path);
            }
        };

        // A forgotten task must not take its error with it.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task error", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Writes a hang report (and on Windows a dump); returns the report file.</summary>
    public static string? WriteHang(TimeSpan blockedFor, bool withDump = true)
    {
        var path = Write("hang", $"ThixSync hat {blockedFor.TotalSeconds:0} Sekunden lang nicht reagiert (die Oberfläche war blockiert).",
            string.Join(Environment.NewLine,
                "Unter Windows liegt ein Speicherabbild dieses Moments als .dmp-Datei daneben.",
                "Es enthält den Speicher von ThixSync, also auch Datei- und Ordnernamen –",
                "gib es nur weiter, wenn du das möchtest."));
        if (withDump && path is not null && OperatingSystem.IsWindows())
        {
            WriteDump(Path.ChangeExtension(path, ".dmp"));
        }

        return path;
    }

    /// <summary>Adds a line to a report (e.g. how long the hang lasted in the end).</summary>
    public static void Append(string path, string line)
    {
        try
        {
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The reports the user has not seen yet (oldest first); they count as seen afterwards.</summary>
    public static IReadOnlyList<ProblemReport> TakeUnseen()
    {
        try
        {
            if (!Directory.Exists(AppPaths.LogDirectory))
            {
                return [];
            }

            var seen = ReadSeen();
            var reports = ReportFiles()
                .Where(file => string.CompareOrdinal(Stamp(file), seen) > 0)
                .Select(Read)
                .OfType<ProblemReport>()
                .OrderBy(report => report.Time)
                .ToList();
            if (reports.Count > 0)
            {
                MarkSeen(reports[^1].FilePath);
            }

            return reports;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Problem reports could not be read: {exception.Message}");
            return [];
        }
    }

    private static string? Write(string kind, string headline, string details)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            var now = DateTime.Now;
            var path = Path.Combine(AppPaths.LogDirectory, $"{kind}-{now:yyyyMMdd-HHmmss}.txt");
            var text = new StringBuilder()
                .AppendLine(headline)
                .AppendLine()
                .AppendLine($"Zeit:     {now:dd.MM.yyyy HH:mm:ss}")
                .AppendLine($"Version:  {AppInfo.Version} ({RuntimeInformation.RuntimeIdentifier})")
                .AppendLine($"System:   {RuntimeInformation.OSDescription}, .NET {Environment.Version}")
                .AppendLine($"Thread:   {Thread.CurrentThread.Name ?? Environment.CurrentManagedThreadId.ToString()}")
                .AppendLine()
                .AppendLine(details)
                .ToString();
            File.WriteAllText(path, text, Encoding.UTF8);
            RemoveOldReports();
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ProblemReport? Read(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (!DateTime.TryParseExact(Stamp(path), "yyyyMMdd-HHmmss", null, System.Globalization.DateTimeStyles.None, out var time))
        {
            return null;
        }

        return new ProblemReport(path, time, name.StartsWith("hang-", StringComparison.Ordinal), File.ReadAllText(path, Encoding.UTF8));
    }

    private static IEnumerable<string> ReportFiles() =>
        Directory.EnumerateFiles(AppPaths.LogDirectory, "crash-*.txt")
            .Concat(Directory.EnumerateFiles(AppPaths.LogDirectory, "hang-*.txt"));

    /// <summary>"crash-20261115-181200.txt" -> "20261115-181200": sorts by time across both kinds.</summary>
    private static string Stamp(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name[(name.IndexOf('-') + 1)..];
    }

    /// <summary>The marker keeps the time stamp of the newest report shown.</summary>
    private static string ReadSeen()
    {
        var marker = Path.Combine(AppPaths.LogDirectory, SeenMarker);
        return File.Exists(marker) ? File.ReadAllText(marker).Trim() : "";
    }

    private static void MarkSeen(string? reportPath)
    {
        if (reportPath is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(Path.Combine(AppPaths.LogDirectory, SeenMarker), Stamp(reportPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void RemoveOldReports()
    {
        foreach (var old in ReportFiles().OrderByDescending(Path.GetFileName).Skip(KeptReports))
        {
            File.Delete(old);
        }

        // A dump is big: only the newest one stays.
        foreach (var dump in Directory.EnumerateFiles(AppPaths.LogDirectory, "hang-*.dmp").OrderByDescending(Path.GetFileName).Skip(1))
        {
            File.Delete(dump);
        }
    }

    private static void ShowMessageBox(Exception? exception, string? path)
    {
        var text = $"ThixSync wurde wegen eines unerwarteten Fehlers beendet.\n\n{exception?.GetType().FullName}: {exception?.Message}\n\n" +
                   (path is null ? "" : $"Der vollständige Bericht liegt hier:\n{path}\n\n") +
                   "Strg+C kopiert diese Meldung.";
        const uint iconError = 0x10, topMost = 0x40000, setForeground = 0x10000;
        _ = MessageBoxW(IntPtr.Zero, text, "ThixSync – unerwarteter Fehler", iconError | topMost | setForeground);
    }

    /// <summary>A dump with the private memory (managed heap) and thread data: enough for the managed stacks of every thread.</summary>
    private static void WriteDump(string path)
    {
        try
        {
            RemoveOldReports();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            const uint withDataSegs = 0x1, withHandleData = 0x4, withUnloadedModules = 0x20, withPrivateReadWriteMemory = 0x200,
                withFullMemoryInfo = 0x800, withThreadInfo = 0x1000;
            var type = withDataSegs | withHandleData | withUnloadedModules | withPrivateReadWriteMemory | withFullMemoryInfo | withThreadInfo;
            if (!MiniDumpWriteDump(process.Handle, (uint)process.Id, file.SafeFileHandle, type, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
            {
                Log.Warning($"Hang dump failed: error {Marshal.GetLastPInvokeError()}");
            }
            else
            {
                Log.Info($"Hang dump written ({file.Length / 1024 / 1024} MB)");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
                                              or DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warning($"Hang dump failed: {exception.Message}");
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(IntPtr process, uint processId, SafeFileHandle file, uint type,
        IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);
}
