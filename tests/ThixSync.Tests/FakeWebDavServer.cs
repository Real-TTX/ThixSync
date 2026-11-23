using System.Globalization;
using System.Net;
using System.Text;

namespace ThixSync.Tests;

/// <summary>
/// A WebDAV server in memory (an HttpMessageHandler: no network): PROPFIND Depth 0/1, GET, PUT, MKCOL, DELETE, MOVE with
/// Destination/Overwrite, Basic auth, and – as Nextcloud does – X-OC-Mtime on PUT. Paths are kept unescaped; every request URI is
/// recorded raw, so tests can check the percent-encoding the client sent.
/// </summary>
internal sealed class FakeWebDavServer : HttpMessageHandler
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);

    public FakeWebDavServer(string top = "/dav")
    {
        Top = top.TrimEnd('/');
        AddFolder(Top);
        for (var parent = Parent(Top); parent.Length > 0; parent = Parent(parent))
        {
            AddFolder(parent);
        }
    }

    /// <summary>The unescaped path of the top of the tree ("/dav", "/remote.php/dav/files/anna").</summary>
    public string Top { get; }

    /// <summary>Nextcloud: X-OC-Mtime sets the time and is answered with "X-OC-MTime: accepted".</summary>
    public bool AcceptsMtime { get; init; }

    public string? User { get; init; }

    public string? Password { get; init; }

    /// <summary>Nextcloud: the user id the OCS API returns for the signed-in user (null: no OCS API).</summary>
    public string? OcsUserId { get; init; }

    /// <summary>Every request: method and the raw (escaped) path and query.</summary>
    public List<string> Requests { get; } = [];

    public sealed class Node
    {
        public bool IsFolder { get; init; }

        public byte[] Content { get; set; } = [];

        public DateTimeOffset Modified { get; set; } = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    }

    public void AddFolder(string path)
    {
        lock (_gate)
        {
            _nodes[path.TrimEnd('/')] = new Node { IsFolder = true };
        }
    }

    public void AddFile(string path, string content, DateTimeOffset? modified = null)
    {
        lock (_gate)
        {
            _nodes[path] = new Node { Content = Encoding.UTF8.GetBytes(content), Modified = modified ?? new DateTimeOffset(2026, 11, 2, 8, 30, 0, TimeSpan.Zero) };
        }
    }

    public Node? Find(string path)
    {
        lock (_gate)
        {
            return _nodes.GetValueOrDefault(path.TrimEnd('/'));
        }
    }

    public string? Text(string path) => Find(path) is { IsFolder: false } node ? Encoding.UTF8.GetString(node.Content) : null;

    public IReadOnlyList<string> Paths()
    {
        lock (_gate)
        {
            return [.. _nodes.Keys.Order(StringComparer.Ordinal)];
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var raw = request.RequestUri!.PathAndQuery;
        lock (_gate)
        {
            Requests.Add($"{request.Method} {raw}");
        }

        if (User is not null && request.Headers.Authorization?.Parameter != Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}")))
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        if (request.RequestUri.AbsolutePath == "/ocs/v2.php/cloud/user")
        {
            return OcsUserId is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ocs\":{\"meta\":{\"status\":\"ok\"},\"data\":{\"id\":\"" + OcsUserId + "\"}}}", Encoding.UTF8, "application/json") };
        }

        var path = Uri.UnescapeDataString(request.RequestUri.AbsolutePath).TrimEnd('/');
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        lock (_gate)
        {
            return request.Method.Method switch
            {
                "PROPFIND" => Propfind(path, request.Headers.TryGetValues("Depth", out var depth) ? depth.First() : "1"),
                "GET" => Get(path),
                "PUT" => Put(path, body, request),
                "MKCOL" => Mkcol(path),
                "DELETE" => Delete(path),
                "MOVE" => Move(path, request),
                _ => new HttpResponseMessage(HttpStatusCode.MethodNotAllowed),
            };
        }
    }

    private HttpResponseMessage Propfind(string path, string depth)
    {
        if (!_nodes.TryGetValue(path, out var node))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\" xmlns:oc=\"http://owncloud.org/ns\">");
        AppendResponse(xml, path, node);
        if (node.IsFolder && depth != "0")
        {
            foreach (var (childPath, child) in _nodes.Where(pair => Parent(pair.Key) == path && pair.Key != path))
            {
                AppendResponse(xml, childPath, child);
            }
        }

        xml.Append("</d:multistatus>");
        return new HttpResponseMessage(HttpStatusCode.MultiStatus) { Content = new StringContent(xml.ToString(), Encoding.UTF8, "application/xml") };
    }

    private static void AppendResponse(StringBuilder xml, string path, Node node)
    {
        var href = string.Join('/', path.Split('/').Select(Uri.EscapeDataString)) + (node.IsFolder ? "/" : "");
        xml.Append("<d:response><d:href>").Append(href).Append("</d:href><d:propstat><d:prop>");
        xml.Append(node.IsFolder ? "<d:resourcetype><d:collection/></d:resourcetype>" : "<d:resourcetype/>");
        if (!node.IsFolder)
        {
            xml.Append("<d:getcontentlength>").Append(node.Content.Length.ToString(CultureInfo.InvariantCulture)).Append("</d:getcontentlength>");
            xml.Append("<oc:checksums><oc:checksum>SHA1:ABCDEF MD5:0123</oc:checksum></oc:checksums>");
        }

        xml.Append("<d:getlastmodified>").Append(node.Modified.ToString("r", CultureInfo.InvariantCulture)).Append("</d:getlastmodified>");
        xml.Append("<d:getetag>\"e").Append(node.Modified.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)).Append("\"</d:getetag>");
        xml.Append("</d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>");
        xml.Append("<d:propstat><d:prop><oc:fileid/></d:prop><d:status>HTTP/1.1 404 Not Found</d:status></d:propstat></d:response>");
    }

    private HttpResponseMessage Get(string path)
    {
        return _nodes.TryGetValue(path, out var node) && !node.IsFolder
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(node.Content) }
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private HttpResponseMessage Put(string path, byte[] body, HttpRequestMessage request)
    {
        if (!_nodes.TryGetValue(Parent(path), out var parent) || !parent.IsFolder)
        {
            return new HttpResponseMessage(HttpStatusCode.Conflict);
        }

        if (request.Content?.Headers.ContentLength is not { } length || length != body.Length)
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        var node = new Node { Content = body, Modified = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero) };
        var response = new HttpResponseMessage(_nodes.ContainsKey(path) ? HttpStatusCode.NoContent : HttpStatusCode.Created);
        if (AcceptsMtime && request.Headers.TryGetValues("X-OC-Mtime", out var mtime))
        {
            node.Modified = DateTimeOffset.FromUnixTimeSeconds(long.Parse(mtime.First(), CultureInfo.InvariantCulture));
            response.Headers.TryAddWithoutValidation("X-OC-MTime", "accepted");
            response.Headers.TryAddWithoutValidation("OC-FileId", "00000042oc");
        }

        _nodes[path] = node;
        return response;
    }

    private HttpResponseMessage Mkcol(string path)
    {
        if (_nodes.ContainsKey(path))
        {
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        if (!_nodes.TryGetValue(Parent(path), out var parent) || !parent.IsFolder)
        {
            return new HttpResponseMessage(HttpStatusCode.Conflict);
        }

        _nodes[path] = new Node { IsFolder = true };
        return new HttpResponseMessage(HttpStatusCode.Created);
    }

    private HttpResponseMessage Delete(string path)
    {
        if (!_nodes.ContainsKey(path))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        foreach (var key in _nodes.Keys.Where(key => key == path || key.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
        {
            _nodes.Remove(key);
        }

        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    private HttpResponseMessage Move(string path, HttpRequestMessage request)
    {
        if (!request.Headers.TryGetValues("Destination", out var destinations) || !Uri.TryCreate(destinations.First(), UriKind.Absolute, out var destination))
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        var target = Uri.UnescapeDataString(destination.AbsolutePath).TrimEnd('/');
        if (!_nodes.ContainsKey(path))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (!_nodes.TryGetValue(Parent(target), out var parent) || !parent.IsFolder)
        {
            return new HttpResponseMessage(HttpStatusCode.Conflict);
        }

        var overwrite = request.Headers.TryGetValues("Overwrite", out var values) && values.First() == "T";
        if (_nodes.ContainsKey(target) && !overwrite)
        {
            return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
        }

        Delete(target);
        foreach (var key in _nodes.Keys.Where(key => key == path || key.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
        {
            var node = _nodes[key];
            _nodes.Remove(key);
            _nodes[target + key[path.Length..]] = node;
        }

        return new HttpResponseMessage(HttpStatusCode.Created);
    }

    private static string Parent(string path)
    {
        var index = path.LastIndexOf('/');
        return index <= 0 ? "" : path[..index];
    }
}
