using ThixSync.Core.Models;
using ThixSync.Core.Storage.Providers.Network;

namespace ThixSync.Core.Storage.Providers.WebDav;

/// <summary>
/// "WebDAV / HTTPS" (the address of the connection is the top of the tree) and "Nextcloud / ownCloud" (the server address;
/// the files of the user live below {server}/remote.php/dav/files/{user}/, signed in with an app password).
/// </summary>
public sealed class WebDavProvider : IStorageProvider, IFolderBrowser
{
    public IReadOnlyList<ConnectionKind> Kinds { get; } = [ConnectionKind.WebDav, ConnectionKind.Nextcloud];

    public IStorage Open(StorageContext context)
    {
        var address = AddressOf(context.Connection.Get(ConnectionFields.Url));
        return OpenWebDav(context, address, context.Connection.Kind == ConnectionKind.Nextcloud);
    }

    public Task<IReadOnlyList<string>> ListFoldersAsync(StorageContext context, string folder, CancellationToken cancellationToken)
    {
        var storage = (WebDavStorage)Open(context with { Folder = folder });
        return FolderBrowsing.FoldersAsync(storage, storage.ListFolderAsync, cancellationToken);
    }

    /// <summary>A WebDAV storage at <paramref name="address"/> (also Synology/QNAP and the Hetzner Storage Box with WebDAV).</summary>
    internal static WebDavStorage OpenWebDav(StorageContext context, Uri address, bool isOwnCloud)
    {
        var connection = context.Connection;
        var user = connection.Get(ConnectionFields.UserName);
        var baseUri = isOwnCloud ? WebDavStorage.OwnCloudFilesUri(address, user) : address;
        var root = ServerPaths.RootOf(context);
        var shown = isOwnCloud ? $"{address.Host} ({user})/{root}" : $"{address.GetLeftPart(UriPartial.Path).TrimEnd('/')}/{root}";
        return new WebDavStorage(new WebDavOptions(connection.Kind, baseUri, root, user, context.Secret, isOwnCloud)
        {
            DisplayName = Uri.UnescapeDataString(shown.TrimEnd('/')),
            TrustedCertificate = connection.Get(ConnectionFields.CertificateHash),
            SaveSetting = context.SaveSettingAsync,
        });
    }

    /// <summary>The address of a connection as an absolute http(s) URI (a missing scheme means https).</summary>
    internal static Uri AddressOf(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length > 0 && !trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "https://" + trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new StorageException(StorageErrorKind.Unreachable, "Die Adresse des Servers fehlt oder ist ungültig (https://…).");
        }

        return uri;
    }
}
