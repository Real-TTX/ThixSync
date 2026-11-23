using ThixSync.Core.Business;
using ThixSync.Core.Models;

namespace ThixSync.Cli;

/// <summary>Finds what the command line names – by id or by name – through the services; <see cref="NotFoundException"/> otherwise.</summary>
internal static class Lookup
{
    /// <summary>A run may be named by the first characters of its id (at least this many; <c>runs</c> shows eight).</summary>
    public const int MinimumIdPrefix = 4;

    public static SyncJob Job(string text)
    {
        var job = JobService.FindByIdOrName(text);
        if (job is not null)
        {
            return job;
        }

        var sameName = JobService.List().Count(other => string.Equals(other.Name, text.Trim(), StringComparison.CurrentCultureIgnoreCase));
        throw new NotFoundException(sameName > 1
            ? $"Mehrere Aufträge heißen „{text}“ – bitte die Id angeben (thixsync-cli list)."
            : $"Einen Auftrag „{text}“ gibt es nicht (thixsync-cli list zeigt alle).");
    }

    public static Connection Connection(string text)
    {
        if (Guid.TryParse(text, out var id))
        {
            return ConnectionService.Find(id) ?? throw new NotFoundException($"Eine Verbindung mit der Id {id} gibt es nicht.");
        }

        var matches = ConnectionService.List().Where(connection => string.Equals(connection.Name, text.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new NotFoundException($"Eine Verbindung „{text}“ gibt es nicht (thixsync-cli connections zeigt alle)."),
            _ => throw new NotFoundException($"Mehrere Verbindungen heißen „{text}“ – bitte die Id angeben."),
        };
    }

    /// <summary>A run by its id or the first characters of it (among the newest 1000 runs).</summary>
    public static SyncRun Run(string text)
    {
        if (Guid.TryParse(text, out var id))
        {
            return ProtocolService.Find(id) ?? throw new NotFoundException($"Einen Lauf mit der Id {id} gibt es nicht.");
        }

        var prefix = text.Trim().ToLowerInvariant();
        if (prefix.Length < MinimumIdPrefix || !prefix.All(character => char.IsAsciiHexDigit(character) || character == '-'))
        {
            throw new UsageException($"„{text}“ ist keine Id eines Laufs (mindestens {MinimumIdPrefix} Zeichen, thixsync-cli runs zeigt sie).", "log");
        }

        var matches = ProtocolService.Query(null, RunFilterKind.All, RunSort.Newest, null, 0, 1000).Items
            .Where(run => run.Id.ToString("D").StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new NotFoundException($"Einen Lauf „{text}“ gibt es nicht (thixsync-cli runs zeigt die letzten)."),
            _ => throw new UsageException($"„{text}“ passt zu mehreren Läufen – bitte mehr Zeichen der Id angeben.", "log"),
        };
    }

    /// <summary>The short id the lists show (the first eight characters).</summary>
    public static string ShortId(Guid id) => id.ToString("N")[..8];
}
