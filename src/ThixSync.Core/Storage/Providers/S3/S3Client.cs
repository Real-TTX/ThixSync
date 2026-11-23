using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ThixSync.Core.Storage.Providers.S3;

/// <summary>An object as S3 lists it (key, size, upload time to the second, ETag without quotes).</summary>
internal sealed record S3Object(string Key, long Size, DateTimeOffset Modified, string ETag)
{
    /// <summary>The original modification time the uploader kept in x-amz-meta-mtime (HEAD only).</summary>
    public string? MetaMtime { get; init; }
}

/// <summary>One page of ListObjectsV2.</summary>
internal sealed record S3ListPage(IReadOnlyList<S3Object> Objects, IReadOnlyList<string> CommonPrefixes, string? NextToken);

/// <summary>The bucket lives in another region than the request was signed for.</summary>
internal sealed class S3WrongRegionException(string region, string bucket) : StorageException(StorageErrorKind.Authentication,
    $"Der Bucket „{bucket}“ liegt in der Region „{region}“. Bitte diese Region in der Verbindung eintragen.")
{
    public string Region { get; } = region;
}

/// <summary>
/// The REST calls of S3 (and compatible services), signed with Signature V4, errors mapped to <see cref="StorageException"/>.
/// Safe for many calls at once (one pooled HttpClient). Follows a bucket of AWS into its real region once (and tells
/// <see cref="RegionLearned"/>).
/// </summary>
internal sealed class S3Client : IDisposable
{
    private static readonly XmlReaderSettings XmlSettings = new()
    {
        Async = true,
        CheckCharacters = false, // keys may hold characters XML 1.0 does not allow
        DtdProcessing = DtdProcessing.Prohibit,
        IgnoreWhitespace = true,
    };

    private readonly HttpClient _http;
    private S3Settings _settings;

    public S3Client(S3Settings settings, HttpMessageHandler? handler)
    {
        _settings = settings;
        _http = CloudHttp.CreateClient(handler, 64);
    }

    public S3Settings Settings => Volatile.Read(ref _settings);

    /// <summary>Called once when the bucket turned out to be in another region (the provider keeps it in the connection).</summary>
    public Func<string, Task>? RegionLearned { get; set; }

    public void Dispose() => _http.Dispose();

    // ---- Listing ----------------------------------------------------------------------------------------------------

    public async Task<S3ListPage> ListPageAsync(string prefix, string? delimiter, string? continuationToken, int? maxKeys, string path, CancellationToken cancellationToken)
    {
        var query = new List<KeyValuePair<string, string>> { new("list-type", "2"), new("prefix", prefix) };
        if (delimiter is not null)
        {
            query.Add(new("delimiter", delimiter));
        }

        if (continuationToken is not null)
        {
            query.Add(new("continuation-token", continuationToken));
        }

        if (maxKeys is not null)
        {
            query.Add(new("max-keys", maxKeys.Value.ToString(CultureInfo.InvariantCulture)));
        }

        using var response = await SendAsync(new S3Request(HttpMethod.Get, "", BucketLevel: true) { Query = query }, path, cancellationToken);
        var root = await ReadXmlAsync(response, path, cancellationToken);
        var objects = new List<S3Object>();
        var prefixes = new List<string>();
        foreach (var element in root.Elements())
        {
            switch (element.Name.LocalName)
            {
                case "Contents":
                    objects.Add(new S3Object(
                        Value(element, "Key") ?? "",
                        long.TryParse(Value(element, "Size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : 0,
                        ParseTime(Value(element, "LastModified")),
                        (Value(element, "ETag") ?? "").Trim('"')));
                    break;
                case "CommonPrefixes":
                    prefixes.Add(Value(element, "Prefix") ?? "");
                    break;
            }
        }

        var truncated = string.Equals(Value(root, "IsTruncated"), "true", StringComparison.OrdinalIgnoreCase);
        var next = Value(root, "NextContinuationToken");
        return new S3ListPage(objects, prefixes, truncated && !string.IsNullOrEmpty(next) ? next : null);
    }

    /// <summary>Every object below a prefix (all pages).</summary>
    public async IAsyncEnumerable<S3Object> ListAllAsync(string prefix, string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? token = null;
        do
        {
            var page = await ListPageAsync(prefix, null, token, null, path, cancellationToken);
            foreach (var item in page.Objects)
            {
                yield return item;
            }

            token = page.NextToken;
        }
        while (token is not null);
    }

    // ---- Objects ----------------------------------------------------------------------------------------------------

    /// <summary>The object, or null when there is none under this key.</summary>
    public async Task<S3Object?> HeadAsync(string key, string path, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(new S3Request(HttpMethod.Head, key), path, cancellationToken);
            return new S3Object(
                key,
                response.Content.Headers.ContentLength ?? 0,
                Truncate(response.Content.Headers.LastModified ?? DateTimeOffset.UtcNow),
                Header(response, "ETag")?.Trim('"') ?? "")
            {
                MetaMtime = Header(response, "x-amz-meta-mtime"),
            };
        }
        catch (StorageException exception) when (exception.Kind == StorageErrorKind.NotFound)
        {
            return null;
        }
    }

    public async Task<Stream> OpenReadAsync(string key, string path, CancellationToken cancellationToken)
    {
        var response = await SendAsync(new S3Request(HttpMethod.Get, key) { Completion = HttpCompletionOption.ResponseHeadersRead }, path, cancellationToken);
        return await ResponseStream.OpenAsync(response, path, cancellationToken);
    }

    /// <summary>PutObject; returns the ETag.</summary>
    public async Task<string> PutAsync(string key, Func<HttpContent> content, long length, bool repeatable, string payloadHash, IReadOnlyList<KeyValuePair<string, string>> headers,
        string path, CancellationToken cancellationToken)
    {
        var request = new S3Request(HttpMethod.Put, key)
        {
            Headers = headers,
            Content = content,
            Repeatable = repeatable,
            PayloadHash = payloadHash,
            LimitTime = length < 1024 * 1024, // a large body takes its time to go up
        };
        using var response = await SendAsync(request, path, cancellationToken);
        return Header(response, "ETag")?.Trim('"') ?? "";
    }

    public async Task DeleteAsync(string key, string path, CancellationToken cancellationToken)
    {
        try
        {
            using var _ = await SendAsync(new S3Request(HttpMethod.Delete, key), path, cancellationToken);
        }
        catch (StorageException exception) when (exception.Kind == StorageErrorKind.NotFound)
        {
        }
    }

    /// <summary>DeleteObjects with up to 1000 keys (quiet: only failures come back).</summary>
    public async Task DeleteManyAsync(IReadOnlyList<string> keys, string path, CancellationToken cancellationToken)
    {
        var document = new XElement("Delete",
            new XElement("Quiet", "true"),
            keys.Select(key => new XElement("Object", new XElement("Key", key))));
        var body = Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
        var request = new S3Request(HttpMethod.Post, "", BucketLevel: true)
        {
            Query = [new("delete", "")],
            Headers = [new("Content-MD5", Convert.ToBase64String(MD5.HashData(body))), new("Content-Type", "application/xml")],
            Content = () => new ByteArrayContent(body),
            Repeatable = true,
            PayloadHash = S3Signer.Sha256Hex(body),
        };
        using var response = await SendAsync(request, path, cancellationToken);
        var root = await ReadXmlAsync(response, path, cancellationToken);
        var error = root.Elements().FirstOrDefault(element => element.Name.LocalName == "Error");
        if (error is null)
        {
            return;
        }

        var code = Value(error, "Code");
        if (code == "NoSuchKey")
        {
            return;
        }

        throw Map(StatusOf(code), code, Value(error, "Message"), null, path, Settings.Bucket);
    }

    /// <summary>CopyObject inside the bucket (metadata kept); returns the ETag of the copy.</summary>
    public async Task<string> CopyAsync(string sourceKey, string targetKey, IReadOnlyList<KeyValuePair<string, string>> headers, string path, CancellationToken cancellationToken)
    {
        var request = new S3Request(HttpMethod.Put, targetKey)
        {
            Headers = [new("x-amz-copy-source", Settings.CopySource(sourceKey)), new("x-amz-metadata-directive", "COPY"), .. headers],
            Content = () => new ByteArrayContent([]),
            Repeatable = true,
            LimitTime = false, // a large copy takes its time on the server
        };
        using var response = await SendAsync(request, path, cancellationToken);
        var root = await ReadXmlAsync(response, path, cancellationToken);
        ThrowIfError(root, path);
        return (Value(root, "ETag") ?? "").Trim('"');
    }

    // ---- Multipart --------------------------------------------------------------------------------------------------

    public async Task<string> CreateMultipartAsync(string key, IReadOnlyList<KeyValuePair<string, string>> headers, string path, CancellationToken cancellationToken)
    {
        var request = new S3Request(HttpMethod.Post, key)
        {
            Query = [new("uploads", "")],
            Headers = headers,
            Content = () => new ByteArrayContent([]),
            Repeatable = true,
        };
        using var response = await SendAsync(request, path, cancellationToken);
        var root = await ReadXmlAsync(response, path, cancellationToken);
        ThrowIfError(root, path);
        return Value(root, "UploadId") ?? throw new StorageException(StorageErrorKind.Transient, $"Der Server nannte keine Upload-ID{CloudHttp.Where(path)}.");
    }

    /// <summary>UploadPart with a buffered body; returns the ETag (with its quotes, as CompleteMultipartUpload wants it).</summary>
    public async Task<string> UploadPartAsync(string key, string uploadId, int partNumber, Func<HttpContent> content, string payloadHash, string path,
        CancellationToken cancellationToken)
    {
        var request = new S3Request(HttpMethod.Put, key)
        {
            Query = [new("partNumber", partNumber.ToString(CultureInfo.InvariantCulture)), new("uploadId", uploadId)],
            Content = content,
            PayloadHash = payloadHash,
            LimitTime = false,
        };
        using var response = await SendAsync(request, path, cancellationToken);
        return Header(response, "ETag") ?? throw new StorageException(StorageErrorKind.Transient, $"Der Server nannte kein ETag für Teil {partNumber}{CloudHttp.Where(path)}.");
    }

    /// <summary>UploadPartCopy of the bytes <paramref name="first"/>…<paramref name="last"/> of another object; returns the ETag.</summary>
    public async Task<string> UploadPartCopyAsync(string key, string uploadId, int partNumber, string sourceKey, long first, long last, string path,
        CancellationToken cancellationToken)
    {
        var request = new S3Request(HttpMethod.Put, key)
        {
            Query = [new("partNumber", partNumber.ToString(CultureInfo.InvariantCulture)), new("uploadId", uploadId)],
            Headers =
            [
                new("x-amz-copy-source", Settings.CopySource(sourceKey)),
                new("x-amz-copy-source-range", $"bytes={first.ToString(CultureInfo.InvariantCulture)}-{last.ToString(CultureInfo.InvariantCulture)}"),
            ],
            Content = () => new ByteArrayContent([]),
            Repeatable = true,
            LimitTime = false,
        };
        using var response = await SendAsync(request, path, cancellationToken);
        var root = await ReadXmlAsync(response, path, cancellationToken);
        ThrowIfError(root, path);
        return Value(root, "ETag") ?? throw new StorageException(StorageErrorKind.Transient, $"Der Server nannte kein ETag für Teil {partNumber}{CloudHttp.Where(path)}.");
    }

    /// <summary>CompleteMultipartUpload (the parts in order); returns the ETag of the object.</summary>
    public async Task<string> CompleteMultipartAsync(string key, string uploadId, IReadOnlyList<string> partETags, string path, CancellationToken cancellationToken)
    {
        var document = new XElement("CompleteMultipartUpload",
            partETags.Select((etag, index) => new XElement("Part",
                new XElement("PartNumber", (index + 1).ToString(CultureInfo.InvariantCulture)),
                new XElement("ETag", etag))));
        var body = Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
        var request = new S3Request(HttpMethod.Post, key)
        {
            Query = [new("uploadId", uploadId)],
            Headers = [new("Content-Type", "application/xml")],
            Content = () => new ByteArrayContent(body),
            Repeatable = true,
            PayloadHash = S3Signer.Sha256Hex(body),
            LimitTime = false, // the server joins the parts before it answers
        };
        using var response = await SendAsync(request, path, cancellationToken);
        var root = await ReadXmlAsync(response, path, cancellationToken);
        ThrowIfError(root, path);
        return (Value(root, "ETag") ?? "").Trim('"');
    }

    /// <summary>AbortMultipartUpload, best effort (the parts would cost storage until a lifecycle rule removes them).</summary>
    public async Task AbortMultipartAsync(string key, string uploadId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(CloudHttp.RequestTimeout);
            using var _ = await SendAsync(new S3Request(HttpMethod.Delete, key) { Query = [new("uploadId", uploadId)] }, "", timeout.Token);
        }
        catch (Exception exception) when (exception is StorageException or OperationCanceledException)
        {
        }
    }

    // ---- Sending ----------------------------------------------------------------------------------------------------

    /// <summary>Signs and sends a request; a failed answer becomes a <see cref="StorageException"/>.</summary>
    public async Task<HttpResponseMessage> SendAsync(S3Request request, string path, CancellationToken cancellationToken)
    {
        var settings = Settings;
        try
        {
            return await SendOnceAsync(settings, request, path, cancellationToken);
        }
        catch (S3WrongRegionException wrong) when (settings.RegionAdjustable && request.Repeatable && wrong.Region != settings.Region)
        {
            var moved = settings.WithRegion(wrong.Region);
            Interlocked.CompareExchange(ref _settings, moved, settings);
            if (RegionLearned is { } learned)
            {
                await learned(wrong.Region);
            }

            return await SendOnceAsync(Settings, request, path, cancellationToken);
        }
    }

    /// <summary>Maps an S3 error code (or the status when there is none) to a German <see cref="StorageException"/>.</summary>
    public static StorageException Map(HttpStatusCode status, string? code, string? serverMessage, TimeSpan? retryAfter, string path, string bucket)
    {
        var kind = code switch
        {
            "InvalidAccessKeyId" or "SignatureDoesNotMatch" or "InvalidToken" or "ExpiredToken" or "TokenRefreshRequired" or "RequestTimeTooSkewed"
                or "AuthorizationHeaderMalformed" or "InvalidSecurity" or "AccountProblem" or "NotSignedUp" => StorageErrorKind.Authentication,
            "AccessDenied" or "AllAccessDisabled" or "AccountDisabled" => StorageErrorKind.AccessDenied,
            "NoSuchBucket" or "NoSuchKey" or "NoSuchUpload" => StorageErrorKind.NotFound,
            "SlowDown" or "TooManyRequests" or "RequestLimitExceeded" or "Throttling" or "ServiceUnavailable" => StorageErrorKind.RateLimited,
            "KeyTooLongError" or "InvalidObjectName" => StorageErrorKind.InvalidName,
            "QuotaExceeded" or "StorageQuotaExceeded" or "InsufficientStorage" or "XMinioStorageFull" => StorageErrorKind.QuotaExceeded,
            "InternalError" or "OperationAborted" or "RequestTimeout" => StorageErrorKind.Transient,
            _ => CloudHttp.KindOf(status),
        };

        // Backblaze answers a reached cap with 403 "Cap exceeded"; others write "quota" into the message
        if (kind is StorageErrorKind.AccessDenied or StorageErrorKind.Unknown && serverMessage is not null
            && (serverMessage.Contains("cap exceeded", StringComparison.OrdinalIgnoreCase) || serverMessage.Contains("quota", StringComparison.OrdinalIgnoreCase)))
        {
            kind = StorageErrorKind.QuotaExceeded;
        }

        var text = code switch
        {
            "RequestTimeTooSkewed" => "Die Uhr dieses Computers geht falsch – bitte die Uhrzeit stellen",
            "NoSuchBucket" => $"Den Bucket „{bucket}“ gibt es nicht",
            "NoSuchUpload" => "Der Upload ist abgelaufen",
            "EntityTooLarge" => "Die Datei ist zu groß für diesen Speicher",
            _ when kind == StorageErrorKind.Authentication => "Die Anmeldung wurde abgelehnt: Schlüssel-ID oder geheimer Schlüssel stimmt nicht",
            _ => CloudHttp.Describe(kind),
        };
        var detail = code is { Length: > 0 } ? code : $"HTTP {(int)status}";
        return new StorageException(kind, $"{text}{CloudHttp.Where(path)} [{detail}].")
        {
            RetryAfter = kind == StorageErrorKind.RateLimited ? retryAfter : null,
        };
    }

    /// <summary>The hash of an ETag: "md5:…" for objects uploaded in one piece; null for multipart ETags ("…-12").</summary>
    public static string? HashOf(string etag)
    {
        var value = etag.Trim('"');
        if (value.Length != 32 || value.Contains('-') || !value.All(Uri.IsHexDigit))
        {
            return null;
        }

        return "md5:" + value.ToLowerInvariant();
    }

    /// <summary>Times to the second: lists may carry milliseconds, HEAD answers never do (so both give the same time).</summary>
    public static DateTimeOffset Truncate(DateTimeOffset time) =>
        new(time.UtcTicks - time.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private async Task<HttpResponseMessage> SendOnceAsync(S3Settings settings, S3Request request, string path, CancellationToken cancellationToken)
    {
        var (uri, canonicalPath) = settings.Address(request.Key, request.BucketLevel, request.Query);
        var message = new HttpRequestMessage(request.Method, uri) { Content = request.Content?.Invoke() };
        try
        {
            var time = DateTime.UtcNow;
            var signed = new List<KeyValuePair<string, string>>(request.Headers.Count + 3)
            {
                new("host", uri.Authority),
                new("x-amz-date", S3Signer.AmzDate(time)),
                new("x-amz-content-sha256", request.PayloadHash),
            };
            message.Headers.TryAddWithoutValidation("x-amz-date", S3Signer.AmzDate(time));
            message.Headers.TryAddWithoutValidation("x-amz-content-sha256", request.PayloadHash);
            foreach (var header in request.Headers)
            {
                signed.Add(header);
                if (header.Key.Equals("Content-MD5", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    message.Content!.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                else
                {
                    message.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            message.Headers.TryAddWithoutValidation("Authorization", S3Signer.Authorization(settings.AccessKeyId, settings.SecretKey, settings.Region, "s3",
                request.Method.Method, canonicalPath, request.Query, signed, request.PayloadHash, time));

            var response = await CloudHttp.SendAsync(_http, message, request.Completion, request.LimitTime, path, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            using (response)
            {
                throw await ErrorOfAsync(settings, request, response, path, cancellationToken);
            }
        }
        finally
        {
            message.Dispose();
        }
    }

    private static async Task<StorageException> ErrorOfAsync(S3Settings settings, S3Request request, HttpResponseMessage response, string path, CancellationToken cancellationToken)
    {
        string? code = null;
        string? serverMessage = null;
        string? region = null;
        if (request.Method != HttpMethod.Head)
        {
            try
            {
                var root = await ReadXmlAsync(response, path, cancellationToken);
                code = Value(root, "Code");
                serverMessage = Value(root, "Message");
                region = Value(root, "Region");
            }
            catch (Exception exception) when (exception is XmlException or StorageException)
            {
                // no XML body (HEAD, a proxy's HTML page): the status decides
            }
        }

        region ??= Header(response, "x-amz-bucket-region");
        var status = (int)response.StatusCode;
        var regionProblem = status is 301 or 307 || code is "AuthorizationHeaderMalformed" or "PermanentRedirect" or "IncorrectEndpoint"
            || (status == 400 && request.Method == HttpMethod.Head);
        if (regionProblem && !string.IsNullOrEmpty(region) && region != settings.Region)
        {
            return new S3WrongRegionException(region, settings.Bucket);
        }

        if (status is 301 or 307)
        {
            return new StorageException(StorageErrorKind.Authentication,
                $"Der Bucket „{settings.Bucket}“ liegt in einer anderen Region oder unter einer anderen Adresse. Bitte Region und Endpunkt prüfen.");
        }

        return Map(response.StatusCode, code, serverMessage, CloudHttp.RetryAfter(response), path, settings.Bucket);
    }

    private void ThrowIfError(XElement root, string path)
    {
        // CopyObject and CompleteMultipartUpload may answer 200 and still fail
        if (root.Name.LocalName != "Error")
        {
            return;
        }

        var code = Value(root, "Code");
        throw Map(StatusOf(code), code, Value(root, "Message"), null, path, Settings.Bucket);
    }

    private static HttpStatusCode StatusOf(string? code) => code switch
    {
        "AccessDenied" => HttpStatusCode.Forbidden,
        "NoSuchKey" or "NoSuchUpload" or "NoSuchBucket" => HttpStatusCode.NotFound,
        "SlowDown" => HttpStatusCode.ServiceUnavailable,
        _ => HttpStatusCode.InternalServerError,
    };

    private static async Task<XElement> ReadXmlAsync(HttpResponseMessage response, string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = XmlReader.Create(stream, XmlSettings);
            var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
            return document.Root ?? throw new XmlException("Leere Antwort.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw CloudHttp.MapTransport(exception, path);
        }
    }

    private static string? Value(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(element => element.Name.LocalName == name)?.Value;

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values) ? values.FirstOrDefault() : null;

    private static DateTimeOffset ParseTime(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
            ? Truncate(time)
            : DateTimeOffset.UnixEpoch;
}

/// <summary>
/// One S3 call: method, key (or the bucket), query, headers to sign and send, the body (made fresh for every attempt) and its
/// payload hash. <see cref="Repeatable"/>: the body can be made again (the call may be repeated in the right region).
/// </summary>
internal sealed record S3Request(HttpMethod Method, string Key, bool BucketLevel = false)
{
    public IReadOnlyList<KeyValuePair<string, string>> Query { get; init; } = [];

    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];

    public Func<HttpContent>? Content { get; init; }

    public bool Repeatable { get; init; } = true;

    public string PayloadHash { get; init; } = S3Signer.EmptyPayloadHash;

    public HttpCompletionOption Completion { get; init; } = HttpCompletionOption.ResponseContentRead;

    /// <summary>The answer must start within <see cref="CloudHttp.RequestTimeout"/> (not for large bodies).</summary>
    public bool LimitTime { get; init; } = true;
}
