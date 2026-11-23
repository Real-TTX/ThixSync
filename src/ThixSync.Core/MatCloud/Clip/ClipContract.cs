// MatCloud.Contract 1.7.0 – a copy from MatCloud, do not edit here
namespace MatCloud.Contract.Clip;

/// <summary>
/// The protocol of the ThixClip module of MatCloud (the clipboard history). Only a device of the app <see cref="CloudApps.Clip"/> may call it.
///
/// How a device stays in sync:
/// 1. Local changes (new entry, favourite, pin, delete) are sent with <see cref="Push"/> right away. An image entry is pushed
///    first, then its PNG is uploaded with PUT <see cref="Image"/>.
/// 2. <see cref="Wait"/> is a long poll: it answers as soon as the account has a revision above "since" (or after the timeout).
/// 3. <see cref="Changes"/> returns everything above "since" in revision order (deleted entries included, as tombstones);
///    the device stores the last revision it has seen and asks again while <see cref="ClipChangeSet.HasMore"/> is true.
/// Every change on the server (push, image upload, a change in the browser, the retention clean-up) gets a new revision
/// from one sequence, so nothing is ever missed. Deleting wins; the same content under another SyncId is merged (<see cref="ClipPushResult.MergedInto"/>).
/// </summary>
public static class ClipContract
{
    public const string Settings = "/api/v1/clip/settings";
    public const string Changes = "/api/v1/clip/changes";
    public const string Wait = "/api/v1/clip/wait";
    public const string Push = "/api/v1/clip/push";

    /// <summary>GET: the PNG of an image entry; PUT (body image/png): uploads it.</summary>
    public static string Image(Guid syncId) => $"/api/v1/clip/{syncId:D}/image";

    /// <summary>GET: a small PNG preview (longest side <see cref="ThumbnailSize"/> px).</summary>
    public static string Thumbnail(Guid syncId) => $"/api/v1/clip/{syncId:D}/thumbnail";

    /// <summary>Longest side of a thumbnail in pixels.</summary>
    public const int ThumbnailSize = 320;

    /// <summary>The largest text of one entry (characters). Longer copies are not recorded.</summary>
    public const int MaxTextLength = 1_000_000;

    /// <summary>The largest image of one entry (bytes of the PNG).</summary>
    public const long MaxImageBytes = 25L * 1024 * 1024;

    /// <summary>The most entries one push or one change set carries.</summary>
    public const int MaxBatchSize = 200;

    /// <summary>The retention choices in days (0 = keep forever). Favourites and pinned entries are always kept.</summary>
    public static readonly IReadOnlyList<int> RetentionChoices = [1, 7, 30, 90, 365, 0];

    public const int DefaultRetentionDays = 30;

    public static bool IsValidRetention(int days) => RetentionChoices.Contains(days);
}

/// <summary>What an entry holds. A link is a text that is one http(s) address.</summary>
public enum ClipKind
{
    Text = 1,
    Link = 2,
    Image = 3,
}

/// <summary>GET / PUT <see cref="ClipContract.Settings"/>: what holds for the clips of the whole account.</summary>
public sealed record ClipSettings(int RetentionDays);

/// <summary>
/// One entry as it travels between device and cloud. <see cref="SyncId"/> is created by whoever created the entry (device or
/// browser) and never changes. <see cref="Revision"/> is set by the server (0 in a push).
/// </summary>
public sealed record ClipDto
{
    public required Guid SyncId { get; init; }

    public required ClipKind Kind { get; init; }

    /// <summary>The text of a text or link entry; null for an image.</summary>
    public string? Text { get; init; }

    /// <summary>Text recognised in an image (OCR); null when none was recognised (yet).</summary>
    public string? OcrText { get; init; }

    /// <summary>SHA-256 (hex, upper case) of the text (UTF-8) or of the PNG: the same content is one entry.</summary>
    public required string ContentHash { get; init; }

    public bool IsFavorite { get; init; }

    public bool IsPinned { get; init; }

    /// <summary>When the content was copied first.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the content was copied last: the list is ordered by it.</summary>
    public required DateTimeOffset CopiedAt { get; init; }

    /// <summary>When the entry was changed last on the device or in the browser (last writer wins).</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    public int? ImageWidth { get; init; }

    public int? ImageHeight { get; init; }

    /// <summary>Size of the PNG in bytes.</summary>
    public long? ImageBytes { get; init; }

    /// <summary>True when the server has the PNG of an image entry (it can be downloaded).</summary>
    public bool HasImage { get; init; }

    /// <summary>Where it was copied: the device name, or "Web".</summary>
    public string? Source { get; init; }

    /// <summary>A deleted entry (tombstone). Deleting wins over every other change.</summary>
    public bool IsDeleted { get; init; }

    public long Revision { get; init; }
}

/// <summary>GET <see cref="ClipContract.Changes"/>?since=&amp;limit=: the changes above a revision, oldest first, and the retention of the account.</summary>
public sealed record ClipChangeSet(
    IReadOnlyList<ClipDto> Clips,
    long Latest,
    bool HasMore,
    int RetentionDays);

/// <summary>POST <see cref="ClipContract.Push"/>: local changes of a device (Revision and HasImage are ignored).</summary>
public sealed record ClipPushRequest(IReadOnlyList<ClipDto> Clips);

/// <summary>
/// What the server did with one pushed entry. <see cref="MergedInto"/> is set when the account already had the same content
/// under another SyncId: the server moved that entry to the top instead of keeping a second one, and the device re-keys its
/// local entry to that SyncId.
/// </summary>
public sealed record ClipPushResult(Guid SyncId, long Revision, Guid? MergedInto, bool IsDeleted, bool NeedsImage);

public sealed record ClipPushResponse(IReadOnlyList<ClipPushResult> Results, long Latest);
