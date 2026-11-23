namespace ThixSync.Core.Infrastructure;

/// <summary>
/// The time of the history and of what the window shows: the system clock, or one pinned moment (the snapshots of
/// "ThixSync --snapshot", so every picture shows the same times on every machine and every day).
/// </summary>
public static class AppClock
{
    private static DateTimeOffset? _pinned;

    public static DateTimeOffset UtcNow => _pinned?.ToUniversalTime() ?? DateTimeOffset.UtcNow;

    public static DateTimeOffset Now => _pinned?.ToLocalTime() ?? DateTimeOffset.Now;

    /// <summary>The local date (like <see cref="DateTime.Today"/>).</summary>
    public static DateTime Today => DateTime.SpecifyKind(Now.Date, DateTimeKind.Local);

    /// <summary>The moment the clock stands at (null: it runs).</summary>
    internal static DateTimeOffset? Pinned => _pinned;

    /// <summary>Stops the clock at <paramref name="moment"/>; null = the system clock again.</summary>
    internal static void Pin(DateTimeOffset? moment) => _pinned = moment;
}
