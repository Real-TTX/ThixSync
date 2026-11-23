using FluentFTP;
using ThixSync.Core.Models;
using ThixSync.Core.Storage.Providers.Network;

namespace ThixSync.Core.Storage.Providers.Ftp;

/// <summary>"FTP / FTPS": encryption none, explicit (AUTH TLS, port 21) or implicit (port 990), passive or active mode, anonymous without a user name.</summary>
public sealed class FtpProvider : IStorageProvider, IFolderBrowser
{
    public IReadOnlyList<ConnectionKind> Kinds { get; } = [ConnectionKind.Ftp];

    public IStorage Open(StorageContext context) => OpenFtp(context);

    public Task<IReadOnlyList<string>> ListFoldersAsync(StorageContext context, string folder, CancellationToken cancellationToken)
    {
        var storage = OpenFtp(context with { Folder = folder });
        return FolderBrowsing.FoldersAsync(storage, storage.ListFolderAsync, cancellationToken);
    }

    internal static FtpStorage OpenFtp(StorageContext context)
    {
        var connection = context.Connection;
        var host = connection.Get(ConnectionFields.Host).Trim();
        if (host.Length == 0)
        {
            throw new StorageException(StorageErrorKind.Unreachable, "Der Server der Verbindung fehlt.");
        }

        var encryption = ConnectionCatalog.Value(connection, ConnectionFields.Encryption) switch
        {
            "none" => FtpEncryptionMode.None,
            "implicit" => FtpEncryptionMode.Implicit,
            _ => FtpEncryptionMode.Explicit,
        };
        var port = ServerPaths.PortOf(connection, encryption == FtpEncryptionMode.Implicit ? 990 : 21);
        var passive = !string.Equals(ConnectionCatalog.Value(connection, ConnectionFields.Passive), "false", StringComparison.OrdinalIgnoreCase);
        var user = connection.Get(ConnectionFields.UserName);
        var settings = new FtpSettings(host, port, user, context.Secret, encryption, passive, connection.Get(ConnectionFields.CertificateHash));
        var root = ServerPaths.RootOf(context);
        var scheme = encryption == FtpEncryptionMode.None ? "ftp" : "ftps";
        var display = $"{scheme}://{(user.Length > 0 ? user + "@" : "")}{host}/{root}".TrimEnd('/');
        return new FtpStorage(settings, root, display, context.SaveSettingAsync);
    }
}
