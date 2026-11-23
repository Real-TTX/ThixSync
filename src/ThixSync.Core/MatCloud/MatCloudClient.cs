// MatCloud.Contract 1.7.0 – a copy from MatCloud, do not edit here
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MatCloud.Contract;

/// <summary>MatCloud refused a request; <see cref="StatusCode"/> 401 means the device is no longer signed in, 403 that its app is switched off.</summary>
public sealed class CloudException(HttpStatusCode statusCode, string message, string? field = null) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string? Field { get; } = field;

    public bool IsUnauthorized => StatusCode == HttpStatusCode.Unauthorized;

    public bool IsForbidden => StatusCode == HttpStatusCode.Forbidden;
}

/// <summary>
/// The HTTP calls of the protocol (<see cref="CloudContract"/>, <see cref="SyncContract"/>): the sign-in (password or browser), the account and
/// the general sync. One instance per server and token. An app with a module of its own (ThixClip) sends its own routes through
/// <see cref="SendAsync{T}"/>, <see cref="PutBytesAsync"/> and <see cref="GetBytesAsync"/>. The calls of the Drive (also for ThixWrite, ThixCalc and
/// ThixShow) are in Drive/MatCloudClient.Drive.cs.
/// </summary>
public sealed partial class MatCloudClient : IDisposable
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;

    /// <param name="serverUrl">The address of the cloud (https://cloud.example.com).</param>
    /// <param name="token">The device token, or null before the sign-in.</param>
    /// <param name="userAgent">The app and its version ("ThixNote/1.1.42"); the cloud shows the version with the device.</param>
    /// <param name="handler">Tests talk to a server in memory.</param>
    public MatCloudClient(string serverUrl, string? token, string userAgent, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/");
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        _http.DefaultRequestHeaders.Add(CloudContract.ProtocolHeader, CloudContract.ProtocolVersion.ToString(CultureInfo.InvariantCulture));
        if (token is not null)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    /// <summary>
    /// The address a person types, cleaned: "https://" is added when it is missing, a trailing "/" goes. Throws <see cref="UriFormatException"/>
    /// for something that is no http(s) address.
    /// </summary>
    public static string NormalizeServerUrl(string? serverUrl)
    {
        var text = (serverUrl ?? string.Empty).Trim().TrimEnd('/');
        if (text.Length == 0)
        {
            throw new UriFormatException("empty");
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        var uri = new Uri(text, UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new UriFormatException("not http");
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    // ---- sign-in and account ----

    public Task<DeviceLoginResponse> LoginAsync(DeviceLoginRequest request, CancellationToken ct) =>
        SendAsync<DeviceLoginResponse>(HttpMethod.Post, CloudContract.DeviceLogin, JsonContent.Create(request, options: CloudContract.Json), RequestTimeout, ct);

    public Task<DeviceLinkStart> StartLinkAsync(DeviceLinkRequest request, CancellationToken ct) =>
        SendAsync<DeviceLinkStart>(HttpMethod.Post, CloudContract.DeviceLink, JsonContent.Create(request, options: CloudContract.Json), RequestTimeout, ct);

    public Task<DeviceLinkPollResponse> PollLinkAsync(string pollToken, CancellationToken ct) =>
        SendAsync<DeviceLinkPollResponse>(HttpMethod.Post, CloudContract.DeviceLinkPoll,
            JsonContent.Create(new DeviceLinkPollRequest(pollToken), options: CloudContract.Json), RequestTimeout, ct);

    /// <summary>
    /// Waits until the person confirmed (or refused) the code of <paramref name="start"/> in the browser: asks every interval until the code expires.
    /// Returns the sign-in; throws <see cref="CloudException"/> (403) when it was refused and (408) when it expired.
    /// </summary>
    public async Task<DeviceLoginResponse> WaitForLinkAsync(DeviceLinkStart start, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(start.IntervalSeconds, 1, 30));
        var until = DateTimeOffset.UtcNow.AddSeconds(start.ExpiresInSeconds);
        while (DateTimeOffset.UtcNow < until)
        {
            await Task.Delay(interval, ct);
            var answer = await PollLinkAsync(start.PollToken, ct);
            switch (answer.State)
            {
                case DeviceLinkState.Approved when answer.Login is not null:
                    return answer.Login;
                case DeviceLinkState.Denied:
                    throw new CloudException(HttpStatusCode.Forbidden, "Die Anmeldung wurde im Browser abgelehnt.");
                case DeviceLinkState.Expired:
                    throw new CloudException(HttpStatusCode.RequestTimeout, "Der Code ist abgelaufen. Bitte noch einmal anmelden.");
            }
        }

        throw new CloudException(HttpStatusCode.RequestTimeout, "Der Code ist abgelaufen. Bitte noch einmal anmelden.");
    }

    /// <summary>ThixLauncher: a sign-in for an app it installs (<see cref="CloudContract.DeviceHandoff"/>).</summary>
    public Task<DeviceHandoffStart> HandOffAsync(DeviceHandoffRequest request, CancellationToken ct) =>
        SendAsync<DeviceHandoffStart>(HttpMethod.Post, CloudContract.DeviceHandoff, JsonContent.Create(request, options: CloudContract.Json), RequestTimeout, ct);

    /// <summary>
    /// The app: fetches the sign-in ThixLauncher left for it (<see cref="CloudHandoff.Take"/>). Returns null when it expired or was used already;
    /// throws <see cref="CloudException"/> / <see cref="HttpRequestException"/> when the cloud cannot be asked (the app keeps the hand-over then).
    /// </summary>
    public async Task<DeviceLoginResponse?> RedeemHandoffAsync(DeviceHandoff handoff, CancellationToken ct)
    {
        var answer = await PollLinkAsync(handoff.PollToken, ct);
        return answer is { State: DeviceLinkState.Approved, Login: { } login } ? login : null;
    }

    public Task LogoutAsync(CancellationToken ct) =>
        SendAsync<object>(HttpMethod.Post, CloudContract.DeviceLogout, null, RequestTimeout, ct, expectBody: false);

    public Task<AccountInfo> GetAccountAsync(CancellationToken ct) =>
        SendAsync<AccountInfo>(HttpMethod.Get, CloudContract.Account, null, RequestTimeout, ct);

    // ---- the general sync ----

    public Task<SyncChangeSet> GetChangesAsync(long since, int limit, CancellationToken ct) =>
        SendAsync<SyncChangeSet>(HttpMethod.Get, $"{SyncContract.Changes}?since={since}&limit={limit}", null, RequestTimeout, ct);

    public Task<WaitResult> WaitAsync(long since, int timeoutSeconds, CancellationToken ct) =>
        SendAsync<WaitResult>(HttpMethod.Get, $"{SyncContract.Wait}?since={since}&timeout={timeoutSeconds}", null, TimeSpan.FromSeconds(timeoutSeconds + 20), ct);

    public Task<SyncPushResponse> PushAsync(SyncPushRequest request, CancellationToken ct) =>
        SendAsync<SyncPushResponse>(HttpMethod.Post, SyncContract.Push, JsonContent.Create(request, options: CloudContract.Json), TransferTimeout, ct);

    // ---- ThixCalendar: the accounts MatCloud keeps in step ----

    public Task<Calendar.CalendarCloudAccount> HandOverCalendarAsync(Calendar.CalendarHandover handover, CancellationToken ct) =>
        SendAsync<Calendar.CalendarCloudAccount>(HttpMethod.Put, Calendar.CalendarContract.Connections, JsonContent.Create(handover, options: CloudContract.Json), RequestTimeout, ct);

    public Task DisconnectCalendarAsync(string account, CancellationToken ct) =>
        SendAsync<object>(HttpMethod.Post, Calendar.CalendarContract.Disconnect, JsonContent.Create(new Calendar.CalendarAccountRequest(account), options: CloudContract.Json), RequestTimeout, ct, expectBody: false);

    public Task SyncCalendarNowAsync(string account, CancellationToken ct) =>
        SendAsync<object>(HttpMethod.Post, Calendar.CalendarContract.SyncNow, JsonContent.Create(new Calendar.CalendarAccountRequest(account), options: CloudContract.Json), RequestTimeout, ct, expectBody: false);

    /// <summary>Changes an event at the provider through MatCloud (it may take a while: the provider is asked first).</summary>
    public Task<Calendar.CalendarEventChangeResult> ChangeCalendarEventAsync(Calendar.CalendarEventChange change, CancellationToken ct) =>
        SendAsync<Calendar.CalendarEventChangeResult>(HttpMethod.Post, Calendar.CalendarContract.EventChange, JsonContent.Create(change, options: CloudContract.Json), TransferTimeout, ct);

    // ---- the building blocks (also for the routes of a module) ----

    /// <summary>Sends a request and reads the JSON answer (<paramref name="expectBody"/> false: no answer is read).</summary>
    public async Task<T> SendAsync<T>(HttpMethod method, string path, HttpContent? content, TimeSpan timeoutAfter, CancellationToken ct, bool expectBody = true)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutAfter);
        using var request = new HttpRequestMessage(method, path.TrimStart('/')) { Content = content };
        using var response = await _http.SendAsync(request, timeout.Token);
        await ThrowIfFailedAsync(response, timeout.Token);
        if (!expectBody)
        {
            return default!;
        }

        return await response.Content.ReadFromJsonAsync<T>(CloudContract.Json, timeout.Token)
               ?? throw new CloudException(response.StatusCode, "Die Cloud hat eine leere Antwort geschickt.");
    }

    /// <summary>PUT of bytes (a picture).</summary>
    public async Task PutBytesAsync(string path, byte[] bytes, string contentType, CancellationToken ct)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        await SendAsync<object>(HttpMethod.Put, path, content, TransferTimeout, ct, expectBody: false);
    }

    /// <summary>GET of bytes; null when the cloud answers 404.</summary>
    public async Task<byte[]?> GetBytesAsync(string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TransferTimeout);
        using var response = await _http.GetAsync(path.TrimStart('/'), timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await ThrowIfFailedAsync(response, timeout.Token);
        return await response.Content.ReadAsByteArrayAsync(timeout.Token);
    }

    public void Dispose() => _http.Dispose();

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        ApiError? error = null;
        try
        {
            if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                error = await response.Content.ReadFromJsonAsync<ApiError>(CloudContract.Json, ct);
            }
        }
        catch (JsonException)
        {
        }

        var message = error?.Message ?? response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Anmeldung abgelehnt.",
            HttpStatusCode.Forbidden => "Keine Berechtigung.",
            HttpStatusCode.NotFound => "Diese Adresse ist keine MatCloud.",
            HttpStatusCode.TooManyRequests => "Zu viele Versuche – bitte kurz warten.",
            _ => $"Die Cloud antwortet mit Fehler {(int)response.StatusCode}.",
        };
        throw new CloudException(response.StatusCode, message, error?.Field);
    }
}
