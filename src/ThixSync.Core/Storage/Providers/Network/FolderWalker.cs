using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace ThixSync.Core.Storage.Providers.Network;

/// <summary>
/// Lists a tree on a server that can only list one folder per request (SFTP, FTP, WebDAV Depth:1): several folders are read at
/// the same time, every folder found is queued, the entries flow out while the walk goes on. A folder that cannot be read for a
/// lasting reason (no permission, gone) is reported through onProblem and skipped; a short-lived problem is retried; anything else
/// (connection lost, sign-in refused) ends the listing with the error – an incomplete listing must never look complete.
/// </summary>
internal static class FolderWalker
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    /// <summary>Reads the folders below <paramref name="start"/> with <paramref name="parallelism"/> readers.</summary>
    /// <param name="readFolder">The entries directly in a folder (paths relative to the root of the storage).</param>
    public static async IAsyncEnumerable<StorageEntry> WalkAsync(string start, int parallelism, Func<string, CancellationToken, Task<IReadOnlyList<StorageEntry>>> readFolder,
        Action<string, Exception>? onProblem, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var output = Channel.CreateBounded<StorageEntry>(new BoundedChannelOptions(8192) { SingleReader = true });
        var folders = Channel.CreateUnbounded<string>();
        var outstanding = 1;
        Exception? failure = null;
        folders.Writer.TryWrite(start);

        async Task WorkAsync()
        {
            try
            {
                await foreach (var folder in folders.Reader.ReadAllAsync(stop.Token))
                {
                    try
                    {
                        var entries = await ReadWithRetryAsync(folder, folder == start, readFolder, onProblem, stop.Token);
                        foreach (var entry in entries)
                        {
                            if (entry.IsFolder)
                            {
                                Interlocked.Increment(ref outstanding);
                                folders.Writer.TryWrite(entry.Path);
                            }

                            await output.Writer.WriteAsync(entry, stop.Token);
                        }
                    }
                    finally
                    {
                        if (Interlocked.Decrement(ref outstanding) == 0)
                        {
                            folders.Writer.TryComplete();
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Interlocked.CompareExchange(ref failure, exception, null);
                await stop.CancelAsync(); // the other readers stop as well
            }
            catch (OperationCanceledException)
            {
                // stopped: by the caller or because another reader failed
            }
        }

        var workers = Enumerable.Range(0, Math.Clamp(parallelism, 1, 32)).Select(_ => Task.Run(WorkAsync, CancellationToken.None)).ToArray();
        var all = Task.WhenAll(workers).ContinueWith(_ =>
        {
            var error = Volatile.Read(ref failure);
            if (error is null && cancellationToken.IsCancellationRequested)
            {
                error = new OperationCanceledException(cancellationToken);
            }

            output.Writer.TryComplete(error);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        try
        {
            await foreach (var entry in output.Reader.ReadAllAsync(cancellationToken))
            {
                yield return entry;
            }
        }
        finally
        {
            await stop.CancelAsync();
            folders.Writer.TryComplete();
            await all;
        }
    }

    /// <summary>True for errors that are about one folder only (the listing goes on without it).</summary>
    public static bool IsFolderProblem(StorageException exception) =>
        exception.Kind is StorageErrorKind.AccessDenied or StorageErrorKind.NotFound or StorageErrorKind.InvalidName or StorageErrorKind.Locked;

    private static async Task<IReadOnlyList<StorageEntry>> ReadWithRetryAsync(string folder, bool isStart, Func<string, CancellationToken, Task<IReadOnlyList<StorageEntry>>> readFolder,
        Action<string, Exception>? onProblem, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await readFolder(folder, cancellationToken);
            }
            catch (StorageException exception) when (!isStart && IsFolderProblem(exception))
            {
                onProblem?.Invoke(folder, exception);
                return [];
            }
            catch (StorageException exception) when (exception.IsRetryable && !exception.StopsRun && attempt < RetryDelays.Length)
            {
                await Task.Delay(exception.RetryAfter ?? RetryDelays[attempt], cancellationToken);
            }
        }
    }
}
