using ThixSync.Controls;
using ThixSync.Core.Business;
using ThixSync.Core.Models;

namespace ThixSync;

/// <summary>The state of a job in one word (the pill of a card, the schedules, the tray).</summary>
public enum JobState
{
    UpToDate,
    Running,
    Failed,
    Conflicts,
    Warnings,
    NeedsConfirmation,
    Cancelled,
    Paused,
    Never,
}

/// <summary>
/// What a job's card and list rows say about it: the state (from the running progress, the pause and the newest run), its word, icon
/// and tone, the newest run and the conflicts that wait (of the kept plan, else of the newest run).
/// </summary>
public sealed record JobStatus(JobState State, string Text, string Icon, Tone Tone, SyncRun? LastRun, int OpenConflicts)
{
    /// <summary>The state of a job; <paramref name="latest"/> is <see cref="ProtocolService.LatestRuns"/> (asked once per list).</summary>
    public static JobStatus Of(SyncJob job, IReadOnlyDictionary<Guid, SyncRun> latest)
    {
        var lastRun = latest.GetValueOrDefault(job.Id);
        var plan = LastPlan(job.Id);
        var conflicts = plan?.OpenConflicts ?? (int)(lastRun?.Conflicts ?? 0);
        if (RunMonitor.IsRunning(job.Id))
        {
            return new JobStatus(JobState.Running, "Läuft …", "refresh", Tone.Accent, lastRun, conflicts);
        }

        if (job.IsPaused)
        {
            return new JobStatus(JobState.Paused, "Pausiert", "pause", Tone.Muted, lastRun, conflicts);
        }

        return lastRun?.Outcome switch
        {
            null => new JobStatus(JobState.Never, "Nie synchronisiert", "circle", Tone.Muted, null, conflicts),
            RunOutcome.Failed => new JobStatus(JobState.Failed, "Fehler", "x-circle", Tone.Danger, lastRun, conflicts),
            RunOutcome.NeedsConfirmation => new JobStatus(JobState.NeedsConfirmation, "Bestätigung nötig", "alert", Tone.Warning, lastRun, conflicts),
            RunOutcome.Cancelled => new JobStatus(JobState.Cancelled, "Abgebrochen", "stop", Tone.Muted, lastRun, conflicts),
            _ when conflicts > 0 => new JobStatus(JobState.Conflicts, "Konflikte", "alert", Tone.Warning, lastRun, conflicts),
            RunOutcome.Warnings => new JobStatus(JobState.Warnings, "Mit Warnungen", "alert", Tone.Warning, lastRun, conflicts),
            _ => new JobStatus(JobState.UpToDate, "Aktuell", "check", Tone.Success, lastRun, conflicts),
        };
    }

    /// <summary>The word and tone of how a run ended (the protocol).</summary>
    public static (string Text, string Icon, Tone Tone) OfOutcome(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Running => ("Läuft", "refresh", Tone.Accent),
        RunOutcome.Success => ("Erfolgreich", "check-circle", Tone.Success),
        RunOutcome.Warnings => ("Mit Warnungen", "alert", Tone.Warning),
        RunOutcome.Failed => ("Fehlgeschlagen", "x-circle", Tone.Danger),
        RunOutcome.Cancelled => ("Abgebrochen", "stop", Tone.Muted),
        RunOutcome.Analysed => ("Analyse", "search", Tone.Info),
        RunOutcome.NeedsConfirmation => ("Bestätigung nötig", "alert", Tone.Warning),
        _ => (outcome.ToString(), "info", Tone.Muted),
    };

    /// <summary>Why a run started, in words.</summary>
    public static string OfTrigger(RunTrigger trigger) => trigger switch
    {
        RunTrigger.Manual => "Von Hand",
        RunTrigger.Schedule => "Zeitplan",
        RunTrigger.Change => "Änderung erkannt",
        RunTrigger.Startup => "Beim Start",
        RunTrigger.Cli => "Kommandozeile",
        _ => trigger.ToString(),
    };

    /// <summary>The kept plan of a job (null: none, or no engine yet).</summary>
    public static SyncPlan? LastPlan(Guid jobId)
    {
        try
        {
            return JobRunner.LastPlan(jobId);
        }
        catch (NotImplementedException)
        {
            return null;
        }
    }
}
