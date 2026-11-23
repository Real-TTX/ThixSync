using System.Net;
using System.Text;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;
using ThixSync.Core.Storage.Providers.OneDrive;

namespace ThixSync.Tests;

/// <summary>OneDrive and SharePoint against a fake Microsoft Graph: delta paging, uploads, the CDN redirect, throttling.</summary>
public sealed class OneDriveTests
{
    private const string Graph = "https://graph.microsoft.com/v1.0";

    [Fact]
    public async Task The_tree_comes_from_one_delta_pass_following_the_next_links()
    {
        using var test = new CloudTest();
        PersonalDrive(test);
        test.Http.On(HttpMethod.Get, "/items/TOP/delta?token=page2", """
            {"value":[
              {"id":"b","name":"b.txt","size":3,"file":{"hashes":{"quickXorHash":"QQ=="}},"parentReference":{"id":"TOP"},"fileSystemInfo":{"lastModifiedDateTime":"2026-02-03T04:05:06Z"}},
              {"id":"nb","name":"Notizbuch","package":{"type":"oneNote"},"parentReference":{"id":"TOP"}}
            ],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/drives/D/items/TOP/delta?token=done"}
            """);
        test.Http.On(HttpMethod.Get, "/items/TOP/delta?", """
            {"value":[
              {"id":"TOP","name":"root","root":{},"folder":{}},
              {"id":"F","name":"Fotos","folder":{"childCount":1},"parentReference":{"id":"TOP"}},
              {"id":"a","name":"a.jpg","size":1234,"file":{"hashes":{"quickXorHash":"AAAA"}},"parentReference":{"id":"F"},
               "fileSystemInfo":{"lastModifiedDateTime":"2026-01-02T03:04:05Z"},"lastModifiedDateTime":"2026-11-01T00:00:00Z"},
              {"id":"gone","name":"weg.txt","deleted":{"state":"deleted"},"parentReference":{"id":"TOP"}}
            ],"@odata.nextLink":"https://graph.microsoft.com/v1.0/drives/D/items/TOP/delta?token=page2"}
            """);
        var storage = new OneDriveProvider().Open(test.Context(ConnectionKind.OneDrive));
        var problems = new List<string>();

        var entries = await CloudTest.ListAllAsync(storage, problems);

        Assert.Equal(["Fotos", "Fotos/a.jpg", "b.txt"], entries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new StorageEntry("Fotos/a.jpg", false, 1234, new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)) { Hash = "quickxor:AAAA", NativeId = "a" },
            entries["Fotos/a.jpg"]);
        Assert.Single(problems); // the OneNote notebook
        Assert.Equal(2, test.Http.Requests.Count(request => request.Url.Contains("/delta", StringComparison.Ordinal)));
        Assert.Equal(StorageFeatures.SetModifiedTime | StorageFeatures.Move | StorageFeatures.RealFolders | StorageFeatures.ChangeFeed | StorageFeatures.CoarseTime,
            storage.Capabilities.Features);
    }

    [Fact]
    public async Task A_business_drive_below_its_top_is_walked_folder_by_folder()
    {
        using var test = new CloudTest();
        test.Http.On(HttpMethod.Get, "/me/drive?", """{"id":"D","driveType":"business"}""");
        test.Http.On(HttpMethod.Get, "/drives/D/root?", """{"id":"TOP"}""");
        test.Http.On(HttpMethod.Get, "/items/TOP:/Backup?", """{"id":"BK","name":"Backup","folder":{}}""");
        test.Http.On(HttpMethod.Get, "/items/BK/children", """{"value":[{"id":"S","name":"Sub","folder":{}},{"id":"x","name":"x.txt","size":1,"file":{}}]}""");
        test.Http.On(HttpMethod.Get, "/items/S/children", """{"value":[{"id":"y","name":"y.txt","size":2,"file":{}}]}""");
        var storage = new OneDriveProvider().Open(test.Context(ConnectionKind.OneDrive, "Backup"));

        var entries = await CloudTest.ListAllAsync(storage);

        Assert.Equal(["Sub", "Sub/y.txt", "x.txt"], entries.Keys.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(test.Http.Requests, request => request.Url.Contains("/delta", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Small_files_are_put_and_get_their_time_large_ones_go_in_an_upload_session()
    {
        using var test = new CloudTest();
        PersonalDrive(test);
        GraphStorage.SimpleUploadLimit = 1024;
        GraphStorage.ChunkSize = 320 * 1024;
        var content = CloudTest.Bytes(700 * 1024);
        var received = new MemoryStream();
        test.Http.On(HttpMethod.Put, "/items/TOP:/klein.txt:/content", """{"id":"K"}""");
        test.Http.On(HttpMethod.Patch, "/items/K?", """{"id":"K","name":"klein.txt","size":5,"file":{"hashes":{"quickXorHash":"KK"}},"fileSystemInfo":{"lastModifiedDateTime":"2026-11-20T10:00:00Z"}}""");
        test.Http.On(HttpMethod.Post, "/items/TOP:/gross.bin:/createUploadSession", """{"uploadUrl":"https://upload.example.com/graph/session-1"}""");
        test.Http.On(HttpMethod.Put, "upload.example.com/graph/session-1", request =>
        {
            received.Write(request.Body);
            return received.Length < content.Length
                ? FakeHttp.Json($$"""{"nextExpectedRanges":["{{received.Length}}-"]}""", HttpStatusCode.Accepted)
                : FakeHttp.Json("""{"id":"G","name":"gross.bin"}""", HttpStatusCode.Created);
        });
        test.Http.On(HttpMethod.Get, "/items/G?", $$$"""{"id":"G","name":"gross.bin","size":{{{content.Length}}},"file":{"hashes":{"quickXorHash":"GG"}},"fileSystemInfo":{"lastModifiedDateTime":"2026-11-20T10:00:00Z"}}""");
        var storage = new OneDriveProvider().Open(test.Context(ConnectionKind.OneDrive));
        var time = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);

        var small = await storage.WriteAsync("klein.txt", new MemoryStream(Encoding.UTF8.GetBytes("hallo")), 5, time, null, CancellationToken.None);
        var progress = new CountingProgress();
        var large = await storage.WriteAsync("gross.bin", new MemoryStream(content), content.Length, time, progress, CancellationToken.None);

        Assert.Equal(new StorageEntry("klein.txt", false, 5, time) { Hash = "quickxor:KK", NativeId = "K" }, small);
        Assert.Contains("\"lastModifiedDateTime\":\"2026-11-20T10:00:00Z\"", test.Http.Requests.Single(request => request.Method == HttpMethod.Patch).Text, StringComparison.Ordinal);
        Assert.Equal(new StorageEntry("gross.bin", false, content.Length, time) { Hash = "quickxor:GG", NativeId = "G" }, large);
        Assert.Equal(content, received.ToArray());
        Assert.Equal(content.Length, progress.Total);
        var chunks = test.Http.Requests.Where(request => request.Url.Contains("session-1", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, chunk => Assert.Null(chunk.Header("Authorization"))); // the session address is signed: no token there
        Assert.Equal($"bytes 655360-716799/{content.Length}", chunks[2].Header("Content-Range"));
        var session = test.Http.Requests.Single(request => request.Url.Contains("createUploadSession", StringComparison.Ordinal));
        Assert.Contains("\"@microsoft.graph.conflictBehavior\":\"replace\"", session.Text, StringComparison.Ordinal);
        Assert.Contains("\"lastModifiedDateTime\":\"2026-11-20T10:00:00Z\"", session.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Downloads_follow_the_redirect_to_the_cdn_without_the_token()
    {
        using var test = new CloudTest();
        PersonalDrive(test);
        test.Http.On(HttpMethod.Get, "/items/TOP:/Ordner/ä b.txt:/content", _ =>
        {
            var response = FakeHttp.Status(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://cdn.example.com/download/abc");
            return response;
        });
        test.Http.On(HttpMethod.Get, "cdn.example.com/download/abc", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("inhalt") });
        var storage = new OneDriveProvider().Open(test.Context(ConnectionKind.OneDrive));

        await using var stream = await storage.OpenReadAsync("Ordner/ä b.txt", CancellationToken.None);

        Assert.Equal("inhalt", await new StreamReader(stream).ReadToEndAsync());
        Assert.StartsWith("Bearer ", test.Http.Requests.Single(request => request.Url.Contains(":/content", StringComparison.Ordinal)).Header("Authorization"), StringComparison.Ordinal);
        Assert.Null(test.Http.Requests.Single(request => request.Url.Contains("cdn.example.com", StringComparison.Ordinal)).Header("Authorization"));
    }

    [Fact]
    public async Task Throttling_becomes_rate_limited_with_the_wait_and_short_waits_are_sat_out()
    {
        using var test = new CloudTest();
        PersonalDrive(test);
        var calls = 0;
        test.Http.On(HttpMethod.Get, "/items/TOP:/kurz.txt?", _ => Interlocked.Increment(ref calls) == 1
            ? FakeHttp.Json("""{"error":{"code":"activityLimitReached"}}""", HttpStatusCode.TooManyRequests).WithRetryAfter(0)
            : FakeHttp.Json("""{"id":"k","name":"kurz.txt","size":1,"file":{}}"""));
        test.Http.On(HttpMethod.Get, "/items/TOP:/lang.txt?", _ => FakeHttp.Json("""{"error":{"code":"activityLimitReached"}}""", HttpStatusCode.TooManyRequests).WithRetryAfter(120));
        test.Http.On(HttpMethod.Get, "/items/TOP:/voll.txt?", _ => FakeHttp.Json("""{"error":{"code":"serviceNotAvailable"}}""", HttpStatusCode.ServiceUnavailable).WithRetryAfter(90));
        test.Http.On(HttpMethod.Get, "/items/TOP:/fehlt.txt?", _ => FakeHttp.Json("""{"error":{"code":"itemNotFound"}}""", HttpStatusCode.NotFound));
        var storage = new OneDriveProvider().Open(test.Context(ConnectionKind.OneDrive));

        var shortWait = await storage.GetAsync("kurz.txt", CancellationToken.None);
        var throttled = await Assert.ThrowsAsync<StorageException>(() => storage.GetAsync("lang.txt", CancellationToken.None));
        var unavailable = await Assert.ThrowsAsync<StorageException>(() => storage.GetAsync("voll.txt", CancellationToken.None));
        var missing = await storage.GetAsync("fehlt.txt", CancellationToken.None);

        Assert.Equal("k", shortWait!.Value.NativeId);
        Assert.Equal(2, calls);
        Assert.Equal(StorageErrorKind.RateLimited, throttled.Kind);
        Assert.Equal(TimeSpan.FromSeconds(120), throttled.RetryAfter);
        Assert.True(throttled.IsRetryable);
        Assert.Equal(StorageErrorKind.RateLimited, unavailable.Kind); // Graph throttles with 503 + Retry-After too
        Assert.Equal(TimeSpan.FromSeconds(90), unavailable.RetryAfter);
        Assert.Null(missing);
    }

    [Fact]
    public async Task Names_that_onedrive_refuses_are_skipped_before_any_upload()
    {
        using var test = new CloudTest();
        PersonalDrive(test);
        var storage = new OneDriveProvider().Open(test.Context(ConnectionKind.OneDrive));

        var error = await Assert.ThrowsAsync<StorageException>(() =>
            storage.WriteAsync("Ordner/frage?.txt", new MemoryStream([1]), 1, DateTimeOffset.UnixEpoch, null, CancellationToken.None));
        var dot = await Assert.ThrowsAsync<StorageException>(() => storage.CreateFolderAsync("Ende.", CancellationToken.None));

        Assert.Equal(StorageErrorKind.InvalidName, error.Kind);
        Assert.Equal(StorageErrorKind.InvalidName, dot.Kind);
        Assert.Contains('?', storage.Capabilities.InvalidNameCharacters);
    }

    [Fact]
    public async Task SharePoint_finds_the_site_from_its_address_and_the_library_by_its_german_or_english_name()
    {
        using var test = new CloudTest();
        test.Http.On(HttpMethod.Get, "/sites/firma.sharepoint.com:/sites/Team Projekte?", """{"id":"SITE"}""");
        test.Http.On(HttpMethod.Get, "/sites/SITE/drives?", """
            {"value":[
              {"id":"D1","name":"Archiv","driveType":"documentLibrary","webUrl":"https://firma.sharepoint.com/sites/Team%20Projekte/Archiv"},
              {"id":"D2","name":"Documents","driveType":"documentLibrary","webUrl":"https://firma.sharepoint.com/sites/Team%20Projekte/Shared%20Documents"}
            ]}
            """);
        test.Http.On(HttpMethod.Get, "/drives/D2/root?", """{"id":"TOP2"}""");
        test.Http.On(HttpMethod.Get, "/drives/D2/items/TOP2/delta", """{"value":[{"id":"x","name":"x.docx","size":1,"file":{},"parentReference":{"id":"TOP2"}}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/x"}""");
        var settings = new Dictionary<string, string>
        {
            [ConnectionFields.SiteUrl] = "https://firma.sharepoint.com/sites/Team%20Projekte/Freigegebene%20Dokumente/Forms/AllItems.aspx",
            [ConnectionFields.Library] = "Dokumente",
        };
        var storage = new OneDriveProvider().Open(test.Context(ConnectionKind.SharePoint, settings: settings));

        var entries = await CloudTest.ListAllAsync(storage);

        Assert.Equal(["x.docx"], entries.Keys);
        Assert.Equal(ConnectionKind.SharePoint, storage.Kind);
    }

    [Fact]
    public async Task The_change_feed_is_the_delta_link()
    {
        using var test = new CloudTest();
        PersonalDrive(test);
        test.Http.On(HttpMethod.Get, "/items/TOP/delta?token=latest", """{"value":[],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/drives/D/items/TOP/delta?token=t1"}""");
        test.Http.On(HttpMethod.Get, "/items/TOP/delta?token=t1", """{"value":[{"id":"TOP","root":{},"folder":{}}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/drives/D/items/TOP/delta?token=t2"}""");
        test.Http.On(HttpMethod.Get, "/items/TOP/delta?token=t2", """{"value":[{"id":"TOP","root":{}},{"id":"n","name":"neu.txt","file":{},"parentReference":{"id":"TOP"}}],"@odata.deltaLink":"x"}""");
        var feed = (IChangeFeed)new OneDriveProvider().Open(test.Context(ConnectionKind.OneDrive));

        var cursor = await feed.GetCursorAsync(CancellationToken.None);

        Assert.Equal($"{Graph}/drives/D/items/TOP/delta?token=t1", cursor);
        Assert.False(await feed.HasChangesAsync(cursor, CancellationToken.None));
        Assert.True(await feed.HasChangesAsync($"{Graph}/drives/D/items/TOP/delta?token=t2", CancellationToken.None));
        Assert.True(await feed.HasChangesAsync("https://elsewhere.example.com/steal?token=t2", CancellationToken.None));
        Assert.DoesNotContain(test.Http.Requests, request => request.Url.Contains("elsewhere", StringComparison.Ordinal));
    }

    private static void PersonalDrive(CloudTest test)
    {
        test.Http.On(HttpMethod.Get, "/me/drive?", """{"id":"D","driveType":"personal"}""");
        test.Http.On(HttpMethod.Get, "/drives/D/root?", """{"id":"TOP"}""");
    }
}
