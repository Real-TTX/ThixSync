using System.Globalization;
using System.Net;
using System.Text;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;
using ThixSync.Core.Storage.Providers.S3;

namespace ThixSync.Tests;

/// <summary>Signature V4 against the examples of the AWS documentation, and the S3 storage against an in-memory bucket.</summary>
public sealed class S3StorageTests
{
    private const string ExampleKeyId = "AKIAIOSFODNN7EXAMPLE";
    private const string ExampleSecret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    private static readonly DateTime ExampleTime = new(2013, 5, 24, 0, 0, 0, DateTimeKind.Utc);

    // ---- Signature V4: the examples of "Signature Calculations for the Authorization Header" (S3 documentation) ----------

    [Fact]
    public void Signature_of_the_get_object_example_matches_aws()
    {
        var authorization = S3Signer.Authorization(ExampleKeyId, ExampleSecret, "us-east-1", "s3", "GET", "/test.txt", [],
        [
            new("Host", "examplebucket.s3.amazonaws.com"),
            new("Range", "bytes=0-9"),
            new("x-amz-content-sha256", S3Signer.EmptyPayloadHash),
            new("x-amz-date", "20130524T000000Z"),
        ], S3Signer.EmptyPayloadHash, ExampleTime);

        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request,SignedHeaders=host;range;x-amz-content-sha256;x-amz-date,"
            + "Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41", authorization);
    }

    [Fact]
    public void Signature_of_the_put_object_example_matches_aws()
    {
        var payload = S3Signer.Sha256Hex("Welcome to Amazon S3."u8);
        Assert.Equal("44ce7dd67c959e0d3524ffac1771dfbba87d2b6b4b4e99e42034a8b803f8b072", payload);
        var path = "/" + CloudHttp.UriEncode("test$file.text", keepSlash: true);
        Assert.Equal("/test%24file.text", path);

        var authorization = S3Signer.Authorization(ExampleKeyId, ExampleSecret, "us-east-1", "s3", "PUT", path, [],
        [
            new("Host", "examplebucket.s3.amazonaws.com"),
            new("Date", "Fri, 24 May 2013 00:00:00 GMT"),
            new("x-amz-date", "20130524T000000Z"),
            new("x-amz-storage-class", "REDUCED_REDUNDANCY"),
            new("x-amz-content-sha256", payload),
        ], payload, ExampleTime);

        Assert.EndsWith("SignedHeaders=date;host;x-amz-content-sha256;x-amz-date;x-amz-storage-class,"
            + "Signature=98ad721746da40c64f1a55b78f14c238d841ea1380cd77a1b5971af0ece108bd", authorization);
    }

    [Fact]
    public void Signature_of_the_lifecycle_and_list_examples_matches_aws()
    {
        IReadOnlyList<KeyValuePair<string, string>> Headers() =>
        [
            new("Host", "examplebucket.s3.amazonaws.com"),
            new("x-amz-date", "20130524T000000Z"),
            new("x-amz-content-sha256", S3Signer.EmptyPayloadHash),
        ];

        var lifecycle = S3Signer.Authorization(ExampleKeyId, ExampleSecret, "us-east-1", "s3", "GET", "/", [new("lifecycle", "")], Headers(),
            S3Signer.EmptyPayloadHash, ExampleTime);
        var list = S3Signer.Authorization(ExampleKeyId, ExampleSecret, "us-east-1", "s3", "GET", "/", [new("prefix", "J"), new("max-keys", "2")], Headers(),
            S3Signer.EmptyPayloadHash, ExampleTime);

        Assert.EndsWith("Signature=fea454ca298b7da1c68078a5d1bdbfbbe0d65c699e0f91ac7a200a0136783543", lifecycle);
        Assert.EndsWith("Signature=34b48302e7b5fa45bde8084f4b7868a86f0a534bc59db6670ed5711ef69dc6f7", list);
    }

    [Fact]
    public void Keys_are_encoded_once_with_slashes_kept()
    {
        Assert.Equal("Fotos/Urlaub%202024/Bild%20%231%2B2%20%C3%A4.jpg", CloudHttp.UriEncode("Fotos/Urlaub 2024/Bild #1+2 ä.jpg", keepSlash: true));
        Assert.Equal("a%2Fb%3Dc%26d~e", CloudHttp.UriEncode("a/b=c&d~e", keepSlash: false));
        Assert.Equal("%F0%9F%98%80", CloudHttp.UriEncode("😀", keepSlash: false));
    }

    [Fact]
    public void Endpoints_of_the_presets_and_aws()
    {
        static S3Settings Of(ConnectionKind kind, params (string Key, string Value)[] fields) => S3Settings.From(Context(kind, [("accessKeyId", "AKID"), .. fields]), "Fotos");

        var aws = Of(ConnectionKind.S3, ("bucket", "my-bucket"), ("region", "eu-central-1"), ("base", "/Backup/"));
        Assert.Equal("https://my-bucket.s3.eu-central-1.amazonaws.com/Backup/Fotos/a%20b.txt", aws.Address("Backup/Fotos/a b.txt", false, []).Uri.AbsoluteUri);
        Assert.Equal("Backup/Fotos/", aws.RootPrefix);
        Assert.True(aws.RegionAdjustable);

        var dotted = Of(ConnectionKind.S3, ("bucket", "my.bucket"), ("region", "us-west-2"));
        Assert.Equal("https://s3.us-west-2.amazonaws.com/my.bucket/k", dotted.Address("k", false, []).Uri.AbsoluteUri);

        var minio = Of(ConnectionKind.S3, ("bucket", "b"), ("endpoint", "http://nas.local:9000"), ("pathStyle", "true"));
        Assert.Equal("http://nas.local:9000/b/Fotos/x", minio.Address("Fotos/x", false, []).Uri.AbsoluteUri);
        Assert.False(minio.RegionAdjustable);

        Assert.Equal("https://b.s3.eu-central-2.wasabisys.com/k", Of(ConnectionKind.Wasabi, ("bucket", "b"), ("region", "eu-central-2")).Address("k", false, []).Uri.AbsoluteUri);
        Assert.Equal("https://b.s3.eu-central-003.backblazeb2.com/k", Of(ConnectionKind.BackblazeB2, ("bucket", "b")).Address("k", false, []).Uri.AbsoluteUri);

        var gcs = Of(ConnectionKind.GoogleCloudStorage, ("bucket", "b"));
        Assert.Equal("https://storage.googleapis.com/b/k", gcs.Address("k", false, []).Uri.AbsoluteUri);
        Assert.Equal("auto", gcs.Region);
        Assert.False(gcs.BatchDelete);
    }

    // ---- The storage against the fake bucket ---------------------------------------------------------------------------

    [Fact]
    public async Task Writes_lists_reads_and_heads_keys_with_special_characters()
    {
        var fake = new FakeS3("bucket", "secret");
        await using var storage = Open(fake, ("base", "Basis"));
        await storage.ConnectAsync(createRoot: true, CancellationToken.None);
        string[] names = ["Fotos/Urlaub 2024/Bild #1+2 ä.jpg", "a&b=c?.txt", "100%.txt", ".hidden", "Ordner/Zürich €/x~y.txt"];
        var written = new Dictionary<string, StorageEntry>();
        foreach (var name in names)
        {
            var modified = new DateTimeOffset(2024, 5, 1, 12, 30, 15, 250, TimeSpan.Zero);
            written[name] = await WriteTextAsync(storage, name, "Inhalt von " + name, modified);
        }

        Assert.True(fake.Objects.ContainsKey("Basis/Fotos/Urlaub 2024/Bild #1+2 ä.jpg"));
        Assert.Equal("1714566615.25", fake.Objects["Basis/a&b=c?.txt"].Meta["mtime"]);

        var listed = await ListAsync(storage, "");
        foreach (var name in names)
        {
            var entry = listed.Single(item => item.Path == name);
            Assert.Equal(written[name], entry); // time, size and hash as WriteAsync returned them
            Assert.StartsWith("md5:", entry.Hash);
            Assert.Equal(TimeSpan.Zero, TimeSpan.FromTicks(entry.Modified.UtcTicks % TimeSpan.TicksPerSecond));
            Assert.Equal("Inhalt von " + name, await ReadTextAsync(storage, name));
        }

        Assert.Contains(listed, entry => entry is { Path: "Fotos/Urlaub 2024", IsFolder: true });
        Assert.Contains(listed, entry => entry is { Path: "Fotos", IsFolder: true });
        Assert.True(listed.Single(entry => entry.Path == ".hidden").IsHidden);

        Assert.Equal(written["100%.txt"], await storage.GetAsync("100%.txt", CancellationToken.None));
        Assert.True((await storage.GetAsync("Fotos/Urlaub 2024", CancellationToken.None))!.Value.IsFolder);
        Assert.Null(await storage.GetAsync("gibt es nicht", CancellationToken.None));
    }

    [Fact]
    public async Task Lists_the_whole_tree_in_pages_with_markers_and_derived_folders()
    {
        var fake = new FakeS3("bucket", "secret") { PageSize = 2 };
        fake.Put("root/a.txt", "1");
        fake.Put("root/leer/", "");
        fake.Put("root/x/y/z.txt", "22");
        fake.Put("root/x/b.txt", "333");
        fake.Put("root/x/y/", "");
        fake.Put("rootandmore/nicht.txt", "no");
        fake.Put("root/bad//key.txt", "skipped");
        await using var storage = Open(fake, ("base", "root"));

        var problems = new List<string>();
        var entries = new List<StorageEntry>();
        await foreach (var entry in storage.ListAsync("", (path, _) => problems.Add(path), CancellationToken.None))
        {
            entries.Add(entry);
        }

        Assert.Equal(["a.txt", "leer", "x", "x/b.txt", "x/y", "x/y/z.txt"], entries.Select(entry => entry.Path).Order(StringComparer.Ordinal));
        Assert.True(entries.Single(entry => entry.Path == "leer").IsFolder);
        Assert.Equal(3, entries.Single(entry => entry.Path == "x/b.txt").Size);
        Assert.Equal(["bad//key.txt"], problems);
        Assert.True(fake.Log.Count(line => line.Contains("list-type=2", StringComparison.Ordinal)) >= 3); // really paged (6 keys, 2 per page)

        var below = await ListAsync(storage, "x");
        Assert.Equal(["x/b.txt", "x/y", "x/y/z.txt"], below.Select(entry => entry.Path).Order(StringComparer.Ordinal));

        Assert.Equal(["bad", "leer", "x"], await storage.ListChildFoldersAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Large_files_go_up_in_parts_with_progress()
    {
        var fake = new FakeS3("bucket", "secret");
        await using var small = new S3Storage(Context(ConnectionKind.S3, Fields()), fake) { SinglePutLimit = 1024, PartSize = 1000 };
        var data = Enumerable.Range(0, 4500).Select(index => (byte)(index * 7)).ToArray();
        var reported = 0L;
        var progress = new SyncProgress(value => Interlocked.Add(ref reported, value));

        var entry = await small.WriteAsync("groß/datei.bin", new NonSeekableStream(data), data.Length, DateTimeOffset.UtcNow, progress, CancellationToken.None);

        Assert.Equal(data, fake.Objects["groß/datei.bin"].Data);
        Assert.Equal(5, fake.Log.Count(line => line.Contains("partNumber=", StringComparison.Ordinal)));
        Assert.Equal(data.Length, reported);
        Assert.Null(entry.Hash); // a multipart ETag is no MD5
        Assert.Equal(data.Length, entry.Size);
        Assert.Empty(fake.Uploads);
        Assert.Contains("mtime", fake.Objects["groß/datei.bin"].Meta.Keys);
    }

    [Fact]
    public async Task A_failing_part_aborts_the_multipart_upload()
    {
        var fake = new FakeS3("bucket", "secret");
        fake.Intercept = request => request.RequestUri!.Query.Contains("partNumber=3", StringComparison.Ordinal)
            ? FakeS3.Error(HttpStatusCode.InternalServerError, "InternalError")
            : null;
        await using var storage = new S3Storage(Context(ConnectionKind.S3, Fields()), fake) { SinglePutLimit = 1024, PartSize = 1000 };
        var data = new byte[4500];

        var error = await Assert.ThrowsAsync<StorageException>(() =>
            storage.WriteAsync("f.bin", new MemoryStream(data), data.Length, DateTimeOffset.UtcNow, null, CancellationToken.None));

        Assert.Equal(StorageErrorKind.Transient, error.Kind);
        Assert.Empty(fake.Uploads); // aborted
        Assert.False(fake.Objects.ContainsKey("f.bin"));
        Assert.Contains(fake.Log, line => line.StartsWith("DELETE", StringComparison.Ordinal) && line.Contains("uploadId=", StringComparison.Ordinal));
    }

    [Fact]
    public void Part_size_grows_to_stay_below_ten_thousand_parts()
    {
        const long MiB = 1024 * 1024;
        Assert.Equal(16 * MiB, S3Storage.PartSizeFor(100 * MiB, 16 * MiB));
        Assert.Equal(16 * MiB, S3Storage.PartSizeFor(10_000 * 16 * MiB, 16 * MiB));
        var huge = 5L * 1024 * 1024 * MiB;
        var size = S3Storage.PartSizeFor(huge, 16 * MiB);
        Assert.True((huge + size - 1) / size <= 10_000);
        Assert.Equal(0, size % MiB);
    }

    [Fact]
    public async Task Plain_http_endpoints_sign_the_payload_hash()
    {
        var fake = new FakeS3("bucket", "secret");
        await using var storage = new S3Storage(Context(ConnectionKind.S3, Fields(("endpoint", "http://nas.local:9000"))), fake);

        await WriteTextAsync(storage, "a.txt", "Hallo", DateTimeOffset.UtcNow);

        Assert.Equal("Hallo", Encoding.UTF8.GetString(fake.Objects["a.txt"].Data)); // FakeS3 checked x-amz-content-sha256 against the body
    }

    [Fact]
    public async Task Deletes_files_and_folders_in_batches()
    {
        var fake = new FakeS3("bucket", "secret");
        for (var index = 0; index < 1205; index++)
        {
            fake.Put($"weg/{index:D4}.txt", "x");
        }

        fake.Put("weg/", "");
        fake.Put("weg-nicht.txt", "bleibt");
        fake.Put("einzeln.txt", "x");
        await using var storage = Open(fake);

        await storage.DeleteAsync("einzeln.txt", isFolder: false, CancellationToken.None);
        await storage.DeleteAsync("weg", isFolder: true, CancellationToken.None);
        await storage.DeleteAsync("gibt-es-nicht.txt", isFolder: false, CancellationToken.None);

        Assert.Equal(["weg-nicht.txt"], fake.Objects.Keys);
        Assert.Equal(2, fake.Log.Count(line => line.Contains("?delete", StringComparison.Ordinal)));
        await Assert.ThrowsAsync<StorageException>(() => storage.DeleteAsync("", isFolder: true, CancellationToken.None));
    }

    [Fact]
    public async Task Google_cloud_storage_deletes_one_by_one()
    {
        var fake = new FakeS3("bucket", "secret");
        fake.Put("ordner/a", "1");
        fake.Put("ordner/b", "2");
        await using var storage = new S3Storage(Context(ConnectionKind.GoogleCloudStorage, Fields()), fake);

        await storage.DeleteAsync("ordner", isFolder: true, CancellationToken.None);

        Assert.Empty(fake.Objects);
        Assert.DoesNotContain(fake.Log, line => line.Contains("?delete", StringComparison.Ordinal));
        Assert.All(fake.Log, line => Assert.Contains("storage.googleapis.com", line));
    }

    [Fact]
    public async Task Moves_files_and_folders_by_copying_on_the_server()
    {
        var fake = new FakeS3("bucket", "secret");
        fake.Put("alt/a b.txt", "A");
        fake.Put("alt/sub/c+d.txt", "C");
        fake.Put("datei.txt", "D");
        await using var storage = Open(fake);
        Assert.True(storage.Capabilities.Has(StorageFeatures.Move | StorageFeatures.ServerSideCopy));
        Assert.False(storage.Capabilities.Has(StorageFeatures.SetModifiedTime));

        await storage.MoveAsync("datei.txt", "neu/datei #2.txt", CancellationToken.None);
        await storage.MoveAsync("alt", "neu/ordner", CancellationToken.None);

        Assert.Equal(["neu/datei #2.txt", "neu/ordner/a b.txt", "neu/ordner/sub/c+d.txt"], fake.Objects.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("C", Encoding.UTF8.GetString(fake.Objects["neu/ordner/sub/c+d.txt"].Data));
        await Assert.ThrowsAsync<StorageException>(() => storage.MoveAsync("nichts", "da", CancellationToken.None));
    }

    [Fact]
    public async Task Large_objects_are_copied_in_parts()
    {
        var fake = new FakeS3("bucket", "secret");
        var data = Enumerable.Range(0, 2500).Select(index => (byte)index).ToArray();
        fake.Objects["gross.bin"] = new FakeS3.StoredObject(data, DateTimeOffset.UtcNow, "x-1", new Dictionary<string, string> { ["mtime"] = "1700000000" });
        await using var storage = new S3Storage(Context(ConnectionKind.S3, Fields()), fake) { SingleCopyLimit = 1000, CopyPartSize = 1000 };

        await storage.MoveAsync("gross.bin", "ziel.bin", CancellationToken.None);

        Assert.Equal(data, fake.Objects["ziel.bin"].Data);
        Assert.Equal("1700000000", fake.Objects["ziel.bin"].Meta["mtime"]);
        Assert.Equal(3, fake.Log.Count(line => line.Contains("partNumber=", StringComparison.Ordinal)));
        Assert.False(fake.Objects.ContainsKey("gross.bin"));
    }

    [Fact]
    public async Task Connect_checks_the_folder_and_creates_it_on_request()
    {
        var fake = new FakeS3("bucket", "secret");
        await using var storage = Open(fake, ("base", "Backup/Neu"));

        var missing = await Assert.ThrowsAsync<StorageException>(() => storage.ConnectAsync(createRoot: false, CancellationToken.None));
        Assert.Equal(StorageErrorKind.NotFound, missing.Kind);

        await storage.ConnectAsync(createRoot: true, CancellationToken.None);
        Assert.True(fake.Objects.ContainsKey("Backup/Neu/"));
        await storage.ConnectAsync(createRoot: false, CancellationToken.None);

        await storage.CreateFolderAsync("Leer/Ordner", CancellationToken.None);
        Assert.True(fake.Objects.ContainsKey("Backup/Neu/Leer/Ordner/"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "SignatureDoesNotMatch", StorageErrorKind.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, "InvalidAccessKeyId", StorageErrorKind.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, "AccessDenied", StorageErrorKind.AccessDenied)]
    [InlineData(HttpStatusCode.NotFound, "NoSuchKey", StorageErrorKind.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "SlowDown", StorageErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.TooManyRequests, "", StorageErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, "InternalError", StorageErrorKind.Transient)]
    [InlineData(HttpStatusCode.BadGateway, "", StorageErrorKind.Transient)]
    [InlineData(HttpStatusCode.Forbidden, "QuotaExceeded", StorageErrorKind.QuotaExceeded)]
    [InlineData(HttpStatusCode.BadRequest, "KeyTooLongError", StorageErrorKind.InvalidName)]
    public async Task Errors_are_mapped(HttpStatusCode status, string code, StorageErrorKind kind)
    {
        var fake = new FakeS3("bucket", "secret");
        fake.Intercept = _ =>
        {
            var response = code.Length == 0 ? new HttpResponseMessage(status) : FakeS3.Error(status, code);
            response.Headers.TryAddWithoutValidation("Retry-After", "7");
            return response;
        };
        await using var storage = Open(fake);

        var error = await Assert.ThrowsAsync<StorageException>(() => storage.OpenReadAsync("a.txt", CancellationToken.None));

        Assert.Equal(kind, error.Kind);
        Assert.Contains("a.txt", error.Message);
        Assert.DoesNotContain("secret", error.Message);
        Assert.Equal(kind == StorageErrorKind.RateLimited ? TimeSpan.FromSeconds(7) : null, error.RetryAfter);
    }

    [Fact]
    public async Task Connect_reports_a_missing_bucket_and_refused_access()
    {
        var fake = new FakeS3("bucket", "secret") { Intercept = _ => FakeS3.Error(HttpStatusCode.NotFound, "NoSuchBucket") };
        await using var storage = Open(fake);
        var missing = await Assert.ThrowsAsync<StorageException>(() => storage.ConnectAsync(false, CancellationToken.None));
        Assert.Equal(StorageErrorKind.NotFound, missing.Kind);
        Assert.Contains("„bucket“", missing.Message);

        fake.Intercept = _ => FakeS3.Error(HttpStatusCode.Forbidden, "AccessDenied");
        var denied = await Assert.ThrowsAsync<StorageException>(() => storage.ConnectAsync(false, CancellationToken.None));
        Assert.Equal(StorageErrorKind.Authentication, denied.Kind);
    }

    [Fact]
    public async Task A_bucket_in_another_region_is_followed_and_remembered()
    {
        var fake = new FakeS3("bucket", "secret");
        fake.Intercept = request =>
        {
            if (!request.RequestUri!.Host.Contains("eu-central-1", StringComparison.Ordinal))
            {
                return null;
            }

            var response = FakeS3.Error(HttpStatusCode.BadRequest, "AuthorizationHeaderMalformed", "us-west-2");
            return response;
        };
        var learned = new List<(string Key, string Value)>();
        var context = Context(ConnectionKind.S3, ("bucket", "bucket"), ("region", "eu-central-1"), ("accessKeyId", "AKID")) with
        {
            SaveSettingAsync = (key, value, _) =>
            {
                learned.Add((key, value));
                return Task.CompletedTask;
            },
        };
        await using var storage = new S3Storage(context, fake);

        await storage.ConnectAsync(createRoot: false, CancellationToken.None);
        await WriteTextAsync(storage, "a.txt", "x", DateTimeOffset.UtcNow);

        Assert.Equal([("region", "us-west-2")], learned);
        Assert.Contains(fake.Log, line => line.Contains("bucket.s3.us-west-2.amazonaws.com", StringComparison.Ordinal));
        Assert.True(fake.Objects.ContainsKey("a.txt"));

        // a custom endpoint cannot be moved: the user gets the region named
        var other = new FakeS3("bucket", "secret") { Intercept = _ => FakeS3.Error(HttpStatusCode.BadRequest, "AuthorizationHeaderMalformed", "us-west-2") };
        await using var custom = new S3Storage(Context(ConnectionKind.S3, Fields(("endpoint", "https://s3.example.com"))), other);
        var error = await Assert.ThrowsAsync<S3WrongRegionException>(() => custom.ConnectAsync(false, CancellationToken.None));
        Assert.Contains("„us-west-2“", error.Message);
    }

    [Fact]
    public void Etag_hashes_only_for_single_part_objects()
    {
        Assert.Equal("md5:d41d8cd98f00b204e9800998ecf8427e", S3Client.HashOf("\"D41D8CD98F00B204E9800998ECF8427E\""));
        Assert.Null(S3Client.HashOf("\"9b2cf535f27731c974343645a3985328-3\""));
        Assert.Null(S3Client.HashOf(""));
    }

    [Fact]
    public void The_provider_is_found_for_every_s3_kind()
    {
        foreach (var kind in new[] { ConnectionKind.S3, ConnectionKind.Wasabi, ConnectionKind.BackblazeB2, ConnectionKind.GoogleCloudStorage })
        {
            Assert.IsType<S3Provider>(StorageProviders.Find(kind));
        }
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------------

    internal static StorageContext Context(ConnectionKind kind, params (string Key, string Value)[] fields) => new()
    {
        Connection = new Connection
        {
            Kind = kind,
            Name = "Test",
            Settings = fields.GroupBy(field => field.Key).ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal),
        },
        Secret = "secret",
    };

    private static (string Key, string Value)[] Fields(params (string Key, string Value)[] more) =>
        [("bucket", "bucket"), ("endpoint", "https://s3.test"), ("pathStyle", "true"), ("accessKeyId", "AKID"), .. more];

    private static S3Storage Open(FakeS3 fake, params (string Key, string Value)[] more) => new(Context(ConnectionKind.S3, Fields(more)), fake);

    internal static async Task<StorageEntry> WriteTextAsync(IStorage storage, string path, string text, DateTimeOffset modified)
    {
        var data = Encoding.UTF8.GetBytes(text);
        return await storage.WriteAsync(path, new NonSeekableStream(data), data.Length, modified, null, CancellationToken.None);
    }

    internal static async Task<string> ReadTextAsync(IStorage storage, string path)
    {
        await using var stream = await storage.OpenReadAsync(path, CancellationToken.None);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    internal static async Task<List<StorageEntry>> ListAsync(IStorage storage, string folder)
    {
        var entries = new List<StorageEntry>();
        await foreach (var entry in storage.ListAsync(folder, null, CancellationToken.None))
        {
            entries.Add(entry);
        }

        return entries;
    }
}

/// <summary>A source that cannot seek (like a download from another storage).</summary>
internal sealed class NonSeekableStream(byte[] data) : Stream
{
    private readonly MemoryStream _inner = new(data);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Progress without a synchronization context (Progress&lt;T&gt; would post to the thread pool and arrive late).</summary>
internal sealed class SyncProgress(Action<long> report) : IProgress<long>
{
    public void Report(long value) => report(value);
}
