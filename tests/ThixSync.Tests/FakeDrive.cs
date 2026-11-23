using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using MatCloud.Contract;
using MatCloud.Contract.Drive;

namespace ThixSync.Tests;

/// <summary>
/// ThixDrive of MatCloud in memory, after the rules of <see cref="DriveContract"/>: folders and files with stable ids, names unique per
/// folder whatever the case, versions with ETags (PUT needs If-Match: 428 without, 412 with an old one), 409 for a taken name, uploads in
/// pieces, the trash, the quota. Only a device with <see cref="Token"/> gets in (401 otherwise). Enough to test the storage "MatCloud".
/// </summary>
internal sealed class FakeDrive : HttpMessageHandler
{
    public const string Token = "drive-token";

    private readonly Lock _gate = new();
    private readonly Dictionary<long, Node> _nodes = [];
    private readonly Dictionary<Guid, Upload> _uploads = [];
    private long _lastId;
    private long _lastVersion;

    /// <summary>Every request ("PUT /api/v1/drive/items/3/content If-Match:\"4\"").</summary>
    public List<string> Requests { get; } = [];

    /// <summary>The administrator did not grant the Drive to ThixSync: 403.</summary>
    public bool IsForbidden { get; set; }

    /// <summary>The quota of the account is full: 507 for new bytes.</summary>
    public bool IsFull { get; set; }

    /// <summary>The device was removed: 401.</summary>
    public bool IsSignedOut { get; set; }

    /// <summary>Puts a file at a path from the top of the Drive (folders on the way are made); returns its id.</summary>
    public long Put(string path, byte[] content)
    {
        lock (_gate)
        {
            var parts = path.Split('/');
            long? parent = null;
            foreach (var folder in parts[..^1])
            {
                parent = (ChildNamed(parent, folder) ?? AddNode(parent, folder, DriveItemKind.Folder, null)).Id;
            }

            if (ChildNamed(parent, parts[^1]) is { } existing)
            {
                NewVersion(existing, content);
                return existing.Id;
            }

            return AddNode(parent, parts[^1], DriveItemKind.File, content).Id;
        }
    }

    /// <summary>The live item at a path, or null.</summary>
    public DriveItem? Find(string path)
    {
        lock (_gate)
        {
            return FindNode(path) is { } node ? ToItem(node) : null;
        }
    }

    public byte[]? ContentOf(string path)
    {
        lock (_gate)
        {
            return FindNode(path)?.Content;
        }
    }

    public bool IsInTrash(long id)
    {
        lock (_gate)
        {
            return _nodes.TryGetValue(id, out var node) && node.IsTrashed;
        }
    }

    /// <summary>Another device renames an item.</summary>
    public void Rename(string path, string name)
    {
        lock (_gate)
        {
            FindNode(path)!.Name = name;
        }
    }

    public int Count(string method, string pathStart)
    {
        lock (_gate)
        {
            return Requests.Count(request => request.StartsWith($"{method} {pathStart}", StringComparison.Ordinal));
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        var ifMatch = request.Headers.TryGetValues("If-Match", out var values) ? values.Single() : null;
        lock (_gate)
        {
            Requests.Add($"{request.Method} {path}{(ifMatch is null ? "" : $" If-Match:{ifMatch}")}");
        }

        if (!request.Headers.TryGetValues(CloudContract.ProtocolHeader, out var protocol) || protocol.Single() != "1")
        {
            return Error(HttpStatusCode.BadRequest, "Protokoll fehlt.");
        }

        if (IsSignedOut || request.Headers.Authorization?.Parameter != Token)
        {
            return Error(HttpStatusCode.Unauthorized, "Dieses Gerät ist nicht (mehr) angemeldet.");
        }

        if (IsForbidden)
        {
            return Error(HttpStatusCode.Forbidden, "Diese App darf ThixDrive nicht verwenden.");
        }

        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        lock (_gate)
        {
            return Answer(request.Method, path, query, ifMatch, body);
        }
    }

    private HttpResponseMessage Answer(HttpMethod method, string path, System.Collections.Specialized.NameValueCollection query, string? ifMatch, byte[] body)
    {
        var parts = path[DriveContract.Root.Length..].Trim('/').Split('/');
        long? Parent() => query["parentId"] is { Length: > 0 } parent ? long.Parse(parent, CultureInfo.InvariantCulture) : null;

        switch (method.Method, parts)
        {
            case ("GET", ["quota"]):
                return Json(HttpStatusCode.OK, new DriveQuota(_nodes.Values.Sum(node => node.Content?.Length ?? 0), null, 0, 0, 2L << 30,
                    DriveContract.MaxSimpleUploadBytes, DriveContract.ChunkBytes));

            case ("GET", ["items"]):
            {
                var parent = Parent();
                if (parent is { } id && (!_nodes.TryGetValue(id, out var folder) || folder.IsTrashed || folder.Kind != DriveItemKind.Folder))
                {
                    return Error(HttpStatusCode.NotFound, "Diesen Ordner gibt es nicht.");
                }

                var offset = int.Parse(query["offset"] ?? "0", CultureInfo.InvariantCulture);
                var limit = Math.Clamp(int.Parse(query["limit"] ?? "200", CultureInfo.InvariantCulture), 1, DriveContract.MaxPageSize);
                var children = Children(parent).OrderBy(node => node.Kind).ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase).ToList();
                var page = children.Skip(offset).Take(limit).Select(ToItem).ToList();
                return Json(HttpStatusCode.OK, new DriveListing(parent is { } shown ? ToItem(_nodes[shown]) : null, [], page, children.Count, offset, limit));
            }

            case ("GET", ["items", var id]):
                return _nodes.TryGetValue(long.Parse(id, CultureInfo.InvariantCulture), out var item)
                    ? Json(HttpStatusCode.OK, ToItem(item))
                    : Error(HttpStatusCode.NotFound, "Nicht gefunden.");

            case ("GET", ["items", var id, "content"]):
            {
                if (LiveNode(id) is not { Kind: DriveItemKind.File } file)
                {
                    return Error(HttpStatusCode.NotFound, "Nicht gefunden.");
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file.Content!) };
                response.Headers.ETag = new EntityTagHeaderValue(ETagOf(file));
                return response;
            }

            case ("PUT", ["items", var id, "content"]):
            {
                if (LiveNode(id) is not { Kind: DriveItemKind.File } file)
                {
                    return Error(HttpStatusCode.NotFound, "Nicht gefunden.");
                }

                if (VersionProblem(file, ifMatch) is { } problem)
                {
                    return problem;
                }

                if (IsFull)
                {
                    return Error(HttpStatusCode.InsufficientStorage, "Der Speicherplatz ist voll.");
                }

                NewVersion(file, body);
                return Json(HttpStatusCode.OK, ToItem(file));
            }

            case ("POST", ["files"]):
            {
                if (body.Length > DriveContract.MaxSimpleUploadBytes)
                {
                    return Error(HttpStatusCode.RequestEntityTooLarge, "Zu groß.");
                }

                return CreateFile(Parent(), query["name"] ?? "", body);
            }

            case ("POST", ["folders"]):
            {
                var folder = System.Text.Json.JsonSerializer.Deserialize<DriveFolderRequest>(body, CloudContract.Json)!;
                if (NameProblem(folder.ParentId, folder.Name) is { } problem)
                {
                    return problem;
                }

                return Json(HttpStatusCode.Created, ToItem(AddNode(folder.ParentId, folder.Name, DriveItemKind.Folder, null)));
            }

            case ("POST", ["items", var id, "rename"]):
            {
                var rename = System.Text.Json.JsonSerializer.Deserialize<DriveRenameRequest>(body, CloudContract.Json)!;
                if (LiveNode(id) is not { } node)
                {
                    return Error(HttpStatusCode.NotFound, "Nicht gefunden.");
                }

                if (NameProblem(node.ParentId, rename.Name, node.Id) is { } problem)
                {
                    return problem;
                }

                node.Name = rename.Name;
                return Json(HttpStatusCode.OK, ToItem(node));
            }

            case ("POST", ["items", var id, "move"]):
            {
                var move = System.Text.Json.JsonSerializer.Deserialize<DriveMoveRequest>(body, CloudContract.Json)!;
                if (LiveNode(id) is not { } node)
                {
                    return Error(HttpStatusCode.NotFound, "Nicht gefunden.");
                }

                if (NameProblem(move.ParentId, node.Name, node.Id) is { } problem)
                {
                    return problem;
                }

                node.ParentId = move.ParentId;
                return Json(HttpStatusCode.OK, ToItem(node));
            }

            case ("POST", ["items", var id, "trash"]):
            {
                if (LiveNode(id) is not { } node)
                {
                    return Error(HttpStatusCode.NotFound, "Nicht gefunden.");
                }

                node.IsTrashed = true;
                return Json(HttpStatusCode.OK, ToItem(node));
            }

            case ("POST", ["uploads"]):
            {
                var start = System.Text.Json.JsonSerializer.Deserialize<DriveUploadRequest>(body, CloudContract.Json)!;
                if (start.ItemId is { } itemId)
                {
                    if (LiveNode(itemId.ToString(CultureInfo.InvariantCulture)) is not { Kind: DriveItemKind.File } file)
                    {
                        return Error(HttpStatusCode.NotFound, "Nicht gefunden.");
                    }

                    if (VersionProblem(file, start.IfMatch) is { } problem)
                    {
                        return problem;
                    }
                }
                else if (NameProblem(start.ParentId, start.Name ?? "") is { } problem)
                {
                    return problem;
                }

                var upload = new Upload(Guid.NewGuid(), start, new MemoryStream());
                _uploads[upload.Id] = upload;
                return Json(HttpStatusCode.Created, StateOf(upload));
            }

            case ("PUT", ["uploads", var id, "chunk"]):
            {
                var upload = _uploads[Guid.Parse(id)];
                var offset = long.Parse(query["offset"] ?? "0", CultureInfo.InvariantCulture);
                if (body.Length > DriveContract.MaxChunkBytes || offset > upload.Data.Length)
                {
                    return Error(HttpStatusCode.BadRequest, "Falsches Stück.");
                }

                upload.Data.Position = offset;
                upload.Data.Write(body);
                return Json(HttpStatusCode.OK, StateOf(upload));
            }

            case ("POST", ["uploads", var id, "finish"]):
            {
                var upload = _uploads[Guid.Parse(id)];
                _uploads.Remove(upload.Id);
                if (upload.Data.Length != upload.Request.Size)
                {
                    return Error(HttpStatusCode.BadRequest, "Es fehlen Bytes.");
                }

                if (upload.Request.ItemId is { } itemId)
                {
                    var file = _nodes[itemId];
                    NewVersion(file, upload.Data.ToArray());
                    return Json(HttpStatusCode.OK, ToItem(file));
                }

                return CreateFile(upload.Request.ParentId, upload.Request.Name ?? "", upload.Data.ToArray());
            }

            default:
                return Error(HttpStatusCode.NotFound, "Diese Adresse gibt es nicht.");
        }
    }

    private HttpResponseMessage CreateFile(long? parentId, string name, byte[] content)
    {
        if (NameProblem(parentId, name) is { } problem)
        {
            return problem;
        }

        if (IsFull)
        {
            return Error(HttpStatusCode.InsufficientStorage, "Der Speicherplatz ist voll.");
        }

        return Json(HttpStatusCode.Created, ToItem(AddNode(parentId, name, DriveItemKind.File, content)));
    }

    private HttpResponseMessage? NameProblem(long? parentId, string name, long? self = null)
    {
        if (name.Length == 0 || name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || name.EndsWith('.') || name.EndsWith(' '))
        {
            return Json(HttpStatusCode.BadRequest, new ApiError("Ein Name darf diese Zeichen nicht enthalten: / \\ : * ? \" < > |", "name"));
        }

        if (parentId is { } id && (!_nodes.TryGetValue(id, out var folder) || folder.IsTrashed || folder.Kind != DriveItemKind.Folder))
        {
            return Error(HttpStatusCode.NotFound, "Diesen Ordner gibt es nicht.");
        }

        var taken = ChildNamed(parentId, name);
        return taken is not null && taken.Id != self
            ? Json(HttpStatusCode.Conflict, new DriveConflict($"„{name}“ gibt es dort schon.", "name", ToItem(taken)))
            : null;
    }

    private HttpResponseMessage? VersionProblem(Node file, string? ifMatch)
    {
        if (ifMatch is null)
        {
            return Error(HttpStatusCode.PreconditionRequired, "If-Match fehlt.");
        }

        return ifMatch != DriveContract.AnyVersion && ifMatch != ETagOf(file)
            ? Json(HttpStatusCode.PreconditionFailed, new DriveConflict("Die Datei wurde inzwischen geändert.", null, ToItem(file)))
            : null;
    }

    private Node AddNode(long? parentId, string name, DriveItemKind kind, byte[]? content)
    {
        var now = DateTimeOffset.UtcNow;
        var node = new Node { Id = ++_lastId, ParentId = parentId, Name = name, Kind = kind, CreatedAt = now, ModifiedAt = now };
        _nodes[node.Id] = node;
        if (content is not null)
        {
            NewVersion(node, content);
        }

        return node;
    }

    private void NewVersion(Node file, byte[] content)
    {
        file.Content = content;
        file.VersionId = ++_lastVersion;
        file.Version++;
        file.ModifiedAt = DateTimeOffset.UtcNow;
    }

    private IEnumerable<Node> Children(long? parentId) => _nodes.Values.Where(node => node.ParentId == parentId && !node.IsTrashed);

    private Node? ChildNamed(long? parentId, string name) =>
        Children(parentId).FirstOrDefault(node => string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase));

    private Node? LiveNode(string id) =>
        _nodes.TryGetValue(long.Parse(id, CultureInfo.InvariantCulture), out var node) && !node.IsTrashed ? node : null;

    private Node? FindNode(string path)
    {
        long? parent = null;
        Node? node = null;
        foreach (var name in path.Split('/'))
        {
            node = ChildNamed(parent, name);
            if (node is null)
            {
                return null;
            }

            parent = node.Id;
        }

        return node;
    }

    private static string ETagOf(Node file) => $"\"{file.VersionId.ToString(CultureInfo.InvariantCulture)}\"";

    private static DriveItem ToItem(Node node) => new()
    {
        Id = node.Id,
        ParentId = node.ParentId,
        Kind = node.Kind,
        Name = node.Name,
        Size = node.Content?.Length ?? 0,
        ETag = node.Kind == DriveItemKind.File ? ETagOf(node) : null,
        Sha256 = node.Content is null ? null : Convert.ToHexStringLower(SHA256.HashData(node.Content)),
        Version = node.Kind == DriveItemKind.File ? node.Version : null,
        CreatedAt = node.CreatedAt,
        ModifiedAt = node.ModifiedAt,
        IsTrashed = node.IsTrashed,
    };

    private static DriveUploadState StateOf(Upload upload) =>
        new(upload.Id, upload.Request.Size, upload.Data.Length, DriveContract.ChunkBytes, DateTimeOffset.UtcNow.AddHours(DriveContract.UploadHours));

    private static HttpResponseMessage Error(HttpStatusCode status, string message) => Json(status, new ApiError(message));

    private static HttpResponseMessage Json<T>(HttpStatusCode status, T body) =>
        new(status) { Content = JsonContent.Create(body, options: CloudContract.Json) };

    private sealed class Node
    {
        public long Id { get; init; }

        public long? ParentId { get; set; }

        public string Name { get; set; } = "";

        public DriveItemKind Kind { get; init; }

        public byte[]? Content { get; set; }

        public long VersionId { get; set; }

        public int Version { get; set; }

        public DateTimeOffset CreatedAt { get; init; }

        public DateTimeOffset ModifiedAt { get; set; }

        public bool IsTrashed { get; set; }
    }

    private sealed record Upload(Guid Id, DriveUploadRequest Request, MemoryStream Data);
}
