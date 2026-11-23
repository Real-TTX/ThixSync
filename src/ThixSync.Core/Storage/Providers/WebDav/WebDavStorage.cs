using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ThixSync.Core.Models;
using ThixSync.Core.Storage.Providers.Network;

namespace ThixSync.Core.Storage.Providers.WebDav;

/// <summary>Where and how a WebDAV storage talks to its server.</summary>
/// <param name="BaseUri">The top of the server's WebDAV tree (with a trailing '/'): the address of the connection, or {server}/remote.php/dav/files/{user}/.</param>
/// <param name="Root">The root of the storage below <paramref name="BaseUri"/> (base folder + folder of the pair, a relative storage path).</param>
/// <param name="IsOwnCloud">Nextcloud/ownCloud: X-OC-Mtime sets the time, PUT is atomic on the server, oc:checksums and oc:fileid are read.</param>
internal sealed record WebDavOptions(ConnectionKind Kind, Uri BaseUri, string Root, string UserName, string? Password, bool IsOwnCloud)
{
    public int Parallelism { get; init; } = 8;

    public string DisplayName { get; init; } = "";

    /// <summary>The certificate kept with the connection (a self-signed certificate of a NAS, trusted on first use).</summary>
    public string TrustedCertificate { get; init; } = "";

    /// <summary>Keeps what was learned while connecting (the certificate).</summary>
    public Func<string, string, CancellationToken, Task> SaveSetting { get; init; } = (_, _, _) => Task.CompletedTask;
}

/// <summary>
/// A folder on a WebDAV server (also Nextcloud/ownCloud, Synology/QNAP over WebDAV, the Hetzner Storage Box over WebDAV) with
/// HttpClient only. Lists with parallel PROPFIND Depth:1 (Depth:infinity is often switched off), reads with a streamed GET, writes
/// with a streamed PUT of known length to a temporary name and a MOVE onto the real name (Nextcloud/ownCloud: PUT directly – the
/// server itself writes a .part file and renames it – with X-OC-Mtime, which keeps the modification time). Missing parent
/// folders are made with MKCOL, parents first. Every path segment is percent-encoded on its own (spaces, umlauts, '#', '%').
/// </summary>
internal sealed class WebDavStorage : IStorage
{
    private static readonly HttpMethod Propfind = new("PROPFIND");
    private static readonly HttpMethod Mkcol = new("MKCOL");
    private static readonly HttpMethod Move = new("MOVE");

    private readonly HttpClient _http;
    private readonly TlsTrust _trust;
    private readonly AuthenticationHeaderValue? _authorization;
    private readonly ConcurrentDictionary<string, bool> _knownFolders = new(StringComparer.Ordinal);
    private readonly string _root;
    private readonly string _host;
    private Uri _baseUri;

    public WebDavStorage(WebDavOptions options)
    {
        Options = options;
        Kind = options.Kind;
        _baseUri = WithTrailingSlash(options.BaseUri);
        _root = StoragePath.Normalize(options.Root);
        _host = options.BaseUri.Host;
        _trust = new TlsTrust(options.TrustedCertificate, options.SaveSetting);
        _http = HttpStorageClient.Create(options.Parallelism, _trust);
        _authorization = options.UserName.Length > 0 || !string.IsNullOrEmpty(options.Password)
            ? HttpStorageClient.Basic(options.UserName, options.Password ?? "")
            : null;
        DisplayName = options.DisplayName.Length > 0 ? options.DisplayName : new Uri(_baseUri, EscapePath(_root)).ToString();
        Capabilities = new StorageCapabilities
        {
            Features = StorageFeatures.Move | StorageFeatures.RealFolders | StorageFeatures.CoarseTime
                | (options.IsOwnCloud ? StorageFeatures.SetModifiedTime | StorageFeatures.CaseSensitive : 0),
            MaxParallelism = options.Parallelism,
            InvalidNameCharacters = options.IsOwnCloud ? "\\" : "",
        };
    }

    public WebDavOptions Options { get; }

    public ConnectionKind Kind { get; }

    public StorageCapabilities Capabilities { get; }

    public string DisplayName { get; }

    /// <summary>The top of the WebDAV tree in use (Nextcloud: may change to the user id after connecting).</summary>
    internal Uri BaseUri => _baseUri;

    public async Task ConnectAsync(bool createRoot, CancellationToken cancellationToken)
    {
        var root = await PropfindOneAsync(_root, folder: true, cancellationToken);
        if (root is null && Options.IsOwnCloud && await FindOwnCloudUserIdAsync(cancellationToken) is { } userId)
        {
            // signed in with an e-mail address or another login name: the files live below the user id
            _baseUri = OwnCloudFilesUri(_baseUri, userId);
            root = await PropfindOneAsync(_root, folder: true, cancellationToken);
        }

        await _trust.SaveLearnedAsync(cancellationToken);
        if (root is { IsFolder: true })
        {
            _knownFolders[_root] = true;
            return;
        }

        if (root is not null)
        {
            throw new StorageException(StorageErrorKind.NotFound, "Der Ordner ist auf dem Server eine Datei.");
        }

        if (!createRoot)
        {
            throw new StorageException(StorageErrorKind.NotFound, $"Den Ordner {DisplayName} gibt es auf dem Server nicht.");
        }

        await EnsureFolderAsync(_root, cancellationToken);
    }

    public IAsyncEnumerable<StorageEntry> ListAsync(string folder, Action<string, Exception>? onProblem, CancellationToken cancellationToken) =>
        FolderWalker.WalkAsync(StoragePath.Normalize(folder), Options.Parallelism, ListFolderAsync, onProblem, cancellationToken);

    /// <summary>The entries directly in a folder (one PROPFIND Depth:1).</summary>
    internal async Task<IReadOnlyList<StorageEntry>> ListFolderAsync(string folder, CancellationToken cancellationToken)
    {
        var full = Full(folder);
        var url = UrlOf(full, folder: true);
        var selfPath = WebDavMultistatus.PathOfHref(url.AbsoluteUri, url);
        var items = await RequestAsync(Propfind, url, folder, request => PreparePropfind(request, "1"), async (response, token) =>
        {
            await EnsureMultistatusAsync(response, folder);
            await using var body = await response.Content.ReadAsStreamAsync(token);
            return await WebDavMultistatus.ParseAsync(body, url, token);
        }, HttpStorageClient.MetadataTimeout, cancellationToken);

        var entries = new List<StorageEntry>(items.Count);
        foreach (var item in items)
        {
            if (WebDavMultistatus.SamePath(item.Path, selfPath) || item.Path.Length == 0 || ServerPaths.IsHiddenName(item.Name))
            {
                continue;
            }

            var path = ServerPaths.Child(folder, item.Name);
            if (item.IsFolder)
            {
                _knownFolders[Full(path)] = true;
            }

            entries.Add(ToEntry(path, item));
        }

        return entries;
    }

    public async Task<StorageEntry?> GetAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        var item = await PropfindOneAsync(Full(normalized), folder: false, cancellationToken);
        return item is null ? null : ToEntry(normalized, item);
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        var url = UrlOf(Full(normalized), folder: false);
        HttpResponseMessage? response = null;
        try
        {
            using var request = NewRequest(HttpMethod.Get, url);
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw HttpStorageClient.Map(response, normalized);
            }

            var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            var owned = response;
            response = null;
            return new OwnedReadStream(body, _ =>
            {
                owned.Dispose();
                return ValueTask.CompletedTask;
            });
        }
        catch (Exception exception)
        {
            response?.Dispose();
            throw HttpStorageClient.Map(exception, _host, cancellationToken, _trust);
        }
    }

    public async Task<StorageEntry> WriteAsync(string path, Stream content, long length, DateTimeOffset modified, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.Length == 0)
        {
            throw new StorageException(StorageErrorKind.InvalidName, "Ohne Namen kann keine Datei geschrieben werden.");
        }

        var target = Full(normalized);
        await EnsureFolderAsync(StoragePath.ParentOf(target), cancellationToken);
        var upload = Options.IsOwnCloud ? target : ServerPaths.TempPathFor(target);
        var mtimeAccepted = false;
        string? etag = null;
        string? fileId = null;
        try
        {
            await RequestAsync(HttpMethod.Put, UrlOf(upload, folder: false), normalized, request =>
            {
                request.Content = new StreamingContent(content, length, progress);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                request.Headers.TryAddWithoutValidation("X-OC-Mtime", HttpStorageClient.UnixSeconds(modified));
            }, (response, _) =>
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw HttpStorageClient.Map(response, normalized, "name");
                }

                mtimeAccepted = response.Headers.TryGetValues("X-OC-MTime", out var values) && values.Any(value => value.Equals("accepted", StringComparison.OrdinalIgnoreCase));
                etag = response.Headers.ETag?.Tag.Trim('"') ?? HeaderOf(response, "OC-ETag")?.Trim('"');
                fileId = HeaderOf(response, "OC-FileId");
                return Task.FromResult(true);
            }, null, cancellationToken);

            if (!Options.IsOwnCloud)
            {
                await MoveFullAsync(upload, target, normalized, cancellationToken);
            }
        }
        catch
        {
            if (!Options.IsOwnCloud)
            {
                await TryDeleteAsync(upload);
            }

            throw;
        }

        if (Options.IsOwnCloud && mtimeAccepted)
        {
            return new StorageEntry(normalized, false, length, DateTimeOffset.FromUnixTimeSeconds(modified.ToUnixTimeSeconds()))
            {
                NativeId = fileId,
                IsHidden = StoragePath.NameOf(normalized).StartsWith('.'),
            };
        }

        var written = await PropfindOneAsync(target, folder: false, cancellationToken)
            ?? throw new StorageException(StorageErrorKind.Transient, $"Die Datei ist nach dem Hochladen nicht zu sehen ({normalized}).");
        return ToEntry(normalized, written with { ETag = written.ETag ?? etag });
    }

    public Task CreateFolderAsync(string path, CancellationToken cancellationToken) =>
        EnsureFolderAsync(Full(StoragePath.Normalize(path)), cancellationToken);

    public async Task DeleteAsync(string path, bool isFolder, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.Length == 0)
        {
            throw new StorageException(StorageErrorKind.AccessDenied, "Der Wurzelordner eines Auftrags wird nie gelöscht.");
        }

        var full = Full(normalized);
        await RequestAsync(HttpMethod.Delete, UrlOf(full, isFolder), normalized, null, (response, _) =>
        {
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            {
                throw HttpStorageClient.Map(response, normalized);
            }

            return Task.FromResult(true);
        }, HttpStorageClient.MetadataTimeout, cancellationToken);

        if (isFolder)
        {
            foreach (var known in _knownFolders.Keys.Where(known => StoragePath.IsInside(known, full)))
            {
                _knownFolders.TryRemove(known, out _);
            }
        }
    }

    public async Task MoveAsync(string fromPath, string toPath, CancellationToken cancellationToken)
    {
        var from = Full(StoragePath.Normalize(fromPath));
        var to = Full(StoragePath.Normalize(toPath));
        await EnsureFolderAsync(StoragePath.ParentOf(to), cancellationToken);
        await MoveFullAsync(from, to, StoragePath.Normalize(fromPath), cancellationToken);
        foreach (var known in _knownFolders.Keys.Where(known => StoragePath.IsInside(known, from)))
        {
            _knownFolders.TryRemove(known, out _);
        }
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>The address of a path below the top of the tree: every segment percent-encoded on its own.</summary>
    internal Uri UrlOf(string fullPath, bool folder)
    {
        var escaped = EscapePath(fullPath);
        return new Uri(_baseUri.AbsoluteUri + escaped + (folder && escaped.Length > 0 ? "/" : ""));
    }

    /// <summary>"a b/ü#1" → "a%20b/%C3%BC%231".</summary>
    internal static string EscapePath(string path) =>
        path.Length == 0 ? "" : string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    /// <summary>{server}/remote.php/dav/files/{user}/ of a Nextcloud/ownCloud server (the address may be the server or an old WebDAV address).</summary>
    internal static Uri OwnCloudFilesUri(Uri server, string user)
    {
        var top = server.GetLeftPart(UriPartial.Authority) + OwnCloudServerPath(server);
        return new Uri($"{top}/remote.php/dav/files/{Uri.EscapeDataString(user)}/");
    }

    private static string OwnCloudServerPath(Uri server)
    {
        var path = server.AbsolutePath;
        var index = path.IndexOf("/remote.php", StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            path = path[..index];
        }

        return path.TrimEnd('/');
    }

    private string Full(string path) => StoragePath.Combine(_root, path);

    private static Uri WithTrailingSlash(Uri uri) => uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");

    private static string? HeaderOf(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>The path below the root for messages (a parent of the root stays as it is).</summary>
    private string RelativeOf(string full) => StoragePath.IsInside(full, _root) ? StoragePath.RelativeTo(full, _root) : full;

    private StorageEntry ToEntry(string path, WebDavItem item) =>
        new(path, item.IsFolder, item.Size, item.Modified)
        {
            Hash = item.Hash,
            NativeId = item.FileId,
            IsHidden = StoragePath.NameOf(path).StartsWith('.'),
        };

    /// <summary>One item (PROPFIND Depth:0), or null when nothing is there. Retries with a trailing '/' when the server redirects a folder.</summary>
    private async Task<WebDavItem?> PropfindOneAsync(string full, bool folder, CancellationToken cancellationToken)
    {
        var relative = RelativeOf(full);
        var url = UrlOf(full, folder);
        var (status, item) = await RequestAsync(Propfind, url, relative, request => PreparePropfind(request, "0"), async (response, token) =>
        {
            if (response.StatusCode == HttpStatusCode.NotFound || (int)response.StatusCode is >= 300 and < 400)
            {
                return (response.StatusCode, (WebDavItem?)null);
            }

            await EnsureMultistatusAsync(response, relative);
            await using var body = await response.Content.ReadAsStreamAsync(token);
            var items = await WebDavMultistatus.ParseAsync(body, url, token);
            return (response.StatusCode, items.FirstOrDefault());
        }, HttpStorageClient.MetadataTimeout, cancellationToken);

        if ((int)status is >= 300 and < 400 && !folder && full.Length > 0)
        {
            return await PropfindOneAsync(full, folder: true, cancellationToken);
        }

        if ((int)status is >= 300 and < 400)
        {
            throw new StorageException(StorageErrorKind.Unknown, $"Der Server leitet auf eine andere Adresse um (HTTP {(int)status}) – stimmt die Adresse der Verbindung?");
        }

        return item;
    }

    private static void PreparePropfind(HttpRequestMessage request, string depth)
    {
        request.Headers.TryAddWithoutValidation("Depth", depth);
        request.Content = new StringContent(WebDavMultistatus.PropfindBody(ownCloud: true), Encoding.UTF8, "application/xml");
    }

    private static async Task EnsureMultistatusAsync(HttpResponseMessage response, string path)
    {
        if (response.StatusCode == HttpStatusCode.MultiStatus)
        {
            return;
        }

        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.MethodNotAllowed)
        {
            await response.Content.LoadIntoBufferAsync();
            throw new StorageException(StorageErrorKind.Unknown, "Der Server antwortet nicht wie ein WebDAV-Server – stimmt die Adresse?");
        }

        throw HttpStorageClient.Map(response, path);
    }

    /// <summary>Makes a folder and its missing parents (MKCOL, parents first; known folders are not asked again).</summary>
    private async Task EnsureFolderAsync(string full, CancellationToken cancellationToken)
    {
        if (full.Length == 0 || _knownFolders.ContainsKey(full))
        {
            return;
        }

        var status = await MkcolAsync(full, cancellationToken);
        if (status == HttpStatusCode.Conflict)
        {
            await EnsureFolderAsync(StoragePath.ParentOf(full), cancellationToken);
            status = await MkcolAsync(full, cancellationToken);
        }

        if (status is HttpStatusCode.Created or HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.MethodNotAllowed)
        {
            _knownFolders[full] = true;
            return;
        }

        throw new StorageException(status == HttpStatusCode.Conflict ? StorageErrorKind.NotFound : StorageErrorKind.Unknown,
            $"Der Ordner konnte nicht angelegt werden (HTTP {(int)status}, {RelativeOf(full)}).");
    }

    private Task<HttpStatusCode> MkcolAsync(string full, CancellationToken cancellationToken) =>
        RequestAsync(Mkcol, UrlOf(full, folder: true), RelativeOf(full), null, (response, _) =>
        {
            // 405: it is there already; 409: a parent is missing
            if (response.IsSuccessStatusCode || response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.Conflict)
            {
                return Task.FromResult(response.StatusCode);
            }

            throw HttpStorageClient.Map(response, RelativeOf(full), "name");
        }, HttpStorageClient.MetadataTimeout, cancellationToken);

    private async Task MoveFullAsync(string fromFull, string toFull, string path, CancellationToken cancellationToken)
    {
        var status = await MoveOnceAsync(fromFull, toFull, folder: false, path, cancellationToken);
        if ((int)status is >= 300 and < 400)
        {
            status = await MoveOnceAsync(fromFull, toFull, folder: true, path, cancellationToken);
        }

        if ((int)status is >= 300 and < 400)
        {
            throw new StorageException(StorageErrorKind.Unknown, $"Der Server leitet das Verschieben um (HTTP {(int)status}, {path}).");
        }
    }

    private Task<HttpStatusCode> MoveOnceAsync(string fromFull, string toFull, bool folder, string path, CancellationToken cancellationToken) =>
        RequestAsync(Move, UrlOf(fromFull, folder), path, request =>
        {
            request.Headers.TryAddWithoutValidation("Destination", UrlOf(toFull, folder).AbsoluteUri);
            request.Headers.TryAddWithoutValidation("Overwrite", "T");
        }, (response, _) =>
        {
            if (response.IsSuccessStatusCode || (int)response.StatusCode is >= 300 and < 400)
            {
                return Task.FromResult(response.StatusCode);
            }

            throw HttpStorageClient.Map(response, path, "name");
        }, HttpStorageClient.MetadataTimeout, cancellationToken);

    private async Task TryDeleteAsync(string full)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var request = NewRequest(HttpMethod.Delete, UrlOf(full, folder: false));
            using var _ = await _http.SendAsync(request, timeout.Token);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            // the temporary file stays; its name starts with the temp prefix, so no listing shows it
        }
    }

    /// <summary>The user id of the signed-in user (Nextcloud/ownCloud OCS), or null.</summary>
    private async Task<string?> FindOwnCloudUserIdAsync(CancellationToken cancellationToken)
    {
        var url = new Uri(_baseUri.GetLeftPart(UriPartial.Authority) + OwnCloudServerPath(_baseUri) + "/ocs/v2.php/cloud/user?format=json");
        return await RequestAsync(HttpMethod.Get, url, "", request => request.Headers.TryAddWithoutValidation("OCS-APIRequest", "true"), async (response, token) =>
        {
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            try
            {
                await using var body = await response.Content.ReadAsStreamAsync(token);
                using var json = await JsonDocument.ParseAsync(body, cancellationToken: token);
                var id = json.RootElement.GetProperty("ocs").GetProperty("data").GetProperty("id").GetString();
                return string.IsNullOrEmpty(id) ? null : id;
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return null;
            }
        }, HttpStorageClient.MetadataTimeout, cancellationToken);
    }

    private HttpRequestMessage NewRequest(HttpMethod method, Uri url)
    {
        var request = new HttpRequestMessage(method, url);
        if (_authorization is not null)
        {
            request.Headers.Authorization = _authorization;
        }

        return request;
    }

    /// <summary>Sends one request and hands the answer to <paramref name="handle"/>; transport errors become <see cref="StorageException"/>.</summary>
    private async Task<T> RequestAsync<T>(HttpMethod method, Uri url, string path, Action<HttpRequestMessage>? prepare,
        Func<HttpResponseMessage, CancellationToken, Task<T>> handle, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } time)
        {
            limit.CancelAfter(time);
        }

        try
        {
            using var request = NewRequest(method, url);
            prepare?.Invoke(request);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            return await handle(response, limit.Token);
        }
        catch (Exception exception) when (exception is not StorageException)
        {
            throw HttpStorageClient.Map(exception, _host, cancellationToken, _trust);
        }
    }
}
