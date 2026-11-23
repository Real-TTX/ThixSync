using System.Collections.Concurrent;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using ThixSync.Core.Models;
using ThixSync.Core.Storage.Providers.Network;

namespace ThixSync.Core.Storage.Providers.Sftp;

/// <summary>
/// A folder on an SSH server over SFTP (SSH.NET). Sessions are pooled (<see cref="StorageCapabilities.MaxParallelism"/> at most, each
/// used by one operation at a time). The root is absolute ("/backup/Fotos"); a base folder starting with "~" is below the home
/// folder of the user. Lists by reading several folders at once; writes to a temporary name in the target folder, sets the time,
/// then renames onto the real name (posix-rename@openssh.com replaces atomically; servers without it: delete + rename). Symbolic
/// links are skipped like on a local drive (no loops, nothing outside the root).
/// </summary>
internal class SftpStorage : IStorage
{
    private readonly ConcurrentDictionary<string, bool> _knownFolders = new(StringComparer.Ordinal);
    private readonly string _rootSpec;
    private int _posixRename; // 0 unknown, 1 the server has posix-rename@openssh.com, -1 it does not
    private string? _root;

    public SftpStorage(ConnectionKind kind, SshEndpoint endpoint, string root, string displayName, int parallelism = 8)
    {
        Kind = kind;
        Endpoint = endpoint;
        _rootSpec = StoragePath.Normalize(root);
        DisplayName = displayName;
        Pool = new ConnectionPool<SftpClient>(parallelism, endpoint.ConnectSftpAsync, client => client.IsConnected, client =>
        {
            client.Dispose();
            return ValueTask.CompletedTask;
        });
        Capabilities = new StorageCapabilities
        {
            // SFTP (version 3) carries whole seconds
            Features = StorageFeatures.SetModifiedTime | StorageFeatures.Move | StorageFeatures.RealFolders | StorageFeatures.CaseSensitive | StorageFeatures.CoarseTime,
            MaxParallelism = parallelism,
            InvalidNameCharacters = "",
        };
    }

    public ConnectionKind Kind { get; }

    public StorageCapabilities Capabilities { get; }

    public string DisplayName { get; }

    protected SshEndpoint Endpoint { get; }

    protected ConnectionPool<SftpClient> Pool { get; }

    /// <summary>The absolute root on the server ("/backup/Fotos"), known after <see cref="ConnectAsync"/>.</summary>
    protected string Root => _root ?? throw new InvalidOperationException("Die Verbindung ist noch nicht hergestellt.");

    public async Task ConnectAsync(bool createRoot, CancellationToken cancellationToken)
    {
        await UseAsync("", async client =>
        {
            _root = ResolveRoot(_rootSpec, client.WorkingDirectory);
            SftpFileAttributes? attributes;
            try
            {
                attributes = await client.GetAttributesAsync(_root, cancellationToken);
            }
            catch (SftpPathNotFoundException)
            {
                attributes = null;
            }

            if (attributes is { IsDirectory: true })
            {
                _knownFolders[_root] = true;
                return true;
            }

            if (attributes is not null)
            {
                throw new StorageException(StorageErrorKind.NotFound, $"{_root} ist auf dem Server eine Datei, kein Ordner.");
            }

            if (!createRoot)
            {
                throw new StorageException(StorageErrorKind.NotFound, $"Den Ordner {_root} gibt es auf dem Server nicht.");
            }

            await EnsureFolderAsync(client, _root, cancellationToken);
            return true;
        }, cancellationToken);
        await OnConnectedAsync(cancellationToken);
    }

    public virtual IAsyncEnumerable<StorageEntry> ListAsync(string folder, Action<string, Exception>? onProblem, CancellationToken cancellationToken) =>
        FolderWalker.WalkAsync(StoragePath.Normalize(folder), Capabilities.MaxParallelism, ListFolderAsync, onProblem, cancellationToken);

    /// <summary>The entries directly in a folder (one SFTP readdir).</summary>
    internal Task<IReadOnlyList<StorageEntry>> ListFolderAsync(string folder, CancellationToken cancellationToken) =>
        UseAsync<IReadOnlyList<StorageEntry>>(folder, async client =>
        {
            var entries = new List<StorageEntry>();
            await foreach (var file in client.ListDirectoryAsync(Absolute(folder), cancellationToken))
            {
                if (ServerPaths.IsHiddenName(file.Name) || file.IsSymbolicLink || (!file.IsDirectory && !file.IsRegularFile))
                {
                    continue;
                }

                var path = ServerPaths.Child(folder, file.Name);
                if (file.IsDirectory)
                {
                    _knownFolders[Absolute(path)] = true;
                }

                entries.Add(ToEntry(path, file.Attributes));
            }

            return entries;
        }, cancellationToken);

    public Task<StorageEntry?> GetAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        return UseAsync(normalized, async client =>
        {
            try
            {
                var attributes = await client.GetAttributesAsync(Absolute(normalized), cancellationToken);
                return attributes.IsDirectory || attributes.IsRegularFile ? ToEntry(normalized, attributes) : (StorageEntry?)null;
            }
            catch (SftpPathNotFoundException)
            {
                return null;
            }
        }, cancellationToken);
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        var lease = await RentAsync(cancellationToken);
        try
        {
            var stream = await lease.Client.OpenAsync(Absolute(normalized), FileMode.Open, FileAccess.Read, cancellationToken);
            return new OwnedReadStream(stream, _ => lease.DisposeAsync());
        }
        catch (Exception exception)
        {
            if (SshErrors.BreaksSession(exception))
            {
                lease.Break();
            }

            await lease.DisposeAsync();
            throw SshErrors.Map(exception, normalized, cancellationToken);
        }
    }

    public async Task<StorageEntry> WriteAsync(string path, Stream content, long length, DateTimeOffset modified, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.Length == 0)
        {
            throw new StorageException(StorageErrorKind.InvalidName, "Ohne Namen kann keine Datei geschrieben werden.");
        }

        var target = Absolute(normalized);
        var temp = ServerPaths.TempPathFor(target);
        return await UseAsync(normalized, async client =>
        {
            await EnsureFolderAsync(client, ParentOfAbsolute(target), cancellationToken);
            try
            {
                var source = new ProgressReadStream(content, length, progress);
                await client.UploadFileAsync(source, temp, canOverride: true, uploadProgress: null, cancellationToken);
                if (source.BytesRead != length)
                {
                    throw new StorageException(StorageErrorKind.ChangedWhileReading, $"Die Datei ist kürzer geworden, während sie gelesen wurde ({normalized}).");
                }

                await Task.Run(() => client.SetLastWriteTimeUtc(temp, modified.UtcDateTime), cancellationToken);
                await RenameOverAsync(client, temp, target, isFolder: false, cancellationToken);
            }
            catch
            {
                await TryDeleteFileAsync(client, temp);
                throw;
            }

            return ToEntry(normalized, await client.GetAttributesAsync(target, cancellationToken));
        }, cancellationToken);
    }

    public Task CreateFolderAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        return UseAsync(normalized, async client =>
        {
            await EnsureFolderAsync(client, Absolute(normalized), cancellationToken);
            return true;
        }, cancellationToken);
    }

    public async Task DeleteAsync(string path, bool isFolder, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.Length == 0)
        {
            throw new StorageException(StorageErrorKind.AccessDenied, "Der Wurzelordner eines Auftrags wird nie gelöscht.");
        }

        var absolute = Absolute(normalized);
        if (isFolder)
        {
            await DeleteFolderAsync(absolute, normalized, cancellationToken);
            ForgetFolders(absolute);
            return;
        }

        await UseAsync(normalized, async client =>
        {
            try
            {
                await client.DeleteFileAsync(absolute, cancellationToken);
            }
            catch (SftpPathNotFoundException)
            {
                // gone already
            }

            return true;
        }, cancellationToken);
    }

    public Task MoveAsync(string fromPath, string toPath, CancellationToken cancellationToken)
    {
        var from = StoragePath.Normalize(fromPath);
        var to = StoragePath.Normalize(toPath);
        return UseAsync(from, async client =>
        {
            var source = Absolute(from);
            var target = Absolute(to);
            var attributes = await client.GetAttributesAsync(source, cancellationToken);
            await EnsureFolderAsync(client, ParentOfAbsolute(target), cancellationToken);
            await RenameOverAsync(client, source, target, attributes.IsDirectory, cancellationToken);
            ForgetFolders(source);
            return true;
        }, cancellationToken);
    }

    public virtual async ValueTask DisposeAsync()
    {
        await Pool.DisposeAsync();
        Endpoint.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>"~/backup" → below the home folder; anything else is absolute from '/'.</summary>
    internal static string ResolveRoot(string rootSpec, string? home)
    {
        if (rootSpec == "~" || rootSpec.StartsWith("~/", StringComparison.Ordinal))
        {
            var start = string.IsNullOrEmpty(home) ? "/" : home;
            var rest = rootSpec.Length > 2 ? rootSpec[2..] : "";
            return ServerPaths.Join(start, rest);
        }

        return "/" + rootSpec;
    }

    /// <summary>The absolute path on the server of a path below the root.</summary>
    protected string Absolute(string path) => ServerPaths.Join(Root, path);

    /// <summary>Runs after the root is checked (rsync: prepares the command channel).</summary>
    protected virtual Task OnConnectedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Deletes a folder with everything in it (SFTP has no recursive delete: depth first).</summary>
    protected virtual Task DeleteFolderAsync(string absolute, string path, CancellationToken cancellationToken) =>
        UseAsync(path, async client =>
        {
            await DeleteTreeAsync(client, absolute, cancellationToken);
            return true;
        }, cancellationToken);

    protected async Task<ConnectionPool<SftpClient>.Lease> RentAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await Pool.RentAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not StorageException)
        {
            throw SshErrors.Map(exception, "", cancellationToken, connecting: true, Endpoint.Settings.Host);
        }
    }

    /// <summary>Runs an operation with a session of the pool; a broken session is thrown away, errors become <see cref="StorageException"/>.</summary>
    protected async Task<T> UseAsync<T>(string path, Func<SftpClient, Task<T>> action, CancellationToken cancellationToken)
    {
        await using var lease = await RentAsync(cancellationToken);
        try
        {
            return await action(lease.Client);
        }
        catch (Exception exception)
        {
            if (SshErrors.BreaksSession(exception))
            {
                lease.Break();
            }

            throw SshErrors.Map(exception, path, cancellationToken);
        }
    }

    protected void ForgetFolders(string absolute)
    {
        foreach (var known in _knownFolders.Keys.Where(known => known == absolute || known.StartsWith(absolute + "/", StringComparison.Ordinal)))
        {
            _knownFolders.TryRemove(known, out _);
        }
    }

    private static StorageEntry ToEntry(string path, SftpFileAttributes attributes) =>
        new(path, attributes.IsDirectory, attributes.IsDirectory ? 0 : attributes.Size,
            new DateTimeOffset(DateTime.SpecifyKind(attributes.LastWriteTimeUtc, DateTimeKind.Utc)))
        {
            IsHidden = StoragePath.NameOf(path).StartsWith('.'),
        };

    private static string ParentOfAbsolute(string absolute)
    {
        var index = absolute.LastIndexOf('/');
        return index <= 0 ? "/" : absolute[..index];
    }

    /// <summary>Makes a folder and its missing parents (known folders are not asked again).</summary>
    private async Task EnsureFolderAsync(SftpClient client, string absolute, CancellationToken cancellationToken)
    {
        if (absolute is "/" or "" || _knownFolders.ContainsKey(absolute))
        {
            return;
        }

        SftpFileAttributes? attributes;
        try
        {
            attributes = await client.GetAttributesAsync(absolute, cancellationToken);
        }
        catch (SftpPathNotFoundException)
        {
            attributes = null;
        }

        if (attributes is null)
        {
            await EnsureFolderAsync(client, ParentOfAbsolute(absolute), cancellationToken);
            try
            {
                await client.CreateDirectoryAsync(absolute, cancellationToken);
            }
            catch (SftpException)
            {
                // another worker may have made it in the meantime
                if (!await IsFolderAsync(client, absolute, cancellationToken))
                {
                    throw;
                }
            }
        }
        else if (!attributes.IsDirectory)
        {
            throw new StorageException(StorageErrorKind.InvalidName, "Ein Ordner kann nicht angelegt werden: an seiner Stelle liegt eine Datei.");
        }

        _knownFolders[absolute] = true;
    }

    private static async Task<bool> IsFolderAsync(SftpClient client, string absolute, CancellationToken cancellationToken)
    {
        try
        {
            return (await client.GetAttributesAsync(absolute, cancellationToken)).IsDirectory;
        }
        catch (SftpPathNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Renames onto <paramref name="to"/>, replacing a file there.</summary>
    private async Task RenameOverAsync(SftpClient client, string from, string to, bool isFolder, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _posixRename) >= 0)
        {
            try
            {
                await Task.Run(() => client.RenameFile(from, to, isPosix: true), cancellationToken);
                Volatile.Write(ref _posixRename, 1);
                return;
            }
            catch (NotSupportedException)
            {
                Volatile.Write(ref _posixRename, -1);
            }
        }

        // plain SFTP rename refuses an existing target: the new file is complete already, so remove the old one first
        if (!isFolder)
        {
            try
            {
                await client.DeleteFileAsync(to, cancellationToken);
            }
            catch (SftpPathNotFoundException)
            {
                // nothing there
            }
        }

        await client.RenameFileAsync(from, to, cancellationToken);
    }

    private static async Task DeleteTreeAsync(SftpClient client, string absolute, CancellationToken cancellationToken)
    {
        var children = new List<ISftpFile>();
        try
        {
            await foreach (var file in client.ListDirectoryAsync(absolute, cancellationToken))
            {
                if (file.Name is not ("." or ".."))
                {
                    children.Add(file);
                }
            }
        }
        catch (SftpPathNotFoundException)
        {
            return;
        }

        foreach (var child in children)
        {
            if (child.IsDirectory && !child.IsSymbolicLink)
            {
                await DeleteTreeAsync(client, child.FullName, cancellationToken);
                continue;
            }

            await client.DeleteFileAsync(child.FullName, cancellationToken);
        }

        await client.DeleteDirectoryAsync(absolute, cancellationToken);
    }

    private static async Task TryDeleteFileAsync(SftpClient client, string absolute)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await client.DeleteFileAsync(absolute, timeout.Token);
        }
        catch (Exception exception) when (exception is SshException or IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // the temporary file stays (its name starts with the temp prefix: no listing shows it)
        }
    }
}
