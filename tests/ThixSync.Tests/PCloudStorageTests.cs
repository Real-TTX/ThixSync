using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;
using ThixSync.Core.Storage.Providers.PCloud;

namespace ThixSync.Tests;

/// <summary>pCloud: digest sign-in, the recursive listing, uploads with mtime, the result codes, against an in-memory account.</summary>
public sealed class PCloudStorageTests
{
    [Fact]
    public void Password_digest_matches_the_documented_formula()
    {
        // sha1("geheim" + sha1("anna@example.com") + "YWJjZGVm"), computed with openssl
        Assert.Equal("d4b1acbc6e3a2869cfa6db452066ad74875f78c9", PCloudClient.PasswordDigest("Anna@Example.com", "geheim", "YWJjZGVm"));
    }

    [Fact]
    public async Task Signs_in_with_the_digest_and_never_sends_the_password()
    {
        var fake = new FakePCloud("anna@example.com", "geheim");
        await using var storage = new PCloudStorage(Context(), fake);

        await storage.ConnectAsync(createRoot: true, CancellationToken.None);

        Assert.Equal(1, fake.SignIns);
        Assert.DoesNotContain(fake.Log, line => line.Contains("geheim", StringComparison.Ordinal));
        Assert.True(fake.Folders.ContainsKey("/Backup/Fotos"));
        Assert.StartsWith("https://eapi.pcloud.com/", fake.Log.First());
    }

    [Fact]
    public async Task A_wrong_password_is_an_authentication_error()
    {
        var fake = new FakePCloud("anna@example.com", "anders");
        await using var storage = new PCloudStorage(Context(), fake);

        var error = await Assert.ThrowsAsync<PCloudException>(() => storage.ConnectAsync(false, CancellationToken.None));

        Assert.Equal(StorageErrorKind.Authentication, error.Kind);
        Assert.True(error.StopsRun);
        Assert.DoesNotContain("geheim", error.Message);
    }

    [Fact]
    public async Task Uploads_with_mtime_lists_recursively_reads_moves_and_deletes()
    {
        var fake = new FakePCloud("anna@example.com", "geheim");
        await using var storage = new PCloudStorage(Context(("region", "us")), fake);
        await storage.ConnectAsync(createRoot: true, CancellationToken.None);
        Assert.Contains("api.pcloud.com", fake.Log.First());
        Assert.True(storage.Capabilities.Has(StorageFeatures.SetModifiedTime | StorageFeatures.RealFolders | StorageFeatures.Move));

        var modified = new DateTimeOffset(2023, 7, 1, 8, 9, 10, TimeSpan.Zero);
        var reported = 0L;
        var data = Encoding.UTF8.GetBytes("Grüße aus pCloud");
        var written = await storage.WriteAsync("Urlaub 2023/Strand & Meer #1.txt", new NonSeekableStream(data), data.Length, modified,
            new SyncProgress(value => Interlocked.Add(ref reported, value)), CancellationToken.None);
        await S3StorageTests.WriteTextAsync(storage, "Urlaub 2023/tief/unten/ä.txt", "tief", modified);
        await storage.CreateFolderAsync("Leer", CancellationToken.None);

        Assert.Equal(modified, written.Modified);
        Assert.Equal(data.Length, written.Size);
        Assert.Equal(data.Length, reported);
        Assert.Equal("Strand & Meer #1.txt", fake.Files["/Backup/Fotos/Urlaub 2023/Strand & Meer #1.txt"].PartFileName); // UTF-8 safe multipart name

        var listed = await S3StorageTests.ListAsync(storage, "");
        Assert.Equal(["Leer", "Urlaub 2023", "Urlaub 2023/Strand & Meer #1.txt", "Urlaub 2023/tief", "Urlaub 2023/tief/unten", "Urlaub 2023/tief/unten/ä.txt"],
            listed.Select(entry => entry.Path).Order(StringComparer.Ordinal));
        Assert.Equal(written, listed.Single(entry => entry.Path == "Urlaub 2023/Strand & Meer #1.txt"));
        Assert.Equal(1, fake.Log.Count(line => line.Contains("/listfolder?", StringComparison.Ordinal) && line.Contains("recursive=1", StringComparison.Ordinal)));

        Assert.Equal("Grüße aus pCloud", await S3StorageTests.ReadTextAsync(storage, "Urlaub 2023/Strand & Meer #1.txt"));
        Assert.Equal(written, await storage.GetAsync("Urlaub 2023/Strand & Meer #1.txt", CancellationToken.None));
        Assert.True((await storage.GetAsync("Urlaub 2023/tief", CancellationToken.None))!.Value.IsFolder);
        Assert.Null(await storage.GetAsync("gibt es nicht", CancellationToken.None));
        Assert.Equal(["Leer", "Urlaub 2023"], await storage.ListChildFoldersAsync(CancellationToken.None));

        await storage.MoveAsync("Urlaub 2023/Strand & Meer #1.txt", "Neu/Strand.txt", CancellationToken.None);
        await storage.MoveAsync("Urlaub 2023/tief", "Neu/tief", CancellationToken.None);
        Assert.True(fake.Files.ContainsKey("/Backup/Fotos/Neu/Strand.txt"));
        Assert.True(fake.Files.ContainsKey("/Backup/Fotos/Neu/tief/unten/ä.txt"));

        await storage.DeleteAsync("Neu/Strand.txt", isFolder: false, CancellationToken.None);
        await storage.DeleteAsync("Neu", isFolder: true, CancellationToken.None);
        await storage.DeleteAsync("gibt es nicht", isFolder: false, CancellationToken.None);
        Assert.Empty(fake.Files);
        Assert.False(fake.Folders.ContainsKey("/Backup/Fotos/Neu"));
    }

    [Fact]
    public async Task An_expired_token_is_renewed_once()
    {
        var fake = new FakePCloud("anna@example.com", "geheim");
        await using var storage = new PCloudStorage(Context(), fake);
        await storage.ConnectAsync(createRoot: true, CancellationToken.None);

        fake.ExpireTokens();
        await storage.CreateFolderAsync("Nach dem Ablauf", CancellationToken.None);

        Assert.Equal(2, fake.SignIns);
        Assert.True(fake.Folders.ContainsKey("/Backup/Fotos/Nach dem Ablauf"));
    }

    [Theory]
    [InlineData(2000, StorageErrorKind.Authentication)]
    [InlineData(1000, StorageErrorKind.Authentication)]
    [InlineData(2003, StorageErrorKind.AccessDenied)]
    [InlineData(2008, StorageErrorKind.QuotaExceeded)]
    [InlineData(2005, StorageErrorKind.NotFound)]
    [InlineData(2009, StorageErrorKind.NotFound)]
    [InlineData(2001, StorageErrorKind.InvalidName)]
    [InlineData(4000, StorageErrorKind.RateLimited)]
    [InlineData(5000, StorageErrorKind.Transient)]
    [InlineData(1004, StorageErrorKind.Unknown)]
    public void Result_codes_are_mapped(int result, StorageErrorKind kind)
    {
        var error = PCloudClient.Map(result, "fake", "a/b.txt");

        Assert.Equal(kind, error.Kind);
        Assert.Contains("a/b.txt", error.Message);
        Assert.Equal(kind == StorageErrorKind.RateLimited, error.RetryAfter is not null);
    }

    [Fact]
    public async Task A_full_account_stops_the_upload()
    {
        var fake = new FakePCloud("anna@example.com", "geheim") { FailUploadsWith = 2008 };
        await using var storage = new PCloudStorage(Context(), fake);
        await storage.ConnectAsync(true, CancellationToken.None);

        var error = await Assert.ThrowsAsync<PCloudException>(() => S3StorageTests.WriteTextAsync(storage, "a.txt", "x", DateTimeOffset.UtcNow));

        Assert.Equal(StorageErrorKind.QuotaExceeded, error.Kind);
    }

    [Fact]
    public void The_provider_is_found()
    {
        Assert.IsType<PCloudProvider>(StorageProviders.Find(ConnectionKind.PCloud));
    }

    private static StorageContext Context(params (string Key, string Value)[] more) =>
        S3StorageTests.Context(ConnectionKind.PCloud, [("user", "Anna@Example.com"), ("base", "/Backup"), .. more]) with { Secret = "geheim", Folder = "Fotos" };
}

/// <summary>
/// A pCloud account in memory: getdigest/userinfo (checks the password digest), listfolder (recursive, nofiles), stat (files),
/// createfolderifnotexists, uploadfile (multipart parsed from the bytes on the wire, mtime), getfilelink + the content server,
/// deletefile, deletefolderrecursive, renamefile, renamefolder, logout. Every call but the sign-in needs the current token.
/// </summary>
internal sealed class FakePCloud(string userName, string password) : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, bool> _digests = new();
    private readonly ConcurrentDictionary<string, bool> _tokens = new();
    private int _ids;

    public sealed record FakeFile(byte[] Data, long Modified, int Id, string PartFileName);

    public ConcurrentDictionary<string, int> Folders { get; } = new(StringComparer.Ordinal) { ["/"] = 0 };

    public ConcurrentDictionary<string, FakeFile> Files { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<string> Log { get; } = new();

    public int SignIns => _signIns;

    public int? FailUploadsWith { get; set; }

    private int _signIns;

    public void ExpireTokens() => _tokens.Clear();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Log.Enqueue(uri.AbsoluteUri);
        if (uri.Host == "c1.pcloud.test")
        {
            var file = Files.Values.Single(item => uri.AbsolutePath == $"/dl/{item.Id}");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file.Data) };
        }

        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split('=', 2))
            .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => part.Length > 1 ? Uri.UnescapeDataString(part[1]) : "");
        var method = uri.AbsolutePath.Trim('/');
        if (method == "getdigest")
        {
            var digest = Guid.NewGuid().ToString("N");
            _digests[digest] = true;
            return Json(new JsonObject { ["result"] = 0, ["digest"] = digest });
        }

        if (method == "userinfo")
        {
            if (query.ContainsKey("password") || !_digests.TryRemove(query["digest"], out _)
                || query["username"].ToLowerInvariant() != userName
                || query["passworddigest"] != PCloudClient.PasswordDigest(userName, password, query["digest"]))
            {
                return Result(2000, "Log in failed.");
            }

            Interlocked.Increment(ref _signIns);
            var token = Guid.NewGuid().ToString("N");
            _tokens[token] = true;
            return Json(new JsonObject { ["result"] = 0, ["auth"] = token, ["email"] = userName });
        }

        if (!query.TryGetValue("auth", out var auth) || !_tokens.ContainsKey(auth))
        {
            return Result(1000, "Log in required.");
        }

        var path = query.GetValueOrDefault("path") ?? "/";
        return method switch
        {
            "listfolder" => ListFolder(path, query.ContainsKey("recursive"), query.ContainsKey("nofiles")),
            "stat" => Files.TryGetValue(path, out var file) ? Json(new JsonObject { ["result"] = 0, ["metadata"] = FileMetadata(path, file) }) : Result(2009, "File not found."),
            "createfolderifnotexists" => CreateFolder(path),
            "uploadfile" => await UploadAsync(request, query, cancellationToken),
            "getfilelink" => Files.TryGetValue(path, out var linked)
                ? Json(new JsonObject { ["result"] = 0, ["hosts"] = new JsonArray("c1.pcloud.test"), ["path"] = $"/dl/{linked.Id}" })
                : Result(2009, "File not found."),
            "deletefile" => Files.TryRemove(path, out _) ? Json(new JsonObject { ["result"] = 0 }) : Result(2009, "File not found."),
            "deletefolderrecursive" => DeleteFolder(path),
            "renamefile" => RenameFile(path, query["topath"]),
            "renamefolder" => RenameFolder(path, query["topath"]),
            "logout" => Json(new JsonObject { ["result"] = 0 }),
            _ => Result(1004, "Unknown method."),
        };
    }

    private HttpResponseMessage ListFolder(string path, bool recursive, bool noFiles)
    {
        if (!Folders.ContainsKey(path))
        {
            return Result(2005, "Directory does not exist.");
        }

        return Json(new JsonObject { ["result"] = 0, ["metadata"] = FolderMetadata(path, recursive, noFiles, top: true) });
    }

    private JsonObject FolderMetadata(string path, bool recursive, bool noFiles, bool top)
    {
        var metadata = new JsonObject { ["name"] = path == "/" ? "/" : path[(path.LastIndexOf('/') + 1)..], ["isfolder"] = true, ["id"] = $"d{Folders[path]}", ["modified"] = 1600000000 };
        if (!top && !recursive)
        {
            return metadata;
        }

        var prefix = path == "/" ? "/" : path + "/";
        var contents = new JsonArray();
        foreach (var folder in Folders.Keys.Where(folder => folder.StartsWith(prefix, StringComparison.Ordinal) && folder.Length > prefix.Length && !folder[prefix.Length..].Contains('/')))
        {
            contents.Add(FolderMetadata(folder, recursive, noFiles, top: false));
        }

        if (!noFiles)
        {
            foreach (var (filePath, file) in Files.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal) && !item.Key[prefix.Length..].Contains('/')))
            {
                contents.Add(FileMetadata(filePath, file));
            }
        }

        metadata["contents"] = contents;
        return metadata;
    }

    private static JsonObject FileMetadata(string path, FakeFile file) => new()
    {
        ["name"] = path[(path.LastIndexOf('/') + 1)..],
        ["isfolder"] = false,
        ["id"] = $"f{file.Id}",
        ["size"] = file.Data.Length,
        ["modified"] = file.Modified,
        ["hash"] = 123456789,
    };

    private HttpResponseMessage CreateFolder(string path)
    {
        var parent = path.LastIndexOf('/') is var slash and > 0 ? path[..slash] : "/";
        if (!Folders.ContainsKey(parent))
        {
            return Result(2002, "A component of parent directory does not exist.");
        }

        Folders.TryAdd(path, Interlocked.Increment(ref _ids));
        return Json(new JsonObject { ["result"] = 0, ["created"] = true });
    }

    private async Task<HttpResponseMessage> UploadAsync(HttpRequestMessage request, Dictionary<string, string> query, CancellationToken cancellationToken)
    {
        if (FailUploadsWith is { } code)
        {
            return Result(code, "Upload refused.");
        }

        var folder = query["path"];
        if (!Folders.ContainsKey(folder))
        {
            return Result(2005, "Directory does not exist.");
        }

        var boundary = request.Content!.Headers.ContentType!.Parameters.Single(parameter => parameter.Name == "boundary").Value!.Trim('"');
        var body = Encoding.UTF8.GetString(await request.Content.ReadAsByteArrayAsync(cancellationToken));
        Assert.Equal(request.Content.Headers.ContentLength, Encoding.UTF8.GetByteCount(body)); // a length, not chunked
        var part = body.Split("--" + boundary)[1];
        var headerEnd = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var headers = part[..headerEnd];
        var data = part[(headerEnd + 4)..^2];
        var partFileName = headers.Split("filename=\"")[1].Split('"')[0];
        var name = query["filename"];
        Assert.Equal(name, partFileName);
        Assert.Equal("1", query["nopartial"]);

        var path = folder == "/" ? "/" + name : $"{folder}/{name}";
        var file = new FakeFile(Encoding.UTF8.GetBytes(data), long.Parse(query["mtime"], CultureInfo.InvariantCulture), Interlocked.Increment(ref _ids), partFileName);
        Files[path] = file;
        return Json(new JsonObject { ["result"] = 0, ["fileids"] = new JsonArray(file.Id), ["metadata"] = new JsonArray(FileMetadata(path, file)) });
    }

    private HttpResponseMessage DeleteFolder(string path)
    {
        if (!Folders.ContainsKey(path))
        {
            return Result(2005, "Directory does not exist.");
        }

        foreach (var folder in Folders.Keys.Where(folder => folder == path || folder.StartsWith(path + "/", StringComparison.Ordinal)))
        {
            Folders.TryRemove(folder, out _);
        }

        foreach (var file in Files.Keys.Where(file => file.StartsWith(path + "/", StringComparison.Ordinal)))
        {
            Files.TryRemove(file, out _);
        }

        return Json(new JsonObject { ["result"] = 0 });
    }

    private HttpResponseMessage RenameFile(string from, string to)
    {
        if (!Files.TryRemove(from, out var file))
        {
            return Result(2009, "File not found.");
        }

        Files[to] = file;
        return Json(new JsonObject { ["result"] = 0 });
    }

    private HttpResponseMessage RenameFolder(string from, string to)
    {
        if (!Folders.ContainsKey(from))
        {
            return Result(2005, "Directory does not exist.");
        }

        foreach (var folder in Folders.Keys.Where(folder => folder == from || folder.StartsWith(from + "/", StringComparison.Ordinal)).ToList())
        {
            Folders.TryRemove(folder, out var id);
            Folders[to + folder[from.Length..]] = id;
        }

        foreach (var file in Files.Keys.Where(file => file.StartsWith(from + "/", StringComparison.Ordinal)).ToList())
        {
            Files.TryRemove(file, out var item);
            Files[to + file[from.Length..]] = item!;
        }

        return Json(new JsonObject { ["result"] = 0 });
    }

    private static HttpResponseMessage Result(int code, string error) => Json(new JsonObject { ["result"] = code, ["error"] = error });

    private static HttpResponseMessage Json(JsonObject json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), Encoding.UTF8, "application/json") };
}
