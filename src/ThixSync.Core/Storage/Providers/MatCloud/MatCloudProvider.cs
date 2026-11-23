using ThixSync.Core.Models;

namespace ThixSync.Core.Storage.Providers.MatCloud;

/// <summary>
/// "MatCloud / ThixCloud": ThixDrive of the MatCloud account this computer is signed in with (Einstellungen › Cloud; no secret of its
/// own). The folder of a pair lies below <see cref="ConnectionFields.BasePath"/>.
/// </summary>
public sealed class MatCloudProvider : IStorageProvider, IFolderBrowser
{
    public IReadOnlyList<ConnectionKind> Kinds { get; } = [ConnectionKind.MatCloud];

    public IStorage Open(StorageContext context) => new MatCloudStorage(context);

    public async Task<IReadOnlyList<string>> ListFoldersAsync(StorageContext context, string folder, CancellationToken cancellationToken)
    {
        await using var storage = new MatCloudStorage(context with { Folder = "" });
        await storage.ConnectAsync(createRoot: false, cancellationToken);
        return await storage.ListFoldersAsync(folder, cancellationToken);
    }
}
