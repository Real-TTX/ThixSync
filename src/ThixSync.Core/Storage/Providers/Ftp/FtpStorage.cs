using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Authentication;
using FluentFTP;
using FluentFTP.Exceptions;
using ThixSync.Core.Models;
using ThixSync.Core.Storage.Providers.Network;

namespace ThixSync.Core.Storage.Providers.Ftp;

/// <summary>The server, sign-in and transport of an FTP storage.</summary>
internal sealed record FtpSettings(string Host, int Port, string UserName, string? Password, FtpEncryptionMode Encryption, bool Passive, string TrustedCertificate)
{
    public int Parallelism { get; init; } = 4;
}

/// <summary>
/// A folder on an FTP or FTPS server (FluentFTP). Control connections are pooled (4 at most: many servers allow few per address).
/// Lists folder by folder in parallel (MLSD when the server has it, otherwise LIST), writes to a temporary name and renames onto
/// the real one, sets the modification time with MFMT when the server knows it (otherwise the server's time stays). The
/// certificate of FTPS is checked as usual; a self-signed one is trusted on first use (<see cref="CertificateTrust"/>).
/// </summary>
internal sealed class FtpStorage : IStorage
{
    private readonly FtpSettings _settings;
    private readonly TlsTrust _trust;
    private readonly ConnectionPool<AsyncFtpClient> _pool;
    private readonly ConcurrentDictionary<string, bool> _knownFolders = new(StringComparer.Ordinal);
    private readonly string _rootSpec;
    private string? _root;

    public FtpStorage(FtpSettings settings, string root, string displayName, Func<string, string, CancellationToken, Task> saveSetting)
    {
        _settings = settings;
        _trust = new TlsTrust(settings.TrustedCertificate, saveSetting);
        _rootSpec = StoragePath.Normalize(root);
        DisplayName = displayName;
        _pool = new ConnectionPool<AsyncFtpClient>(settings.Parallelism, CreateClientAsync, client => client.IsConnected, DestroyAsync);
        Capabilities = CapabilitiesWith(setTime: false);
    }

    public ConnectionKind Kind => ConnectionKind.Ftp;

    /// <summary>SetModifiedTime only when the server knows MFMT (known after <see cref="ConnectAsync"/>).</summary>
    public StorageCapabilities Capabilities { get; private set; }

    public string DisplayName { get; }

    private string Root => _root ?? throw new InvalidOperationException("Die Verbindung ist noch nicht hergestellt.");

    public async Task ConnectAsync(bool createRoot, CancellationToken cancellationToken)
    {
        await UseAsync("", async client =>
        {
            Capabilities = CapabilitiesWith(client.HasFeature(FtpCapability.MFMT));
            _root = _rootSpec == "~" || _rootSpec.StartsWith("~/", StringComparison.Ordinal)
                ? ServerPaths.Join(await client.GetWorkingDirectory(cancellationToken), _rootSpec.Length > 2 ? _rootSpec[2..] : "")
                : "/" + _rootSpec;
            if (await client.DirectoryExists(_root, cancellationToken))
            {
                _knownFolders[_root] = true;
                return true;
            }

            if (!createRoot)
            {
                throw new StorageException(StorageErrorKind.NotFound, $"Den Ordner {_root} gibt es auf dem Server nicht.");
            }

            await EnsureFolderAsync(client, _root, cancellationToken);
            return true;
        }, cancellationToken);
    }

    public IAsyncEnumerable<StorageEntry> ListAsync(string folder, Action<string, Exception>? onProblem, CancellationToken cancellationToken) =>
        FolderWalker.WalkAsync(StoragePath.Normalize(folder), Capabilities.MaxParallelism, ListFolderAsync, onProblem, cancellationToken);

    /// <summary>The entries directly in a folder (MLSD, or LIST on old servers).</summary>
    internal Task<IReadOnlyList<StorageEntry>> ListFolderAsync(string folder, CancellationToken cancellationToken) =>
        UseAsync<IReadOnlyList<StorageEntry>>(folder, async client =>
        {
            var absolute = Absolute(folder);
            var items = await client.GetListing(absolute, FtpListOption.AllFiles, cancellationToken);
            if (items.Length == 0 && !await client.DirectoryExists(absolute, cancellationToken))
            {
                // some servers answer a missing folder with an empty listing
                throw new StorageException(StorageErrorKind.NotFound, $"Nicht gefunden ({folder}).");
            }

            var entries = new List<StorageEntry>(items.Length);
            foreach (var item in items)
            {
                if (ServerPaths.IsHiddenName(item.Name) || item.Type == FtpObjectType.Link || item.Name.Contains('/'))
                {
                    continue;
                }

                var path = ServerPaths.Child(folder, item.Name);
                if (item.Type == FtpObjectType.Directory)
                {
                    _knownFolders[Absolute(path)] = true;
                }

                entries.Add(ToEntry(path, item));
            }

            return entries;
        }, cancellationToken);

    public Task<StorageEntry?> GetAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        return UseAsync(normalized, async client =>
        {
            var absolute = Absolute(normalized);
            if (normalized.Length > 0 && await client.GetObjectInfo(absolute, dateModified: true, cancellationToken) is { } item && item.Type != FtpObjectType.Link)
            {
                return ToEntry(normalized, item);
            }

            return await client.DirectoryExists(absolute, cancellationToken)
                ? new StorageEntry(normalized, true, 0, DateTimeOffset.UnixEpoch) { IsHidden = StoragePath.NameOf(normalized).StartsWith('.') }
                : (StorageEntry?)null;
        }, cancellationToken);
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        var lease = await RentAsync(cancellationToken);
        try
        {
            var client = lease.Client;
            var stream = await client.OpenRead(Absolute(normalized), FtpDataType.Binary, 0, false, cancellationToken);
            return new OwnedReadStream(stream, async completed =>
            {
                try
                {
                    if (!completed)
                    {
                        lease.Break(); // an aborted transfer leaves the control connection in an unknown state
                        return;
                    }

                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var reply = await client.GetReply(timeout.Token);
                    if (!reply.Success)
                    {
                        lease.Break();
                    }
                }
                catch (Exception exception) when (exception is FtpException or IOException or SocketException or TimeoutException or OperationCanceledException or ObjectDisposedException)
                {
                    lease.Break();
                }
                finally
                {
                    await lease.DisposeAsync();
                }
            });
        }
        catch (Exception exception)
        {
            lease.Break();
            await lease.DisposeAsync();
            throw Map(exception, normalized, cancellationToken);
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
        var setTime = Capabilities.Has(StorageFeatures.SetModifiedTime);
        return await UseAsync(normalized, async client =>
        {
            await EnsureFolderAsync(client, ParentOfAbsolute(target), cancellationToken);
            try
            {
                var source = new ProgressReadStream(content, length, progress);
                var status = await client.UploadStream(source, temp, FtpRemoteExists.NoCheck, false, null, cancellationToken);
                if (status != FtpStatus.Success)
                {
                    throw new StorageException(StorageErrorKind.Transient, $"Der Server hat das Hochladen nicht bestätigt ({normalized}).");
                }

                if (source.BytesRead != length)
                {
                    throw new StorageException(StorageErrorKind.ChangedWhileReading, $"Die Datei ist kürzer geworden, während sie gelesen wurde ({normalized}).");
                }

                if (setTime)
                {
                    await client.SetModifiedTime(temp, modified.UtcDateTime, cancellationToken);
                }

                await RenameOverAsync(client, temp, target, isFolder: false, cancellationToken);
            }
            catch
            {
                await TryDeleteAsync(client, temp);
                throw;
            }

            var written = await client.GetObjectInfo(target, dateModified: true, cancellationToken);
            return written is null
                ? new StorageEntry(normalized, false, length, setTime ? modified : DateTimeOffset.UtcNow)
                : ToEntry(normalized, written);
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

    public Task DeleteAsync(string path, bool isFolder, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.Length == 0)
        {
            throw new StorageException(StorageErrorKind.AccessDenied, "Der Wurzelordner eines Auftrags wird nie gelöscht.");
        }

        var absolute = Absolute(normalized);
        return UseAsync(normalized, async client =>
        {
            try
            {
                if (isFolder)
                {
                    await client.DeleteDirectory(absolute, cancellationToken);
                }
                else
                {
                    await client.DeleteFile(absolute, cancellationToken);
                }
            }
            catch (FtpCommandException)
            {
                var stillThere = isFolder ? await client.DirectoryExists(absolute, cancellationToken) : await client.FileExists(absolute, cancellationToken);
                if (stillThere)
                {
                    throw;
                }
            }

            ForgetFolders(absolute);
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
            var isFolder = await client.DirectoryExists(source, cancellationToken);
            await EnsureFolderAsync(client, ParentOfAbsolute(target), cancellationToken);
            await RenameOverAsync(client, source, target, isFolder, cancellationToken);
            ForgetFolders(source);
            return true;
        }, cancellationToken);
    }

    public ValueTask DisposeAsync() => _pool.DisposeAsync();

    private static StorageCapabilities CapabilitiesWith(bool setTime) => new()
    {
        Features = StorageFeatures.Move | StorageFeatures.RealFolders | StorageFeatures.CaseSensitive | StorageFeatures.CoarseTime
            | (setTime ? StorageFeatures.SetModifiedTime : 0),
        MaxParallelism = 4,
        InvalidNameCharacters = "\r\n",
    };

    private string Absolute(string path) => ServerPaths.Join(Root, path);

    private static string ParentOfAbsolute(string absolute)
    {
        var index = absolute.LastIndexOf('/');
        return index <= 0 ? "/" : absolute[..index];
    }

    private static StorageEntry ToEntry(string path, FtpListItem item)
    {
        var isFolder = item.Type == FtpObjectType.Directory;
        var modified = item.Modified == DateTime.MinValue ? DateTimeOffset.UnixEpoch : new DateTimeOffset(DateTime.SpecifyKind(item.Modified, DateTimeKind.Utc));
        return new StorageEntry(path, isFolder, isFolder ? 0 : Math.Max(0, item.Size), modified)
        {
            IsHidden = StoragePath.NameOf(path).StartsWith('.'),
        };
    }

    private void ForgetFolders(string absolute)
    {
        foreach (var known in _knownFolders.Keys.Where(known => known == absolute || known.StartsWith(absolute + "/", StringComparison.Ordinal)))
        {
            _knownFolders.TryRemove(known, out _);
        }
    }

    private async Task EnsureFolderAsync(AsyncFtpClient client, string absolute, CancellationToken cancellationToken)
    {
        if (absolute is "/" or "" || _knownFolders.ContainsKey(absolute))
        {
            return;
        }

        if (!await client.DirectoryExists(absolute, cancellationToken) && !await client.CreateDirectory(absolute, true, cancellationToken)
            && !await client.DirectoryExists(absolute, cancellationToken))
        {
            throw new StorageException(StorageErrorKind.AccessDenied, "Ein Ordner konnte auf dem Server nicht angelegt werden.");
        }

        _knownFolders[absolute] = true;
    }

    /// <summary>RNFR/RNTO; servers that refuse an existing target get it removed first (the new file is complete already).</summary>
    private static async Task RenameOverAsync(AsyncFtpClient client, string from, string to, bool isFolder, CancellationToken cancellationToken)
    {
        try
        {
            await client.Rename(from, to, cancellationToken);
        }
        catch (FtpCommandException)
        {
            if (isFolder || !await client.FileExists(to, cancellationToken))
            {
                throw;
            }

            await client.DeleteFile(to, cancellationToken);
            await client.Rename(from, to, cancellationToken);
        }
    }

    private static async Task TryDeleteAsync(AsyncFtpClient client, string absolute)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await client.DeleteFile(absolute, timeout.Token);
        }
        catch (Exception exception) when (exception is FtpException or IOException or SocketException or TimeoutException or OperationCanceledException or ObjectDisposedException)
        {
            // the temporary file stays (its name starts with the temp prefix: no listing shows it)
        }
    }

    private async Task<ConnectionPool<AsyncFtpClient>.Lease> RentAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _pool.RentAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not StorageException)
        {
            throw Map(exception, "", cancellationToken, connecting: true);
        }
    }

    private async Task<T> UseAsync<T>(string path, Func<AsyncFtpClient, Task<T>> action, CancellationToken cancellationToken)
    {
        await using var lease = await RentAsync(cancellationToken);
        try
        {
            return await action(lease.Client);
        }
        catch (Exception exception)
        {
            if (exception is not (StorageException or FtpCommandException or FtpMissingObjectException))
            {
                lease.Break();
            }

            throw Map(exception, path, cancellationToken);
        }
    }

    private async Task<AsyncFtpClient> CreateClientAsync(CancellationToken cancellationToken)
    {
        var anonymous = _settings.UserName.Length == 0;
        var client = new AsyncFtpClient(_settings.Host, anonymous ? "anonymous" : _settings.UserName,
            anonymous && string.IsNullOrEmpty(_settings.Password) ? "anonymous@" : _settings.Password ?? "", _settings.Port);
        client.Config.EncryptionMode = _settings.Encryption;
        client.Config.DataConnectionType = _settings.Passive ? FtpDataConnectionType.AutoPassive : FtpDataConnectionType.AutoActive;
        client.Config.ValidateAnyCertificate = false;
        client.Config.ConnectTimeout = 30_000;
        client.Config.ReadTimeout = 120_000;
        client.Config.DataConnectionConnectTimeout = 30_000;
        client.Config.DataConnectionReadTimeout = 120_000;
        client.Config.RetryAttempts = 1;
        client.Config.SocketKeepAlive = true;
        client.Config.TimeConversion = FtpDate.ServerTime; // MLSD and MFMT speak UTC: the times stay as they are
        client.Config.LogPassword = false;
        client.Config.LogToConsole = false;
        client.ValidateCertificate += (_, e) => e.Accept = _trust.Validate(e.Certificate, e.PolicyErrors);
        try
        {
            await client.Connect(cancellationToken);
        }
        catch
        {
            await DestroyAsync(client);
            throw;
        }

        await _trust.SaveLearnedAsync(cancellationToken);
        return client;
    }

    private static async ValueTask DestroyAsync(AsyncFtpClient client)
    {
        try
        {
            if (client.IsConnected)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await client.Disconnect(timeout.Token);
            }
        }
        catch (Exception exception) when (exception is FtpException or IOException or SocketException or TimeoutException or OperationCanceledException or ObjectDisposedException)
        {
            // it is gone anyway
        }

        client.Dispose();
    }

    private Exception Map(Exception exception, string path, CancellationToken cancellationToken, bool connecting = false)
    {
        var where = path.Length == 0 ? "" : $" ({path})";
        if (exception is StorageException || (exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            return exception;
        }

        if (_trust.Refused && exception is FtpInvalidCertificateException or AuthenticationException or IOException)
        {
            return TlsTrust.Mismatch(_settings.Host, exception);
        }

        return exception switch
        {
            FtpAuthenticationException => new StorageException(StorageErrorKind.Authentication, "Der FTP-Server hat die Anmeldung abgelehnt – stimmen Benutzername und Passwort?", exception),
            FtpInvalidCertificateException or AuthenticationException => new StorageException(StorageErrorKind.Authentication,
                $"Die verschlüsselte Verbindung zu {_settings.Host} ist fehlgeschlagen (Zertifikat).", exception),
            FtpSecurityNotAvailableException => new StorageException(StorageErrorKind.Authentication,
                $"{_settings.Host} bietet keine Verschlüsselung (FTPS) an. Falls das gewollt ist: Verschlüsselung „Keine“ wählen.", exception),
            FtpMissingObjectException => new StorageException(StorageErrorKind.NotFound, $"Nicht gefunden{where}.", exception),
            FtpCommandException command => MapReply(command, where),
            SocketException => new StorageException(StorageErrorKind.Unreachable, $"{_settings.Host} ist nicht erreichbar.", exception),
            TimeoutException or OperationCanceledException => new StorageException(connecting ? StorageErrorKind.Unreachable : StorageErrorKind.Transient,
                $"{_settings.Host} antwortet nicht rechtzeitig{where}.", exception),
            IOException or ObjectDisposedException or FtpMissingSocketException => new StorageException(connecting ? StorageErrorKind.Unreachable : StorageErrorKind.Transient,
                $"Die Verbindung zu {_settings.Host} wurde unterbrochen{where}.", exception),
            FtpException => new StorageException(StorageErrorKind.Unknown, $"FTP-Fehler: {exception.Message}{where}", exception),
            _ => exception,
        };
    }

    /// <summary>The reply codes of FTP (RFC 959/3659) as kinds.</summary>
    internal static StorageException MapReply(FtpCommandException exception, string where)
    {
        var code = exception.CompletionCode ?? "";
        var text = exception.Message;
        if (code is "530" or "532")
        {
            return new StorageException(StorageErrorKind.Authentication, "Der FTP-Server hat die Anmeldung abgelehnt – stimmen Benutzername und Passwort?", exception);
        }

        if (code is "552" or "452")
        {
            return new StorageException(StorageErrorKind.QuotaExceeded, "Auf dem FTP-Server ist kein Platz mehr (Speicher voll oder Kontingent erreicht).", exception);
        }

        if (code == "553")
        {
            return new StorageException(StorageErrorKind.InvalidName, $"Der Server nimmt diesen Namen nicht an{where}.", exception);
        }

        if (code == "550")
        {
            return text.Contains("denied", StringComparison.OrdinalIgnoreCase) || text.Contains("permission", StringComparison.OrdinalIgnoreCase)
                ? new StorageException(StorageErrorKind.AccessDenied, $"Kein Zugriff{where}.", exception)
                : new StorageException(StorageErrorKind.NotFound, $"Nicht gefunden oder nicht erlaubt{where}.", exception);
        }

        return code.StartsWith('4')
            ? new StorageException(StorageErrorKind.Transient, $"Der FTP-Server ist gerade nicht bereit ({code}){where}.", exception)
            : new StorageException(StorageErrorKind.Unknown, $"Der FTP-Server hat abgelehnt ({code}){where}.", exception);
    }
}
