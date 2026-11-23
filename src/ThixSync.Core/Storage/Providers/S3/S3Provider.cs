using ThixSync.Core.Models;

namespace ThixSync.Core.Storage.Providers.S3;

/// <summary>
/// Amazon S3 and S3-compatible services (MinIO, Ceph, Cloudflare R2 … over the endpoint field), Wasabi, Backblaze B2 (its S3
/// interface) and Google Cloud Storage (XML interface with HMAC keys): the same REST calls, signed with Signature V4.
/// </summary>
public sealed class S3Provider : IStorageProvider, IFolderBrowser
{
    public IReadOnlyList<ConnectionKind> Kinds { get; } =
        [ConnectionKind.S3, ConnectionKind.Wasabi, ConnectionKind.BackblazeB2, ConnectionKind.GoogleCloudStorage];

    public IStorage Open(StorageContext context) => new S3Storage(context);

    public async Task<IReadOnlyList<string>> ListFoldersAsync(StorageContext context, string folder, CancellationToken cancellationToken)
    {
        await using var storage = new S3Storage(context with { Folder = folder });
        return await storage.ListChildFoldersAsync(cancellationToken);
    }
}
