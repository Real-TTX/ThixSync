using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using ThixSync.Core.Models;

namespace ThixSync.Core.Storage.Providers.S3;

/// <summary>
/// A folder (prefix) of an S3-compatible bucket. Lists the whole tree in one pass (ListObjectsV2 without delimiter; folders come
/// from the key prefixes and from zero-byte "folder/" marker objects), writes files up to 16 MiB with one PutObject and larger
/// ones with a multipart upload (up to four parts at once, aborted on failure), moves by copying on the server. Objects keep
/// their upload time (S3 cannot set it); the original modification time is kept in x-amz-meta-mtime for restores.
/// </summary>
internal sealed class S3Storage : IStorage
{
    private const long MiB = 1024 * 1024;
    private const int MaxParts = 10_000;
    private const int DeleteBatch = 1000;

    private readonly S3Client _client;
    private readonly SemaphoreSlim _partBuffers = new(8); // buffered parts of all uploads together (memory bound)

    public S3Storage(StorageContext context, HttpMessageHandler? handler = null)
    {
        Kind = context.Connection.Kind;
        _client = new S3Client(S3Settings.From(context, context.Folder), handler)
        {
            RegionLearned = region => context.SaveSettingAsync(ConnectionFields.Region, region, CancellationToken.None),
        };
        Capabilities = new StorageCapabilities
        {
            Features = StorageFeatures.CaseSensitive | StorageFeatures.Move | StorageFeatures.ServerSideCopy | StorageFeatures.CoarseTime,
            MaxParallelism = 32,
            HashKind = "md5",
            MaxFileSize = 5L * 1024 * 1024 * MiB,
        };
    }

    public ConnectionKind Kind { get; }

    public StorageCapabilities Capabilities { get; }

    public string DisplayName => _client.Settings.DisplayName;

    /// <summary>Files up to this size go up in one PutObject.</summary>
    internal long SinglePutLimit { get; init; } = 16 * MiB;

    /// <summary>The smallest part of a multipart upload (grows for huge files to stay below 10,000 parts).</summary>
    internal long PartSize { get; init; } = 16 * MiB;

    /// <summary>Objects above this size are copied in parts (CopyObject takes at most 5 GiB).</summary>
    internal long SingleCopyLimit { get; init; } = 5L * 1024 * MiB;

    /// <summary>The smallest part of a multipart copy.</summary>
    internal long CopyPartSize { get; init; } = 512 * MiB;

    internal S3Settings Settings => _client.Settings;

    public async Task ConnectAsync(bool createRoot, CancellationToken cancellationToken)
    {
        var prefix = Settings.RootPrefix;
        S3ListPage page;
        try
        {
            page = await _client.ListPageAsync(prefix, null, null, 1, "", cancellationToken);
        }
        catch (StorageException exception) when (exception.Kind == StorageErrorKind.AccessDenied)
        {
            throw new StorageException(StorageErrorKind.Authentication, $"Der Schlüssel darf den Bucket „{Settings.Bucket}“ nicht lesen.", exception);
        }

        if (prefix.Length == 0 || page.Objects.Count > 0)
        {
            return;
        }

        if (!createRoot)
        {
            throw new StorageException(StorageErrorKind.NotFound, $"Den Ordner „{prefix.TrimEnd('/')}“ gibt es im Bucket „{Settings.Bucket}“ nicht.");
        }

        await PutMarkerAsync(prefix, "", cancellationToken);
    }

    public async IAsyncEnumerable<StorageEntry> ListAsync(string folder, Action<string, Exception>? onProblem, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var start = StoragePath.Normalize(folder);
        var rootPrefix = Settings.RootPrefix;
        var prefix = start.Length == 0 ? rootPrefix : $"{rootPrefix}{start}/";
        var folders = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var item in _client.ListAllAsync(prefix, start, cancellationToken))
        {
            var relative = item.Key[rootPrefix.Length..];
            var isMarker = relative.EndsWith('/');
            var path = isMarker ? relative[..^1] : relative;
            if (path.Length <= start.Length)
            {
                continue; // the marker of the listed folder itself
            }

            if (!IsPlainPath(path))
            {
                onProblem?.Invoke(path, new StorageException(StorageErrorKind.InvalidName, $"Der Schlüssel „{item.Key}“ ist kein gültiger Pfad und wird übersprungen."));
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

            if (isMarker)
            {
                if (folders.Add(path))
                {
                    yield return FolderEntry(path, item.Modified);
                }

                continue;
            }

            yield return FileEntry(path, item);
        }
    }

    public async Task<StorageEntry?> GetAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.Length == 0)
        {
            return FolderEntry("", DateTimeOffset.UnixEpoch);
        }

        var key = KeyOf(normalized);
        var item = await _client.HeadAsync(key, normalized, cancellationToken);
        if (item is not null)
        {
            return FileEntry(normalized, item);
        }

        var page = await _client.ListPageAsync(key + "/", null, null, 1, normalized, cancellationToken);
        return page.Objects.Count > 0 ? FolderEntry(normalized, DateTimeOffset.UnixEpoch) : null;
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        return _client.OpenReadAsync(KeyOf(normalized), normalized, cancellationToken);
    }

    public async Task<StorageEntry> WriteAsync(string path, Stream content, long length, DateTimeOffset modified, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var normalized = NotRoot(path);
        var key = KeyOf(normalized);
        var headers = NewObjectHeaders(modified);
        var etag = length <= SinglePutLimit
            ? await PutSingleAsync(key, content, length, headers, progress, normalized, cancellationToken)
            : await PutMultipartAsync(key, content, length, headers, progress, normalized, cancellationToken);

        // PutObject does not tell the upload time: ask for it (the engine compares the next listing with what it gets here)
        var stored = await _client.HeadAsync(key, normalized, cancellationToken);
        return stored is null
            ? new StorageEntry(normalized, false, length, S3Client.Truncate(DateTimeOffset.UtcNow)) { Hash = S3Client.HashOf(etag), IsHidden = IsHiddenName(normalized) }
            : FileEntry(normalized, stored);
    }

    public Task CreateFolderAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        return normalized.Length == 0 && Settings.RootPrefix.Length == 0
            ? Task.CompletedTask
            : PutMarkerAsync(normalized.Length == 0 ? Settings.RootPrefix : KeyOf(normalized) + "/", normalized, cancellationToken);
    }

    public async Task DeleteAsync(string path, bool isFolder, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.Length == 0)
        {
            throw new StorageException(StorageErrorKind.AccessDenied, "Der Wurzelordner eines Auftrags wird nie gelöscht.");
        }

        var key = KeyOf(normalized);
        if (!isFolder)
        {
            await _client.DeleteAsync(key, normalized, cancellationToken);
            return;
        }

        var batch = new List<string>(DeleteBatch);
        await foreach (var item in _client.ListAllAsync(key + "/", normalized, cancellationToken))
        {
            batch.Add(item.Key);
            if (batch.Count == DeleteBatch)
            {
                await DeleteKeysAsync(batch, normalized, cancellationToken);
                batch.Clear();
            }
        }

        await DeleteKeysAsync(batch, normalized, cancellationToken);
    }

    public async Task MoveAsync(string fromPath, string toPath, CancellationToken cancellationToken)
    {
        var from = NotRoot(fromPath);
        var to = NotRoot(toPath);
        if (from == to)
        {
            return;
        }

        var fromKey = KeyOf(from);
        var toKey = KeyOf(to);
        var file = await _client.HeadAsync(fromKey, from, cancellationToken);
        if (file is not null)
        {
            await CopyObjectAsync(file, toKey, from, cancellationToken);
            await _client.DeleteAsync(fromKey, from, cancellationToken);
            return;
        }

        var fromPrefix = fromKey + "/";
        var toPrefix = toKey + "/";
        var items = new List<S3Object>();
        await foreach (var item in _client.ListAllAsync(fromPrefix, from, cancellationToken))
        {
            items.Add(item);
        }

        if (items.Count == 0)
        {
            throw new StorageException(StorageErrorKind.NotFound, $"Nicht gefunden{CloudHttp.Where(from)}.");
        }

        await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = cancellationToken },
            async (item, token) => await CopyObjectAsync(item, toPrefix + item.Key[fromPrefix.Length..], from, token));
        for (var index = 0; index < items.Count; index += DeleteBatch)
        {
            await DeleteKeysAsync([.. items.Skip(index).Take(DeleteBatch).Select(item => item.Key)], from, cancellationToken);
        }
    }

    /// <summary>The folders directly below the root (the folder picker).</summary>
    internal async Task<IReadOnlyList<string>> ListChildFoldersAsync(CancellationToken cancellationToken)
    {
        var prefix = Settings.RootPrefix;
        var folders = new List<string>();
        string? token = null;
        do
        {
            var page = await _client.ListPageAsync(prefix, "/", token, null, "", cancellationToken);
            folders.AddRange(page.CommonPrefixes
                .Select(common => common[prefix.Length..].TrimEnd('/'))
                .Where(name => name.Length > 0 && !name.Contains('/')));
            token = page.NextToken;
        }
        while (token is not null);

        return [.. folders.Distinct(StringComparer.Ordinal).Order(StringComparer.CurrentCultureIgnoreCase)];
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        _partBuffers.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<string> PutSingleAsync(string key, Stream content, long length, IReadOnlyList<KeyValuePair<string, string>> headers, IProgress<long>? progress,
        string path, CancellationToken cancellationToken)
    {
        if (Settings.IsHttps)
        {
            // streamed: the payload is not hashed (TLS protects it), so nothing has to be read twice or kept in memory
            return await _client.PutAsync(key, () => new StreamContent(new ProgressStream(content, length, progress)) { Headers = { ContentLength = length } }, length,
                repeatable: false, S3Signer.UnsignedPayload, headers, path, cancellationToken);
        }

        // plain HTTP (a MinIO in the LAN) needs the SHA-256 of the body in the signature: small files are read first
        var body = new byte[length];
        await CloudHttp.ReadExactlyAsync(content, body, cancellationToken);
        var etag = await _client.PutAsync(key, () => new ByteArrayContent(body), length, repeatable: true, S3Signer.Sha256Hex(body), headers, path, cancellationToken);
        progress?.Report(length);
        return etag;
    }

    private async Task<string> PutMultipartAsync(string key, Stream content, long length, IReadOnlyList<KeyValuePair<string, string>> headers, IProgress<long>? progress,
        string path, CancellationToken cancellationToken)
    {
        var partSize = PartSizeFor(length, PartSize);
        var partCount = (int)((length + partSize - 1) / partSize);
        var inParallel = (int)Math.Clamp(64 * MiB / partSize, 1, 4);
        var uploadId = await _client.CreateMultipartAsync(key, headers, path, cancellationToken);
        try
        {
            var etags = new string[partCount];
            var tasks = new List<Task>(partCount);
            using var failure = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var slots = new SemaphoreSlim(inParallel);
            Exception? readError = null;
            try
            {
                for (var index = 0; index < partCount; index++)
                {
                    var size = (int)Math.Min(partSize, length - index * partSize);
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

                    var partNumber = index + 1;
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            etags[partNumber - 1] = await UploadPartAsync(key, uploadId, partNumber, buffer, size, progress, path, failure.Token);
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

            await CloudHttp.WhenAllAsync(tasks);
            if (readError is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(readError);
            }

            return await _client.CompleteMultipartAsync(key, uploadId, etags, path, cancellationToken);
        }
        catch
        {
            await _client.AbortMultipartAsync(key, uploadId);
            throw;
        }
    }

    private Task<string> UploadPartAsync(string key, string uploadId, int partNumber, byte[] buffer, int size, IProgress<long>? progress, string path,
        CancellationToken cancellationToken)
    {
        var payloadHash = Settings.IsHttps ? S3Signer.UnsignedPayload : S3Signer.Sha256Hex(buffer.AsSpan(0, size));
        var mark = new ProgressMark();
        return CloudHttp.RetryAsync(() => _client.UploadPartAsync(key, uploadId, partNumber,
            () => new StreamContent(new ProgressStream(new MemoryStream(buffer, 0, size, writable: false), size, progress, mark)) { Headers = { ContentLength = size } },
            payloadHash, path, cancellationToken), cancellationToken);
    }

    private async Task<byte[]> TakeBufferAsync(SemaphoreSlim slots, int size, CancellationToken cancellationToken)
    {
        await slots.WaitAsync(cancellationToken);
        try
        {
            await _partBuffers.WaitAsync(cancellationToken);
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
        _partBuffers.Release();
        slots.Release();
    }

    /// <summary>Copies an object to another key; above 5 GiB in parts where the service can (the metadata goes along).</summary>
    private async Task CopyObjectAsync(S3Object source, string targetKey, string path, CancellationToken cancellationToken)
    {
        if (source.Size <= SingleCopyLimit || !Settings.MultipartCopy)
        {
            await _client.CopyAsync(source.Key, targetKey, StorageClassHeader(), path, cancellationToken);
            return;
        }

        var head = source.MetaMtime is null ? await _client.HeadAsync(source.Key, path, cancellationToken) ?? source : source;
        var headers = new List<KeyValuePair<string, string>>(StorageClassHeader());
        if (head.MetaMtime is { } mtime)
        {
            headers.Add(new("x-amz-meta-mtime", mtime));
        }

        var partSize = PartSizeFor(head.Size, CopyPartSize);
        var partCount = (int)((head.Size + partSize - 1) / partSize);
        var uploadId = await _client.CreateMultipartAsync(targetKey, headers, path, cancellationToken);
        try
        {
            var etags = new string[partCount];
            await Parallel.ForEachAsync(Enumerable.Range(0, partCount), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
                async (index, token) =>
                {
                    var first = index * partSize;
                    var last = Math.Min(first + partSize, head.Size) - 1;
                    etags[index] = await CloudHttp.RetryAsync(() => _client.UploadPartCopyAsync(targetKey, uploadId, index + 1, source.Key, first, last, path, token), token);
                });
            await _client.CompleteMultipartAsync(targetKey, uploadId, etags, path, cancellationToken);
        }
        catch
        {
            await _client.AbortMultipartAsync(targetKey, uploadId);
            throw;
        }
    }

    private async Task DeleteKeysAsync(IReadOnlyList<string> keys, string path, CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
        {
            return;
        }

        if (Settings.BatchDelete)
        {
            await _client.DeleteManyAsync(keys, path, cancellationToken);
            return;
        }

        await Parallel.ForEachAsync(keys, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = cancellationToken },
            async (key, token) => await _client.DeleteAsync(key, path, token));
    }

    private Task PutMarkerAsync(string key, string path, CancellationToken cancellationToken) =>
        _client.PutAsync(key, () => new ByteArrayContent([]), 0, repeatable: true, S3Signer.EmptyPayloadHash, StorageClassHeader(), path, cancellationToken);

    private List<KeyValuePair<string, string>> NewObjectHeaders(DateTimeOffset modified)
    {
        // seconds with milliseconds, as rclone keeps it ("1700000000.123")
        var seconds = modified.ToUnixTimeMilliseconds() / 1000m;
        return [new("x-amz-meta-mtime", seconds.ToString("0.###", CultureInfo.InvariantCulture)), .. StorageClassHeader()];
    }

    private IReadOnlyList<KeyValuePair<string, string>> StorageClassHeader() =>
        Settings.StorageClass is { } storageClass ? [new("x-amz-storage-class", storageClass)] : [];

    private string KeyOf(string normalized) => Settings.RootPrefix + normalized;

    /// <summary>The part size for a length: at least <paramref name="minimum"/>, whole MiB, at most 10,000 parts.</summary>
    internal static long PartSizeFor(long length, long minimum)
    {
        var needed = (length + MaxParts - 1) / MaxParts;
        return needed <= minimum ? minimum : (needed + MiB - 1) / MiB * MiB;
    }

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

    private static StorageEntry FileEntry(string path, S3Object item) =>
        new(path, false, item.Size, item.Modified) { Hash = S3Client.HashOf(item.ETag), IsHidden = IsHiddenName(path) };

    private static StorageEntry FolderEntry(string path, DateTimeOffset modified) =>
        new(path, true, 0, modified) { IsHidden = path.Length > 0 && IsHiddenName(path) };
}
