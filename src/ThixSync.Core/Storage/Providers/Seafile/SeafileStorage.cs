using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ThixSync.Core.Models;
using ThixSync.Core.Storage.Providers.Network;

namespace ThixSync.Core.Storage.Providers.Seafile;

/// <summary>Where a Seafile storage is: the server, the sign-in, the library and the root inside it.</summary>
internal sealed record SeafileOptions(Uri Server, string UserName, string? Password, string Library, string Root)
{
    public int Parallelism { get; init; } = 4;

    /// <summary>The certificate kept with the connection (a self-signed certificate, trusted on first use).</summary>
    public string TrustedCertificate { get; init; } = "";

    /// <summary>Keeps what was learned while connecting (the certificate).</summary>
    public Func<string, string, CancellationToken, Task> SaveSetting { get; init; } = (_, _, _) => Task.CompletedTask;
}

/// <summary>
/// A folder in a library of a Seafile server, through the REST API (api2): signs in with user name and password for a token
/// (kept in memory only, renewed once when the server refuses it), finds the library by its name, lists the whole tree with one
/// recursive request (older servers: folder by folder), downloads through a download link, uploads through an upload link
/// (multipart, streamed, replace=1 – the server commits the new version atomically). Folders are made one level at a time and never
/// twice at once (Seafile would make "Ordner (1)"). The modification time is the server's (no SetModifiedTime).
/// </summary>
internal sealed class SeafileStorage : IStorage
{
    private readonly SeafileOptions _options;
    private readonly HttpClient _http;
    private readonly TlsTrust _trust;
    private readonly string _root;
    private readonly SemaphoreSlim _signIn = new(1, 1);
    private readonly SemaphoreSlim _folderGate = new(1, 1);
    private readonly ConcurrentDictionary<string, bool> _knownFolders = new(StringComparer.Ordinal);
    private string? _token;
    private string _libraryId = "";

    public SeafileStorage(SeafileOptions options)
    {
        _options = options;
        _root = StoragePath.Normalize(options.Root);
        _trust = new TlsTrust(options.TrustedCertificate, options.SaveSetting);
        _http = HttpStorageClient.Create(options.Parallelism, _trust);
        DisplayName = $"{options.Server.Host} › {options.Library}" + (_root.Length > 0 ? "/" + _root : "");
        Capabilities = new StorageCapabilities
        {
            Features = StorageFeatures.Move | StorageFeatures.RealFolders | StorageFeatures.CaseSensitive | StorageFeatures.CoarseTime,
            MaxParallelism = options.Parallelism,
            InvalidNameCharacters = "\\/",
        };
    }

    public ConnectionKind Kind => ConnectionKind.Seafile;

    public StorageCapabilities Capabilities { get; }

    public string DisplayName { get; }

    /// <summary>The id of the library (after connecting).</summary>
    internal string LibraryId => _libraryId;

    public async Task ConnectAsync(bool createRoot, CancellationToken cancellationToken)
    {
        await SignInAsync(null, cancellationToken);
        await _trust.SaveLearnedAsync(cancellationToken);
        _libraryId = await FindLibraryAsync(cancellationToken);
        _knownFolders[""] = true;
        if (_root.Length == 0)
        {
            return;
        }

        if (await FolderExistsAsync(_root, cancellationToken))
        {
            _knownFolders[_root] = true;
            return;
        }

        if (!createRoot)
        {
            throw new StorageException(StorageErrorKind.NotFound, $"Den Ordner „{_root}“ gibt es in der Bibliothek „{_options.Library}“ nicht.");
        }

        await EnsureFolderAsync(_root, cancellationToken);
    }

    public async IAsyncEnumerable<StorageEntry> ListAsync(string folder, Action<string, Exception>? onProblem, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var start = StoragePath.Normalize(folder);
        var recursive = await TryListRecursiveAsync(start, cancellationToken);
        if (recursive is not null)
        {
            foreach (var entry in recursive)
            {
                yield return entry;
            }

            yield break;
        }

        await foreach (var entry in FolderWalker.WalkAsync(start, _options.Parallelism, ListFolderAsync, onProblem, cancellationToken))
        {
            yield return entry;
        }
    }

    /// <summary>The entries directly in a folder.</summary>
    internal async Task<IReadOnlyList<StorageEntry>> ListFolderAsync(string folder, CancellationToken cancellationToken)
    {
        var full = Full(folder);
        using var json = await GetJsonAsync($"api2/repos/{_libraryId}/dir/?p={Param(full)}", folder, cancellationToken);
        var entries = new List<StorageEntry>();
        foreach (var item in json.RootElement.EnumerateArray())
        {
            if (ToEntry(folder, item) is { } entry)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    public async Task<StorageEntry?> GetAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.Length == 0)
        {
            return new StorageEntry("", true, 0, DateTimeOffset.UnixEpoch);
        }

        var full = Full(normalized);
        var file = await TryGetJsonAsync($"api2/repos/{_libraryId}/file/detail/?p={Param(full)}", normalized, cancellationToken);
        if (file is not null)
        {
            using (file)
            {
                return new StorageEntry(normalized, false, Int64Of(file.RootElement, "size"), TimeOf(file.RootElement))
                {
                    IsHidden = StoragePath.NameOf(normalized).StartsWith('.'),
                };
            }
        }

        var parent = StoragePath.ParentOf(normalized);
        var folders = await TryGetJsonAsync($"api2/repos/{_libraryId}/dir/?p={Param(StoragePath.ParentOf(full))}&t=d", normalized, cancellationToken);
        if (folders is null)
        {
            return null;
        }

        using (folders)
        {
            var name = StoragePath.NameOf(normalized);
            foreach (var item in folders.RootElement.EnumerateArray())
            {
                if (StringOf(item, "name") == name)
                {
                    return ToEntry(parent, item);
                }
            }
        }

        return null;
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        var link = await GetLinkAsync($"api2/repos/{_libraryId}/file/?p={Param(Full(normalized))}&reuse=1", normalized, cancellationToken);
        HttpResponseMessage? response = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, link);
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw Map(response, normalized);
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
            throw HttpStorageClient.Map(exception, _options.Server.Host, cancellationToken, _trust);
        }
    }

    public async Task<StorageEntry> WriteAsync(string path, Stream content, long length, DateTimeOffset modified, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.Length == 0)
        {
            throw new StorageException(StorageErrorKind.InvalidName, "Ohne Namen kann keine Datei geschrieben werden.");
        }

        var full = Full(normalized);
        var parent = StoragePath.ParentOf(full);
        await EnsureFolderAsync(parent, cancellationToken);
        var link = await GetLinkAsync($"api2/repos/{_libraryId}/upload-link/?p={Param(parent)}", normalized, cancellationToken);
        var upload = new Uri(link + (link.Contains('?') ? "&" : "?") + "ret-json=1");
        try
        {
            using var form = new MultipartFormDataContent("----thixsync" + Guid.NewGuid().ToString("N"));
            form.HeaderEncodingSelector = (_, _) => Encoding.UTF8;
            form.Add(new StringContent("/" + parent), "parent_dir");
            form.Add(new StringContent("1"), "replace");
            var file = new StreamingContent(content, length, progress);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var name = StoragePath.NameOf(full).Replace("\"", "%22", StringComparison.Ordinal);
            file.Headers.TryAddWithoutValidation("Content-Disposition", $"form-data; name=\"file\"; filename=\"{name}\"");
            form.Add(file);
            using var request = new HttpRequestMessage(HttpMethod.Post, upload) { Content = form };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw Map(response, normalized);
            }
        }
        catch (Exception exception)
        {
            throw HttpStorageClient.Map(exception, _options.Server.Host, cancellationToken, _trust);
        }

        return await GetAsync(normalized, cancellationToken)
            ?? throw new StorageException(StorageErrorKind.Transient, $"Die Datei ist nach dem Hochladen nicht zu sehen ({normalized}).");
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
        var kind = isFolder ? "dir" : "file";
        await SendAsync(HttpMethod.Delete, $"api2/repos/{_libraryId}/{kind}/?p={Param(full)}", normalized, null, allowNotFound: true, cancellationToken);
        ForgetFolders(full);
    }

    public async Task MoveAsync(string fromPath, string toPath, CancellationToken cancellationToken)
    {
        var from = StoragePath.Normalize(fromPath);
        var to = StoragePath.Normalize(toPath);
        var source = await GetAsync(from, cancellationToken)
            ?? throw new StorageException(StorageErrorKind.NotFound, $"Nicht gefunden ({from}).");
        if (await GetAsync(to, cancellationToken) is { } existing)
        {
            // Seafile would keep both ("Name (1)"); a move replaces like every other storage
            await DeleteAsync(to, existing.IsFolder, cancellationToken);
        }

        var fromFull = Full(from);
        var toFull = Full(to);
        var kind = source.IsFolder ? "dir" : "file";
        await EnsureFolderAsync(StoragePath.ParentOf(toFull), cancellationToken);
        if (StoragePath.ParentOf(fromFull) == StoragePath.ParentOf(toFull))
        {
            await RenameAsync(kind, fromFull, StoragePath.NameOf(toFull), from, cancellationToken);
            ForgetFolders(fromFull);
            return;
        }

        var moving = fromFull;
        var sameName = StoragePath.NameOf(fromFull) == StoragePath.NameOf(toFull);
        if (!sameName)
        {
            // a unique name first: the final name may exist in the old folder
            var temporary = $"{Local.LocalStorage.TempPrefix}{Guid.NewGuid():N}";
            await RenameAsync(kind, fromFull, temporary, from, cancellationToken);
            moving = ServerPaths.Child(StoragePath.ParentOf(fromFull), temporary);
        }

        await SendAsync(HttpMethod.Post, $"api2/repos/{_libraryId}/fileops/move/?p={Param(StoragePath.ParentOf(moving))}", from, () => new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["file_names"] = StoragePath.NameOf(moving),
            ["dst_repo"] = _libraryId,
            ["dst_dir"] = "/" + StoragePath.ParentOf(toFull),
        }), allowNotFound: false, cancellationToken);

        if (!sameName)
        {
            await RenameAsync(kind, ServerPaths.Child(StoragePath.ParentOf(toFull), StoragePath.NameOf(moving)), StoragePath.NameOf(toFull), to, cancellationToken);
        }

        ForgetFolders(fromFull);
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        _signIn.Dispose();
        _folderGate.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>"/a b/ü" as a query value.</summary>
    private static string Param(string full) => Uri.EscapeDataString("/" + full);

    private string Full(string path) => StoragePath.Combine(_root, path);

    /// <summary>The path below the root (a parent of the root stays as it is: messages only).</summary>
    private string RelativeOf(string full) => StoragePath.IsInside(full, _root) ? StoragePath.RelativeTo(full, _root) : full;

    private void ForgetFolders(string full)
    {
        foreach (var known in _knownFolders.Keys.Where(known => StoragePath.IsInside(known, full)))
        {
            _knownFolders.TryRemove(known, out _);
        }
    }

    /// <summary>The whole tree with one request (Seafile 6.3+: every entry has its parent_dir); null when the server does not do that.</summary>
    private async Task<List<StorageEntry>?> TryListRecursiveAsync(string start, CancellationToken cancellationToken)
    {
        var startFull = Full(start);
        using var json = await GetJsonAsync($"api2/repos/{_libraryId}/dir/?p={Param(startFull)}&recursive=1", start, cancellationToken);
        var entries = new List<StorageEntry>();
        foreach (var item in json.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("parent_dir", out var parentDir) || parentDir.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var parentFull = StoragePath.Normalize(parentDir.GetString());
            if (!StoragePath.IsInside(parentFull, _root) || (startFull.Length > 0 && !StoragePath.IsInside(parentFull, startFull)))
            {
                continue;
            }

            if (ToEntry(RelativeOf(parentFull), item) is { } entry)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    private StorageEntry? ToEntry(string folder, JsonElement item)
    {
        var name = StringOf(item, "name");
        if (name.Length == 0 || ServerPaths.IsHiddenName(name))
        {
            return null;
        }

        var isFolder = StringOf(item, "type") == "dir";
        var path = ServerPaths.Child(folder, name);
        if (isFolder)
        {
            _knownFolders[Full(path)] = true;
        }

        return new StorageEntry(path, isFolder, isFolder ? 0 : Int64Of(item, "size"), TimeOf(item))
        {
            IsHidden = name.StartsWith('.'),
        };
    }

    private static string StringOf(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static long Int64Of(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;

    /// <summary>"mtime": unix seconds (api2) or ISO 8601 (some newer answers).</summary>
    private static DateTimeOffset TimeOf(JsonElement item)
    {
        if (!item.TryGetProperty("mtime", out var value))
        {
            return DateTimeOffset.UnixEpoch;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        return value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var time)
            ? time.ToUniversalTime()
            : DateTimeOffset.UnixEpoch;
    }

    private async Task<bool> FolderExistsAsync(string full, CancellationToken cancellationToken)
    {
        var parent = StoragePath.ParentOf(full);
        using var folders = await TryGetJsonAsync($"api2/repos/{_libraryId}/dir/?p={Param(parent)}&t=d", RelativeOf(full), cancellationToken);
        if (folders is null)
        {
            return false;
        }

        var name = StoragePath.NameOf(full);
        var found = false;
        foreach (var item in folders.RootElement.EnumerateArray())
        {
            var itemName = StringOf(item, "name");
            _knownFolders[ServerPaths.Child(parent, itemName)] = true;
            found |= itemName == name;
        }

        return found;
    }

    /// <summary>Makes a folder and its missing parents, one at a time (two at once would give "Ordner (1)").</summary>
    private async Task EnsureFolderAsync(string full, CancellationToken cancellationToken)
    {
        if (full.Length == 0 || _knownFolders.ContainsKey(full))
        {
            return;
        }

        await EnsureFolderAsync(StoragePath.ParentOf(full), cancellationToken);
        await _folderGate.WaitAsync(cancellationToken);
        try
        {
            if (_knownFolders.ContainsKey(full) || await FolderExistsAsync(full, cancellationToken))
            {
                _knownFolders[full] = true;
                return;
            }

            await SendAsync(HttpMethod.Post, $"api2/repos/{_libraryId}/dir/?p={Param(full)}", RelativeOf(full),
                () => new FormUrlEncodedContent(new Dictionary<string, string> { ["operation"] = "mkdir" }), allowNotFound: false, cancellationToken);
            _knownFolders[full] = true;
        }
        finally
        {
            _folderGate.Release();
        }
    }

    private Task RenameAsync(string kind, string full, string newName, string path, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, $"api2/repos/{_libraryId}/{kind}/?p={Param(full)}", path, () => new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["operation"] = "rename",
            ["newname"] = newName,
        }), allowNotFound: false, cancellationToken);

    private async Task SignInAsync(string? refused, CancellationToken cancellationToken)
    {
        await _signIn.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null && _token != refused)
            {
                return;
            }

            var url = new Uri(_options.Server, "api2/auth-token/");
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = _options.UserName,
                    ["password"] = _options.Password ?? "",
                }),
            };
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(HttpStorageClient.MetadataTimeout);
            using var response = await SendRawAsync(request, limit.Token, cancellationToken);
            if (response.Headers.TryGetValues("X-Seafile-OTP", out var otp) && otp.Contains("required", StringComparer.OrdinalIgnoreCase))
            {
                throw new StorageException(StorageErrorKind.Authentication, "Das Konto verlangt eine Zwei-Faktor-Anmeldung – ThixSync kann sich so nicht anmelden.");
            }

            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new StorageException(StorageErrorKind.Authentication, "Die Anmeldung bei Seafile wurde abgelehnt – stimmen Benutzer und Passwort?");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw Map(response, "");
            }

            await using var body = await response.Content.ReadAsStreamAsync(limit.Token);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: limit.Token);
            _token = json.RootElement.TryGetProperty("token", out var token) && token.GetString() is { Length: > 0 } value
                ? value
                : throw new StorageException(StorageErrorKind.Authentication, "Seafile hat keine Anmeldung zurückgegeben.");
        }
        catch (JsonException exception)
        {
            throw new StorageException(StorageErrorKind.Unknown, "Der Server antwortet nicht wie ein Seafile-Server – stimmt die Adresse?", exception);
        }
        finally
        {
            _signIn.Release();
        }
    }

    private async Task<string> FindLibraryAsync(CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync("api2/repos/", "", cancellationToken);
        var libraries = json.RootElement.EnumerateArray().ToList();
        var match = libraries.FirstOrDefault(item => StringOf(item, "name") == _options.Library && StringOf(item, "type") == "repo");
        if (match.ValueKind == JsonValueKind.Undefined)
        {
            match = libraries.FirstOrDefault(item => StringOf(item, "name") == _options.Library);
        }

        if (match.ValueKind == JsonValueKind.Undefined)
        {
            match = libraries.FirstOrDefault(item => string.Equals(StringOf(item, "name"), _options.Library, StringComparison.CurrentCultureIgnoreCase));
        }

        if (match.ValueKind == JsonValueKind.Undefined)
        {
            throw new StorageException(StorageErrorKind.NotFound, $"Die Bibliothek „{_options.Library}“ gibt es auf dem Server nicht (oder sie ist nicht freigegeben).");
        }

        if (match.TryGetProperty("encrypted", out var encrypted) && encrypted.ValueKind is JsonValueKind.True)
        {
            throw new StorageException(StorageErrorKind.AccessDenied, $"Die Bibliothek „{_options.Library}“ ist verschlüsselt – das unterstützt ThixSync noch nicht.");
        }

        return StringOf(match, "id");
    }

    /// <summary>A download or upload link (the API answers with a JSON string).</summary>
    private async Task<string> GetLinkAsync(string relativeUrl, string path, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync(relativeUrl, path, cancellationToken);
        return json.RootElement.ValueKind == JsonValueKind.String && json.RootElement.GetString() is { Length: > 0 } link
            ? link
            : throw new StorageException(StorageErrorKind.Unknown, $"Seafile hat keinen Link zurückgegeben ({path}).");
    }

    private async Task<JsonDocument> GetJsonAsync(string relativeUrl, string path, CancellationToken cancellationToken) =>
        await TryGetJsonAsync(relativeUrl, path, cancellationToken) ?? throw new StorageException(StorageErrorKind.NotFound, $"Nicht gefunden ({path}).");

    /// <summary>GET of the API as JSON; null on 404.</summary>
    private Task<JsonDocument?> TryGetJsonAsync(string relativeUrl, string path, CancellationToken cancellationToken) =>
        ApiAsync(HttpMethod.Get, relativeUrl, path, null, async (response, token) =>
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw Map(response, path);
            }

            await using var body = await response.Content.ReadAsStreamAsync(token);
            try
            {
                return await JsonDocument.ParseAsync(body, cancellationToken: token);
            }
            catch (JsonException exception)
            {
                throw new StorageException(StorageErrorKind.Transient, $"Seafile hat eine unlesbare Antwort geschickt ({path}).", exception);
            }
        }, cancellationToken);

    private Task SendAsync(HttpMethod method, string relativeUrl, string path, Func<HttpContent>? content, bool allowNotFound, CancellationToken cancellationToken) =>
        ApiAsync(method, relativeUrl, path, content, (response, _) =>
        {
            if (!response.IsSuccessStatusCode && !(allowNotFound && response.StatusCode == HttpStatusCode.NotFound))
            {
                throw Map(response, path);
            }

            return Task.FromResult(true);
        }, cancellationToken);

    /// <summary>A request of the API with the token; a refused token is renewed once (the content is built again for the retry).</summary>
    private async Task<T> ApiAsync<T>(HttpMethod method, string relativeUrl, string path, Func<HttpContent>? content,
        Func<HttpResponseMessage, CancellationToken, Task<T>> handle, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = _token ?? throw new StorageException(StorageErrorKind.Authentication, "Nicht bei Seafile angemeldet.");
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(HttpStorageClient.MetadataTimeout);
            using var request = new HttpRequestMessage(method, new Uri(_options.Server, relativeUrl)) { Content = content?.Invoke() };
            request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await SendRawAsync(request, limit.Token, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                await SignInAsync(token, cancellationToken);
                continue;
            }

            try
            {
                return await handle(response, limit.Token);
            }
            catch (Exception exception) when (exception is not StorageException)
            {
                throw HttpStorageClient.Map(exception, _options.Server.Host, cancellationToken, _trust);
            }
        }
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken limit, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit);
        }
        catch (Exception exception)
        {
            throw HttpStorageClient.Map(exception, _options.Server.Host, cancellationToken, _trust);
        }
    }

    /// <summary>Seafile's own codes on top of HTTP: 443 = over quota, 520 = operation failed.</summary>
    private static StorageException Map(HttpResponseMessage response, string path) => (int)response.StatusCode switch
    {
        443 => new StorageException(StorageErrorKind.QuotaExceeded, "Das Speicherkontingent auf dem Seafile-Server ist ausgeschöpft."),
        520 => new StorageException(StorageErrorKind.Transient, $"Seafile konnte die Aktion nicht ausführen{(path.Length > 0 ? $" ({path})" : "")}."),
        _ => HttpStorageClient.Map(response, path),
    };
}
