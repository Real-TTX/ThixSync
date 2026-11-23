using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ThixSync.Core.Models;
using ThixSync.Core.Storage.Providers.OAuth;

namespace ThixSync.Core.Storage.Providers.OneDrive;

/// <summary>
/// A folder of OneDrive (personal or for Business) or of a SharePoint document library, through Microsoft Graph. Items are addressed by
/// path below the root item (<c>/drives/{drive}/items/{root}:/a/b.txt</c>), so no id cache is needed except for the folders an upload
/// creates (once, however many workers need them). Listing: one delta pass over the root folder (OneDrive personal, or the top of a
/// business drive – Graph has delta below the top only for personal drives), else a parallel walk. Uploads: PUT up to 4 MiB, an upload
/// session in 10 MiB chunks above (a multiple of 320 KiB), the modification time as fileSystemInfo. Downloads follow the redirect to the
/// CDN without the token. Deleting moves to the recycle bin. Hash: quickXorHash ("quickxor:" + Base64, as Graph gives it).
/// </summary>
internal sealed class GraphStorage : IStorage, IChangeFeed
{
    private const string Graph = "https://graph.microsoft.com/v1.0";
    private const string ItemFields = "id,name,size,file,folder,package,root,deleted,parentReference,fileSystemInfo,lastModifiedDateTime";

    /// <summary>What OneDrive and SharePoint do not allow in a name (also: a trailing dot or space, a leading space).</summary>
    internal const string InvalidCharacters = "\"*:<>?/\\|";

    private readonly ConnectionKind _kind;
    private readonly StorageContext _context;
    private readonly CloudApi _api;
    private readonly string _rootPath;
    private readonly PathIds _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private string? _driveId;
    private string? _driveType;
    private string? _rootId;
    private bool _rootIsDriveTop;

    public GraphStorage(StorageContext context, OAuthApp app)
    {
        _kind = context.Connection.Kind;
        _context = context;
        _rootPath = OAuthApp.RootOf(context);
        ServiceName = _kind == ConnectionKind.SharePoint ? "SharePoint" : "OneDrive";
        _api = new CloudApi(ServiceName, OAuthTokenSource.For(context, app), Capabilities.MaxParallelism, Describe);
        var account = context.Connection.Get(ConnectionFields.SignedInAs);
        var where = _kind == ConnectionKind.SharePoint
            ? $"SharePoint – {context.Connection.Get(ConnectionFields.SiteUrl)}"
            : account.Length > 0 ? $"OneDrive ({account})" : "OneDrive";
        DisplayName = _rootPath.Length > 0 ? $"{where}/{_rootPath}" : where;
    }

    /// <summary>Files up to this size go in one PUT (tests make it small).</summary>
    internal static long SimpleUploadLimit { get; set; } = 4L * 1024 * 1024;

    /// <summary>The chunk of an upload session (a multiple of 320 KiB).</summary>
    internal static int ChunkSize { get; set; } = 32 * 320 * 1024;

    public ConnectionKind Kind => _kind;

    public string ServiceName { get; }

    public StorageCapabilities Capabilities { get; } = new()
    {
        Features = StorageFeatures.SetModifiedTime | StorageFeatures.Move | StorageFeatures.RealFolders | StorageFeatures.ChangeFeed | StorageFeatures.CoarseTime,
        MaxParallelism = 8,
        HashKind = "quickxor",
        MaxFileSize = 250L * 1024 * 1024 * 1024,
        InvalidNameCharacters = InvalidCharacters,
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

            var drive = _kind == ConnectionKind.SharePoint
                ? await FindLibraryAsync(cancellationToken)
                : await _api.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{Graph}/me/drive?$select=id,driveType"), cancellationToken);
            _driveId = CloudJson.Text(drive, "id");
            _driveType = CloudJson.Text(drive, "driveType");

            var top = await _api.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{Graph}/drives/{_driveId}/root?$select=id"), cancellationToken);
            var id = CloudJson.Text(top, "id");
            _rootIsDriveTop = _rootPath.Length == 0;
            var walked = "";
            foreach (var part in _rootPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                walked = StoragePath.Combine(walked, part);
                var child = await GetItemAsync($"{Graph}/drives/{_driveId}/items/{id}:/{Uri.EscapeDataString(part)}", cancellationToken);
                if (child is not null)
                {
                    id = CloudJson.Text(child, "id");
                    continue;
                }

                if (!createRoot)
                {
                    throw new StorageException(StorageErrorKind.NotFound, $"Den Ordner „{walked}“ gibt es bei {ServiceName} nicht.");
                }

                id = await CreateFolderInAsync(id, part, walked, cancellationToken);
            }

            _rootId = id;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async IAsyncEnumerable<StorageEntry> ListAsync(string folder, Action<string, Exception>? onProblem, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await ConnectAsync(false, cancellationToken);
        var start = StoragePath.Normalize(folder);
        var startItem = start.Length == 0 ? null : await GetItemAsync(ItemUrl(start), cancellationToken);
        if (start.Length > 0 && startItem?["folder"] is null)
        {
            throw new StorageException(StorageErrorKind.NotFound, $"{ServiceName}: Den Ordner „{start}“ gibt es nicht.");
        }

        var startId = startItem is null ? _rootId! : CloudJson.Text(startItem, "id");
        if (_driveType == "personal" || (_rootIsDriveTop && start.Length == 0))
        {
            foreach (var entry in await ListByDeltaAsync(start, startId, onProblem, cancellationToken))
            {
                yield return entry;
            }

            yield break;
        }

        await foreach (var entry in CloudWalk.WalkAsync(start, startId, ListChildrenAsync, Capabilities.MaxParallelism, onProblem, cancellationToken))
        {
            yield return entry;
        }
    }

    public async Task<StorageEntry?> GetAsync(string path, CancellationToken cancellationToken)
    {
        await ConnectAsync(false, cancellationToken);
        var normalized = StoragePath.Normalize(path);
        var item = await GetItemAsync(ItemUrl(normalized), cancellationToken);
        return item is null ? null : ToEntry(normalized, item);
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        await ConnectAsync(false, cancellationToken);
        var normalized = StoragePath.Normalize(path);
        var url = ItemUrl(normalized, "/content");
        var response = await _api.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken, path: normalized);
        return await CloudStreams.OpenAsync(response, cancellationToken);
    }

    public async Task<StorageEntry> WriteAsync(string path, Stream content, long length, DateTimeOffset modified, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        await ConnectAsync(false, cancellationToken);
        var normalized = StoragePath.Normalize(path);
        CheckName(normalized);
        var parentId = await EnsureFolderAsync(StoragePath.ParentOf(normalized), cancellationToken);
        var target = $"{Graph}/drives/{_driveId}/items/{parentId}:/{Uri.EscapeDataString(StoragePath.NameOf(normalized))}:";
        var fileSystemInfo = new JsonObject { ["lastModifiedDateTime"] = CloudJson.Rfc3339Seconds(modified) };

        JsonNode item;
        if (length <= SimpleUploadLimit)
        {
            var bytes = await CloudStreams.ReadSmallAsync(content, length, normalized, cancellationToken);
            var uploaded = await _api.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Put, $"{target}/content?$select=id")
            {
                Content = new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } },
            }, cancellationToken, path: normalized);
            progress?.Report(length);
            var body = new JsonObject { ["fileSystemInfo"] = fileSystemInfo };
            var itemUrl = $"{Graph}/drives/{_driveId}/items/{CloudJson.Text(uploaded, "id")}?$select={ItemFields}";
            item = await _api.SendJsonAsync(() => CloudApi.Json(HttpMethod.Patch, itemUrl, body), cancellationToken, path: normalized);
        }
        else
        {
            item = await UploadInSessionAsync(normalized, target, fileSystemInfo, content, length, progress, cancellationToken);
        }

        return ToEntry(normalized, item);
    }

    public async Task CreateFolderAsync(string path, CancellationToken cancellationToken)
    {
        await ConnectAsync(false, cancellationToken);
        var normalized = StoragePath.Normalize(path);
        CheckName(normalized);
        await EnsureFolderAsync(normalized, cancellationToken);
    }

    public async Task DeleteAsync(string path, bool isFolder, CancellationToken cancellationToken)
    {
        await ConnectAsync(false, cancellationToken);
        var normalized = StoragePath.Normalize(path);
        if (normalized.Length == 0)
        {
            throw new StorageException(StorageErrorKind.AccessDenied, "Der Wurzelordner eines Auftrags wird nie gelöscht.");
        }

        try
        {
            using var _ = await _api.SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, ItemUrl(normalized)), cancellationToken, path: normalized);
        }
        catch (StorageException exception) when (exception.Kind == StorageErrorKind.NotFound)
        {
        }

        _folders.Remove(normalized);
    }

    public async Task MoveAsync(string fromPath, string toPath, CancellationToken cancellationToken)
    {
        await ConnectAsync(false, cancellationToken);
        var from = StoragePath.Normalize(fromPath);
        var to = StoragePath.Normalize(toPath);
        CheckName(to);
        var toParentId = await EnsureFolderAsync(StoragePath.ParentOf(to), cancellationToken);
        var existing = await GetItemAsync(ItemUrl(to), cancellationToken);
        var source = await GetItemAsync(ItemUrl(from), cancellationToken)
            ?? throw new StorageException(StorageErrorKind.NotFound, $"{ServiceName}: „{from}“ gibt es nicht.");
        if (existing is not null && CloudJson.Text(existing, "id") != CloudJson.Text(source, "id"))
        {
            if (existing["folder"] is not null)
            {
                throw new StorageException(StorageErrorKind.Unknown, $"{ServiceName}: Am Ziel „{to}“ gibt es schon einen Ordner.");
            }

            await DeleteAsync(to, isFolder: false, cancellationToken);
        }

        var body = new JsonObject { ["name"] = StoragePath.NameOf(to), ["parentReference"] = new JsonObject { ["id"] = toParentId } };
        var url = $"{Graph}/drives/{_driveId}/items/{CloudJson.Text(source, "id")}?$select=id";
        await _api.SendJsonAsync(() => CloudApi.Json(HttpMethod.Patch, url, body), cancellationToken, path: from);
        _folders.Remove(from);
        _folders.Remove(to);
    }

    public async Task<string> GetCursorAsync(CancellationToken cancellationToken)
    {
        await ConnectAsync(false, cancellationToken);
        var url = $"{DeltaBase()}?token=latest";
        var answer = await _api.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
        return CloudJson.Text(answer, "@odata.deltaLink");
    }

    public async Task<bool> HasChangesAsync(string cursor, CancellationToken cancellationToken)
    {
        await ConnectAsync(false, cancellationToken);
        if (!cursor.StartsWith(Graph + "/", StringComparison.OrdinalIgnoreCase))
        {
            return true; // never send the token anywhere else
        }

        var deltaOnRoot = DeltaBase().EndsWith($"/items/{_rootId}/delta", StringComparison.Ordinal);
        var ancestry = new CloudAncestry(_rootId!, ParentsOfAsync);
        var url = cursor;
        for (var page = 0; page < 20; page++)
        {
            JsonNode answer;
            try
            {
                var pageUrl = url;
                answer = await _api.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, pageUrl), cancellationToken);
            }
            catch (StorageException exception) when (exception.Kind is StorageErrorKind.NotFound or StorageErrorKind.Unknown)
            {
                return true; // 410 resyncRequired, an expired token
            }

            foreach (var item in CloudJson.Items(answer, "value"))
            {
                var id = CloudJson.Text(item, "id");
                if (deltaOnRoot)
                {
                    if (id != _rootId)
                    {
                        return true;
                    }

                    continue;
                }

                var parentId = CloudJson.Text(item["parentReference"], "id");
                if (parentId.Length == 0 ? item["root"] is null : await ancestry.IsBelowRootAsync(id, [parentId], cancellationToken))
                {
                    return true;
                }
            }

            if (CloudJson.Text(answer, "@odata.nextLink") is not { Length: > 0 } next || !next.StartsWith(Graph + "/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            url = next;
        }

        return true;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The folders directly below a folder (relative to the root of this storage), for the folder picker.</summary>
    public async Task<IReadOnlyList<string>> ListFoldersAsync(string folder, CancellationToken cancellationToken)
    {
        await ConnectAsync(false, cancellationToken);
        var normalized = StoragePath.Normalize(folder);
        var item = normalized.Length == 0 ? new JsonObject { ["id"] = _rootId } : await GetItemAsync(ItemUrl(normalized), cancellationToken);
        if (item is null)
        {
            throw new StorageException(StorageErrorKind.NotFound, $"{ServiceName}: Den Ordner „{normalized}“ gibt es nicht.");
        }

        var children = await ListChildrenAsync(normalized, CloudJson.Text(item, "id"), cancellationToken);
        return [.. children.Where(entry => entry.IsFolder).Select(entry => entry.Name).Order(StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>The signed-in account (e-mail, else the sign-in name) for a fresh access token.</summary>
    internal static async Task<string> ReadAccountAsync(CloudApi api, CancellationToken cancellationToken)
    {
        var me = await api.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{Graph}/me?$select=mail,userPrincipalName,displayName"), cancellationToken);
        var mail = CloudJson.Text(me, "mail");
        if (mail.Length > 0)
        {
            return mail;
        }

        var principal = CloudJson.Text(me, "userPrincipalName");
        return principal.Length > 0 ? principal : CloudJson.Text(me, "displayName");
    }

    /// <summary>What Graph's error answers mean ({"error": {"code": …, "message": …}}).</summary>
    internal static CloudError Describe(HttpStatusCode status, JsonNode? body)
    {
        var error = body?["error"];
        var code = CloudJson.Text(error, "code");
        var inner = CloudJson.Text(error?["innererror"], "code");
        StorageErrorKind? kind = code switch
        {
            "itemNotFound" => StorageErrorKind.NotFound,
            "accessDenied" => StorageErrorKind.AccessDenied,
            "quotaLimitReached" or "insufficientStorage" => StorageErrorKind.QuotaExceeded,
            "activityLimitReached" or "tooManyRequests" => StorageErrorKind.RateLimited,
            "resourceLocked" => StorageErrorKind.Locked,
            "InvalidAuthenticationToken" or "unauthenticated" => StorageErrorKind.Authentication,
            "serviceNotAvailable" or "generalException" => StorageErrorKind.Transient,
            _ => null,
        };
        if (kind is null && inner is "resourceLocked" or "lockMismatch")
        {
            kind = StorageErrorKind.Locked;
        }

        return new CloudError(kind, code.Length > 0 ? code : CloudJson.Text(error, "message"));
    }

    /// <summary>Whether a path is allowed on OneDrive / SharePoint (InvalidName otherwise: the file is skipped, not the run).</summary>
    internal static void CheckName(string path)
    {
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.IndexOfAny(InvalidCharacters.ToCharArray()) >= 0 || part.EndsWith('.') || part.EndsWith(' ') || part.StartsWith(' '))
            {
                throw new StorageException(StorageErrorKind.InvalidName,
                    $"„{part}“ ist bei OneDrive/SharePoint nicht erlaubt (keine \" * : < > ? / \\ |, kein Punkt oder Leerzeichen am Ende).");
            }
        }
    }

    private static StorageEntry ToEntry(string path, JsonNode item)
    {
        var parsed = ToItem(item);
        return new StorageEntry(path, parsed.IsFolder, parsed.IsFolder ? 0 : parsed.Size, parsed.Modified)
        {
            Hash = parsed.Hash,
            NativeId = parsed.Id,
            IsHidden = parsed.Name.StartsWith('.'),
        };
    }

    private static CloudItem ToItem(JsonNode item)
    {
        var modified = CloudJson.Time(item["fileSystemInfo"], "lastModifiedDateTime");
        var quickXor = CloudJson.Text(item["file"]?["hashes"], "quickXorHash");
        var parentId = CloudJson.Text(item["parentReference"], "id");
        return new CloudItem(
            CloudJson.Text(item, "id"),
            CloudJson.Text(item, "name"),
            parentId.Length > 0 ? [parentId] : [],
            item["folder"] is not null,
            CloudJson.Number(item, "size"),
            modified != DateTimeOffset.MinValue ? modified : CloudJson.Time(item, "lastModifiedDateTime"))
        {
            Hash = quickXor.Length > 0 ? $"quickxor:{quickXor}" : null,
            IsSkipped = item["package"] is not null || (item["folder"] is null && item["file"] is null),
        };
    }

    /// <summary>The address of an item by its path below the root ("" = the root item).</summary>
    private string ItemUrl(string path, string suffix = "")
    {
        if (path.Length == 0)
        {
            return $"{Graph}/drives/{_driveId}/items/{_rootId}{suffix}";
        }

        var escaped = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        return suffix.Length == 0 ? $"{Graph}/drives/{_driveId}/items/{_rootId}:/{escaped}" : $"{Graph}/drives/{_driveId}/items/{_rootId}:/{escaped}:{suffix}";
    }

    /// <summary>Delta on the root item where Graph has it (personal drives, the top of any drive), else on the top of the drive.</summary>
    private string DeltaBase() => _driveType == "personal" || _rootIsDriveTop
        ? $"{Graph}/drives/{_driveId}/items/{_rootId}/delta"
        : $"{Graph}/drives/{_driveId}/root/delta";

    private async Task<JsonNode?> GetItemAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            return await _api.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{url}?$select={ItemFields}"), cancellationToken);
        }
        catch (StorageException exception) when (exception.Kind == StorageErrorKind.NotFound)
        {
            return null;
        }
    }

    private async Task<List<StorageEntry>> ListByDeltaAsync(string start, string startId, Action<string, Exception>? onProblem, CancellationToken cancellationToken)
    {
        var items = new List<CloudItem>();
        var url = $"{Graph}/drives/{_driveId}/items/{startId}/delta?$select={ItemFields}";
        while (true)
        {
            var pageUrl = url;
            var page = await _api.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, pageUrl), cancellationToken);
            items.AddRange(CloudJson.Items(page, "value").Where(item => item["deleted"] is null).Select(ToItem).Where(item => item.Id != startId));
            var next = CloudJson.Text(page, "@odata.nextLink");
            if (next.Length == 0 || !next.StartsWith(Graph + "/", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            url = next;
        }

        var entries = CloudTree.Build(items, startId, start, StringComparer.OrdinalIgnoreCase, ServiceName, onProblem);
        foreach (var entry in entries.Where(entry => entry.IsFolder))
        {
            _folders.Set(entry.Path, entry.NativeId!, isFolder: true);
        }

        return entries;
    }

    private async Task<IReadOnlyList<StorageEntry>> ListChildrenAsync(string folderPath, string folderId, CancellationToken cancellationToken)
    {
        var entries = new List<StorageEntry>();
        var url = $"{Graph}/drives/{_driveId}/items/{folderId}/children?$select={ItemFields}&$top=1000";
        while (true)
        {
            var pageUrl = url;
            var page = await _api.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, pageUrl), cancellationToken, path: folderPath);
            foreach (var item in CloudJson.Items(page, "value"))
            {
                var parsed = ToItem(item);
                if (parsed.IsSkipped)
                {
                    continue;
                }

                var path = StoragePath.Combine(folderPath, parsed.Name);
                entries.Add(ToEntry(path, item));
                if (parsed.IsFolder)
                {
                    _folders.Set(path, parsed.Id, isFolder: true);
                }
            }

            var next = CloudJson.Text(page, "@odata.nextLink");
            if (next.Length == 0 || !next.StartsWith(Graph + "/", StringComparison.OrdinalIgnoreCase))
            {
                return entries;
            }

            url = next;
        }
    }

    private async Task<IReadOnlyList<string>?> ParentsOfAsync(string id, CancellationToken cancellationToken)
    {
        var item = await GetItemAsync($"{Graph}/drives/{_driveId}/items/{id}", cancellationToken);
        if (item is null)
        {
            return null;
        }

        var parentId = CloudJson.Text(item["parentReference"], "id");
        return parentId.Length > 0 ? [parentId] : [];
    }

    /// <summary>The id of a folder, created with its missing parents (once, however many workers need it at the same time).</summary>
    private async Task<string> EnsureFolderAsync(string path, CancellationToken cancellationToken)
    {
        if (path.Length == 0)
        {
            return _rootId!;
        }

        if (_folders.TryGet(path, out var cached))
        {
            return cached.Id;
        }

        var parentId = await EnsureFolderAsync(StoragePath.ParentOf(path), cancellationToken);
        using (await _folders.LockAsync(path, cancellationToken))
        {
            if (_folders.TryGet(path, out cached))
            {
                return cached.Id;
            }

            var id = await CreateFolderInAsync(parentId, StoragePath.NameOf(path), path, cancellationToken);
            _folders.Set(path, id, isFolder: true);
            return id;
        }
    }

    /// <summary>Creates a folder in a folder; an existing one of that name is taken (Graph refuses a second one: 409).</summary>
    private async Task<string> CreateFolderInAsync(string parentId, string name, string path, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["name"] = name, ["folder"] = new JsonObject(), ["@microsoft.graph.conflictBehavior"] = "fail" };
        try
        {
            var created = await _api.SendJsonAsync(() => CloudApi.Json(HttpMethod.Post, $"{Graph}/drives/{_driveId}/items/{parentId}/children?$select=id", body),
                cancellationToken, path: path);
            return CloudJson.Text(created, "id");
        }
        catch (StorageException exception) when (exception.Message.Contains("nameAlreadyExists", StringComparison.Ordinal))
        {
            var existing = await GetItemAsync($"{Graph}/drives/{_driveId}/items/{parentId}:/{Uri.EscapeDataString(name)}", cancellationToken);
            if (existing?["folder"] is null)
            {
                throw new StorageException(StorageErrorKind.Unknown, $"{ServiceName}: „{path}“ ist eine Datei, kein Ordner.");
            }

            return CloudJson.Text(existing, "id");
        }
    }

    /// <summary>The SharePoint library: the site from its address, the drive by name ("Dokumente" and "Documents" are the same one).</summary>
    private async Task<JsonNode> FindLibraryAsync(CancellationToken cancellationToken)
    {
        var siteUrl = _context.Connection.Get(ConnectionFields.SiteUrl).Trim();
        if (!Uri.TryCreate(siteUrl, UriKind.Absolute, out var site) || site.Scheme != Uri.UriSchemeHttps)
        {
            throw new StorageException(StorageErrorKind.NotFound, "SharePoint: Die Adresse der Website ist ungültig (https://firma.sharepoint.com/sites/Team).");
        }

        var segments = site.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
        if (segments.Count >= 2 && segments[0] is "sites" or "teams")
        {
            segments = segments[..2]; // a pasted address of a library or a page inside the site
        }
        else
        {
            segments.Clear();
        }

        var siteAddress = segments.Count == 0
            ? $"{Graph}/sites/{site.Host}?$select=id"
            : $"{Graph}/sites/{site.Host}:/{string.Join('/', segments.Select(Uri.EscapeDataString))}?$select=id";
        var found = await _api.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, siteAddress), cancellationToken);
        var siteId = CloudJson.Text(found, "id");

        var library = ConnectionCatalog.Value(_context.Connection, ConnectionFields.Library).Trim();
        if (library.Length == 0)
        {
            return await _api.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{Graph}/sites/{siteId}/drive?$select=id,driveType"), cancellationToken);
        }

        var drives = await _api.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{Graph}/sites/{siteId}/drives?$select=id,name,driveType,webUrl"), cancellationToken);
        var names = LibraryNames(library);
        foreach (var drive in CloudJson.Items(drives, "value"))
        {
            var name = CloudJson.Text(drive, "name");
            var urlName = Uri.UnescapeDataString(CloudJson.Text(drive, "webUrl").TrimEnd('/').Split('/').Last());
            if (names.Contains(name) || names.Contains(urlName))
            {
                return drive;
            }
        }

        throw new StorageException(StorageErrorKind.NotFound, $"SharePoint: Die Bibliothek „{library}“ gibt es auf dieser Website nicht.");
    }

    /// <summary>The names a library may have: the standard library is "Dokumente"/"Documents" (in the address "Shared Documents").</summary>
    private static HashSet<string> LibraryNames(string library)
    {
        string[] standard = ["Dokumente", "Documents", "Freigegebene Dokumente", "Shared Documents"];
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { library };
        if (standard.Contains(library, StringComparer.OrdinalIgnoreCase))
        {
            names.UnionWith(standard);
        }

        return names;
    }

    private async Task<JsonNode> UploadInSessionAsync(string path, string target, JsonObject fileSystemInfo, Stream content, long length, IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["item"] = new JsonObject { ["@microsoft.graph.conflictBehavior"] = "replace", ["fileSystemInfo"] = fileSystemInfo } };
        var session = await _api.SendJsonAsync(() => CloudApi.Json(HttpMethod.Post, $"{target}/createUploadSession", body), cancellationToken, path: path);
        var uploadUrl = CloudJson.Text(session, "uploadUrl");
        if (!Uri.TryCreate(uploadUrl, UriKind.Absolute, out _))
        {
            throw new StorageException(StorageErrorKind.Transient, $"{ServiceName}: Der Upload von „{path}“ konnte nicht beginnen.");
        }

        try
        {
            var buffer = new byte[ChunkSize];
            var offset = 0L;
            while (true)
            {
                var count = (int)Math.Min(ChunkSize, length - offset);
                await CloudStreams.ReadExactlyAsync(content, buffer, count, path, cancellationToken);
                var finished = await SendChunkAsync(uploadUrl, buffer, offset, count, length, path, cancellationToken);
                progress?.Report(count);
                offset += count;
                if (finished is not null)
                {
                    var id = CloudJson.Text(finished, "id");
                    return await _api.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{Graph}/drives/{_driveId}/items/{id}?$select={ItemFields}"),
                        cancellationToken, path: path);
                }

                if (offset >= length)
                {
                    throw new StorageException(StorageErrorKind.Transient, $"{ServiceName}: Der Upload von „{path}“ wurde nicht abgeschlossen.");
                }
            }
        }
        catch
        {
            await CancelSessionAsync(uploadUrl);
            throw;
        }
    }

    /// <summary>
    /// Sends one chunk to the session (no token: the address is signed); after a failed attempt asks how far it got and sends the rest of
    /// the chunk (up to 3 attempts). Returns the item when it was the last chunk, else null.
    /// </summary>
    private async Task<JsonNode?> SendChunkAsync(string uploadUrl, byte[] buffer, long offset, int count, long length, string path, CancellationToken cancellationToken)
    {
        var sent = 0;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var from = offset + sent;
                var size = count - sent;
                using var response = await _api.SendAsync(() => new HttpRequestMessage(HttpMethod.Put, uploadUrl)
                {
                    Content = new ByteArrayContent(buffer, sent, size) { Headers = { ContentRange = new ContentRangeHeaderValue(from, from + size - 1, length) } },
                }, cancellationToken, replayable: false, authorize: false, path: path);
                var answer = await CloudApi.ReadJsonAsync(response, cancellationToken);
                return response.StatusCode == HttpStatusCode.Accepted ? null : answer;
            }
            catch (StorageException exception) when (exception.IsRetryable && attempt < 3)
            {
                await Task.Delay(exception.RetryAfter is { } wait && wait <= CloudApi.MaxInternalWait ? wait : CloudApi.FirstBackoff, cancellationToken);
                var status = await _api.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, uploadUrl), cancellationToken, path: path, authorize: false);
                var expected = CloudJson.Items(status, "nextExpectedRanges").Select(range => range.GetValue<string>()).FirstOrDefault();
                if (expected is null || !long.TryParse(expected.Split('-')[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var next)
                    || next < offset)
                {
                    throw new StorageException(StorageErrorKind.Transient, $"{ServiceName}: Der Upload von „{path}“ muss neu beginnen.");
                }

                if (next >= offset + count)
                {
                    return null;
                }

                sent = (int)(next - offset);
            }
        }
    }

    private static async Task CancelSessionAsync(string uploadUrl)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, uploadUrl);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var _ = await CloudHttp.Client.SendAsync(request, timeout.Token);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            // the session expires by itself
        }
    }
}
