using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using MatCloud.Contract;
using MatCloud.Contract.Drive;
using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;

namespace ThixSync.Core.Storage.Providers.MatCloud;

/// <summary>
/// A folder of ThixDrive, the Drive of the MatCloud account this computer is signed in with (Einstellungen › Cloud; ThixSync is a guest
/// app of the Drive since contract 1.7). Items of the Drive are reached by their stable id, never by a path: this class keeps the ids of
/// the paths it saw (listed folders, written files) and lists a folder when it meets a path it does not know. Names are unique per folder
/// whatever the case. A new version of a file is saved with If-Match (the ETag it knows); a file changed in between (412) or a name taken
/// meanwhile (409) is a transient error and the engine tries again with what the Drive has now. Large files go in pieces (the client of
/// the contract). Deleting moves into the trash of the Drive. The Drive keeps its own modification times (its upload time).
/// </summary>
internal sealed class MatCloudStorage : IStorage
{
    public const int Parallelism = 8;

    private readonly string _root;
    private readonly ConcurrentDictionary<string, DriveItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task>> _listed = new(StringComparer.OrdinalIgnoreCase);
    private DriveClients? _drive;
    private long? _rootId;
    private DriveItem? _rootItem;

    public MatCloudStorage(StorageContext context)
    {
        _root = StoragePath.Combine(StoragePath.Normalize(ConnectionCatalog.Value(context.Connection, ConnectionFields.BasePath)), StoragePath.Normalize(context.Folder));
        DisplayName = "MatCloud" + (_root.Length > 0 ? $"/{_root}" : "");
    }

    public ConnectionKind Kind => ConnectionKind.MatCloud;

    public StorageCapabilities Capabilities { get; private set; } = new()
    {
        Features = StorageFeatures.Move | StorageFeatures.RealFolders,
        MaxParallelism = Parallelism,
        HashKind = "sha256",
        InvalidNameCharacters = "/\\:*?\"<>|",
    };

    public string DisplayName { get; private set; }

    public async Task ConnectAsync(bool createRoot, CancellationToken cancellationToken)
    {
        EnsureAllowed();
        _drive ??= CloudSyncService.OpenDrive()
            ?? throw new StorageException(StorageErrorKind.Authentication, "Diesen Computer zuerst mit der MatCloud verbinden (Einstellungen › Cloud).");
        DisplayName = $"MatCloud ({_drive.UserName})" + (_root.Length > 0 ? $"/{_root}" : "");
        var quota = await CallAsync(token => _drive.Client.GetDriveQuotaAsync(token), "", cancellationToken);
        Capabilities = Capabilities with { MaxFileSize = quota.MaxFileBytes };

        long? folderId = null;
        DriveItem? folder = null;
        foreach (var name in _root.Length == 0 ? [] : _root.Split('/'))
        {
            var children = await ListChildrenAsync(folderId, "", cancellationToken);
            var match = children.FirstOrDefault(child => string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is { Kind: DriveItemKind.File })
            {
                throw new StorageException(StorageErrorKind.NotFound, $"{DisplayName}: „{name}“ ist eine Datei, kein Ordner.");
            }

            if (match is null && !createRoot)
            {
                throw new StorageException(StorageErrorKind.NotFound, $"Den Ordner {DisplayName} gibt es nicht.");
            }

            folder = match ?? (await CreateFolderInAsync(folderId, name, "", cancellationToken)).Item;
            folderId = folder.Id;
        }

        _rootId = folderId;
        _rootItem = folder;
    }

    public async IAsyncEnumerable<StorageEntry> ListAsync(string folder, Action<string, Exception>? onProblem, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var start = StoragePath.Normalize(folder);
        long? startId = _rootId;
        if (start.Length > 0)
        {
            var item = await FindAsync(start, cancellationToken);
            if (item is not { Kind: DriveItemKind.Folder })
            {
                throw new StorageException(StorageErrorKind.NotFound, $"Den Ordner gibt es nicht ({start}).");
            }

            startId = item.Id;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateUnbounded<StorageEntry>(new UnboundedChannelOptions { SingleReader = true });
        var slots = new SemaphoreSlim(Parallelism, Parallelism);
        var outstanding = 1;

        void Walk(string path, long? id) => _ = Task.Run(async () =>
        {
            try
            {
                List<DriveItem> children;
                await slots.WaitAsync(stop.Token);
                try
                {
                    children = await ListChildrenAsync(id, path, stop.Token);
                }
                finally
                {
                    slots.Release();
                }

                Remember(path, children);
                foreach (var child in children)
                {
                    var childPath = StoragePath.Combine(path, child.Name);
                    if (child.Kind == DriveItemKind.Folder)
                    {
                        Interlocked.Increment(ref outstanding);
                        Walk(childPath, child.Id);
                    }

                    channel.Writer.TryWrite(ToEntry(childPath, child));
                }
            }
            catch (StorageException exception) when (!exception.StopsRun && exception.Kind != StorageErrorKind.Unreachable && path != start)
            {
                onProblem?.Invoke(path, exception);
            }
            catch (Exception exception)
            {
                channel.Writer.TryComplete(exception);
            }
            finally
            {
                if (Interlocked.Decrement(ref outstanding) == 0)
                {
                    channel.Writer.TryComplete();
                }
            }
        }, CancellationToken.None);

        Walk(start, startId);
        try
        {
            await foreach (var entry in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return entry;
            }
        }
        finally
        {
            await stop.CancelAsync();
        }
    }

    public async Task<StorageEntry?> GetAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.Length == 0)
        {
            return _rootItem is { } root ? ToEntry("", root) : new StorageEntry("", true, 0, DateTimeOffset.UnixEpoch);
        }

        // what the cache knows may be old: ask the Drive for the item itself, and list the folder again when it moved or is gone
        if (await FindAsync(normalized, cancellationToken) is { } known)
        {
            var fresh = await TryGetItemAsync(known.Id, normalized, cancellationToken);
            if (fresh is { IsTrashed: false } && fresh.ParentId == known.ParentId && string.Equals(fresh.Name, StoragePath.NameOf(normalized), StringComparison.OrdinalIgnoreCase))
            {
                _items[normalized] = fresh;
                return ToEntry(normalized, fresh);
            }
        }

        var parent = StoragePath.ParentOf(normalized);
        if (await FolderIdAsync(parent, cancellationToken) is not { } parentFolder)
        {
            return null;
        }

        Remember(parent, await ListChildrenAsync(parentFolder.Id, parent, cancellationToken));
        return _items.TryGetValue(normalized, out var item) ? ToEntry(normalized, item) : null;
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        var item = await FindAsync(normalized, cancellationToken);
        if (item is not { Kind: DriveItemKind.File })
        {
            throw new StorageException(StorageErrorKind.NotFound, $"Nicht gefunden ({normalized}).");
        }

        var drive = Drive();
        HttpResponseMessage? response = null;
        try
        {
            response = await drive.Http.GetAsync(DriveContract.Content(item.Id).TrimStart('/'), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    Forget(normalized);
                }

                throw MapStatus(response.StatusCode, null, normalized);
            }

            var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            return new DownloadStream(response, body, response.Content.Headers.ContentLength ?? item.Size);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();
            throw Map(exception, normalized);
        }
        catch
        {
            response?.Dispose();
            throw;
        }
    }

    public async Task<StorageEntry> WriteAsync(string path, Stream content, long length, DateTimeOffset modified, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        var name = StoragePath.NameOf(normalized);
        if (normalized.Length == 0)
        {
            throw new StorageException(StorageErrorKind.InvalidName, "Eine Datei braucht einen Namen.");
        }

        if (Capabilities.MaxFileSize > 0 && length > Capabilities.MaxFileSize)
        {
            throw new StorageException(StorageErrorKind.QuotaExceeded, $"Die Datei ist zu groß für die MatCloud ({normalized}).");
        }

        var parentId = await EnsureFolderAsync(StoragePath.ParentOf(normalized), cancellationToken);
        var existing = await FindAsync(normalized, cancellationToken);
        if (existing is { Kind: DriveItemKind.Folder })
        {
            throw new StorageException(StorageErrorKind.AccessDenied, $"Dort liegt ein Ordner mit diesem Namen ({normalized}).");
        }

        var source = new UploadSource(content, length, progress);
        var drive = Drive();
        DriveItem saved;
        try
        {
            saved = existing is null
                ? await CallAsync(token => drive.Client.UploadDriveFileAsync(parentId, name, source, DriveNameConflict.Fail, token), normalized, cancellationToken)
                : await CallAsync(token => drive.Client.SaveDriveFileAsync(existing.Id, source, existing.ETag ?? DriveContract.AnyVersion, token), normalized, cancellationToken);
        }
        catch (StorageException exception) when (exception.InnerException is DriveConflictException conflict)
        {
            // another device created or saved the file in between: the next try works with the item as it is now
            if (conflict.Current is { Kind: DriveItemKind.File } current && string.Equals(current.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                _items[normalized] = current;
            }
            else
            {
                Forget(normalized);
            }

            throw;
        }
        catch (StorageException exception) when (exception.Kind == StorageErrorKind.NotFound)
        {
            Forget(normalized);
            throw;
        }

        _items[normalized] = saved;
        return ToEntry(normalized, saved);
    }

    public async Task CreateFolderAsync(string path, CancellationToken cancellationToken) =>
        await EnsureFolderAsync(StoragePath.Normalize(path), cancellationToken);

    public async Task DeleteAsync(string path, bool isFolder, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.Length == 0)
        {
            throw new StorageException(StorageErrorKind.AccessDenied, "Der Wurzelordner eines Auftrags wird nie gelöscht.");
        }

        var item = await FindAsync(normalized, cancellationToken);
        if (item is null)
        {
            return;
        }

        try
        {
            await CallAsync(token => Drive().Client.TrashDriveItemAsync(item.Id, token), normalized, cancellationToken);
        }
        catch (StorageException exception) when (exception.Kind == StorageErrorKind.NotFound)
        {
            // gone already
        }

        Forget(normalized);
    }

    public async Task MoveAsync(string fromPath, string toPath, CancellationToken cancellationToken)
    {
        var from = StoragePath.Normalize(fromPath);
        var to = StoragePath.Normalize(toPath);
        if (from.Length == 0 || to.Length == 0)
        {
            throw new StorageException(StorageErrorKind.AccessDenied, "Der Wurzelordner eines Auftrags wird nicht verschoben.");
        }

        var item = await FindAsync(from, cancellationToken) ?? throw new StorageException(StorageErrorKind.NotFound, $"Nicht gefunden ({from}).");
        var targetParentId = await EnsureFolderAsync(StoragePath.ParentOf(to), cancellationToken);
        var targetName = StoragePath.NameOf(to);

        // like on a disk: a file that is in the way is replaced (it goes into the trash)
        if (await FindAsync(to, cancellationToken) is { } inTheWay && inTheWay.Id != item.Id)
        {
            if (inTheWay.Kind == DriveItemKind.Folder || item.Kind == DriveItemKind.Folder)
            {
                throw new StorageException(StorageErrorKind.AccessDenied, $"Am Ziel gibt es schon „{targetName}“ ({to}).");
            }

            await DeleteAsync(to, false, cancellationToken);
        }

        var client = Drive().Client;
        DriveItem moved;
        if (item.ParentId == targetParentId)
        {
            moved = item.Name == targetName ? item : await CallAsync(token => client.RenameDriveItemAsync(item.Id, targetName, token), from, cancellationToken);
        }
        else if (item.Name == targetName)
        {
            moved = await CallAsync(token => client.MoveDriveItemAsync(item.Id, targetParentId, token), from, cancellationToken);
        }
        else
        {
            // a name of its own on the way, so neither the old nor the new folder can have it already
            var passing = $"{Local.LocalStorage.TempPrefix}{Guid.NewGuid():N}";
            await CallAsync(token => client.RenameDriveItemAsync(item.Id, passing, token), from, cancellationToken);
            try
            {
                await CallAsync(token => client.MoveDriveItemAsync(item.Id, targetParentId, token), from, cancellationToken);
                moved = await CallAsync(token => client.RenameDriveItemAsync(item.Id, targetName, token), to, cancellationToken);
            }
            catch (StorageException)
            {
                Forget(from);
                await TryRenameAsync(item.Id, item.Name);
                throw;
            }
        }

        Forget(from);
        Forget(to);
        _items[to] = moved;
    }

    /// <summary>The folders directly below <paramref name="folder"/> (the folder picker).</summary>
    public async Task<IReadOnlyList<string>> ListFoldersAsync(string folder, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(folder);
        var parent = await FolderIdAsync(normalized, cancellationToken)
            ?? throw new StorageException(StorageErrorKind.NotFound, $"Den Ordner gibt es nicht ({normalized}).");
        var children = await ListChildrenAsync(parent.Id, normalized, cancellationToken);
        return [.. children.Where(child => child.Kind == DriveItemKind.Folder).Select(child => child.Name).Order(StringComparer.CurrentCultureIgnoreCase)];
    }

    public ValueTask DisposeAsync()
    {
        _drive?.Dispose();
        _drive = null;
        return ValueTask.CompletedTask;
    }

    // ---- paths and ids ----

    /// <summary>The item at a path below the root (lists its folder when it is not known yet); null when there is none.</summary>
    private async Task<DriveItem?> FindAsync(string path, CancellationToken cancellationToken)
    {
        if (_items.TryGetValue(path, out var known))
        {
            return known;
        }

        var parent = StoragePath.ParentOf(path);
        if (await FolderIdAsync(parent, cancellationToken) is not { } folder)
        {
            return null;
        }

        await EnsureListedAsync(parent, folder.Id, cancellationToken);
        return _items.GetValueOrDefault(path);
    }

    /// <summary>The id of a folder below the root ("" = the root, whose id is null at the top of the Drive); null when there is no such folder.</summary>
    private async Task<FolderRef?> FolderIdAsync(string folder, CancellationToken cancellationToken)
    {
        if (folder.Length == 0)
        {
            return new FolderRef(_rootId);
        }

        return await FindAsync(folder, cancellationToken) is { Kind: DriveItemKind.Folder } item ? new FolderRef(item.Id) : null;
    }

    /// <summary>Lists a folder once (also when many workers ask for it at the same time); a failed listing is tried again by the next one.</summary>
    private async Task EnsureListedAsync(string folder, long? folderId, CancellationToken cancellationToken)
    {
        var listing = _listed.GetOrAdd(folder, _ => new Lazy<Task>(async () => Remember(folder, await ListChildrenAsync(folderId, folder, CancellationToken.None))));
        try
        {
            await listing.Value.WaitAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _listed.TryRemove(new KeyValuePair<string, Lazy<Task>>(folder, listing));
            throw;
        }
    }

    /// <summary>The folder (and its parents) made where missing; returns its id.</summary>
    private async Task<long?> EnsureFolderAsync(string folder, CancellationToken cancellationToken)
    {
        if (folder.Length == 0)
        {
            return _rootId;
        }

        var existing = await FindAsync(folder, cancellationToken);
        if (existing is { Kind: DriveItemKind.Folder })
        {
            return existing.Id;
        }

        if (existing is not null)
        {
            throw new StorageException(StorageErrorKind.AccessDenied, $"Dort liegt eine Datei mit dem Namen des Ordners ({folder}).");
        }

        var parentId = await EnsureFolderAsync(StoragePath.ParentOf(folder), cancellationToken);
        var (item, isNew) = await CreateFolderInAsync(parentId, StoragePath.NameOf(folder), folder, cancellationToken);
        _items[folder] = item;
        if (isNew)
        {
            _listed.TryAdd(folder, new Lazy<Task>(Task.CompletedTask)); // empty: nothing to list
        }

        return item.Id;
    }

    /// <summary>A new folder; when another worker or device made it a moment before (409), that one (not new: it may have items).</summary>
    private async Task<(DriveItem Item, bool IsNew)> CreateFolderInAsync(long? parentId, string name, string path, CancellationToken cancellationToken)
    {
        try
        {
            return (await CallAsync(token => Drive().Client.CreateDriveFolderAsync(parentId, name, token), path, cancellationToken), true);
        }
        catch (StorageException exception) when (exception.InnerException is DriveConflictException { Current: { Kind: DriveItemKind.Folder } current })
        {
            return (current, false);
        }
    }

    /// <summary>Every item of a folder (all pages).</summary>
    private async Task<List<DriveItem>> ListChildrenAsync(long? folderId, string path, CancellationToken cancellationToken)
    {
        var items = new List<DriveItem>();
        while (true)
        {
            var offset = items.Count;
            var page = await CallAsync(token => Drive().Client.ListDriveFolderAsync(folderId, token, DriveSort.Name, false, offset, DriveContract.MaxPageSize),
                path, cancellationToken);
            items.AddRange(page.Items);
            if (page.Items.Count == 0 || items.Count >= page.Total)
            {
                return items;
            }
        }
    }

    private async Task<DriveItem?> TryGetItemAsync(long id, string path, CancellationToken cancellationToken)
    {
        try
        {
            return await CallAsync(token => Drive().Client.GetDriveItemAsync(id, token), path, cancellationToken);
        }
        catch (StorageException exception) when (exception.Kind == StorageErrorKind.NotFound)
        {
            return null;
        }
    }

    private async Task TryRenameAsync(long id, string name)
    {
        try
        {
            await Drive().Client.RenameDriveItemAsync(id, name, CancellationToken.None);
        }
        catch (Exception exception) when (exception is CloudException or DriveConflictException or HttpRequestException or TaskCanceledException)
        {
            Log.Warning($"MatCloud: an item could not get its name back after a failed move ({exception.GetType().Name})");
        }
    }

    /// <summary>Keeps the children of a listed folder (and drops what the folder no longer has).</summary>
    private void Remember(string folder, List<DriveItem> children)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in children)
        {
            var path = StoragePath.Combine(folder, child.Name);
            names.Add(path);
            _items[path] = child;
        }

        foreach (var path in _items.Keys)
        {
            if (string.Equals(StoragePath.ParentOf(path), folder, StringComparison.OrdinalIgnoreCase) && !names.Contains(path))
            {
                Forget(path);
            }
        }

        _listed.TryAdd(folder, new Lazy<Task>(Task.CompletedTask));
    }

    /// <summary>Forgets a path and everything below it (deleted, moved).</summary>
    private void Forget(string path)
    {
        foreach (var key in _items.Keys)
        {
            if (StoragePath.IsInside(key, path, StringComparison.OrdinalIgnoreCase))
            {
                _items.TryRemove(key, out _);
            }
        }

        foreach (var key in _listed.Keys)
        {
            if (StoragePath.IsInside(key, path, StringComparison.OrdinalIgnoreCase))
            {
                _listed.TryRemove(key, out _);
            }
        }
    }

    private static StorageEntry ToEntry(string path, DriveItem item) =>
        new(path, item.Kind == DriveItemKind.Folder, item.Kind == DriveItemKind.Folder ? 0 : item.Size, item.ModifiedAt)
        {
            Hash = item.Sha256 is { Length: > 0 } sha256 ? $"sha256:{sha256}" : null,
            NativeId = item.Id.ToString(CultureInfo.InvariantCulture),
            IsHidden = item.Name.StartsWith('.'),
        };

    // ---- calls and errors ----

    private DriveClients Drive()
    {
        EnsureAllowed();
        return _drive ?? throw new InvalidOperationException("ConnectAsync first.");
    }

    private static void EnsureAllowed()
    {
        if (AccessPolicy.IsCloudDisabled)
        {
            throw new StorageException(StorageErrorKind.AccessDenied, "Die MatCloud wurde von einem Administrator deaktiviert (Richtlinie DisableCloud).");
        }
    }

    /// <summary>A call of the contract; its errors as <see cref="StorageException"/> (cancellation by the caller stays what it is).</summary>
    private static async Task<T> CallAsync<T>(Func<CancellationToken, Task<T>> call, string path, CancellationToken cancellationToken)
    {
        try
        {
            return await call(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw Map(exception, path);
        }
    }

    internal static StorageException Map(Exception exception, string path)
    {
        var where = path.Length == 0 ? "" : $" ({path})";
        return exception switch
        {
            StorageException storage => storage,
            DriveConflictException conflict => new StorageException(StorageErrorKind.Transient,
                $"In der MatCloud wurde gleichzeitig etwas geändert{where} – wird erneut versucht.", conflict),
            CloudException cloud => MapStatus(cloud.StatusCode, cloud.Message, path, cloud),
            HttpRequestException { InnerException: StorageException inner } => inner,
            HttpRequestException => new StorageException(StorageErrorKind.Unreachable, "Die MatCloud ist nicht erreichbar.", exception),
            OperationCanceledException => new StorageException(StorageErrorKind.Transient, "Die MatCloud antwortet nicht rechtzeitig.", exception),
            NotSupportedException => new StorageException(StorageErrorKind.Transient, $"Die Übertragung brach ab und lässt sich nicht fortsetzen{where}.", exception),
            JsonException => new StorageException(StorageErrorKind.Transient, "Die MatCloud hat eine unverständliche Antwort geschickt.", exception),
            IOException => new StorageException(StorageErrorKind.Transient, $"Die Verbindung zur MatCloud wurde unterbrochen{where}.", exception),
            _ => new StorageException(StorageErrorKind.Unknown, $"{exception.Message}{where}", exception),
        };
    }

    private static StorageException MapStatus(HttpStatusCode status, string? message, string path, Exception? inner = null)
    {
        var where = path.Length == 0 ? "" : $" ({path})";
        return (int)status switch
        {
            401 => new StorageException(StorageErrorKind.Authentication, "Die MatCloud-Anmeldung ist abgelaufen – Einstellungen › Cloud", inner),
            403 => new StorageException(StorageErrorKind.AccessDenied,
                "Die MatCloud gibt ThixSync keinen Zugriff auf ThixDrive – der Server muss ThixSync kennen (MatCloud mit Vertrag 1.7 oder neuer) und " +
                "ThixDrive muss für das Konto eingeschaltet sein.", inner),
            404 => new StorageException(StorageErrorKind.NotFound, $"Nicht gefunden{where}.", inner),
            413 => new StorageException(StorageErrorKind.QuotaExceeded, $"Die Datei ist zu groß für die MatCloud{where}.", inner),
            507 => new StorageException(StorageErrorKind.QuotaExceeded, "Der Speicherplatz in der MatCloud ist voll.", inner),
            429 => new StorageException(StorageErrorKind.RateLimited, "Die MatCloud bittet, kurz zu warten.", inner) { RetryAfter = TimeSpan.FromSeconds(10) },
            400 => new StorageException(StorageErrorKind.InvalidName, $"{message ?? "Die MatCloud nimmt diesen Namen nicht an."}{where}", inner),
            408 or >= 500 => new StorageException(StorageErrorKind.Transient, $"Die MatCloud hat gerade ein Problem (Fehler {(int)status}).", inner),
            _ => new StorageException(StorageErrorKind.Unknown, $"{message ?? $"Die MatCloud antwortet mit Fehler {(int)status}."}{where}", inner),
        };
    }

    private sealed record FolderRef(long? Id);
}
