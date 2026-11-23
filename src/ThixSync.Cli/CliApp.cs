using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;

namespace ThixSync.Cli;

/// <summary>
/// thixsync-cli: reads the command line, runs the command and turns what went wrong into the documented exit code
/// (<see cref="ExitCodes"/>): a wrong call 3, something not found 4, a refusal of the business layer 5 (message on stderr).
/// </summary>
internal static class CliApp
{
    public const string ProgramName = "thixsync-cli";

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CliHost host)
    {
        try
        {
            var parsed = CommandLine.Parse(args, Commands.All);
            switch (parsed.Kind)
            {
                case RequestKind.Version:
                    host.Out.WriteLine(VersionText());
                    return ExitCodes.Success;
                case RequestKind.Help:
                    host.Out.Write(parsed.Command is null ? GeneralHelp() : CommandHelp(parsed.Command));
                    return ExitCodes.Success;
                default:
                    return await parsed.Command!.Handler(new CliContext(host, parsed));
            }
        }
        catch (UsageException exception)
        {
            host.Error.WriteLine($"Fehler: {exception.Message}");
            host.Error.WriteLine($"Hilfe: {ProgramName} help{(exception.Command is null ? "" : " " + exception.Command)}");
            return ExitCodes.Usage;
        }
        catch (NotFoundException exception)
        {
            host.Error.WriteLine(exception.Message);
            return ExitCodes.NotFound;
        }
        catch (BusinessException exception)
        {
            host.Error.WriteLine(exception.Message);
            return ExitCodes.BusinessError;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.Error("thixsync-cli failed", exception);
            host.Error.WriteLine($"Fehler: {exception.Message}");
            return ExitCodes.Failed;
        }
    }

    public static string VersionText() => $"{ProgramName} {AppInfo.Version} ({AppInfo.Channel})";

    public static string GeneralHelp()
    {
        var help = new StringWriter();
        help.WriteLine($"ThixSync – Synchronisation und Backup, Befehlszeile ({AppInfo.Version})");
        help.WriteLine();
        help.WriteLine($"Aufruf: {ProgramName} <befehl> [angaben] [optionen]");
        help.WriteLine();
        help.WriteLine("Befehle:");
        var width = Commands.All.Max(command => command.Name.Length);
        foreach (var command in Commands.All)
        {
            help.WriteLine($"  {command.Name.PadRight(width)}  {command.Summary}");
        }

        help.WriteLine();
        help.WriteLine($"  {ProgramName} help <befehl>   Hilfe zu einem Befehl (auch: <befehl> --help)");
        help.WriteLine($"  {ProgramName} --version       Version");
        help.WriteLine();
        help.WriteLine("Aufträge und Verbindungen werden mit ihrem Namen oder ihrer Id angegeben.");
        help.WriteLine($"Die Daten sind die der App ({AppPaths.DataDirectory}); {AppPaths.DataDirectoryVariable}=<ordner> wählt andere.");
        help.WriteLine();
        help.Write(ExitCodeHelp());
        return help.ToString();
    }

    public static string CommandHelp(CommandSpec command)
    {
        var help = new StringWriter();
        help.WriteLine($"Aufruf: {command.Usage}");
        help.WriteLine();
        help.WriteLine($"{command.Summary}.");
        foreach (var paragraph in command.Details.Split('\n'))
        {
            help.WriteLine(paragraph);
        }

        var options = command.Options.Append(new OptionSpec("help", "Diese Hilfe", Short: "-h")).ToList();
        help.WriteLine();
        help.WriteLine("Optionen:");
        var width = options.Max(option => option.Display.Length);
        foreach (var option in options)
        {
            help.WriteLine($"  {option.Display.PadRight(width)}  {option.Help}");
        }

        help.WriteLine();
        help.Write(ExitCodeHelp());
        return help.ToString();
    }

    private static string ExitCodeHelp() =>
        """
        Exit-Codes:
            0  Erfolg (auch: Vorschau erstellt)
            1  Lauf mit Warnungen oder offenen Konflikten, oder Löschungen warten auf Bestätigung (--confirm-deletes)
            2  Lauf oder Verbindungstest fehlgeschlagen, unerwarteter Fehler
            3  Falscher Aufruf (unbekannter Befehl, unbekannte Option, fehlende Angabe)
            4  Nicht gefunden (Auftrag, Verbindung, Lauf, Datei)
            5  Abgelehnt: eine Prüfung, eine Richtlinie, der Auftrag läuft schon, Import mit abgelehnten Einträgen (Meldung auf stderr)
          130  Mit Strg+C abgebrochen

        """;
}
