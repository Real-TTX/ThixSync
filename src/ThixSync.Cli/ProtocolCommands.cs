using ThixSync.Core.Business;
using ThixSync.Core.Models;

namespace ThixSync.Cli;

/// <summary>status, runs, log.</summary>
internal static class ProtocolCommands
{
    public const int DefaultRunCount = 20;

    public static Task<int> Status(CliContext context)
    {
        var running = context.Host.Running();
        if (context.Json)
        {
            CliOutput.WriteJson(context.Out, running.Select(ProgressJson.Of).ToList());
            return Task.FromResult(ExitCodes.Success);
        }

        if (running.Count == 0)
        {
            context.Out.WriteLine("Gerade läuft kein Auftrag.");
            return Task.FromResult(ExitCodes.Success);
        }

        var rows = running.Select(progress => (IReadOnlyList<string>)
        [
            JobService.Find(progress.JobId)?.Name ?? progress.JobId.ToString(),
            ProgressLine.Render(progress, int.MaxValue),
        ]).ToList();
        CliOutput.WriteTable(context.Out, ["Auftrag", "Fortschritt"], rows);
        return Task.FromResult(ExitCodes.Success);
    }

    public static Task<int> Runs(CliContext context)
    {
        var jobId = context.Command.Value("job") is { } jobText ? Lookup.Job(jobText).Id : (Guid?)null;
        var limit = context.Command.Number("limit", DefaultRunCount, 1, 1000);
        var runs = ProtocolService.Query(null, RunFilterKind.All, RunSort.Newest, jobId, 0, limit).Items;
        if (context.Json)
        {
            CliOutput.WriteJson(context.Out, runs.Select(RunJson.Of).ToList());
            return Task.FromResult(ExitCodes.Success);
        }

        if (runs.Count == 0)
        {
            context.Out.WriteLine("Noch keine Läufe im Protokoll.");
            return Task.FromResult(ExitCodes.Success);
        }

        var rows = runs.Select(run => (IReadOnlyList<string>)
        [
            Lookup.ShortId(run.Id),
            CliFormat.DateTime(run.StartedAt),
            run.JobName,
            CliFormat.Outcome(run.Outcome),
            CliFormat.Duration(run.Duration),
            CliFormat.Count(run.FilesCopied),
            CliFormat.Bytes(run.BytesCopied),
            CliFormat.Trigger(run.Trigger),
        ]).ToList();
        CliOutput.WriteTable(context.Out, ["Id", "Gestartet", "Auftrag", "Ergebnis", "Dauer", "Dateien", "Daten", "Auslöser"], rows);
        return Task.FromResult(ExitCodes.Success);
    }

    public static Task<int> Log(CliContext context)
    {
        var run = Lookup.Run(context.Arguments[0]);
        var entries = AllEntries(run.Id, context.Command.Has("problems"));
        if (context.Json)
        {
            CliOutput.WriteJson(context.Out, new RunLogJson(RunJson.Of(run), [.. entries.Select(EntryJson.Of)]));
            return Task.FromResult(ExitCodes.Success);
        }

        context.Out.WriteLine($"Lauf {run.Id} · {CliFormat.DateTime(run.StartedAt)} · {CliFormat.Trigger(run.Trigger)} · {run.DeviceName}");
        JobCommands.WriteSummary(context.Out, run);
        context.Out.WriteLine();
        if (entries.Count == 0)
        {
            context.Out.WriteLine(context.Command.Has("problems") ? "Keine Warnungen und Fehler." : "Das Protokoll hat keine Zeilen.");
            return Task.FromResult(ExitCodes.Success);
        }

        foreach (var entry in entries)
        {
            var action = entry.Action is { } planAction ? $"{CliFormat.Action(planAction)}  " : "";
            var path = entry.Path.Length > 0 ? $"{entry.Path}  " : "";
            var size = entry.Bytes > 0 ? $"({CliFormat.Bytes(entry.Bytes)})  " : "";
            context.Out.WriteLine($"{CliFormat.Time(entry.At)}  {CliFormat.Level(entry.Level),-7}  {action}{path}{size}{entry.Message}".TrimEnd());
        }

        return Task.FromResult(ExitCodes.Success);
    }

    /// <summary>Every line of a run, page by page.</summary>
    private static List<RunEntry> AllEntries(Guid runId, bool onlyProblems)
    {
        const int pageSize = 2000;
        var entries = new List<RunEntry>();
        for (var pageIndex = 0; ; pageIndex++)
        {
            var page = ProtocolService.Entries(runId, null, onlyProblems, pageIndex, pageSize);
            entries.AddRange(page.Items);
            if (page.Items.Count == 0 || (pageIndex + 1) * pageSize >= page.TotalCount)
            {
                return entries;
            }
        }
    }
}
