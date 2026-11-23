using ThixSync.Core.Models;

namespace ThixSync.Core.Storage.Providers.Mega;

/// <summary>MEGA (end-to-end encrypted), signed in with e-mail and password; the encryption is done by MegaApiClient.</summary>
public sealed class MegaProvider : IStorageProvider, IFolderBrowser
{
    public IReadOnlyList<ConnectionKind> Kinds { get; } = [ConnectionKind.Mega];

    public IStorage Open(StorageContext context) => new MegaStorage(context);

    public async Task<IReadOnlyList<string>> ListFoldersAsync(StorageContext context, string folder, CancellationToken cancellationToken)
    {
        await using var storage = new MegaStorage(context with { Folder = folder });
        return await storage.ListChildFoldersAsync(cancellationToken);
    }
}
