using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;

namespace ThixSync.Core.Remote;

/// <summary>
/// The server of ThixSync Remote: a TCP listener (IPv4 and IPv6) on the port of the settings while Remote is on and the policy allows it,
/// a UDP responder for the discovery, the pairing code, the paired computers. Follows <see cref="SettingsService.Changed"/> by itself
/// (on/off, port); the shares are read for every request, so a change applies at once. Every connection is a <see cref="RemoteSession"/>.
/// </summary>
public static partial class RemoteServer
{
    private static readonly TimeSpan LastSeenInterval = TimeSpan.FromMinutes(5);

    private static readonly Lock Gate = new();
    private static readonly ConcurrentDictionary<RemoteSession, byte> Sessions = new();
    private static readonly ConcurrentDictionary<string, int> PeerConnections = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> LastSeenWritten = new(StringComparer.Ordinal);
    private static bool _started;
    private static Listener? _listener;
    private static ActivePairing? _pairing;
    private static int _connections;

    /// <summary>The UDP port of the discovery (tests use another one than a real ThixSync on the same computer).</summary>
    internal static int DiscoveryPort { get; set; } = RemoteProtocol.DefaultPort;

    public static partial void Start()
    {
        lock (Gate)
        {
            if (!_started)
            {
                SettingsService.Changed += OnSettingsChanged;
                _started = true;
            }
        }

        Apply(SettingsService.Get());
    }

    public static partial void Stop()
    {
        lock (Gate)
        {
            if (_started)
            {
                SettingsService.Changed -= OnSettingsChanged;
                _started = false;
            }

            StopListening();
        }
    }

    public static partial bool IsListening
    {
        get
        {
            lock (Gate)
            {
                return _listener is not null;
            }
        }
    }

    public static partial string Fingerprint => RemoteIdentity.Fingerprint;

    public static partial PairingCode CreatePairingCode()
    {
        AccessPolicy.EnsureRemoteAllowed();
        AccessPolicy.EnsureConfigurationEditable();
        if (!SettingsService.Get().Remote.IsEnabled)
        {
            throw new BusinessException("ThixSync Remote ist ausgeschaltet – zuerst in Einstellungen › Remote einschalten.");
        }

        var digits = RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8", CultureInfo.InvariantCulture);
        var expiresAt = AppClock.UtcNow.AddMinutes(RemoteProtocol.PairingMinutes);
        lock (Gate)
        {
            _pairing = new ActivePairing(digits, expiresAt);
        }

        return new PairingCode($"{digits[..4]}-{digits[4..]}", expiresAt);
    }

    public static partial void RemovePeer(string keyId)
    {
        AccessPolicy.EnsureConfigurationEditable();
        RemotePeerKeys.Remove(keyId);
        SettingsService.UpdatePeers(peers => [.. peers.Where(peer => peer.KeyId != keyId)]);
        LastSeenWritten.TryRemove(keyId, out _);
        foreach (var session in Sessions.Keys.Where(session => session.KeyId == keyId))
        {
            session.Close();
        }
    }

    public static partial Task<IReadOnlyList<RemoteComputer>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var targets = BroadcastAddresses().Select(address => new IPEndPoint(address, DiscoveryPort)).ToList();
        return DiscoverAsync(timeout, targets, includeSelf: false, cancellationToken);
    }

    /// <summary>Asks <paramref name="targets"/> who is there (tests: one address instead of the broadcast).</summary>
    internal static async Task<IReadOnlyList<RemoteComputer>> DiscoverAsync(TimeSpan timeout, IReadOnlyList<IPEndPoint> targets, bool includeSelf, CancellationToken cancellationToken)
    {
        if (AccessPolicy.IsRemoteDisabled)
        {
            return [];
        }

        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var question = JsonSerializer.SerializeToUtf8Bytes(new DiscoverMessage("discover", RemoteProtocol.Version), RemoteJson.Default.DiscoverMessage);
        foreach (var target in targets)
        {
            try
            {
                await udp.SendAsync(question, target, cancellationToken);
            }
            catch (SocketException exception)
            {
                Log.Warning($"Remote: the discovery could not be sent to {target} ({exception.SocketErrorCode})");
            }
        }

        var ownFingerprint = includeSelf || !IsListening ? null : RemoteIdentity.LoadedFingerprint;
        var found = new Dictionary<(string Host, int Port), RemoteComputer>();
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(timeout);
        while (true)
        {
            UdpReceiveResult answer;
            try
            {
                answer = await udp.ReceiveAsync(wait.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                continue; // e.g. "port unreachable" of a target without ThixSync (Windows reports it on the next receive)
            }

            if (ParseDiscover(answer.Buffer) is not { Thixsync: "here", Device: { Length: > 0 } device, Port: > 0 and <= 65535 } message
                || (ownFingerprint is not null && message.Fingerprint == ownFingerprint))
            {
                continue;
            }

            var host = answer.RemoteEndPoint.Address.ToString();
            found[(host, message.Port)] = new RemoteComputer(device, host, message.Port);
        }

        return [.. found.Values.OrderBy(computer => computer.DeviceName, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Tests: back to the start (no listener, no pairing code, nothing remembered).</summary>
    internal static void ResetForTests()
    {
        Stop();
        lock (Gate)
        {
            _pairing = null;
        }

        LastSeenWritten.Clear();
    }

    // ---- for the sessions ----

    /// <summary>Remote is on and allowed (checked again for every request).</summary>
    internal static bool IsServing => SettingsService.Get().Remote.IsEnabled && !AccessPolicy.IsRemoteDisabled;

    /// <summary>The key of a paired computer that is still in the settings; null when it was removed.</summary>
    internal static byte[]? PeerKey(string keyId) =>
        SettingsService.Get().Remote.Peers.Any(peer => peer.KeyId == keyId) ? RemotePeerKeys.Find(keyId) : null;

    /// <summary>Counts one try of the current pairing code and returns it; null when there is none (any more).</summary>
    internal static ActivePairing? TryPairing()
    {
        lock (Gate)
        {
            if (_pairing is not { } pairing)
            {
                return null;
            }

            if (pairing.ExpiresAt <= AppClock.UtcNow || pairing.Attempts >= RemoteProtocol.PairingAttempts)
            {
                _pairing = null;
                return null;
            }

            pairing.Attempts++;
            return pairing;
        }
    }

    /// <summary>A wrong code: after the last allowed try the code is void.</summary>
    internal static void PairingFailed(ActivePairing pairing)
    {
        lock (Gate)
        {
            if (ReferenceEquals(_pairing, pairing) && pairing.Attempts >= RemoteProtocol.PairingAttempts)
            {
                _pairing = null;
            }
        }

        Log.Warning("Remote: a computer tried to pair with a wrong code");
    }

    /// <summary>The right code: it pairs this one computer and ends. False when another computer used it a moment before.</summary>
    internal static bool CompletePairing(ActivePairing pairing)
    {
        lock (Gate)
        {
            if (!ReferenceEquals(_pairing, pairing))
            {
                return false;
            }

            _pairing = null;
            return true;
        }
    }

    /// <summary>Keeps a newly paired computer (its key protected, the peer in the settings).</summary>
    internal static void AddPeer(string deviceName, string keyId, byte[] key)
    {
        RemotePeerKeys.Add(keyId, key);
        var now = AppClock.UtcNow;
        SettingsService.UpdatePeers(peers => [.. peers, new RemotePeer { DeviceName = deviceName, KeyId = keyId, PairedAt = now, LastSeen = now }]);
        LastSeenWritten[keyId] = now;
        Log.Info($"Remote: paired with {deviceName}");
    }

    /// <summary>Remembers that a peer was here (written at most every few minutes).</summary>
    internal static void Seen(string keyId)
    {
        var now = AppClock.UtcNow;
        if (LastSeenWritten.TryGetValue(keyId, out var written) && now - written < LastSeenInterval)
        {
            return;
        }

        LastSeenWritten[keyId] = now;
        SettingsService.UpdatePeers(peers => [.. peers.Select(peer => peer.KeyId == keyId ? peer with { LastSeen = now } : peer)]);
    }

    /// <summary>One more connection of a peer; false when it has too many.</summary>
    internal static bool EnterPeer(string keyId)
    {
        while (true)
        {
            var count = PeerConnections.GetOrAdd(keyId, 0);
            if (count >= RemoteProtocol.MaxConnectionsPerPeer)
            {
                return false;
            }

            if (PeerConnections.TryUpdate(keyId, count + 1, count))
            {
                return true;
            }
        }
    }

    internal static void LeavePeer(string keyId) => PeerConnections.AddOrUpdate(keyId, 0, (_, count) => Math.Max(0, count - 1));

    // ---- listening ----

    private static void OnSettingsChanged(object? sender, AppSettings settings) => Apply(settings);

    private static void Apply(AppSettings settings)
    {
        var wanted = settings.Remote.IsEnabled && !AccessPolicy.IsRemoteDisabled;
        lock (Gate)
        {
            if (!_started)
            {
                return;
            }

            if (!wanted)
            {
                StopListening();
                return;
            }

            if (_listener is { } running && running.Port == settings.Remote.Port)
            {
                return;
            }

            StopListening();
            _listener = Listener.TryStart(settings.Remote.Port);
        }
    }

    private static void StopListening()
    {
        if (_listener is null)
        {
            return;
        }

        _listener.Dispose();
        _listener = null;
        foreach (var session in Sessions.Keys)
        {
            session.Close();
        }
    }

    private static async Task AcceptAsync(TcpListener tcp, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await tcp.AcceptTcpClientAsync(stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException exception)
            {
                Log.Warning($"Remote: accepting a connection failed ({exception.SocketErrorCode})");
                continue;
            }

            if (Interlocked.Increment(ref _connections) > RemoteProtocol.MaxConnections)
            {
                Interlocked.Decrement(ref _connections);
                client.Dispose();
                continue;
            }

            var session = new RemoteSession(client, stop);
            Sessions[session] = 0;
            _ = Task.Run(async () =>
            {
                try
                {
                    await session.RunAsync();
                }
                finally
                {
                    Sessions.TryRemove(session, out _);
                    Interlocked.Decrement(ref _connections);
                }
            }, CancellationToken.None);
        }
    }

    private static async Task AnswerDiscoveryAsync(UdpClient udp, int port, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var question = await udp.ReceiveAsync(stop);
                if (ParseDiscover(question.Buffer) is not { Thixsync: "discover" } || !IsServing)
                {
                    continue;
                }

                var answer = new DiscoverMessage("here", RemoteProtocol.Version, AppInfo.MachineName, port, RemoteIdentity.Fingerprint);
                await udp.SendAsync(JsonSerializer.SerializeToUtf8Bytes(answer, RemoteJson.Default.DiscoverMessage), question.RemoteEndPoint, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // a sender that went away (Windows reports "connection reset" for UDP): keep answering the others
            }
        }
    }

    private static DiscoverMessage? ParseDiscover(byte[] buffer)
    {
        if (buffer.Length is 0 or > 4096)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(buffer, RemoteJson.Default.DiscoverMessage);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<IPAddress> BroadcastAddresses()
    {
        var addresses = new List<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                    {
                        continue;
                    }

                    var address = unicast.Address.GetAddressBytes();
                    var mask = unicast.IPv4Mask.GetAddressBytes();
                    var broadcast = new byte[4];
                    for (var i = 0; i < 4; i++)
                    {
                        broadcast[i] = (byte)(address[i] | ~mask[i]);
                    }

                    addresses.Add(new IPAddress(broadcast));
                }
            }
        }
        catch (NetworkInformationException exception)
        {
            Log.Warning($"Remote: the network interfaces could not be read ({exception.Message})");
        }

        return [.. addresses.Distinct()];
    }

    /// <summary>The pairing code that is valid right now (its tries are counted).</summary>
    internal sealed class ActivePairing(string digits, DateTimeOffset expiresAt)
    {
        public string Digits { get; } = digits;

        public DateTimeOffset ExpiresAt { get; } = expiresAt;

        public int Attempts { get; set; }
    }

    /// <summary>A running TCP listener with its discovery responder.</summary>
    private sealed class Listener : IDisposable
    {
        private readonly TcpListener _tcp;
        private readonly UdpClient? _udp;
        private readonly CancellationTokenSource _stop = new();

        private Listener(int port, TcpListener tcp, UdpClient? udp)
        {
            Port = port;
            _tcp = tcp;
            _udp = udp;
            _ = Task.Run(() => AcceptAsync(_tcp, _stop.Token), CancellationToken.None);
            if (_udp is not null)
            {
                _ = Task.Run(() => AnswerDiscoveryAsync(_udp, port, _stop.Token), CancellationToken.None);
            }
        }

        public int Port { get; }

        public static Listener? TryStart(int port)
        {
            var tcp = OpenTcp(port);
            if (tcp is null)
            {
                return null;
            }

            Log.Info($"Remote: listening on port {port}");
            return new Listener(port, tcp, OpenDiscovery());
        }

        public void Dispose()
        {
            _stop.Cancel();
            _tcp.Stop();
            _udp?.Dispose();
            Log.Info($"Remote: stopped listening on port {Port}");
        }

        private static TcpListener? OpenTcp(int port)
        {
            try
            {
                if (Socket.OSSupportsIPv6)
                {
                    var dual = new TcpListener(IPAddress.IPv6Any, port);
                    dual.Server.DualMode = true;
                    dual.Start();
                    return dual;
                }
            }
            catch (SocketException exception) when (exception.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported)
            {
                // no IPv6 on this computer: IPv4 only
            }
            catch (SocketException exception)
            {
                Log.Warning($"Remote: port {port} could not be opened ({exception.SocketErrorCode})");
                return null;
            }

            try
            {
                var ipv4 = new TcpListener(IPAddress.Any, port);
                ipv4.Start();
                return ipv4;
            }
            catch (SocketException exception)
            {
                Log.Warning($"Remote: port {port} could not be opened ({exception.SocketErrorCode})");
                return null;
            }
        }

        private static UdpClient? OpenDiscovery()
        {
            try
            {
                var udp = new UdpClient(AddressFamily.InterNetwork);
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                return udp;
            }
            catch (SocketException exception)
            {
                Log.Warning($"Remote: the discovery port {DiscoveryPort} could not be opened ({exception.SocketErrorCode}); other computers find this one only by its address");
                return null;
            }
        }
    }
}
