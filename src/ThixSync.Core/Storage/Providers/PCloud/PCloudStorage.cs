using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ThixSync.Core.Models;
using ThixSync.Core.Storage.Providers.S3;

namespace ThixSync.Core.Storage.Providers.PCloud;

/// <summary>
/// A folder of a pCloud account. Lists the whole tree with one call (listfolder recursive), uploads streamed with the original
/// modification time (mtime) and nopartial (a broken upload leaves nothing; an existing file is replaced as a new revision),
/// downloads through the file link, moves with renamefile/renamefolder. pCloud's own checksum (sha1) needs one call per file and
/// is not used.
/// </summary>
internal sealed class PCloudStorage : IStorage
{
    private readonly PCloudClient _client;
    private readonly string _root; // "/" or "/Basis/Fotos"
    private readonly ConcurrentDictionary<string, Lazy<Task>> _knownFolders = new(StringComparer.Ordinal);

    public PCloudStorage(StorageContext context, HttpMessageHandler? handler = null)
    {
        var connection = context.Connection;
        var userName = connection.Get(ConnectionFields.UserName).Trim();
        if (userName.Length == 0 || string.IsNullOrEmpty(context.Secret))
        {
            throw new StorageException(StorageErrorKind.Authentication, "E-Mail oder Passwort für pCloud fehlen.");
        }

        _client = new PCloudClient(PCloudClient.ApiFor(ConnectionCatalog.Value(connection, ConnectionFields.Region)), userName, context.Secret, handler);
        var root = StoragePath.Combine(ConnectionCatalog.Value(connection, ConnectionFields.BasePath), context.Folder);
        _root = "/" + root;
        DisplayName = $"pCloud ({userName})/{root}";
        Capabilities = new StorageCapabilities
        {
            Features = StorageFeatures.SetModifiedTime | StorageFeatures.Move | StorageFeatures.RealFolders | StorageFeatures.CoarseTime,
            MaxParallelism = 6,
        };
    }

    public ConnectionKind Kind => ConnectionKind.PCloud;

    public StorageCapabilities Capabilities { get; }

    public string DisplayName { get; }

    public async Task ConnectAsync(bool createRoot, CancellationToken cancellationToken)
    {
        await _client.SignInAsync(cancellationToken);
        if (_root == "/")
        {
            return;
        }

        try
        {
            await _client.CallAsync("listfolder", [new("path", _root), new("nofiles", "1"), new("noshares", "1")], "", cancellationToken);
        }
        catch (StorageException exception) when (exception.Kind == StorageErrorKind.NotFound)
        {
            if (!createRoot)
            {
                throw new StorageException(StorageErrorKind.NotFound, $"Den Ordner „{_root}“ gibt es bei pCloud nicht.", exception);
            }

            await EnsureFolderAsync("", cancellationToken);
        }
    }

    public async IAsyncEnumerable<StorageEntry> ListAsync(string folder, Action<string, Exception>? onProblem, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var start = StoragePath.Normalize(folder);
        var listing = await _client.CallAsync("listfolder", [new("path", PathOf(start)), new("recursive", "1"), new("timeformat", "timestamp")], start, cancellationToken);
        var pending = new Stack<(string Path, JsonElement Folder)>();
        pending.Push((start, listing.GetProperty("metadata")));
        while (pending.Count > 0)
        {
            var (path, metadata) = pending.Pop();
            if (!metadata.TryGetProperty("contents", out var contents))
            {
                continue;
            }

            foreach (var item in contents.EnumerateArray())
            {
                var entry = EntryOf(path.Length == 0 ? NameOf(item) : $"{path}/{NameOf(item)}", item);
                yield return entry;
                if (entry.IsFolder)
                {
                    pending.Push((entry.Path, item));
                }
            }
        }
    }

    public async Task<StorageEntry?> GetAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.Length == 0)
        {
            return new StorageEntry("", true, 0, DateTimeOffset.UnixEpoch);
        }

        try
        {
            var stat = await _client.CallAsync("stat", [new("path", PathOf(normalized)), new("timeformat", "timestamp")], normalized, cancellationToken);
            return EntryOf(normalized, stat.GetProperty("metadata"));
        }
        catch (StorageException exception) when (exception.Kind is StorageErrorKind.NotFound or StorageErrorKind.Unknown)
        {
            // stat knows files only on some servers: a folder is asked for with listfolder
        }

        try
        {
            var folder = await _client.CallAsync("listfolder", [new("path", PathOf(normalized)), new("nofiles", "1"), new("noshares", "1"), new("timeformat", "timestamp")],
                normalized, cancellationToken);
            return EntryOf(normalized, folder.GetProperty("metadata")) with { IsFolder = true, Size = 0 };
        }
        catch (StorageException exception) when (exception.Kind == StorageErrorKind.NotFound)
        {
            return null;
        }
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        return _client.DownloadAsync(PathOf(normalized), normalized, cancellationToken);
    }

    public async Task<StorageEntry> WriteAsync(string path, Stream content, long length, DateTimeOffset modified, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.Length == 0)
        {
            throw new StorageException(StorageErrorKind.InvalidName, "Der Wurzelordner ist keine Datei.");
        }

        var parent = StoragePath.ParentOf(normalized);
        await EnsureFolderAsync(parent, cancellationToken);
        var metadata = await _client.UploadAsync(PathOf(parent), StoragePath.NameOf(normalized), content, length, modified, progress, normalized, cancellationToken);
        return EntryOf(normalized, metadata);
    }

    public Task CreateFolderAsync(string path, CancellationToken cancellationToken) => EnsureFolderAsync(StoragePath.Normalize(path), cancellationToken);

    public async Task DeleteAsync(string path, bool isFolder, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.Length == 0)
        {
            throw new StorageException(StorageErrorKind.AccessDenied, "Der Wurzelordner eines Auftrags wird nie gelöscht.");
        }

        try
        {
            await _client.CallAsync(isFolder ? "deletefolderrecursive" : "deletefile", [new("path", PathOf(normalized))], normalized, cancellationToken);
        }
        catch (StorageException exception) when (exception.Kind == StorageErrorKind.NotFound)
        {
        }

        if (isFolder)
        {
            foreach (var known in _knownFolders.Keys.Where(known => StoragePath.IsInside(known, normalized)))
            {
                _knownFolders.TryRemove(known, out _);
            }
        }
    }

    public async Task MoveAsync(string fromPath, string toPath, CancellationToken cancellationToken)
    {
        var from = StoragePath.Normalize(fromPath);
        var to = StoragePath.Normalize(toPath);
        if (from.Length == 0 || to.Length == 0)
        {
            throw new StorageException(StorageErrorKind.InvalidName, "Der Wurzelordner wird nicht verschoben.");
        }

        if (from == to)
        {
            return;
        }

        await EnsureFolderAsync(StoragePath.ParentOf(to), cancellationToken);
        IReadOnlyList<KeyValuePair<string, string>> parameters = [new("path", PathOf(from)), new("topath", PathOf(to))];
        try
        {
            await _client.CallAsync("renamefile", parameters, from, cancellationToken);
        }
        catch (StorageException exception) when (exception.Kind == StorageErrorKind.NotFound)
        {
            await _client.CallAsync("renamefolder", parameters, from, cancellationToken);
            foreach (var known in _knownFolders.Keys.Where(known => StoragePath.IsInside(known, from)))
            {
                _knownFolders.TryRemove(known, out _);
            }
        }
    }

    /// <summary>The folders directly below the root (the folder picker).</summary>
    internal async Task<IReadOnlyList<string>> ListChildFoldersAsync(CancellationToken cancellationToken)
    {
        var listing = await _client.CallAsync("listfolder", [new("path", _root), new("nofiles", "1")], "", cancellationToken);
        if (!listing.GetProperty("metadata").TryGetProperty("contents", out var contents))
        {
            return [];
        }

        return [.. contents.EnumerateArray()
            .Where(item => item.TryGetProperty("isfolder", out var isFolder) && isFolder.ValueKind == JsonValueKind.True)
            .Select(NameOf)
            .Order(StringComparer.CurrentCultureIgnoreCase)];
    }

    public async ValueTask DisposeAsync()
    {
        await _client.SignOutAsync();
        _client.Dispose();
    }

    /// <summary>Creates a folder and its parents (each level once per storage: createfolderifnotexists).</summary>
    private Task EnsureFolderAsync(string path, CancellationToken cancellationToken)
    {
        if (path.Length == 0 && _root == "/")
        {
            return Task.CompletedTask;
        }

        // created without the caller's token: other writers may wait for the same folder
        var lazy = _knownFolders.GetOrAdd(path, _ => new Lazy<Task>(() => CreateLevelAsync(path)));
        return AwaitOrForgetAsync(path, lazy, cancellationToken);
    }

    private async Task AwaitOrForgetAsync(string path, Lazy<Task> lazy, CancellationToken cancellationToken)
    {
        try
        {
            await lazy.Value.WaitAsync(cancellationToken);
        }
        catch when (lazy.Value.IsFaulted)
        {
            _knownFolders.TryRemove(new KeyValuePair<string, Lazy<Task>>(path, lazy)); // the next writer tries again
            throw;
        }
    }

    private async Task CreateLevelAsync(string path)
    {
        if (path.Length > 0)
        {
            await EnsureFolderAsync(StoragePath.ParentOf(path), CancellationToken.None);
        }
        else
        {
            // the root of the storage: its parents below "/" first
            var parent = _root.LastIndexOf('/') is var slash and > 0 ? _root[..slash] : "/";
            if (parent != "/")
            {
                await CreateAbsoluteAsync(parent, CancellationToken.None);
            }
        }

        await _client.CallAsync("createfolderifnotexists", [new("path", PathOf(path))], path, CancellationToken.None);
    }

    private async Task CreateAbsoluteAsync(string absolute, CancellationToken cancellationToken)
    {
        var parent = absolute.LastIndexOf('/') is var slash and > 0 ? absolute[..slash] : "/";
        if (parent != "/")
        {
            await CreateAbsoluteAsync(parent, cancellationToken);
        }

        await _client.CallAsync("createfolderifnotexists", [new("path", absolute)], "", cancellationToken);
    }

    private string PathOf(string normalized) =>
        normalized.Length == 0 ? _root : _root == "/" ? "/" + normalized : $"{_root}/{normalized}";

    private static string NameOf(JsonElement item) => item.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";

    /// <summary>An entry from pCloud's metadata (modified as unix seconds with timeformat=timestamp, else as RFC 1123 text).</summary>
    internal static StorageEntry EntryOf(string path, JsonElement item)
    {
        var isFolder = item.TryGetProperty("isfolder", out var folder) && folder.ValueKind == JsonValueKind.True;
        var size = !isFolder && item.TryGetProperty("size", out var length) && length.TryGetInt64(out var bytes) ? bytes : 0;
        var modified = DateTimeOffset.UnixEpoch;
        if (item.TryGetProperty("modified", out var time))
        {
            if (time.ValueKind == JsonValueKind.Number && time.TryGetInt64(out var seconds))
            {
                modified = DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
            else if (time.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(time.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                modified = parsed.ToUniversalTime();
            }
        }

        return new StorageEntry(path, isFolder, size, modified)
        {
            NativeId = item.TryGetProperty("id", out var id) ? id.GetString() : null,
            IsHidden = StoragePath.NameOf(path).StartsWith('.'),
        };
    }
}
