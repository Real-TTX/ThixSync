using System.Globalization;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ThixSync.Core.Models;
using ThixSync.Core.Storage.Providers.S3;

namespace ThixSync.Core.Storage.Providers.Azure;

/// <summary>A blob as Azure lists it (name, size, time to the second, Content-MD5 in base64, a directory of an HNS account).</summary>
internal sealed record AzureBlob(string Name, long Size, DateTimeOffset Modified, string? ContentMd5, bool IsFolder)
{
    /// <summary>The state of a copy into this blob (HEAD only: "success", "pending", "failed", "aborted").</summary>
    public string? CopyStatus { get; init; }
}

internal sealed record AzureListPage(IReadOnlyList<AzureBlob> Blobs, IReadOnlyList<string> Prefixes, string? NextMarker);

/// <summary>
/// The REST calls of Azure Blob Storage for one container: signed with Shared Key (account key) or authorized by a SAS token
/// in the query. Errors become <see cref="StorageException"/>. Safe for many calls at once (one pooled HttpClient).
/// </summary>
internal sealed class AzureBlobClient : IDisposable
{
    public const string ApiVersion = "2023-11-03";

    private static readonly XmlReaderSettings XmlSettings = new()
    {
        Async = true,
        CheckCharacters = false,
        DtdProcessing = DtdProcessing.Prohibit,
        IgnoreWhitespace = true,
    };

    private readonly HttpClient _http;
    private readonly byte[]? _key;
    private readonly string? _sas;
    private readonly string _baseAddress;

    public AzureBlobClient(StorageContext context, HttpMessageHandler? handler)
    {
        var connection = context.Connection;
        Account = connection.Get(ConnectionFields.AccountName).Trim();
        Container = connection.Get(ConnectionFields.Container).Trim();
        if (Account.Length == 0 || Container.Length == 0)
        {
            throw new StorageException(StorageErrorKind.Unknown, "Speicherkonto und Container müssen angegeben sein.");
        }

        var secret = context.Secret?.Trim() ?? "";
        if (secret.Length == 0)
        {
            throw new StorageException(StorageErrorKind.Authentication, "Der Kontoschlüssel oder das SAS-Token fehlt.");
        }

        if (ConnectionCatalog.Value(connection, ConnectionFields.AuthMethod) == "sas")
        {
            // a pasted SAS URL works as well as the bare token
            var question = secret.IndexOf('?');
            _sas = question >= 0 ? secret[(question + 1)..] : secret;
        }
        else
        {
            try
            {
                _key = Convert.FromBase64String(secret);
            }
            catch (FormatException exception)
            {
                throw new StorageException(StorageErrorKind.Authentication, "Der Kontoschlüssel ist ungültig (Base64 erwartet).", exception);
            }
        }

        var endpoint = connection.Get(ConnectionFields.Endpoint).Trim();
        if (endpoint.Length == 0)
        {
            endpoint = $"https://{Account}.blob.core.windows.net";
        }
        else if (!endpoint.Contains("://", StringComparison.Ordinal))
        {
            endpoint = "https://" + endpoint;
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) || (endpointUri.Scheme != Uri.UriSchemeHttps && endpointUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new StorageException(StorageErrorKind.Unknown, "Der Endpunkt ist keine gültige Adresse (https://…).");
        }

        _baseAddress = $"{endpointUri.GetLeftPart(UriPartial.Path).TrimEnd('/')}/{CloudHttp.UriEncode(Container, false)}";
        _http = CloudHttp.CreateClient(handler, 64);
    }

    public string Account { get; }

    public string Container { get; }

    public void Dispose() => _http.Dispose();

    // ---- Calls ------------------------------------------------------------------------------------------------------

    /// <summary>List Blobs (flat, or by <paramref name="delimiter"/>) with metadata (directories of HNS accounts).</summary>
    public async Task<AzureListPage> ListPageAsync(string prefix, string? delimiter, string? marker, int? maxResults, string path, CancellationToken cancellationToken)
    {
        var query = new List<KeyValuePair<string, string>> { new("restype", "container"), new("comp", "list"), new("include", "metadata") };
        if (prefix.Length > 0)
        {
            query.Add(new("prefix", prefix));
        }

        if (delimiter is not null)
        {
            query.Add(new("delimiter", delimiter));
        }

        if (marker is not null)
        {
            query.Add(new("marker", marker));
        }

        query.Add(new("maxresults", (maxResults ?? 5000).ToString(CultureInfo.InvariantCulture)));
        using var response = await SendAsync(new AzureRequest(HttpMethod.Get, null) { Query = query }, path, cancellationToken);
        var root = await ReadXmlAsync(response, path, cancellationToken);
        var blobs = new List<AzureBlob>();
        var prefixes = new List<string>();
        var container = Child(root, "Blobs");
        foreach (var element in container?.Elements() ?? [])
        {
            var name = NameOf(element);
            if (element.Name.LocalName == "BlobPrefix")
            {
                prefixes.Add(name);
                continue;
            }

            if (element.Name.LocalName != "Blob")
            {
                continue;
            }

            var properties = Child(element, "Properties");
            var metadata = Child(element, "Metadata");
            blobs.Add(new AzureBlob(
                name,
                long.TryParse(Value(properties, "Content-Length"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : 0,
                ParseTime(Value(properties, "Last-Modified")),
                NullIfEmpty(Value(properties, "Content-MD5")),
                string.Equals(Value(metadata, "hdi_isfolder"), "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Value(properties, "ResourceType"), "directory", StringComparison.OrdinalIgnoreCase)));
        }

        return new AzureListPage(blobs, prefixes, NullIfEmpty(Value(root, "NextMarker")));
    }

    public async IAsyncEnumerable<AzureBlob> ListAllAsync(string prefix, string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? marker = null;
        do
        {
            var page = await ListPageAsync(prefix, null, marker, null, path, cancellationToken);
            foreach (var blob in page.Blobs)
            {
                yield return blob;
            }

            marker = page.NextMarker;
        }
        while (marker is not null);
    }

    /// <summary>Get Blob Properties; null when there is no such blob.</summary>
    public async Task<AzureBlob?> HeadAsync(string name, string path, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(new AzureRequest(HttpMethod.Head, name), path, cancellationToken);
            return BlobOf(name, response);
        }
        catch (StorageException exception) when (exception.Kind == StorageErrorKind.NotFound)
        {
            return null;
        }
    }

    public async Task<Stream> OpenReadAsync(string name, string path, CancellationToken cancellationToken)
    {
        var response = await SendAsync(new AzureRequest(HttpMethod.Get, name) { Completion = HttpCompletionOption.ResponseHeadersRead }, path, cancellationToken);
        return await ResponseStream.OpenAsync(response, path, cancellationToken);
    }

    /// <summary>Put Blob (a block blob in one request); returns the blob as stored (time, Content-MD5 the service computed).</summary>
    public async Task<AzureBlob> PutBlobAsync(string name, Func<HttpContent> content, long length, IReadOnlyList<KeyValuePair<string, string>> headers, string path,
        CancellationToken cancellationToken)
    {
        var request = new AzureRequest(HttpMethod.Put, name)
        {
            Headers = [new("x-ms-blob-type", "BlockBlob"), .. headers],
            Content = content,
            LimitTime = length < 1024 * 1024,
        };
        using var response = await SendAsync(request, path, cancellationToken);
        return BlobOf(name, response) with { Size = length };
    }

    public async Task PutBlockAsync(string name, string blockId, Func<HttpContent> content, string path, CancellationToken cancellationToken)
    {
        var request = new AzureRequest(HttpMethod.Put, name)
        {
            Query = [new("comp", "block"), new("blockid", blockId)],
            Content = content,
            LimitTime = false,
        };
        using var _ = await SendAsync(request, path, cancellationToken);
    }

    /// <summary>Put Block List (commits the blocks in order); returns the blob as stored.</summary>
    public async Task<AzureBlob> PutBlockListAsync(string name, IReadOnlyList<string> blockIds, long length, IReadOnlyList<KeyValuePair<string, string>> headers, string path,
        CancellationToken cancellationToken)
    {
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("BlockList", blockIds.Select(id => new XElement("Latest", id))));
        var body = Encoding.UTF8.GetBytes(document.Declaration + document.ToString(SaveOptions.DisableFormatting));
        var request = new AzureRequest(HttpMethod.Put, name)
        {
            Query = [new("comp", "blocklist")],
            Headers = [new("Content-Type", "application/xml"), .. headers],
            Content = () => new ByteArrayContent(body),
            LimitTime = false,
        };
        using var response = await SendAsync(request, path, cancellationToken);
        return BlobOf(name, response) with { Size = length };
    }

    public async Task DeleteAsync(string name, string path, CancellationToken cancellationToken)
    {
        try
        {
            using var _ = await SendAsync(new AzureRequest(HttpMethod.Delete, name) { Headers = [new("x-ms-delete-snapshots", "include")] }, path, cancellationToken);
        }
        catch (StorageException exception) when (exception.Kind == StorageErrorKind.NotFound)
        {
        }
    }

    /// <summary>Copy Blob inside the account; waits until the copy is done (inside one account it usually is at once).</summary>
    public async Task CopyAsync(string sourceName, string targetName, string path, CancellationToken cancellationToken)
    {
        var source = UrlOf(sourceName, []).AbsoluteUri;
        var request = new AzureRequest(HttpMethod.Put, targetName)
        {
            Headers = [new("x-ms-copy-source", source)],
            Content = () => new ByteArrayContent([]),
        };
        string? status;
        using (var response = await SendAsync(request, path, cancellationToken))
        {
            status = Header(response, "x-ms-copy-status");
        }

        while (status == "pending")
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            status = (await HeadAsync(targetName, path, cancellationToken))?.CopyStatus;
        }

        if (status is "failed" or "aborted")
        {
            throw new StorageException(StorageErrorKind.Transient, $"Das Kopieren im Speicherkonto ist fehlgeschlagen{CloudHttp.Where(path)}.");
        }
    }

    // ---- Sending ----------------------------------------------------------------------------------------------------

    public async Task<HttpResponseMessage> SendAsync(AzureRequest request, string path, CancellationToken cancellationToken)
    {
        var uri = UrlOf(request.Blob, request.Query);
        using var message = new HttpRequestMessage(request.Method, uri) { Content = request.Content?.Invoke() };
        var date = DateTimeOffset.UtcNow.ToString("R", CultureInfo.InvariantCulture);
        var headers = new List<KeyValuePair<string, string>>(request.Headers.Count + 3) { new("x-ms-date", date), new("x-ms-version", ApiVersion) };
        headers.AddRange(request.Headers);
        foreach (var (name, value) in headers)
        {
            if (name.Equals("Content-MD5", StringComparison.OrdinalIgnoreCase) || name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                message.Content!.Headers.TryAddWithoutValidation(name, value);
            }
            else
            {
                message.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (_key is not null)
        {
            if (message.Content?.Headers.ContentLength is { } length)
            {
                headers.Add(new("Content-Length", length.ToString(CultureInfo.InvariantCulture)));
            }

            message.Headers.TryAddWithoutValidation("Authorization",
                AzureSharedKey.Authorization(Account, _key, AzureSharedKey.StringToSign(request.Method.Method, Account, uri, headers)));
        }

        var response = await CloudHttp.SendAsync(_http, message, request.Completion, request.LimitTime, path, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            throw await ErrorOfAsync(request, response, path, cancellationToken);
        }
    }

    /// <summary>Maps an Azure error code (or the status) to a German <see cref="StorageException"/>.</summary>
    public StorageException Map(HttpStatusCode status, string? code, TimeSpan? retryAfter, string path)
    {
        var kind = code switch
        {
            "AuthenticationFailed" or "InvalidAuthenticationInfo" or "NoAuthenticationInformation" or "AccountIsDisabled" => StorageErrorKind.Authentication,
            "AuthorizationFailure" or "AuthorizationPermissionMismatch" or "AuthorizationResourceTypeMismatch" or "AuthorizationServiceMismatch"
                or "AuthorizationProtocolMismatch" or "AuthorizationSourceIPMismatch" or "InsufficientAccountPermissions" or "BlobImmutableDueToPolicy"
                => StorageErrorKind.AccessDenied,
            "ContainerNotFound" or "BlobNotFound" or "ResourceNotFound" => StorageErrorKind.NotFound,
            "ServerBusy" or "TooManyRequests" or "IngressOverAccountLimit" or "EgressOverAccountLimit" => StorageErrorKind.RateLimited,
            "InternalError" or "OperationTimedOut" or "ContainerBeingDeleted" => StorageErrorKind.Transient,
            "InvalidResourceName" or "InvalidUri" => StorageErrorKind.InvalidName,
            "LeaseIdMissing" or "LeaseAlreadyPresent" or "LeaseIdMismatchWithBlobOperation" => StorageErrorKind.Locked,
            _ when code?.Contains("Quota", StringComparison.OrdinalIgnoreCase) == true => StorageErrorKind.QuotaExceeded,
            _ => CloudHttp.KindOf(status),
        };
        var text = code switch
        {
            "ContainerNotFound" => $"Den Container „{Container}“ gibt es nicht",
            "AuthenticationFailed" when _sas is not null => "Das SAS-Token wurde abgelehnt (abgelaufen oder ungültig)",
            "AuthenticationFailed" => "Die Anmeldung wurde abgelehnt: Speicherkonto oder Kontoschlüssel stimmt nicht",
            "AuthorizationPermissionMismatch" when _sas is not null => "Das SAS-Token erlaubt das nicht (Berechtigungen prüfen)",
            _ => CloudHttp.Describe(kind),
        };
        var detail = code is { Length: > 0 } ? code : $"HTTP {(int)status}";
        return new StorageException(kind, $"{text}{CloudHttp.Where(path)} [{detail}].")
        {
            RetryAfter = kind == StorageErrorKind.RateLimited ? retryAfter : null,
        };
    }

    /// <summary>"md5:…" (hex) from a Content-MD5 in base64; null when there is none.</summary>
    public static string? HashOf(string? contentMd5)
    {
        if (string.IsNullOrEmpty(contentMd5))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(contentMd5);
            return bytes.Length == 16 ? "md5:" + Convert.ToHexStringLower(bytes) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private Uri UrlOf(string? blob, IReadOnlyList<KeyValuePair<string, string>> query)
    {
        var address = blob is null ? _baseAddress : $"{_baseAddress}/{CloudHttp.UriEncode(blob, true)}";
        var parts = new List<string>(2);
        if (query.Count > 0)
        {
            parts.Add(CloudHttp.Query(query));
        }

        if (_sas is { Length: > 0 })
        {
            parts.Add(_sas);
        }

        return new Uri(parts.Count == 0 ? address : $"{address}?{string.Join('&', parts)}");
    }

    private async Task<StorageException> ErrorOfAsync(AzureRequest request, HttpResponseMessage response, string path, CancellationToken cancellationToken)
    {
        var code = Header(response, "x-ms-error-code");
        if (code is null && request.Method != HttpMethod.Head)
        {
            try
            {
                var root = await ReadXmlAsync(response, path, cancellationToken);
                code = Value(root, "Code");
            }
            catch (Exception exception) when (exception is XmlException or StorageException)
            {
                // no XML body: the status decides
            }
        }

        return Map(response.StatusCode, code, CloudHttp.RetryAfter(response), path);
    }

    private static AzureBlob BlobOf(string name, HttpResponseMessage response) => new(
        name,
        response.Content.Headers.ContentLength ?? 0,
        Truncate(response.Content.Headers.LastModified ?? DateTimeOffset.UtcNow),
        Header(response, "Content-MD5"),
        string.Equals(Header(response, "x-ms-meta-hdi_isfolder"), "true", StringComparison.OrdinalIgnoreCase))
    {
        CopyStatus = Header(response, "x-ms-copy-status"),
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

    /// <summary>A blob name; names with characters XML cannot carry come URL-encoded (Encoded="true").</summary>
    private static string NameOf(XElement element)
    {
        var name = Child(element, "Name");
        if (name is null)
        {
            return "";
        }

        return string.Equals(name.Attribute("Encoded")?.Value, "true", StringComparison.OrdinalIgnoreCase) ? Uri.UnescapeDataString(name.Value) : name.Value;
    }

    private static XElement? Child(XElement? parent, string name) => parent?.Elements().FirstOrDefault(element => element.Name.LocalName == name);

    private static string? Value(XElement? parent, string name) => Child(parent, name)?.Value;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values) ? values.FirstOrDefault() : null;

    private static DateTimeOffset ParseTime(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
            ? Truncate(time)
            : DateTimeOffset.UnixEpoch;

    private static DateTimeOffset Truncate(DateTimeOffset time) => S3Client.Truncate(time);
}

/// <summary>One Azure call: method, blob (null: the container), query, headers, a body made fresh per attempt.</summary>
internal sealed record AzureRequest(HttpMethod Method, string? Blob)
{
    public IReadOnlyList<KeyValuePair<string, string>> Query { get; init; } = [];

    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];

    public Func<HttpContent>? Content { get; init; }

    public HttpCompletionOption Completion { get; init; } = HttpCompletionOption.ResponseContentRead;

    public bool LimitTime { get; init; } = true;
}
