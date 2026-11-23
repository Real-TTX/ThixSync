// MatCloud.Contract 1.7.0 – a copy from MatCloud, do not edit here
namespace MatCloud.Contract.Drive;

/// <summary>
/// The Drive of MatCloud (app <see cref="CloudApps.Drive"/>, ThixDrive): the files and folders of an account. Besides a device of ThixDrive, a device of
/// <see cref="GuestApps"/> (ThixWrite, ThixCalc, ThixShow, ThixSync) may call these routes and reaches the Drive of ITS account – a grant of the server, not a rule
/// that loosens the others: a device of ThixClip or ThixNote gets 403. When an administrator switches the Drive off, the office apps lose it too (403).
///
/// Every item has a stable <see cref="DriveItem.Id"/> (a number that never changes, also not when the item is renamed or moved). A file has versions; its
/// <see cref="DriveItem.ETag"/> names the current one. Opening and saving a document:
/// 1. Open: <see cref="Content"/> (GET, ranges allowed) answers the bytes and the header <c>ETag</c> of the version; keep it.
/// 2. Save: PUT <see cref="Content"/> with <c>If-Match: &lt;that ETag&gt;</c>. 200 + the item with its new ETag. When somebody saved in between: 412 with
///    a <see cref="DriveConflict"/> whose <see cref="DriveConflict.Current"/> is the item as it is now; the app asks "Überschreiben" (PUT again with
///    <c>If-Match: &lt;Current.ETag&gt;</c> or <c>*</c>) or "Als Kopie speichern" (<see cref="Files"/> with <see cref="DriveNameConflict.Rename"/>).
///    Without If-Match the cloud answers 428: a version is never overwritten by accident.
/// 3. A new file: POST <see cref="Files"/>?parentId=&amp;name=&amp;onConflict= with the bytes (up to <see cref="MaxSimpleUploadBytes"/>); larger
///    files go in pieces through <see cref="Uploads"/> (resumable: <see cref="Upload(Guid)"/> tells how much arrived).
///
/// Errors are <see cref="ApiError"/> (409 and 412: <see cref="DriveConflict"/>, which is an ApiError with the current item): 400 a bad value
/// (<c>field</c>), 403 another app or the Drive switched off, 404 not found (also: an item of another account), 409 the name is taken (or a move into
/// itself), 412 the ETag of If-Match is not the current one, 413 too large, 416 a bad range, 428 If-Match is missing, 507 the quota is full.
/// </summary>
public static class DriveContract
{
    /// <summary>The apps whose devices may use the Drive besides ThixDrive's own: the office apps open and save their documents here.</summary>
    public static readonly IReadOnlyList<string> GuestApps = [CloudApps.Write, CloudApps.Calc, CloudApps.Show, CloudApps.Sync];

    public const string Root = "/api/v1/drive";

    /// <summary>GET ?parentId=&amp;sort=&amp;descending=&amp;offset=&amp;limit=: the items of a folder (no parentId: the top of the Drive), folders first → <see cref="DriveListing"/>.</summary>
    public const string Items = Root + "/items";

    /// <summary>GET: one item with its folders above it (<see cref="DriveItem.Ancestors"/>).</summary>
    public static string Item(long id) => $"{Items}/{id}";

    /// <summary>
    /// GET: the bytes of the current version (header ETag, Last-Modified; Range and If-None-Match work). PUT: a new version; needs <c>If-Match</c> →
    /// <see cref="DriveItem"/>, 412 <see cref="DriveConflict"/>, 428 without If-Match.
    /// </summary>
    public static string Content(long id) => $"{Item(id)}/content";

    /// <summary>POST <see cref="DriveRenameRequest"/> → <see cref="DriveItem"/> (409 when the name is taken).</summary>
    public static string Rename(long id) => $"{Item(id)}/rename";

    /// <summary>POST <see cref="DriveMoveRequest"/> → <see cref="DriveItem"/> (409 when the name is taken there or a folder would go into itself).</summary>
    public static string Move(long id) => $"{Item(id)}/move";

    /// <summary>POST <see cref="DriveCopyRequest"/> → <see cref="DriveItem"/> (the copy; a folder with everything in it).</summary>
    public static string Copy(long id) => $"{Item(id)}/copy";

    /// <summary>POST: into the trash, with everything below it → <see cref="DriveItem"/>.</summary>
    public static string Trash(long id) => $"{Item(id)}/trash";

    /// <summary>POST: out of the trash (back into its folder, or to the top when that is gone; a taken name gets a number) → <see cref="DriveItem"/>.</summary>
    public static string Restore(long id) => $"{Item(id)}/restore";

    /// <summary>POST: starred, DELETE: not starred (204).</summary>
    public static string Star(long id) => $"{Item(id)}/star";

    /// <summary>GET: the versions of a file, newest first → <see cref="DriveVersionList"/>.</summary>
    public static string Versions(long id) => $"{Item(id)}/versions";

    /// <summary>GET: the bytes of one version (ranges allowed).</summary>
    public static string VersionContent(long id, long versionId) => $"{Versions(id)}/{versionId}/content";

    /// <summary>POST: an older version becomes the current one again (as a new version: the history stays whole) → <see cref="DriveItem"/>.</summary>
    public static string VersionRestore(long id, long versionId) => $"{Versions(id)}/{versionId}/restore";

    /// <summary>DELETE (only an item in the trash): gone for good with every version (204; 409 when it is not in the trash).</summary>
    public static string Delete(long id) => Item(id);

    /// <summary>POST ?parentId=&amp;name=&amp;onConflict= with the bytes as the body: a new file (201 <see cref="DriveItem"/>; 409 <see cref="DriveConflict"/>).</summary>
    public const string Files = Root + "/files";

    /// <summary>POST <see cref="DriveFolderRequest"/> → 201 <see cref="DriveItem"/> (409 when the name is taken).</summary>
    public const string Folders = Root + "/folders";

    /// <summary>GET ?q=&amp;offset=&amp;limit=: items whose name contains the text, anywhere in the Drive → <see cref="DriveItemPage"/>.</summary>
    public const string Search = Root + "/search";

    /// <summary>GET ?limit=: the files the account opened or saved last (on any device, in any app, in the browser) → <see cref="DriveItemPage"/>.</summary>
    public const string Recent = Root + "/recent";

    /// <summary>GET ?offset=&amp;limit=: the starred items → <see cref="DriveItemPage"/>.</summary>
    public const string Starred = Root + "/starred";

    /// <summary>GET ?offset=&amp;limit=: what was put into the trash (the items themselves, not what lies below them) → <see cref="DriveItemPage"/>.</summary>
    public const string TrashList = Root + "/trash";

    /// <summary>GET: used space, the quota and the limits → <see cref="DriveQuota"/>.</summary>
    public const string Quota = Root + "/quota";

    /// <summary>POST <see cref="DriveUploadRequest"/> → 201 <see cref="DriveUploadState"/>: an upload in pieces (the checks of the target run here, before the first byte).</summary>
    public const string Uploads = Root + "/uploads";

    /// <summary>GET: how much arrived (resume from <see cref="DriveUploadState.Received"/>); DELETE: give up (204).</summary>
    public static string Upload(Guid id) => $"{Uploads}/{id:D}";

    /// <summary>PUT ?offset= with the bytes of one piece (at most <see cref="MaxChunkBytes"/>) → <see cref="DriveUploadState"/>. A piece sent twice is fine.</summary>
    public static string UploadChunk(Guid id, long offset) => $"{Upload(id)}/chunk?offset={offset}";

    /// <summary>POST, when every byte arrived → 201 (new file) or 200 (new version) <see cref="DriveItem"/>; 409 / 412 <see cref="DriveConflict"/> as for a single request.</summary>
    public static string UploadFinish(Guid id) => $"{Upload(id)}/finish";

    /// <summary>The longest name of an item (characters).</summary>
    public const int MaxNameLength = 255;

    /// <summary>The largest body of <see cref="Files"/> and of PUT <see cref="Content"/>; larger files go through <see cref="Uploads"/>.</summary>
    public const long MaxSimpleUploadBytes = 64L * 1024 * 1024;

    /// <summary>The size of a piece an app should send (the last one is smaller).</summary>
    public const int ChunkBytes = 8 * 1024 * 1024;

    /// <summary>The largest piece the cloud takes.</summary>
    public const int MaxChunkBytes = 16 * 1024 * 1024;

    /// <summary>The most items one page of a list carries.</summary>
    public const int MaxPageSize = 500;

    /// <summary>An upload in pieces that is not finished is given up after these hours.</summary>
    public const int UploadHours = 24;

    /// <summary>Items in the trash go for good after these days.</summary>
    public const int TrashDays = 30;

    /// <summary>The value of If-Match that overwrites whatever is there ("Überschreiben").</summary>
    public const string AnyVersion = "*";
}

public enum DriveItemKind
{
    Folder = 1,
    File = 2,
}

/// <summary>The order of a folder (folders always come first).</summary>
public enum DriveSort
{
    Name = 1,
    Modified = 2,
    Size = 3,
    Type = 4,
}

/// <summary>What happens when a new file meets an item of the same name in the folder.</summary>
public enum DriveNameConflict
{
    /// <summary>409 <see cref="DriveConflict"/> with the item that has the name: the app asks the person.</summary>
    Fail = 1,

    /// <summary>The new file gets a free name: "Angebot (2).docx" ("Als Kopie speichern").</summary>
    Rename = 2,
}

/// <summary>A folder above an item.</summary>
public sealed record DriveCrumb(long Id, string Name);

/// <summary>
/// A file or a folder. <see cref="Id"/> never changes. <see cref="ETag"/> (a file) names its current version: send it as <c>If-Match</c> when saving.
/// <see cref="Location"/> is the path of its folder for people ("/Projekte/2026", "/" at the top).
/// </summary>
public sealed record DriveItem
{
    public required long Id { get; init; }

    /// <summary>The folder it lies in; null at the top of the Drive.</summary>
    public long? ParentId { get; init; }

    public required DriveItemKind Kind { get; init; }

    public required string Name { get; init; }

    /// <summary>The bytes of the current version (a folder: 0).</summary>
    public long Size { get; init; }

    /// <summary>The MIME type by the name ("application/vnd.openxmlformats-officedocument.wordprocessingml.document").</summary>
    public string? ContentType { get; init; }

    /// <summary>The ETag of the current version, with its quotes (<c>"1234"</c>); null for a folder.</summary>
    public string? ETag { get; init; }

    /// <summary>The SHA-256 of the current version (hex, lower case); null for a folder.</summary>
    public string? Sha256 { get; init; }

    /// <summary>1, 2, 3 … the number of the current version; null for a folder.</summary>
    public int? Version { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the content changed last (a folder: when something in it changed).</summary>
    public required DateTimeOffset ModifiedAt { get; init; }

    /// <summary>Who changed it last: the name of the device, or "Web".</summary>
    public string? ModifiedBy { get; init; }

    public string Location { get; init; } = "/";

    public bool IsStarred { get; init; }

    public bool IsTrashed { get; init; }

    public DateTimeOffset? TrashedAt { get; init; }

    /// <summary>The folders above it, from the top (only in the answer of <see cref="DriveContract.Item"/>).</summary>
    public IReadOnlyList<DriveCrumb>? Ancestors { get; init; }
}

/// <summary>A folder and one page of its items (folders first, then by <see cref="DriveSort"/>).</summary>
public sealed record DriveListing(DriveItem? Folder, IReadOnlyList<DriveCrumb> Ancestors, IReadOnlyList<DriveItem> Items, int Total, int Offset, int Limit);

/// <summary>One page of items of a search or a list (recent, starred, trash).</summary>
public sealed record DriveItemPage(IReadOnlyList<DriveItem> Items, int Total, int Offset, int Limit);

public sealed record DriveFolderRequest(long? ParentId, string Name);

public sealed record DriveRenameRequest(string Name);

/// <summary>Into a folder; <see cref="ParentId"/> null = the top of the Drive.</summary>
public sealed record DriveMoveRequest(long? ParentId);

/// <summary>A copy into a folder (null: the top); without a name the copy is named "Kopie von …" in the same folder, else it keeps its name (a number when it is taken).</summary>
public sealed record DriveCopyRequest(long? ParentId, string? Name = null);

/// <summary>
/// Starts an upload in pieces. A new file: <see cref="ParentId"/> + <see cref="Name"/> (+ <see cref="OnConflict"/>). A new version of a file:
/// <see cref="ItemId"/> + <see cref="IfMatch"/> (required, as for PUT <see cref="DriveContract.Content"/>).
/// </summary>
public sealed record DriveUploadRequest(long Size, long? ParentId = null, string? Name = null, long? ItemId = null, string? IfMatch = null, DriveNameConflict OnConflict = DriveNameConflict.Fail);

/// <summary>An upload in pieces: send the bytes from <see cref="Received"/> on, in pieces of <see cref="ChunkBytes"/>, then finish it.</summary>
public sealed record DriveUploadState(Guid Id, long Size, long Received, int ChunkBytes, DateTimeOffset ExpiresAt);

/// <summary>One version of a file. <see cref="Source"/> is the device that saved it (or "Web"), <see cref="App"/> its app ("write"; null in the browser).</summary>
public sealed record DriveVersion(long Id, int Number, long Size, string ETag, string Sha256, DateTimeOffset CreatedAt, string? Source, string? App, bool IsCurrent);

public sealed record DriveVersionList(long ItemId, IReadOnlyList<DriveVersion> Versions);

/// <summary>
/// The space of the account: <see cref="UsedBytes"/> counts every version of every file, the trash included; <see cref="QuotaBytes"/> null = no limit.
/// The limits of this installation: the largest file, the largest single request, the size of a piece.
/// </summary>
public sealed record DriveQuota(long UsedBytes, long? QuotaBytes, long TrashBytes, long OlderVersionsBytes, long MaxFileBytes, long MaxSimpleUploadBytes, int ChunkBytes);

/// <summary>
/// The body of 409 (a name is taken: <see cref="Current"/> is the item that has it) and 412 (the file changed since it was opened: <see cref="Current"/>
/// is the file as it is now, with its ETag). An <see cref="ApiError"/> with the item.
/// </summary>
public sealed record DriveConflict(string Message, string? Field, DriveItem? Current);
