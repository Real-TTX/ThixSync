using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ThixSync.Core.Storage.Providers.OAuth;

/// <summary>What a provider reads from an error answer of its API: the kind (null: decided by the HTTP status) and a short detail.</summary>
internal readonly record struct CloudError(StorageErrorKind? Kind, string Detail);

/// <summary>
/// Sends the requests of one opened cloud storage: at most <c>maxParallelism</c> at the same time for the whole connection (the API's
/// rate limits, however many workers the engine runs and storages it opens), with the bearer token of the connection (renewed once on
/// 401, shared with every worker), redirects followed WITHOUT the Authorization header (downloads go to a CDN), short waits on 429/5xx for requests that can be sent again, and every failure
/// as a <see cref="StorageException"/> with a German message (never a token).
/// </summary>
internal sealed class CloudApi
{
    private const int MaxAttempts = 3;
    private const int MaxRedirects = 5;

    private readonly OAuthTokenSource _tokens;
    private readonly SemaphoreSlim _throttle;
    private readonly Func<HttpStatusCode, JsonNode?, CloudError> _describe;

    public CloudApi(string serviceName, OAuthTokenSource tokens, int maxParallelism, Func<HttpStatusCode, JsonNode?, CloudError> describe)
    {
        ServiceName = serviceName;
        _tokens = tokens;
        _throttle = tokens.Throttle(maxParallelism);
        _describe = describe;
    }

    /// <summary>The longest wait a request sits out by itself before it gives the error to the engine (tests shorten it).</summary>
    internal static TimeSpan MaxInternalWait { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The first wait between two attempts without a Retry-After (doubles with every attempt; tests shorten it).</summary>
    internal static TimeSpan FirstBackoff { get; set; } = TimeSpan.FromSeconds(1);

    public string ServiceName { get; }

    /// <summary>
    /// Sends a request built by <paramref name="build"/> (called again for another attempt) and returns the successful answer (2xx, or
    /// 308 of a resumable upload) – the caller disposes it; the body is not read yet. <paramref name="replayable"/>: the request may be
    /// sent again (false for a streamed upload whose source cannot be read twice). <paramref name="path"/> names the file in messages.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken cancellationToken, bool replayable = true,
        bool authorize = true, string? path = null)
    {
        var renewed = false;
        for (var attempt = 1; ; attempt++)
        {
            string? token = null;
            HttpMethod method;
            HttpResponseMessage response;
            await _throttle.WaitAsync(cancellationToken);
            try
            {
                using var request = build();
                method = request.Method;
                if (authorize)
                {
                    token = await _tokens.GetAccessTokenAsync(cancellationToken);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }

                response = await CloudHttp.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (Exception exception) when (IsNetworkFailure(exception, cancellationToken))
            {
                var failure = NetworkFailure(exception);
                if (replayable && attempt < MaxAttempts)
                {
                    await Task.Delay(Backoff(attempt), cancellationToken);
                    continue;
                }

                throw failure;
            }
            finally
            {
                _throttle.Release();
            }

            if (method == HttpMethod.Get)
            {
                response = await FollowRedirectsAsync(response, cancellationToken);
            }

            if (response.IsSuccessStatusCode || (int)response.StatusCode == 308)
            {
                return response;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized && authorize && !renewed)
            {
                response.Dispose();
                renewed = true;
                await _tokens.RenewAsync(token!, cancellationToken);
                if (replayable)
                {
                    attempt--;
                    continue;
                }

                throw new StorageException(StorageErrorKind.Transient, $"{ServiceName}: Die Anmeldung wurde erneuert – die Übertragung wird wiederholt.");
            }

            var error = await ErrorOfAsync(response, path, cancellationToken);
            response.Dispose();
            if (replayable && attempt < MaxAttempts && error.Kind is StorageErrorKind.RateLimited or StorageErrorKind.Transient)
            {
                var wait = error.RetryAfter ?? Backoff(attempt);
                if (wait <= MaxInternalWait)
                {
                    await Task.Delay(wait, cancellationToken);
                    continue;
                }
            }

            throw error;
        }
    }

    /// <summary>Sends and reads the answer as JSON (an empty object when the body is empty).</summary>
    public async Task<JsonNode> SendJsonAsync(Func<HttpRequestMessage> build, CancellationToken cancellationToken, bool replayable = true, string? path = null,
        bool authorize = true)
    {
        using var response = await SendAsync(build, cancellationToken, replayable, authorize, path);
        return await ReadJsonAsync(response, cancellationToken);
    }

    /// <summary>The JSON body of an answer (an empty object for an empty or broken body).</summary>
    public static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseJson(text) ?? new JsonObject();
    }

    /// <summary>The StorageException for a failed answer (reads its body).</summary>
    public async Task<StorageException> ErrorOfAsync(HttpResponseMessage response, string? path, CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            text = "";
        }

        var status = response.StatusCode;
        var described = _describe(status, ParseJson(text));
        var kind = described.Kind ?? KindOf(status);
        var retryAfter = CloudHttp.RetryAfter(response);
        if (status == HttpStatusCode.ServiceUnavailable && retryAfter is not null && kind == StorageErrorKind.Transient)
        {
            kind = StorageErrorKind.RateLimited; // Graph throttles with 503 + Retry-After
        }

        return new StorageException(kind, MessageOf(kind, (int)status, path, described.Detail))
        {
            RetryAfter = kind == StorageErrorKind.RateLimited ? retryAfter ?? TimeSpan.FromSeconds(10) : retryAfter,
        };
    }

    /// <summary>A request with a JSON body.</summary>
    public static HttpRequestMessage Json(HttpMethod method, string url, JsonNode? body) => new(method, url)
    {
        Content = new StringContent(body?.ToJsonString() ?? "null", Encoding.UTF8, "application/json"),
    };

    public static JsonNode? ParseJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string MessageOf(StorageErrorKind kind, int status, string? path, string detail)
    {
        var text = kind switch
        {
            StorageErrorKind.NotFound => "Nicht gefunden",
            StorageErrorKind.AccessDenied => "Keine Berechtigung",
            StorageErrorKind.Authentication => "Die Anmeldung ist abgelaufen oder wurde widerrufen – bitte die Verbindung neu anmelden",
            StorageErrorKind.QuotaExceeded => "Der Speicherplatz ist voll",
            StorageErrorKind.RateLimited => "Zu viele Anfragen – es geht gleich weiter",
            StorageErrorKind.Transient => $"Ein vorübergehendes Problem des Dienstes (HTTP {status})",
            StorageErrorKind.InvalidName => "Dieser Name ist hier nicht erlaubt",
            StorageErrorKind.Locked => "Die Datei ist gesperrt",
            _ => $"Die Anfrage wurde abgelehnt (HTTP {status})",
        };
        var where = string.IsNullOrEmpty(path) ? "" : $" – {path}";
        var why = detail.Length > 0 ? $" ({detail})" : "";
        return $"{ServiceName}: {text}{where}{why}.";
    }

    private static StorageErrorKind KindOf(HttpStatusCode status) => (int)status switch
    {
        401 => StorageErrorKind.Authentication,
        403 => StorageErrorKind.AccessDenied,
        404 or 410 => StorageErrorKind.NotFound,
        408 => StorageErrorKind.Transient,
        423 => StorageErrorKind.Locked,
        429 => StorageErrorKind.RateLimited,
        507 => StorageErrorKind.QuotaExceeded,
        >= 500 => StorageErrorKind.Transient,
        _ => StorageErrorKind.Unknown,
    };

    private static bool IsNetworkFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or IOException || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested;

    private StorageException NetworkFailure(Exception exception)
    {
        if (exception is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError })
        {
            return new StorageException(StorageErrorKind.Unreachable, $"{ServiceName} ist nicht erreichbar ({exception.Message}).", exception);
        }

        if (exception is TaskCanceledException)
        {
            return new StorageException(StorageErrorKind.Unreachable, $"{ServiceName} antwortet nicht.", exception);
        }

        return new StorageException(StorageErrorKind.Transient, $"{ServiceName}: Die Verbindung wurde unterbrochen ({exception.Message}).", exception);
    }

    private static TimeSpan Backoff(int attempt) => FirstBackoff * Math.Pow(2, attempt - 1);

    private async Task<HttpResponseMessage> FollowRedirectsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        for (var hop = 0; hop < MaxRedirects; hop++)
        {
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is not { } location)
            {
                return response;
            }

            var target = location.IsAbsoluteUri ? location : new Uri(response.RequestMessage!.RequestUri!, location);
            response.Dispose();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, target); // no Authorization: the CDN gets a signed address
                response = await CloudHttp.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (Exception exception) when (IsNetworkFailure(exception, cancellationToken))
            {
                throw NetworkFailure(exception);
            }
        }

        return response;
    }
}
