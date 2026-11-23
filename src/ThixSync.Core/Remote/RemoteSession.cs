using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;
using ThixSync.Core.Storage.Providers.Local;

namespace ThixSync.Core.Remote;

/// <summary>
/// One connection of another computer to this one: TLS, the sign-in (pairing code or device key), then its requests one after the other
/// (see <see cref="RemoteProtocol"/>). Every request is checked again: Remote still on, the peer still paired, the share there, the path
/// inside the share, writes only on a share that is not read-only. The folders are read and written with <see cref="LocalStorage"/>.
/// </summary>
internal sealed class RemoteSession
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, LocalStorage> Storages = new(StringComparer.Ordinal);

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly TcpClient _client;
    private readonly CancellationTokenSource _cancel;
    private bool _entered;

    public RemoteSession(TcpClient client, CancellationToken stop)
    {
        _client = client;
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(stop);
    }

    /// <summary>The key id of the signed-in peer (null before the sign-in).</summary>
    public string? KeyId { get; private set; }

    private static string Machine => AppInfo.MachineName;

    /// <summary>Ends the connection now (Remote stopped, the peer was removed).</summary>
    public void Close()
    {
        try
        {
            _cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _client.Dispose();
    }

    public async Task RunAsync()
    {
        try
        {
            var token = _cancel.Token;
            _client.NoDelay = true;
            await using var tls = new SslStream(_client.GetStream(), leaveInnerStreamOpen: false);
            var channel = new RemoteChannel(tls);
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                handshake.CancelAfter(HandshakeTimeout);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = RemoteIdentity.Certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificateRequired = false,
                    CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
                }, handshake.Token);
                if (!await AuthenticateAsync(channel, handshake.Token))
                {
                    return;
                }
            }

            await ServeAsync(channel, token);
        }
        catch (Exception exception) when (exception is IOException or AuthenticationException or SocketException or OperationCanceledException
            or ObjectDisposedException or InvalidDataException or JsonException or FormatException or StorageException)
        {
            // the other side went away or broke the protocol, or Remote stopped: the connection ends
        }
        finally
        {
            if (_entered)
            {
                RemoteServer.LeavePeer(KeyId!);
            }

            _client.Dispose();
            _cancel.Dispose();
        }
    }

    // ---- sign-in ----

    private async Task<bool> AuthenticateAsync(RemoteChannel channel, CancellationToken token)
    {
        var hello = await channel.ReadJsonAsync(RemoteFrameType.Request, RemoteJson.Default.HelloMessage, token);
        if (hello.Version != RemoteProtocol.Version)
        {
            await channel.WriteErrorAsync(StorageErrorKind.Unknown,
                $"{Machine} spricht eine andere Version von ThixSync Remote – bitte ThixSync auf beiden Computern aktualisieren.", token);
            return false;
        }

        if (!RemoteServer.IsServing)
        {
            await channel.WriteErrorAsync(StorageErrorKind.Unreachable, $"ThixSync Remote ist auf {Machine} ausgeschaltet.", token);
            return false;
        }

        var clientNonce = Convert.FromBase64String(hello.Nonce);
        if (clientNonce.Length != RemoteCrypto.NonceBytes)
        {
            return false;
        }

        var serverNonce = RandomNumberGenerator.GetBytes(RemoteCrypto.NonceBytes);
        await channel.WriteJsonAsync(RemoteFrameType.Response, new HelloReply(RemoteProtocol.Version, Machine, Convert.ToBase64String(serverNonce)),
            RemoteJson.Default.HelloReply, token);
        var proof = await channel.ReadJsonAsync(RemoteFrameType.Request, RemoteJson.Default.ProofMessage, token);
        var fingerprint = RemoteCrypto.Utf8(RemoteIdentity.Fingerprint);
        return hello.Auth switch
        {
            "key" => await SignInWithKeyAsync(channel, hello, proof, clientNonce, serverNonce, fingerprint, token),
            "pair" => await PairAsync(channel, hello, proof, clientNonce, serverNonce, fingerprint, token),
            _ => false,
        };
    }

    private async Task<bool> SignInWithKeyAsync(RemoteChannel channel, HelloMessage hello, ProofMessage proof, byte[] clientNonce, byte[] serverNonce,
        byte[] fingerprint, CancellationToken token)
    {
        var keyId = hello.KeyId ?? "";
        var key = keyId.Length == 0 ? null : RemoteServer.PeerKey(keyId);
        var expected = key is null ? null : RemoteCrypto.Proof(key, RemoteCrypto.Utf8("key"), clientNonce, serverNonce, fingerprint, RemoteCrypto.Utf8(keyId));
        if (expected is null || !RemoteCrypto.Matches(expected, proof.Proof))
        {
            await channel.WriteErrorAsync(StorageErrorKind.Authentication,
                $"Dieser Computer ist mit {Machine} nicht (mehr) gekoppelt – bitte die Verbindung mit einem neuen Kopplungscode koppeln.", token);
            return false;
        }

        if (!RemoteServer.EnterPeer(keyId))
        {
            await channel.WriteErrorAsync(StorageErrorKind.Transient, $"Zu viele gleichzeitige Verbindungen zu {Machine} – gleich noch einmal versuchen.", token);
            return false;
        }

        _entered = true;
        KeyId = keyId;
        await channel.WriteJsonAsync(RemoteFrameType.Response, new AuthReply(), RemoteJson.Default.AuthReply, token);
        return true;
    }

    private async Task<bool> PairAsync(RemoteChannel channel, HelloMessage hello, ProofMessage proof, byte[] clientNonce, byte[] serverNonce,
        byte[] fingerprint, CancellationToken token)
    {
        if (RemoteServer.TryPairing() is not { } pairing)
        {
            await channel.WriteErrorAsync(StorageErrorKind.Authentication,
                $"Auf {Machine} ist gerade kein Kopplungscode gültig – dort einen neuen Code anzeigen lassen (Einstellungen › Remote).", token);
            return false;
        }

        var pairingKey = RemoteCrypto.PairingKey(pairing.Digits, clientNonce, serverNonce);
        var expected = RemoteCrypto.Proof(pairingKey, RemoteCrypto.Utf8("pair-client"), clientNonce, serverNonce, fingerprint, RemoteCrypto.Utf8(hello.Device));
        if (!RemoteCrypto.Matches(expected, proof.Proof))
        {
            RemoteServer.PairingFailed(pairing);
            await channel.WriteErrorAsync(StorageErrorKind.Authentication, "Der Kopplungscode stimmt nicht.", token);
            return false;
        }

        if (!RemoteServer.CompletePairing(pairing))
        {
            await channel.WriteErrorAsync(StorageErrorKind.Authentication, "Dieser Kopplungscode wurde gerade schon verwendet – bitte einen neuen anzeigen lassen.", token);
            return false;
        }

        var keyId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        var key = RandomNumberGenerator.GetBytes(RemoteCrypto.KeyBytes);
        var deviceName = hello.Device.Trim();
        RemoteServer.AddPeer(deviceName.Length == 0 ? "Computer" : deviceName[..Math.Min(deviceName.Length, 100)], keyId, key);
        RemoteServer.EnterPeer(keyId);
        _entered = true;
        KeyId = keyId;
        var serverProof = RemoteCrypto.Proof(pairingKey, RemoteCrypto.Utf8("pair-server"), clientNonce, serverNonce, fingerprint, RemoteCrypto.Utf8(keyId), key);
        await channel.WriteJsonAsync(RemoteFrameType.Response, new AuthReply(keyId, Convert.ToBase64String(key), Convert.ToBase64String(serverProof)),
            RemoteJson.Default.AuthReply, token);
        return true;
    }

    // ---- requests ----

    private async Task ServeAsync(RemoteChannel channel, CancellationToken token)
    {
        while (true)
        {
            RemoteFrame frame;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                idle.CancelAfter(IdleTimeout);
                frame = await channel.ReadAsync(idle.Token);
            }

            if (frame.Type != RemoteFrameType.Request)
            {
                return;
            }

            var request = RemoteChannel.Parse(frame, RemoteJson.Default.RemoteRequest);
            if (Refusal() is { } refusal)
            {
                await channel.WriteErrorAsync(refusal.Kind, refusal.Message, token);
                return;
            }

            RemoteServer.Seen(KeyId!);
            try
            {
                await HandleAsync(channel, request, token);
            }
            catch (StorageException exception) when (exception is not RemoteRefusedException)
            {
                await channel.WriteErrorAsync(exception.Kind, exception.Message, token);
            }
        }
    }

    private StorageException? Refusal()
    {
        if (!RemoteServer.IsServing)
        {
            return new StorageException(StorageErrorKind.Unreachable, $"ThixSync Remote ist auf {Machine} ausgeschaltet.");
        }

        return RemoteServer.PeerKey(KeyId!) is null
            ? new StorageException(StorageErrorKind.Authentication, $"Dieser Computer ist mit {Machine} nicht mehr gekoppelt – bitte neu koppeln.")
            : null;
    }

    private static Task HandleAsync(RemoteChannel channel, RemoteRequest request, CancellationToken token) => request.Op switch
    {
        "shares" => SharesAsync(channel, token),
        "share" => ShareAsync(channel, request, token),
        "list" => ListAsync(channel, request, token),
        "get" => GetAsync(channel, request, token),
        "read" => ReadAsync(channel, request, token),
        "write" => WriteAsync(channel, request, token),
        "mkdir" => CreateFolderAsync(channel, request, token),
        "delete" => DeleteAsync(channel, request, token),
        "move" => MoveAsync(channel, request, token),
        _ => throw new StorageException(StorageErrorKind.Unknown, "Diese Anfrage kennt der andere Computer nicht – bitte ThixSync aktualisieren."),
    };

    private static Task SharesAsync(RemoteChannel channel, CancellationToken token)
    {
        var shares = SettingsService.Get().Remote.Shares.Select(share => new WireShare(share.Name, share.IsReadOnly)).ToList();
        return channel.WriteJsonAsync(RemoteFrameType.Response, new RemoteReply { Shares = shares }, RemoteJson.Default.RemoteReply, token);
    }

    private static Task ShareAsync(RemoteChannel channel, RemoteRequest request, CancellationToken token)
    {
        var (storage, share) = Open(request.Share);
        var features = storage.Capabilities.Features & ~StorageFeatures.LocalFileSystem;
        return channel.WriteJsonAsync(RemoteFrameType.Response, new RemoteReply { Features = (int)features, ReadOnly = share.IsReadOnly }, RemoteJson.Default.RemoteReply, token);
    }

    private static async Task ListAsync(RemoteChannel channel, RemoteRequest request, CancellationToken token)
    {
        var (storage, _) = Open(request.Share);
        var folder = Inside(storage, request.Path);
        if (folder.Length > 0 && !Directory.Exists(storage.GetLocalPath(folder)))
        {
            throw new StorageException(StorageErrorKind.NotFound, $"Den Ordner gibt es nicht ({folder}).");
        }

        // a batch ends after ListBatch entries or that many characters of paths (a frame stays far below MaxFrameBytes)
        const int MaxBatchCharacters = 256 * 1024;
        var problems = new ConcurrentQueue<WireProblem>();
        var batch = new List<WireEntry>(RemoteProtocol.ListBatch);
        var characters = 0;
        async Task SendBatchAsync()
        {
            await SendProblemsAsync(channel, problems, token);
            if (batch.Count > 0)
            {
                await channel.WriteJsonAsync(RemoteFrameType.Entries, batch, RemoteJson.Default.ListWireEntry, token);
            }

            batch.Clear();
            characters = 0;
        }

        async Task AddAsync(WireEntry entry)
        {
            batch.Add(entry);
            characters += entry.P.Length;
            if (batch.Count >= RemoteProtocol.ListBatch || characters >= MaxBatchCharacters)
            {
                await SendBatchAsync();
            }
        }

        if (request.Recursive)
        {
            await foreach (var entry in storage.ListAsync(folder, (path, exception) => problems.Enqueue(ProblemOf(path, exception)), token))
            {
                await AddAsync(WireEntry.From(entry));
            }
        }
        else
        {
            foreach (var entry in ReadChildren(storage, folder))
            {
                await AddAsync(entry);
            }
        }

        await SendBatchAsync();
        await channel.WriteAsync(RemoteFrameType.End, [], token);
    }

    private static async Task GetAsync(RemoteChannel channel, RemoteRequest request, CancellationToken token)
    {
        var (storage, _) = Open(request.Share);
        var path = Inside(storage, request.Path);
        var entry = await storage.GetAsync(path, token);
        await channel.WriteJsonAsync(RemoteFrameType.Response, new RemoteReply { Entry = entry is { } found ? WireEntry.From(found) : null },
            RemoteJson.Default.RemoteReply, token);
    }

    private static async Task ReadAsync(RemoteChannel channel, RemoteRequest request, CancellationToken token)
    {
        var (storage, _) = Open(request.Share);
        var path = Inside(storage, request.Path);
        await using var file = await storage.OpenReadAsync(path, token);
        long size;
        try
        {
            size = file.Length;
        }
        catch (IOException exception)
        {
            throw LocalStorage.Map(exception, path);
        }

        await channel.WriteJsonAsync(RemoteFrameType.Response, new RemoteReply { Size = size }, RemoteJson.Default.RemoteReply, token);
        while (true)
        {
            int read;
            try
            {
                read = await file.ReadAsync(channel.DataBuffer, token);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                var mapped = LocalStorage.Map(exception, path);
                await channel.WriteErrorAsync(mapped.Kind, mapped.Message, token);
                return;
            }

            if (read == 0)
            {
                break;
            }

            await channel.WriteDataAsync(read, token);
        }

        await channel.WriteAsync(RemoteFrameType.End, [], token);
    }

    private static async Task WriteAsync(RemoteChannel channel, RemoteRequest request, CancellationToken token)
    {
        var (storage, share) = Open(request.Share);
        EnsureWritable(share);
        var path = Inside(storage, request.Path);
        if (path.Length == 0 || request.Length < 0)
        {
            throw new StorageException(StorageErrorKind.InvalidName, "Eine Datei braucht einen Namen.");
        }

        var modified = TimeOf(request.Modified);
        await channel.WriteJsonAsync(RemoteFrameType.Response, new RemoteReply(), RemoteJson.Default.RemoteReply, token);
        var incoming = new IncomingDataStream(channel, request.Length);
        StorageEntry entry;
        try
        {
            entry = await storage.WriteAsync(path, incoming, request.Length, modified, null, token);
        }
        catch (Exception exception) when (!incoming.IsBroken && exception is StorageException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // the file could not be written: read the rest of the data, so the connection can take the next request
            await incoming.DrainAsync(token);
            var mapped = LocalStorage.Map(exception, path);
            await channel.WriteErrorAsync(mapped.Kind, mapped.Message, token);
            return;
        }

        await channel.WriteJsonAsync(RemoteFrameType.Response, new RemoteReply { Entry = WireEntry.From(entry) }, RemoteJson.Default.RemoteReply, token);
    }

    private static async Task CreateFolderAsync(RemoteChannel channel, RemoteRequest request, CancellationToken token)
    {
        var (storage, share) = Open(request.Share);
        EnsureWritable(share);
        await storage.CreateFolderAsync(Inside(storage, request.Path), token);
        await channel.WriteJsonAsync(RemoteFrameType.Response, new RemoteReply(), RemoteJson.Default.RemoteReply, token);
    }

    private static async Task DeleteAsync(RemoteChannel channel, RemoteRequest request, CancellationToken token)
    {
        var (storage, share) = Open(request.Share);
        EnsureWritable(share);
        await storage.DeleteAsync(Inside(storage, request.Path), request.IsFolder, token);
        await channel.WriteJsonAsync(RemoteFrameType.Response, new RemoteReply(), RemoteJson.Default.RemoteReply, token);
    }

    private static async Task MoveAsync(RemoteChannel channel, RemoteRequest request, CancellationToken token)
    {
        var (storage, share) = Open(request.Share);
        EnsureWritable(share);
        var from = Inside(storage, request.Path);
        var to = Inside(storage, request.To);
        if (from.Length == 0 || to.Length == 0)
        {
            throw new StorageException(StorageErrorKind.AccessDenied, "Der Wurzelordner einer Freigabe wird nicht verschoben.");
        }

        await storage.MoveAsync(from, to, token);
        await channel.WriteJsonAsync(RemoteFrameType.Response, new RemoteReply(), RemoteJson.Default.RemoteReply, token);
    }

    // ---- shares and paths ----

    private static (LocalStorage Storage, RemoteShare Share) Open(string? name)
    {
        var share = SettingsService.Get().Remote.Shares.FirstOrDefault(share => string.Equals(share.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new StorageException(StorageErrorKind.NotFound, $"Die Freigabe „{name}“ gibt es auf {Machine} nicht.");
        if (!Directory.Exists(share.LocalPath))
        {
            throw new StorageException(StorageErrorKind.NotFound, $"Der Ordner der Freigabe „{share.Name}“ fehlt auf {Machine}.");
        }

        var storage = Storages.GetOrAdd(share.LocalPath, localPath => new LocalStorage(localPath, ConnectionKind.Local, share.Name));
        return (storage, share);
    }

    private static void EnsureWritable(RemoteShare share)
    {
        if (share.IsReadOnly)
        {
            throw new StorageException(StorageErrorKind.AccessDenied, $"Die Freigabe „{share.Name}“ ist auf {Machine} nur zum Lesen freigegeben.");
        }
    }

    /// <summary>
    /// The path inside the share, or AccessDenied: no "..", no absolute path, no drive letter or stream (':'), no device name of Windows,
    /// nothing that ends outside the root, no link or junction on the way.
    /// </summary>
    internal static string Inside(LocalStorage storage, string? path)
    {
        var text = path ?? "";
        if (text.StartsWith('/') || text.StartsWith('\\') || text.Contains(':') || text.Contains('\0'))
        {
            throw Outside();
        }

        string normalized;
        try
        {
            normalized = StoragePath.Normalize(text);
        }
        catch (ArgumentException)
        {
            throw Outside();
        }

        if (normalized.Length == 0)
        {
            return normalized;
        }

        var parts = normalized.Split('/');
        if (OperatingSystem.IsWindows() && parts.Any(part => ReservedNames.Contains(part.Split('.')[0].TrimEnd())))
        {
            throw new StorageException(StorageErrorKind.InvalidName, $"Dieser Name ist unter Windows nicht erlaubt ({normalized}).");
        }

        var root = storage.Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!Path.GetFullPath(storage.GetLocalPath(normalized)).StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            throw Outside();
        }

        var current = storage.Root;
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                break; // not there (yet): nothing below it can be a link
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new StorageException(StorageErrorKind.AccessDenied, $"Verknüpfungen werden nicht verfolgt ({normalized}).");
            }
        }

        return normalized;
    }

    private static StorageException Outside() => new(StorageErrorKind.AccessDenied, "Der Pfad führt aus der Freigabe heraus.");

    private static DateTimeOffset TimeOf(long unixMilliseconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new StorageException(StorageErrorKind.Unknown, "Die Änderungszeit ist ungültig.");
        }
    }

    private static List<WireEntry> ReadChildren(LocalStorage storage, string folder)
    {
        var prefix = folder.Length == 0 ? "" : folder + "/";
        try
        {
            return [.. new DirectoryInfo(storage.GetLocalPath(folder))
                .EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(info => !info.Name.StartsWith(LocalStorage.TempPrefix, StringComparison.Ordinal))
                .Select(info => new WireEntry(prefix + info.Name, info is DirectoryInfo, info is FileInfo file ? file.Length : 0,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    (info.Attributes & FileAttributes.Hidden) != 0 || info.Name.StartsWith('.'), (info.Attributes & FileAttributes.System) != 0))];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw LocalStorage.Map(exception, folder);
        }
    }

    private static WireProblem ProblemOf(string path, Exception exception)
    {
        var mapped = LocalStorage.Map(exception, path);
        return new WireProblem(path, (int)mapped.Kind, mapped.Message);
    }

    private static async Task SendProblemsAsync(RemoteChannel channel, ConcurrentQueue<WireProblem> problems, CancellationToken token)
    {
        while (problems.TryDequeue(out var problem))
        {
            await channel.WriteJsonAsync(RemoteFrameType.Problem, problem, RemoteJson.Default.WireProblem, token);
        }
    }

    /// <summary>The data frames of a write as a stream, up to the end frame (exactly the announced length).</summary>
    private sealed class IncomingDataStream(RemoteChannel channel, long length) : Stream
    {
        private ReadOnlyMemory<byte> _pending;
        private long _received;
        private bool _ended;

        /// <summary>The connection broke or the other side broke the protocol: it cannot take another request.</summary>
        public bool IsBroken { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get => _received - _pending.Length; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_pending.IsEmpty)
            {
                if (_ended || !await NextAsync(cancellationToken))
                {
                    return 0;
                }
            }

            var count = Math.Min(buffer.Length, _pending.Length);
            _pending[..count].CopyTo(buffer);
            _pending = _pending[count..];
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        /// <summary>Reads and drops what is left up to the end frame.</summary>
        public async Task DrainAsync(CancellationToken cancellationToken)
        {
            _pending = default;
            while (!_ended)
            {
                try
                {
                    await NextAsync(cancellationToken);
                }
                catch (StorageException)
                {
                    // the length did not match: the end frame was read, nothing is left
                }

                _pending = default;
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <summary>The next frame: data (true) or the end (false).</summary>
        private async Task<bool> NextAsync(CancellationToken cancellationToken)
        {
            RemoteFrame frame;
            try
            {
                frame = await channel.ReadAsync(cancellationToken);
            }
            catch
            {
                IsBroken = true;
                throw;
            }

            if (frame.Type == RemoteFrameType.End)
            {
                _ended = true;
                return _received == length
                    ? false
                    : throw new StorageException(StorageErrorKind.ChangedWhileReading, "Die Datei hat sich beim Senden geändert (andere Größe).");
            }

            _received += frame.Payload.Length;
            if (frame.Type != RemoteFrameType.Data || _received > length)
            {
                IsBroken = true;
                throw new InvalidDataException("The other side sent more or other data than it announced.");
            }

            _pending = frame.Payload;
            return true;
        }
    }
}
