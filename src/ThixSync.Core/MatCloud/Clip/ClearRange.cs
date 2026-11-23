// MatCloud.Contract 1.7.0 – a copy from MatCloud, do not edit here
namespace MatCloud.Contract.Clip;

/// <summary>
/// "Verlauf löschen" as in a browser: what was copied within the range goes, favourites and pinned entries stay. The
/// desktop app and the browser offer the same ranges. Not part of the sync protocol: a deletion travels as tombstones.
/// </summary>
public enum ClearRange
{
    LastHour = 0,
    Last24Hours = 1,
    Last7Days = 2,
    Last4Weeks = 3,
    All = 4,
}

public static class ClearRanges
{
    /// <summary>The choices in the order they are offered (shortest first).</summary>
    public static readonly IReadOnlyList<ClearRange> Choices = [ClearRange.LastHour, ClearRange.Last24Hours, ClearRange.Last7Days, ClearRange.Last4Weeks, ClearRange.All];

    public static bool IsValid(ClearRange range) => Enum.IsDefined(range);

    /// <summary>The name of a range as both UIs show it.</summary>
    public static string Text(ClearRange range) => range switch
    {
        ClearRange.LastHour => "Letzte Stunde",
        ClearRange.Last24Hours => "Letzte 24 Stunden",
        ClearRange.Last7Days => "Letzte 7 Tage",
        ClearRange.Last4Weeks => "Letzte 4 Wochen",
        _ => "Gesamte Zeit",
    };

    /// <summary>The first moment of the range: entries last copied at or after it go. Null for <see cref="ClearRange.All"/>.</summary>
    public static DateTimeOffset? Start(ClearRange range, DateTimeOffset now) => range switch
    {
        ClearRange.LastHour => now.AddHours(-1),
        ClearRange.Last24Hours => now.AddHours(-24),
        ClearRange.Last7Days => now.AddDays(-7),
        ClearRange.Last4Weeks => now.AddDays(-28),
        _ => null,
    };
}
