using ThixSync.Core.Models;

namespace ThixSync.Core.Storage.Providers.PCloud;

/// <summary>pCloud (data region EU or USA), signed in with e-mail and password over the digest login.</summary>
public sealed class PCloudProvider : IStorageProvider, IFolderBrowser
{
    public IReadOnlyList<ConnectionKind> Kinds { get; } = [ConnectionKind.PCloud];

    public IStorage Open(StorageContext context) => new PCloudStorage(context);

    public async Task<IReadOnlyList<string>> ListFoldersAsync(StorageContext context, string folder, CancellationToken cancellationToken)
    {
        await using var storage = new PCloudStorage(context with { Folder = folder });
        return await storage.ListChildFoldersAsync(cancellationToken);
    }
}
