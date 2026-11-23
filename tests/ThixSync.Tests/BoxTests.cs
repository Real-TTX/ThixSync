using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;
using ThixSync.Core.Storage.Providers.Box;

namespace ThixSync.Tests;

/// <summary>Box against a fake REST API: the folder walk with marker paging, uploads (multipart and chunked with digests), errors.</summary>
public sealed class BoxTests
{
    [Fact]
    public async Task The_folders_are_walked_with_marker_paging()
    {
        using var test = new CloudTest();
        test.Http.On(HttpMethod.Get, "/folders/0/items?fields=id,type,name,size,modified_at,content_modified_at,sha1&limit=1000&usemarker=true&marker=m2", """
            {"entries":[
              {"type":"file","id":"22","name":"b.txt","size":2,"modified_at":"2026-05-05T00:00:00Z"},
              {"type":"web_link","id":"30","name":"Verknüpfung"}
            ],"next_marker":""}
            """);
        test.Http.On(HttpMethod.Get, "/folders/0/items?", """
            {"entries":[
              {"type":"folder","id":"11","name":"Fotos"},
              {"type":"file","id":"21","name":"a.txt","size":3,"content_modified_at":"2026-01-02T03:04:05-08:00","modified_at":"2026-11-01T00:00:00Z","sha1":"aa"}
            ],"next_marker":"m2"}
            """);
        test.Http.On(HttpMethod.Get, "/folders/11/items?", """{"entries":[{"type":"file","id":"23","name":"c.jpg","size":9,"sha1":"cc"}]}""");
        var storage = new BoxProvider().Open(test.Context(ConnectionKind.Box));

        var entries = await CloudTest.ListAllAsync(storage);

        Assert.Equal(["Fotos", "Fotos/c.jpg", "a.txt", "b.txt"], entries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new StorageEntry("a.txt", false, 3, new DateTimeOffset(2026, 1, 2, 11, 4, 5, TimeSpan.Zero)) { Hash = "sha1:aa", NativeId = "21" }, entries["a.txt"]);
        Assert.Equal(new DateTimeOffset(2026, 5, 5, 0, 0, 0, TimeSpan.Zero), entries["b.txt"].Modified);

        // The walk filled the cache: reading a listed file asks Box nothing else.
        test.Http.On(HttpMethod.Get, "/files/23/content", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("bild") });
        var before = test.Http.Requests.Count;
        await using (var stream = await storage.OpenReadAsync("fotos/C.JPG", CancellationToken.None))
        {
            Assert.Equal("bild", await new StreamReader(stream).ReadToEndAsync());
        }

        Assert.Equal(before + 1, test.Http.Requests.Count);
    }

    [Fact]
    public async Task Small_files_go_in_one_multipart_request_large_ones_in_parts_with_digests()
    {
        using var test = new CloudTest();
        BoxStorage.SessionUploadLimit = 1024;
        var content = CloudTest.Bytes(2500);
        var received = new MemoryStream();
        var commits = 0;
        test.Http.On(HttpMethod.Get, "/folders/0/items?", """{"entries":[]}""");
        test.Http.On(HttpMethod.Post, "upload.box.com/api/2.0/files/content", """
            {"total_count":1,"entries":[{"type":"file","id":"41","name":"klein.txt","size":5,"sha1":"k1","content_modified_at":"2026-11-20T10:00:00Z"}]}
            """);
        test.Http.On(HttpMethod.Post, "/files/upload_sessions/S1/commit", request =>
        {
            if (Interlocked.Increment(ref commits) == 1)
            {
                return FakeHttp.Status(HttpStatusCode.Accepted).WithRetryAfter(0); // still putting the parts together
            }

            Assert.Equal($"sha={Convert.ToBase64String(SHA1.HashData(content))}", request.Header("Digest"));
            Assert.Equal(3, request.Json!["parts"]!.AsArray().Count);
            Assert.Equal("2026-11-20T10:00:00Z", request.Json!["attributes"]!["content_modified_at"]!.GetValue<string>());
            return FakeHttp.Json("""{"entries":[{"type":"file","id":"42","name":"gross.bin","size":2500,"sha1":"g1","content_modified_at":"2026-11-20T10:00:00Z"}]}""",
                HttpStatusCode.Created);
        });
        test.Http.On(HttpMethod.Post, "upload.box.com/api/2.0/files/upload_sessions", """{"id":"S1","part_size":1024,"total_parts":3}""");
        test.Http.On(HttpMethod.Put, "/files/upload_sessions/S1", request =>
        {
            Assert.Equal($"sha={Convert.ToBase64String(SHA1.HashData(request.Body))}", request.Header("Digest"));
            Assert.Equal($"bytes {received.Length}-{received.Length + request.Body.Length - 1}/2500", request.Header("Content-Range"));
            var part = new JsonObject { ["part_id"] = $"p{received.Length}", ["offset"] = received.Length, ["size"] = request.Body.Length };
            received.Write(request.Body);
            return FakeHttp.Json(new JsonObject { ["part"] = part });
        });
        var storage = new BoxProvider().Open(test.Context(ConnectionKind.Box));
        var time = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);

        var small = await storage.WriteAsync("klein.txt", new MemoryStream(Encoding.UTF8.GetBytes("hallo")), 5, time, null, CancellationToken.None);
        var progress = new CountingProgress();
        var large = await storage.WriteAsync("gross.bin", new MemoryStream(content), content.Length, time, progress, CancellationToken.None);

        Assert.Equal(new StorageEntry("klein.txt", false, 5, time) { Hash = "sha1:k1", NativeId = "41" }, small);
        var form = test.Http.Requests.Single(request => request.Url.Contains("/files/content", StringComparison.Ordinal)).Text;
        Assert.Contains("name=attributes", form, StringComparison.Ordinal);
        Assert.Contains("\"parent\":{\"id\":\"0\"}", form, StringComparison.Ordinal);
        Assert.Contains("\"content_modified_at\":\"2026-11-20T10:00:00Z\"", form, StringComparison.Ordinal);
        Assert.True(form.IndexOf("name=attributes", StringComparison.Ordinal) < form.IndexOf("hallo", StringComparison.Ordinal)); // attributes first
        Assert.Equal(new StorageEntry("gross.bin", false, 2500, time) { Hash = "sha1:g1", NativeId = "42" }, large);
        Assert.Equal(content, received.ToArray());
        Assert.Equal(content.Length, progress.Total);
        Assert.Equal(2, commits);
        var session = test.Http.Requests.Single(request => request.Url.EndsWith("/files/upload_sessions", StringComparison.Ordinal)).Json!;
        Assert.Equal("0", session["folder_id"]!.GetValue<string>());
        Assert.Equal(2500, session["file_size"]!.GetValue<long>());
    }

    [Fact]
    public async Task A_changed_file_gets_a_new_version_and_folders_are_made_once()
    {
        using var test = new CloudTest();
        var made = 0;
        test.Http.On(HttpMethod.Get, "/folders/0/items?", """{"entries":[{"type":"file","id":"50","name":"alt.txt","size":1}]}""");
        test.Http.On(HttpMethod.Post, "upload.box.com/api/2.0/files/50/content", """{"entries":[{"type":"file","id":"50","name":"alt.txt","size":3,"sha1":"v2"}]}""");
        test.Http.On(HttpMethod.Post, "api.box.com/2.0/folders?", request =>
        {
            Interlocked.Increment(ref made);
            return FakeHttp.Json(new JsonObject { ["id"] = "F-" + request.Json!["name"]!.GetValue<string>() }, HttpStatusCode.Created);
        });
        test.Http.On(HttpMethod.Post, "upload.box.com/api/2.0/files/content", """{"entries":[{"type":"file","id":"60","name":"x","size":1}]}""");
        var storage = new BoxProvider().Open(test.Context(ConnectionKind.Box));

        var version = await storage.WriteAsync("ALT.txt", new MemoryStream([1, 2, 3]), 3, DateTimeOffset.UnixEpoch, null, CancellationToken.None);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            storage.WriteAsync($"Neu/{index}.txt", new MemoryStream([1]), 1, DateTimeOffset.UnixEpoch, null, CancellationToken.None)));

        Assert.Equal("50", version.NativeId);
        Assert.DoesNotContain("parent", test.Http.Requests.Single(request => request.Url.Contains("/files/50/content", StringComparison.Ordinal)).Text, StringComparison.Ordinal);
        Assert.Equal(1, made);
        Assert.DoesNotContain(test.Http.Requests, request => request.Url.Contains("/folders/F-Neu/items", StringComparison.Ordinal)); // a new folder is empty
    }

    [Fact]
    public async Task Box_errors_become_storage_errors()
    {
        using var test = new CloudTest();
        test.Http.On(HttpMethod.Get, "/folders/0/items?", _ =>
            FakeHttp.Json("""{"type":"error","status":429,"code":"rate_limit_exceeded","message":"Request rate limit exceeded"}""", HttpStatusCode.TooManyRequests).WithRetryAfter(60));
        var storage = new BoxProvider().Open(test.Context(ConnectionKind.Box));

        var error = await Assert.ThrowsAsync<StorageException>(() => storage.GetAsync("a.txt", CancellationToken.None));

        Assert.Equal(StorageErrorKind.RateLimited, error.Kind);
        Assert.Equal(TimeSpan.FromSeconds(60), error.RetryAfter);
        Assert.Contains("Box", error.Message, StringComparison.Ordinal);
        Assert.Equal(StorageErrorKind.QuotaExceeded, BoxStorage.Describe(HttpStatusCode.InsufficientStorage, JsonNode.Parse("""{"code":"storage_limit_exceeded"}""")).Kind);
        Assert.Equal(StorageErrorKind.AccessDenied, BoxStorage.Describe(HttpStatusCode.Forbidden, JsonNode.Parse("""{"code":"access_denied_insufficient_permissions"}""")).Kind);
    }
}
