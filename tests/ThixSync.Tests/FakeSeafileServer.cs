using System.Net;
using System.Text;
using System.Text.Json;

namespace ThixSync.Tests;

/// <summary>
/// A Seafile server in memory (api2, one library "Meine Bibliothek"): auth-token, repos, dir (list, recursive, mkdir, rename),
/// file (detail, download link, rename, delete), upload-link + multipart upload, fileops/move. Like the real server it keeps both
/// when a folder is made twice or a rename hits an existing name ("Name (1)") – the storage must avoid that.
/// </summary>
internal sealed class FakeSeafileServer : HttpMessageHandler
{
    public const string Host = "https://seafile.test/";
    public const string LibraryId = "0f1e2d3c-repo";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal) { ["/"] = new Node { IsFolder = true } };
    private string _token = "token-1";

    public sealed class Node
    {
        public bool IsFolder { get; init; }

        public byte[] Content { get; set; } = [];

        public long Mtime { get; set; } = 1_790_000_000;
    }

    public string User { get; init; } = "anna@example.com";

    public string Password { get; init; } = "geheim";

    /// <summary>Older servers: recursive=1 is ignored (no parent_dir).</summary>
    public bool SupportsRecursive { get; init; } = true;

    public List<string> Requests { get; } = [];

    /// <summary>The names of the uploaded files as they arrived in the multipart body.</summary>
    public List<string> UploadedNames { get; } = [];

    public int SignIns { get; private set; }

    public void AddFolder(string path)
    {
        lock (_gate)
        {
            _nodes[path] = new Node { IsFolder = true };
        }
    }

    public void AddFile(string path, string content)
    {
        lock (_gate)
        {
            _nodes[path] = new Node { Content = Encoding.UTF8.GetBytes(content) };
        }
    }

    public string? Text(string path)
    {
        lock (_gate)
        {
            return _nodes.TryGetValue(path, out var node) && !node.IsFolder ? Encoding.UTF8.GetString(node.Content) : null;
        }
    }

    public IReadOnlyList<string> Paths()
    {
        lock (_gate)
        {
            return [.. _nodes.Keys.Order(StringComparer.Ordinal)];
        }
    }

    /// <summary>The token expires: the next request with it is refused.</summary>
    public void ExpireToken()
    {
        lock (_gate)
        {
            _token = "token-" + Guid.NewGuid().ToString("N");
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        lock (_gate)
        {
            Requests.Add($"{request.Method} {uri.PathAndQuery}");
            var query = Query(uri);
            var route = uri.AbsolutePath;
            if (route == "/api2/auth-token/" && request.Method == HttpMethod.Post)
            {
                return SignIn(Form(body));
            }

            if (route.StartsWith("/seafhttp/files/", StringComparison.Ordinal))
            {
                var path = Uri.UnescapeDataString(route["/seafhttp/files/".Length..]);
                return _nodes.TryGetValue(path, out var file) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file.Content) } : Status(404);
            }

            if (route == "/seafhttp/upload-api/upload-token" && request.Method == HttpMethod.Post)
            {
                return Upload(body, request.Content!.Headers.ContentType!);
            }

            if (request.Headers.Authorization is not { Scheme: "Token" } authorization || authorization.Parameter != _token)
            {
                return Json(401, """{"detail":"Invalid token"}""");
            }

            var p = query.GetValueOrDefault("p", "/");
            return (request.Method.Method, route) switch
            {
                ("GET", "/api2/repos/") => Json(200, $$"""[{"id":"other","name":"Andere","type":"repo","encrypted":false},{"id":"{{LibraryId}}","name":"Meine Bibliothek","type":"repo","encrypted":false}]"""),
                ("GET", $"/api2/repos/{LibraryId}/dir/") => ListDir(p, query.GetValueOrDefault("t"), query.GetValueOrDefault("recursive") == "1"),
                ("POST", $"/api2/repos/{LibraryId}/dir/") => DirOperation(p, Form(body)),
                ("DELETE", $"/api2/repos/{LibraryId}/dir/") => Remove(p),
                ("GET", $"/api2/repos/{LibraryId}/file/detail/") => FileDetail(p),
                ("GET", $"/api2/repos/{LibraryId}/file/") => _nodes.TryGetValue(p, out var node) && !node.IsFolder
                    ? Json(200, JsonSerializer.Serialize("https://seafile.test/seafhttp/files/" + Uri.EscapeDataString(p)))
                    : Status(404),
                ("POST", $"/api2/repos/{LibraryId}/file/") => Rename(p, Form(body)),
                ("DELETE", $"/api2/repos/{LibraryId}/file/") => Remove(p),
                ("GET", $"/api2/repos/{LibraryId}/upload-link/") => _nodes.ContainsKey(p) ? Json(200, "\"https://seafile.test/seafhttp/upload-api/upload-token\"") : Status(404),
                ("POST", $"/api2/repos/{LibraryId}/fileops/move/") => MoveItems(p, Form(body)),
                _ => Status(404),
            };
        }
    }

    private HttpResponseMessage SignIn(Dictionary<string, string> form)
    {
        if (form.GetValueOrDefault("username") != User || form.GetValueOrDefault("password") != Password)
        {
            return Json(400, """{"non_field_errors":["Unable to login with provided credentials."]}""");
        }

        SignIns++;
        return Json(200, $$"""{"token":"{{_token}}"}""");
    }

    private HttpResponseMessage ListDir(string path, string? type, bool recursive)
    {
        var folder = path.TrimEnd('/').Length == 0 ? "/" : path.TrimEnd('/');
        if (!_nodes.TryGetValue(folder, out var node) || !node.IsFolder)
        {
            return Json(404, """{"error_msg":"Folder not found."}""");
        }

        var deep = recursive && SupportsRecursive;
        var items = _nodes.Where(pair => pair.Key != folder && (deep ? IsBelow(pair.Key, folder) : ParentOf(pair.Key) == folder))
            .Where(pair => type is null || (type == "d") == pair.Value.IsFolder)
            .Select(pair => new Dictionary<string, object>
            {
                ["type"] = pair.Value.IsFolder ? "dir" : "file",
                ["name"] = pair.Key[(pair.Key.LastIndexOf('/') + 1)..],
                ["mtime"] = pair.Value.Mtime,
                ["size"] = pair.Value.Content.Length,
                ["id"] = "0000",
                ["parent_dir"] = ParentOf(pair.Key) == "/" ? "/" : ParentOf(pair.Key) + "/",
            })
            .Select(item =>
            {
                if (!deep)
                {
                    item.Remove("parent_dir");
                }

                return item;
            });
        return Json(200, JsonSerializer.Serialize(items));
    }

    private HttpResponseMessage FileDetail(string path)
    {
        return _nodes.TryGetValue(path, out var node) && !node.IsFolder
            ? Json(200, $$"""{"id":"abc","type":"file","name":"{{path[(path.LastIndexOf('/') + 1)..]}}","size":{{node.Content.Length}},"mtime":{{node.Mtime}}}""")
            : Json(404, """{"error_msg":"File not found"}""");
    }

    private HttpResponseMessage DirOperation(string path, Dictionary<string, string> form)
    {
        if (form.GetValueOrDefault("operation") == "mkdir")
        {
            if (!_nodes.ContainsKey(ParentOf(path)))
            {
                return Json(404, """{"error_msg":"Parent folder not found."}""");
            }

            _nodes[FreeName(path)] = new Node { IsFolder = true };
            return Json(201, "\"success\"");
        }

        return Rename(path, form);
    }

    private HttpResponseMessage Rename(string path, Dictionary<string, string> form)
    {
        if (form.GetValueOrDefault("operation") != "rename" || !_nodes.ContainsKey(path))
        {
            return Status(form.GetValueOrDefault("operation") == "rename" ? 404 : 400);
        }

        var target = FreeName(Combine(ParentOf(path), form["newname"]));
        MoveTree(path, target);
        return Json(200, "\"success\"");
    }

    private HttpResponseMessage MoveItems(string parent, Dictionary<string, string> form)
    {
        var source = Combine(parent.TrimEnd('/').Length == 0 ? "/" : parent.TrimEnd('/'), form["file_names"]);
        var targetFolder = form["dst_dir"].TrimEnd('/').Length == 0 ? "/" : form["dst_dir"].TrimEnd('/');
        if (!_nodes.ContainsKey(source) || !_nodes.ContainsKey(targetFolder) || form["dst_repo"] != LibraryId)
        {
            return Status(404);
        }

        MoveTree(source, FreeName(Combine(targetFolder, form["file_names"])));
        return Json(200, """{"success":true}""");
    }

    private HttpResponseMessage Remove(string path)
    {
        foreach (var key in _nodes.Keys.Where(key => key == path || IsBelow(key, path)).ToList())
        {
            _nodes.Remove(key);
        }

        return Json(200, "\"success\"");
    }

    private HttpResponseMessage Upload(byte[] body, System.Net.Http.Headers.MediaTypeHeaderValue contentType)
    {
        var boundary = contentType.Parameters.First(parameter => parameter.Name == "boundary").Value!.Trim('"');
        var parts = ParseMultipart(body, boundary);
        var parent = Encoding.UTF8.GetString(parts["parent_dir"].Body).TrimEnd('/');
        var (fileName, content) = (parts["file"].FileName!, parts["file"].Body);
        UploadedNames.Add(fileName);
        if (!_nodes.ContainsKey(parent.Length == 0 ? "/" : parent))
        {
            return Status(404);
        }

        var path = Combine(parent.Length == 0 ? "/" : parent, fileName);
        _nodes[path] = new Node { Content = content, Mtime = 1_795_000_000 };
        return Json(200, $$"""[{"name":"{{fileName}}","id":"abc","size":{{content.Length}}}]""");
    }

    private static Dictionary<string, (string? FileName, byte[] Body)> ParseMultipart(byte[] body, string boundary)
    {
        var text = Encoding.Latin1.GetString(body); // byte for byte, split on the ASCII boundary
        var parts = new Dictionary<string, (string?, byte[])>();
        foreach (var section in text.Split("--" + boundary).Skip(1))
        {
            if (section.StartsWith("--", StringComparison.Ordinal))
            {
                break;
            }

            var split = section.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var headers = Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(section[..split]));
            var content = Encoding.Latin1.GetBytes(section[(split + 4)..^2]);
            var disposition = headers.Split("\r\n").First(line => line.StartsWith("Content-Disposition", StringComparison.OrdinalIgnoreCase));
            var name = ValueOf(disposition, "name")!;
            parts[name] = (ValueOf(disposition, "filename"), content);
        }

        return parts;
    }

    private static string? ValueOf(string disposition, string key)
    {
        var marker = $" {key}=";
        var index = disposition.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var rest = disposition[(index + marker.Length)..].Trim();
        return rest.StartsWith('"') ? rest[1..rest.IndexOf('"', 1)] : rest.Split(';')[0];
    }

    private void MoveTree(string from, string to)
    {
        foreach (var key in _nodes.Keys.Where(key => key == from || IsBelow(key, from)).ToList())
        {
            var node = _nodes[key];
            _nodes.Remove(key);
            _nodes[to + key[from.Length..]] = node;
        }
    }

    /// <summary>Seafile keeps both: "Name" is taken → "Name (1)".</summary>
    private string FreeName(string path)
    {
        if (!_nodes.ContainsKey(path))
        {
            return path;
        }

        for (var number = 1; ; number++)
        {
            var candidate = $"{path} ({number})";
            if (!_nodes.ContainsKey(candidate))
            {
                return candidate;
            }
        }
    }

    private static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "");

    private static Dictionary<string, string> Form(byte[] body) =>
        Encoding.UTF8.GetString(body).Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')), pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "");

    private static string ParentOf(string path)
    {
        var index = path.LastIndexOf('/');
        return index <= 0 ? "/" : path[..index];
    }

    private static bool IsBelow(string path, string folder) => folder == "/" ? path != "/" : path.StartsWith(folder + "/", StringComparison.Ordinal);

    private static string Combine(string folder, string name) => folder == "/" ? "/" + name : folder + "/" + name;

    private static HttpResponseMessage Status(int code) => new((HttpStatusCode)code);

    private static HttpResponseMessage Json(int code, string json) =>
        new((HttpStatusCode)code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
