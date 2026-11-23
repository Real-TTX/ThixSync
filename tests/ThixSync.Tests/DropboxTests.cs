using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;
using ThixSync.Core.Storage.Providers.Dropbox;
using ThixSync.Core.Storage.Providers.OAuth;

namespace ThixSync.Tests;

/// <summary>Dropbox against a fake API v2: list_folder/continue, the escaped Dropbox-API-Arg header, uploads, errors, the cursor.</summary>
public sealed class DropboxTests
{
    [Fact]
    public async Task The_tree_comes_from_one_recursive_listing_with_continue()
    {
        using var test = new CloudTest();
        test.Http.On(HttpMethod.Post, "/2/files/list_folder/continue", request =>
        {
            Assert.Equal("c1", request.Json!["cursor"]!.GetValue<string>());
            return FakeHttp.Json("""
                {"entries":[
                  {".tag":"deleted","name":"weg.txt","path_display":"/Backup/weg.txt"},
                  {".tag":"file","name":"B.txt","path_display":"/backup/B.txt","id":"id:b","size":7,"client_modified":"2026-03-04T05:06:07Z","content_hash":"bb"}
                ],"cursor":"c2","has_more":false}
                """);
        });
        test.Http.On(HttpMethod.Post, "/2/files/list_folder", """
            {"entries":[
              {".tag":"folder","name":"Backup","path_display":"/Backup","id":"id:root"},
              {".tag":"folder","name":"Fotos","path_display":"/Backup/Fotos","id":"id:f"},
              {".tag":"file","name":"a.jpg","path_display":"/Backup/Fotos/a.jpg","id":"id:a","size":1234,"client_modified":"2026-01-02T03:04:05Z",
               "server_modified":"2026-11-01T00:00:00Z","content_hash":"aa"}
            ],"cursor":"c1","has_more":true}
            """);
        var storage = new DropboxProvider().Open(test.Context(ConnectionKind.Dropbox, settings: new() { [ConnectionFields.BasePath] = "/Backup/" }));

        var entries = await CloudTest.ListAllAsync(storage);

        Assert.Equal(["B.txt", "Fotos", "Fotos/a.jpg"], entries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new StorageEntry("Fotos/a.jpg", false, 1234, new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)) { Hash = "dropbox:aa", NativeId = "id:a" },
            entries["Fotos/a.jpg"]);
        var first = test.Http.Requests.Single(request => request.Url.EndsWith("/2/files/list_folder", StringComparison.Ordinal)).Json!;
        Assert.Equal("/Backup", first["path"]!.GetValue<string>());
        Assert.True(first["recursive"]!.GetValue<bool>());
        Assert.False(storage.Capabilities.Has(StorageFeatures.CaseSensitive));
    }

    [Fact]
    public async Task The_api_arg_header_carries_non_ascii_names_escaped()
    {
        using var test = new CloudTest();
        test.Http.On(HttpMethod.Post, "content.dropboxapi.com/2/files/download", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("inhalt") });
        var storage = new DropboxProvider().Open(test.Context(ConnectionKind.Dropbox, "Fotos"));

        await using (var stream = await storage.OpenReadAsync("Müller ☃ 😀.txt", CancellationToken.None))
        {
            Assert.Equal("inhalt", await new StreamReader(stream).ReadToEndAsync());
        }

        var header = test.Http.Requests.Single(request => request.Url.Contains("/files/download", StringComparison.Ordinal)).Header("Dropbox-API-Arg")!;
        Assert.All(header, character => Assert.InRange(character, ' ', '~'));
        Assert.Equal("/Fotos/Müller ☃ 😀.txt", JsonNode.Parse(header)!["path"]!.GetValue<string>());
        var backslash = (char)92;
        var umlautAndEmoji = (char)0xE4 + char.ConvertFromUtf32(0x1F600);
        Assert.Equal($"{{\"path\":\"{backslash}u00e4{backslash}ud83d{backslash}ude00\"}}", CloudJson.HeaderSafe($"{{\"path\":\"{umlautAndEmoji}\"}}"));
    }

    [Fact]
    public async Task Small_files_go_in_one_upload_large_ones_in_an_upload_session()
    {
        using var test = new CloudTest();
        DropboxStorage.SimpleUploadLimit = 1024;
        DropboxStorage.ChunkSize = 4096;
        var content = CloudTest.Bytes(10_000);
        var received = new MemoryStream();
        var appends = 0;
        test.Http.On(HttpMethod.Post, "/2/files/upload_session/start", """{"session_id":"S1"}""");
        test.Http.On(HttpMethod.Post, "/2/files/upload_session/append_v2", request =>
        {
            if (Interlocked.Increment(ref appends) == 2)
            {
                return FakeHttp.Status(HttpStatusCode.InternalServerError); // sent again by itself
            }

            var argument = JsonNode.Parse(request.Header("Dropbox-API-Arg")!)!;
            Assert.Equal(received.Length, argument["cursor"]!["offset"]!.GetValue<long>());
            received.Write(request.Body);
            Assert.Equal(received.Length == content.Length, argument["close"]!.GetValue<bool>());
            return FakeHttp.Json("null");
        });
        test.Http.On(HttpMethod.Post, "/2/files/upload_session/finish",
            """{"name":"gross.bin","path_display":"/gross.bin","id":"id:g","size":10000,"client_modified":"2026-11-20T10:00:00Z","content_hash":"gg"}""");
        test.Http.On(HttpMethod.Post, "/2/files/upload",
            """{"name":"klein.txt","path_display":"/klein.txt","id":"id:k","size":5,"client_modified":"2026-11-20T10:00:00Z","content_hash":"kk"}""");
        var storage = new DropboxProvider().Open(test.Context(ConnectionKind.Dropbox));
        var time = new DateTimeOffset(2026, 11, 20, 10, 0, 0, 500, TimeSpan.Zero);

        var small = await storage.WriteAsync("klein.txt", new MemoryStream(Encoding.UTF8.GetBytes("hallo")), 5, time, null, CancellationToken.None);
        var progress = new CountingProgress();
        var large = await storage.WriteAsync("gross.bin", new MemoryStream(content), content.Length, time, progress, CancellationToken.None);

        Assert.Equal(new StorageEntry("klein.txt", false, 5, time.AddMilliseconds(-500)) { Hash = "dropbox:kk", NativeId = "id:k" }, small);
        var upload = JsonNode.Parse(test.Http.Requests.Single(request => request.Url.EndsWith("/2/files/upload", StringComparison.Ordinal)).Header("Dropbox-API-Arg")!)!;
        Assert.Equal("overwrite", upload["mode"]!.GetValue<string>());
        Assert.Equal("2026-11-20T10:00:00Z", upload["client_modified"]!.GetValue<string>());
        Assert.False(upload["autorename"]!.GetValue<bool>());
        Assert.False(upload["strict_conflict"]!.GetValue<bool>());

        Assert.Equal("dropbox:gg", large.Hash);
        Assert.Equal(content, received.ToArray());
        Assert.Equal(content.Length, progress.Total);
        Assert.Equal(4, appends); // three chunks, one of them twice
        var finish = JsonNode.Parse(test.Http.Requests.Single(request => request.Url.Contains("finish", StringComparison.Ordinal)).Header("Dropbox-API-Arg")!)!;
        Assert.Equal(10_000, finish["cursor"]!["offset"]!.GetValue<long>());
        Assert.Equal("/gross.bin", finish["commit"]!["path"]!.GetValue<string>());
        Assert.Equal("2026-11-20T10:00:00Z", finish["commit"]!["client_modified"]!.GetValue<string>());
    }

    [Fact]
    public async Task Errors_become_storage_errors_and_a_missing_file_is_nothing()
    {
        using var test = new CloudTest();
        test.Http.On(request => request.Url.EndsWith("/2/files/get_metadata", StringComparison.Ordinal) && request.Text.Contains("fehlt", StringComparison.Ordinal),
            _ => FakeHttp.Json("""{"error_summary":"path/not_found/..","error":{".tag":"path"}}""", HttpStatusCode.Conflict));
        test.Http.On(HttpMethod.Post, "/2/files/get_metadata", _ =>
            FakeHttp.Json("""{"error_summary":"too_many_write_operations/..","error":{".tag":"too_many_write_operations"}}""", HttpStatusCode.TooManyRequests).WithRetryAfter(300));
        test.Http.On(HttpMethod.Post, "/2/files/delete_v2", _ => FakeHttp.Json("""{"error_summary":"path_lookup/not_found/.."}""", HttpStatusCode.Conflict));
        test.Http.On(HttpMethod.Post, "/2/files/create_folder_v2", _ => FakeHttp.Json("""{"error_summary":"path/conflict/folder/.."}""", HttpStatusCode.Conflict));
        var storage = new DropboxProvider().Open(test.Context(ConnectionKind.Dropbox));

        var missing = await storage.GetAsync("fehlt.txt", CancellationToken.None);
        var limited = await Assert.ThrowsAsync<StorageException>(() => storage.GetAsync("da.txt", CancellationToken.None));
        await storage.DeleteAsync("weg.txt", false, CancellationToken.None);
        await storage.CreateFolderAsync("gibt-es-schon", CancellationToken.None);

        Assert.Null(missing);
        Assert.Equal(StorageErrorKind.RateLimited, limited.Kind);
        Assert.Equal(TimeSpan.FromSeconds(300), limited.RetryAfter);
        Assert.Equal(StorageErrorKind.QuotaExceeded, DropboxStorage.Describe(HttpStatusCode.Conflict, JsonNode.Parse("""{"error_summary":"path/insufficient_space/.."}""")).Kind);
        Assert.Equal(StorageErrorKind.InvalidName, DropboxStorage.Describe(HttpStatusCode.Conflict, JsonNode.Parse("""{"error_summary":"path/disallowed_name/"}""")).Kind);
    }

    [Fact]
    public async Task The_cursor_tells_whether_anything_changed()
    {
        using var test = new CloudTest();
        test.Http.On(HttpMethod.Post, "/2/files/list_folder/get_latest_cursor", """{"cursor":"c9"}""");
        test.Http.On(request => request.Url.EndsWith("/list_folder/continue", StringComparison.Ordinal) && request.Text.Contains("c9", StringComparison.Ordinal),
            _ => FakeHttp.Json("""{"entries":[],"cursor":"c9","has_more":false}"""));
        test.Http.On(request => request.Url.EndsWith("/list_folder/continue", StringComparison.Ordinal) && request.Text.Contains("old", StringComparison.Ordinal),
            _ => FakeHttp.Json("""{"error_summary":"reset/..","error":{".tag":"reset"}}""", HttpStatusCode.Conflict));
        test.Http.On(HttpMethod.Post, "/list_folder/continue", """{"entries":[{".tag":"file","name":"n.txt","path_display":"/n.txt"}],"cursor":"c10","has_more":false}""");
        var feed = (IChangeFeed)new DropboxProvider().Open(test.Context(ConnectionKind.Dropbox));

        Assert.Equal("c9", await feed.GetCursorAsync(CancellationToken.None));
        Assert.False(await feed.HasChangesAsync("c9", CancellationToken.None));
        Assert.True(await feed.HasChangesAsync("c8", CancellationToken.None));
        Assert.True(await feed.HasChangesAsync("old", CancellationToken.None));
    }
}
