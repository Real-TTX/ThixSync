using ThixSync.Core.Models;
using ThixSync.Core.Platform;
using ThixSync.Core.Storage.Providers.Local;
using ThixSync.Core.Storage.Providers.Network;
using ThixSync.Core.Storage.Providers.Smb;

namespace ThixSync.Core.Storage.Providers.Nfs;

/// <summary>
/// "NFS": Windows reaches the export as \\server\export through the "Client für NFS" (a clear message when it is not installed);
/// macOS mounts it with <c>mount_nfs -o resvport</c> below the data folder (or uses a mount that exists). Then it is a local folder.
/// NFS has no password: the server decides by the address of this computer.
/// </summary>
public sealed class NfsProvider : IStorageProvider, IFolderBrowser
{
    public IReadOnlyList<ConnectionKind> Kinds { get; } = [ConnectionKind.Nfs];

    public IStorage Open(StorageContext context)
    {
        var connection = context.Connection;
        var host = connection.Get(ConnectionFields.Host).Trim();
        var export = connection.Get(ConnectionFields.Share).Trim();
        if (host.Length == 0 || export.Trim('/', '\\').Length == 0)
        {
            throw new StorageException(StorageErrorKind.Unreachable, "Server und Export der Verbindung müssen angegeben sein.");
        }

        var sub = ServerPaths.RootOf(context);
        var version = ConnectionCatalog.Value(connection, ConnectionFields.NfsVersion);
        var root = RootOf(host, export, sub, connection.Id);
        var display = $"{host}:/{export.Trim('/')}" + (sub.Length > 0 ? "/" + sub : "");
        return new MountedShareStorage(root, ConnectionKind.Nfs, display,
            cancellationToken => NetworkShares.ConnectNfsAsync(host, export, sub, version, connection.Id, cancellationToken));
    }

    public async Task<IReadOnlyList<string>> ListFoldersAsync(StorageContext context, string folder, CancellationToken cancellationToken)
    {
        await using var storage = (LocalStorage)Open(context with { Folder = folder });
        await storage.ConnectAsync(createRoot: false, cancellationToken);
        return SmbProvider.MountedFolders(storage);
    }

    /// <summary>The local path of the root: \\server\export\sub on Windows; below the mount on macOS.</summary>
    internal static string RootOf(string host, string export, string sub, Guid connectionId)
    {
        if (OperatingSystem.IsWindows())
        {
            return NetworkShares.NfsUnc(host, export, sub);
        }

        return NetworkShares.NfsLocalPathIfConnected(host, export, sub) ?? SmbProvider.JoinLocal(NetworkShares.MountPointFor(connectionId), sub);
    }
}
