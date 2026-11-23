using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Remote;

namespace ThixSync.Core.Storage.Providers.Remote;

/// <summary>How a client signs in at the other computer: with the key of an earlier pairing, or with a pairing code (once).</summary>
internal abstract record RemoteCredentials
{
    /// <summary>"key:&lt;keyId&gt;:&lt;base64 key&gt;" or a pairing code ("1234-5678"); null for anything else.</summary>
    public static RemoteCredentials? Parse(string? secret)
    {
        if (secret is null)
        {
            return null;
        }

        var parts = secret.Trim().Split(':');
        if (parts is ["key", { Length: > 0 } keyId, { Length: > 0 } key])
        {
            try
            {
                return new DeviceKey(keyId, Convert.FromBase64String(key));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        return RemoteCrypto.PairingDigits(secret) is { } digits ? new Pairing(digits) : null;
    }

    public sealed record DeviceKey(string KeyId, byte[] Key) : RemoteCredentials
    {
        /// <summary>The secret as the connection keeps it.</summary>
        public string Secret => $"key:{KeyId}:{Convert.ToBase64String(Key)}";
    }

    public sealed record Pairing(string Digits) : RemoteCredentials;
}

/// <summary>
/// One TLS connection of a client to a Remote server, signed in (see <see cref="RemoteProtocol"/>). The certificate of the server must
/// have the pinned fingerprint (none pinned yet: any, and <see cref="Fingerprint"/> tells which one it was).
/// </summary>
internal sealed class RemoteClientConnection : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    private readonly TcpClient _tcp;

    private RemoteClientConnection(TcpClient tcp, RemoteChannel channel, string fingerprint, string serverDevice)
    {
        _tcp = tcp;
        Channel = channel;
        Fingerprint = fingerprint;
        ServerDevice = serverDevice;
        LastUsed = DateTimeOffset.UtcNow;
    }

    public RemoteChannel Channel { get; }

    /// <summary>The fingerprint of the server's certificate ("AB:CD:…").</summary>
    public string Fingerprint { get; }

    /// <summary>The computer name of the server.</summary>
    public string ServerDevice { get; }

    public DateTimeOffset LastUsed { get; set; }

    /// <summary>
    /// Connects, checks the certificate and signs in. Returns the connection and, after a pairing, the new device key. Throws
    /// <see cref="StorageException"/> (Unreachable, Authentication …) or <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<(RemoteClientConnection Connection, RemoteCredentials.DeviceKey? NewKey)> OpenAsync(string host, int port, RemoteCredentials credentials,
        string? pinnedFingerprint, string deviceName, CancellationToken cancellationToken)
    {
        var tcp = new TcpClient { NoDelay = true };
        string? seenFingerprint = null;
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connect.CancelAfter(ConnectTimeout);
                try
                {
                    await tcp.ConnectAsync(host, port, connect.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new StorageException(StorageErrorKind.Unreachable, $"{host} antwortet nicht (Port {port}).");
                }
            }

            var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            var channel = new RemoteChannel(tls);
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshake.CancelAfter(HandshakeTimeout);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    // self-signed by design: the pinned fingerprint is the trust, not a certificate authority
                    if (certificate is null)
                    {
                        return false;
                    }

                    seenFingerprint = RemoteCrypto.FingerprintOf(certificate);
                    return pinnedFingerprint is null || string.Equals(seenFingerprint, pinnedFingerprint, StringComparison.OrdinalIgnoreCase);
                },
            }, handshake.Token);

            var (serverDevice, newKey) = await SignInAsync(channel, credentials, seenFingerprint!, deviceName, handshake.Token);
            return (new RemoteClientConnection(tcp, channel, seenFingerprint!, serverDevice), newKey);
        }
        catch (AuthenticationException exception) when (pinnedFingerprint is not null && seenFingerprint is not null
            && !string.Equals(seenFingerprint, pinnedFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            tcp.Dispose();
            throw new StorageException(StorageErrorKind.Authentication,
                $"Der andere Computer hat sich verändert (Fingerabdruck stimmt nicht mehr). Wurde ThixSync auf {host} neu eingerichtet, die Verbindung " +
                "neu koppeln – sonst könnte jemand dazwischen sein.", exception);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new StorageException(StorageErrorKind.Unreachable, $"{host} hat die Anmeldung nicht rechtzeitig beantwortet.");
        }
        catch (Exception exception) when (exception is not StorageException and not OperationCanceledException)
        {
            tcp.Dispose();
            throw Map(exception, host, port);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>A network problem as a <see cref="StorageException"/> (German, no secrets).</summary>
    public static StorageException Map(Exception exception, string host, int port) => exception switch
    {
        StorageException storage => storage,
        SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain } =>
            new StorageException(StorageErrorKind.Unreachable, $"Den Computer „{host}“ gibt es im Netzwerk nicht (Name unbekannt).", exception),
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
            new StorageException(StorageErrorKind.Unreachable, $"{host} nimmt keine Verbindung an (Port {port}) – ist ThixSync Remote dort eingeschaltet?", exception),
        SocketException => new StorageException(StorageErrorKind.Unreachable, $"{host} ist nicht erreichbar (Port {port}).", exception),
        AuthenticationException => new StorageException(StorageErrorKind.Unreachable, $"Die verschlüsselte Verbindung zu {host} kam nicht zustande.", exception),
        _ => new StorageException(StorageErrorKind.Transient, $"Die Verbindung zu {host} wurde unterbrochen.", exception),
    };

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Channel.DisposeAsync();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException)
        {
        }

        _tcp.Dispose();
    }

    private static async Task<(string ServerDevice, RemoteCredentials.DeviceKey? NewKey)> SignInAsync(RemoteChannel channel, RemoteCredentials credentials,
        string fingerprint, string deviceName, CancellationToken cancellationToken)
    {
        var clientNonce = RandomNumberGenerator.GetBytes(RemoteCrypto.NonceBytes);
        var keyId = (credentials as RemoteCredentials.DeviceKey)?.KeyId;
        await channel.WriteJsonAsync(RemoteFrameType.Request,
            new HelloMessage(RemoteProtocol.Version, deviceName, AppInfo.PlatformName, keyId is null ? "pair" : "key", keyId, Convert.ToBase64String(clientNonce)),
            RemoteJson.Default.HelloMessage, cancellationToken);
        var hello = await channel.ReadJsonAsync(RemoteFrameType.Response, RemoteJson.Default.HelloReply, cancellationToken);
        var serverNonce = Convert.FromBase64String(hello.Nonce);
        var fingerprintBytes = RemoteCrypto.Utf8(fingerprint);
        if (credentials is RemoteCredentials.DeviceKey key)
        {
            var proof = RemoteCrypto.Proof(key.Key, RemoteCrypto.Utf8("key"), clientNonce, serverNonce, fingerprintBytes, RemoteCrypto.Utf8(key.KeyId));
            await channel.WriteJsonAsync(RemoteFrameType.Request, new ProofMessage(Convert.ToBase64String(proof)), RemoteJson.Default.ProofMessage, cancellationToken);
            await channel.ReadJsonAsync(RemoteFrameType.Response, RemoteJson.Default.AuthReply, cancellationToken);
            return (hello.Device, null);
        }

        var digits = ((RemoteCredentials.Pairing)credentials).Digits;
        var pairingKey = RemoteCrypto.PairingKey(digits, clientNonce, serverNonce);
        var pairProof = RemoteCrypto.Proof(pairingKey, RemoteCrypto.Utf8("pair-client"), clientNonce, serverNonce, fingerprintBytes, RemoteCrypto.Utf8(deviceName));
        await channel.WriteJsonAsync(RemoteFrameType.Request, new ProofMessage(Convert.ToBase64String(pairProof)), RemoteJson.Default.ProofMessage, cancellationToken);
        var paired = await channel.ReadJsonAsync(RemoteFrameType.Response, RemoteJson.Default.AuthReply, cancellationToken);
        if (paired is not { KeyId: { Length: > 0 } newKeyId, Key: { } encodedKey })
        {
            throw new InvalidDataException("The pairing answer has no key.");
        }

        var newKey = Convert.FromBase64String(encodedKey);
        var expected = RemoteCrypto.Proof(pairingKey, RemoteCrypto.Utf8("pair-server"), clientNonce, serverNonce, fingerprintBytes, RemoteCrypto.Utf8(newKeyId), newKey);
        if (!RemoteCrypto.Matches(expected, paired.Proof))
        {
            throw new StorageException(StorageErrorKind.Authentication, $"{hello.Device} konnte nicht beweisen, dass es den Kopplungscode kennt – Kopplung abgebrochen.");
        }

        return (hello.Device, new RemoteCredentials.DeviceKey(newKeyId, newKey));
    }
}
