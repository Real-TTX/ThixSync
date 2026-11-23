using ThixSync.Core.Models;

namespace ThixSync.Core.Storage.Providers.Azure;

/// <summary>Azure Blob Storage: a container of a storage account, signed in with the account key (Shared Key) or a SAS token.</summary>
public sealed class AzureBlobProvider : IStorageProvider, IFolderBrowser
{
    public IReadOnlyList<ConnectionKind> Kinds { get; } = [ConnectionKind.AzureBlob];

    public IStorage Open(StorageContext context) => new AzureBlobStorage(context);

    public async Task<IReadOnlyList<string>> ListFoldersAsync(StorageContext context, string folder, CancellationToken cancellationToken)
    {
        await using var storage = new AzureBlobStorage(context with { Folder = folder });
        return await storage.ListChildFoldersAsync(cancellationToken);
    }
}
