using ThixSync.Core.Models;
using ThixSync.Core.Storage.Providers.OAuth;

namespace ThixSync.Core.Storage.Providers.Dropbox;

/// <summary>Dropbox (Basic, Plus, Business), signed in with OAuth and PKCE (<see cref="OAuthApp.Dropbox"/>).</summary>
public sealed class DropboxProvider : IStorageProvider, IFolderBrowser
{
    public IReadOnlyList<ConnectionKind> Kinds { get; } = [ConnectionKind.Dropbox];

    public IStorage Open(StorageContext context) => new DropboxStorage(context, OAuthApp.Require(ConnectionKind.Dropbox));

    public async Task<IReadOnlyList<string>> ListFoldersAsync(StorageContext context, string folder, CancellationToken cancellationToken)
    {
        await using var storage = new DropboxStorage(context with { Folder = "" }, OAuthApp.Require(ConnectionKind.Dropbox));
        return await storage.ListFoldersAsync(folder, cancellationToken);
    }
}
