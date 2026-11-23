using ThixSync.Core.Models;
using ThixSync.Core.Storage.Providers.OAuth;

namespace ThixSync.Core.Storage.Providers.OneDrive;

/// <summary>
/// Microsoft OneDrive (personal and for Business) and SharePoint document libraries through Microsoft Graph, signed in with OAuth
/// (<see cref="OAuthApp.Microsoft"/>, tenant "common").
/// </summary>
public sealed class OneDriveProvider : IStorageProvider, IFolderBrowser
{
    public IReadOnlyList<ConnectionKind> Kinds { get; } = [ConnectionKind.OneDrive, ConnectionKind.SharePoint];

    public IStorage Open(StorageContext context) => new GraphStorage(context, OAuthApp.Require(context.Connection.Kind));

    public async Task<IReadOnlyList<string>> ListFoldersAsync(StorageContext context, string folder, CancellationToken cancellationToken)
    {
        await using var storage = new GraphStorage(context with { Folder = "" }, OAuthApp.Require(context.Connection.Kind));
        await storage.ConnectAsync(createRoot: false, cancellationToken);
        return await storage.ListFoldersAsync(folder, cancellationToken);
    }
}
