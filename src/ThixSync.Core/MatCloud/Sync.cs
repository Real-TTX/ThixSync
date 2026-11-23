// MatCloud.Contract 1.7.0 – a copy from MatCloud, do not edit here
using System.Text.RegularExpressions;

namespace MatCloud.Contract;

/// <summary>
/// The general sync: every app without a module of its own keeps its data here, as items of its own choosing. An item is named by a
/// <see cref="SyncItemDto.Collection"/> (a kind of thing: "notes", "settings") and a <see cref="SyncItemDto.Key"/> (one thing, usually a GUID);
/// its <see cref="SyncItemDto.Data"/> is text the app defines (JSON, the text of a note, base64 of encrypted bytes). The cloud never looks into it.
///
/// The device token decides the app: a device of ThixNote reads and writes only the items of ThixNote of its account.
///
/// How a device stays in sync (the same as ThixClip):
/// 1. Local changes go up with <see cref="Push"/> right away.
/// 2. <see cref="Wait"/> is a long poll: it answers as soon as the app's data of the account has a revision above "since" (or after the timeout).
/// 3. <see cref="Changes"/> returns everything above "since" in revision order (deleted items included, as tombstones without data); the device
///    keeps the last revision it has seen and asks again while <see cref="SyncChangeSet.HasMore"/> is true.
///
/// Rules: the last writer wins by <see cref="SyncItemDto.UpdatedAt"/> (a delete too, and a later write brings a deleted item back); a push that
/// loses gets the item that won in <see cref="SyncPushResult.Current"/>. Every change takes the next number of one sequence, so nothing is missed.
/// The collections that begin with <see cref="ServerCollectionPrefix"/> are written by MatCloud only (a module that syncs on the server, e.g.
/// ThixCalendar's accounts MatCloud keeps in step): a device reads them, its pushes into them are not applied (they answer the item as it is).
/// </summary>
public static partial class SyncContract
{
    public const string Changes = "/api/v1/sync/changes";
    public const string Wait = "/api/v1/sync/wait";
    public const string Push = "/api/v1/sync/push";

    /// <summary>
    /// The query value that names ANOTHER app's data (contract 1.5.0): <c>?app=contacts</c>. Without it (the default) a device reaches its own app. A
    /// device may name an app only when that app's module grants it (<c>ContactsContract.GuestApps</c>: ThixMail reads and writes the contacts and the
    /// calendar of its account; <c>ContactsContract.ReadingGuestApps</c>: ThixCalendar reads the contacts – changes and wait, no push); anything else is
    /// 403, and so is an app the administrator switched off.
    /// </summary>
    public const string AppParameter = "app";

    /// <summary>The most items one push or one change set carries.</summary>
    public const int MaxBatchSize = 200;

    /// <summary>The longest <see cref="SyncItemDto.Data"/> of one item (characters, about 2 MB).</summary>
    public const int MaxDataLength = 2_000_000;

    /// <summary>The largest body of one push (bytes).</summary>
    public const long MaxPushBytes = 32L * 1024 * 1024;

    /// <summary>The longest key of an item.</summary>
    public const int MaxKeyLength = 200;

    /// <summary>The beginning of the collections only MatCloud writes ("cloud-events").</summary>
    public const string ServerCollectionPrefix = "cloud-";

    /// <summary>A collection only MatCloud writes.</summary>
    public static bool IsServerCollection(string? collection) => collection?.StartsWith(ServerCollectionPrefix, StringComparison.Ordinal) == true;

    /// <summary>A collection: small letters, digits, '.', '_' and '-', 1 to 64 characters, starting with a letter or a digit.</summary>
    public static bool IsValidCollection(string? collection) => collection is not null && CollectionRegex().IsMatch(collection);

    /// <summary>A key: 1 to <see cref="MaxKeyLength"/> characters, no control characters.</summary>
    public static bool IsValidKey(string? key) => !string.IsNullOrEmpty(key) && key.Length <= MaxKeyLength && !key.Any(char.IsControl);

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex CollectionRegex();
}

/// <summary>One item as it travels between device and cloud. <see cref="Revision"/>, <see cref="Source"/> are set by the cloud (ignored in a push).</summary>
public sealed record SyncItemDto
{
    public required string Collection { get; init; }

    public required string Key { get; init; }

    /// <summary>The content, as the app defines it; null for a deleted item.</summary>
    public string? Data { get; init; }

    /// <summary>Optional: a hash the app computes over its data (to skip what it has already).</summary>
    public string? ContentHash { get; init; }

    /// <summary>When the item was changed last on a device: the last writer wins.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>A deleted item (tombstone, without data).</summary>
    public bool IsDeleted { get; init; }

    /// <summary>The name of the device that wrote it last.</summary>
    public string? Source { get; init; }

    public long Revision { get; init; }
}

/// <summary>GET <see cref="SyncContract.Changes"/>?since=&amp;limit=: the changes above a revision, oldest first.</summary>
public sealed record SyncChangeSet(IReadOnlyList<SyncItemDto> Items, long Latest, bool HasMore);

/// <summary>POST <see cref="SyncContract.Push"/>: local changes of a device.</summary>
public sealed record SyncPushRequest(IReadOnlyList<SyncItemDto> Items);

/// <summary>
/// What the cloud did with one pushed item: <see cref="Applied"/> = it is the state now (<see cref="Revision"/> its number); otherwise a newer change
/// was there already and <see cref="Current"/> is that item (the device takes it).
/// </summary>
public sealed record SyncPushResult(string Collection, string Key, long Revision, bool Applied, SyncItemDto? Current = null);

public sealed record SyncPushResponse(IReadOnlyList<SyncPushResult> Results, long Latest);
