using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;
using ThixSync.Core.Storage.Providers.GoogleDrive;

namespace ThixSync.Tests;

/// <summary>Google Drive against a fake Drive API v3: the tree from a flat listing, uploads small and resumable, errors.</summary>
public sealed class GoogleDriveTests
{
    private const string Folder = "application/vnd.google-apps.folder";

    [Fact]
    public async Task The_tree_is_built_from_one_flat_listing_of_the_drive()
    {
        using var test = new CloudTest();
        test.Http.On(HttpMethod.Get, "/drive/v3/files/root?", """{"id":"ROOT"}""");
        test.Http.On(HttpMethod.Get, "name = 'Backup'", $$"""{"files":[{"id":"B","name":"Backup","mimeType":"{{Folder}}","parents":["ROOT"]}]}""");
        test.Http.On(HttpMethod.Get, "pageToken=p2", $$"""
            {"files":[
              {"id":"Y","name":"2024","mimeType":"{{Folder}}","parents":["F"],"modifiedTime":"2026-01-01T00:00:00Z"},
              {"id":"b","name":"b.jpg","mimeType":"image/jpeg","parents":["Y"],"size":"5","modifiedTime":"2026-02-01T00:00:00Z","md5Checksum":"bbb"},
              {"id":"d1","name":"dup.txt","mimeType":"text/plain","parents":["B"],"size":"1","modifiedTime":"2026-03-01T00:00:00Z"},
              {"id":"d2","name":"dup.txt","mimeType":"text/plain","parents":["B"],"size":"2","modifiedTime":"2026-04-01T00:00:00Z"}
            ]}
            """);
        test.Http.On(HttpMethod.Get, "files?q=trashed = false", $$"""
            {"nextPageToken":"p2","files":[
              {"id":"F","name":"Fotos","mimeType":"{{Folder}}","parents":["B"],"modifiedTime":"2026-01-01T00:00:00Z"},
              {"id":"a","name":"a.jpg","mimeType":"image/jpeg","parents":["F"],"size":"1234","modifiedTime":"2026-05-06T07:08:09.123Z","md5Checksum":"aaa"},
              {"id":"n","name":"Notizen","mimeType":"application/vnd.google-apps.document","parents":["B"],"modifiedTime":"2026-01-01T00:00:00Z"},
              {"id":"s","name":"Tabelle","mimeType":"application/vnd.google-apps.spreadsheet","parents":["F"],"modifiedTime":"2026-01-01T00:00:00Z"},
              {"id":"o","name":"other.txt","mimeType":"text/plain","parents":["ROOT"],"size":"9","modifiedTime":"2026-01-01T00:00:00Z"}
            ]}
            """);
        var storage = new GoogleDriveProvider().Open(test.Context(ConnectionKind.GoogleDrive, settings: new() { [ConnectionFields.BasePath] = "Backup" }));
        await storage.ConnectAsync(false, CancellationToken.None);
        var problems = new List<string>();

        var entries = await CloudTest.ListAllAsync(storage, problems);

        Assert.Equal(["Fotos", "Fotos/2024", "Fotos/2024/b.jpg", "Fotos/a.jpg", "dup.txt"], entries.Keys.Order(StringComparer.Ordinal));
        var photo = entries["Fotos/a.jpg"];
        Assert.Equal(1234, photo.Size);
        Assert.Equal("md5:aaa", photo.Hash);
        Assert.Equal("a", photo.NativeId);
        Assert.Equal(new DateTimeOffset(2026, 5, 6, 7, 8, 9, 123, TimeSpan.Zero), photo.Modified);
        Assert.True(entries["Fotos/2024"].IsFolder);
        Assert.Equal("d2", entries["dup.txt"].NativeId); // the newest of the two
        Assert.Single(problems, problem => problem.StartsWith("dup.txt:", StringComparison.Ordinal));
        Assert.Single(problems, problem => problem.Contains("2 Online-Dokument", StringComparison.Ordinal));
        var listings = test.Http.Requests.Where(request => request.Url.Contains("files?q=trashed = false", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, listings.Count);
        Assert.All(listings, request => Assert.Contains("pageSize=1000", request.Url, StringComparison.Ordinal));
        Assert.Contains("md5Checksum", listings[0].Url, StringComparison.Ordinal);

        // The listing filled the cache: reading a listed file needs no lookup.
        test.Http.On(HttpMethod.Get, "/files/a?alt=media", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("photo") });
        var before = test.Http.Requests.Count;
        await using (var stream = await storage.OpenReadAsync("Fotos/a.jpg", CancellationToken.None))
        {
            Assert.Equal("photo", await new StreamReader(stream).ReadToEndAsync());
        }

        Assert.Equal(before + 1, test.Http.Requests.Count);
    }

    [Fact]
    public async Task A_shared_drive_is_found_by_name_and_listed_as_its_own_corpus()
    {
        using var test = new CloudTest();
        test.Http.On(HttpMethod.Get, "/drive/v3/drives?", """{"drives":[{"id":"0AAA","name":"Andere"},{"id":"0SHARED","name":"Team"}]}""");
        test.Http.On(HttpMethod.Get, "files?q=trashed = false", $$"""{"files":[{"id":"x","name":"x.txt","mimeType":"text/plain","parents":["0SHARED"],"size":"3"}]}""");
        var storage = new GoogleDriveProvider().Open(test.Context(ConnectionKind.GoogleDrive, settings: new() { [ConnectionFields.SharedDrive] = "team" }));
        await storage.ConnectAsync(false, CancellationToken.None);

        var entries = await CloudTest.ListAllAsync(storage);

        Assert.Equal(["x.txt"], entries.Keys);
        var listing = test.Http.Requests.Last(request => request.Url.Contains("files?q=", StringComparison.Ordinal));
        Assert.Contains("corpora=drive&driveId=0SHARED&includeItemsFromAllDrives=true&supportsAllDrives=true", listing.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Small_files_go_in_one_multipart_request_with_their_time()
    {
        using var test = new CloudTest();
        test.Http.On(HttpMethod.Get, "/drive/v3/files/root?", """{"id":"ROOT"}""");
        test.Http.On(HttpMethod.Get, "name = 'small.txt'", """{"files":[]}""");
        test.Http.On(HttpMethod.Post, "/upload/drive/v3/files?uploadType=multipart",
            """{"id":"S","name":"small.txt","mimeType":"text/plain","parents":["ROOT"],"size":"5","modifiedTime":"2026-11-20T10:00:00.000Z","md5Checksum":"5d41"}""");
        var storage = new GoogleDriveProvider().Open(test.Context(ConnectionKind.GoogleDrive));
        var progress = new CountingProgress();

        var entry = await storage.WriteAsync("small.txt", new MemoryStream(Encoding.UTF8.GetBytes("hello")), 5, new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero),
            progress, CancellationToken.None);

        Assert.Equal(new StorageEntry("small.txt", false, 5, new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero)) { Hash = "md5:5d41", NativeId = "S" }, entry);
        Assert.Equal(5, progress.Total);
        var upload = test.Http.Requests.Single(request => request.Url.Contains("uploadType=multipart", StringComparison.Ordinal));
        Assert.StartsWith("multipart/related", upload.Header("Content-Type"), StringComparison.Ordinal);
        Assert.Contains("\"modifiedTime\":\"2026-11-20T10:00:00.000Z\"", upload.Text, StringComparison.Ordinal);
        Assert.Contains("\"parents\":[\"ROOT\"]", upload.Text, StringComparison.Ordinal);
        Assert.Contains("hello", upload.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Large_files_go_in_resumable_chunks_and_a_failed_chunk_resumes_where_drive_stopped()
    {
        using var test = new CloudTest();
        GoogleDriveStorage.SimpleUploadLimit = 1024;
        GoogleDriveStorage.ChunkSize = 256 * 1024;
        var content = CloudTest.Bytes(600 * 1024);
        var received = new MemoryStream();
        var failedOnce = false;
        test.Http.On(HttpMethod.Get, "/drive/v3/files/root?", """{"id":"ROOT"}""");
        test.Http.On(HttpMethod.Get, "name = 'Ordner'", $$"""{"files":[{"id":"O","name":"Ordner","mimeType":"{{Folder}}","parents":["ROOT"]}]}""");
        test.Http.On(HttpMethod.Get, "name = 'big.bin'", """{"files":[{"id":"OLD","name":"big.bin","mimeType":"application/octet-stream","parents":["O"],"size":"3"}]}""");
        test.Http.On(HttpMethod.Patch, "/upload/drive/v3/files/OLD?uploadType=resumable", _ =>
        {
            var response = FakeHttp.Status(HttpStatusCode.OK);
            response.Headers.Location = new Uri("https://www.googleapis.com/upload/drive/v3/files/OLD?upload_id=session-1");
            return response;
        });
        test.Http.On(HttpMethod.Put, "upload_id=session-1", request =>
        {
            var range = request.Header("Content-Range")!;
            if (range.StartsWith("bytes */", StringComparison.Ordinal))
            {
                return Incomplete(received.Length);
            }

            var start = long.Parse(range["bytes ".Length..range.IndexOf('-')], System.Globalization.CultureInfo.InvariantCulture);
            if (start == 256 * 1024 && !failedOnce)
            {
                failedOnce = true;
                received.Write(request.Body, 0, 1000); // a part arrived before the connection broke
                return FakeHttp.Status(HttpStatusCode.ServiceUnavailable);
            }

            Assert.Equal(received.Length, start);
            received.Write(request.Body);
            return received.Length < content.Length
                ? Incomplete(received.Length)
                : FakeHttp.Json($$"""{"id":"OLD","name":"big.bin","size":"{{content.Length}}","modifiedTime":"2026-11-20T10:00:00.000Z","md5Checksum":"ff"}""");
        });
        var storage = new GoogleDriveProvider().Open(test.Context(ConnectionKind.GoogleDrive));
        var progress = new CountingProgress();

        var entry = await storage.WriteAsync("Ordner/big.bin", new MemoryStream(content), content.Length, DateTimeOffset.UnixEpoch, progress, CancellationToken.None);

        Assert.Equal(content, received.ToArray());
        Assert.Equal(content.Length, progress.Total);
        Assert.Equal("OLD", entry.NativeId); // a new version of the same file
        Assert.Equal("md5:ff", entry.Hash);
        var start = test.Http.Requests.Single(request => request.Url.Contains("uploadType=resumable", StringComparison.Ordinal));
        Assert.Equal(content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), start.Header("X-Upload-Content-Length"));
        Assert.Contains("\"modifiedTime\":\"1970-01-01T00:00:00.000Z\"", start.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("parents", start.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_folders_are_made_once_even_when_many_workers_need_them()
    {
        using var test = new CloudTest();
        var made = 0;
        test.Http.On(HttpMethod.Get, "/drive/v3/files/root?", """{"id":"ROOT"}""");
        test.Http.On(HttpMethod.Get, "files?q=", """{"files":[]}""");
        test.Http.On(request => request.Method == HttpMethod.Post && request.Url.StartsWith("https://www.googleapis.com/drive/v3/files?", StringComparison.Ordinal), request =>
        {
            Interlocked.Increment(ref made);
            return FakeHttp.Json(new JsonObject { ["id"] = "folder-" + request.Json!["name"]!.GetValue<string>() });
        });
        test.Http.On(HttpMethod.Post, "uploadType=multipart", request => FakeHttp.Json("""{"id":"f","name":"x","mimeType":"text/plain","size":"1"}"""));
        var storage = new GoogleDriveProvider().Open(test.Context(ConnectionKind.GoogleDrive));

        await Task.WhenAll(Enumerable.Range(0, 10).Select(index =>
            storage.WriteAsync($"Neu/Tief/{index}.txt", new MemoryStream([1]), 1, DateTimeOffset.UnixEpoch, null, CancellationToken.None)));

        Assert.Equal(2, made);
        Assert.All(test.Http.Requests.Where(request => request.Url.Contains("uploadType=multipart", StringComparison.Ordinal)),
            request => Assert.Contains("\"parents\":[\"folder-Tief\"]", request.Text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deleting_moves_to_the_trash()
    {
        using var test = new CloudTest();
        test.Http.On(HttpMethod.Get, "/drive/v3/files/root?", """{"id":"ROOT"}""");
        test.Http.On(HttpMethod.Get, "name = 'alt.txt'", """{"files":[{"id":"A","name":"alt.txt","mimeType":"text/plain","parents":["ROOT"]}]}""");
        test.Http.On(HttpMethod.Patch, "/drive/v3/files/A?", """{"id":"A"}""");
        var storage = new GoogleDriveProvider().Open(test.Context(ConnectionKind.GoogleDrive));

        await storage.DeleteAsync("alt.txt", false, CancellationToken.None);

        Assert.Equal("""{"trashed":true}""", test.Http.Requests.Single(request => request.Method == HttpMethod.Patch).Text);
    }

    [Fact]
    public void Drive_errors_are_mapped_by_their_reason()
    {
        CloudErrorOf("""{"error":{"errors":[{"reason":"userRateLimitExceeded"}],"code":403}}""", StorageErrorKind.RateLimited);
        CloudErrorOf("""{"error":{"errors":[{"reason":"storageQuotaExceeded"}],"code":403}}""", StorageErrorKind.QuotaExceeded);
        CloudErrorOf("""{"error":{"errors":[{"reason":"insufficientFilePermissions"}],"code":403}}""", StorageErrorKind.AccessDenied);
        CloudErrorOf("""{"error":{"errors":[{"reason":"somethingElse"}],"code":403}}""", null);

        static void CloudErrorOf(string json, StorageErrorKind? expected) =>
            Assert.Equal(expected, GoogleDriveStorage.Describe(HttpStatusCode.Forbidden, JsonNode.Parse(json)).Kind);
    }

    private static HttpResponseMessage Incomplete(long received)
    {
        var response = new HttpResponseMessage((HttpStatusCode)308) { Content = new ByteArrayContent([]) };
        if (received > 0)
        {
            response.Headers.TryAddWithoutValidation("Range", $"bytes=0-{received - 1}");
        }

        return response;
    }
}
