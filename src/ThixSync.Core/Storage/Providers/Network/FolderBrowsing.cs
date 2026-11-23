namespace ThixSync.Core.Storage.Providers.Network;

/// <summary>The folder picker of the server providers: connect at the folder, read that one folder, keep the names of its folders.</summary>
internal static class FolderBrowsing
{
    public static async Task<IReadOnlyList<string>> FoldersAsync(IStorage storage, Func<string, CancellationToken, Task<IReadOnlyList<StorageEntry>>> readFolder,
        CancellationToken cancellationToken)
    {
        await using (storage)
        {
            await storage.ConnectAsync(createRoot: false, cancellationToken);
            var entries = await readFolder("", cancellationToken);
            return [.. entries.Where(entry => entry.IsFolder).Select(entry => entry.Name).Order(StringComparer.CurrentCultureIgnoreCase)];
        }
    }
}
