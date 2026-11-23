using ThixSync.Core.Models;
using ThixSync.Core.Storage.Providers.Network;

namespace ThixSync.Core.Storage.Providers.Sftp;

/// <summary>
/// "SFTP / SSH" and "rsync über SSH": sign-in with password or key file (the passphrase is the secret), the host key is trusted on
/// first use and kept with the connection. Also used by Synology/QNAP and the Hetzner Storage Box with SFTP.
/// </summary>
public sealed class SftpProvider : IStorageProvider, IFolderBrowser
{
    public const int DefaultPort = 22;

    public IReadOnlyList<ConnectionKind> Kinds { get; } = [ConnectionKind.Sftp, ConnectionKind.RsyncSsh];

    public IStorage Open(StorageContext context) => OpenSftp(context, context.Connection.Get(ConnectionFields.Host), DefaultPort);

    public Task<IReadOnlyList<string>> ListFoldersAsync(StorageContext context, string folder, CancellationToken cancellationToken)
    {
        var storage = OpenSftp(context with { Folder = folder }, context.Connection.Get(ConnectionFields.Host), DefaultPort);
        return FolderBrowsing.FoldersAsync(storage, storage.ListFolderAsync, cancellationToken);
    }

    /// <summary>An SFTP (or, for <see cref="ConnectionKind.RsyncSsh"/>, rsync) storage of a connection on <paramref name="host"/>.</summary>
    internal static SftpStorage OpenSftp(StorageContext context, string host, int defaultPort)
    {
        var connection = context.Connection;
        if (host.Trim().Length == 0)
        {
            throw new StorageException(StorageErrorKind.Unreachable, "Der Server der Verbindung fehlt.");
        }

        var port = ServerPaths.PortOf(connection, defaultPort);
        var user = connection.Get(ConnectionFields.UserName);
        var settings = new SshSettings(
            host.Trim(),
            port,
            user,
            UseKeyFile: string.Equals(ConnectionCatalog.Value(connection, ConnectionFields.AuthMethod), "key", StringComparison.OrdinalIgnoreCase)
                && connection.Kind is ConnectionKind.Sftp or ConnectionKind.RsyncSsh,
            KeyFile: connection.Get(ConnectionFields.PrivateKeyFile),
            Secret: context.Secret,
            TrustedHostKey: connection.Get(ConnectionFields.HostKey));
        var endpoint = new SshEndpoint(settings, context.SaveSettingAsync);
        var root = ServerPaths.RootOf(context);
        var display = $"sftp://{user}@{settings.Host}{(port == DefaultPort ? "" : $":{port}")}/{root}".TrimEnd('/');
        return connection.Kind == ConnectionKind.RsyncSsh
            ? new RsyncStorage(endpoint, root, display)
            : new SftpStorage(connection.Kind, endpoint, root, display);
    }
}
