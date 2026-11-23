using ThixSync.Core.Models;
using ThixSync.Core.Storage.Providers.OAuth;

namespace ThixSync.Core.Storage.Providers.GoogleDrive;

/// <summary>Google Drive (My Drive or a shared drive), signed in with OAuth (<see cref="OAuthApp.Google"/>).</summary>
public sealed class GoogleDriveProvider : IStorageProvider, IFolderBrowser
{
    public IReadOnlyList<ConnectionKind> Kinds { get; } = [ConnectionKind.GoogleDrive];

    public IStorage Open(StorageContext context) => new GoogleDriveStorage(context, OAuthApp.Require(ConnectionKind.GoogleDrive));

    public async Task<IReadOnlyList<string>> ListFoldersAsync(StorageContext context, string folder, CancellationToken cancellationToken)
    {
        await using var storage = new GoogleDriveStorage(context with { Folder = "" }, OAuthApp.Require(ConnectionKind.GoogleDrive));
        await storage.ConnectAsync(createRoot: false, cancellationToken);
        return await storage.ListFoldersAsync(folder, cancellationToken);
    }
}
