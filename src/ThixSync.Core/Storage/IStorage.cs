using ThixSync.Core.Models;

namespace ThixSync.Core.Storage;

/// <summary>
/// A file or folder as a storage lists it. <see cref="Path"/> is relative to the root the storage was opened at, separated by
/// '/', without a leading or trailing '/' (StoragePath.Normalize). Times are UTC.
/// </summary>
public readonly record struct StorageEntry(string Path, bool IsFolder, long Size, DateTimeOffset Modified)
{
    /// <summary>A content hash the storage knows without reading the file (e.g. "md5:…", "sha1:…", "quickxor:…", "dropbox:…"); null if none.</summary>
    public string? Hash { get; init; }

    /// <summary>The storage's own id of the item (Google Drive file id, OneDrive item id …), if it has one; providers use it as a cache.</summary>
    public string? NativeId { get; init; }

    public string Name => StoragePath.NameOf(Path);
}

/// <summary>What a storage can do (the engine adapts to it).</summary>
[Flags]
public enum StorageFeatures
{
    None = 0,

    /// <summary>The modification time of a written file can be set (otherwise the storage's own upload time is kept and the engine remembers it).</summary>
    SetModifiedTime = 1,

    /// <summary>Names that differ only in case are different files (Linux, S3 …).</summary>
    CaseSensitive = 2,

    /// <summary>Rename/move on the storage itself (no copy).</summary>
    Move = 4,

    /// <summary>Folders really exist (S3, B2, Azure, GCS only know prefixes: an empty folder is a marker object or nothing).</summary>
    RealFolders = 8,

    /// <summary>The storage is a folder of this computer (<see cref="ILocalStorage"/>): the engine copies with the operating system.</summary>
    LocalFileSystem = 16,

    /// <summary>The storage can tell changes since a point without a full listing (<see cref="IChangeFeed"/>).</summary>
    ChangeFeed = 32,

    /// <summary>Copies inside the storage without the bytes going through this computer.</summary>
    ServerSideCopy = 64,

    /// <summary>Time stamps are rounded to whole seconds or worse (FTP, FAT, some clouds): the engine compares with tolerance.</summary>
    CoarseTime = 128,
}

/// <summary>The abilities and limits of an opened storage.</summary>
public sealed record StorageCapabilities
{
    public StorageFeatures Features { get; init; }

    /// <summary>How many operations at the same time are good for this storage (connections, API rate limits).</summary>
    public int MaxParallelism { get; init; } = 8;

    /// <summary>The hash kind <see cref="StorageEntry.Hash"/> carries ("md5", "sha1", "sha256", "quickxor", "dropbox", "crc32c" …; null: none).</summary>
    public string? HashKind { get; init; }

    /// <summary>The largest file the storage takes (0: no limit).</summary>
    public long MaxFileSize { get; init; }

    /// <summary>Characters the storage does not allow in names (the engine reports such files instead of failing in the middle).</summary>
    public string InvalidNameCharacters { get; init; } = "";

    public bool Has(StorageFeatures feature) => (Features & feature) == feature;
}

/// <summary>
/// One opened place of a connection, rooted at a folder (the base of the connection + the folder of a pair). Every method takes
/// paths relative to that root ('/', see <see cref="StoragePath"/>). An implementation MUST be safe for many calls at the same time
/// (the engine copies with many workers): pool connections inside, throttle by <see cref="StorageCapabilities.MaxParallelism"/>.
/// Errors: <see cref="StorageException"/> (with <see cref="StorageErrorKind"/>), <see cref="OperationCanceledException"/> for
/// cancellation. Never log secrets; paths only at debug level.
/// </summary>
public interface IStorage : IAsyncDisposable
{
    ConnectionKind Kind { get; }

    StorageCapabilities Capabilities { get; }

    /// <summary>Where this is, for people ("sftp://nas.local/backup/Fotos", "Google Drive (anna@…)/Fotos").</summary>
    string DisplayName { get; }

    /// <summary>Signs in / connects and checks that the root exists; creates the root when <paramref name="createRoot"/>.</summary>
    Task ConnectAsync(bool createRoot, CancellationToken cancellationToken);

    /// <summary>
    /// Everything below <paramref name="folder"/> (empty: the root), recursively, folders and files, in any order. Providers that can
    /// list a whole tree at once (S3 prefixes, Drive queries, <c>find</c> over SSH) do so; others walk the folders in parallel.
    /// Unreadable folders are reported through <paramref name="onProblem"/> and skipped (the listing goes on).
    /// </summary>
    IAsyncEnumerable<StorageEntry> ListAsync(string folder, Action<string, Exception>? onProblem, CancellationToken cancellationToken);

    /// <summary>One entry, or null when nothing is there.</summary>
    Task<StorageEntry?> GetAsync(string path, CancellationToken cancellationToken);

    /// <summary>Reads a file from the start. The caller disposes the stream.</summary>
    Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a whole file (creating missing parent folders), replacing an existing one as atomically as the storage allows
    /// (temporary name + rename where possible, so a cancelled copy never leaves a half file under the real name). Sets the
    /// modification time when the storage can (<see cref="StorageFeatures.SetModifiedTime"/>). <paramref name="length"/> is
    /// the exact number of bytes <paramref name="content"/> yields. Reports written bytes to <paramref name="progress"/> as it goes.
    /// Returns the entry as the storage now has it (its time, hash and id).
    /// </summary>
    Task<StorageEntry> WriteAsync(string path, Stream content, long length, DateTimeOffset modified, IProgress<long>? progress, CancellationToken cancellationToken);

    /// <summary>Creates a folder (and its parents); nothing happens when it exists.</summary>
    Task CreateFolderAsync(string path, CancellationToken cancellationToken);

    /// <summary>Deletes a file, or a folder with everything in it. Nothing happens when it is not there.</summary>
    Task DeleteAsync(string path, bool isFolder, CancellationToken cancellationToken);

    /// <summary>Renames/moves a file or folder (creating missing parent folders). Only when <see cref="StorageFeatures.Move"/>; otherwise NotSupportedException.</summary>
    Task MoveAsync(string fromPath, string toPath, CancellationToken cancellationToken);
}

/// <summary>A storage that is a folder of this computer: the engine uses the operating system's copy and the change watcher.</summary>
public interface ILocalStorage : IStorage
{
    /// <summary>The full local path of a relative path.</summary>
    string GetLocalPath(string path);
}

/// <summary>A storage that can tell what changed since a cursor (Drive changes, OneDrive delta, Dropbox cursor …).</summary>
public interface IChangeFeed
{
    /// <summary>The current cursor (store it after a run).</summary>
    Task<string> GetCursorAsync(CancellationToken cancellationToken);

    /// <summary>True when anything changed below the root since <paramref name="cursor"/>.</summary>
    Task<bool> HasChangesAsync(string cursor, CancellationToken cancellationToken);
}

/// <summary>Why a storage operation failed: the engine decides from this whether to retry, skip the file or stop the run.</summary>
public enum StorageErrorKind
{
    /// <summary>Anything else.</summary>
    Unknown = 0,

    /// <summary>Not reachable (DNS, timeout, refused, offline): retried, then the run fails.</summary>
    Unreachable = 1,

    /// <summary>Sign-in refused / token expired and could not be renewed: the run fails, the connection needs the user.</summary>
    Authentication = 2,

    /// <summary>No permission for this file or folder: the file is skipped.</summary>
    AccessDenied = 3,

    NotFound = 4,

    /// <summary>The file is open/locked by another program: retried, then skipped.</summary>
    Locked = 5,

    /// <summary>The target is full / over quota: the run stops.</summary>
    QuotaExceeded = 6,

    /// <summary>Too many requests: wait (<see cref="StorageException.RetryAfter"/>) and retry.</summary>
    RateLimited = 7,

    /// <summary>The name is not allowed on this storage: the file is skipped.</summary>
    InvalidName = 8,

    /// <summary>A short-lived server problem (5xx, connection reset): retried.</summary>
    Transient = 9,

    /// <summary>The file changed while it was read: retried.</summary>
    ChangedWhileReading = 10,
}

/// <summary>A failed storage operation with its kind (message in German for the protocol, no secrets).</summary>
public class StorageException : Exception
{
    public StorageException(StorageErrorKind kind, string message, Exception? inner = null) : base(message, inner)
    {
        Kind = kind;
    }

    public StorageErrorKind Kind { get; }

    /// <summary>For <see cref="StorageErrorKind.RateLimited"/>: how long the storage asked to wait.</summary>
    public TimeSpan? RetryAfter { get; init; }

    public bool IsRetryable => Kind is StorageErrorKind.Unreachable or StorageErrorKind.Locked or StorageErrorKind.RateLimited
        or StorageErrorKind.Transient or StorageErrorKind.ChangedWhileReading;

    /// <summary>The whole run cannot go on (the side is gone, sign-in refused, full).</summary>
    public bool StopsRun => Kind is StorageErrorKind.Authentication or StorageErrorKind.QuotaExceeded;
}
