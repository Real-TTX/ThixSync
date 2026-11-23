using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;
using ThixSync.Core.Storage.Providers.Azure;

namespace ThixSync.Tests;

/// <summary>The Shared Key signature (known answer) and the Azure Blob storage against an in-memory container.</summary>
public sealed class AzureBlobStorageTests
{
    // the well-known key of the storage emulator (Azurite), public on purpose
    private const string EmulatorKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    [Fact]
    public void Shared_key_signature_matches_a_known_answer()
    {
        var uri = new Uri("https://myaccount.blob.core.windows.net/mycontainer/Fotos/Urlaub%20%C3%A4.jpg?comp=block&blockid=QUJD%2B%2F%3D%3D");
        var stringToSign = AzureSharedKey.StringToSign("PUT", "myaccount", uri,
        [
            new("x-ms-version", "2023-11-03"),
            new("x-ms-date", "Mon, 23 Nov 2026 10:00:00 GMT"),
            new("Content-Length", "11"),
            new("x-ms-meta-mtime", "1700000000"),
        ]);

        Assert.Equal("PUT\n\n\n11\n\n\n\n\n\n\n\n\nx-ms-date:Mon, 23 Nov 2026 10:00:00 GMT\nx-ms-meta-mtime:1700000000\nx-ms-version:2023-11-03\n"
            + "/myaccount/mycontainer/Fotos/Urlaub%20%C3%A4.jpg\nblockid:QUJD+/==\ncomp:block", stringToSign);

        // computed independently with: openssl dgst -sha256 -mac HMAC -macopt hexkey:<key> -binary | base64
        Assert.Equal("SharedKey myaccount:GcGPH4Qg6TTmwyjbRIthGFagJ3nS3A52DQJb8QR4fp0=",
            AzureSharedKey.Authorization("myaccount", Convert.FromBase64String(EmulatorKey), stringToSign));
    }

    [Fact]
    public void Content_length_zero_is_signed_empty_and_the_emulator_path_keeps_the_account()
    {
        var stringToSign = AzureSharedKey.StringToSign("GET", "devstoreaccount1", new Uri("http://127.0.0.1:10000/devstoreaccount1/c?restype=container&comp=list"),
            [new("Content-Length", "0"), new("x-ms-date", "d"), new("x-ms-version", "v")]);

        Assert.Equal("GET\n\n\n\n\n\n\n\n\n\n\n\nx-ms-date:d\nx-ms-version:v\n/devstoreaccount1/devstoreaccount1/c\ncomp:list\nrestype:container", stringToSign);
    }

    [Fact]
    public async Task Writes_lists_reads_moves_and_deletes()
    {
        var fake = new FakeAzure("myaccount", "backup", EmulatorKey);
        await using var storage = new AzureBlobStorage(Context(("base", "Basis")), fake);
        await storage.ConnectAsync(createRoot: true, CancellationToken.None);
        Assert.True(fake.Blobs.ContainsKey("Basis/"));

        var modified = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var written = await S3StorageTests.WriteTextAsync(storage, "Fotos/Bild #1+ä.jpg", "Hallo Azure", modified);
        await S3StorageTests.WriteTextAsync(storage, "Fotos/sub/b.txt", "B", modified);
        await storage.CreateFolderAsync("Leer", CancellationToken.None);

        Assert.Equal("md5:" + Convert.ToHexStringLower(MD5.HashData("Hallo Azure"u8)), written.Hash);
        Assert.Equal("1704164645", fake.Blobs["Basis/Fotos/Bild #1+ä.jpg"].Meta["mtime"]);

        var listed = await S3StorageTests.ListAsync(storage, "");
        Assert.Equal(["Fotos", "Fotos/Bild #1+ä.jpg", "Fotos/sub", "Fotos/sub/b.txt", "Leer"], listed.Select(entry => entry.Path).Order(StringComparer.Ordinal));
        Assert.Equal(written, listed.Single(entry => entry.Path == "Fotos/Bild #1+ä.jpg"));
        Assert.Equal("Hallo Azure", await S3StorageTests.ReadTextAsync(storage, "Fotos/Bild #1+ä.jpg"));
        Assert.Equal(written, await storage.GetAsync("Fotos/Bild #1+ä.jpg", CancellationToken.None));
        Assert.True((await storage.GetAsync("Fotos/sub", CancellationToken.None))!.Value.IsFolder);
        Assert.Null(await storage.GetAsync("nichts", CancellationToken.None));
        Assert.Equal(["Fotos", "Leer"], await storage.ListChildFoldersAsync(CancellationToken.None));

        await storage.MoveAsync("Fotos/Bild #1+ä.jpg", "Neu/Bild.jpg", CancellationToken.None);
        await storage.MoveAsync("Fotos", "Alt/Fotos", CancellationToken.None);
        Assert.Equal(["Basis/", "Basis/Alt/Fotos/sub/b.txt", "Basis/Leer/", "Basis/Neu/Bild.jpg"], fake.Blobs.Keys.Order(StringComparer.Ordinal));

        await storage.DeleteAsync("Neu/Bild.jpg", isFolder: false, CancellationToken.None);
        await storage.DeleteAsync("Alt", isFolder: true, CancellationToken.None);
        Assert.Equal(["Basis/", "Basis/Leer/"], fake.Blobs.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Large_files_go_up_in_blocks_with_the_md5_of_the_whole_file()
    {
        var fake = new FakeAzure("myaccount", "backup", EmulatorKey) { PageSize = 1 };
        await using var storage = new AzureBlobStorage(Context(), fake) { SinglePutLimit = 1000, BlockSize = 700 };
        var data = Enumerable.Range(0, 3000).Select(index => (byte)(index % 251)).ToArray();
        var reported = 0L;

        var entry = await storage.WriteAsync("gross.bin", new NonSeekableStream(data), data.Length, DateTimeOffset.UtcNow,
            new SyncProgress(value => Interlocked.Add(ref reported, value)), CancellationToken.None);

        Assert.Equal(data, fake.Blobs["gross.bin"].Data);
        Assert.Equal(5, fake.Log.Count(line => line.Contains("comp=block&", StringComparison.Ordinal)));
        Assert.Equal("md5:" + Convert.ToHexStringLower(MD5.HashData(data)), entry.Hash);
        Assert.Equal(data.Length, reported);
        Assert.Equal(entry, (await S3StorageTests.ListAsync(storage, "")).Single());
    }

    [Fact]
    public async Task Sas_tokens_go_into_the_query_without_signature()
    {
        var fake = new FakeAzure("myaccount", "backup", null);
        await using var storage = new AzureBlobStorage(Context(("auth", "sas")) with { Secret = "?sv=2022-11-02&ss=b&sig=abc%2Bdef%3D" }, fake);

        await S3StorageTests.WriteTextAsync(storage, "a.txt", "x", DateTimeOffset.UtcNow);
        await storage.ConnectAsync(false, CancellationToken.None);

        Assert.All(fake.Log, line => Assert.Contains("sig=abc%2Bdef%3D", line));
        Assert.True(fake.Blobs.ContainsKey("a.txt"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "AuthenticationFailed", StorageErrorKind.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, "AuthorizationPermissionMismatch", StorageErrorKind.AccessDenied)]
    [InlineData(HttpStatusCode.NotFound, "BlobNotFound", StorageErrorKind.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "ServerBusy", StorageErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, "OperationTimedOut", StorageErrorKind.Transient)]
    [InlineData(HttpStatusCode.Conflict, "LeaseIdMissing", StorageErrorKind.Locked)]
    public async Task Errors_are_mapped(HttpStatusCode status, string code, StorageErrorKind kind)
    {
        var fake = new FakeAzure("myaccount", "backup", EmulatorKey) { Intercept = _ => FakeAzure.Error(status, code) };
        await using var storage = new AzureBlobStorage(Context(), fake);

        var error = await Assert.ThrowsAsync<StorageException>(() => storage.OpenReadAsync("x.txt", CancellationToken.None));

        Assert.Equal(kind, error.Kind);
        Assert.DoesNotContain(EmulatorKey, error.Message);
    }

    [Fact]
    public async Task A_missing_container_is_reported_by_name()
    {
        var fake = new FakeAzure("myaccount", "backup", EmulatorKey) { Intercept = _ => FakeAzure.Error(HttpStatusCode.NotFound, "ContainerNotFound") };
        await using var storage = new AzureBlobStorage(Context(), fake);

        var error = await Assert.ThrowsAsync<StorageException>(() => storage.ConnectAsync(false, CancellationToken.None));

        Assert.Equal(StorageErrorKind.NotFound, error.Kind);
        Assert.Contains("„backup“", error.Message);
    }

    [Fact]
    public void The_provider_is_found()
    {
        Assert.IsType<AzureBlobProvider>(StorageProviders.Find(ConnectionKind.AzureBlob));
    }

    private static StorageContext Context(params (string Key, string Value)[] more) =>
        S3StorageTests.Context(ConnectionKind.AzureBlob, [("account", "myaccount"), ("container", "backup"), .. more]) with { Secret = EmulatorKey };
}

/// <summary>
/// A container in memory behind an HttpMessageHandler: List Blobs (prefix, delimiter, marker, small pages), Get/Put/Delete Blob,
/// Put Block + Put Block List, Copy Blob. Checks the Shared Key signature of every request (or the SAS token in the query).
/// </summary>
internal sealed class FakeAzure(string account, string container, string? key) : HttpMessageHandler
{
    public sealed record StoredBlob(byte[] Data, DateTimeOffset Modified, string? ContentMd5, IReadOnlyDictionary<string, string> Meta);

    public ConcurrentDictionary<string, StoredBlob> Blobs { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<(string Blob, string Id), byte[]> Blocks { get; } = new();

    public int PageSize { get; set; } = 5000;

    public Func<HttpRequestMessage, HttpResponseMessage?>? Intercept { get; set; }

    public ConcurrentQueue<string> Log { get; } = new();

    public static HttpResponseMessage Error(HttpStatusCode status, string code)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(new XElement("Error", new XElement("Code", code), new XElement("Message", "fake")).ToString(), Encoding.UTF8, "application/xml"),
        };
        response.Headers.TryAddWithoutValidation("x-ms-error-code", code);
        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Log.Enqueue($"{request.Method} {uri.PathAndQuery}");
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        VerifyAuthorization(request);
        if (Intercept?.Invoke(request) is { } intercepted)
        {
            return intercepted;
        }

        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => part.Length > 1 ? Uri.UnescapeDataString(part[1]) : "");
        var containerPath = $"/{container}";
        if (uri.AbsolutePath == containerPath)
        {
            return request.Method == HttpMethod.Get && query.GetValueOrDefault("comp") == "list" ? List(query) : Error(HttpStatusCode.BadRequest, "Unsupported");
        }

        var name = Uri.UnescapeDataString(uri.AbsolutePath[(containerPath.Length + 1)..]);
        return request.Method.Method switch
        {
            "HEAD" => Head(name),
            "GET" => Blobs.TryGetValue(name, out var blob) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(blob.Data) } : Error(HttpStatusCode.NotFound, "BlobNotFound"),
            "PUT" when query.GetValueOrDefault("comp") == "block" => PutBlock(name, query["blockid"], body),
            "PUT" when query.GetValueOrDefault("comp") == "blocklist" => PutBlockList(request, name, body),
            "PUT" when request.Headers.Contains("x-ms-copy-source") => Copy(request, name),
            "PUT" => PutBlob(request, name, body),
            "DELETE" => Blobs.TryRemove(name, out _) ? new HttpResponseMessage(HttpStatusCode.Accepted) : Error(HttpStatusCode.NotFound, "BlobNotFound"),
            _ => Error(HttpStatusCode.BadRequest, "Unsupported"),
        };
    }

    private HttpResponseMessage List(Dictionary<string, string> query)
    {
        var prefix = query.GetValueOrDefault("prefix") ?? "";
        var delimiter = query.GetValueOrDefault("delimiter");
        var marker = query.GetValueOrDefault("marker");
        var max = Math.Min(PageSize, int.Parse(query.GetValueOrDefault("maxresults") ?? "5000", CultureInfo.InvariantCulture));
        var items = new List<XElement>();
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        string? next = null;
        foreach (var name in Blobs.Keys.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            if (marker is not null && string.CompareOrdinal(name, marker) < 0)
            {
                continue;
            }

            if (items.Count >= max)
            {
                next = name;
                break;
            }

            var rest = name[prefix.Length..];
            if (delimiter is not null && rest.IndexOf(delimiter, StringComparison.Ordinal) is var cut and >= 0)
            {
                if (prefixes.Add(prefix + rest[..(cut + 1)]))
                {
                    items.Add(new XElement("BlobPrefix", new XElement("Name", prefix + rest[..(cut + 1)])));
                }

                continue;
            }

            var blob = Blobs[name];
            items.Add(new XElement("Blob",
                new XElement("Name", name),
                new XElement("Properties",
                    new XElement("Last-Modified", blob.Modified.ToString("R", CultureInfo.InvariantCulture)),
                    new XElement("Etag", "0x8D"),
                    new XElement("Content-Length", blob.Data.Length),
                    new XElement("Content-MD5", blob.ContentMd5 ?? "")),
                new XElement("Metadata", blob.Meta.Select(meta => new XElement(meta.Key, meta.Value)))));
        }

        var result = new XElement("EnumerationResults", new XAttribute("ContainerName", container), new XElement("Prefix", prefix),
            new XElement("Blobs", items), new XElement("NextMarker", next ?? ""));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(result.ToString(), Encoding.UTF8, "application/xml") };
    }

    private HttpResponseMessage Head(string name)
    {
        if (!Blobs.TryGetValue(name, out var blob))
        {
            var missing = new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) };
            missing.Headers.TryAddWithoutValidation("x-ms-error-code", "BlobNotFound");
            return missing;
        }

        return WithProperties(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }, blob, includeLength: true);
    }

    private HttpResponseMessage PutBlob(HttpRequestMessage request, string name, byte[] body)
    {
        Assert.Equal("BlockBlob", request.Headers.GetValues("x-ms-blob-type").Single());
        var blob = new StoredBlob(body, DateTimeOffset.UtcNow, Convert.ToBase64String(MD5.HashData(body)), MetaOf(request));
        Blobs[name] = blob;
        return WithProperties(new HttpResponseMessage(HttpStatusCode.Created) { Content = new ByteArrayContent([]) }, blob, includeLength: false);
    }

    private HttpResponseMessage PutBlock(string name, string id, byte[] body)
    {
        Blocks[(name, id)] = body;
        return new HttpResponseMessage(HttpStatusCode.Created);
    }

    private HttpResponseMessage PutBlockList(HttpRequestMessage request, string name, byte[] body)
    {
        var ids = XElement.Parse(Encoding.UTF8.GetString(body)).Elements("Latest").Select(element => element.Value).ToList();
        var data = ids.SelectMany(id => Blocks[(name, id)]).ToArray();
        var md5 = request.Headers.TryGetValues("x-ms-blob-content-md5", out var values) ? values.Single() : null;
        var blob = new StoredBlob(data, DateTimeOffset.UtcNow, md5, MetaOf(request));
        Blobs[name] = blob;
        return WithProperties(new HttpResponseMessage(HttpStatusCode.Created) { Content = new ByteArrayContent([]) }, blob, includeLength: false);
    }

    private HttpResponseMessage Copy(HttpRequestMessage request, string name)
    {
        var source = new Uri(request.Headers.GetValues("x-ms-copy-source").Single());
        var sourceName = Uri.UnescapeDataString(source.AbsolutePath[(container.Length + 2)..]);
        if (!Blobs.TryGetValue(sourceName, out var blob))
        {
            return Error(HttpStatusCode.NotFound, "CannotVerifyCopySource");
        }

        Blobs[name] = blob with { Modified = DateTimeOffset.UtcNow };
        var response = new HttpResponseMessage(HttpStatusCode.Accepted);
        response.Headers.TryAddWithoutValidation("x-ms-copy-status", "success");
        return response;
    }

    private static HttpResponseMessage WithProperties(HttpResponseMessage response, StoredBlob blob, bool includeLength)
    {
        if (includeLength)
        {
            response.Content.Headers.ContentLength = blob.Data.Length;
        }

        response.Content.Headers.LastModified = blob.Modified;
        if (blob.ContentMd5 is not null)
        {
            response.Content.Headers.TryAddWithoutValidation("Content-MD5", blob.ContentMd5);
        }

        foreach (var (name, value) in blob.Meta)
        {
            response.Headers.TryAddWithoutValidation("x-ms-meta-" + name, value);
        }

        return response;
    }

    private static Dictionary<string, string> MetaOf(HttpRequestMessage request) =>
        request.Headers.Where(header => header.Key.StartsWith("x-ms-meta-", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(header => header.Key["x-ms-meta-".Length..].ToLowerInvariant(), header => header.Value.Single());

    private void VerifyAuthorization(HttpRequestMessage request)
    {
        Assert.Equal(AzureBlobClient.ApiVersion, request.Headers.GetValues("x-ms-version").Single());
        if (key is null)
        {
            Assert.False(request.Headers.Contains("Authorization"));
            Assert.Contains("sig=", request.RequestUri!.Query);
            return;
        }

        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .Where(header => header.Key != "Authorization")
            .Select(header => new KeyValuePair<string, string>(header.Key, string.Join(",", header.Value)))
            .ToList();
        var expected = AzureSharedKey.Authorization(account, Convert.FromBase64String(key), AzureSharedKey.StringToSign(request.Method.Method, account, request.RequestUri!, headers));
        Assert.Equal(expected, request.Headers.GetValues("Authorization").Single());
    }
}
