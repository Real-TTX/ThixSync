using System.Globalization;
using ThixSync.Core.Models;

namespace ThixSync.Cli;

/// <summary>
/// German numbers, sizes, speeds, durations and the words of the models for the console ("3,2 GB", "48,2 MB/s", "1.204").
/// Sizes count in steps of 1024, like Windows Explorer. (The engine brings Business/Format.cs; the two may be unified.)
/// </summary>
internal static class CliFormat
{
    public static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>"512 B", "48,2 MB", "3,2 GB", "120 GB".</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{Math.Max(0, bytes).ToString(German)} B";
        }

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // 1023,95 would round up to "1024,0": then it is the next unit
        if (Math.Round(value, 1) >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value.ToString(value >= 100 ? "0" : "0.0", German)} {Units[unit]}";
    }

    /// <summary>"48,2 MB/s".</summary>
    public static string Speed(double bytesPerSecond) => $"{Bytes((long)Math.Max(0, bytesPerSecond))}/s";

    /// <summary>"1.204".</summary>
    public static string Count(long count) => count.ToString("N0", German);

    /// <summary>"1 Auftrag", "1.204 Aufträge".</summary>
    public static string Count(long count, string one, string many) => $"{Count(count)} {(count == 1 ? one : many)}";

    /// <summary>"120 Dateien/s", "3,5 Dateien/s".</summary>
    public static string FilesPerSecond(double filesPerSecond) =>
        $"{filesPerSecond.ToString(filesPerSecond >= 10 ? "N0" : "0.0", German)} Dateien/s";

    /// <summary>"45 %".</summary>
    public static string Percent(double fraction) => $"{Math.Clamp(fraction * 100, 0, 100).ToString("0", German)} %";

    /// <summary>"0 s", "45 s", "2 min 05 s", "1 h 12 min", "2 d 3 h".</summary>
    public static string Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalSeconds < 60)
        {
            return $"{(int)duration.TotalSeconds} s";
        }

        if (duration.TotalMinutes < 60)
        {
            return $"{(int)duration.TotalMinutes} min {duration.Seconds:00} s";
        }

        if (duration.TotalHours < 24)
        {
            return $"{(int)duration.TotalHours} h {duration.Minutes:00} min";
        }

        return $"{(int)duration.TotalDays} d {duration.Hours} h";
    }

    /// <summary>"23.11.2026 14:32" in local time.</summary>
    public static string DateTime(DateTimeOffset value) => value.ToLocalTime().ToString("dd.MM.yyyy HH:mm", German);

    /// <summary>"14:32:05" in local time.</summary>
    public static string Time(DateTimeOffset value) => value.ToLocalTime().ToString("HH:mm:ss", German);

    public static string Outcome(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Running => "Läuft",
        RunOutcome.Success => "Erfolgreich",
        RunOutcome.Warnings => "Mit Warnungen",
        RunOutcome.Failed => "Fehlgeschlagen",
        RunOutcome.Cancelled => "Abgebrochen",
        RunOutcome.Analysed => "Vorschau",
        RunOutcome.NeedsConfirmation => "Wartet auf Bestätigung",
        _ => outcome.ToString(),
    };

    public static string Mode(SyncMode mode) => mode switch
    {
        SyncMode.TwoWay => "Bidirektional",
        SyncMode.Mirror => "Spiegelung",
        SyncMode.Backup => "Backup",
        _ => mode.ToString(),
    };

    public static string Trigger(RunTrigger trigger) => trigger switch
    {
        RunTrigger.Manual => "Von Hand",
        RunTrigger.Schedule => "Zeitplan",
        RunTrigger.Change => "Änderung",
        RunTrigger.Startup => "Start",
        RunTrigger.Cli => "Befehlszeile",
        _ => trigger.ToString(),
    };

    public static string Phase(RunPhase phase) => phase switch
    {
        RunPhase.Connecting => "Verbinden",
        RunPhase.Scanning => "Durchsuchen",
        RunPhase.Comparing => "Vergleichen",
        RunPhase.Transferring => "Übertragen",
        RunPhase.Finishing => "Abschließen",
        _ => phase.ToString(),
    };

    /// <summary>What a step of a plan does, in the words of the preview.</summary>
    public static string Action(PlanAction action) => action switch
    {
        PlanAction.CopyToTarget => "Kopieren → Ziel",
        PlanAction.CopyToSource => "Kopieren → Quelle",
        PlanAction.DeleteOnTarget => "Löschen im Ziel",
        PlanAction.DeleteOnSource => "Löschen in der Quelle",
        PlanAction.CreateFolderOnTarget => "Ordner anlegen im Ziel",
        PlanAction.CreateFolderOnSource => "Ordner anlegen in der Quelle",
        PlanAction.MoveOnTarget => "Verschieben im Ziel",
        PlanAction.MoveOnSource => "Verschieben in der Quelle",
        PlanAction.Conflict => "Konflikt",
        PlanAction.InStep => "Gleich",
        _ => action.ToString(),
    };

    public static string Level(RunEntryLevel level) => level switch
    {
        RunEntryLevel.Warning => "WARNUNG",
        RunEntryLevel.Error => "FEHLER",
        _ => "INFO",
    };
}
