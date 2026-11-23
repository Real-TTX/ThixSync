using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace ThixSync.Core.Storage.Providers.OAuth;

/// <summary>
/// Lists a tree folder by folder for clouds without a listing of everything (Box, OneDrive for Business below the top): every level of
/// folders is read with up to <c>parallelism</c> requests at the same time. A folder that cannot be read (no permission, gone meanwhile)
/// is reported and skipped; every other error ends the listing.
/// </summary>
internal static class CloudWalk
{
    /// <summary>
    /// The entries below the folder <paramref name="startId"/> (at <paramref name="startPath"/>). <paramref name="listChildren"/> returns
    /// the entries directly inside one folder (paths complete, folders with <see cref="StorageEntry.NativeId"/>).
    /// </summary>
    public static async IAsyncEnumerable<StorageEntry> WalkAsync(string startPath, string startId,
        Func<string, string, CancellationToken, Task<IReadOnlyList<StorageEntry>>> listChildren, int parallelism, Action<string, Exception>? onProblem,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IReadOnlyList<(string Path, string Id)> level = [(startPath, startId)];
        while (level.Count > 0)
        {
            var found = new ConcurrentQueue<StorageEntry>();
            var next = new ConcurrentQueue<(string Path, string Id)>();
            var options = new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken };
            await Parallel.ForEachAsync(level, options, async (folder, token) =>
            {
                IReadOnlyList<StorageEntry> children;
                try
                {
                    children = await listChildren(folder.Path, folder.Id, token);
                }
                catch (StorageException exception) when (exception.Kind is StorageErrorKind.AccessDenied or StorageErrorKind.NotFound && folder.Path != startPath)
                {
                    onProblem?.Invoke(folder.Path, exception);
                    return;
                }

                foreach (var child in children)
                {
                    found.Enqueue(child);
                    if (child.IsFolder && child.NativeId is { } id)
                    {
                        next.Enqueue((child.Path, id));
                    }
                }
            });

            foreach (var entry in found)
            {
                yield return entry;
            }

            level = [.. next];
        }
    }
}
