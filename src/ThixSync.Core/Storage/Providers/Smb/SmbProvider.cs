using ThixSync.Core.Models;
using ThixSync.Core.Platform;
using ThixSync.Core.Storage.Providers.Local;
using ThixSync.Core.Storage.Providers.Network;

namespace ThixSync.Core.Storage.Providers.Smb;

/// <summary>
/// "SMB / CIFS": Windows connects \\server\share with the user name (DOMÄNE\benutzer) and password of the connection
/// (WNetAddConnection2; without a user name the sign-in of this computer is used) and reads it as a folder; macOS mounts the share
/// below the data folder with NetFS (or uses a mount the Finder made). Then it is a local folder (<see cref="LocalStorage"/>).
/// Also used by Synology/QNAP with SMB.
/// </summary>
public sealed class SmbProvider : IStorageProvider, IFolderBrowser
{
    public IReadOnlyList<ConnectionKind> Kinds { get; } = [ConnectionKind.Smb];

    public IStorage Open(StorageContext context)
    {
        var connection = context.Connection;
        return OpenShare(context, connection.Get(ConnectionFields.Host), connection.Get(ConnectionFields.Share), connection.Get(ConnectionFields.Domain));
    }

    public async Task<IReadOnlyList<string>> ListFoldersAsync(StorageContext context, string folder, CancellationToken cancellationToken)
    {
        await using var storage = (LocalStorage)Open(context with { Folder = folder });
        await storage.ConnectAsync(createRoot: false, cancellationToken);
        return MountedFolders(storage);
    }

    /// <summary>A share of <paramref name="host"/> (the share may name a folder in it: "daten/Fotos") at base folder + folder of the pair.</summary>
    internal static LocalStorage OpenShare(StorageContext context, string host, string share, string domain)
    {
        var (address, sub) = AddressOf(host, share, ServerPaths.RootOf(context));
        var user = context.Connection.Get(ConnectionFields.UserName).Trim();
        var signIn = user.Length > 0 && domain.Trim().Length > 0 && !user.Contains('\\') ? $"{domain.Trim()}\\{user}" : user;
        var connectionId = context.Connection.Id;
        var withSub = address with { SubPath = sub };
        var root = OperatingSystem.IsWindows()
            ? withSub.Unc
            : NetworkShares.LocalPathIfConnected(withSub) ?? JoinLocal(NetworkShares.MountPointFor(connectionId), sub);
        var display = withSub.Unc.Replace('\\', OperatingSystem.IsWindows() ? '\\' : '/');
        return new MountedShareStorage(root, context.Connection.Kind, display,
            cancellationToken => NetworkShares.ConnectAsync(withSub, signIn, context.Secret, connectionId, cancellationToken));
    }

    /// <summary>The share and the folder below it: "daten/Fotos" + "2024" → (\\server\daten, "Fotos/2024").</summary>
    internal static (ShareAddress Address, string SubPath) AddressOf(string host, string share, string root)
    {
        var server = host.Trim().Trim('\\', '/');
        var parts = StoragePath.Normalize(share).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (server.Length == 0 || parts.Length == 0)
        {
            throw new StorageException(StorageErrorKind.Unreachable, "Server und Freigabe der Verbindung müssen angegeben sein.");
        }

        var sub = StoragePath.Combine([.. parts.Skip(1), root]);
        return (new ShareAddress(server, parts[0], ""), sub);
    }

    /// <summary>The folders directly in the root of a mounted share (names, sorted).</summary>
    internal static IReadOnlyList<string> MountedFolders(LocalStorage storage)
    {
        try
        {
            return [.. new DirectoryInfo(storage.Root)
                .EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint })
                .Select(directory => directory.Name)
                .Where(name => !ServerPaths.IsHiddenName(name))
                .Order(StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw LocalStorage.Map(exception, "");
        }
    }

    internal static string JoinLocal(string folder, string sub) =>
        sub.Length == 0 ? folder : Path.Combine(folder, sub.Replace('/', Path.DirectorySeparatorChar));
}
