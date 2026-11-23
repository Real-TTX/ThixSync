// MatCloud.Contract 1.7.0 – a copy from MatCloud, do not edit here
using System.Globalization;
using System.Text.Json;

namespace MatCloud.Contract.Contacts;

/// <summary>
/// The contacts of an account (app <see cref="CloudApps.Contacts"/>, ThixContacts). They live in the general sync (<see cref="SyncContract"/>), in two
/// collections the apps and the pages of MatCloud write alike:
///   contacts        (key = a GUID of the contact)  data = <see cref="ContactData"/> as JSON (<see cref="CloudContract.Json"/>)
///   contact-groups  (key = a GUID of the group)    data = <see cref="ContactGroupData"/> as JSON; its members are keys of contacts
/// The last writer wins per item (by <see cref="SyncItemDto.UpdatedAt"/>), a delete leaves a tombstone. A contact's picture is not in the item: it is
/// kept once per content under <see cref="Photos"/> and the item names it (<see cref="ContactData.PhotoId"/> = the SHA-256 of the bytes, hex).
///
/// The grant (<see cref="GuestApps"/>): a device of ThixMail reaches the contacts of ITS account too, reading and writing (the general sync with
/// <c>?app=contacts</c>, see <see cref="SyncContract.AppParameter"/>, and the pictures); a device of ThixCalendar may only read them
/// (<see cref="ReadingGuestApps"/>: the participants of an event). Every other app gets 403.
/// </summary>
public static class ContactsContract
{
    /// <summary>The apps whose devices may read AND write the contacts of their account besides ThixContacts' own.</summary>
    public static readonly IReadOnlyList<string> GuestApps = [CloudApps.Mail];

    /// <summary>The apps whose devices may only read the contacts of their account (changes, wait, pictures; no push, no upload).</summary>
    public static readonly IReadOnlyList<string> ReadingGuestApps = [CloudApps.Calendar];

    /// <summary>The collection of the contacts.</summary>
    public const string Contacts = "contacts";

    /// <summary>The collection of the groups (Outlook's "Kontaktgruppen").</summary>
    public const string Groups = "contact-groups";

    public const string Root = "/api/v1/contacts";

    /// <summary>POST with the bytes of a picture (JPEG, PNG, GIF or WebP; Content-Type of the picture) → <see cref="ContactPhotoInfo"/>. The same bytes again answer the same id.</summary>
    public const string Photos = Root + "/photos";

    /// <summary>GET: the bytes of a picture (404 when the account has none with this id).</summary>
    public static string Photo(string id) => $"{Photos}/{id}";

    /// <summary>The largest picture of a contact (bytes).</summary>
    public const int MaxPhotoBytes = 5 * 1024 * 1024;

    /// <summary>The longest name part, company, title … (characters).</summary>
    public const int MaxNameLength = 200;

    /// <summary>The longest single value (an e-mail address, a phone number, a line of an address, a web address).</summary>
    public const int MaxValueLength = 500;

    /// <summary>The longest notes of a contact (characters).</summary>
    public const int MaxNotesLength = 100_000;

    /// <summary>The most values of one kind (e-mail addresses, phone numbers, addresses, web addresses, categories) of one contact.</summary>
    public const int MaxValues = 50;

    /// <summary>The most members of a group.</summary>
    public const int MaxGroupMembers = 5_000;

    /// <summary>A picture id: 64 small hex digits (the SHA-256 of the bytes).</summary>
    public static bool IsValidPhotoId(string? id) => id is { Length: 64 } && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>
    /// A date of a contact (birthday, anniversary): <c>yyyy-MM-dd</c>, or <c>--MM-dd</c> when the year is not known (as vCard 4.0 writes it).
    /// </summary>
    public static bool IsValidDate(string? date) => TryParseDate(date, out _, out _, out _);

    /// <summary>Reads a date of a contact; <paramref name="year"/> is null for <c>--MM-dd</c>.</summary>
    public static bool TryParseDate(string? date, out int? year, out int month, out int day)
    {
        year = null;
        month = day = 0;
        if (date is null)
        {
            return false;
        }

        DateTime value;
        if (date.Length == 7 && date.StartsWith("--", StringComparison.Ordinal))
        {
            // a leap year, so that 29 February is a valid day without a year
            if (!DateTime.TryParseExact("2000-" + date[2..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
            {
                return false;
            }
        }
        else if (date.Length == 10 && DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
        {
            year = value.Year;
        }
        else
        {
            return false;
        }

        month = value.Month;
        day = value.Day;
        return true;
    }

    /// <summary>The JSON of a contact as it goes into the item.</summary>
    public static string Serialize(ContactData contact) => JsonSerializer.Serialize(contact, CloudContract.Json);

    public static string Serialize(ContactGroupData group) => JsonSerializer.Serialize(group, CloudContract.Json);

    /// <summary>The contact of an item's data; null when it is not one (unreadable data never stops a list).</summary>
    public static ContactData? ReadContact(string? data) => Read<ContactData>(data);

    public static ContactGroupData? ReadGroup(string? data) => Read<ContactGroupData>(data);

    private static T? Read<T>(string? data) where T : class
    {
        if (string.IsNullOrEmpty(data))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(data, CloudContract.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The labels of values (<see cref="ContactValue.Label"/>, <see cref="ContactAddress.Label"/>): these are known to every app (German names in
/// <see cref="NameOf"/>); any other text is a label of the user's own and is kept as it is.
/// </summary>
public static class ContactLabels
{
    public const string Home = "home";
    public const string Work = "work";
    public const string Other = "other";
    public const string Mobile = "mobile";
    public const string Main = "main";
    public const string HomeFax = "home-fax";
    public const string WorkFax = "work-fax";
    public const string Pager = "pager";
    public const string Blog = "blog";
    public const string Profile = "profile";

    /// <summary>The labels of e-mail addresses, in the order a form offers them.</summary>
    public static readonly IReadOnlyList<string> Email = [Work, Home, Other];

    public static readonly IReadOnlyList<string> Phone = [Mobile, Work, Home, Main, WorkFax, HomeFax, Pager, Other];

    public static readonly IReadOnlyList<string> Address = [Work, Home, Other];

    public static readonly IReadOnlyList<string> Url = [Work, Home, Blog, Profile, Other];

    /// <summary>The German name of a label ("Geschäftlich"); a label of the user's own is its own name.</summary>
    public static string NameOf(string? label) => label switch
    {
        Home => "Privat",
        Work => "Geschäftlich",
        Other or null or "" => "Weitere",
        Mobile => "Mobil",
        Main => "Hauptnummer",
        HomeFax => "Fax privat",
        WorkFax => "Fax geschäftlich",
        Pager => "Pager",
        Blog => "Blog",
        Profile => "Profil",
        _ => label,
    };
}

/// <summary>One value of a contact with its label: an e-mail address, a phone number, a web address.</summary>
public sealed record ContactValue
{
    /// <summary>One of <see cref="ContactLabels"/> or a label of the user's own.</summary>
    public string Label { get; init; } = ContactLabels.Other;

    public string Value { get; init; } = string.Empty;

    public ContactValue()
    {
    }

    public ContactValue(string label, string value)
    {
        Label = label;
        Value = value;
    }
}

/// <summary>A postal address of a contact.</summary>
public sealed record ContactAddress
{
    public string Label { get; init; } = ContactLabels.Work;

    /// <summary>Street and number (may have several lines, separated by "\n").</summary>
    public string Street { get; init; } = string.Empty;

    public string PostalCode { get; init; } = string.Empty;

    public string City { get; init; } = string.Empty;

    /// <summary>State or region ("Bayern").</summary>
    public string Region { get; init; } = string.Empty;

    public string Country { get; init; } = string.Empty;

    /// <summary>Nothing filled in.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => Street.Length == 0 && PostalCode.Length == 0 && City.Length == 0 && Region.Length == 0 && Country.Length == 0;
}

/// <summary>
/// A contact as it travels in the item of the collection <see cref="ContactsContract.Contacts"/>. Every field is optional; a contact needs at least a name,
/// a company or an e-mail address (the apps and MatCloud refuse an empty one). Lists keep their order: the first value is the preferred one.
/// Fields an app does not know (a newer contract) are kept by the others: they rewrite only what changed and keep <see cref="Extra"/>.
/// </summary>
public sealed record ContactData
{
    /// <summary>"Dr.", "Prof."</summary>
    public string Prefix { get; init; } = string.Empty;

    public string GivenName { get; init; } = string.Empty;

    public string MiddleName { get; init; } = string.Empty;

    public string FamilyName { get; init; } = string.Empty;

    /// <summary>"jun.", "MBA"</summary>
    public string Suffix { get; init; } = string.Empty;

    public string Nickname { get; init; } = string.Empty;

    /// <summary>The name the person chose to show ("Speichern unter"); empty: the apps build it from the parts.</summary>
    public string DisplayName { get; init; } = string.Empty;

    public string Company { get; init; } = string.Empty;

    public string Department { get; init; } = string.Empty;

    public string JobTitle { get; init; } = string.Empty;

    public IReadOnlyList<ContactValue> Emails { get; init; } = [];

    public IReadOnlyList<ContactValue> Phones { get; init; } = [];

    public IReadOnlyList<ContactAddress> Addresses { get; init; } = [];

    public IReadOnlyList<ContactValue> Urls { get; init; } = [];

    /// <summary><c>yyyy-MM-dd</c> or <c>--MM-dd</c> (<see cref="ContactsContract.IsValidDate"/>); empty: none.</summary>
    public string Birthday { get; init; } = string.Empty;

    public string Anniversary { get; init; } = string.Empty;

    public string Notes { get; init; } = string.Empty;

    /// <summary>The picture (<see cref="ContactsContract.Photo"/>): the SHA-256 of its bytes; empty: none.</summary>
    public string PhotoId { get; init; } = string.Empty;

    /// <summary>Outlook's categories ("Kunde", "Familie"); the colours are the apps' own.</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    public bool IsFavorite { get; init; }

    /// <summary>What a newer app wrote that this contract does not know; written back unchanged.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    /// <summary>
    /// The name to show: <see cref="DisplayName"/>, else "Given Middle Family", else the nickname, the company or the first e-mail address.
    /// </summary>
    public string NameToShow()
    {
        if (DisplayName.Trim().Length > 0)
        {
            return DisplayName.Trim();
        }

        var name = string.Join(' ', new[] { GivenName, MiddleName, FamilyName }.Select(p => p.Trim()).Where(p => p.Length > 0));
        if (name.Length > 0)
        {
            return name;
        }

        if (Nickname.Trim().Length > 0)
        {
            return Nickname.Trim();
        }

        return Company.Trim().Length > 0 ? Company.Trim() : Emails.FirstOrDefault(e => e.Value.Trim().Length > 0)?.Value.Trim() ?? string.Empty;
    }

    /// <summary>True when there is something to call the contact by (a name, a company or an e-mail address).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasName => NameToShow().Length > 0;
}

/// <summary>A group of contacts (Outlook's "Kontaktgruppe"): a name and the keys of its members, in the item of <see cref="ContactsContract.Groups"/>.</summary>
public sealed record ContactGroupData
{
    public string Name { get; init; } = string.Empty;

    /// <summary>The keys of the contacts (items of <see cref="ContactsContract.Contacts"/>); a key whose contact is gone is ignored by the readers.</summary>
    public IReadOnlyList<string> Members { get; init; } = [];

    public string Notes { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>The answer of an upload of a picture: its id (the SHA-256, hex), its type and size.</summary>
public sealed record ContactPhotoInfo(string Id, string ContentType, long Size);
