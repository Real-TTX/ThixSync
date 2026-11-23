using System.Globalization;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;

namespace ThixSync.Core.Business;

/// <summary>
/// The sync jobs (page "Synchronisationen"): list, query, create, change, pause, delete. Every check is here (names, connections,
/// folder pairs that do not overlap, schedule, options, policies). Running them is <see cref="JobRunner"/>'s work.
/// </summary>
public static class JobService
{
    public const int MaxPairs = 50;
    public const int MaxParallelism = 128;

    private static readonly Lock Gate = new();
    private static List<SyncJob>? _cache;
    private static long _cacheVersion;

    /// <summary>Raised after any change (any thread).</summary>
    public static event EventHandler? Changed;

    public static IReadOnlyList<SyncJob> List()
    {
        lock (Gate)
        {
            var version = SyncStore.DataVersion();
            if (_cache is null || version != _cacheVersion)
            {
                _cache = SyncStore.LoadJobs();
                _cacheVersion = version;
            }

            return _cache;
        }
    }

    public static SyncJob? Find(Guid id) => List().FirstOrDefault(job => job.Id == id);

    public static SyncJob Get(Guid id) => Find(id) ?? throw new BusinessException("Diesen Auftrag gibt es nicht mehr.");

    /// <summary>A job by its id or (for the CLI) its name, case-insensitive; null when none or several match.</summary>
    public static SyncJob? FindByIdOrName(string text)
    {
        if (Guid.TryParse(text, out var id))
        {
            return Find(id);
        }

        var matches = List().Where(job => string.Equals(job.Name, text.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>Searches, filters and sorts (the toolbar of the list). <paramref name="search"/> matches name, description and folders.</summary>
    public static IReadOnlyList<SyncJob> Query(string? search, JobFilterKind filter, JobSort sort)
    {
        IEnumerable<SyncJob> jobs = List();
        var latest = SyncStore.LatestRunPerJob();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            jobs = jobs.Where(job => job.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                || job.Description.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                || job.Pairs.Any(pair => pair.SourcePath.Contains(text, StringComparison.CurrentCultureIgnoreCase) || pair.TargetPath.Contains(text, StringComparison.CurrentCultureIgnoreCase)));
        }

        jobs = filter switch
        {
            JobFilterKind.TwoWay => jobs.Where(job => job.Mode == SyncMode.TwoWay),
            JobFilterKind.Mirror => jobs.Where(job => job.Mode == SyncMode.Mirror),
            JobFilterKind.Backup => jobs.Where(job => job.Mode == SyncMode.Backup),
            JobFilterKind.Problems => jobs.Where(job => latest.TryGetValue(job.Id, out var run) && run.Outcome is RunOutcome.Failed or RunOutcome.Warnings or RunOutcome.NeedsConfirmation),
            JobFilterKind.Scheduled => jobs.Where(job => job.Schedule.Kind != ScheduleKind.Manual || job.Schedule.RunOnChange),
            JobFilterKind.Paused => jobs.Where(job => job.IsPaused),
            _ => jobs,
        };

        return sort switch
        {
            JobSort.LastRun => [.. jobs.OrderByDescending(job => latest.TryGetValue(job.Id, out var run) ? run.StartedAt : DateTimeOffset.MinValue)],
            JobSort.NextRun => [.. jobs.OrderBy(job => NextRun(job, AppClock.UtcNow) ?? DateTimeOffset.MaxValue)],
            JobSort.Created => [.. jobs.OrderByDescending(job => job.CreateDate)],
            _ => [.. jobs.OrderBy(job => job.Name, StringComparer.CurrentCultureIgnoreCase)],
        };
    }

    /// <summary>A new job with sensible defaults (this computer as source, the first folder pair empty).</summary>
    public static SyncJob CreateDraft()
    {
        var thisComputer = ConnectionService.EnsureThisComputer();
        return new SyncJob
        {
            Name = "",
            Mode = SyncMode.TwoWay,
            SourceConnectionId = thisComputer.Id,
            Pairs = [new FolderPair()],
            RunsOnDevice = AppInfo.MachineName,
        };
    }

    /// <summary>Saves a job after every check. Returns the saved job.</summary>
    public static SyncJob Save(SyncJob job)
    {
        AccessPolicy.EnsureConfigurationEditable();
        var existing = Find(job.Id);
        var name = job.Name.Trim();
        if (name.Length == 0)
        {
            throw new BusinessException("Bitte einen Namen eingeben.", nameof(SyncJob.Name));
        }

        if (name.Length > 100)
        {
            throw new BusinessException("Der Name ist zu lang (höchstens 100 Zeichen).", nameof(SyncJob.Name));
        }

        if (List().Any(other => other.Id != job.Id && string.Equals(other.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new BusinessException("Einen Auftrag mit diesem Namen gibt es schon.", nameof(SyncJob.Name));
        }

        if (!Enum.IsDefined(job.Mode))
        {
            throw new BusinessException("Diese Art der Synchronisation gibt es nicht.", nameof(SyncJob.Mode));
        }

        var source = ConnectionService.Find(job.SourceConnectionId) ?? throw new BusinessException("Bitte eine Quelle wählen.", nameof(SyncJob.SourceConnectionId));
        var target = ConnectionService.Find(job.TargetConnectionId) ?? throw new BusinessException("Bitte ein Ziel wählen.", nameof(SyncJob.TargetConnectionId));
        AccessPolicy.EnsureConnectionKindAllowed(source.Kind);
        AccessPolicy.EnsureConnectionKindAllowed(target.Kind);

        var pairs = job.Pairs.Select(pair => new FolderPair { SourcePath = pair.SourcePath.Trim(), TargetPath = pair.TargetPath.Trim() }).ToList();
        ValidatePairs(pairs, source, target);
        var schedule = ValidateSchedule(job.Schedule);
        var options = ValidateOptions(job.Options, job.Mode);
        var filter = ValidateFilter(job.Filter);

        var now = AppClock.UtcNow;
        var saved = job with
        {
            Name = name,
            Description = job.Description.Trim(),
            Pairs = pairs,
            Schedule = schedule,
            Options = options,
            Filter = filter,
            RunsOnDevice = job.RunsOnDevice.Trim().Length > 0 ? job.RunsOnDevice.Trim() : AppInfo.MachineName,
            CreateDate = existing?.CreateDate ?? now,
            UpdateDate = now,
            SyncState = SyncState.Pending,
        };

        SyncStore.SaveJob(saved);
        ChangedNow();
        CloudSyncService.Kick();
        return Get(saved.Id);
    }

    /// <summary>Pauses or resumes the automatic runs of a job.</summary>
    public static SyncJob SetPaused(Guid id, bool paused)
    {
        var job = Get(id);
        return job.IsPaused == paused ? job : Save(job with { IsPaused = paused });
    }

    /// <summary>Makes a copy of a job ("Kopie von …"), paused, never run.</summary>
    public static SyncJob Duplicate(Guid id)
    {
        var job = Get(id);
        var name = $"Kopie von {job.Name}";
        for (var number = 2; List().Any(other => string.Equals(other.Name, name, StringComparison.CurrentCultureIgnoreCase)); number++)
        {
            name = $"Kopie {number} von {job.Name}";
        }

        return Save(job with { Id = Guid.NewGuid(), Name = name, IsPaused = true, CreateDate = default });
    }

    /// <summary>Deletes a job (everywhere) with its state; its protocol stays until it ages out. Not while it runs.</summary>
    public static void Delete(Guid id)
    {
        AccessPolicy.EnsureConfigurationEditable();
        Get(id);
        if (JobRunner.IsRunning(id))
        {
            throw new BusinessException("Der Auftrag läuft gerade – bitte zuerst anhalten.");
        }

        SyncStore.DeleteJob(id, AppClock.UtcNow, CloudSyncService.IsConnected);
        JobRunner.ForgetState(id);
        ChangedNow();
        CloudSyncService.Kick();
    }

    /// <summary>Whether this computer runs the job by itself (schedule, changes).</summary>
    public static bool RunsHere(SyncJob job) =>
        string.Equals(job.RunsOnDevice, AppInfo.MachineName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this computer can run the job at all (it reaches both connections).</summary>
    public static bool CanRunHere(SyncJob job, out string reason)
    {
        var source = ConnectionService.Find(job.SourceConnectionId);
        var target = ConnectionService.Find(job.TargetConnectionId);
        if (source is null || target is null)
        {
            reason = "Eine Verbindung des Auftrags gibt es nicht mehr.";
            return false;
        }

        if (!ConnectionService.IsUsableHere(source, out var sourceReason))
        {
            reason = $"Quelle: {sourceReason}";
            return false;
        }

        if (!ConnectionService.IsUsableHere(target, out var targetReason))
        {
            reason = $"Ziel: {targetReason}";
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>The next time the schedule wants a run after <paramref name="after"/> (null: never by time).</summary>
    public static DateTimeOffset? NextRun(SyncJob job, DateTimeOffset after)
    {
        if (job.IsPaused)
        {
            return null;
        }

        var schedule = job.Schedule;
        var local = after.ToLocalTime();
        switch (schedule.Kind)
        {
            case ScheduleKind.Interval:
                var last = SyncStore.LatestRunPerJob().TryGetValue(job.Id, out var run) ? run.StartedAt : (DateTimeOffset?)null;
                var next = (last ?? after).AddMinutes(Math.Max(1, schedule.IntervalMinutes));
                return next < after ? after : next;
            case ScheduleKind.Daily:
            case ScheduleKind.Weekly:
                for (var day = 0; day <= 7; day++)
                {
                    var date = DateOnly.FromDateTime(local.Date).AddDays(day);
                    if (schedule.Kind == ScheduleKind.Weekly && !schedule.Days.Contains(date.DayOfWeek))
                    {
                        continue;
                    }

                    var candidate = new DateTimeOffset(date.ToDateTime(schedule.TimeOfDay), local.Offset);
                    candidate = new DateTimeOffset(candidate.DateTime, TimeZoneInfo.Local.GetUtcOffset(candidate.DateTime));
                    if (candidate > after)
                    {
                        return candidate;
                    }
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>The schedule in words ("Täglich, 20:00", "Alle 15 Minuten · bei Änderungen", "Manuell").</summary>
    public static string DescribeSchedule(JobSchedule schedule)
    {
        var culture = CultureInfo.GetCultureInfo("de-DE");
        var time = schedule.TimeOfDay.ToString("HH:mm", culture);
        var text = schedule.Kind switch
        {
            ScheduleKind.Interval when schedule.IntervalMinutes % 60 == 0 && schedule.IntervalMinutes >= 60 =>
                schedule.IntervalMinutes == 60 ? "Stündlich" : $"Alle {schedule.IntervalMinutes / 60} Stunden",
            ScheduleKind.Interval => $"Alle {schedule.IntervalMinutes} Minuten",
            ScheduleKind.Daily => $"Täglich, {time}",
            ScheduleKind.Weekly when schedule.Days.Count == 5 && !schedule.Days.Contains(DayOfWeek.Saturday) && !schedule.Days.Contains(DayOfWeek.Sunday) =>
                $"Werktags, {time}",
            ScheduleKind.Weekly => $"{string.Join(", ", OrderedDays(schedule.Days).Select(day => culture.DateTimeFormat.GetAbbreviatedDayName(day)))}, {time}",
            _ => schedule.RunOnChange ? "" : "Manuell",
        };

        var extras = new List<string>();
        if (schedule.RunOnChange)
        {
            extras.Add("bei Änderungen");
        }

        if (schedule.RunAtStartup)
        {
            extras.Add("beim Start");
        }

        if (extras.Count == 0)
        {
            return text;
        }

        var extra = string.Join(" · ", extras);
        return text.Length == 0 ? char.ToUpper(extra[0], culture) + extra[1..] : $"{text} · {extra}";
    }

    /// <summary>The mode in words ("Bidirektionale Synchronisation" …).</summary>
    public static string DescribeMode(SyncMode mode) => mode switch
    {
        SyncMode.TwoWay => "Bidirektionale Synchronisation",
        SyncMode.Mirror => "Spiegelung (Quelle → Ziel)",
        SyncMode.Backup => "Backup mit Versionen",
        _ => mode.ToString(),
    };

    /// <summary>A copy of the cloud is applied here.</summary>
    internal static void ApplyRemote(SyncJob job)
    {
        SyncStore.SaveJob(job with { SyncState = SyncState.Synced });
        ChangedNow();
    }

    internal static void ApplyRemoteDelete(Guid id)
    {
        SyncStore.DeleteJob(id, AppClock.UtcNow, keepTombstone: false);
        JobRunner.ForgetState(id);
        ChangedNow();
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _cache = null;
        }
    }

    internal static void ChangedNow()
    {
        lock (Gate)
        {
            _cache = null;
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static IEnumerable<DayOfWeek> OrderedDays(IEnumerable<DayOfWeek> days) =>
        days.Distinct().OrderBy(day => ((int)day + 6) % 7);

    private static void ValidatePairs(List<FolderPair> pairs, Connection source, Connection target)
    {
        if (pairs.Count == 0)
        {
            throw new BusinessException("Bitte mindestens ein Ordnerpaar angeben.", nameof(SyncJob.Pairs));
        }

        if (pairs.Count > MaxPairs)
        {
            throw new BusinessException($"Höchstens {MaxPairs} Ordnerpaare je Auftrag.", nameof(SyncJob.Pairs));
        }

        for (var index = 0; index < pairs.Count; index++)
        {
            var pair = pairs[index];
            if (source.Kind is ConnectionKind.Local && pair.SourcePath.Length == 0)
            {
                throw new BusinessException($"Ordnerpaar {index + 1}: bitte den Ordner der Quelle wählen.", nameof(SyncJob.Pairs));
            }

            if (target.Kind is ConnectionKind.Local && pair.TargetPath.Length == 0)
            {
                throw new BusinessException($"Ordnerpaar {index + 1}: bitte den Ordner des Ziels wählen.", nameof(SyncJob.Pairs));
            }

            if (source.Kind == ConnectionKind.Local && !Path.IsPathFullyQualified(pair.SourcePath))
            {
                throw new BusinessException($"Ordnerpaar {index + 1}: der Ordner der Quelle muss ein vollständiger Pfad sein.", nameof(SyncJob.Pairs));
            }

            if (target.Kind == ConnectionKind.Local && !Path.IsPathFullyQualified(pair.TargetPath))
            {
                throw new BusinessException($"Ordnerpaar {index + 1}: der Ordner des Ziels muss ein vollständiger Pfad sein.", nameof(SyncJob.Pairs));
            }

            if (source.Id == target.Id && Overlaps(source.Kind, pair.SourcePath, pair.TargetPath))
            {
                throw new BusinessException($"Ordnerpaar {index + 1}: Quelle und Ziel dürfen nicht derselbe Ordner sein oder ineinander liegen.", nameof(SyncJob.Pairs));
            }

            for (var other = 0; other < index; other++)
            {
                if (Overlaps(target.Kind, pairs[other].TargetPath, pair.TargetPath))
                {
                    throw new BusinessException($"Ordnerpaar {index + 1}: das Ziel überschneidet sich mit Ordnerpaar {other + 1}.", nameof(SyncJob.Pairs));
                }
            }
        }
    }

    private static bool Overlaps(ConnectionKind kind, string first, string second)
    {
        var comparison = kind == ConnectionKind.Local && !OperatingSystem.IsLinux() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var a = kind == ConnectionKind.Local ? first.Replace('\\', '/').TrimEnd('/') : StoragePath.Normalize(first);
        var b = kind == ConnectionKind.Local ? second.Replace('\\', '/').TrimEnd('/') : StoragePath.Normalize(second);
        return StoragePath.IsInside(a, b, comparison) || StoragePath.IsInside(b, a, comparison);
    }

    private static JobSchedule ValidateSchedule(JobSchedule schedule)
    {
        if (!Enum.IsDefined(schedule.Kind))
        {
            throw new BusinessException("Diesen Zeitplan gibt es nicht.", nameof(SyncJob.Schedule));
        }

        if (schedule.Kind == ScheduleKind.Interval && schedule.IntervalMinutes is < 1 or > 7 * 24 * 60)
        {
            throw new BusinessException("Der Abstand muss zwischen 1 Minute und 7 Tagen liegen.", nameof(JobSchedule.IntervalMinutes));
        }

        if (schedule.Kind == ScheduleKind.Weekly && schedule.Days.Count == 0)
        {
            throw new BusinessException("Bitte mindestens einen Wochentag wählen.", nameof(JobSchedule.Days));
        }

        if (schedule.ChangeDelaySeconds is < 1 or > 3600)
        {
            throw new BusinessException("Die Wartezeit nach Änderungen muss zwischen 1 und 3600 Sekunden liegen.", nameof(JobSchedule.ChangeDelaySeconds));
        }

        if (schedule.PollMinutes is < 1 or > 24 * 60)
        {
            throw new BusinessException("Die Prüfung entfernter Seiten muss zwischen 1 Minute und 24 Stunden liegen.", nameof(JobSchedule.PollMinutes));
        }

        return schedule with { Days = [.. OrderedDays(schedule.Days)] };
    }

    private static JobOptions ValidateOptions(JobOptions options, SyncMode mode)
    {
        if (!Enum.IsDefined(options.Conflicts))
        {
            throw new BusinessException("Diese Konfliktregel gibt es nicht.", nameof(JobOptions.Conflicts));
        }

        if (options.Parallelism is < 0 or > MaxParallelism)
        {
            throw new BusinessException($"Gleichzeitige Übertragungen: 0 (automatisch) bis {MaxParallelism}.", nameof(JobOptions.Parallelism));
        }

        if (options.KeepVersionsDays is < 0 or > 3650 || options.KeepVersionsCount is < 0 or > 1000)
        {
            throw new BusinessException("Versionen: höchstens 3650 Tage und 1000 Versionen je Datei.", nameof(JobOptions.KeepVersionsDays));
        }

        if (options.SafetyDeletePercent is < 0 or > 100)
        {
            throw new BusinessException("Die Sicherheitsgrenze ist ein Prozentwert (0 bis 100).", nameof(JobOptions.SafetyDeletePercent));
        }

        if (options.TimeToleranceSeconds is < 0 or > 7200)
        {
            throw new BusinessException("Die Zeittoleranz muss zwischen 0 und 7200 Sekunden liegen.", nameof(JobOptions.TimeToleranceSeconds));
        }

        // a backup never deletes on the target; the policy can forbid deleting at all
        var propagate = options.PropagateDeletes && mode != SyncMode.Backup && !AccessPolicy.IsDeleteDisabled;
        var keepVersions = options.KeepVersions || mode == SyncMode.Backup;
        return options with { PropagateDeletes = propagate, KeepVersions = keepVersions };
    }

    private static JobFilter ValidateFilter(JobFilter filter)
    {
        static List<string> Clean(IEnumerable<string> patterns) =>
            [.. patterns.Select(pattern => pattern.Trim()).Where(pattern => pattern.Length > 0).Distinct(StringComparer.Ordinal)];

        if (filter.MaxFileSizeBytes < 0 || filter.MaxAgeDays < 0)
        {
            throw new BusinessException("Größe und Alter dürfen nicht negativ sein.", nameof(JobFilter.MaxFileSizeBytes));
        }

        var include = Clean(filter.Include);
        var exclude = Clean(filter.Exclude);
        foreach (var pattern in include.Concat(exclude))
        {
            if (pattern.Contains(".."))
            {
                throw new BusinessException($"Das Muster „{pattern}“ ist ungültig (..).", nameof(JobFilter.Exclude));
            }
        }

        return filter with { Include = include, Exclude = exclude };
    }
}

/// <summary>The filter of the job list (toolbar).</summary>
public enum JobFilterKind
{
    All = 0,
    TwoWay = 1,
    Mirror = 2,
    Backup = 3,
    Problems = 4,
    Scheduled = 5,
    Paused = 6,
}

/// <summary>The sort orders of the job list.</summary>
public enum JobSort
{
    Name = 0,
    LastRun = 1,
    NextRun = 2,
    Created = 3,
}
