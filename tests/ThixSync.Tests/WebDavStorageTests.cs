using System.Text;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;
using ThixSync.Core.Storage.Providers.Network;
using ThixSync.Core.Storage.Providers.WebDav;

namespace ThixSync.Tests;

/// <summary>
/// The WebDAV and Nextcloud storages against <see cref="FakeWebDavServer"/>: connecting (root made parents first), listing, reading,
/// writing to a temporary name + MOVE (Nextcloud: PUT with X-OC-Mtime), folders, deleting, moving – with names that need
/// percent-encoding (spaces, umlauts, '#', '&amp;') – and the errors the engine decides by.
/// </summary>
public sealed class WebDavStorageTests : IDisposable
{
    private static readonly DateTimeOffset Modified = new(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);

    public void Dispose() => HttpStorageClient.HandlerForTests = null;

    [Fact]
    public async Task WebDav_writes_reads_lists_moves_and_deletes_names_that_need_encoding()
    {
        var server = Use(new FakeWebDavServer("/dav") { User = "anna", Password = "geheim" });
        await using var storage = Open(ConnectionKind.WebDav, "https://dav.example.test/dav/", "/Backup", "Fotos 2024");

        await storage.ConnectAsync(createRoot: true, CancellationToken.None);
        // the folder itself is tried first (usually only it is missing); refused for a missing parent, the parents come first
        Assert.Equal(["MKCOL /dav/Backup/", "MKCOL /dav/Backup/Fotos%202024/"], server.Requests.Where(request => request.StartsWith("MKCOL", StringComparison.Ordinal)).TakeLast(2));
        Assert.True(server.Find("/dav/Backup/Fotos 2024")?.IsFolder);

        var progress = new CountingProgress();
        var entry = await WriteAsync(storage, "Ordner #1/Grüße & Co.txt", "Hallo Welt", progress);
        Assert.Equal(10, progress.Total);
        Assert.Equal("Hallo Welt", server.Text("/dav/Backup/Fotos 2024/Ordner #1/Grüße & Co.txt"));
        Assert.Equal("Ordner #1/Grüße & Co.txt", entry.Path);
        Assert.Equal(10, entry.Size);
        Assert.Contains(server.Requests, request => request.StartsWith("PUT /dav/Backup/Fotos%202024/Ordner%20%231/.~thixsync-", StringComparison.Ordinal));
        Assert.Contains(server.Requests, request => request.StartsWith("MOVE /dav/Backup/Fotos%202024/Ordner%20%231/.~thixsync-", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Paths(), path => path.Contains(".~thixsync-", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Requests, request => request[(request.IndexOf(' ') + 1)..].IndexOfAny([' ', '#', 'ü', 'ß']) >= 0);

        var listed = await ListAsync(storage);
        Assert.Equal(["Ordner #1", "Ordner #1/Grüße & Co.txt"], listed.Select(item => item.Path));
        Assert.True(listed[0].IsFolder);
        Assert.Equal(10, listed[1].Size);

        Assert.Equal("Ordner #1/Grüße & Co.txt", (await storage.GetAsync("Ordner #1/Grüße & Co.txt", CancellationToken.None))?.Path);
        Assert.Null(await storage.GetAsync("gibt es nicht.txt", CancellationToken.None));
        Assert.Equal("Hallo Welt", await ReadAsync(storage, "Ordner #1/Grüße & Co.txt"));

        await storage.MoveAsync("Ordner #1/Grüße & Co.txt", "Archiv 100%/Neu ä.txt", CancellationToken.None);
        Assert.Null(server.Find("/dav/Backup/Fotos 2024/Ordner #1/Grüße & Co.txt"));
        Assert.Equal("Hallo Welt", server.Text("/dav/Backup/Fotos 2024/Archiv 100%/Neu ä.txt"));

        await storage.CreateFolderAsync("Leer/Tief", CancellationToken.None);
        Assert.True(server.Find("/dav/Backup/Fotos 2024/Leer/Tief")?.IsFolder);

        await storage.DeleteAsync("Archiv 100%/Neu ä.txt", isFolder: false, CancellationToken.None);
        await storage.DeleteAsync("Ordner #1", isFolder: true, CancellationToken.None);
        await storage.DeleteAsync("schon weg.txt", isFolder: false, CancellationToken.None);
        Assert.Equal(["Archiv 100%", "Leer", "Leer/Tief"], (await ListAsync(storage)).Select(item => item.Path));
        Assert.Equal(StorageFeatures.None, storage.Capabilities.Features & StorageFeatures.SetModifiedTime);
    }

    [Fact]
    public async Task Writing_replaces_an_existing_file_and_creates_missing_parents()
    {
        var server = Use(new FakeWebDavServer("/dav"));
        server.AddFile("/dav/alt.txt", "alt");
        await using var storage = Open(ConnectionKind.WebDav, "https://dav.example.test/dav", "", "");
        await storage.ConnectAsync(createRoot: false, CancellationToken.None);

        await WriteAsync(storage, "alt.txt", "neu");
        await WriteAsync(storage, "a/b/c/d.txt", "tief");

        Assert.Equal("neu", server.Text("/dav/alt.txt"));
        Assert.Equal("tief", server.Text("/dav/a/b/c/d.txt"));
    }

    [Fact]
    public async Task Nextcloud_puts_directly_and_keeps_the_modification_time()
    {
        var server = Use(new FakeWebDavServer("/remote.php/dav/files/anna") { AcceptsMtime = true });
        await using var storage = Open(ConnectionKind.Nextcloud, "https://cloud.example.test", "", "Dokumente");
        await storage.ConnectAsync(createRoot: true, CancellationToken.None);

        var entry = await WriteAsync(storage, "Brief an Jörg.txt", "Hallo");

        Assert.True(storage.Capabilities.Has(StorageFeatures.SetModifiedTime | StorageFeatures.CaseSensitive));
        Assert.Equal(Modified, entry.Modified);
        Assert.Equal("00000042oc", entry.NativeId);
        Assert.Equal(Modified, server.Find("/remote.php/dav/files/anna/Dokumente/Brief an Jörg.txt")?.Modified);
        Assert.Contains("PUT /remote.php/dav/files/anna/Dokumente/Brief%20an%20J%C3%B6rg.txt", server.Requests);
        Assert.DoesNotContain(server.Requests, request => request.StartsWith("MOVE", StringComparison.Ordinal));

        var listed = Assert.Single(await ListAsync(storage));
        Assert.Equal("sha1:abcdef", listed.Hash);
        Assert.Equal(Modified, listed.Modified);
    }

    [Fact]
    public async Task Nextcloud_signed_in_by_mail_finds_the_files_of_the_user_id()
    {
        var server = Use(new FakeWebDavServer("/remote.php/dav/files/anna-id") { OcsUserId = "anna-id" });
        server.AddFile("/remote.php/dav/files/anna-id/notiz.txt", "x");
        var context = Context(ConnectionKind.Nextcloud, "https://cloud.example.test/", "", "") with
        {
            Connection = Connection(ConnectionKind.Nextcloud, "https://cloud.example.test/", "").With(ConnectionFields.UserName, "anna@example.test"),
        };
        await using var storage = new WebDavProvider().Open(context);

        await storage.ConnectAsync(createRoot: false, CancellationToken.None);

        Assert.Equal("notiz.txt", Assert.Single(await ListAsync(storage)).Path);
    }

    [Fact]
    public async Task Errors_say_what_happened()
    {
        Use(new FakeWebDavServer("/dav") { User = "anna", Password = "richtig" });
        await using var wrongPassword = Open(ConnectionKind.WebDav, "https://dav.example.test/dav/", "", "");
        var refused = await Assert.ThrowsAsync<StorageException>(() => wrongPassword.ConnectAsync(createRoot: false, CancellationToken.None));
        Assert.Equal(StorageErrorKind.Authentication, refused.Kind);
        Assert.True(refused.StopsRun);
        Assert.DoesNotContain("geheim", refused.Message, StringComparison.Ordinal);

        Use(new FakeWebDavServer("/dav"));
        await using var missing = Open(ConnectionKind.WebDav, "https://dav.example.test/dav/", "", "Fehlt");
        var notFound = await Assert.ThrowsAsync<StorageException>(() => missing.ConnectAsync(createRoot: false, CancellationToken.None));
        Assert.Equal(StorageErrorKind.NotFound, notFound.Kind);
        var unreadable = await Assert.ThrowsAsync<StorageException>(() => missing.OpenReadAsync("nichts.txt", CancellationToken.None));
        Assert.Equal(StorageErrorKind.NotFound, unreadable.Kind);
    }

    [Fact]
    public async Task The_multistatus_answer_is_read_whatever_form_the_hrefs_have()
    {
        const string Xml = """
            <?xml version="1.0"?>
            <D:multistatus xmlns:D="DAV:">
              <D:response><D:href>https://dav.example.test/dav/Fotos/</D:href>
                <D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>
              <D:response><D:href>/dav/Fotos/Gr%C3%BC%C3%9Fe%20%2310.jpg</D:href>
                <D:propstat><D:prop><D:resourcetype/><D:getcontentlength>1234</D:getcontentlength>
                <D:getlastmodified>Tue, 14 Nov 2023 09:12:00 GMT</D:getlastmodified><D:getetag>"abc"</D:getetag></D:prop>
                <D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>
              <D:response><D:href>/dav/Fotos/roh #1 ü.txt</D:href>
                <D:propstat><D:prop><D:getcontentlength>5</D:getcontentlength></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>
            </D:multistatus>
            """;
        var request = new Uri("https://dav.example.test/dav/Fotos/");

        var items = await WebDavMultistatus.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(Xml)), request, CancellationToken.None);

        Assert.Equal(["/dav/Fotos", "/dav/Fotos/Grüße #10.jpg", "/dav/Fotos/roh #1 ü.txt"], items.Select(item => item.Path));
        Assert.True(items[0].IsFolder);
        Assert.Equal(1234, items[1].Size);
        Assert.Equal(new DateTimeOffset(2023, 11, 14, 9, 12, 0, TimeSpan.Zero), items[1].Modified);
        Assert.Equal("abc", items[1].ETag);
        Assert.Equal("roh #1 ü.txt", items[2].Name);
    }

    [Fact]
    public void Addresses_are_built_segment_by_segment()
    {
        Assert.Equal("a%20b/%C3%BC%231/100%25", WebDavStorage.EscapePath("a b/ü#1/100%"));
        Assert.Equal("https://cloud.example.test/nc/remote.php/dav/files/anna%40example.test/",
            WebDavStorage.OwnCloudFilesUri(new Uri("https://cloud.example.test/nc/remote.php/webdav/"), "anna@example.test").AbsoluteUri);
        Assert.Equal("https://dav.example.test/", WebDavProvider.AddressOf("dav.example.test").AbsoluteUri);
        Assert.Throws<StorageException>(() => WebDavProvider.AddressOf("ftp://x"));
    }

    private static FakeWebDavServer Use(FakeWebDavServer server)
    {
        HttpStorageClient.HandlerForTests = () => server;
        return server;
    }

    private static Connection Connection(ConnectionKind kind, string url, string basePath) => new()
    {
        Name = "Server",
        Kind = kind,
        Settings = new Dictionary<string, string>
        {
            [ConnectionFields.Url] = url,
            [ConnectionFields.UserName] = "anna",
            [ConnectionFields.BasePath] = basePath,
        },
    };

    private static StorageContext Context(ConnectionKind kind, string url, string basePath, string folder) => new()
    {
        Connection = Connection(kind, url, basePath),
        Folder = folder,
        Secret = "geheim",
    };

    private static IStorage Open(ConnectionKind kind, string url, string basePath, string folder) =>
        new WebDavProvider().Open(Context(kind, url, basePath, folder));

    internal static async Task<StorageEntry> WriteAsync(IStorage storage, string path, string text, IProgress<long>? progress = null)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await using var content = new MemoryStream(bytes);
        return await storage.WriteAsync(path, content, bytes.Length, Modified, progress, CancellationToken.None);
    }

    internal static async Task<string> ReadAsync(IStorage storage, string path)
    {
        await using var stream = await storage.OpenReadAsync(path, CancellationToken.None);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    internal static async Task<List<StorageEntry>> ListAsync(IStorage storage)
    {
        var entries = new List<StorageEntry>();
        await foreach (var entry in storage.ListAsync("", (folder, exception) => throw new InvalidOperationException($"{folder}: {exception.Message}"), CancellationToken.None))
        {
            entries.Add(entry);
        }

        return [.. entries.OrderBy(entry => entry.Path, StringComparer.Ordinal)];
    }

    /// <summary>Adds up the reported bytes on the calling thread (Progress&lt;T&gt; would report later on the thread pool).</summary>
    internal sealed class CountingProgress : IProgress<long>
    {
        private long _total;

        public long Total => Interlocked.Read(ref _total);

        public void Report(long value) => Interlocked.Add(ref _total, value);
    }
}
