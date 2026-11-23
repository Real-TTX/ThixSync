using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using ThixSync.Core.Models;
using ThixSync.Core.Storage.Providers.OAuth;

namespace ThixSync.Core.Storage.Providers.GoogleDrive;

/// <summary>
/// A folder of Google Drive (My Drive, or a shared drive given by name or id), Drive API v3. Lists the WHOLE drive in a few pages of
/// 1000 and builds the tree below the root from the parents (much faster than one query per folder). Google documents (Docs, Sheets …)
/// are no files and are left out (reported once); two items with the same name in one folder (Drive allows it) yield the newest one.
/// Uploads: multipart up to 5 MiB, resumable in 8 MiB chunks above, the modification time in the metadata; a changed file gets a new
/// version of the same file (sharing and history stay). Deleting moves to the trash of Drive (30 days to undo). Hash: md5.
/// </summary>
internal sealed class GoogleDriveStorage : IStorage, IChangeFeed
{
    private const string Api = "https://www.googleapis.com/drive/v3";
    private const string UploadApi = "https://www.googleapis.com/upload/drive/v3";
    private const string FolderType = "application/vnd.google-apps.folder";
    private const string FileFields = "id,name,mimeType,parents,size,modifiedTime,md5Checksum";

    private readonly CloudApi _api;
    private readonly string _rootPath;
    private readonly string _sharedDrive;
    private readonly PathIds _ids = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private string? _driveId;
    private string? _rootId;

    public GoogleDriveStorage(StorageContext context, OAuthApp app)
    {
        _rootPath = OAuthApp.RootOf(context);
        _sharedDrive = context.Connection.Get(ConnectionFields.SharedDrive).Trim();
        _api = new CloudApi("Google Drive", OAuthTokenSource.For(context, app), Capabilities.MaxParallelism, Describe);
        var account = context.Connection.Get(ConnectionFields.SignedInAs);
        var where = _sharedDrive.Length > 0 ? $"Google Drive – {_sharedDrive}" : account.Length > 0 ? $"Google Drive ({account})" : "Google Drive";
        DisplayName = _rootPath.Length > 0 ? $"{where}/{_rootPath}" : where;
    }

    /// <summary>Files up to this size go in one multipart request (tests make it small).</summary>
    internal static long SimpleUploadLimit { get; set; } = 5L * 1024 * 1024;

    /// <summary>The chunk of a resumable upload (a multiple of 256 KiB).</summary>
    internal static int ChunkSize { get; set; } = 8 * 1024 * 1024;

    public ConnectionKind Kind => ConnectionKind.GoogleDrive;

    public StorageCapabilities Capabilities { get; } = new()
    {
        Features = StorageFeatures.SetModifiedTime | StorageFeatures.Move | StorageFeatures.RealFolders | StorageFeatures.ChangeFeed | StorageFeatures.CaseSensitive,
        MaxParallelism = 8,
        HashKind = "md5",
        MaxFileSize = 5L * 1024 * 1024 * 1024 * 1024,
    };

    public string DisplayName { get; }

    public async Task ConnectAsync(bool createRoot, CancellationToken cancellationToken)
    {
        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            if (_rootId is not null)
            {
                return;
            }

            var topId = _sharedDrive.Length > 0 ? await FindSharedDriveAsync(cancellationToken) : await MyDriveRootAsync(cancellationToken);
            var id = topId;
            var walked = "";
            foreach (var part in _rootPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                walked = StoragePath.Combine(walked, part);
                var found = Newest(await FindChildrenAsync(id, part, foldersOnly: true, cancellationToken));
                if (found is not null)
                {
                    id = found.Id;
                    continue;
                }

                if (!createRoot)
                {
                    throw new StorageException(StorageErrorKind.NotFound, $"Den Ordner „{walked}“ gibt es bei Google Drive nicht.");
                }

                id = await CreateFolderInAsync(id, part, cancellationToken);
            }

            _rootId = id;
            _ids.Set("", id, isFolder: true);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async IAsyncEnumerable<StorageEntry> ListAsync(string folder, Action<string, Exception>? onProblem, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var start = StoragePath.Normalize(folder);
        var startId = await ResolveAsync(start, cancellationToken);
        if (startId is not { IsFolder: true })
        {
            throw new StorageException(StorageErrorKind.NotFound, $"Google Drive: Den Ordner „{start}“ gibt es nicht.");
        }

        var items = new List<CloudItem>();
        string? pageToken = null;
        do
        {
            var url = $"{Api}/files?q={Uri.EscapeDataString("trashed = false")}&pageSize=1000&fields={Uri.EscapeDataString($"nextPageToken,files({FileFields})")}{Corpora()}" +
                      (pageToken is null ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");
            var page = await _api.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
            items.AddRange(CloudJson.Items(page, "files").Select(ToItem));
            pageToken = CloudJson.Text(page, "nextPageToken") is { Length: > 0 } next ? next : null;
        }
        while (pageToken is not null);

        var entries = CloudTree.Build(items, startId.Value.Id, start, StringComparer.Ordinal, "Google Drive", onProblem);
        _ids.MarkComplete(start);
        foreach (var entry in entries)
        {
            _ids.Set(entry.Path, entry.NativeId!, entry.IsFolder);
            if (entry.IsFolder)
            {
                _ids.MarkComplete(entry.Path);
            }
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
        }
    }

    public async Task<StorageEntry?> GetAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.Length == 0)
        {
            await ConnectAsync(false, cancellationToken);
            return new StorageEntry("", true, 0, DateTimeOffset.MinValue) { NativeId = _rootId };
        }

        var parent = await ResolveAsync(StoragePath.ParentOf(normalized), cancellationToken);
        if (parent is not { IsFolder: true })
        {
            return null;
        }

        var found = Newest(await FindChildrenAsync(parent.Value.Id, StoragePath.NameOf(normalized), foldersOnly: false, cancellationToken));
        if (found is null)
        {
            _ids.Remove(normalized);
            return null;
        }

        _ids.Set(normalized, found.Id, found.IsFolder);
        return ToEntry(normalized, found);
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        for (var attempt = 0; ; attempt++)
        {
            var id = await ResolveAsync(normalized, cancellationToken);
            if (id is not { IsFolder: false })
            {
                throw new StorageException(StorageErrorKind.NotFound, $"Google Drive: Die Datei „{normalized}“ gibt es nicht.");
            }

            try
            {
                var url = $"{Api}/files/{id.Value.Id}?alt=media&supportsAllDrives=true";
                var response = await _api.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken, path: normalized);
                return await CloudStreams.OpenAsync(response, cancellationToken);
            }
            catch (StorageException exception) when (exception.Kind == StorageErrorKind.NotFound && attempt == 0)
            {
                _ids.Invalidate(normalized);
            }
        }
    }

    public async Task<StorageEntry> WriteAsync(string path, Stream content, long length, DateTimeOffset modified, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.Length == 0)
        {
            throw new StorageException(StorageErrorKind.InvalidName, "Google Drive: Eine Datei braucht einen Namen.");
        }

        var parentId = await EnsureFolderAsync(StoragePath.ParentOf(normalized), cancellationToken);
        var existing = await ResolveAsync(normalized, cancellationToken);
        if (existing is { IsFolder: true })
        {
            throw new StorageException(StorageErrorKind.Unknown, $"Google Drive: „{normalized}“ ist ein Ordner.");
        }

        var metadata = new JsonObject { ["name"] = StoragePath.NameOf(normalized), ["modifiedTime"] = CloudJson.Rfc3339(modified) };
        if (existing is null)
        {
            metadata["parents"] = new JsonArray(parentId);
        }

        JsonNode file;
        if (length <= SimpleUploadLimit)
        {
            var bytes = await CloudStreams.ReadSmallAsync(content, length, normalized, cancellationToken);
            file = await UploadSmallAsync(normalized, existing?.Id, metadata, bytes, parentId, cancellationToken);
            progress?.Report(length);
        }
        else
        {
            file = await UploadResumableAsync(normalized, existing?.Id, metadata, content, length, parentId, progress, cancellationToken);
        }

        var item = ToItem(file);
        _ids.Set(normalized, item.Id, isFolder: false);
        return ToEntry(normalized, item);
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

        var id = await ResolveAsync(normalized, cancellationToken);
        if (id is null)
        {
            return;
        }

        try
        {
            await TrashAsync(id.Value.Id, normalized, cancellationToken);
        }
        catch (StorageException exception) when (exception.Kind == StorageErrorKind.NotFound)
        {
        }

        _ids.Remove(normalized);
    }

    public async Task MoveAsync(string fromPath, string toPath, CancellationToken cancellationToken)
    {
        var from = StoragePath.Normalize(fromPath);
        var to = StoragePath.Normalize(toPath);
        var source = await ResolveAsync(from, cancellationToken)
            ?? throw new StorageException(StorageErrorKind.NotFound, $"Google Drive: „{from}“ gibt es nicht.");
        var fromParent = await ResolveAsync(StoragePath.ParentOf(from), cancellationToken);
        var toParentId = await EnsureFolderAsync(StoragePath.ParentOf(to), cancellationToken);
        var target = await ResolveAsync(to, cancellationToken);
        if (target is { } existing && existing.Id != source.Id)
        {
            if (existing.IsFolder)
            {
                throw new StorageException(StorageErrorKind.Unknown, $"Google Drive: Am Ziel „{to}“ gibt es schon einen Ordner.");
            }

            await TrashAsync(existing.Id, to, cancellationToken);
        }

        var url = $"{Api}/files/{source.Id}?supportsAllDrives=true&fields=id";
        if (fromParent is { } oldParent && oldParent.Id != toParentId)
        {
            url += $"&addParents={toParentId}&removeParents={oldParent.Id}";
        }

        var body = new JsonObject { ["name"] = StoragePath.NameOf(to) };
        await _api.SendJsonAsync(() => CloudApi.Json(HttpMethod.Patch, url, body), cancellationToken, path: from);
        _ids.Remove(from);
        _ids.Remove(to);
        _ids.Set(to, source.Id, source.IsFolder);
    }

    public async Task<string> GetCursorAsync(CancellationToken cancellationToken)
    {
        await ConnectAsync(false, cancellationToken);
        var url = $"{Api}/changes/startPageToken?supportsAllDrives=true{DriveParameter()}";
        var answer = await _api.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
        return CloudJson.Text(answer, "startPageToken");
    }

    public async Task<bool> HasChangesAsync(string cursor, CancellationToken cancellationToken)
    {
        await ConnectAsync(false, cancellationToken);
        var ancestry = new CloudAncestry(_rootId!, ParentsOfAsync);
        var pageToken = cursor;
        for (var page = 0; page < 20; page++)
        {
            var url = $"{Api}/changes?pageToken={Uri.EscapeDataString(pageToken)}&pageSize=1000&includeRemoved=true&supportsAllDrives=true" +
                      $"&includeItemsFromAllDrives=true{DriveParameter()}&fields={Uri.EscapeDataString("nextPageToken,newStartPageToken,changes(fileId,removed,file(id,parents))")}";
            JsonNode answer;
            try
            {
                answer = await _api.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
            }
            catch (StorageException exception) when (exception.Kind is StorageErrorKind.NotFound or StorageErrorKind.Unknown)
            {
                return true; // the cursor is no longer valid
            }

            foreach (var change in CloudJson.Items(answer, "changes"))
            {
                var fileId = CloudJson.Text(change, "fileId");
                if (fileId.Length == 0)
                {
                    continue; // a change of the shared drive itself (name, theme)
                }

                if (CloudJson.Flag(change, "removed") || change["file"] is null)
                {
                    return true; // gone for good: where it was is unknown
                }

                var parents = CloudJson.Items(change["file"], "parents").Select(parent => parent.GetValue<string>()).ToList();
                if (await ancestry.IsBelowRootAsync(fileId, parents, cancellationToken))
                {
                    return true;
                }
            }

            if (CloudJson.Text(answer, "nextPageToken") is not { Length: > 0 } next)
            {
                return false;
            }

            pageToken = next;
        }

        return true;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The folders directly below a folder (relative to the root of this storage), for the folder picker.</summary>
    public async Task<IReadOnlyList<string>> ListFoldersAsync(string folder, CancellationToken cancellationToken)
    {
        var parent = await ResolveAsync(StoragePath.Normalize(folder), cancellationToken);
        if (parent is not { IsFolder: true })
        {
            throw new StorageException(StorageErrorKind.NotFound, $"Google Drive: Den Ordner „{folder}“ gibt es nicht.");
        }

        var names = new List<string>();
        string? pageToken = null;
        do
        {
            var query = $"'{parent.Value.Id}' in parents and mimeType = '{FolderType}' and trashed = false";
            var url = $"{Api}/files?q={Uri.EscapeDataString(query)}&pageSize=1000&fields={Uri.EscapeDataString("nextPageToken,files(name)")}{Corpora()}" +
                      (pageToken is null ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");
            var page = await _api.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
            names.AddRange(CloudJson.Items(page, "files").Select(file => CloudJson.Text(file, "name")));
            pageToken = CloudJson.Text(page, "nextPageToken") is { Length: > 0 } next ? next : null;
        }
        while (pageToken is not null);

        return [.. names.Distinct(StringComparer.Ordinal).Order(StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>The signed-in account (e-mail, else the name) for a fresh access token.</summary>
    internal static async Task<string> ReadAccountAsync(CloudApi api, CancellationToken cancellationToken)
    {
        var url = $"{Api}/about?fields={Uri.EscapeDataString("user(emailAddress,displayName)")}";
        var about = await api.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
        var email = CloudJson.Text(about["user"], "emailAddress");
        return email.Length > 0 ? email : CloudJson.Text(about["user"], "displayName");
    }

    /// <summary>What Drive's error answers mean ({"error": {"errors": [{"reason": …}], "message": …}}).</summary>
    internal static CloudError Describe(HttpStatusCode status, JsonNode? body)
    {
        var error = body?["error"];
        var reason = CloudJson.Items(error, "errors").Select(item => CloudJson.Text(item, "reason")).FirstOrDefault() ?? "";
        var detail = reason.Length > 0 ? reason : CloudJson.Text(error, "message");
        StorageErrorKind? kind = reason switch
        {
            "rateLimitExceeded" or "userRateLimitExceeded" or "sharingRateLimitExceeded" => StorageErrorKind.RateLimited,
            "storageQuotaExceeded" or "teamDriveFileLimitExceeded" or "numChildrenInNonRootLimitExceeded" => StorageErrorKind.QuotaExceeded,
            "notFound" => StorageErrorKind.NotFound,
            "insufficientFilePermissions" or "insufficientPermissions" or "domainPolicy" => StorageErrorKind.AccessDenied,
            "authError" => StorageErrorKind.Authentication,
            "backendError" or "internalError" => StorageErrorKind.Transient,
            _ => null,
        };
        return new CloudError(kind, detail);
    }

    private static CloudItem ToItem(JsonNode file)
    {
        var mimeType = CloudJson.Text(file, "mimeType");
        var isFolder = mimeType == FolderType;
        var md5 = CloudJson.Text(file, "md5Checksum");
        return new CloudItem(
            CloudJson.Text(file, "id"),
            CloudJson.Text(file, "name"),
            [.. CloudJson.Items(file, "parents").Select(parent => parent.GetValue<string>())],
            isFolder,
            CloudJson.Number(file, "size"),
            CloudJson.Time(file, "modifiedTime"))
        {
            Hash = md5.Length > 0 ? $"md5:{md5}" : null,
            IsSkipped = !isFolder && mimeType.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal),
        };
    }

    private static StorageEntry ToEntry(string path, CloudItem item) =>
        new(path, item.IsFolder, item.IsFolder ? 0 : item.Size, item.Modified) { Hash = item.Hash, NativeId = item.Id, IsHidden = item.Name.StartsWith('.') };

    private static CloudItem? Newest(IEnumerable<CloudItem> items) =>
        items.Where(item => !item.IsSkipped).OrderByDescending(item => item.Modified).ThenBy(item => item.Id, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>"'" and "\" escaped for a Drive query.</summary>
    private static string Quote(string name) => name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    private string Corpora() => _driveId is null
        ? "&corpora=user&supportsAllDrives=true"
        : $"&corpora=drive&driveId={Uri.EscapeDataString(_driveId)}&includeItemsFromAllDrives=true&supportsAllDrives=true";

    private string DriveParameter() => _driveId is null ? "" : $"&driveId={Uri.EscapeDataString(_driveId)}";

    private async Task<string> MyDriveRootAsync(CancellationToken cancellationToken)
    {
        var root = await _api.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{Api}/files/root?fields=id"), cancellationToken);
        return CloudJson.Text(root, "id");
    }

    /// <summary>The shared drive named (or with the id) in the connection; its id is also the id of its top folder.</summary>
    private async Task<string> FindSharedDriveAsync(CancellationToken cancellationToken)
    {
        string? pageToken = null;
        do
        {
            var url = $"{Api}/drives?pageSize=100&fields={Uri.EscapeDataString("nextPageToken,drives(id,name)")}" +
                      (pageToken is null ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");
            var page = await _api.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
            foreach (var drive in CloudJson.Items(page, "drives"))
            {
                var id = CloudJson.Text(drive, "id");
                if (id == _sharedDrive || string.Equals(CloudJson.Text(drive, "name"), _sharedDrive, StringComparison.CurrentCultureIgnoreCase))
                {
                    _driveId = id;
                    return id;
                }
            }

            pageToken = CloudJson.Text(page, "nextPageToken") is { Length: > 0 } next ? next : null;
        }
        while (pageToken is not null);

        throw new StorageException(StorageErrorKind.NotFound, $"Die geteilte Ablage „{_sharedDrive}“ gibt es nicht (oder dieses Konto hat keinen Zugriff).");
    }

    /// <summary>The id of a path: from the cache, else asked folder by folder (null: not there).</summary>
    private async Task<CachedId?> ResolveAsync(string path, CancellationToken cancellationToken)
    {
        if (_rootId is null)
        {
            await ConnectAsync(false, cancellationToken);
        }

        if (path.Length == 0)
        {
            return new CachedId(_rootId!, IsFolder: true);
        }

        if (_ids.TryGet(path, out var cached))
        {
            return cached;
        }

        var parentPath = StoragePath.ParentOf(path);
        var parent = await ResolveAsync(parentPath, cancellationToken);
        if (parent is not { IsFolder: true } || _ids.IsComplete(parentPath))
        {
            return null;
        }

        var found = Newest(await FindChildrenAsync(parent.Value.Id, StoragePath.NameOf(path), foldersOnly: false, cancellationToken));
        if (found is null)
        {
            return null;
        }

        _ids.Set(path, found.Id, found.IsFolder);
        return new CachedId(found.Id, found.IsFolder);
    }

    private async Task<List<CloudItem>> FindChildrenAsync(string parentId, string name, bool foldersOnly, CancellationToken cancellationToken)
    {
        var query = $"'{parentId}' in parents and name = '{Quote(name)}' and trashed = false" + (foldersOnly ? $" and mimeType = '{FolderType}'" : "");
        var url = $"{Api}/files?q={Uri.EscapeDataString(query)}&pageSize=100&fields={Uri.EscapeDataString($"files({FileFields})")}{Corpora()}";
        var answer = await _api.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
        return [.. CloudJson.Items(answer, "files").Select(ToItem)];
    }

    /// <summary>The id of a folder, created with its missing parents (once, however many workers need it at the same time).</summary>
    private async Task<string> EnsureFolderAsync(string path, CancellationToken cancellationToken)
    {
        if (_rootId is null)
        {
            await ConnectAsync(false, cancellationToken);
        }

        if (_ids.TryGet(path, out var cached) && cached.IsFolder)
        {
            return cached.Id;
        }

        var parentPath = StoragePath.ParentOf(path);
        var parentId = await EnsureFolderAsync(parentPath, cancellationToken);
        using (await _ids.LockAsync(path, cancellationToken))
        {
            if (_ids.TryGet(path, out cached) && cached.IsFolder)
            {
                return cached.Id;
            }

            if (!_ids.IsComplete(parentPath))
            {
                var found = Newest(await FindChildrenAsync(parentId, StoragePath.NameOf(path), foldersOnly: true, cancellationToken));
                if (found is not null)
                {
                    _ids.Set(path, found.Id, isFolder: true);
                    return found.Id;
                }
            }

            var id = await CreateFolderInAsync(parentId, StoragePath.NameOf(path), cancellationToken);
            _ids.Set(path, id, isFolder: true);
            _ids.MarkComplete(path);
            return id;
        }
    }

    private async Task<string> CreateFolderInAsync(string parentId, string name, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["name"] = name, ["mimeType"] = FolderType, ["parents"] = new JsonArray(parentId) };
        // Not sent twice by itself: a second attempt after a lost answer would make a second folder of the same name.
        var created = await _api.SendJsonAsync(() => CloudApi.Json(HttpMethod.Post, $"{Api}/files?supportsAllDrives=true&fields=id", body), cancellationToken,
            replayable: false, path: name);
        return CloudJson.Text(created, "id");
    }

    private Task TrashAsync(string id, string path, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["trashed"] = true };
        return _api.SendJsonAsync(() => CloudApi.Json(HttpMethod.Patch, $"{Api}/files/{id}?supportsAllDrives=true&fields=id", body), cancellationToken, path: path);
    }

    private async Task<IReadOnlyList<string>?> ParentsOfAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            var file = await _api.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{Api}/files/{id}?supportsAllDrives=true&fields=parents"), cancellationToken);
            return [.. CloudJson.Items(file, "parents").Select(parent => parent.GetValue<string>())];
        }
        catch (StorageException exception) when (exception.Kind == StorageErrorKind.NotFound)
        {
            return null;
        }
    }

    private async Task<JsonNode> UploadSmallAsync(string path, string? existingId, JsonObject metadata, byte[] bytes, string parentId, CancellationToken cancellationToken)
    {
        HttpRequestMessage Build(HttpMethod method, string url)
        {
            var multipart = new MultipartContent("related")
            {
                new StringContent(metadata.ToJsonString(), Encoding.UTF8, "application/json"),
                new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } },
            };
            return new HttpRequestMessage(method, url) { Content = multipart };
        }

        if (existingId is not null)
        {
            try
            {
                var updateUrl = $"{UploadApi}/files/{existingId}?uploadType=multipart&supportsAllDrives=true&fields={Uri.EscapeDataString(FileFields)}";
                return await _api.SendJsonAsync(() => Build(HttpMethod.Patch, updateUrl), cancellationToken, path: path);
            }
            catch (StorageException exception) when (exception.Kind == StorageErrorKind.NotFound)
            {
                _ids.Invalidate(path); // gone meanwhile: a new file
                metadata["parents"] = new JsonArray(parentId);
            }
        }

        var createUrl = $"{UploadApi}/files?uploadType=multipart&supportsAllDrives=true&fields={Uri.EscapeDataString(FileFields)}";
        return await _api.SendJsonAsync(() => Build(HttpMethod.Post, createUrl), cancellationToken, replayable: false, path: path);
    }

    private async Task<JsonNode> UploadResumableAsync(string path, string? existingId, JsonObject metadata, Stream content, long length, string parentId,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        string session;
        try
        {
            session = await StartSessionAsync(path, existingId, metadata, length, cancellationToken);
        }
        catch (StorageException exception) when (exception.Kind == StorageErrorKind.NotFound && existingId is not null)
        {
            _ids.Invalidate(path); // gone meanwhile: a new file
            metadata["parents"] = new JsonArray(parentId);
            session = await StartSessionAsync(path, null, metadata, length, cancellationToken);
        }

        var buffer = new byte[ChunkSize];
        var offset = 0L;
        while (true)
        {
            var count = (int)Math.Min(ChunkSize, length - offset);
            await CloudStreams.ReadExactlyAsync(content, buffer, count, path, cancellationToken);
            var finished = await SendChunkAsync(session, buffer, offset, count, length, path, cancellationToken);
            progress?.Report(count);
            offset += count;
            if (finished is not null)
            {
                return finished;
            }

            if (offset >= length)
            {
                throw new StorageException(StorageErrorKind.Transient, $"Google Drive: Der Upload von „{path}“ wurde nicht abgeschlossen.");
            }
        }
    }

    /// <summary>Opens a resumable upload (no file is made yet: it may be sent again) and returns its session address.</summary>
    private async Task<string> StartSessionAsync(string path, string? existingId, JsonObject metadata, long length, CancellationToken cancellationToken)
    {
        var url = existingId is null
            ? $"{UploadApi}/files?uploadType=resumable&supportsAllDrives=true&fields={Uri.EscapeDataString(FileFields)}"
            : $"{UploadApi}/files/{existingId}?uploadType=resumable&supportsAllDrives=true&fields={Uri.EscapeDataString(FileFields)}";
        using var started = await _api.SendAsync(() =>
        {
            var request = CloudApi.Json(existingId is null ? HttpMethod.Post : HttpMethod.Patch, url, metadata);
            request.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
            request.Headers.Add("X-Upload-Content-Length", length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return request;
        }, cancellationToken, path: path);
        return started.Headers.Location?.ToString()
            ?? throw new StorageException(StorageErrorKind.Transient, $"Google Drive: Der Upload von „{path}“ konnte nicht beginnen.");
    }

    /// <summary>
    /// Sends one chunk; after a failed attempt asks Drive how far it got and sends the rest of the chunk (up to 3 attempts). Returns the
    /// file when it was the last chunk, else null.
    /// </summary>
    private async Task<JsonNode?> SendChunkAsync(string session, byte[] buffer, long offset, int count, long length, string path, CancellationToken cancellationToken)
    {
        var sent = 0;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var from = offset + sent;
                var size = count - sent;
                using var response = await _api.SendAsync(() => new HttpRequestMessage(HttpMethod.Put, session)
                {
                    Content = new ByteArrayContent(buffer, sent, size)
                    {
                        Headers = { ContentRange = new ContentRangeHeaderValue(from, from + size - 1, length) },
                    },
                }, cancellationToken, replayable: false, path: path);
                if ((int)response.StatusCode != 308)
                {
                    return await CloudApi.ReadJsonAsync(response, cancellationToken);
                }

                var received = ReceivedUpTo(response);
                if (received >= offset + count)
                {
                    return null;
                }

                sent = (int)Math.Max(0, received - offset);
            }
            catch (StorageException exception) when (exception.IsRetryable && attempt < 3)
            {
                await Task.Delay(exception.RetryAfter is { } wait && wait <= CloudApi.MaxInternalWait ? wait : CloudApi.FirstBackoff, cancellationToken);
                var status = await QueryUploadAsync(session, length, path, cancellationToken);
                if (status.Finished is not null)
                {
                    return status.Finished;
                }

                if (status.Received < offset)
                {
                    throw new StorageException(StorageErrorKind.Transient, $"Google Drive: Der Upload von „{path}“ muss neu beginnen.");
                }

                if (status.Received >= offset + count)
                {
                    return null;
                }

                sent = (int)(status.Received - offset);
            }
        }
    }

    private async Task<(long Received, JsonNode? Finished)> QueryUploadAsync(string session, long length, string path, CancellationToken cancellationToken)
    {
        using var response = await _api.SendAsync(() => new HttpRequestMessage(HttpMethod.Put, session)
        {
            Content = new ByteArrayContent([]) { Headers = { ContentRange = new ContentRangeHeaderValue(length) } },
        }, cancellationToken, path: path);
        return (int)response.StatusCode == 308 ? (ReceivedUpTo(response), null) : (length, await CloudApi.ReadJsonAsync(response, cancellationToken));
    }

    /// <summary>The bytes Drive has (from "Range: bytes=0-N" of a 308 answer; none: 0).</summary>
    private static long ReceivedUpTo(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Range", out var values))
        {
            return 0;
        }

        var range = values.First();
        var dash = range.LastIndexOf('-');
        return dash >= 0 && long.TryParse(range[(dash + 1)..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var last)
            ? last + 1
            : 0;
    }
}
