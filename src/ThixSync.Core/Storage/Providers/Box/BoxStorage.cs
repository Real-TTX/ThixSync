using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ThixSync.Core.Models;
using ThixSync.Core.Storage.Providers.OAuth;

namespace ThixSync.Core.Storage.Providers.Box;

/// <summary>
/// A folder of Box (REST API 2.0). Box works with ids: a path → id cache (case-insensitive, like Box's names) is filled by listings, a
/// missing part is looked up by listing its folder. Listing: a parallel walk of the folders (limit 1000, marker paging). Uploads: one
/// multipart request below 50 MiB, a chunked upload session above (the part size Box gives, a SHA-1 digest per part and for the whole
/// file); content_modified_at carries the modification time; a changed file gets a new version of the same file. Downloads follow the
/// redirect to Box's download server without the token. Deleting moves to Box's trash. Hash: sha1.
/// </summary>
internal sealed class BoxStorage : IStorage, IChangeFeed
{
    private const string Api = "https://api.box.com/2.0";
    private const string UploadApi = "https://upload.box.com/api/2.0";
    private const string Fields = "id,type,name,size,modified_at,content_modified_at,sha1";
    private const string TopFolderId = "0";

    private readonly CloudApi _api;
    private readonly string _rootPath;
    private readonly PathIds _ids = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private string? _rootId;

    public BoxStorage(StorageContext context, OAuthApp app)
    {
        _rootPath = OAuthApp.RootOf(context);
        _api = new CloudApi("Box", OAuthTokenSource.For(context, app), Capabilities.MaxParallelism, Describe);
        var account = context.Connection.Get(ConnectionFields.SignedInAs);
        var where = account.Length > 0 ? $"Box ({account})" : "Box";
        DisplayName = _rootPath.Length > 0 ? $"{where}/{_rootPath}" : where;
    }

    /// <summary>Files from this size on go through an upload session (Box: at least 20 MB; tests make it small).</summary>
    internal static long SessionUploadLimit { get; set; } = 50L * 1024 * 1024;

    /// <summary>Files up to this size are read into memory first, so a failed attempt can be sent again.</summary>
    internal static long BufferedUploadLimit { get; set; } = 8L * 1024 * 1024;

    /// <summary>How long a commit that Box is still processing (202) is waited for at most, per attempt (tests shorten it).</summary>
    internal static TimeSpan CommitWait { get; set; } = TimeSpan.FromSeconds(5);

    public ConnectionKind Kind => ConnectionKind.Box;

    public StorageCapabilities Capabilities { get; } = new()
    {
        Features = StorageFeatures.SetModifiedTime | StorageFeatures.Move | StorageFeatures.RealFolders | StorageFeatures.ChangeFeed | StorageFeatures.CoarseTime,
        MaxParallelism = 8,
        HashKind = "sha1",
        InvalidNameCharacters = "/\\",
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

            var id = TopFolderId;
            var walked = "";
            foreach (var part in _rootPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                walked = StoragePath.Combine(walked, part);
                var found = (await ListItemsAsync(id, walked, cancellationToken))
                    .FirstOrDefault(item => CloudJson.Text(item, "type") == "folder" && string.Equals(CloudJson.Text(item, "name"), part, StringComparison.OrdinalIgnoreCase));
                if (found is not null)
                {
                    id = CloudJson.Text(found, "id");
                    continue;
                }

                if (!createRoot)
                {
                    throw new StorageException(StorageErrorKind.NotFound, $"Den Ordner „{walked}“ gibt es bei Box nicht.");
                }

                id = await CreateFolderInAsync(id, part, walked, cancellationToken)
                     ?? throw new StorageException(StorageErrorKind.Transient, $"Box: Der Ordner „{walked}“ konnte nicht angelegt werden.");
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
            throw new StorageException(StorageErrorKind.NotFound, $"Box: Den Ordner „{start}“ gibt es nicht.");
        }

        await foreach (var entry in CloudWalk.WalkAsync(start, startId.Value.Id, ListChildrenAsync, Capabilities.MaxParallelism, onProblem, cancellationToken))
        {
            yield return entry;
        }
    }

    public async Task<StorageEntry?> GetAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        for (var attempt = 0; ; attempt++)
        {
            var id = await ResolveAsync(normalized, cancellationToken);
            if (id is null)
            {
                return null;
            }

            try
            {
                var url = $"{Api}/{(id.Value.IsFolder ? "folders" : "files")}/{id.Value.Id}?fields={Fields}";
                var item = await _api.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken, path: normalized);
                return ToEntry(normalized, item);
            }
            catch (StorageException exception) when (exception.Kind == StorageErrorKind.NotFound && attempt == 0 && normalized.Length > 0)
            {
                _ids.Invalidate(normalized);
            }
        }
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        for (var attempt = 0; ; attempt++)
        {
            var id = await ResolveAsync(normalized, cancellationToken);
            if (id is not { IsFolder: false })
            {
                throw new StorageException(StorageErrorKind.NotFound, $"Box: Die Datei „{normalized}“ gibt es nicht.");
            }

            try
            {
                var url = $"{Api}/files/{id.Value.Id}/content";
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
        CheckName(normalized);
        var parentId = await EnsureFolderAsync(StoragePath.ParentOf(normalized), cancellationToken);
        var existing = await ResolveAsync(normalized, cancellationToken);
        if (existing is { IsFolder: true })
        {
            throw new StorageException(StorageErrorKind.Unknown, $"Box: „{normalized}“ ist ein Ordner.");
        }

        var file = length >= SessionUploadLimit
            ? await UploadInSessionAsync(normalized, parentId, existing?.Id, content, length, modified, progress, cancellationToken)
            : await UploadAsync(normalized, parentId, existing?.Id, content, length, modified, progress, cancellationToken);
        _ids.Set(normalized, CloudJson.Text(file, "id"), isFolder: false);
        return ToEntry(normalized, file);
    }

    public async Task CreateFolderAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        CheckName(normalized);
        await EnsureFolderAsync(normalized, cancellationToken);
    }

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
            var url = id.Value.IsFolder ? $"{Api}/folders/{id.Value.Id}?recursive=true" : $"{Api}/files/{id.Value.Id}";
            using var _ = await _api.SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, url), cancellationToken, path: normalized);
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
        CheckName(to);
        var source = await ResolveAsync(from, cancellationToken)
            ?? throw new StorageException(StorageErrorKind.NotFound, $"Box: „{from}“ gibt es nicht.");
        var toParentId = await EnsureFolderAsync(StoragePath.ParentOf(to), cancellationToken);
        var target = await ResolveAsync(to, cancellationToken);
        if (target is { } existing && existing.Id != source.Id)
        {
            if (existing.IsFolder)
            {
                throw new StorageException(StorageErrorKind.Unknown, $"Box: Am Ziel „{to}“ gibt es schon einen Ordner.");
            }

            await DeleteAsync(to, isFolder: false, cancellationToken);
        }

        var body = new JsonObject { ["name"] = StoragePath.NameOf(to), ["parent"] = new JsonObject { ["id"] = toParentId } };
        var url = $"{Api}/{(source.IsFolder ? "folders" : "files")}/{source.Id}?fields=id";
        await _api.SendJsonAsync(() => CloudApi.Json(HttpMethod.Put, url, body), cancellationToken, path: from);
        _ids.Remove(from);
        _ids.Remove(to);
        _ids.Set(to, source.Id, source.IsFolder);
    }

    public async Task<string> GetCursorAsync(CancellationToken cancellationToken)
    {
        var answer = await _api.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{Api}/events?stream_position=now"), cancellationToken);
        return StreamPosition(answer);
    }

    public async Task<bool> HasChangesAsync(string cursor, CancellationToken cancellationToken)
    {
        await ConnectAsync(false, cancellationToken);
        var position = cursor;
        for (var page = 0; page < 10; page++)
        {
            const int Limit = 500;
            var url = $"{Api}/events?stream_type=changes&limit={Limit}&stream_position={Uri.EscapeDataString(position)}";
            JsonNode answer;
            try
            {
                answer = await _api.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
            }
            catch (StorageException exception) when (exception.Kind is StorageErrorKind.Unknown or StorageErrorKind.NotFound)
            {
                return true; // the position is no longer valid
            }

            var events = CloudJson.Items(answer, "entries").ToList();
            foreach (var change in events)
            {
                var source = change["source"];
                if (source is null)
                {
                    continue;
                }

                var inPath = CloudJson.Items(source["path_collection"], "entries").Any(folder => CloudJson.Text(folder, "id") == _rootId);
                if (_rootId == TopFolderId || CloudJson.Text(source, "id") == _rootId || inPath)
                {
                    return true;
                }
            }

            if (events.Count < Limit)
            {
                return false;
            }

            position = StreamPosition(answer);
        }

        return true;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The folders directly below a folder (relative to the root of this storage), for the folder picker.</summary>
    public async Task<IReadOnlyList<string>> ListFoldersAsync(string folder, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(folder);
        var id = await ResolveAsync(normalized, cancellationToken);
        if (id is not { IsFolder: true })
        {
            throw new StorageException(StorageErrorKind.NotFound, $"Box: Den Ordner „{normalized}“ gibt es nicht.");
        }

        var children = await ListChildrenAsync(normalized, id.Value.Id, cancellationToken);
        return [.. children.Where(entry => entry.IsFolder).Select(entry => entry.Name).Order(StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>The signed-in account (login e-mail, else the name) for a fresh access token.</summary>
    internal static async Task<string> ReadAccountAsync(CloudApi api, CancellationToken cancellationToken)
    {
        var me = await api.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{Api}/users/me?fields=login,name"), cancellationToken);
        var login = CloudJson.Text(me, "login");
        return login.Length > 0 ? login : CloudJson.Text(me, "name");
    }

    /// <summary>What Box's error answers mean ({"type": "error", "code": …, "message": …}).</summary>
    internal static CloudError Describe(HttpStatusCode status, JsonNode? body)
    {
        var code = CloudJson.Text(body, "code");
        StorageErrorKind? kind = code switch
        {
            "storage_limit_exceeded" or "user_storage_limit_exceeded" => StorageErrorKind.QuotaExceeded,
            "not_found" or "trashed" => StorageErrorKind.NotFound,
            "access_denied_item_locked" => StorageErrorKind.Locked,
            "access_denied_insufficient_permissions" or "forbidden" or "access_denied" => StorageErrorKind.AccessDenied,
            "rate_limit_exceeded" => StorageErrorKind.RateLimited,
            "item_name_invalid" or "item_name_too_long" => StorageErrorKind.InvalidName,
            "unauthorized" => StorageErrorKind.Authentication,
            "operation_blocked_temporary" or "internal_server_error" or "unavailable" => StorageErrorKind.Transient,
            _ => null,
        };
        return new CloudError(kind, code.Length > 0 ? code : CloudJson.Text(body, "message"));
    }

    /// <summary>Whether a path is allowed on Box (no / or \, no leading or trailing space, not "." or "..").</summary>
    private static void CheckName(string path)
    {
        if (path.Length == 0)
        {
            throw new StorageException(StorageErrorKind.InvalidName, "Box: Eine Datei braucht einen Namen.");
        }

        foreach (var part in path.Split('/'))
        {
            if (part.Contains('\\') || part.StartsWith(' ') || part.EndsWith(' ') || part.Length > 255)
            {
                throw new StorageException(StorageErrorKind.InvalidName, $"„{part}“ ist bei Box nicht erlaubt (kein \\, kein Leerzeichen am Anfang oder Ende, höchstens 255 Zeichen).");
            }
        }
    }

    private static string StreamPosition(JsonNode answer)
    {
        var position = answer["next_stream_position"];
        if (position is JsonValue value && value.TryGetValue<long>(out var number))
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }

        return CloudJson.Text(answer, "next_stream_position");
    }

    private static StorageEntry ToEntry(string path, JsonNode item)
    {
        var isFolder = CloudJson.Text(item, "type") == "folder";
        var name = CloudJson.Text(item, "name");
        var contentModified = CloudJson.Time(item, "content_modified_at");
        var sha1 = CloudJson.Text(item, "sha1");
        return new StorageEntry(path, isFolder, isFolder ? 0 : CloudJson.Number(item, "size"),
            contentModified != DateTimeOffset.MinValue ? contentModified : CloudJson.Time(item, "modified_at"))
        {
            Hash = !isFolder && sha1.Length > 0 ? $"sha1:{sha1}" : null,
            NativeId = CloudJson.Text(item, "id"),
            IsHidden = name.StartsWith('.'),
        };
    }

    private static string DigestOf(byte[] sha1) => $"sha={Convert.ToBase64String(sha1)}";

    /// <summary>Every item of a folder (marker paging, 1000 per page; web links are left out).</summary>
    private async Task<List<JsonNode>> ListItemsAsync(string folderId, string path, CancellationToken cancellationToken)
    {
        var items = new List<JsonNode>();
        string? marker = null;
        do
        {
            var url = $"{Api}/folders/{folderId}/items?fields={Fields}&limit=1000&usemarker=true" + (marker is null ? "" : $"&marker={Uri.EscapeDataString(marker)}");
            var page = await _api.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken, path: path);
            items.AddRange(CloudJson.Items(page, "entries").Where(item => CloudJson.Text(item, "type") is "file" or "folder"));
            marker = CloudJson.Text(page, "next_marker") is { Length: > 0 } next ? next : null;
        }
        while (marker is not null);

        return items;
    }

    /// <summary>The entries of a folder; fills the cache and marks the folder complete.</summary>
    private async Task<IReadOnlyList<StorageEntry>> ListChildrenAsync(string folderPath, string folderId, CancellationToken cancellationToken)
    {
        var items = await ListItemsAsync(folderId, folderPath, cancellationToken);
        var entries = new List<StorageEntry>(items.Count);
        foreach (var item in items)
        {
            var entry = ToEntry(StoragePath.Combine(folderPath, CloudJson.Text(item, "name")), item);
            entries.Add(entry);
            _ids.Set(entry.Path, entry.NativeId!, entry.IsFolder);
        }

        _ids.MarkComplete(folderPath);
        return entries;
    }

    /// <summary>The id of a path: from the cache, else by listing its folder (null: not there).</summary>
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

        await ListChildrenAsync(parentPath, parent.Value.Id, cancellationToken);
        return _ids.TryGet(path, out cached) ? cached : null;
    }

    /// <summary>The id of a folder, created with its missing parents (once, however many workers need it at the same time).</summary>
    private async Task<string> EnsureFolderAsync(string path, CancellationToken cancellationToken)
    {
        var known = await ResolveAsync(path, cancellationToken);
        if (known is { IsFolder: true })
        {
            return known.Value.Id;
        }

        if (known is not null)
        {
            throw new StorageException(StorageErrorKind.Unknown, $"Box: „{path}“ ist eine Datei, kein Ordner.");
        }

        var parentPath = StoragePath.ParentOf(path);
        var parentId = await EnsureFolderAsync(parentPath, cancellationToken);
        using (await _ids.LockAsync(path, cancellationToken))
        {
            if (_ids.TryGet(path, out var cached) && cached.IsFolder)
            {
                return cached.Id;
            }

            var id = await CreateFolderInAsync(parentId, StoragePath.NameOf(path), path, cancellationToken);
            if (id is null)
            {
                await ListChildrenAsync(parentPath, parentId, cancellationToken); // made by someone else meanwhile
                if (!_ids.TryGet(path, out cached) || !cached.IsFolder)
                {
                    throw new StorageException(StorageErrorKind.Unknown, $"Box: „{path}“ ist eine Datei, kein Ordner.");
                }

                return cached.Id;
            }

            _ids.Set(path, id, isFolder: true);
            _ids.MarkComplete(path);
            return id;
        }
    }

    /// <summary>Creates a folder; null when the name is taken (Box refuses a second one: 409 item_name_in_use).</summary>
    private async Task<string?> CreateFolderInAsync(string parentId, string name, string path, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["name"] = name, ["parent"] = new JsonObject { ["id"] = parentId } };
        try
        {
            var created = await _api.SendJsonAsync(() => CloudApi.Json(HttpMethod.Post, $"{Api}/folders?fields=id", body), cancellationToken, path: path);
            return CloudJson.Text(created, "id");
        }
        catch (StorageException exception) when (exception.Message.Contains("item_name_in_use", StringComparison.Ordinal))
        {
            return null;
        }
    }

    private async Task<JsonNode> UploadAsync(string path, string parentId, string? existingId, Stream content, long length, DateTimeOffset modified,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var bytes = length <= BufferedUploadLimit ? await CloudStreams.ReadSmallAsync(content, length, path, cancellationToken) : null;
        HttpRequestMessage Build(string? fileId)
        {
            var attributes = new JsonObject { ["name"] = StoragePath.NameOf(path), ["content_modified_at"] = CloudJson.Rfc3339Seconds(modified) };
            if (fileId is null)
            {
                attributes["parent"] = new JsonObject { ["id"] = parentId };
            }

            HttpContent file = bytes is not null ? new ByteArrayContent(bytes) : new ProgressContent(content, length, progress, path);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var form = new MultipartFormDataContent
            {
                { new StringContent(attributes.ToJsonString(), Encoding.UTF8), "attributes" },
                { file, "file", "file" },
            };
            var url = fileId is null ? $"{UploadApi}/files/content?fields={Fields}" : $"{UploadApi}/files/{fileId}/content?fields={Fields}";
            return new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        }

        JsonNode answer;
        try
        {
            answer = await _api.SendJsonAsync(() => Build(existingId), cancellationToken, replayable: bytes is not null, path: path);
        }
        catch (StorageException exception) when (exception.Kind == StorageErrorKind.NotFound || exception.Message.Contains("item_name_in_use", StringComparison.Ordinal))
        {
            _ids.Invalidate(path); // the cache was out of date (deleted or made by someone else meanwhile)
            if (bytes is null)
            {
                throw new StorageException(StorageErrorKind.Transient, $"Box: „{path}“ hat sich während des Uploads geändert.", exception);
            }

            var fresh = await ResolveAsync(path, cancellationToken);
            answer = await _api.SendJsonAsync(() => Build(fresh?.Id), cancellationToken, path: path);
        }

        if (bytes is not null)
        {
            progress?.Report(length);
        }

        return CloudJson.Items(answer, "entries").FirstOrDefault()
               ?? throw new StorageException(StorageErrorKind.Transient, $"Box: Der Upload von „{path}“ gab keine Antwort.");
    }

    private async Task<JsonNode> UploadInSessionAsync(string path, string parentId, string? existingId, Stream content, long length, DateTimeOffset modified,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        JsonNode session;
        try
        {
            session = await StartSessionAsync(path, parentId, existingId, length, cancellationToken);
        }
        catch (StorageException exception) when (exception.Kind == StorageErrorKind.NotFound || exception.Message.Contains("item_name_in_use", StringComparison.Ordinal))
        {
            _ids.Invalidate(path);
            var fresh = await ResolveAsync(path, cancellationToken);
            session = await StartSessionAsync(path, parentId, fresh?.Id, length, cancellationToken);
        }

        var sessionId = CloudJson.Text(session, "id");
        var partSize = (int)CloudJson.Number(session, "part_size");
        if (sessionId.Length == 0 || partSize <= 0)
        {
            throw new StorageException(StorageErrorKind.Transient, $"Box: Der Upload von „{path}“ konnte nicht beginnen.");
        }

        var sessionUrl = $"{UploadApi}/files/upload_sessions/{sessionId}";
        try
        {
            using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            var parts = new JsonArray();
            var buffer = new byte[partSize];
            var offset = 0L;
            while (offset < length)
            {
                var count = (int)Math.Min(partSize, length - offset);
                await CloudStreams.ReadExactlyAsync(content, buffer, count, path, cancellationToken);
                whole.AppendData(buffer, 0, count);
                var digest = DigestOf(SHA1.HashData(buffer.AsSpan(0, count)));
                var from = offset;
                var uploaded = await _api.SendJsonAsync(() =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Put, sessionUrl)
                    {
                        Content = new ByteArrayContent(buffer, 0, count)
                        {
                            Headers =
                            {
                                ContentType = new MediaTypeHeaderValue("application/octet-stream"),
                                ContentRange = new ContentRangeHeaderValue(from, from + count - 1, length),
                            },
                        },
                    };
                    request.Headers.TryAddWithoutValidation("Digest", digest);
                    return request;
                }, cancellationToken, path: path);
                parts.Add(uploaded["part"]?.DeepClone());
                progress?.Report(count);
                offset += count;
            }

            return await CommitAsync(sessionUrl, parts, DigestOf(whole.GetHashAndReset()), modified, path, cancellationToken);
        }
        catch
        {
            await AbortSessionAsync(sessionUrl);
            throw;
        }
    }

    private Task<JsonNode> StartSessionAsync(string path, string parentId, string? existingId, long length, CancellationToken cancellationToken)
    {
        var body = existingId is null
            ? new JsonObject { ["folder_id"] = parentId, ["file_size"] = length, ["file_name"] = StoragePath.NameOf(path) }
            : new JsonObject { ["file_size"] = length, ["file_name"] = StoragePath.NameOf(path) };
        var url = existingId is null ? $"{UploadApi}/files/upload_sessions" : $"{UploadApi}/files/{existingId}/upload_sessions";
        return _api.SendJsonAsync(() => CloudApi.Json(HttpMethod.Post, url, body), cancellationToken, path: path);
    }

    /// <summary>Commits the parts; Box may answer 202 (still assembling) with Retry-After: then it is asked again.</summary>
    private async Task<JsonNode> CommitAsync(string sessionUrl, JsonArray parts, string digest, DateTimeOffset modified, string path, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["parts"] = parts,
            ["attributes"] = new JsonObject { ["content_modified_at"] = CloudJson.Rfc3339Seconds(modified) },
        };
        for (var attempt = 0; attempt < 30; attempt++)
        {
            using var response = await _api.SendAsync(() =>
            {
                var request = CloudApi.Json(HttpMethod.Post, $"{sessionUrl}/commit?fields={Fields}", body);
                request.Headers.TryAddWithoutValidation("Digest", digest);
                return request;
            }, cancellationToken, path: path);
            if (response.StatusCode != HttpStatusCode.Accepted)
            {
                var answer = await CloudApi.ReadJsonAsync(response, cancellationToken);
                return CloudJson.Items(answer, "entries").FirstOrDefault()
                       ?? throw new StorageException(StorageErrorKind.Transient, $"Box: Der Upload von „{path}“ gab keine Antwort.");
            }

            var wait = CloudHttp.RetryAfter(response) ?? TimeSpan.FromSeconds(1);
            await Task.Delay(wait < CommitWait ? wait : CommitWait, cancellationToken);
        }

        throw new StorageException(StorageErrorKind.Transient, $"Box: Der Upload von „{path}“ wurde nicht rechtzeitig abgeschlossen.");
    }

    private async Task AbortSessionAsync(string sessionUrl)
    {
        // The session expires by itself after a day; the abort only frees the space sooner.
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var _ = await _api.SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, sessionUrl), timeout.Token, replayable: false);
        }
        catch (Exception exception) when (exception is StorageException or OperationCanceledException)
        {
        }
    }
}
