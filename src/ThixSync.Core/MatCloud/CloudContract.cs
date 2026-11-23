// MatCloud.Contract 1.7.0 – a copy from MatCloud, do not edit here
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MatCloud.Contract;

/// <summary>
/// The protocol of MatCloud for the Thix apps. Every route below /api/v1 except the sign-in (<see cref="DeviceLogin"/>, <see cref="DeviceLink"/>,
/// <see cref="DeviceLinkPoll"/>) needs the device token as "Authorization: Bearer &lt;token&gt;"; an unknown or removed device answers 401 (the app
/// then signs out), an app the administrator switched off answers 403.
///
/// A device belongs to ONE app (<see cref="DeviceLoginRequest.App"/>): its token reads and writes only the data of that app. Apps with a module in
/// MatCloud (ThixClip: <see cref="Clip.ClipContract"/>) have routes of their own; every other app keeps its data in the general sync
/// (<see cref="SyncContract"/>). The one exception is granted by a module, explicitly: the Drive lets the office apps in
/// (<see cref="Drive.DriveContract.GuestApps"/>: a device of ThixWrite, ThixCalc, ThixShow or ThixSync reaches the Drive of its account).
/// </summary>
public static class CloudContract
{
    /// <summary>Raised when a change is not backwards compatible. Sent as header <see cref="ProtocolHeader"/> both ways.</summary>
    public const int ProtocolVersion = 1;

    public const string ProtocolHeader = "X-MatCloud-Protocol";

    /// <summary>POST: signs a device in with the user name and password of a local account.</summary>
    public const string DeviceLogin = "/api/v1/devices/login";

    /// <summary>POST: starts the sign-in in the browser (works with every account, also with MatIdentity): answers a code and an address.</summary>
    public const string DeviceLink = "/api/v1/devices/link";

    /// <summary>POST: asks whether the code was confirmed in the browser (every <see cref="DeviceLinkStart.IntervalSeconds"/>).</summary>
    public const string DeviceLinkPoll = "/api/v1/devices/link/poll";

    /// <summary>
    /// POST (only a device of <see cref="CloudApps.Launcher"/>): ThixLauncher hands its sign-in to an app it installs on its computer
    /// (<see cref="DeviceHandoffRequest"/> → <see cref="DeviceHandoffStart"/>; the hand-over goes through <see cref="CloudHandoff"/>).
    /// </summary>
    public const string DeviceHandoff = "/api/v1/devices/handoff";

    /// <summary>POST: the device signs itself out; its token ends (204).</summary>
    public const string DeviceLogout = "/api/v1/devices/logout";

    /// <summary>GET: the account and the device as the cloud knows them (<see cref="AccountInfo"/>).</summary>
    public const string Account = "/api/v1/account";

    /// <summary>The path of the page where a person confirms a code in the browser.</summary>
    public const string ConnectPage = "/Connect";

    /// <summary>The longest wait of a long poll in seconds.</summary>
    public const int MaxWaitSeconds = 30;

    /// <summary>The longest name of a device (characters).</summary>
    public const int MaxDeviceNameLength = 100;

    /// <summary>The JSON settings of every request and answer (camelCase, enums as text, nulls left out).</summary>
    public static readonly JsonSerializerOptions Json = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

/// <summary>A Thix app as MatCloud knows it. <see cref="Id"/> is what a device sends as its app.</summary>
public sealed record CloudApp(string Id, string Name, string Description);

/// <summary>
/// The apps that may sign devices in. A new app is added here (and copied into the apps) before its first device signs in; the cloud refuses an id
/// it does not know. The order is the order of the lists.
/// </summary>
public static class CloudApps
{
    public const string Clip = "clip";
    public const string Note = "note";
    public const string Calendar = "calendar";

    /// <summary>ThixDrive: the files of the account (<see cref="Drive.DriveContract"/>), also reached by <see cref="Write"/>, <see cref="Calc"/> and <see cref="Show"/>.</summary>
    public const string Drive = "drive";

    /// <summary>ThixWrite (word processing): opens and saves its documents in the Drive (<see cref="Drive.DriveContract.GuestApps"/>).</summary>
    public const string Write = "write";

    /// <summary>ThixCalc (spreadsheets): opens and saves its workbooks in the Drive.</summary>
    public const string Calc = "calc";

    /// <summary>ThixShow (presentations): opens and saves its presentations in the Drive.</summary>
    public const string Show = "show";

    public const string Tunnel = "tunnel";
    public const string Launcher = "launcher";
    public const string Fred = "fred";

    /// <summary>ThixContacts (people, Outlook's "Personen"): the contacts and groups of the account (<see cref="Contacts.ContactsContract"/>).</summary>
    public const string Contacts = "contacts";

    /// <summary>ThixMail (e-mail): reads and writes the contacts and the calendar of its account (the grants of those modules); mail itself stays on the computer.</summary>
    public const string Mail = "mail";

    /// <summary>ThixSpace (disk usage): its sources and analyses in the general sync.</summary>
    public const string Space = "space";

    /// <summary>ThixSync (file sync and backup): its sync jobs and connections in the general sync; reaches the Drive of its account as a source or target (<see cref="Drive.DriveContract.GuestApps"/>).</summary>
    public const string Sync = "sync";

    public static readonly IReadOnlyList<CloudApp> All =
    [
        new(Clip, "ThixClip", "Der Zwischenablage-Verlauf auf allen Geräten"),
        new(Note, "ThixNote", "Notizen auf allen Geräten"),
        new(Calendar, "ThixCalendar", "Kalender und Termine auf allen Geräten und im Browser"),
        new(Drive, "ThixDrive", "Deine Dateien und Dokumente – im Browser und in ThixWrite, ThixCalc und ThixShow"),
        new(Write, "ThixWrite", "Textverarbeitung: Dokumente in der Ablage öffnen und speichern"),
        new(Calc, "ThixCalc", "Tabellenkalkulation: Arbeitsmappen in der Ablage öffnen und speichern"),
        new(Show, "ThixShow", "Präsentationen: Foliensätze in der Ablage öffnen und speichern"),
        new(Tunnel, "ThixTunnel", "Verbindungen und Profile auf allen Geräten"),
        new(Launcher, "ThixLauncher", "Die Thix-Apps und ihre Einstellungen"),
        new(Fred, "ThixFred", "Der Launcher: Einstellungen, Themes, Websuchen, Skripte und Plugins"),
        new(Contacts, "ThixContacts", "Kontakte und Kontaktgruppen auf allen Geräten und im Browser"),
        new(Mail, "ThixMail", "E-Mail: Konten-Einstellungen, Kontakte und Kalender aus der Cloud"),
        new(Space, "ThixSpace", "Speicheranalyse: Quellen und Analysen auf allen Geräten"),
        new(Sync, "ThixSync", "Synchronisation und Backup: Aufträge und Verbindungen auf allen Geräten, ThixDrive als Ziel"),
    ];

    public static CloudApp? Find(string? id) => All.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));

    public static bool IsKnown(string? id) => Find(id) is not null;

    /// <summary>The name of an app for people ("ThixNote"), or the id itself when it is unknown.</summary>
    public static string NameOf(string? id) => Find(id)?.Name ?? id ?? string.Empty;
}

/// <summary>The body of every error of the API (4xx/5xx), with a German message for the person; <see cref="Field"/> names the wrong value.</summary>
public sealed record ApiError(string Message, string? Field = null);

/// <summary>The answer of a long poll: the latest revision of what the device reads.</summary>
public sealed record WaitResult(long Latest);
