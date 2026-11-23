using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ThixSync.Core.Storage.Providers.S3;

namespace ThixSync.Core.Storage.Providers.PCloud;

/// <summary>
/// The JSON API of pCloud (EU: eapi.pcloud.com, US: api.pcloud.com). Signs in with the digest login (the password never goes
/// over the wire, only sha1(password + sha1(user) + digest)) and keeps the auth token in memory; signs in again once when the
/// token is refused. pCloud answers HTTP 200 with a "result" code: every code other than 0 becomes a <see cref="StorageException"/>.
/// </summary>
internal sealed class PCloudClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _apiBase;
    private readonly string _userName;
    private readonly string _password;
    private readonly SemaphoreSlim _signIn = new(1);
    private volatile string? _auth;

    public PCloudClient(string apiBase, string userName, string password, HttpMessageHandler? handler)
    {
        _apiBase = apiBase.TrimEnd('/');
        _userName = userName;
        _password = password;
        _http = CloudHttp.CreateClient(handler, 16);
    }

    /// <summary>The API address of a data region ("eu" or "us").</summary>
    public static string ApiFor(string region) =>
        region.Equals("us", StringComparison.OrdinalIgnoreCase) ? "https://api.pcloud.com" : "https://eapi.pcloud.com";

    /// <summary>The password digest pCloud checks: sha1(password + sha1(lower(user)) + digest), lower-case hex.</summary>
    public static string PasswordDigest(string userName, string password, string digest)
    {
        var userHash = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(userName.ToLowerInvariant())));
        return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(password + userHash + digest)));
    }

    /// <summary>Signs in (getdigest, then userinfo with getauth=1) unless a token is there already.</summary>
    public async Task SignInAsync(CancellationToken cancellationToken)
    {
        if (_auth is not null)
        {
            return;
        }

        await RenewAsync(null, cancellationToken);
    }

    /// <summary>Calls a method with GET and the parameters; signs in first, and once more when the token is refused.</summary>
    public async Task<JsonElement> CallAsync(string method, IReadOnlyList<KeyValuePair<string, string>> parameters, string path, CancellationToken cancellationToken)
    {
        await SignInAsync(cancellationToken);
        var auth = _auth!;
        try
        {
            return await CallOnceAsync(method, parameters, auth, path, cancellationToken);
        }
        catch (PCloudException exception) when (exception.Result is 1000 or 2000 or 2094)
        {
            await RenewAsync(auth, cancellationToken);
            return await CallOnceAsync(method, parameters, _auth!, path, cancellationToken);
        }
    }

    /// <summary>uploadfile into a folder (multipart, streamed, with mtime; nopartial: a broken upload leaves nothing); the metadata of the file.</summary>
    public async Task<JsonElement> UploadAsync(string folder, string fileName, Stream content, long length, DateTimeOffset modified, IProgress<long>? progress,
        string path, CancellationToken cancellationToken)
    {
        await SignInAsync(cancellationToken);
        var seconds = modified.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("auth", _auth!), new("path", folder), new("filename", fileName), new("nopartial", "1"), new("mtime", seconds), new("timeformat", "timestamp"),
        };
        var file = new StreamContent(new ProgressStream(content, length, progress));
        file.Headers.ContentLength = length;
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        // the name as UTF-8 in the part header (browsers do it so; the RFC 2047 form .NET would write is not understood)
        file.Headers.TryAddWithoutValidation("Content-Disposition", $"form-data; name=\"file\"; filename=\"{fileName.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"");
        using var form = new MultipartFormDataContent { HeaderEncodingSelector = (_, _) => Encoding.UTF8 };
        form.Add(file);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_apiBase}/uploadfile?{CloudHttp.Query(parameters)}") { Content = form };
        using var response = await CloudHttp.SendAsync(_http, request, HttpCompletionOption.ResponseContentRead, limitTime: false, path, cancellationToken);
        var root = await ReadAsync(response, path, cancellationToken);
        var metadata = root.TryGetProperty("metadata", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0
            ? list[0]
            : throw new StorageException(StorageErrorKind.Transient, $"pCloud hat den Upload nicht bestätigt{CloudHttp.Where(path)}.");
        return metadata;
    }

    /// <summary>getfilelink, then GET from the content server.</summary>
    public async Task<Stream> DownloadAsync(string filePath, string path, CancellationToken cancellationToken)
    {
        var link = await CallAsync("getfilelink", [new("path", filePath), new("forcedownload", "1")], path, cancellationToken);
        var host = link.TryGetProperty("hosts", out var hosts) && hosts.ValueKind == JsonValueKind.Array && hosts.GetArrayLength() > 0 ? hosts[0].GetString() : null;
        var linkPath = link.TryGetProperty("path", out var value) ? value.GetString() : null;
        if (host is null || linkPath is null)
        {
            throw new StorageException(StorageErrorKind.Transient, $"pCloud nannte keinen Download-Server{CloudHttp.Where(path)}.");
        }

        var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}{linkPath}");
        var response = await CloudHttp.SendAsync(_http, request, HttpCompletionOption.ResponseHeadersRead, limitTime: true, path, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                var kind = CloudHttp.KindOf(response.StatusCode);
                throw new StorageException(kind, $"{CloudHttp.Describe(kind)}{CloudHttp.Where(path)} [HTTP {(int)response.StatusCode}].")
                {
                    RetryAfter = kind == StorageErrorKind.RateLimited ? CloudHttp.RetryAfter(response) : null,
                };
            }
        }

        return await ResponseStream.OpenAsync(response, path, cancellationToken);
    }

    /// <summary>Ends the session on the server (the token becomes invalid); best effort.</summary>
    public async Task SignOutAsync()
    {
        var auth = _auth;
        if (auth is null)
        {
            return;
        }

        _auth = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await CallOnceAsync("logout", [], auth, "", timeout.Token);
        }
        catch (Exception exception) when (exception is StorageException or OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _signIn.Dispose();
    }

    /// <summary>Maps a result code of pCloud to a German <see cref="StorageException"/>.</summary>
    public static StorageException Map(int result, string? error, string path)
    {
        var kind = result switch
        {
            1000 or 2000 or 2094 => StorageErrorKind.Authentication,
            2003 => StorageErrorKind.AccessDenied,
            2008 => StorageErrorKind.QuotaExceeded,
            2002 or 2005 or 2009 => StorageErrorKind.NotFound,
            2001 or 2010 => StorageErrorKind.InvalidName,
            4000 => StorageErrorKind.RateLimited,
            5000 or 5001 => StorageErrorKind.Transient,
            _ => StorageErrorKind.Unknown,
        };
        var text = result switch
        {
            2000 => "Die Anmeldung bei pCloud wurde abgelehnt: E-Mail oder Passwort stimmt nicht",
            1000 or 2094 => "Die Anmeldung bei pCloud ist abgelaufen",
            4000 => "Zu viele Anmeldeversuche – pCloud sperrt kurz",
            2008 => "Der Speicherplatz bei pCloud ist voll",
            _ when error?.Contains("location", StringComparison.OrdinalIgnoreCase) == true =>
                "Das Konto liegt in einem anderen Rechenzentrum – bitte EU/USA in der Verbindung umstellen",
            _ => CloudHttp.Describe(kind),
        };
        return new PCloudException(result, kind, $"{text}{CloudHttp.Where(path)} [pCloud {result.ToString(CultureInfo.InvariantCulture)}].")
        {
            RetryAfter = kind == StorageErrorKind.RateLimited ? TimeSpan.FromMinutes(1) : null,
        };
    }

    private async Task RenewAsync(string? refusedAuth, CancellationToken cancellationToken)
    {
        await _signIn.WaitAsync(cancellationToken);
        try
        {
            if (_auth is not null && _auth != refusedAuth)
            {
                return; // another call signed in meanwhile
            }

            var digest = (await CallOnceAsync("getdigest", [], null, "", cancellationToken)).GetProperty("digest").GetString() ?? "";
            var user = await CallOnceAsync("userinfo",
            [
                new("getauth", "1"), new("logout", "1"), new("username", _userName), new("digest", digest),
                new("passworddigest", PasswordDigest(_userName, _password, digest)),
            ], null, "", cancellationToken);
            _auth = user.TryGetProperty("auth", out var auth) ? auth.GetString() : null;
            if (_auth is null)
            {
                throw new StorageException(StorageErrorKind.Authentication, "pCloud hat keine Anmeldung zurückgegeben.");
            }
        }
        finally
        {
            _signIn.Release();
        }
    }

    private async Task<JsonElement> CallOnceAsync(string method, IReadOnlyList<KeyValuePair<string, string>> parameters, string? auth, string path,
        CancellationToken cancellationToken)
    {
        var all = auth is null ? parameters : [new("auth", auth), .. parameters];
        using var request = new HttpRequestMessage(HttpMethod.Get, all.Count == 0 ? $"{_apiBase}/{method}" : $"{_apiBase}/{method}?{CloudHttp.Query(all)}");
        // a whole tree (listing, deleting) may take longer than one ordinary answer
        var limitTime = method is not ("listfolder" or "deletefolderrecursive");
        using var response = await CloudHttp.SendAsync(_http, request, HttpCompletionOption.ResponseContentRead, limitTime, path, cancellationToken);
        return await ReadAsync(response, path, cancellationToken);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, string path, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var kind = CloudHttp.KindOf(response.StatusCode);
            throw new StorageException(kind, $"{CloudHttp.Describe(kind)}{CloudHttp.Where(path)} [HTTP {(int)response.StatusCode}].")
            {
                RetryAfter = kind == StorageErrorKind.RateLimited ? CloudHttp.RetryAfter(response) : null,
            };
        }

        JsonElement root;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            root = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new StorageException(StorageErrorKind.Transient, $"pCloud antwortete unverständlich{CloudHttp.Where(path)}.", exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw CloudHttp.MapTransport(exception, path);
        }

        var result = root.TryGetProperty("result", out var code) && code.TryGetInt32(out var number) ? number : -1;
        if (result == 0)
        {
            return root;
        }

        throw Map(result, root.TryGetProperty("error", out var error) ? error.GetString() : null, path);
    }
}

/// <summary>A failed pCloud call with its result code.</summary>
internal sealed class PCloudException(int result, StorageErrorKind kind, string message) : StorageException(kind, message)
{
    public int Result { get; } = result;
}
