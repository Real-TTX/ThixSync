// MatCloud.Contract 1.7.0 – a copy from MatCloud, do not edit here
namespace MatCloud.Contract;

/// <summary>
/// POST <see cref="CloudContract.DeviceLogin"/>: signs a device in with the user name and password of a local account of MatCloud. An account that
/// signs in through MatIdentity has no password here: such a device uses <see cref="DeviceLinkRequest"/>.
/// The same device name, platform and app signing in again replaces the old entry.
/// </summary>
public sealed record DeviceLoginRequest(
    string UserName,
    string Password,
    string DeviceName,
    string Platform,
    string AppVersion,
    string App);

/// <summary>The answer of a successful sign-in: the token is shown once and identifies the device from now on.</summary>
public sealed record DeviceLoginResponse(
    string Token,
    long DeviceId,
    AccountInfo Account);

/// <summary>
/// POST <see cref="CloudContract.DeviceLink"/>: the sign-in in the browser. The device shows <see cref="DeviceLinkStart.Code"/> and opens
/// <see cref="DeviceLinkStart.VerificationUrl"/>; the person signs in there (with a password or through MatIdentity), sees the device and the code and
/// confirms it. Meanwhile the device asks <see cref="CloudContract.DeviceLinkPoll"/> with <see cref="DeviceLinkStart.PollToken"/>.
/// </summary>
public sealed record DeviceLinkRequest(
    string DeviceName,
    string Platform,
    string AppVersion,
    string App);

/// <summary>The started sign-in: the code to show, the address to open, the secret to poll with, how long it is valid.</summary>
public sealed record DeviceLinkStart(
    string Code,
    string VerificationUrl,
    string PollToken,
    int ExpiresInSeconds,
    int IntervalSeconds);

/// <summary>POST <see cref="CloudContract.DeviceLinkPoll"/>: the secret of the started sign-in.</summary>
public sealed record DeviceLinkPollRequest(string PollToken);

/// <summary>Where a sign-in in the browser stands.</summary>
public enum DeviceLinkState
{
    /// <summary>Not confirmed yet: ask again after the interval.</summary>
    Pending,

    /// <summary>Confirmed: <see cref="DeviceLinkPollResponse.Login"/> carries the token (only in this one answer).</summary>
    Approved,

    /// <summary>The person refused it in the browser.</summary>
    Denied,

    /// <summary>Too late, or already fetched: start again.</summary>
    Expired,
}

/// <summary>The answer of a poll (always 200): the state, and the sign-in once it is approved.</summary>
public sealed record DeviceLinkPollResponse(DeviceLinkState State, DeviceLoginResponse? Login = null);

/// <summary>GET <see cref="CloudContract.Account"/>: the account and the device as the cloud knows them.</summary>
public sealed record AccountInfo(
    string UserName,
    string DisplayName,
    string DeviceName,
    string App,
    string ServerVersion);

/// <summary>
/// POST <see cref="CloudContract.DeviceHandoff"/> by ThixLauncher: a sign-in for <paramref name="App"/> on the launcher's computer, for the account of the
/// launcher, already confirmed. The app fetches its token with <see cref="CloudContract.DeviceLinkPoll"/> and the poll token (it finds both in
/// <see cref="CloudHandoff"/>); the device of the app is made only then, with the launcher's device name and platform.
/// </summary>
public sealed record DeviceHandoffRequest(string App, string AppVersion);

/// <summary>The hand-over: the poll token of the confirmed sign-in, valid once and for <paramref name="ExpiresInSeconds"/> (a day).</summary>
public sealed record DeviceHandoffStart(string PollToken, int ExpiresInSeconds);
