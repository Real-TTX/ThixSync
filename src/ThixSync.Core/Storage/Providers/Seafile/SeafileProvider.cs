using ThixSync.Core.Models;
using ThixSync.Core.Storage.Providers.Network;
using ThixSync.Core.Storage.Providers.WebDav;

namespace ThixSync.Core.Storage.Providers.Seafile;

/// <summary>"Seafile": a library of a Seafile server (server address, user, password, the name of the library).</summary>
public sealed class SeafileProvider : IStorageProvider, IFolderBrowser
{
    public IReadOnlyList<ConnectionKind> Kinds { get; } = [ConnectionKind.Seafile];

    public IStorage Open(StorageContext context) => OpenSeafile(context);

    public Task<IReadOnlyList<string>> ListFoldersAsync(StorageContext context, string folder, CancellationToken cancellationToken)
    {
        var storage = OpenSeafile(context with { Folder = folder });
        return FolderBrowsing.FoldersAsync(storage, storage.ListFolderAsync, cancellationToken);
    }

    internal static SeafileStorage OpenSeafile(StorageContext context)
    {
        var connection = context.Connection;
        var address = WebDavProvider.AddressOf(connection.Get(ConnectionFields.Url));
        var server = new Uri(address.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
        var library = connection.Get(ConnectionFields.Library).Trim();
        if (library.Length == 0)
        {
            throw new StorageException(StorageErrorKind.NotFound, "Bitte die Bibliothek der Verbindung angeben.");
        }

        return new SeafileStorage(new SeafileOptions(server, connection.Get(ConnectionFields.UserName), context.Secret, library, ServerPaths.RootOf(context))
        {
            TrustedCertificate = connection.Get(ConnectionFields.CertificateHash),
            SaveSetting = context.SaveSettingAsync,
        });
    }
}
