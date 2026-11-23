// MatCloud.Contract 1.7.0 – a copy from MatCloud, do not edit here
using System.Security.Cryptography;
using System.Text;

namespace MatCloud.Contract.Calendar;

/// <summary>
/// ThixCalendar in MatCloud (app <see cref="CloudApps.Calendar"/>). ThixCalendar keeps its data in the general sync (<see cref="SyncContract"/>),
/// in the collections below; the module of MatCloud shows them in the browser and – for the accounts the user lets MatCloud keep in step
/// (<see cref="CalendarSyncMode.Cloud"/>) – signs in at Microsoft / Google / the ICS address itself and syncs on the server, also while no computer
/// runs.
///
/// Collections written by the apps (and by the pages of MatCloud):
///   settings  (key "calendar")        <see cref="CalendarSettingsItem"/> (plus the reminder settings of ThixCalendar; unknown fields are kept)
///   accounts  (SyncId of the account) <see cref="CalendarAccountItem"/> – never a sign-in
///   calendars (<see cref="CalendarKey"/>) <see cref="CalendarCalendarItem"/> – the user's choices; the whole calendar for an own one
///   events    (key of the event)      <see cref="CalendarEventItem"/> – the events of the own calendars
///   answers   (hash of the occurrence) the answers to reminders
/// Collections written ONLY by MatCloud (the apps read them and never push them):
///   cloud-accounts  (SyncId)          <see cref="CalendarCloudAccount"/> – the state of an account MatCloud keeps in step
///   cloud-calendars (<see cref="CalendarKey"/>) <see cref="CalendarCloudCalendar"/> – its calendars as the provider names them
///   cloud-events    (<see cref="EventKey"/>) <see cref="CalendarCloudEvent"/> – its events from <see cref="WindowPastDays"/> days back to
///                                     <see cref="WindowFutureDays"/> days ahead (series arrive expanded: one item per occurrence)
///
/// An account in <see cref="CalendarSyncMode.Cloud"/> reaches MatCloud's keeping in two ways: a device hands over the sign-in it has
/// (<see cref="Connections"/>, <see cref="CalendarHandover"/>), or the user connects it in the browser. A device then changes events of such an
/// account through <see cref="EventChange"/>: MatCloud writes to the provider first and answers what the provider stored.
/// </summary>
public static class CalendarContract
{
    /// <summary>
    /// The apps whose devices may read and write the calendar of their account besides ThixCalendar's own (contract 1.5.0): ThixMail shows the calendar
    /// and answers invitations (the general sync with <c>?app=calendar</c>, see <see cref="SyncContract.AppParameter"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> GuestApps = [CloudApps.Mail];

    public const string Settings = "settings";
    public const string Accounts = "accounts";
    public const string Calendars = "calendars";
    public const string Events = "events";
    public const string Answers = "answers";
    public const string CloudAccounts = "cloud-accounts";
    public const string CloudCalendars = "cloud-calendars";
    public const string CloudEvents = "cloud-events";

    /// <summary>The key of the settings item.</summary>
    public const string SettingsKey = "calendar";

    /// <summary>PUT (<see cref="CalendarHandover"/> → <see cref="CalendarCloudAccount"/>): MatCloud keeps the account in step from now on.</summary>
    public const string Connections = "/api/v1/calendar/connections";

    /// <summary>POST (<see cref="CalendarAccountRequest"/>, 204): MatCloud forgets the sign-in of the account and stops keeping it in step.</summary>
    public const string Disconnect = "/api/v1/calendar/connections/remove";

    /// <summary>POST (<see cref="CalendarAccountRequest"/>, 202): sync the account now (empty account: all of them).</summary>
    public const string SyncNow = "/api/v1/calendar/sync";

    /// <summary>POST (<see cref="CalendarEventChange"/> → <see cref="CalendarEventChangeResult"/>): creates, changes or deletes an event of an
    /// account MatCloud keeps in step – at the provider first.</summary>
    public const string EventChange = "/api/v1/calendar/events";

    /// <summary>ThixCalendar's registration at Microsoft (Entra, every organisation and personal accounts; a public client: no secret).</summary>
    public const string MicrosoftClientId = "67da608f-1ccd-4664-94d7-fd12ea31f063";

    /// <summary>ThixCalendar's desktop client at Google (its secret is no secret for a desktop app, but GitHub refuses it in code: it comes from
    /// the build or the configuration of MatCloud).</summary>
    public const string GoogleClientId = "466427641870-qud3jgehsu1qsgnb92rq27qve5cbifs6.apps.googleusercontent.com";

    public const int WindowPastDays = 14;

    public const int WindowFutureDays = 120;

    /// <summary>How often MatCloud syncs an account at the least (minutes), whatever the settings say.</summary>
    public const int MinSyncMinutes = 5;

    /// <summary>The key of a calendar: the account's SyncId and a hash of the calendar's key at its provider.</summary>
    public static string CalendarKey(string account, string calendarRemoteId) => $"{account}/{ShortHash(calendarRemoteId)}";

    /// <summary>The key of an event MatCloud keeps: its calendar and a hash of the event's key at the provider.</summary>
    public static string EventKey(string calendarKey, string eventRemoteId) => $"{calendarKey}/{ShortHash(eventRemoteId)}";

    /// <summary>The first 24 hex digits of the SHA-256 of the text (lower case).</summary>
    public static string ShortHash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..24];
}

/// <summary>Who keeps an online account (Microsoft, Google, ICS) in step.</summary>
public enum CalendarSyncMode
{
    /// <summary>Every computer signs in itself and syncs with the provider; MatCloud carries the account without its sign-in.</summary>
    Computers = 0,

    /// <summary>MatCloud keeps the sign-in (protected) and syncs on the server; the computers and the browser get the events from MatCloud.</summary>
    Cloud = 1,
}

/// <summary>The kinds of accounts, named as ThixCalendar names them.</summary>
public enum CalendarAccountKind
{
    Local = 0,
    Microsoft = 1,
    Google = 2,
    Ics = 3,
}

public enum CalendarEventStatus
{
    Confirmed = 0,
    Tentative = 1,
    Cancelled = 2,
}

public enum CalendarResponse
{
    None = 0,
    Organizer = 1,
    Accepted = 2,
    Tentative = 3,
    Declined = 4,
    NotResponded = 5,
}

/// <summary>How far MatCloud got with an account it keeps in step.</summary>
public enum CalendarCloudState
{
    /// <summary>Handed over or connected; the first sync has not finished yet.</summary>
    Connecting = 0,

    Ready = 1,

    /// <summary>The provider no longer accepts the sign-in: sign in again (on a computer or in the browser).</summary>
    SignInRequired = 2,

    /// <summary>The last sync failed (network, provider); MatCloud tries again.</summary>
    Failing = 3,
}

/// <summary>The settings item (<see cref="CalendarContract.SettingsKey"/>): what MatCloud needs of it. ThixCalendar's reminder settings are in it too.</summary>
public sealed record CalendarSettingsItem
{
    /// <summary>The sync mode a new online account gets (null: <see cref="CalendarSyncMode.Cloud"/> once the app is connected to MatCloud).</summary>
    public CalendarSyncMode? DefaultSyncMode { get; init; }

    public int SyncIntervalMinutes { get; init; }
}

/// <summary>An account (collection accounts). <see cref="SyncMode"/> null = <see cref="CalendarSyncMode.Computers"/> (accounts from before 1.3.0).</summary>
public sealed record CalendarAccountItem(CalendarAccountKind Kind, string Name, string UserName, string Url, bool IsVisible, CalendarSyncMode? SyncMode = null);

/// <summary>A calendar (collection calendars): for an online one only what the user chose, for an own one all of it.</summary>
public sealed record CalendarCalendarItem(string RemoteId, string Name, string Color, bool IsVisible, bool RemindersEnabled, int? DefaultReminderMinutes, int SortOrder);

/// <summary>An event of an own calendar (collection events, key = the event's own key). <see cref="Calendar"/> is the <see cref="CalendarContract.CalendarKey"/>.</summary>
public sealed record CalendarEventItem(
    string Calendar, string Title, DateTimeOffset Start, DateTimeOffset End, bool IsAllDay, string Location, string Description,
    string MeetingUrl, int? ReminderMinutes, CalendarEventStatus Status);

/// <summary>An account MatCloud keeps in step (collection cloud-accounts, key = its SyncId).</summary>
public sealed record CalendarCloudAccount(
    CalendarAccountKind Kind, string UserName, CalendarCloudState State, DateTimeOffset? LastSync, string Problem = "");

/// <summary>A calendar of an account MatCloud keeps in step, as its provider names it (collection cloud-calendars).</summary>
public sealed record CalendarCloudCalendar(
    string Account, string RemoteId, string Name, string Color, bool IsReadOnly, bool IsPrimary, int? DefaultReminderMinutes);

/// <summary>
/// An event (one occurrence) of an account MatCloud keeps in step (collection cloud-events), and what a device sends to change one. Times are
/// absolute; an all-day event has its dates at midnight UTC and <see cref="End"/> is the midnight after its last day.
/// </summary>
public sealed record CalendarCloudEvent
{
    /// <summary>The <see cref="CalendarContract.CalendarKey"/> of its calendar.</summary>
    public string Calendar { get; init; } = "";

    /// <summary>Its key at the provider ("" for a new one).</summary>
    public string RemoteId { get; init; } = "";

    public string Title { get; init; } = "";

    public DateTimeOffset Start { get; init; }

    public DateTimeOffset End { get; init; }

    public bool IsAllDay { get; init; }

    public string Location { get; init; } = "";

    public string Description { get; init; } = "";

    public string MeetingUrl { get; init; } = "";

    public string WebLink { get; init; } = "";

    public string Organizer { get; init; } = "";

    public int? ReminderMinutes { get; init; }

    public CalendarEventStatus Status { get; init; }

    public CalendarResponse Response { get; init; }

    public bool IsRecurring { get; init; }
}

/// <summary>
/// PUT <see cref="CalendarContract.Connections"/>: a device hands the sign-in of an account over to MatCloud. <see cref="RefreshToken"/> is the
/// refresh token of ThixCalendar's registration (Microsoft: <see cref="CalendarContract.MicrosoftClientId"/>; Google: ThixCalendar's desktop
/// client) – empty for an ICS account (MatCloud reads <see cref="Url"/>). The device forgets its own sign-in afterwards.
/// </summary>
public sealed record CalendarHandover(string Account, CalendarAccountKind Kind, string UserName, string RefreshToken, string Url = "");

/// <summary>Names one account (empty = every account, where that makes sense).</summary>
public sealed record CalendarAccountRequest(string Account);

/// <summary>
/// POST <see cref="CalendarContract.EventChange"/>. <see cref="Event"/> is the <see cref="CalendarContract.EventKey"/> of an existing event (null
/// creates one in <see cref="CalendarCloudEvent.Calendar"/> of <see cref="Data"/>); <see cref="Delete"/> deletes it.
/// </summary>
public sealed record CalendarEventChange(string? Event, CalendarCloudEvent? Data, bool Delete = false);

/// <summary>What the provider stored (<see cref="Data"/> null after a delete), under its key in cloud-events.</summary>
public sealed record CalendarEventChangeResult(string Event, CalendarCloudEvent? Data);
