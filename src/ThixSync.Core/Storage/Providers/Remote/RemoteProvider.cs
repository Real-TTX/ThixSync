using ThixSync.Core.Models;

namespace ThixSync.Core.Storage.Providers.Remote;

/// <summary>
/// "ThixSync Remote": a share of another computer with ThixSync (<see cref="ConnectionFields.Host"/>, <see cref="ConnectionFields.Port"/>,
/// <see cref="ConnectionFields.RemoteShare"/>); the folder of a pair lies inside the share. The secret is the pairing code until the
/// first connection, then the device key ("key:&lt;keyId&gt;:&lt;key&gt;", kept through <see cref="StorageContext.SaveSecretAsync"/>).
/// </summary>
public sealed class RemoteProvider : IStorageProvider, IFolderBrowser
{
    public IReadOnlyList<ConnectionKind> Kinds { get; } = [ConnectionKind.ThixSyncRemote];

    public IStorage Open(StorageContext context) => new RemoteStorage(context);

    public async Task<IReadOnlyList<string>> ListFoldersAsync(StorageContext context, string folder, CancellationToken cancellationToken)
    {
        await using var storage = new RemoteStorage(context with { Folder = "" });
        return await storage.ListFoldersAsync(folder, cancellationToken);
    }
}
