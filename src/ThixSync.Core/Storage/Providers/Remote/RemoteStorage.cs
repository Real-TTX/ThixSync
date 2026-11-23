using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Text.Json;
using ThixSync.Core.Business;
using ThixSync.Core.Models;
using ThixSync.Core.Remote;

namespace ThixSync.Core.Storage.Providers.Remote;

/// <summary>
/// A folder of a share of another computer with ThixSync (ThixSync Remote, see <see cref="RemoteProtocol"/>). Up to
/// <see cref="PoolSize"/> TLS connections at once (one request at a time each); the first connection pairs with the code when the
/// secret is one and keeps the device key instead. The certificate of the other computer is pinned by its fingerprint (learned at the
/// first successful sign-in, <see cref="ConnectionFields.Fingerprint"/>).
/// </summary>
internal sealed class RemoteStorage : IStorage
{
    public const int PoolSize = 8;

    private static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(60);

    private readonly StorageContext _context;
    private readonly string _host;
    private readonly int _port;
    private readonly string _share;
    private readonly string _folder;
    private readonly SemaphoreSlim _slots = new(PoolSize, PoolSize);
    private readonly SemaphoreSlim _pairingGate = new(1, 1);
    private readonly ConcurrentBag<RemoteClientConnection> _idle = [];
    private readonly Lock _gate = new();
    private RemoteCredentials? _credentials;
    private string? _pinnedFingerprint;
    private string _serverDevice;
    private volatile bool _disposed;

    public RemoteStorage(StorageContext context)
    {
        _context = context;
        var connection = context.Connection;
        _host = connection.Get(ConnectionFields.Host).Trim();
        _port = int.TryParse(ConnectionCatalog.Value(connection, ConnectionFields.Port), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            ? port
            : RemoteProtocol.DefaultPort;
        _share = connection.Get(ConnectionFields.RemoteShare).Trim();
        _folder = StoragePath.Normalize(context.Folder);
        _credentials = RemoteCredentials.Parse(context.Secret);
        var fingerprint = connection.Get(ConnectionFields.Fingerprint).Trim();
        _pinnedFingerprint = fingerprint.Length > 0 ? fingerprint : null;
        _serverDevice = connection.Get(ConnectionFields.RemoteDevice);
        DisplayName = $"{(_serverDevice.Length > 0 ? _serverDevice : _host)}/{_share}" + (_folder.Length > 0 ? $"/{_folder}" : "");
    }

    public ConnectionKind Kind => ConnectionKind.ThixSyncRemote;

    public StorageCapabilities Capabilities { get; private set; } = new()
    {
        Features = StorageFeatures.SetModifiedTime | StorageFeatures.Move | StorageFeatures.RealFolders,
        MaxParallelism = PoolSize,
        InvalidNameCharacters = "<>:\"|?*\\",
    };

    public string DisplayName { get; }

    public async Task ConnectAsync(bool createRoot, CancellationToken cancellationToken)
    {
        if (_host.Length == 0 || _share.Length == 0)
        {
            throw new StorageException(StorageErrorKind.NotFound, "Computer und Freigabe der Verbindung fehlen.");
        }

        var share = await RequestAsync(new RemoteRequest { Op = "share", Share = _share }, cancellationToken);
        var serverFeatures = (StorageFeatures)share.Features;
        Capabilities = Capabilities with
        {
            Features = StorageFeatures.SetModifiedTime | StorageFeatures.Move | StorageFeatures.RealFolders
                | (serverFeatures & (StorageFeatures.CaseSensitive | StorageFeatures.CoarseTime)),
        };
        if (_folder.Length == 0)
        {
            return;
        }

        var root = await RequestAsync(new RemoteRequest { Op = "get", Share = _share, Path = _folder }, cancellationToken);
        if (root.Entry is { F: true })
        {
            return;
        }

        if (root.Entry is not null)
        {
            throw new StorageException(StorageErrorKind.NotFound, $"{DisplayName} ist eine Datei, kein Ordner.");
        }

        if (!createRoot)
        {
            throw new StorageException(StorageErrorKind.NotFound, $"Den Ordner {DisplayName} gibt es nicht.");
        }

        await RequestAsync(new RemoteRequest { Op = "mkdir", Share = _share, Path = _folder }, cancellationToken);
    }

    public async IAsyncEnumerable<StorageEntry> ListAsync(string folder, Action<string, Exception>? onProblem, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var connection = await AcquireAsync(cancellationToken);
        var reusable = false;
        try
        {
            await SendAsync(connection, new RemoteRequest { Op = "list", Share = _share, Path = Full(folder), Recursive = true }, cancellationToken);
            while (true)
            {
                var (entries, problem, isEnd) = await ReadListFrameAsync(connection, cancellationToken);
                if (isEnd)
                {
                    reusable = true;
                    yield break;
                }

                if (problem is not null)
                {
                    onProblem?.Invoke(Relative(problem.Path), new StorageException(KindOf(problem.Kind), problem.Message));
                    continue;
                }

                foreach (var entry in entries!)
                {
                    if (StoragePath.IsInside(entry.P, _folder) && entry.P.Length > _folder.Length)
                    {
                        yield return entry.ToEntry(Relative(entry.P));
                    }
                }
            }
        }
        finally
        {
            await ReleaseAsync(connection, reusable);
        }
    }

    public async Task<StorageEntry?> GetAsync(string path, CancellationToken cancellationToken)
    {
        var relative = StoragePath.Normalize(path);
        var reply = await RequestAsync(new RemoteRequest { Op = "get", Share = _share, Path = Full(relative) }, cancellationToken);
        return reply.Entry?.ToEntry(relative);
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        var connection = await AcquireAsync(cancellationToken);
        try
        {
            await SendAsync(connection, new RemoteRequest { Op = "read", Share = _share, Path = Full(path) }, cancellationToken);
            var reply = await ReadReplyAsync(connection, cancellationToken);
            return new RemoteReadStream(this, connection, reply.Size);
        }
        catch (RemoteRefusedException exception)
        {
            await ReleaseAsync(connection, IsReusableAfter(exception));
            throw;
        }
        catch
        {
            await ReleaseAsync(connection, false);
            throw;
        }
    }

    public async Task<StorageEntry> WriteAsync(string path, Stream content, long length, DateTimeOffset modified, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var relative = StoragePath.Normalize(path);
        var connection = await AcquireAsync(cancellationToken);
        var reusable = false;
        try
        {
            var request = new RemoteRequest { Op = "write", Share = _share, Path = Full(relative), Length = length, Modified = modified.ToUnixTimeMilliseconds() };
            await SendAsync(connection, request, cancellationToken);
            await ReadReplyAsync(connection, cancellationToken);
            await SendContentAsync(connection, content, length, progress, cancellationToken);
            var reply = await ReadReplyAsync(connection, cancellationToken);
            reusable = true;
            return reply.Entry?.ToEntry(relative) ?? throw new StorageException(StorageErrorKind.Unknown, "Der andere Computer hat keinen Eintrag geschickt.");
        }
        catch (RemoteRefusedException exception)
        {
            reusable = IsReusableAfter(exception);
            throw;
        }
        finally
        {
            await ReleaseAsync(connection, reusable);
        }
    }

    public Task CreateFolderAsync(string path, CancellationToken cancellationToken) =>
        RequestAsync(new RemoteRequest { Op = "mkdir", Share = _share, Path = Full(path) }, cancellationToken);

    public Task DeleteAsync(string path, bool isFolder, CancellationToken cancellationToken)
    {
        var relative = StoragePath.Normalize(path);
        if (relative.Length == 0)
        {
            throw new StorageException(StorageErrorKind.AccessDenied, "Der Wurzelordner eines Auftrags wird nie gelöscht.");
        }

        return RequestAsync(new RemoteRequest { Op = "delete", Share = _share, Path = Full(relative), IsFolder = isFolder }, cancellationToken);
    }

    public Task MoveAsync(string fromPath, string toPath, CancellationToken cancellationToken) =>
        RequestAsync(new RemoteRequest { Op = "move", Share = _share, Path = Full(fromPath), To = Full(toPath) }, cancellationToken);

    /// <summary>The folders directly below <paramref name="folder"/> (relative to the root of this storage) – the folder picker.</summary>
    public async Task<IReadOnlyList<string>> ListFoldersAsync(string folder, CancellationToken cancellationToken)
    {
        var connection = await AcquireAsync(cancellationToken);
        var reusable = false;
        var folders = new List<string>();
        try
        {
            await SendAsync(connection, new RemoteRequest { Op = "list", Share = _share, Path = Full(folder), Recursive = false }, cancellationToken);
            while (true)
            {
                var (entries, _, isEnd) = await ReadListFrameAsync(connection, cancellationToken);
                if (isEnd)
                {
                    break;
                }

                folders.AddRange(entries?.Where(entry => entry.F && !entry.H).Select(entry => StoragePath.NameOf(entry.P)) ?? []);
            }

            reusable = true;
        }
        catch (RemoteRefusedException exception)
        {
            reusable = IsReusableAfter(exception);
            throw;
        }
        finally
        {
            await ReleaseAsync(connection, reusable);
        }

        return [.. folders.Order(StringComparer.CurrentCultureIgnoreCase)];
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        while (_idle.TryTake(out var connection))
        {
            await connection.DisposeAsync();
        }
    }

    // ---- the pool ----

    /// <summary>A signed-in connection for one request (a free one of the pool, or a new one); give it back with <see cref="ReleaseAsync"/>.</summary>
    internal async Task<RemoteClientConnection> AcquireAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (AccessPolicy.IsRemoteDisabled)
        {
            throw new StorageException(StorageErrorKind.AccessDenied, "ThixSync Remote wurde von einem Administrator deaktiviert (Richtlinie DisableRemote).");
        }

        await _slots.WaitAsync(cancellationToken);
        try
        {
            while (_idle.TryTake(out var idle))
            {
                if (DateTimeOffset.UtcNow - idle.LastUsed < IdleLimit)
                {
                    return idle;
                }

                await idle.DisposeAsync();
            }

            return await OpenConnectionAsync(cancellationToken);
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    /// <summary>Gives a connection back: into the pool when it can take the next request, otherwise it is closed.</summary>
    internal async ValueTask ReleaseAsync(RemoteClientConnection connection, bool reusable)
    {
        try
        {
            if (reusable && !_disposed)
            {
                connection.LastUsed = DateTimeOffset.UtcNow;
                _idle.Add(connection);
                return;
            }

            await connection.DisposeAsync();
        }
        finally
        {
            _slots.Release();
        }
    }

    internal StorageException MapNetwork(Exception exception) => RemoteClientConnection.Map(exception, _host, _port);

    private async Task<RemoteClientConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        if (_credentials is RemoteCredentials.Pairing)
        {
            await _pairingGate.WaitAsync(cancellationToken);
            try
            {
                if (_credentials is RemoteCredentials.Pairing)
                {
                    return await OpenAndLearnAsync(_credentials, cancellationToken);
                }
            }
            finally
            {
                _pairingGate.Release();
            }
        }

        var credentials = _credentials ?? throw new StorageException(StorageErrorKind.Authentication,
            "Bitte den Kopplungscode des anderen Computers eingeben (dort: Einstellungen › Remote › Computer koppeln).");
        return await OpenAndLearnAsync(credentials, cancellationToken);
    }

    /// <summary>Opens a connection and keeps what it learned: the fingerprint, the name of the other computer, a new device key.</summary>
    private async Task<RemoteClientConnection> OpenAndLearnAsync(RemoteCredentials credentials, CancellationToken cancellationToken)
    {
        string? pinned;
        lock (_gate)
        {
            pinned = _pinnedFingerprint;
        }

        var (connection, newKey) = await RemoteClientConnection.OpenAsync(_host, _port, credentials, pinned, _context.DeviceName, cancellationToken);
        var learnFingerprint = false;
        var learnDevice = false;
        lock (_gate)
        {
            if (newKey is not null)
            {
                _credentials = newKey;
            }

            if (_pinnedFingerprint is null)
            {
                _pinnedFingerprint = connection.Fingerprint;
                learnFingerprint = true;
            }

            if (_serverDevice != connection.ServerDevice)
            {
                _serverDevice = connection.ServerDevice;
                learnDevice = true;
            }
        }

        if (newKey is not null)
        {
            await _context.SaveSecretAsync(newKey.Secret, CancellationToken.None);
        }

        if (learnFingerprint)
        {
            await _context.SaveSettingAsync(ConnectionFields.Fingerprint, connection.Fingerprint, CancellationToken.None);
        }

        if (learnDevice)
        {
            await _context.SaveSettingAsync(ConnectionFields.RemoteDevice, connection.ServerDevice, CancellationToken.None);
        }

        return connection;
    }

    // ---- requests ----

    /// <summary>A request with one answer; a connection of the pool that went stale while it waited is replaced once.</summary>
    private async Task<RemoteReply> RequestAsync(RemoteRequest request, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var connection = await AcquireAsync(cancellationToken);
            var wasIdle = DateTimeOffset.UtcNow - connection.LastUsed > TimeSpan.FromSeconds(1);
            var reusable = false;
            try
            {
                await connection.Channel.WriteJsonAsync(RemoteFrameType.Request, request, RemoteJson.Default.RemoteRequest, cancellationToken);
                var reply = await connection.Channel.ReadJsonAsync(RemoteFrameType.Response, RemoteJson.Default.RemoteReply, cancellationToken);
                reusable = true;
                return reply;
            }
            catch (RemoteRefusedException exception)
            {
                reusable = IsReusableAfter(exception);
                throw;
            }
            catch (Exception exception) when (IsNetworkProblem(exception) && wasIdle && attempt == 1 && !cancellationToken.IsCancellationRequested)
            {
                // the other computer closed the idle connection: try once more on a new one
            }
            catch (Exception exception) when (IsNetworkProblem(exception))
            {
                throw MapNetwork(exception);
            }
            finally
            {
                await ReleaseAsync(connection, reusable);
            }
        }
    }

    private async Task SendAsync(RemoteClientConnection connection, RemoteRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await connection.Channel.WriteJsonAsync(RemoteFrameType.Request, request, RemoteJson.Default.RemoteRequest, cancellationToken);
        }
        catch (Exception exception) when (IsNetworkProblem(exception))
        {
            throw MapNetwork(exception);
        }
    }

    private async Task<RemoteReply> ReadReplyAsync(RemoteClientConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            return await connection.Channel.ReadJsonAsync(RemoteFrameType.Response, RemoteJson.Default.RemoteReply, cancellationToken);
        }
        catch (Exception exception) when (IsNetworkProblem(exception))
        {
            throw MapNetwork(exception);
        }
    }

    private async Task<(List<WireEntry>? Entries, WireProblem? Problem, bool IsEnd)> ReadListFrameAsync(RemoteClientConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            var frame = await connection.Channel.ReadAsync(cancellationToken);
            RemoteChannel.ThrowIfError(frame);
            return frame.Type switch
            {
                RemoteFrameType.End => (null, null, true),
                RemoteFrameType.Entries => (RemoteChannel.Parse(frame, RemoteJson.Default.ListWireEntry), null, false),
                RemoteFrameType.Problem => (null, RemoteChannel.Parse(frame, RemoteJson.Default.WireProblem), false),
                _ => throw new InvalidDataException($"Unexpected frame {frame.Type} in a listing."),
            };
        }
        catch (Exception exception) when (IsNetworkProblem(exception))
        {
            throw MapNetwork(exception);
        }
    }

    /// <summary>Sends exactly <paramref name="length"/> bytes of the content as data frames and the end frame.</summary>
    private async Task SendContentAsync(RemoteClientConnection connection, Stream content, long length, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var buffer = connection.Channel.DataBuffer;
        long sent = 0;
        while (true)
        {
            var read = await content.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            sent += read;
            if (sent > length)
            {
                throw new StorageException(StorageErrorKind.ChangedWhileReading, "Die Datei ist beim Lesen größer geworden.");
            }

            try
            {
                await connection.Channel.WriteDataAsync(read, cancellationToken);
            }
            catch (Exception exception) when (IsNetworkProblem(exception))
            {
                throw MapNetwork(exception);
            }

            progress?.Report(read);
        }

        if (sent != length)
        {
            throw new StorageException(StorageErrorKind.ChangedWhileReading, "Die Datei ist beim Lesen kleiner geworden.");
        }

        try
        {
            await connection.Channel.WriteAsync(RemoteFrameType.End, [], cancellationToken);
        }
        catch (Exception exception) when (IsNetworkProblem(exception))
        {
            throw MapNetwork(exception);
        }
    }

    /// <summary>After an error answer the connection can go on – unless the other side ends it (sign-in gone, Remote off).</summary>
    internal static bool IsReusableAfter(StorageException exception) =>
        exception.Kind is not (StorageErrorKind.Authentication or StorageErrorKind.Unreachable);

    internal static bool IsNetworkProblem(Exception exception) =>
        exception is IOException or SocketException or InvalidDataException or AuthenticationException or ObjectDisposedException or JsonException or FormatException;

    private static StorageErrorKind KindOf(int kind) => Enum.IsDefined((StorageErrorKind)kind) ? (StorageErrorKind)kind : StorageErrorKind.Unknown;

    private string Full(string path) => StoragePath.Combine(_folder, StoragePath.Normalize(path));

    private string Relative(string sharePath) => StoragePath.IsInside(sharePath, _folder) ? StoragePath.RelativeTo(sharePath, _folder) : sharePath;
}
