using System.Net;
using System.Runtime.CompilerServices;
using CG.Web.MegaApiClient;
using ThixSync.Core.Models;
using ThixSync.Core.Storage.Providers.S3;

namespace ThixSync.Core.Storage.Providers.Mega;

/// <summary>
/// A folder of a MEGA account through MegaApiClient (which does MEGA's encryption). Signs in with e-mail and password and keeps
/// the session while the storage is open; the tree of nodes is loaded once per listing and kept up to date with the storage's own
/// changes. Uploads keep the original modification time (in the encrypted attributes); a file that is replaced is uploaded as a
/// new node first and the old one removed afterwards (a cancelled upload leaves the old file). MEGA limits requests: few at once.
/// </summary>
internal sealed class MegaStorage : IStorage
{
    private readonly IMegaApiClient _client;
    private readonly string _email;
    private readonly string _password;
    private readonly string _rootPath; // base + folder, relative to the Cloud Drive
    private readonly MegaTree _tree = new();
    private readonly SemaphoreSlim _folders = new(1); // creating folders one at a time (no duplicates)
    private INode? _root;

    public MegaStorage(StorageContext context, IMegaApiClient? client = null)
    {
        _email = context.Connection.Get(ConnectionFields.UserName).Trim();
        _password = context.Secret ?? "";
        if (_email.Length == 0 || _password.Length == 0)
        {
            throw new StorageException(StorageErrorKind.Authentication, "E-Mail oder Passwort für MEGA fehlen.");
        }

        _client = client ?? new MegaApiClient();
        _rootPath = StoragePath.Combine(ConnectionCatalog.Value(context.Connection, ConnectionFields.BasePath), context.Folder);
        DisplayName = $"MEGA ({_email})/{_rootPath}";
        Capabilities = new StorageCapabilities
        {
            Features = StorageFeatures.SetModifiedTime | StorageFeatures.CaseSensitive | StorageFeatures.Move | StorageFeatures.RealFolders | StorageFeatures.CoarseTime,
            MaxParallelism = 3,
        };
    }

    public ConnectionKind Kind => ConnectionKind.Mega;

    public StorageCapabilities Capabilities { get; }

    public string DisplayName { get; }

    public async Task ConnectAsync(bool createRoot, CancellationToken cancellationToken)
    {
        if (!_client.IsLoggedIn)
        {
            try
            {
                await _client.LoginAsync(_email, _password).WaitAsync(cancellationToken);
            }
            catch (ApiException exception) when (exception.ApiResultCode is ApiResultCode.ResourceNotExists or ApiResultCode.BadArguments or ApiResultCode.AccessDenied)
            {
                throw new StorageException(StorageErrorKind.Authentication, "Die Anmeldung bei MEGA wurde abgelehnt: E-Mail oder Passwort stimmt nicht.", exception);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not StorageException)
            {
                throw Map(exception, "");
            }
        }

        await LoadTreeAsync(cancellationToken);
        var root = _tree.Find(_tree.Root!, _rootPath);
        if (root is { Type: NodeType.Directory or NodeType.Root })
        {
            _root = root;
            return;
        }

        if (!createRoot)
        {
            throw new StorageException(StorageErrorKind.NotFound, $"Den Ordner „{_rootPath}“ gibt es bei MEGA nicht.");
        }

        _root = await EnsureFolderAsync(_tree.Root!, _rootPath, cancellationToken);
    }

    public async IAsyncEnumerable<StorageEntry> ListAsync(string folder, Action<string, Exception>? onProblem, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var start = StoragePath.Normalize(folder);
        await LoadTreeAsync(cancellationToken);
        var root = Root;
        var startNode = _tree.Find(root, start);
        if (startNode is not { Type: NodeType.Directory or NodeType.Root })
        {
            yield break;
        }

        var items = _tree.Walk(startNode, start, duplicate => onProblem?.Invoke(duplicate,
            new StorageException(StorageErrorKind.InvalidName, $"„{duplicate}“ gibt es bei MEGA mehrmals; nur der neueste Eintrag wird abgeglichen.")));
        foreach (var (path, node) in items)
        {
            yield return EntryOf(path, node);
        }
    }

    public Task<StorageEntry?> GetAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        var node = _tree.Find(Root, normalized);
        return Task.FromResult<StorageEntry?>(node is null ? null : EntryOf(normalized, node));
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        var node = _tree.Find(Root, normalized);
        if (node is not { Type: NodeType.File })
        {
            throw new StorageException(StorageErrorKind.NotFound, $"Nicht gefunden{CloudHttp.Where(normalized)}.");
        }

        var stream = await RunAsync(() => _client.DownloadAsync(node, null, cancellationToken), normalized, cancellationToken);
        return new MegaReadStream(stream, normalized);
    }

    public async Task<StorageEntry> WriteAsync(string path, Stream content, long length, DateTimeOffset modified, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.Length == 0)
        {
            throw new StorageException(StorageErrorKind.InvalidName, "Der Wurzelordner ist keine Datei.");
        }

        var parent = await EnsureFolderAsync(Root, StoragePath.ParentOf(normalized), cancellationToken);
        var name = StoragePath.NameOf(normalized);
        var previous = _tree.Child(parent, name);
        var source = new ProgressStream(content, length, progress);
        var uploaded = await RunAsync(() => _client.UploadAsync(source, name, parent, null, modified.UtcDateTime, cancellationToken), normalized, cancellationToken);
        _tree.Add(uploaded);
        if (previous is { Type: NodeType.File })
        {
            await RunAsync(() => _client.DeleteAsync(previous, moveToTrash: false), normalized, cancellationToken);
            _tree.Remove(previous);
        }

        return EntryOf(normalized, uploaded) with { Size = length };
    }

    public Task CreateFolderAsync(string path, CancellationToken cancellationToken) =>
        EnsureFolderAsync(Root, StoragePath.Normalize(path), cancellationToken);

    public async Task DeleteAsync(string path, bool isFolder, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.Length == 0)
        {
            throw new StorageException(StorageErrorKind.AccessDenied, "Der Wurzelordner eines Auftrags wird nie gelöscht.");
        }

        var node = _tree.Find(Root, normalized);
        if (node is null)
        {
            return;
        }

        await RunAsync(() => _client.DeleteAsync(node, moveToTrash: false), normalized, cancellationToken);
        _tree.Remove(node);
    }

    public async Task MoveAsync(string fromPath, string toPath, CancellationToken cancellationToken)
    {
        var from = StoragePath.Normalize(fromPath);
        var to = StoragePath.Normalize(toPath);
        if (from.Length == 0 || to.Length == 0)
        {
            throw new StorageException(StorageErrorKind.InvalidName, "Der Wurzelordner wird nicht verschoben.");
        }

        if (from == to)
        {
            return;
        }

        var node = _tree.Find(Root, from) ?? throw new StorageException(StorageErrorKind.NotFound, $"Nicht gefunden{CloudHttp.Where(from)}.");
        var parent = await EnsureFolderAsync(Root, StoragePath.ParentOf(to), cancellationToken);
        var name = StoragePath.NameOf(to);
        if (_tree.Child(parent, name) is { Type: NodeType.File } existing && existing.Id != node.Id)
        {
            await RunAsync(() => _client.DeleteAsync(existing, moveToTrash: false), to, cancellationToken);
            _tree.Remove(existing);
        }

        if (node.ParentId != parent.Id)
        {
            node = await RunAsync(() => _client.MoveAsync(node, parent), from, cancellationToken);
            _tree.Add(node);
        }

        if (node.Name != name)
        {
            node = await RunAsync(() => _client.RenameAsync(node, name), from, cancellationToken);
            _tree.Add(node);
        }
    }

    /// <summary>The folders directly below the root (the folder picker).</summary>
    internal async Task<IReadOnlyList<string>> ListChildFoldersAsync(CancellationToken cancellationToken)
    {
        await ConnectAsync(createRoot: false, cancellationToken);
        return [.. _tree.Walk(Root, "", null)
            .Where(item => item.Node.Type == NodeType.Directory && !item.Path.Contains('/'))
            .Select(item => item.Path)
            .Order(StringComparer.CurrentCultureIgnoreCase)];
    }

    public async ValueTask DisposeAsync()
    {
        _folders.Dispose();
        if (!_client.IsLoggedIn)
        {
            return;
        }

        try
        {
            await _client.LogoutAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception exception) when (exception is ApiException or TimeoutException or HttpRequestException or IOException or WebException)
        {
            // the session ends on its own
        }
    }

    /// <summary>Maps what MegaApiClient throws to a German <see cref="StorageException"/>.</summary>
    internal static StorageException Map(Exception exception, string path)
    {
        if (exception is AggregateException { InnerException: { } inner })
        {
            return Map(inner, path);
        }

        var where = CloudHttp.Where(path);
        return exception switch
        {
            StorageException storage => storage,
            ApiException api => api.ApiResultCode switch
            {
                ApiResultCode.ResourceNotExists => new StorageException(StorageErrorKind.NotFound, $"Nicht gefunden{where}.", exception),
                ApiResultCode.AccessDenied or ApiResultCode.ResourceAdministrativelyBlocked => new StorageException(StorageErrorKind.AccessDenied, $"Kein Zugriff{where}.", exception),
                ApiResultCode.BadSessionId => new StorageException(StorageErrorKind.Authentication, "Die Sitzung bei MEGA ist abgelaufen.", exception),
                ApiResultCode.TwoFactorAuthenticationError => new StorageException(StorageErrorKind.Authentication,
                    "Das MEGA-Konto verlangt Zwei-Faktor-Anmeldung; das unterstützt ThixSync noch nicht.", exception),
                ApiResultCode.QuotaExceeded => new StorageException(StorageErrorKind.QuotaExceeded, "Der Speicherplatz (oder das Transferkontingent) bei MEGA ist erschöpft.", exception),
                ApiResultCode.TooManyRequests or ApiResultCode.ToManyRequestsForThisResource or ApiResultCode.TooManyConnectionsOnThisResource =>
                    new StorageException(StorageErrorKind.RateLimited, $"MEGA bittet, langsamer zu machen{where}.", exception) { RetryAfter = TimeSpan.FromSeconds(30) },
                ApiResultCode.RequestFailedRetry or ApiResultCode.ResourceTemporarilyNotAvailable or ApiResultCode.InternalError or ApiResultCode.RequestIncomplete =>
                    new StorageException(StorageErrorKind.Transient, $"Vorübergehender Fehler bei MEGA{where}.", exception),
                _ => new StorageException(StorageErrorKind.Unknown, $"MEGA meldet einen Fehler{where} [{(int)api.ApiResultCode}].", exception),
            },
            UploadException or DownloadException => new StorageException(StorageErrorKind.Transient, $"Die Übertragung zu MEGA brach ab{where}.", exception),
            NotSupportedException => new StorageException(StorageErrorKind.Transient, $"Die Übertragung zu MEGA brach ab und kann nicht fortgesetzt werden{where}.", exception),
            HttpRequestException or IOException or WebException => CloudHttp.MapTransport(exception, path),
            TimeoutException => new StorageException(StorageErrorKind.Transient, $"MEGA antwortet nicht (Zeitüberschreitung){where}.", exception),
            _ => new StorageException(StorageErrorKind.Unknown, $"MEGA meldet einen Fehler{where}.", exception),
        };
    }

    private INode Root => _root ?? throw new InvalidOperationException("ConnectAsync first.");

    private async Task LoadTreeAsync(CancellationToken cancellationToken)
    {
        var nodes = await RunAsync(() => _client.GetNodesAsync(), "", cancellationToken);
        _tree.Load(nodes);
        if (_tree.Root is null)
        {
            throw new StorageException(StorageErrorKind.Transient, "MEGA hat keinen Cloud-Drive-Ordner geliefert.");
        }

        if (_root is not null)
        {
            _root = _tree.Find(_tree.Root, _rootPath) ?? throw new StorageException(StorageErrorKind.NotFound, $"Den Ordner „{_rootPath}“ gibt es bei MEGA nicht mehr.");
        }
    }

    /// <summary>The folder at a path below <paramref name="start"/>, created with its parents when missing.</summary>
    private async Task<INode> EnsureFolderAsync(INode start, string path, CancellationToken cancellationToken)
    {
        if (path.Length == 0)
        {
            return start;
        }

        if (_tree.Find(start, path) is { Type: NodeType.Directory } existing)
        {
            return existing;
        }

        await _folders.WaitAsync(cancellationToken);
        try
        {
            var node = start;
            foreach (var part in path.Split('/'))
            {
                var child = _tree.Child(node, part);
                if (child is { Type: NodeType.Directory })
                {
                    node = child;
                    continue;
                }

                var parent = node;
                node = await RunAsync(() => _client.CreateFolderAsync(part, parent), path, cancellationToken);
                _tree.Add(node);
            }

            return node;
        }
        finally
        {
            _folders.Release();
        }
    }

    /// <summary>Runs a call of MegaApiClient (most take no token: the wait is abandoned on cancellation) and maps its errors.</summary>
    private static async Task<T> RunAsync<T>(Func<Task<T>> call, string path, CancellationToken cancellationToken)
    {
        try
        {
            return await call().WaitAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Map(exception, path);
        }
    }

    private static async Task RunAsync(Func<Task> call, string path, CancellationToken cancellationToken) =>
        await RunAsync(async () =>
        {
            await call();
            return true;
        }, path, cancellationToken);

    private static StorageEntry EntryOf(string path, INode node)
    {
        var isFolder = node.Type is NodeType.Directory or NodeType.Root;
        return new StorageEntry(path, isFolder, isFolder ? 0 : node.Size, MegaTree.TimeOf(node))
        {
            NativeId = node.Id,
            IsHidden = path.Length > 0 && StoragePath.NameOf(path).StartsWith('.'),
        };
    }
}

/// <summary>A download of MegaApiClient whose errors while reading become <see cref="StorageException"/>.</summary>
internal sealed class MegaReadStream(Stream inner, string path) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            return inner.Read(buffer, offset, count);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw MegaStorage.Map(exception, path);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return await inner.ReadAsync(buffer, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw MegaStorage.Map(exception, path);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
