using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using ThixSync.Core.Storage.Providers.S3;

namespace ThixSync.Tests;

/// <summary>
/// An S3 bucket in memory behind an HttpMessageHandler: ListObjectsV2 (prefix, delimiter, continuation tokens, small pages),
/// HEAD/GET/PUT/DELETE, CopyObject, DeleteObjects (checks Content-MD5), multipart upload and UploadPartCopy. Every request's
/// Signature V4 is computed again from what arrived and must match (so path/query encoding on the wire is what was signed).
/// </summary>
internal sealed class FakeS3(string bucket, string secretKey) : HttpMessageHandler
{
    private static readonly XNamespace S3 = "http://s3.amazonaws.com/doc/2006-03-01/";
    private int _uploads;

    public sealed record StoredObject(byte[] Data, DateTimeOffset Modified, string ETag, IReadOnlyDictionary<string, string> Meta);

    public ConcurrentDictionary<string, StoredObject> Objects { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, (string Key, ConcurrentDictionary<int, byte[]> Parts, Dictionary<string, string> Meta)> Uploads { get; } = new();

    /// <summary>The most keys one list page gives (S3: 1000).</summary>
    public int PageSize { get; set; } = 1000;

    /// <summary>Answers a request before the bucket does (errors, redirects); null: the bucket answers.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Intercept { get; set; }

    /// <summary>"METHOD host path?query" of every request.</summary>
    public ConcurrentQueue<string> Log { get; } = new();

    public void Put(string key, string content, DateTimeOffset? modified = null)
    {
        var data = Encoding.UTF8.GetBytes(content);
        Objects[key] = new StoredObject(data, modified ?? DateTimeOffset.UtcNow, Convert.ToHexStringLower(MD5.HashData(data)), new Dictionary<string, string>());
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Log.Enqueue($"{request.Method} {uri.Host} {uri.PathAndQuery}");
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        VerifySignature(request, body);
        if (Intercept?.Invoke(request) is { } intercepted)
        {
            return intercepted;
        }

        var query = ParseQuery(uri.Query);
        var virtualHosted = uri.Host.StartsWith(bucket + ".", StringComparison.Ordinal);
        var path = uri.AbsolutePath;
        var bucketPath = virtualHosted ? "/" : $"/{bucket}/";
        if (!path.StartsWith(bucketPath, StringComparison.Ordinal) && path != bucketPath.TrimEnd('/'))
        {
            return Error(HttpStatusCode.NotFound, "NoSuchBucket");
        }

        var key = path.Length <= bucketPath.Length ? "" : Uri.UnescapeDataString(path[bucketPath.Length..]);
        var method = request.Method.Method;
        if (key.Length == 0)
        {
            return method switch
            {
                "GET" when query.GetValueOrDefault("list-type") == "2" => List(query),
                "POST" when query.ContainsKey("delete") => DeleteMany(request, body),
                _ => Error(HttpStatusCode.BadRequest, "NotImplemented"),
            };
        }

        return method switch
        {
            "HEAD" => Head(key),
            "GET" => Get(key),
            "PUT" when request.Headers.Contains("x-amz-copy-source") && query.ContainsKey("partNumber") => UploadPartCopy(request, query),
            "PUT" when request.Headers.Contains("x-amz-copy-source") => Copy(request, key),
            "PUT" when query.ContainsKey("partNumber") => UploadPart(query, body),
            "PUT" => PutObject(request, key, body),
            "POST" when query.ContainsKey("uploads") => CreateUpload(request, key),
            "POST" when query.ContainsKey("uploadId") => Complete(query, body),
            "DELETE" when query.ContainsKey("uploadId") => Abort(query),
            "DELETE" => Delete(key),
            _ => Error(HttpStatusCode.BadRequest, "NotImplemented"),
        };
    }

    public static HttpResponseMessage Error(HttpStatusCode status, string code, string? region = null)
    {
        var xml = new XElement("Error", new XElement("Code", code), new XElement("Message", "fake " + code));
        if (region is not null)
        {
            xml.Add(new XElement("Region", region));
        }

        return new HttpResponseMessage(status) { Content = new StringContent(xml.ToString(), Encoding.UTF8, "application/xml") };
    }

    private HttpResponseMessage List(Dictionary<string, string> query)
    {
        var prefix = query.GetValueOrDefault("prefix") ?? "";
        var delimiter = query.GetValueOrDefault("delimiter");
        var after = query.TryGetValue("continuation-token", out var token) ? Encoding.UTF8.GetString(Convert.FromBase64String(token)) : null;
        var max = Math.Min(PageSize, query.TryGetValue("max-keys", out var maxKeys) ? int.Parse(maxKeys, CultureInfo.InvariantCulture) : 1000);
        var keys = Objects.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        var contents = new List<XElement>();
        var prefixes = new SortedSet<string>(StringComparer.Ordinal);
        string? last = null;
        var truncated = false;
        foreach (var key in keys)
        {
            if (after is not null && string.CompareOrdinal(key, after) <= 0)
            {
                continue;
            }

            if (contents.Count + prefixes.Count >= max)
            {
                truncated = true;
                break;
            }

            last = key;
            var rest = key[prefix.Length..];
            if (delimiter is not null && rest.IndexOf(delimiter, StringComparison.Ordinal) is var cut and >= 0)
            {
                prefixes.Add(prefix + rest[..(cut + 1)]);
                continue;
            }

            var item = Objects[key];
            contents.Add(new XElement(S3 + "Contents",
                new XElement(S3 + "Key", key),
                new XElement(S3 + "LastModified", item.Modified.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)),
                new XElement(S3 + "ETag", $"\"{item.ETag}\""),
                new XElement(S3 + "Size", item.Data.Length),
                new XElement(S3 + "StorageClass", "STANDARD")));
        }

        var result = new XElement(S3 + "ListBucketResult",
            new XElement(S3 + "Name", bucket),
            new XElement(S3 + "Prefix", prefix),
            new XElement(S3 + "KeyCount", contents.Count + prefixes.Count),
            new XElement(S3 + "IsTruncated", truncated ? "true" : "false"),
            contents,
            prefixes.Select(common => new XElement(S3 + "CommonPrefixes", new XElement(S3 + "Prefix", common))));
        if (truncated && last is not null)
        {
            result.Add(new XElement(S3 + "NextContinuationToken", Convert.ToBase64String(Encoding.UTF8.GetBytes(last))));
        }

        return Xml(result);
    }

    private HttpResponseMessage Head(string key)
    {
        if (!Objects.TryGetValue(key, out var item))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) };
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        response.Content.Headers.ContentLength = item.Data.Length;
        response.Content.Headers.LastModified = item.Modified;
        response.Headers.TryAddWithoutValidation("ETag", $"\"{item.ETag}\"");
        foreach (var (name, value) in item.Meta)
        {
            response.Headers.TryAddWithoutValidation("x-amz-meta-" + name, value);
        }

        return response;
    }

    private HttpResponseMessage Get(string key)
    {
        if (!Objects.TryGetValue(key, out var item))
        {
            return Error(HttpStatusCode.NotFound, "NoSuchKey");
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(item.Data) };
    }

    private HttpResponseMessage PutObject(HttpRequestMessage request, string key, byte[] body)
    {
        var etag = Convert.ToHexStringLower(MD5.HashData(body));
        Objects[key] = new StoredObject(body, DateTimeOffset.UtcNow, etag, MetaOf(request));
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("ETag", $"\"{etag}\"");
        return response;
    }

    private HttpResponseMessage Copy(HttpRequestMessage request, string key)
    {
        var source = SourceKeyOf(request);
        if (!Objects.TryGetValue(source, out var item))
        {
            return Error(HttpStatusCode.NotFound, "NoSuchKey");
        }

        Objects[key] = item with { Modified = DateTimeOffset.UtcNow };
        return Xml(new XElement(S3 + "CopyObjectResult", new XElement(S3 + "ETag", $"\"{item.ETag}\"")));
    }

    private HttpResponseMessage CreateUpload(HttpRequestMessage request, string key)
    {
        var id = $"upload-{Interlocked.Increment(ref _uploads)}";
        Uploads[id] = (key, new ConcurrentDictionary<int, byte[]>(), MetaOf(request));
        return Xml(new XElement(S3 + "InitiateMultipartUploadResult", new XElement(S3 + "Key", key), new XElement(S3 + "UploadId", id)));
    }

    private HttpResponseMessage UploadPart(Dictionary<string, string> query, byte[] body)
    {
        if (!Uploads.TryGetValue(query["uploadId"], out var upload))
        {
            return Error(HttpStatusCode.NotFound, "NoSuchUpload");
        }

        upload.Parts[int.Parse(query["partNumber"], CultureInfo.InvariantCulture)] = body;
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("ETag", $"\"{Convert.ToHexStringLower(MD5.HashData(body))}\"");
        return response;
    }

    private HttpResponseMessage UploadPartCopy(HttpRequestMessage request, Dictionary<string, string> query)
    {
        if (!Uploads.TryGetValue(query["uploadId"], out var upload) || !Objects.TryGetValue(SourceKeyOf(request), out var source))
        {
            return Error(HttpStatusCode.NotFound, "NoSuchUpload");
        }

        var range = request.Headers.GetValues("x-amz-copy-source-range").Single()["bytes=".Length..].Split('-');
        var first = int.Parse(range[0], CultureInfo.InvariantCulture);
        var last = int.Parse(range[1], CultureInfo.InvariantCulture);
        var part = source.Data[first..(last + 1)];
        upload.Parts[int.Parse(query["partNumber"], CultureInfo.InvariantCulture)] = part;
        return Xml(new XElement(S3 + "CopyPartResult", new XElement(S3 + "ETag", $"\"{Convert.ToHexStringLower(MD5.HashData(part))}\"")));
    }

    private HttpResponseMessage Complete(Dictionary<string, string> query, byte[] body)
    {
        if (!Uploads.TryRemove(query["uploadId"], out var upload))
        {
            return Error(HttpStatusCode.NotFound, "NoSuchUpload");
        }

        var parts = XElement.Parse(Encoding.UTF8.GetString(body)).Elements("Part")
            .Select(part => (Number: int.Parse(part.Element("PartNumber")!.Value, CultureInfo.InvariantCulture), ETag: part.Element("ETag")!.Value))
            .ToList();
        if (parts.Select(part => part.Number).SequenceEqual(Enumerable.Range(1, upload.Parts.Count)) is false)
        {
            return Error(HttpStatusCode.BadRequest, "InvalidPartOrder");
        }

        var data = parts.SelectMany(part => upload.Parts[part.Number]).ToArray();
        var etag = $"{Convert.ToHexStringLower(MD5.HashData(Encoding.ASCII.GetBytes(string.Concat(parts.Select(part => part.ETag)))))}-{parts.Count}";
        Objects[upload.Key] = new StoredObject(data, DateTimeOffset.UtcNow, etag, upload.Meta);
        return Xml(new XElement(S3 + "CompleteMultipartUploadResult", new XElement(S3 + "Key", upload.Key), new XElement(S3 + "ETag", $"\"{etag}\"")));
    }

    private HttpResponseMessage Abort(Dictionary<string, string> query)
    {
        Uploads.TryRemove(query["uploadId"], out _);
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    private HttpResponseMessage Delete(string key)
    {
        Objects.TryRemove(key, out _);
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    private HttpResponseMessage DeleteMany(HttpRequestMessage request, byte[] body)
    {
        var md5 = request.Content!.Headers.TryGetValues("Content-MD5", out var values) ? values.Single() : null;
        if (md5 != Convert.ToBase64String(MD5.HashData(body)))
        {
            return Error(HttpStatusCode.BadRequest, "InvalidDigest");
        }

        var keys = XElement.Parse(Encoding.UTF8.GetString(body)).Elements("Object").Select(item => item.Element("Key")!.Value).ToList();
        if (keys.Count > 1000)
        {
            return Error(HttpStatusCode.BadRequest, "MalformedXML");
        }

        foreach (var key in keys)
        {
            Objects.TryRemove(key, out _);
        }

        return Xml(new XElement(S3 + "DeleteResult"));
    }

    private string SourceKeyOf(HttpRequestMessage request)
    {
        var source = request.Headers.GetValues("x-amz-copy-source").Single();
        return Uri.UnescapeDataString(source[(bucket.Length + 2)..]);
    }

    private static Dictionary<string, string> MetaOf(HttpRequestMessage request) =>
        request.Headers.Where(header => header.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(header => header.Key["x-amz-meta-".Length..].ToLowerInvariant(), header => header.Value.Single());

    private static HttpResponseMessage Xml(XElement element) =>
        new(HttpStatusCode.OK) { Content = new StringContent(new XDocument(element).ToString(), Encoding.UTF8, "application/xml") };

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = part.IndexOf('=');
            var name = Uri.UnescapeDataString(index < 0 ? part : part[..index]);
            result[name] = index < 0 ? "" : Uri.UnescapeDataString(part[(index + 1)..]);
        }

        return result;
    }

    private void VerifySignature(HttpRequestMessage request, byte[] body)
    {
        var authorization = request.Headers.GetValues("Authorization").Single();
        var signedNames = authorization.Split("SignedHeaders=")[1].Split(',')[0].Split(';');
        var allHeaders = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(header => header.Key.ToLowerInvariant(), header => string.Join(",", header.Value));
        allHeaders["host"] = request.RequestUri!.Authority;
        foreach (var name in allHeaders.Keys.Where(name => name.StartsWith("x-amz-", StringComparison.Ordinal) || name is "content-md5" or "content-type"))
        {
            Assert.Contains(name, signedNames);
        }

        var payloadHash = allHeaders["x-amz-content-sha256"];
        if (payloadHash != S3Signer.UnsignedPayload)
        {
            Assert.Equal(S3Signer.Sha256Hex(body), payloadHash);
        }

        var credential = authorization.Split("Credential=")[1].Split(',')[0].Split('/');
        var time = DateTime.ParseExact(allHeaders["x-amz-date"], "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        var query = ParseQuery(request.RequestUri.Query).ToList();
        var headers = signedNames.Select(name => new KeyValuePair<string, string>(name, allHeaders[name])).ToList();
        var expected = S3Signer.Authorization(credential[0], secretKey, credential[2], credential[3], request.Method.Method, request.RequestUri.AbsolutePath,
            query, headers, payloadHash, time);
        Assert.Equal(expected, authorization);
    }
}
