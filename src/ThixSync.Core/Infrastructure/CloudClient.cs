using MatCloud.Contract;

namespace ThixSync.Core.Infrastructure;

/// <summary>
/// The HTTP calls of ThixSync to MatCloud: a thin wrapper around <see cref="MatCloudClient"/> (the copied contract) for the sign-in, the account
/// and the general sync (<see cref="SyncContract"/>, app <see cref="CloudApps.Sync"/>). One instance per server and token. Only
/// <see cref="Business.SyncService"/> uses it. Errors are <see cref="CloudException"/> of the contract.
/// </summary>
internal sealed class CloudClient : IDisposable
{
    private readonly MatCloudClient _client;

    private CloudClient(string serverUrl, string? token) =>
        _client = new MatCloudClient(serverUrl, token, UserAgent, HandlerForTests?.Invoke());

    /// <summary>Tests talk to an in-memory server.</summary>
    internal static Func<HttpMessageHandler>? HandlerForTests { get; set; }

    /// <summary>"ThixSync/0.1.42-20261115 (Windows)": the cloud shows the version with the device.</summary>
    public static string UserAgent => $"ThixSync/{Token(AppInfo.Version)} ({Token(AppInfo.PlatformName)})";

    public static CloudClient Create(string serverUrl, string? token) => new(serverUrl, token);

    public Task<DeviceLoginResponse> LoginAsync(string userName, string password, string deviceName, CancellationToken cancellationToken) =>
        _client.LoginAsync(new DeviceLoginRequest(userName, password, deviceName, AppInfo.PlatformName, AppInfo.Version, CloudApps.Sync), cancellationToken);

    public Task<DeviceLinkStart> StartLinkAsync(string deviceName, CancellationToken cancellationToken) =>
        _client.StartLinkAsync(new DeviceLinkRequest(deviceName, AppInfo.PlatformName, AppInfo.Version, CloudApps.Sync), cancellationToken);

    public Task<DeviceLoginResponse> WaitForLinkAsync(DeviceLinkStart start, CancellationToken cancellationToken) =>
        _client.WaitForLinkAsync(start, cancellationToken);

    public Task<DeviceLoginResponse?> RedeemHandoffAsync(DeviceHandoff handoff, CancellationToken cancellationToken) =>
        _client.RedeemHandoffAsync(handoff, cancellationToken);

    public Task LogoutAsync(CancellationToken cancellationToken) => _client.LogoutAsync(cancellationToken);

    public Task<SyncChangeSet> GetChangesAsync(long since, int limit, CancellationToken cancellationToken) =>
        _client.GetChangesAsync(since, limit, cancellationToken);

    public Task<WaitResult> WaitAsync(long since, int timeoutSeconds, CancellationToken cancellationToken) =>
        _client.WaitAsync(since, timeoutSeconds, cancellationToken);

    public Task<SyncPushResponse> PushAsync(SyncPushRequest request, CancellationToken cancellationToken) =>
        _client.PushAsync(request, cancellationToken);

    public void Dispose() => _client.Dispose();

    /// <summary>Only what a User-Agent header allows (letters, digits, '.', '-', '_').</summary>
    private static string Token(string text)
    {
        var token = new string(text.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray()).Trim('-');
        return token.Length > 0 ? token : "unknown";
    }
}
