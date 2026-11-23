using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace ThixSync.Core.Storage.Providers.OAuth;

/// <summary>Reading the JSON of the cloud APIs without reflection (the Windows build is trimmed) and writing their time stamps.</summary>
internal static class CloudJson
{
    public static string Text(JsonNode? node, string name) =>
        node?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    public static long Number(JsonNode? node, string name)
    {
        if (node?[name] is not JsonValue value)
        {
            return 0;
        }

        if (value.TryGetValue<long>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    public static bool Flag(JsonNode? node, string name) => node?[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    /// <summary>A time stamp (RFC 3339) as UTC; <see cref="DateTimeOffset.MinValue"/> when it is missing.</summary>
    public static DateTimeOffset Time(JsonNode? node, string name)
    {
        var text = Text(node, name);
        return text.Length > 0 && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
            ? time.ToUniversalTime()
            : DateTimeOffset.MinValue;
    }

    public static IEnumerable<JsonNode> Items(JsonNode? node, string name) =>
        node?[name] is JsonArray array ? array.Where(item => item is not null).Select(item => item!) : [];

    /// <summary>RFC 3339 with milliseconds ("2026-11-23T10:15:00.123Z").</summary>
    public static string Rfc3339(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>RFC 3339 in whole seconds ("2026-11-23T10:15:00Z": Dropbox and Box take no fractions).</summary>
    public static string Rfc3339Seconds(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// JSON that may go into an HTTP header (Dropbox-API-Arg): every character outside printable ASCII as \uXXXX (UTF-16 units; a
    /// surrogate pair as two escapes). Headers only carry ASCII safely.
    /// </summary>
    public static string HeaderSafe(string json)
    {
        var builder = new StringBuilder(json.Length + 16);
        foreach (var character in json)
        {
            if (character is >= ' ' and <= '~')
            {
                builder.Append(character);
                continue;
            }

            builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}

/// <summary>
/// Whether a changed item lies below the root of a storage, walking up its parents (with a memory of the parents seen, a limit of
/// lookups and depth: beyond them the answer is "yes" – a needless run is better than a missed change).
/// </summary>
internal sealed class CloudAncestry(string rootId, Func<string, CancellationToken, Task<IReadOnlyList<string>?>> parentsOf)
{
    private const int MaxLookups = 200;
    private const int MaxDepth = 64;

    private readonly Dictionary<string, IReadOnlyList<string>?> _parents = new(StringComparer.Ordinal);
    private int _lookups;

    /// <summary>True when an item with these parents is the root or below it.</summary>
    public async Task<bool> IsBelowRootAsync(string itemId, IReadOnlyList<string> parentIds, CancellationToken cancellationToken)
    {
        if (itemId == rootId)
        {
            return true;
        }

        var level = parentIds;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            if (level.Count == 0)
            {
                return false;
            }

            if (level.Contains(rootId))
            {
                return true;
            }

            var next = new List<string>();
            foreach (var parent in level.Where(seen.Add))
            {
                if (!_parents.TryGetValue(parent, out var grandParents))
                {
                    if (++_lookups > MaxLookups)
                    {
                        return true;
                    }

                    grandParents = await parentsOf(parent, cancellationToken);
                    _parents[parent] = grandParents;
                }

                if (grandParents is null)
                {
                    return true; // unknown (gone meanwhile): count it as a change
                }

                next.AddRange(grandParents);
            }

            level = next;
        }

        return true;
    }
}
