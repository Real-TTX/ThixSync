using ThixSync.Core.Business;
using ThixSync.Core.Models;

namespace ThixSync.Cli;

/// <summary>list, run, pause, resume.</summary>
internal static class JobCommands
{
    /// <summary>The steps of a preview shown without --all.</summary>
    public const int PreviewItems = 20;

    public static Task<int> List(CliContext context)
    {
        var jobs = JobService.Query(null, JobFilterKind.All, JobSort.Name);
        var latest = ProtocolService.LatestRuns();
        if (context.Json)
        {
            var now = context.Host.Now();
            CliOutput.WriteJson(context.Out, jobs.Select(job => JobJson.Of(job, latest.GetValueOrDefault(job.Id), now)).ToList());
            return Task.FromResult(ExitCodes.Success);
        }

        if (jobs.Count == 0)
        {
            context.Out.WriteLine("Noch keine Aufträge. In der App unter „Synchronisationen“ anlegen oder mit „thixsync-cli import“ einlesen.");
            return Task.FromResult(ExitCodes.Success);
        }

        var rows = jobs.Select(job => (IReadOnlyList<string>)
        [
            job.Name,
            CliFormat.Mode(job.Mode),
            Route(job),
            (job.IsPaused ? "Pausiert · " : "") + JobService.DescribeSchedule(job.Schedule),
            latest.TryGetValue(job.Id, out var run) ? $"{CliFormat.Outcome(run.Outcome)} · {CliFormat.DateTime(run.StartedAt)}" : "–",
        ]).ToList();
        CliOutput.WriteTable(context.Out, ["Name", "Art", "Quelle → Ziel", "Zeitplan", "Letzter Lauf"], rows);
        return Task.FromResult(ExitCodes.Success);
    }

    public static async Task<int> Run(CliContext context)
    {
        var job = Lookup.Job(context.Arguments[0]);
        var command = context.Command;
        var host = context.Host;
        var preview = command.Has("preview");
        var quiet = command.Has("quiet");
        if (command.Has("all") && !preview)
        {
            throw new UsageException("--all gilt nur mit --preview.", "run");
        }

        var request = new RunRequest { Trigger = RunTrigger.Cli, AnalyseOnly = preview, ConfirmDeletes = command.Has("confirm-deletes") };
        var talk = !quiet && !context.Json;
        if (talk)
        {
            context.Out.WriteLine(preview ? $"Vorschau für „{job.Name}“ …" : $"Starte „{job.Name}“ … (Strg+C bricht ab)");
        }

        var progressLine = new ProgressLine(host);
        using var cancellation = new CancellationTokenSource();
        var cancelled = false;
        using var progress = host.SubscribeProgress(update =>
        {
            if (talk && update.JobId == job.Id)
            {
                progressLine.Show(update);
            }
        });
        using var cancelKey = host.OnCancelKey(() =>
        {
            cancelled = true;
            host.CancelRun(job.Id);
            cancellation.Cancel();
        });

        SyncRun run;
        try
        {
            run = await host.RunAsync(job.Id, request, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancelled)
        {
            progressLine.Close();
            context.Error.WriteLine($"„{job.Name}“ abgebrochen.");
            return ExitCodes.Cancelled;
        }
        finally
        {
            progressLine.Close();
        }

        var exitCode = cancelled ? ExitCodes.Cancelled : ExitCodes.ForOutcome(run.Outcome);
        var plan = preview ? host.LastPlan(job.Id) : null;
        if (context.Json)
        {
            CliOutput.WriteJson(context.Out, new RunResultJson(RunJson.Of(run), exitCode, plan is null ? null : PlanJson.Of(plan)));
            return exitCode;
        }

        if (quiet)
        {
            if (exitCode != ExitCodes.Success)
            {
                context.Error.WriteLine($"{CliFormat.Outcome(run.Outcome)}: {job.Name} – {run.Summary}");
            }

            return exitCode;
        }

        WriteSummary(context.Out, run);
        if (preview)
        {
            WritePlan(context.Out, plan, command.Has("all"));
        }

        return exitCode;
    }

    public static Task<int> Pause(CliContext context) => SetPaused(context, true);

    public static Task<int> Resume(CliContext context) => SetPaused(context, false);

    /// <summary>The end of a run in a few lines.</summary>
    public static void WriteSummary(TextWriter writer, SyncRun run)
    {
        writer.WriteLine($"{CliFormat.Outcome(run.Outcome)}: {run.JobName} ({CliFormat.Duration(run.Duration)})");
        if (run.Summary.Length > 0)
        {
            writer.WriteLine($"  {run.Summary}");
        }

        if (run.Outcome != RunOutcome.Analysed)
        {
            writer.WriteLine($"  Kopiert: {CliFormat.Count(run.FilesCopied, "Datei", "Dateien")} ({CliFormat.Bytes(run.BytesCopied)}) · " +
                             $"Gelöscht: {CliFormat.Count(run.FilesDeleted)} · Verschoben: {CliFormat.Count(run.FilesMoved)} · " +
                             $"Ordner angelegt: {CliFormat.Count(run.FoldersCreated)}");
        }

        writer.WriteLine($"  Durchsucht: {CliFormat.Count(run.FilesScanned, "Datei", "Dateien")} · Konflikte: {CliFormat.Count(run.Conflicts)} · " +
                         $"Fehler: {CliFormat.Count(run.Errors)}");
        if (run.Outcome == RunOutcome.NeedsConfirmation)
        {
            writer.WriteLine("  Der Lauf würde mehr löschen, als die Sicherheitsgrenze des Auftrags erlaubt. Mit --confirm-deletes bestätigen.");
        }

        writer.WriteLine($"  Protokoll: thixsync-cli log {Lookup.ShortId(run.Id)}");
    }

    /// <summary>The plan of a preview: totals, the steps per action, then the first steps (all with <paramref name="all"/>).</summary>
    public static void WritePlan(TextWriter writer, SyncPlan? plan, bool all)
    {
        if (plan is null)
        {
            writer.WriteLine("Kein Plan vorhanden.");
            return;
        }

        if (plan.IsInStep)
        {
            writer.WriteLine("Beide Seiten sind gleich – nichts zu tun.");
            return;
        }

        var steps = plan.Items.Where(item => item.Action != PlanAction.InStep).ToList();
        writer.WriteLine();
        writer.WriteLine($"Plan: {CliFormat.Count(steps.Count, "Schritt", "Schritte")} · {CliFormat.Bytes(plan.BytesToCopy)} zu kopieren" +
                         (plan.OpenConflicts > 0 ? $" · {CliFormat.Count(plan.OpenConflicts, "offener Konflikt", "offene Konflikte")}" : ""));
        var groups = steps.GroupBy(item => item.Action).OrderBy(group => group.Key).ToList();
        var width = groups.Max(group => CliFormat.Action(group.Key).Length);
        foreach (var group in groups)
        {
            var bytes = group.Sum(item => item.Bytes);
            writer.WriteLine($"  {CliFormat.Action(group.Key).PadRight(width)}  {CliFormat.Count(group.Count()),7}" +
                             (bytes > 0 ? $"  {CliFormat.Bytes(bytes)}" : ""));
        }

        writer.WriteLine();
        var shown = all ? steps : steps.Take(PreviewItems).ToList();
        foreach (var item in shown)
        {
            var path = item.FromPath is null ? item.Path : $"{item.FromPath} → {item.Path}";
            var size = item.Bytes > 0 ? $" ({CliFormat.Bytes(item.Bytes)})" : "";
            var reason = item.Reason.Length > 0 ? $" – {item.Reason}" : "";
            writer.WriteLine($"  {CliFormat.Action(item.Action).PadRight(width)}  {path}{size}{reason}");
        }

        if (shown.Count < steps.Count)
        {
            writer.WriteLine($"  … und {CliFormat.Count(steps.Count - shown.Count)} weitere (--all zeigt alle)");
        }
    }

    /// <summary>"NAS:/fotos → USB-Platte:/backup/fotos (+2 Paare)".</summary>
    private static string Route(SyncJob job)
    {
        var source = ConnectionService.Find(job.SourceConnectionId)?.Name ?? "?";
        var target = ConnectionService.Find(job.TargetConnectionId)?.Name ?? "?";
        if (job.Pairs.Count == 0)
        {
            return $"{source} → {target}";
        }

        var first = job.Pairs[0];
        var more = job.Pairs.Count > 1 ? $" (+{job.Pairs.Count - 1} {(job.Pairs.Count == 2 ? "Paar" : "Paare")})" : "";
        return $"{source}:{first.SourcePath} → {target}:{first.TargetPath}{more}";
    }

    private static Task<int> SetPaused(CliContext context, bool paused)
    {
        var job = JobService.SetPaused(Lookup.Job(context.Arguments[0]).Id, paused);
        context.Out.WriteLine(paused
            ? $"„{job.Name}“ ist pausiert: keine automatischen Läufe mehr."
            : $"„{job.Name}“ läuft wieder automatisch ({JobService.DescribeSchedule(job.Schedule)}).");
        return Task.FromResult(ExitCodes.Success);
    }
}
