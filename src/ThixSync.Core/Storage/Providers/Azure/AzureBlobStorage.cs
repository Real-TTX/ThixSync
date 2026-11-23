using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using ThixSync.Core.Models;
using ThixSync.Core.Storage.Providers.S3;

namespace ThixSync.Core.Storage.Providers.Azure;

/// <summary>
/// A folder (prefix) of an Azure Blob Storage container. Lists flat in one pass (folders from the name prefixes, zero-byte
/// "folder/" marker blobs and the directories of HNS accounts), writes up to 32 MiB with one Put Blob and larger files in blocks
/// (a few at once, committed with Put Block List and the MD5 of the whole file), moves by Copy Blob + delete. Blobs keep their
/// upload time; the original modification time is kept in the metadata "mtime".
/// </summary>
internal sealed class AzureBlobStorage : IStorage
{
    private const long MiB = 1024 * 1024;
    private const int MaxBlocks = 50_000;
    private const long MaxBlockSize = 100 * MiB;

    private readonly AzureBlobClient _client;
    private readonly string _rootPrefix;
    private readonly SemaphoreSlim _blockBuffers = new(8);

    public AzureBlobStorage(StorageContext context, HttpMessageHandler? handler = null)
    {
        _client = new AzureBlobClient(context, handler);
        var root = StoragePath.Combine(ConnectionCatalog.Value(context.Connection, ConnectionFields.BasePath), context.Folder);
        _rootPrefix = root.Length == 0 ? "" : root + "/";
        DisplayName = $"azure://{_client.Account}/{_client.Container}" + (root.Length == 0 ? "" : "/" + root);
        Capabilities = new StorageCapabilities
        {
            Features = StorageFeatures.CaseSensitive | StorageFeatures.Move | StorageFeatures.ServerSideCopy | StorageFeatures.CoarseTime,
            MaxParallelism = 32,
            HashKind = "md5",
            MaxFileSize = MaxBlocks * MaxBlockSize,
        };
    }

    public ConnectionKind Kind => ConnectionKind.AzureBlob;

    public StorageCapabilities Capabilities { get; }

    public string DisplayName { get; }

    /// <summary>Files up to this size go up with one Put Blob.</summary>
    internal long SinglePutLimit { get; init; } = 32 * MiB;

    /// <summary>The smallest block of a larger file (grows for huge files to stay below 50,000 blocks).</summary>
    internal long BlockSize { get; init; } = 16 * MiB;

    public async Task ConnectAsync(bool createRoot, CancellationToken cancellationToken)
    {
        AzureListPage page;
        try
        {
            page = await _client.ListPageAsync(_rootPrefix, null, null, 1, "", cancellationToken);
        }
        catch (StorageException exception) when (exception.Kind == StorageErrorKind.AccessDenied)
        {
            throw new StorageException(StorageErrorKind.Authentication, $"Der Schlüssel darf den Container „{_client.Container}“ nicht auflisten.", exception);
        }

        if (_rootPrefix.Length == 0 || page.Blobs.Count > 0)
        {
            return;
        }

        if (!createRoot)
        {
            throw new StorageException(StorageErrorKind.NotFound, $"Den Ordner „{_rootPrefix.TrimEnd('/')}“ gibt es im Container „{_client.Container}“ nicht.");
        }

        await PutMarkerAsync(_rootPrefix, "", cancellationToken);
    }

    public async IAsyncEnumerable<StorageEntry> ListAsync(string folder, Action<string, Exception>? onProblem, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var start = StoragePath.Normalize(folder);
        var prefix = start.Length == 0 ? _rootPrefix : $"{_rootPrefix}{start}/";
        var folders = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var blob in _client.ListAllAsync(prefix, start, cancellationToken))
        {
            var relative = blob.Name[_rootPrefix.Length..];
            var isFolder = blob.IsFolder || relative.EndsWith('/');
            var path = relative.EndsWith('/') ? relative[..^1] : relative;
            if (path.Length <= start.Length)
            {
                continue;
            }

            if (!IsPlainPath(path))
            {
                onProblem?.Invoke(path, new StorageException(StorageErrorKind.InvalidName, $"Der Blob „{blob.Name}“ ist kein gültiger Pfad und wird übersprungen."));
                continue;
            }

            for (var slash = path.IndexOf('/', start.Length == 0 ? 0 : start.Length + 1); slash > 0; slash = path.IndexOf('/', slash + 1))
            {
                var parent = path[..slash];
                if (folders.Add(parent))
                {
                    yield return FolderEntry(parent, DateTimeOffset.UnixEpoch);
                }
            }

            if (isFolder)
            {
                if (folders.Add(path))
                {
                    yield return FolderEntry(path, blob.Modified);
                }

                continue;
            }

            yield return FileEntry(path, blob);
        }
    }

    public async Task<StorageEntry?> GetAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.Length == 0)
        {
            return FolderEntry("", DateTimeOffset.UnixEpoch);
        }

        var name = NameOf(normalized);
        var blob = await _client.HeadAsync(name, normalized, cancellationToken);
        if (blob is not null)
        {
            return blob.IsFolder ? FolderEntry(normalized, blob.Modified) : FileEntry(normalized, blob);
        }

        var page = await _client.ListPageAsync(name + "/", null, null, 1, normalized, cancellationToken);
        return page.Blobs.Count > 0 ? FolderEntry(normalized, DateTimeOffset.UnixEpoch) : null;
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        return _client.OpenReadAsync(NameOf(normalized), normalized, cancellationToken);
    }

    public async Task<StorageEntry> WriteAsync(string path, Stream content, long length, DateTimeOffset modified, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var normalized = NotRoot(path);
        var name = NameOf(normalized);
        var metadata = MtimeHeader(modified);
        var stored = length <= SinglePutLimit
            ? await _client.PutBlobAsync(name, () => new StreamContent(new ProgressStream(content, length, progress)) { Headers = { ContentLength = length } },
                length, [metadata], normalized, cancellationToken)
            : await PutBlocksAsync(name, content, length, metadata, progress, normalized, cancellationToken);
        return FileEntry(normalized, stored);
    }

    public Task CreateFolderAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        return normalized.Length == 0 && _rootPrefix.Length == 0
            ? Task.CompletedTask
            : PutMarkerAsync(normalized.Length == 0 ? _rootPrefix : NameOf(normalized) + "/", normalized, cancellationToken);
    }

    public async Task DeleteAsync(string path, bool isFolder, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.Length == 0)
        {
            throw new StorageException(StorageErrorKind.AccessDenied, "Der Wurzelordner eines Auftrags wird nie gelöscht.");
        }

        var name = NameOf(normalized);
        if (!isFolder)
        {
            await _client.DeleteAsync(name, normalized, cancellationToken);
            return;
        }

        // files first (in parallel), then the directories of an HNS account from the deepest up (they must be empty)
        var directories = new List<string>();
        var batch = new List<string>();
        await foreach (var blob in _client.ListAllAsync(name + "/", normalized, cancellationToken))
        {
            if (blob.IsFolder)
            {
                directories.Add(blob.Name);
                continue;
            }

            batch.Add(blob.Name);
            if (batch.Count == 1000)
            {
                await DeleteBlobsAsync(batch, normalized, cancellationToken);
                batch.Clear();
            }
        }

        await DeleteBlobsAsync(batch, normalized, cancellationToken);
        foreach (var directory in directories.OrderByDescending(directory => directory.Count(character => character == '/')))
        {
            await _client.DeleteAsync(directory, normalized, cancellationToken);
        }

        if ((await _client.HeadAsync(name, normalized, cancellationToken))?.IsFolder == true)
        {
            await _client.DeleteAsync(name, normalized, cancellationToken);
        }
    }

    public async Task MoveAsync(string fromPath, string toPath, CancellationToken cancellationToken)
    {
        var from = NotRoot(fromPath);
        var to = NotRoot(toPath);
        if (from == to)
        {
            return;
        }

        var fromName = NameOf(from);
        var toName = NameOf(to);
        var blob = await _client.HeadAsync(fromName, from, cancellationToken);
        if (blob is { IsFolder: false })
        {
            await _client.CopyAsync(fromName, toName, from, cancellationToken);
            await _client.DeleteAsync(fromName, from, cancellationToken);
            return;
        }

        var fromPrefix = fromName + "/";
        var toPrefix = toName + "/";
        var blobs = new List<AzureBlob>();
        await foreach (var item in _client.ListAllAsync(fromPrefix, from, cancellationToken))
        {
            blobs.Add(item);
        }

        if (blobs.Count == 0 && blob is null)
        {
            throw new StorageException(StorageErrorKind.NotFound, $"Nicht gefunden{CloudHttp.Where(from)}.");
        }

        // directories of an HNS account come into being with the blobs below them
        await Parallel.ForEachAsync(blobs.Where(item => !item.IsFolder), new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = cancellationToken },
            async (item, token) => await _client.CopyAsync(item.Name, toPrefix + item.Name[fromPrefix.Length..], from, token));
        await DeleteAsync(from, isFolder: true, cancellationToken);
    }

    /// <summary>The folders directly below the root (the folder picker).</summary>
    internal async Task<IReadOnlyList<string>> ListChildFoldersAsync(CancellationToken cancellationToken)
    {
        var folders = new List<string>();
        string? marker = null;
        do
        {
            var page = await _client.ListPageAsync(_rootPrefix, "/", marker, null, "", cancellationToken);
            folders.AddRange(page.Prefixes
                .Select(prefix => prefix[_rootPrefix.Length..].TrimEnd('/'))
                .Where(name => name.Length > 0 && !name.Contains('/')));
            folders.AddRange(page.Blobs.Where(blob => blob.IsFolder).Select(blob => blob.Name[_rootPrefix.Length..]));
            marker = page.NextMarker;
        }
        while (marker is not null);

        return [.. folders.Distinct(StringComparer.Ordinal).Order(StringComparer.CurrentCultureIgnoreCase)];
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        _blockBuffers.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<AzureBlob> PutBlocksAsync(string name, Stream content, long length, KeyValuePair<string, string> metadata, IProgress<long>? progress,
        string path, CancellationToken cancellationToken)
    {
        var blockSize = BlockSizeFor(length, BlockSize);
        var blockCount = (int)((length + blockSize - 1) / blockSize);
        var inParallel = (int)Math.Clamp(64 * MiB / blockSize, 1, 4);
        var blockIds = Enumerable.Range(0, blockCount)
            .Select(index => Convert.ToBase64String(Encoding.ASCII.GetBytes($"thixsync-{index.ToString("D6", CultureInfo.InvariantCulture)}")))
            .ToArray();
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var tasks = new List<Task>(blockCount);
        using var failure = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var slots = new SemaphoreSlim(inParallel);
        Exception? readError = null;
        try
        {
            for (var index = 0; index < blockCount; index++)
            {
                var size = (int)Math.Min(blockSize, length - index * blockSize);
                var buffer = await TakeBufferAsync(slots, size, failure.Token);
                try
                {
                    await CloudHttp.ReadExactlyAsync(content, buffer.AsMemory(0, size), failure.Token);
                }
                catch
                {
                    GiveBackBuffer(slots, buffer);
                    throw;
                }

                md5.AppendData(buffer, 0, size); // the blocks are read in order: the MD5 of the whole file comes for free
                var blockId = blockIds[index];
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var mark = new ProgressMark();
                        await CloudHttp.RetryAsync(async () =>
                        {
                            await _client.PutBlockAsync(name, blockId,
                                () => new StreamContent(new ProgressStream(new MemoryStream(buffer, 0, size, writable: false), size, progress, mark)) { Headers = { ContentLength = size } },
                                path, failure.Token);
                            return true;
                        }, failure.Token);
                    }
                    catch
                    {
                        await failure.CancelAsync();
                        throw;
                    }
                    finally
                    {
                        GiveBackBuffer(slots, buffer);
                    }
                }, CancellationToken.None));
            }
        }
        catch (Exception exception)
        {
            readError = exception;
            await failure.CancelAsync();
        }

        // uncommitted blocks of a failed upload are removed by the service after a week
        await CloudHttp.WhenAllAsync(tasks);
        if (readError is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(readError);
        }

        var contentMd5 = Convert.ToBase64String(md5.GetHashAndReset());
        var stored = await _client.PutBlockListAsync(name, blockIds, length, [new("x-ms-blob-content-md5", contentMd5), metadata], path, cancellationToken);
        return stored with { ContentMd5 = contentMd5 };
    }

    private async Task<byte[]> TakeBufferAsync(SemaphoreSlim slots, int size, CancellationToken cancellationToken)
    {
        await slots.WaitAsync(cancellationToken);
        try
        {
            await _blockBuffers.WaitAsync(cancellationToken);
        }
        catch
        {
            slots.Release();
            throw;
        }

        return ArrayPool<byte>.Shared.Rent(size);
    }

    private void GiveBackBuffer(SemaphoreSlim slots, byte[] buffer)
    {
        ArrayPool<byte>.Shared.Return(buffer);
        _blockBuffers.Release();
        slots.Release();
    }

    private Task DeleteBlobsAsync(IReadOnlyList<string> names, string path, CancellationToken cancellationToken) =>
        Parallel.ForEachAsync(names, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = cancellationToken },
            async (name, token) => await _client.DeleteAsync(name, path, token));

    private Task PutMarkerAsync(string name, string path, CancellationToken cancellationToken) =>
        _client.PutBlobAsync(name, () => new ByteArrayContent([]), 0, [], path, cancellationToken);

    private string NameOf(string normalized) => _rootPrefix + normalized;

    /// <summary>The block size for a length: at least <paramref name="minimum"/>, whole MiB, at most 50,000 blocks.</summary>
    internal static long BlockSizeFor(long length, long minimum)
    {
        var needed = (length + MaxBlocks - 1) / MaxBlocks;
        return needed <= minimum ? minimum : (needed + MiB - 1) / MiB * MiB;
    }

    private static KeyValuePair<string, string> MtimeHeader(DateTimeOffset modified) =>
        new("x-ms-meta-mtime", (modified.ToUnixTimeMilliseconds() / 1000m).ToString("0.###", CultureInfo.InvariantCulture));

    private static string NotRoot(string path)
    {
        var normalized = StoragePath.Normalize(path);
        return normalized.Length > 0 ? normalized : throw new StorageException(StorageErrorKind.InvalidName, "Der Wurzelordner ist keine Datei.");
    }

    private static bool IsPlainPath(string path)
    {
        try
        {
            return StoragePath.Normalize(path) == path;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsHiddenName(string path) => StoragePath.NameOf(path).StartsWith('.');

    private static StorageEntry FileEntry(string path, AzureBlob blob) =>
        new(path, false, blob.Size, blob.Modified) { Hash = AzureBlobClient.HashOf(blob.ContentMd5), IsHidden = IsHiddenName(path) };

    private static StorageEntry FolderEntry(string path, DateTimeOffset modified) =>
        new(path, true, 0, modified) { IsHidden = path.Length > 0 && IsHiddenName(path) };
}
