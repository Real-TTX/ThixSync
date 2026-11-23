namespace ThixSync.Core.Remote;

/// <summary>A pairing code this computer shows (another computer enters it to connect, valid for a few minutes).</summary>
public sealed record PairingCode(string Code, DateTimeOffset ExpiresAt);

/// <summary>A computer with ThixSync found in the local network.</summary>
public sealed record RemoteComputer(string DeviceName, string Host, int Port);

/// <summary>
/// ThixSync Remote on this computer: offers the shares of <see cref="Models.RemoteSettings"/> to paired computers (TLS, port from the
/// settings). Started by the app when Remote is on; follows changes of the settings by itself.
///
/// CONTRACT – implemented in Remote/; keep the public members.
/// </summary>
public static partial class RemoteServer
{
    /// <summary>Starts listening when Remote is on in the settings (and the policy allows it). Safe to call twice.</summary>
    public static partial void Start();

    public static partial void Stop();

    public static partial bool IsListening { get; }

    /// <summary>The fingerprint (SHA-256) of this computer's certificate, as the other computer shows it ("AB:CD:…").</summary>
    public static partial string Fingerprint { get; }

    /// <summary>A new pairing code (the old one ends). Throws BusinessException when Remote is off or forbidden.</summary>
    public static partial PairingCode CreatePairingCode();

    /// <summary>Ends the access of a paired computer.</summary>
    public static partial void RemovePeer(string keyId);

    /// <summary>Computers with ThixSync Remote in the local network (broadcast; waits up to <paramref name="timeout"/>).</summary>
    public static partial Task<IReadOnlyList<RemoteComputer>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
