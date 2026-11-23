using ThixSync.Core.Models;

namespace ThixSync.Core.Storage;

/// <summary>The kind of input of a field of a connection (the form draws it).</summary>
public enum FieldType
{
    Text = 0,
    Number = 1,
    Choice = 2,
    Toggle = 3,

    /// <summary>A folder of this computer (with "Durchsuchen …").</summary>
    LocalFolder = 4,

    /// <summary>A file of this computer (SSH key).</summary>
    LocalFile = 5,

    /// <summary>An address (https://…).</summary>
    Url = 6,

    /// <summary>Filled by the app (the signed-in account of an OAuth connection, the drive's id); shown read-only.</summary>
    ReadOnly = 7,
}

/// <summary>Where a field sits in the connection form (tabs).</summary>
public enum FieldTab
{
    General = 0,
    SignIn = 1,
    Advanced = 2,
}

/// <summary>How a connection signs in.</summary>
public enum AuthKind
{
    /// <summary>Nothing (this computer, a drive).</summary>
    None = 0,

    /// <summary>User name + password (the password is the secret).</summary>
    Password = 1,

    /// <summary>Key id + secret key (S3 family, Azure).</summary>
    AccessKey = 2,

    /// <summary>Sign-in in the browser (OAuth 2 with PKCE); the refresh token is the secret.</summary>
    OAuth = 3,

    /// <summary>A pairing code of the other computer (ThixSync Remote); the derived key is the secret.</summary>
    Pairing = 4,

    /// <summary>The MatCloud sign-in of the app (Einstellungen › Cloud); no secret of its own.</summary>
    MatCloud = 5,
}

public sealed record FieldChoice(string Value, string Label);

/// <summary>
/// One field of a connection type. <see cref="VisibleWhenKey"/>/<see cref="VisibleWhenValues"/> hide the field when another field
/// has a different value (the form shows only what makes sense, e.g. the key file only for key sign-in).
/// </summary>
public sealed record ConnectionField(string Key, string Label, FieldType Type)
{
    public bool Required { get; init; }

    public string Default { get; init; } = "";

    public string Placeholder { get; init; } = "";

    public string Help { get; init; } = "";

    public IReadOnlyList<FieldChoice> Choices { get; init; } = [];

    public FieldTab Tab { get; init; } = FieldTab.General;

    public string? VisibleWhenKey { get; init; }

    public IReadOnlyList<string> VisibleWhenValues { get; init; } = [];

    /// <summary>Whether the field applies with these settings.</summary>
    public bool IsVisible(IReadOnlyDictionary<string, string> settings, IReadOnlyList<ConnectionField> fields)
    {
        if (VisibleWhenKey is null)
        {
            return true;
        }

        var value = settings.TryGetValue(VisibleWhenKey, out var set) && set.Length > 0
            ? set
            : fields.FirstOrDefault(field => field.Key == VisibleWhenKey)?.Default ?? "";
        return VisibleWhenValues.Contains(value, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>A type of connection: name, group, icon, sign-in and fields (the type picker and the form are drawn from this).</summary>
public sealed record ConnectionType(ConnectionKind Kind, string Name, ConnectionGroup Group, string Icon, string Description)
{
    public AuthKind Auth { get; init; } = AuthKind.None;

    /// <summary>The label of the secret ("Passwort", "Secret Access Key" …); null: no secret field.</summary>
    public string? SecretLabel { get; init; }

    /// <summary>The secret field only shows when this field has one of these values (e.g. password only for password sign-in).</summary>
    public string? SecretVisibleWhenKey { get; init; }

    public IReadOnlyList<string> SecretVisibleWhenValues { get; init; } = [];

    /// <summary>The secret may be empty (an SSH key without passphrase, an anonymous FTP).</summary>
    public bool SecretOptional { get; init; }

    /// <summary>An optional secret is required after all when this field has one of <see cref="SecretRequiredWhenValues"/> (SSH: password sign-in).</summary>
    public string? SecretRequiredWhenKey { get; init; }

    public IReadOnlyList<string> SecretRequiredWhenValues { get; init; } = [];

    public IReadOnlyList<ConnectionField> Fields { get; init; } = [];

    /// <summary>Belongs to one computer (this computer, an external drive): the folders are only reachable there.</summary>
    public bool IsBoundToDevice { get; init; }

    /// <summary>How the folders of a pair are written for this type (hint in the form): "C:\Users\…", "/backup/Fotos" …</summary>
    public string FolderHint { get; init; } = "/Ordner";

    /// <summary>The providers of this type are not ready on this platform (shown, but not selectable).</summary>
    public bool IsAvailable { get; init; } = true;

    /// <summary>Whether a secret must be given with these settings.</summary>
    public bool IsSecretRequired(IReadOnlyDictionary<string, string> settings)
    {
        if (!IsSecretVisible(settings))
        {
            return false;
        }

        if (!SecretOptional)
        {
            return true;
        }

        if (SecretRequiredWhenKey is null)
        {
            return false;
        }

        var value = settings.TryGetValue(SecretRequiredWhenKey, out var set) && set.Length > 0
            ? set
            : Fields.FirstOrDefault(field => field.Key == SecretRequiredWhenKey)?.Default ?? "";
        return SecretRequiredWhenValues.Contains(value, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsSecretVisible(IReadOnlyDictionary<string, string> settings)
    {
        if (SecretLabel is null)
        {
            return false;
        }

        if (SecretVisibleWhenKey is null)
        {
            return true;
        }

        var value = settings.TryGetValue(SecretVisibleWhenKey, out var set) && set.Length > 0
            ? set
            : Fields.FirstOrDefault(field => field.Key == SecretVisibleWhenKey)?.Default ?? "";
        return SecretVisibleWhenValues.Contains(value, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>The keys of the fields (stored in <see cref="Connection.Settings"/> and synced: never rename one).</summary>
public static class ConnectionFields
{
    public const string Host = "host";
    public const string Port = "port";
    public const string UserName = "user";
    public const string Domain = "domain";
    public const string Share = "share";
    public const string BasePath = "base";
    public const string Url = "url";
    public const string Protocol = "protocol";
    public const string AuthMethod = "auth";
    public const string PrivateKeyFile = "keyFile";
    public const string HostKey = "hostKey";
    public const string Encryption = "encryption";
    public const string Passive = "passive";
    public const string CertificateHash = "certificate";
    public const string Endpoint = "endpoint";
    public const string Region = "region";
    public const string Bucket = "bucket";
    public const string AccessKeyId = "accessKeyId";
    public const string PathStyle = "pathStyle";
    public const string StorageClass = "storageClass";
    public const string AccountName = "account";
    public const string Container = "container";
    public const string SiteUrl = "site";
    public const string Library = "library";
    public const string SharedDrive = "sharedDrive";
    public const string SignedInAs = "signedInAs";
    public const string VolumeLabel = "volumeLabel";
    public const string VolumeId = "volumeId";
    public const string NfsVersion = "nfsVersion";
    public const string RemoteDevice = "remoteDevice";
    public const string RemoteShare = "remoteShare";
    public const string Fingerprint = "fingerprint";
    public const string RsyncPath = "rsyncPath";
}

/// <summary>
/// Every connection type ThixSync knows, with its fields. The UI draws the type picker and the form from here; the business layer
/// validates against it; the providers read the same keys.
/// </summary>
public static class ConnectionCatalog
{
    private static readonly ConnectionField BasePath = new(ConnectionFields.BasePath, "Basisordner", FieldType.Text)
    {
        Placeholder = "/",
        Help = "Alle Ordner der Aufträge liegen unter diesem Ordner (leer: ganz oben).",
        Tab = FieldTab.Advanced,
    };

    private static readonly ConnectionField UserName = new(ConnectionFields.UserName, "Benutzername", FieldType.Text) { Required = true, Tab = FieldTab.SignIn };

    private static readonly ConnectionField SignedInAs = new(ConnectionFields.SignedInAs, "Angemeldet als", FieldType.ReadOnly) { Tab = FieldTab.SignIn };

    private static readonly IReadOnlyList<FieldChoice> SshAuthChoices = [new("password", "Passwort"), new("key", "Schlüsseldatei")];

    private static ConnectionField Host(string placeholder, string label = "Server") =>
        new(ConnectionFields.Host, label, FieldType.Text) { Required = true, Placeholder = placeholder };

    private static ConnectionField Port(int port) =>
        new(ConnectionFields.Port, "Port", FieldType.Number) { Default = port.ToString(System.Globalization.CultureInfo.InvariantCulture), Tab = FieldTab.Advanced };

    private static ConnectionField[] SshSignIn() =>
    [
        new(ConnectionFields.AuthMethod, "Anmeldung mit", FieldType.Choice) { Default = "password", Choices = SshAuthChoices, Tab = FieldTab.SignIn },
        new(ConnectionFields.PrivateKeyFile, "Schlüsseldatei", FieldType.LocalFile)
        {
            Required = true,
            Placeholder = "~/.ssh/id_ed25519",
            Tab = FieldTab.SignIn,
            VisibleWhenKey = ConnectionFields.AuthMethod,
            VisibleWhenValues = ["key"],
        },
        new(ConnectionFields.HostKey, "Fingerabdruck des Servers", FieldType.ReadOnly)
        {
            Help = "Wird bei der ersten Verbindung gemerkt; ändert er sich, verbindet ThixSync nicht mehr (Schutz vor fremden Servern).",
            Tab = FieldTab.Advanced,
        },
    ];

    private static readonly ConnectionField[] NasProtocol =
    [
        new(ConnectionFields.Protocol, "Protokoll", FieldType.Choice)
        {
            Default = "smb",
            Choices = [new("smb", "SMB (Netzwerkfreigabe)"), new("webdav", "WebDAV (HTTPS)"), new("sftp", "SFTP (SSH)")],
        },
        new(ConnectionFields.Share, "Freigabe", FieldType.Text)
        {
            Required = true,
            Placeholder = "home",
            VisibleWhenKey = ConnectionFields.Protocol,
            VisibleWhenValues = ["smb"],
        },
        new(ConnectionFields.Port, "Port", FieldType.Number)
        {
            Placeholder = "Standard des Protokolls",
            Tab = FieldTab.Advanced,
            VisibleWhenKey = ConnectionFields.Protocol,
            VisibleWhenValues = ["webdav", "sftp"],
        },
    ];

    private static ConnectionField Bucket(string label = "Bucket") => new(ConnectionFields.Bucket, label, FieldType.Text) { Required = true };

    private static ConnectionField KeyId(string label) => new(ConnectionFields.AccessKeyId, label, FieldType.Text) { Required = true, Tab = FieldTab.SignIn };

    private static readonly ConnectionField Prefix = BasePath with { Label = "Präfix (Ordner im Bucket)" };

    public static IReadOnlyList<ConnectionType> All { get; } =
    [
        // ---- Local & network ------------------------------------------------------------------------------------------
        new(ConnectionKind.Local, "Dieser Computer", ConnectionGroup.LocalAndNetwork, "monitor", "Ordner auf diesem Windows-PC oder Mac")
        {
            IsBoundToDevice = true,
            FolderHint = OperatingSystem.IsWindows() ? @"C:\Users\…\Dokumente" : "/Users/…/Documents",
        },
        new(ConnectionKind.ExternalDrive, "Externes Laufwerk", ConnectionGroup.LocalAndNetwork, "hard-drive", "USB-Stick oder externe Festplatte – erkannt am Laufwerk, nicht am Buchstaben")
        {
            IsBoundToDevice = true,
            FolderHint = "/Backup",
            Fields =
            [
                new(ConnectionFields.VolumeLabel, "Laufwerk", FieldType.Choice)
                {
                    Required = true,
                    Help = "Das Laufwerk wird an seiner Kennung erkannt – egal unter welchem Buchstaben oder Pfad es gerade hängt.",
                },
                new(ConnectionFields.VolumeId, "Kennung", FieldType.ReadOnly) { Tab = FieldTab.Advanced },
            ],
        },
        new(ConnectionKind.Smb, "SMB / CIFS", ConnectionGroup.LocalAndNetwork, "network", "Windows-Freigabe, Samba, NAS über SMB")
        {
            Auth = AuthKind.Password,
            SecretLabel = "Passwort",
            SecretOptional = true,
            FolderHint = "/Ordner in der Freigabe",
            Fields =
            [
                Host("nas.local oder 192.168.1.10"),
                new(ConnectionFields.Share, "Freigabe", FieldType.Text) { Required = true, Placeholder = "daten" },
                UserName with { Required = false, Help = "Leer: die Anmeldung dieses Computers." },
                new(ConnectionFields.Domain, "Domäne", FieldType.Text) { Tab = FieldTab.SignIn, Placeholder = "WORKGROUP" },
                BasePath,
            ],
        },
        new(ConnectionKind.Nfs, "NFS", ConnectionGroup.LocalAndNetwork, "server", "Unix-Netzwerkdateisystem (Linux-Server, NAS)")
        {
            FolderHint = "/Ordner im Export",
            Fields =
            [
                Host("nas.local"),
                new(ConnectionFields.Share, "Export", FieldType.Text) { Required = true, Placeholder = "/volume1/daten" },
                new(ConnectionFields.NfsVersion, "NFS-Version", FieldType.Choice)
                {
                    Default = "auto",
                    Choices = [new("auto", "Automatisch"), new("3", "NFSv3"), new("4", "NFSv4")],
                    Tab = FieldTab.Advanced,
                },
                BasePath,
            ],
        },
        new(ConnectionKind.Synology, "Synology NAS", ConnectionGroup.LocalAndNetwork, "database", "DiskStation über SMB, WebDAV oder SFTP")
        {
            Auth = AuthKind.Password,
            SecretLabel = "Passwort",
            Fields = [Host("diskstation.local"), .. NasProtocol, UserName, BasePath],
        },
        new(ConnectionKind.Qnap, "QNAP NAS", ConnectionGroup.LocalAndNetwork, "database", "QNAP über SMB, WebDAV oder SFTP")
        {
            Auth = AuthKind.Password,
            SecretLabel = "Passwort",
            Fields = [Host("qnap.local"), .. NasProtocol, UserName, BasePath],
        },

        // ---- Servers & protocols --------------------------------------------------------------------------------------
        new(ConnectionKind.Sftp, "SFTP / SSH", ConnectionGroup.Servers, "terminal", "Jeder SSH-Server (Linux, NAS, Hosting)")
        {
            Auth = AuthKind.Password,
            SecretLabel = "Passwort / Passphrase",
            SecretOptional = true,
            SecretRequiredWhenKey = ConnectionFields.AuthMethod,
            SecretRequiredWhenValues = ["password"],
            Fields = [Host("server.example.com"), Port(22), UserName, .. SshSignIn(), BasePath],
        },
        new(ConnectionKind.Ftp, "FTP / FTPS", ConnectionGroup.Servers, "upload", "FTP-Server, auf Wunsch verschlüsselt (TLS)")
        {
            Auth = AuthKind.Password,
            SecretLabel = "Passwort",
            SecretOptional = true,
            Fields =
            [
                Host("ftp.example.com"),
                new(ConnectionFields.Encryption, "Verschlüsselung", FieldType.Choice)
                {
                    Default = "explicit",
                    Choices = [new("explicit", "FTPS explizit (empfohlen)"), new("implicit", "FTPS implizit"), new("none", "Keine (unsicher)")],
                },
                Port(21),
                UserName with { Help = "Leer: anonym." , Required = false },
                new(ConnectionFields.Passive, "Passiver Modus", FieldType.Toggle) { Default = "true", Tab = FieldTab.Advanced },
                new(ConnectionFields.CertificateHash, "Zertifikat", FieldType.ReadOnly)
                {
                    Help = "Ein selbst signiertes Zertifikat wird bei der ersten Verbindung bestätigt und gemerkt.",
                    Tab = FieldTab.Advanced,
                },
                BasePath,
            ],
        },
        new(ConnectionKind.WebDav, "WebDAV / HTTPS", ConnectionGroup.Servers, "globe", "Jeder WebDAV-Server")
        {
            Auth = AuthKind.Password,
            SecretLabel = "Passwort",
            SecretOptional = true,
            Fields =
            [
                new(ConnectionFields.Url, "Adresse", FieldType.Url) { Required = true, Placeholder = "https://dav.example.com/files/" },
                UserName with { Required = false },
                BasePath,
            ],
        },
        new(ConnectionKind.RsyncSsh, "rsync über SSH", ConnectionGroup.Servers, "route", "SSH-Server mit rsync: schnelles Auflisten und Prüfsummen auf dem Server")
        {
            Auth = AuthKind.Password,
            SecretLabel = "Passwort / Passphrase",
            SecretOptional = true,
            SecretRequiredWhenKey = ConnectionFields.AuthMethod,
            SecretRequiredWhenValues = ["password"],
            Fields =
            [
                Host("server.example.com"), Port(22), UserName, .. SshSignIn(),
                new(ConnectionFields.RsyncPath, "rsync auf dem Server", FieldType.Text) { Placeholder = "rsync", Tab = FieldTab.Advanced },
                BasePath,
            ],
        },
        new(ConnectionKind.HetznerStorageBox, "Hetzner Storage Box", ConnectionGroup.Servers, "box", "Storage Box über SFTP (Port 23) oder WebDAV")
        {
            Auth = AuthKind.Password,
            SecretLabel = "Passwort",
            Fields =
            [
                new(ConnectionFields.UserName, "Benutzername", FieldType.Text)
                {
                    Required = true,
                    Placeholder = "u123456 oder u123456-sub1",
                    Help = "Der Server ergibt sich daraus: u123456.your-storagebox.de (Unterkonto: u123456-sub1.your-storagebox.de).",
                },
                new(ConnectionFields.Protocol, "Protokoll", FieldType.Choice)
                {
                    Default = "sftp",
                    Choices = [new("sftp", "SFTP (Port 23, empfohlen)"), new("webdav", "WebDAV (HTTPS)")],
                },
                new(ConnectionFields.Host, "Server", FieldType.Text) { Placeholder = "automatisch", Tab = FieldTab.Advanced },
                BasePath,
            ],
        },

        // ---- Cloud storage --------------------------------------------------------------------------------------------
        new(ConnectionKind.S3, "Amazon S3 / S3-kompatibel", ConnectionGroup.Cloud, "cloud", "AWS S3, MinIO, Ceph, IONOS, Cloudflare R2 …")
        {
            Auth = AuthKind.AccessKey,
            SecretLabel = "Secret Access Key",
            FolderHint = "/Präfix",
            Fields =
            [
                Bucket(),
                new(ConnectionFields.Region, "Region", FieldType.Text) { Default = "eu-central-1", Placeholder = "eu-central-1" },
                new(ConnectionFields.Endpoint, "Endpunkt", FieldType.Url)
                {
                    Placeholder = "leer für AWS · https://minio.example.com",
                    Help = "Nur für S3-kompatible Anbieter.",
                    Tab = FieldTab.Advanced,
                },
                KeyId("Access Key ID"),
                new(ConnectionFields.PathStyle, "Pfad-Adressierung (MinIO)", FieldType.Toggle) { Default = "false", Tab = FieldTab.Advanced },
                new(ConnectionFields.StorageClass, "Speicherklasse", FieldType.Choice)
                {
                    Default = "STANDARD",
                    Choices = [new("STANDARD", "Standard"), new("STANDARD_IA", "Standard-IA"), new("INTELLIGENT_TIERING", "Intelligent-Tiering"), new("GLACIER_IR", "Glacier Instant Retrieval")],
                    Tab = FieldTab.Advanced,
                },
                Prefix,
            ],
        },
        new(ConnectionKind.GoogleDrive, "Google Drive", ConnectionGroup.Cloud, "cloud", "Meine Ablage oder eine geteilte Ablage")
        {
            Auth = AuthKind.OAuth,
            Fields =
            [
                SignedInAs,
                new(ConnectionFields.SharedDrive, "Geteilte Ablage", FieldType.Text) { Placeholder = "leer: Meine Ablage", Tab = FieldTab.Advanced },
                BasePath,
            ],
        },
        new(ConnectionKind.OneDrive, "Microsoft OneDrive", ConnectionGroup.Cloud, "cloud", "OneDrive privat oder OneDrive for Business")
        {
            Auth = AuthKind.OAuth,
            Fields = [SignedInAs, BasePath],
        },
        new(ConnectionKind.SharePoint, "SharePoint", ConnectionGroup.Cloud, "users", "Dokumentbibliothek einer SharePoint-Website (Microsoft 365)")
        {
            Auth = AuthKind.OAuth,
            Fields =
            [
                SignedInAs,
                new(ConnectionFields.SiteUrl, "Website", FieldType.Url) { Required = true, Placeholder = "https://firma.sharepoint.com/sites/Team" },
                new(ConnectionFields.Library, "Bibliothek", FieldType.Text) { Default = "Dokumente", Placeholder = "Dokumente" },
                BasePath,
            ],
        },
        new(ConnectionKind.Dropbox, "Dropbox", ConnectionGroup.Cloud, "box", "Dropbox Basic, Plus und Business")
        {
            Auth = AuthKind.OAuth,
            Fields = [SignedInAs, BasePath],
        },
        new(ConnectionKind.Nextcloud, "Nextcloud / ownCloud", ConnectionGroup.Cloud, "cloud", "Über WebDAV, mit App-Passwort")
        {
            Auth = AuthKind.Password,
            SecretLabel = "App-Passwort",
            Fields =
            [
                new(ConnectionFields.Url, "Server", FieldType.Url) { Required = true, Placeholder = "https://cloud.example.com" },
                UserName,
                BasePath,
            ],
        },
        new(ConnectionKind.BackblazeB2, "Backblaze B2", ConnectionGroup.Cloud, "archive", "B2 Cloud Storage (S3-kompatible Schnittstelle)")
        {
            Auth = AuthKind.AccessKey,
            SecretLabel = "Application Key",
            FolderHint = "/Präfix",
            Fields =
            [
                Bucket(),
                new(ConnectionFields.Region, "Region", FieldType.Text) { Required = true, Default = "eu-central-003", Placeholder = "eu-central-003" },
                KeyId("Key ID"),
                Prefix,
            ],
        },
        new(ConnectionKind.Wasabi, "Wasabi", ConnectionGroup.Cloud, "archive", "Wasabi Hot Cloud Storage")
        {
            Auth = AuthKind.AccessKey,
            SecretLabel = "Secret Key",
            FolderHint = "/Präfix",
            Fields =
            [
                Bucket(),
                new(ConnectionFields.Region, "Region", FieldType.Choice)
                {
                    Default = "eu-central-1",
                    Choices =
                    [
                        new("eu-central-1", "Amsterdam (eu-central-1)"), new("eu-central-2", "Frankfurt (eu-central-2)"), new("eu-west-1", "London (eu-west-1)"),
                        new("eu-west-2", "Paris (eu-west-2)"), new("us-east-1", "N. Virginia (us-east-1)"), new("us-east-2", "N. Virginia (us-east-2)"),
                        new("us-central-1", "Texas (us-central-1)"), new("us-west-1", "Oregon (us-west-1)"), new("ap-northeast-1", "Tokio (ap-northeast-1)"),
                    ],
                },
                KeyId("Access Key"),
                Prefix,
            ],
        },
        new(ConnectionKind.AzureBlob, "Azure Blob Storage", ConnectionGroup.Cloud, "cloud", "Container eines Azure-Speicherkontos")
        {
            Auth = AuthKind.AccessKey,
            SecretLabel = "Kontoschlüssel oder SAS-Token",
            FolderHint = "/Präfix",
            Fields =
            [
                new(ConnectionFields.AccountName, "Speicherkonto", FieldType.Text) { Required = true, Placeholder = "meinkonto" },
                new(ConnectionFields.Container, "Container", FieldType.Text) { Required = true, Placeholder = "backup" },
                new(ConnectionFields.AuthMethod, "Anmeldung mit", FieldType.Choice)
                {
                    Default = "key",
                    Choices = [new("key", "Kontoschlüssel"), new("sas", "SAS-Token")],
                    Tab = FieldTab.SignIn,
                },
                new(ConnectionFields.Endpoint, "Endpunkt", FieldType.Url) { Placeholder = "leer: https://<konto>.blob.core.windows.net", Tab = FieldTab.Advanced },
                Prefix,
            ],
        },
        new(ConnectionKind.GoogleCloudStorage, "Google Cloud Storage", ConnectionGroup.Cloud, "cloud", "Bucket über die S3-kompatible XML-Schnittstelle (HMAC-Schlüssel)")
        {
            Auth = AuthKind.AccessKey,
            SecretLabel = "HMAC-Secret",
            FolderHint = "/Präfix",
            Fields = [Bucket(), KeyId("HMAC-Zugriffsschlüssel"), Prefix],
        },
        new(ConnectionKind.Mega, "MEGA", ConnectionGroup.Cloud, "lock", "Ende-zu-Ende-verschlüsselter Cloud-Speicher")
        {
            Auth = AuthKind.Password,
            SecretLabel = "Passwort",
            Fields = [UserName with { Label = "E-Mail" }, BasePath],
        },
        new(ConnectionKind.PCloud, "pCloud", ConnectionGroup.Cloud, "cloud", "pCloud (Rechenzentrum EU oder USA)")
        {
            Auth = AuthKind.Password,
            SecretLabel = "Passwort",
            Fields =
            [
                new(ConnectionFields.Region, "Rechenzentrum", FieldType.Choice) { Default = "eu", Choices = [new("eu", "Europa"), new("us", "USA")] },
                UserName with { Label = "E-Mail" },
                BasePath,
            ],
        },
        new(ConnectionKind.Box, "Box", ConnectionGroup.Cloud, "box", "Box.com für Teams und Unternehmen")
        {
            Auth = AuthKind.OAuth,
            Fields = [SignedInAs, BasePath],
        },
        new(ConnectionKind.Seafile, "Seafile", ConnectionGroup.Cloud, "layers", "Bibliothek eines Seafile-Servers")
        {
            Auth = AuthKind.Password,
            SecretLabel = "Passwort",
            Fields =
            [
                new(ConnectionFields.Url, "Server", FieldType.Url) { Required = true, Placeholder = "https://seafile.example.com" },
                UserName with { Label = "E-Mail / Benutzer" },
                new(ConnectionFields.Library, "Bibliothek", FieldType.Text) { Required = true, Placeholder = "Meine Bibliothek" },
                BasePath,
            ],
        },

        // ---- ThixApps -------------------------------------------------------------------------------------------------
        new(ConnectionKind.ThixSyncRemote, "ThixSync Remote", ConnectionGroup.ThixApps, "link", "Ein anderer Computer mit ThixSync (PC zu PC, verschlüsselt)")
        {
            Auth = AuthKind.Pairing,
            SecretLabel = "Kopplungscode",
            FolderHint = "/Ordner in der Freigabe",
            Fields =
            [
                Host("anna-pc.local oder 192.168.1.20", "Computer"),
                Port(Remote.RemoteProtocol.DefaultPort),
                new(ConnectionFields.RemoteShare, "Freigabe", FieldType.Text)
                {
                    Required = true,
                    Placeholder = "Dokumente",
                    Help = "Der Name einer Freigabe auf dem anderen Computer (dort: Einstellungen › Remote).",
                },
                new(ConnectionFields.RemoteDevice, "Gerät", FieldType.ReadOnly) { Tab = FieldTab.Advanced },
                new(ConnectionFields.Fingerprint, "Fingerabdruck", FieldType.ReadOnly) { Tab = FieldTab.Advanced },
            ],
        },
        new(ConnectionKind.MatCloud, "MatCloud / ThixCloud", ConnectionGroup.ThixApps, "cloud", "ThixDrive deines MatCloud-Kontos (Anmeldung aus Einstellungen › Cloud)")
        {
            Auth = AuthKind.MatCloud,
            Fields = [SignedInAs, BasePath],
        },
    ];

    public static ConnectionType Get(ConnectionKind kind) =>
        All.FirstOrDefault(type => type.Kind == kind) ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unbekannter Verbindungstyp");

    public static string GroupName(ConnectionGroup group) => group switch
    {
        ConnectionGroup.LocalAndNetwork => "Lokal & Netzwerk",
        ConnectionGroup.Servers => "Server & Protokolle",
        ConnectionGroup.Cloud => "Cloud-Speicher",
        ConnectionGroup.ThixApps => "ThixApps",
        _ => group.ToString(),
    };

    /// <summary>The value of a field with its default.</summary>
    public static string Value(Connection connection, string key)
    {
        var value = connection.Get(key);
        if (value.Length > 0)
        {
            return value;
        }

        return Get(connection.Kind).Fields.FirstOrDefault(field => field.Key == key)?.Default ?? "";
    }
}
