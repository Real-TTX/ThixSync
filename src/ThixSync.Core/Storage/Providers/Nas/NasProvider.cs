using ThixSync.Core.Models;
using ThixSync.Core.Storage.Providers.Network;
using ThixSync.Core.Storage.Providers.Sftp;
using ThixSync.Core.Storage.Providers.Smb;
using ThixSync.Core.Storage.Providers.WebDav;

namespace ThixSync.Core.Storage.Providers.Nas;

/// <summary>
/// "Synology NAS" and "QNAP NAS": by the protocol of the connection the SMB, WebDAV or SFTP storage. The defaults are those of
/// the systems: WebDAV over HTTPS on 5006 (Synology "WebDAV Server") or 8081 (QNAP: the HTTPS port of the web server that serves
/// WebDAV), SFTP on 22; the Port field overrides them. The shared folders are the top of the tree: the base folder (or the folder
/// of a pair) starts with the shared folder ("/home/Fotos", "/Public/Backup"). A NAS usually has a self-signed certificate: it is
/// trusted on first use and kept with the connection.
/// </summary>
public sealed class NasProvider : IStorageProvider, IFolderBrowser
{
    public const int SynologyWebDavPort = 5006;
    public const int QnapWebDavPort = 8081;

    public IReadOnlyList<ConnectionKind> Kinds { get; } = [ConnectionKind.Synology, ConnectionKind.Qnap];

    public IStorage Open(StorageContext context)
    {
        var connection = context.Connection;
        var host = connection.Get(ConnectionFields.Host).Trim();
        if (host.Length == 0)
        {
            throw new StorageException(StorageErrorKind.Unreachable, "Der Server der Verbindung fehlt.");
        }

        return ProtocolOf(connection) switch
        {
            "webdav" => WebDavProvider.OpenWebDav(context, WebDavAddress(connection, host), isOwnCloud: false),
            "sftp" => SftpProvider.OpenSftp(context, host, SftpProvider.DefaultPort),
            _ => SmbProvider.OpenShare(context, host, connection.Get(ConnectionFields.Share), ""),
        };
    }

    public async Task<IReadOnlyList<string>> ListFoldersAsync(StorageContext context, string folder, CancellationToken cancellationToken)
    {
        var storage = Open(context with { Folder = folder });
        switch (storage)
        {
            case WebDavStorage webDav:
                return await FolderBrowsing.FoldersAsync(webDav, webDav.ListFolderAsync, cancellationToken);
            case SftpStorage sftp:
                return await FolderBrowsing.FoldersAsync(sftp, sftp.ListFolderAsync, cancellationToken);
            case Local.LocalStorage share:
                await using (share)
                {
                    await share.ConnectAsync(createRoot: false, cancellationToken);
                    return SmbProvider.MountedFolders(share);
                }

            default:
                await storage.DisposeAsync();
                return [];
        }
    }

    /// <summary>"smb", "webdav" or "sftp" (default: smb).</summary>
    internal static string ProtocolOf(Connection connection) => ConnectionCatalog.Value(connection, ConnectionFields.Protocol).Trim().ToLowerInvariant();

    /// <summary>https://host:port/ of the WebDAV server of the NAS.</summary>
    internal static Uri WebDavAddress(Connection connection, string host)
    {
        var port = ServerPaths.PortOf(connection, connection.Kind == ConnectionKind.Qnap ? QnapWebDavPort : SynologyWebDavPort);
        return new UriBuilder(Uri.UriSchemeHttps, host, port, "/").Uri;
    }
}
