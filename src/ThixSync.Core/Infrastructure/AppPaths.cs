namespace ThixSync.Core.Infrastructure;

/// <summary>
/// Where ThixSync keeps its files. Program files and user data are separate: updates and uninstalling never touch the
/// user data unless the user decides so. Connections, jobs and the protocol live in sync.db; the state of each job (what both sides had after the last run)
/// so the cloud gets a smaller copy of each (see Business.SyncService).
///   Windows: %LocalAppData%\ThixSync (database, job states, settings, updates, logs)
///   macOS:   ~/Library/Application Support/ThixSync
/// </summary>
public static class AppPaths
{
    /// <summary>Developers can point ThixSync to a separate folder (sample data, screenshots) with this variable.</summary>
    public const string DataDirectoryVariable = "THIXSYNC_DATA_DIR";

    private static string? _dataDirectoryOverride = NullIfEmpty(Environment.GetEnvironmentVariable(DataDirectoryVariable));

    public static string DataDirectory => _dataDirectoryOverride ?? Path.Combine(UserFolder(), AppInfo.ProductName);

    /// <summary>Connections, jobs, runs (SQLite).</summary>
    public static string DatabaseFile => Path.Combine(DataDirectory, "sync.db");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string UpdateDirectory => Path.Combine(DataDirectory, "updates");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Where SMB shares are mounted on macOS when ThixSync mounts them itself.</summary>
    public static string MountDirectory => Path.Combine(DataDirectory, "mounts");

    /// <summary>The state of each job (what both sides had after its last run): one SQLite file per job, <c>state/&lt;job&gt;.db</c>.</summary>
    public static string StateDirectory => Path.Combine(DataDirectory, "state");

    /// <summary>The certificate and keys of ThixSync Remote (this computer's identity for other computers).</summary>
    public static string RemoteDirectory => Path.Combine(DataDirectory, "remote");

    /// <summary>True when a developer or a test pointed ThixSync to its own folder: it is then its own instance.</summary>
    public static bool HasOverride => _dataDirectoryOverride is not null;

    /// <summary>Tests use a temporary folder.</summary>
    internal static void UseDirectoryForTests(string? directory) => _dataDirectoryOverride = directory;

    private static string UserFolder() => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
        : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
