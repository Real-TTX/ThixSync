namespace ThixSync.Core.Models;

/// <summary>
/// Every kind of storage ThixSync reads and writes. Each one can be the source AND the target of a job, in any combination.
/// The numbers are stored (database, cloud): never change or reuse one.
/// </summary>
public enum ConnectionKind
{
    // ---- Local & network ----
    Local = 0,
    ExternalDrive = 1,
    Smb = 2,
    Nfs = 3,
    Synology = 4,
    Qnap = 5,

    // ---- Servers & protocols ----
    Sftp = 20,
    Ftp = 21,
    WebDav = 22,
    RsyncSsh = 23,
    HetznerStorageBox = 24,

    // ---- Cloud storage ----
    S3 = 40,
    GoogleDrive = 41,
    OneDrive = 42,
    SharePoint = 43,
    Dropbox = 44,
    Nextcloud = 45,
    BackblazeB2 = 46,
    Wasabi = 47,
    AzureBlob = 48,
    GoogleCloudStorage = 49,
    Mega = 50,
    PCloud = 51,
    Box = 52,
    Seafile = 53,

    // ---- ThixApps ----
    ThixSyncRemote = 70,
    MatCloud = 71,
}

/// <summary>The groups of the connection types (the type picker shows them in this order).</summary>
public enum ConnectionGroup
{
    LocalAndNetwork = 0,
    Servers = 1,
    Cloud = 2,
    ThixApps = 3,
}

/// <summary>Whether a row still has to go to the cloud.</summary>
public enum SyncState
{
    Synced = 0,
    Pending = 1,

    /// <summary>The cloud refused it: it stays on this computer only.</summary>
    LocalOnly = 2,
}

/// <summary>
/// A saved place ThixSync can reach (the page "Geräte"): this computer, a USB drive, a NAS, a server, a cloud account, another
/// computer with ThixSync, the MatCloud. A job names a connection for its source and one for its target, plus the folders.
///
/// <see cref="Settings"/> holds the fields of the type (<see cref="Storage.ConnectionCatalog"/>: host, port, bucket ...). The one
/// secret of a connection (password, secret key, refresh token, pairing key) never lives in here and never leaves the computer:
/// it is kept protected (DPAPI / Keychain) by <see cref="Business.ConnectionService"/>; <see cref="HasSecret"/> says it is there.
/// Connections are kept the same on every device of a cloud account (without the secret). A local connection (this computer,
/// an external drive) belongs to the computer in <see cref="DeviceName"/>.
/// </summary>
public sealed record Connection
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = "";

    public ConnectionKind Kind { get; init; }

    /// <summary>The fields of the type, by <see cref="Storage.ConnectionField.Key"/>. Never a secret.</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The computer a local connection belongs to (empty for everything that is reachable from anywhere).</summary>
    public string DeviceName { get; init; } = "";

    /// <summary>The secret is kept on this computer (protected, never synced).</summary>
    public bool HasSecret { get; init; }

    public DateTimeOffset CreateDate { get; init; }

    /// <summary>The last change (the cloud: the last writer wins by this).</summary>
    public DateTimeOffset UpdateDate { get; init; }

    public SyncState SyncState { get; init; }

    /// <summary>A field of the type (empty when it is not set).</summary>
    public string Get(string key) => Settings.TryGetValue(key, out var value) ? value : "";

    /// <summary>A copy with one field changed.</summary>
    public Connection With(string key, string value)
    {
        var settings = new Dictionary<string, string>(Settings, StringComparer.Ordinal) { [key] = value };
        return this with { Settings = settings };
    }
}
