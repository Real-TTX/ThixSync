using System.Globalization;
using System.Text;

namespace ThixSync.Core.Storage.Providers.Sftp;

/// <summary>
/// The commands rsync over SSH runs on the server, and reading their output. Every path goes to the shell in single quotes
/// (POSIX: nothing inside is special; a single quote itself is closed, escaped and reopened: <c>'it'\''s'</c>), so names with
/// spaces, quotes, $, `, ;, newlines or a leading '-' are never interpreted. The commands assume a POSIX login shell (sh, bash,
/// dash, zsh, ksh) as every server with rsync has.
/// </summary>
internal static class RemoteShell
{
    /// <summary>The format of one entry: type, size, modification time (seconds with fraction), path below the start; NUL-terminated.</summary>
    public const string FindFormat = @"%y\t%s\t%T@\t%P\0";

    /// <summary>One argument for a POSIX shell, in single quotes.</summary>
    public static string Quote(string argument)
    {
        if (argument.Contains('\0'))
        {
            throw new ArgumentException("Ein Pfad darf kein NUL-Zeichen enthalten.", nameof(argument));
        }

        return "'" + argument.Replace("'", @"'\''", StringComparison.Ordinal) + "'";
    }

    /// <summary>
    /// Lists the whole tree below <paramref name="absoluteFolder"/> in one go (GNU find; BSD find has no -printf). The first record
    /// is the folder itself (empty path, <see cref="IsStartRecord"/>): the proof that find really ran – a server that only allows
    /// SFTP may answer any command with nothing, which must never look like an empty folder.
    /// </summary>
    public static string FindCommand(string absoluteFolder) =>
        $"LC_ALL=C find {Quote(absoluteFolder)} -printf {Quote(FindFormat)}";

    /// <summary>The record of the start folder itself ("d\t…\t…\t" with an empty path).</summary>
    public static bool IsStartRecord(string record)
    {
        var parts = record.Split('\t', 4);
        return parts.Length == 4 && parts[0] == "d" && parts[3].Length == 0;
    }

    /// <summary>Deletes a folder with everything in it ("--": a name starting with '-' is never an option).</summary>
    public static string RemoveTreeCommand(string absoluteFolder) => $"rm -rf -- {Quote(absoluteFolder)}";

    /// <summary>True when the error output says that find does not know -printf (BSD, busybox without it).</summary>
    public static bool FindLacksPrintf(string errorOutput) =>
        errorOutput.Contains("printf", StringComparison.OrdinalIgnoreCase)
        && (errorOutput.Contains("unknown", StringComparison.OrdinalIgnoreCase) || errorOutput.Contains("invalid", StringComparison.OrdinalIgnoreCase)
            || errorOutput.Contains("unrecognized", StringComparison.OrdinalIgnoreCase) || errorOutput.Contains("illegal", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// One record of <see cref="FindFormat"/> ("f\t1234\t1700000000.5\tFotos/a b.jpg") as an entry below <paramref name="folder"/>;
    /// null for links, devices, sockets, temporary files and anything unreadable. The path is the last field: tabs in names are fine.
    /// </summary>
    public static StorageEntry? ParseFindRecord(string record, string folder)
    {
        var parts = record.Split('\t', 4);
        if (parts.Length != 4 || parts[3].Length == 0)
        {
            return null;
        }

        var isFolder = parts[0] == "d";
        if (!isFolder && parts[0] != "f")
        {
            return null; // links are skipped like on a local drive; devices, pipes and sockets are no files
        }

        var relative = parts[3];
        var name = relative[(relative.LastIndexOf('/') + 1)..];
        if (Network.ServerPaths.IsHiddenName(name) || relative.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
        {
            return null;
        }

        long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
        var modified = ParseTime(parts[2]);
        var path = folder.Length == 0 ? relative : folder + "/" + relative;
        return new StorageEntry(path, isFolder, isFolder ? 0 : size, modified) { IsHidden = name.StartsWith('.') };
    }

    /// <summary>"1700000000.1234567890" (seconds since 1970 with up to nine decimals) → UTC, to the 100 ns .NET keeps.</summary>
    public static DateTimeOffset ParseTime(string text)
    {
        var dot = text.IndexOf('.');
        var secondsText = dot < 0 ? text : text[..dot];
        if (!long.TryParse(secondsText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds))
        {
            return DateTimeOffset.UnixEpoch;
        }

        var time = DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (dot < 0)
        {
            return time;
        }

        var fraction = text[(dot + 1)..].PadRight(7, '0')[..7];
        return int.TryParse(fraction, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ? time.AddTicks(ticks) : time;
    }

    /// <summary>Splits a stream of NUL-terminated UTF-8 records; the incomplete rest stays in <paramref name="pending"/>.</summary>
    public static IEnumerable<string> TakeRecords(List<byte> pending)
    {
        var start = 0;
        var records = new List<string>();
        for (var index = 0; index < pending.Count; index++)
        {
            if (pending[index] != 0)
            {
                continue;
            }

            records.Add(Encoding.UTF8.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pending).Slice(start, index - start)));
            start = index + 1;
        }

        pending.RemoveRange(0, start);
        return records;
    }
}
