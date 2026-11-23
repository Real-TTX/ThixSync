namespace ThixSync.Core.Remote;

// PLACEHOLDER – replaced by the Remote implementation.
public static partial class RemoteServer
{
    public static partial void Start()
    {
    }

    public static partial void Stop()
    {
    }

    public static partial bool IsListening => false;

    public static partial string Fingerprint => "";

    public static partial PairingCode CreatePairingCode() => throw new NotImplementedException();

    public static partial void RemovePeer(string keyId)
    {
    }

    public static partial Task<IReadOnlyList<RemoteComputer>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RemoteComputer>>([]);
}
