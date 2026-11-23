using ThixSync.Core.Business;
using ThixSync.Core.Models;
using ThixSync.Core.Storage.Providers.Network;
using ThixSync.Core.Storage.Providers.WebDav;

namespace ThixSync.Core.Storage.Providers.Sftp;

/// <summary>
/// "Hetzner Storage Box": SFTP on port 23 (the default, recommended) or WebDAV over https://&lt;server&gt;/. The server follows from the
/// user name (u123456 → u123456.your-storagebox.de, a sub-account u123456-sub1 → u123456-sub1.your-storagebox.de, <see cref="ConnectionService.HetznerHost"/>) unless one is set.
/// </summary>
public sealed class HetznerProvider : IStorageProvider, IFolderBrowser
{
    public const int SftpPort = 23;

    public IReadOnlyList<ConnectionKind> Kinds { get; } = [ConnectionKind.HetznerStorageBox];

    public IStorage Open(StorageContext context)
    {
        var connection = context.Connection;
        var host = ConnectionService.HetznerHost(connection).Trim();
        if (host.Length == 0)
        {
            throw new StorageException(StorageErrorKind.Unreachable, "Bitte den Benutzernamen der Storage Box angeben (u123456).");
        }

        return string.Equals(ConnectionCatalog.Value(connection, ConnectionFields.Protocol), "webdav", StringComparison.OrdinalIgnoreCase)
            ? WebDavProvider.OpenWebDav(context, new UriBuilder(Uri.UriSchemeHttps, host, -1, "/").Uri, isOwnCloud: false)
            : SftpProvider.OpenSftp(context, host, SftpPort);
    }

    public Task<IReadOnlyList<string>> ListFoldersAsync(StorageContext context, string folder, CancellationToken cancellationToken)
    {
        return Open(context with { Folder = folder }) switch
        {
            WebDavStorage webDav => FolderBrowsing.FoldersAsync(webDav, webDav.ListFolderAsync, cancellationToken),
            SftpStorage sftp => FolderBrowsing.FoldersAsync(sftp, sftp.ListFolderAsync, cancellationToken),
            _ => Task.FromResult<IReadOnlyList<string>>([]),
        };
    }
}
