using ThixSync.Core.Models;
using ThixSync.Core.Storage.Providers.OAuth;

namespace ThixSync.Core.Storage.Providers.Box;

/// <summary>Box.com, signed in with OAuth 2.0 user authentication (<see cref="OAuthApp.Box"/>: client id + secret).</summary>
public sealed class BoxProvider : IStorageProvider, IFolderBrowser
{
    public IReadOnlyList<ConnectionKind> Kinds { get; } = [ConnectionKind.Box];

    public IStorage Open(StorageContext context) => new BoxStorage(context, OAuthApp.Require(ConnectionKind.Box));

    public async Task<IReadOnlyList<string>> ListFoldersAsync(StorageContext context, string folder, CancellationToken cancellationToken)
    {
        await using var storage = new BoxStorage(context with { Folder = "" }, OAuthApp.Require(ConnectionKind.Box));
        await storage.ConnectAsync(createRoot: false, cancellationToken);
        return await storage.ListFoldersAsync(folder, cancellationToken);
    }
}
