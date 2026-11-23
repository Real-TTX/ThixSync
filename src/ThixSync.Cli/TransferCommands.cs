using System.Text;
using ThixSync.Core.Business;

namespace ThixSync.Cli;

/// <summary>export, import (<see cref="ExportService"/>: jobs and connections as JSON, never a secret).</summary>
internal static class TransferCommands
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static Task<int> Export(CliContext context)
    {
        var json = ExportService.Export();
        var file = context.Arguments.Count > 0 ? context.Arguments[0] : "-";
        if (file == "-")
        {
            context.Out.WriteLine(json);
            return Task.FromResult(ExitCodes.Success);
        }

        var path = Path.GetFullPath(file);
        File.WriteAllText(path, json, Utf8);
        var export = ExportService.Read(json);
        context.Out.WriteLine($"{CliFormat.Count(export.Connections.Count, "Verbindung", "Verbindungen")} und {CliFormat.Count(export.Jobs.Count, "Auftrag", "Aufträge")} " +
                              $"exportiert nach {path} (ohne Kennwörter, Schlüssel und Anmeldungen).");
        return Task.FromResult(ExitCodes.Success);
    }

    public static Task<int> Import(CliContext context)
    {
        var file = context.Arguments[0];
        string json;
        if (file == "-")
        {
            json = context.Host.In.ReadToEnd();
        }
        else
        {
            var path = Path.GetFullPath(file);
            json = File.Exists(path) ? File.ReadAllText(path, Utf8) : throw new NotFoundException($"Die Datei {path} gibt es nicht.");
        }

        var result = ExportService.Import(json);
        var exitCode = result.Failed > 0 ? ExitCodes.BusinessError : ExitCodes.Success;
        if (context.Json)
        {
            CliOutput.WriteJson(context.Out, new ImportJson(result.Imported, result.Skipped, result.Failed, result.Items));
            return Task.FromResult(exitCode);
        }

        foreach (var item in result.Items)
        {
            var kind = item.Kind == ImportItemKind.Connection ? "Verbindung" : "Auftrag";
            var line = item.Outcome switch
            {
                ImportOutcome.Imported => $"+ {kind} „{item.Name}“ importiert",
                ImportOutcome.Skipped => $"= {kind} „{item.Name}“ übersprungen",
                _ => $"! {kind} „{item.Name}“ nicht importiert",
            };
            context.Out.WriteLine(item.Message.Length > 0 ? $"{line}: {item.Message}" : line);
        }

        context.Out.WriteLine($"{CliFormat.Count(result.Imported)} importiert, {CliFormat.Count(result.Skipped)} übersprungen, " +
                              $"{CliFormat.Count(result.Failed)} abgelehnt.");
        return Task.FromResult(exitCode);
    }
}
