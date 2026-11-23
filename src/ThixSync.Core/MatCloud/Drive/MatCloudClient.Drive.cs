// MatCloud.Contract 1.7.0 – a copy from MatCloud, do not edit here
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MatCloud.Contract.Drive;

namespace MatCloud.Contract;

/// <summary>
/// The file changed since it was opened (412) or the name is taken (409): <see cref="Current"/> is the item as the cloud has it now. The app offers
/// "Überschreiben" (<see cref="MatCloudClient.SaveDriveFileAsync"/> with <c>Current.ETag</c>) or "Als Kopie speichern"
/// (<see cref="MatCloudClient.UploadDriveFileAsync"/> with <see cref="DriveNameConflict.Rename"/>).
/// </summary>
public sealed class DriveConflictException(HttpStatusCode statusCode, string message, string? field, DriveItem? current) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string? Field { get; } = field;

    public DriveItem? Current { get; } = current;

    /// <summary>True: somebody saved the file in between (412). False: the name is taken (409).</summary>
    public bool IsChangedMeanwhile => StatusCode == HttpStatusCode.PreconditionFailed;
}

public sealed partial class MatCloudClient
{
    // ---- the Drive: lists ----

    /// <summary>The items of a folder (null: the top of the Drive), folders first.</summary>
    public Task<DriveListing> ListDriveFolderAsync(long? folderId, CancellationToken ct, DriveSort sort = DriveSort.Name, bool descending = false, int offset = 0, int limit = 200)
        => SendAsync<DriveListing>(HttpMethod.Get, DriveContract.Items + Query(("parentId", folderId?.ToString(CultureInfo.InvariantCulture)), ("sort", sort.ToString()),
            ("descending", descending ? "true" : null), ("offset", Number(offset)), ("limit", Number(limit))), null, RequestTimeout, ct);

    /// <summary>One item with the folders above it.</summary>
    public Task<DriveItem> GetDriveItemAsync(long id, CancellationToken ct) => SendAsync<DriveItem>(HttpMethod.Get, DriveContract.Item(id), null, RequestTimeout, ct);

    public Task<DriveItemPage> SearchDriveAsync(string text, CancellationToken ct, int offset = 0, int limit = 100)
        => SendAsync<DriveItemPage>(HttpMethod.Get, DriveContract.Search + Query(("q", text), ("offset", Number(offset)), ("limit", Number(limit))), null, RequestTimeout, ct);

    /// <summary>The files the account opened or saved last – for the list "Zuletzt verwendet" of an app.</summary>
    public Task<DriveItemPage> GetDriveRecentAsync(CancellationToken ct, int limit = 30)
        => SendAsync<DriveItemPage>(HttpMethod.Get, DriveContract.Recent + Query(("limit", Number(limit))), null, RequestTimeout, ct);

    public Task<DriveItemPage> GetDriveStarredAsync(CancellationToken ct, int offset = 0, int limit = 100)
        => SendAsync<DriveItemPage>(HttpMethod.Get, DriveContract.Starred + Query(("offset", Number(offset)), ("limit", Number(limit))), null, RequestTimeout, ct);

    public Task<DriveItemPage> GetDriveTrashAsync(CancellationToken ct, int offset = 0, int limit = 100)
        => SendAsync<DriveItemPage>(HttpMethod.Get, DriveContract.TrashList + Query(("offset", Number(offset)), ("limit", Number(limit))), null, RequestTimeout, ct);

    public Task<DriveQuota> GetDriveQuotaAsync(CancellationToken ct) => SendAsync<DriveQuota>(HttpMethod.Get, DriveContract.Quota, null, RequestTimeout, ct);

    // ---- the Drive: changes of items ----

    /// <summary>A new folder; <see cref="DriveConflictException"/> when the name is taken.</summary>
    public Task<DriveItem> CreateDriveFolderAsync(long? parentId, string name, CancellationToken ct)
        => SendDriveAsync(HttpMethod.Post, DriveContract.Folders, Body(new DriveFolderRequest(parentId, name)), null, RequestTimeout, ct);

    public Task<DriveItem> RenameDriveItemAsync(long id, string name, CancellationToken ct)
        => SendDriveAsync(HttpMethod.Post, DriveContract.Rename(id), Body(new DriveRenameRequest(name)), null, RequestTimeout, ct);

    public Task<DriveItem> MoveDriveItemAsync(long id, long? parentId, CancellationToken ct)
        => SendDriveAsync(HttpMethod.Post, DriveContract.Move(id), Body(new DriveMoveRequest(parentId)), null, RequestTimeout, ct);

    public Task<DriveItem> CopyDriveItemAsync(long id, long? parentId, CancellationToken ct, string? name = null)
        => SendDriveAsync(HttpMethod.Post, DriveContract.Copy(id), Body(new DriveCopyRequest(parentId, name)), null, TransferTimeout, ct);

    public Task<DriveItem> TrashDriveItemAsync(long id, CancellationToken ct)
        => SendDriveAsync(HttpMethod.Post, DriveContract.Trash(id), null, null, RequestTimeout, ct);

    public Task<DriveItem> RestoreDriveItemAsync(long id, CancellationToken ct)
        => SendDriveAsync(HttpMethod.Post, DriveContract.Restore(id), null, null, RequestTimeout, ct);

    /// <summary>Deletes an item of the trash for good.</summary>
    public Task DeleteDriveItemAsync(long id, CancellationToken ct)
        => SendAsync<object>(HttpMethod.Delete, DriveContract.Delete(id), null, RequestTimeout, ct, expectBody: false);

    public Task StarDriveItemAsync(long id, bool starred, CancellationToken ct)
        => SendAsync<object>(starred ? HttpMethod.Post : HttpMethod.Delete, DriveContract.Star(id), null, RequestTimeout, ct, expectBody: false);

    // ---- the Drive: content ----

    /// <summary>
    /// Opens a file: writes the bytes of its current version (or of <paramref name="versionId"/>) into <paramref name="destination"/> and answers the ETag
    /// of that version – keep it for <see cref="SaveDriveFileAsync"/>. Nothing is held in memory.
    /// </summary>
    public async Task<string> DownloadDriveFileAsync(long id, Stream destination, CancellationToken ct, long? versionId = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TransferTimeout);
        var path = versionId is { } version ? DriveContract.VersionContent(id, version) : DriveContract.Content(id);
        using var response = await _http.GetAsync(path.TrimStart('/'), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        await ThrowIfFailedAsync(response, timeout.Token);
        await using (var body = await response.Content.ReadAsStreamAsync(timeout.Token))
        {
            await body.CopyToAsync(destination, timeout.Token);
        }

        return response.Headers.ETag?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Saves a NEW file in a folder (null: the top). Small files go in one request, larger ones in pieces (resumed after a broken connection).
    /// <see cref="DriveNameConflict.Fail"/>: <see cref="DriveConflictException"/> (409) when the name is taken; <see cref="DriveNameConflict.Rename"/>:
    /// the file gets a free name ("Als Kopie speichern").
    /// </summary>
    public async Task<DriveItem> UploadDriveFileAsync(long? parentId, string name, Stream content, DriveNameConflict onConflict, CancellationToken ct, IProgress<long>? progress = null)
    {
        await using var source = await SeekableAsync(content, ct);
        var size = source.Length - source.Position;
        if (size <= DriveContract.MaxSimpleUploadBytes)
        {
            var path = DriveContract.Files + Query(("parentId", parentId?.ToString(CultureInfo.InvariantCulture)), ("name", name), ("onConflict", onConflict.ToString()));
            var item = await SendDriveAsync(HttpMethod.Post, path, Bytes(source), null, TransferTimeout, ct);
            progress?.Report(size);
            return item;
        }

        var upload = await StartDriveUploadAsync(new DriveUploadRequest(size, parentId, name, OnConflict: onConflict), ct);
        return await SendPiecesAsync(upload, source, progress, ct);
    }

    /// <summary>
    /// Saves a new version of a file. <paramref name="ifMatch"/> is the ETag the app opened (or <see cref="DriveContract.AnyVersion"/> to overwrite
    /// whatever is there). Throws <see cref="DriveConflictException"/> (412, <see cref="DriveConflictException.Current"/>) when somebody saved in between.
    /// </summary>
    public async Task<DriveItem> SaveDriveFileAsync(long id, Stream content, string ifMatch, CancellationToken ct, IProgress<long>? progress = null)
    {
        await using var source = await SeekableAsync(content, ct);
        var size = source.Length - source.Position;
        if (size <= DriveContract.MaxSimpleUploadBytes)
        {
            var item = await SendDriveAsync(HttpMethod.Put, DriveContract.Content(id), Bytes(source), ifMatch, TransferTimeout, ct);
            progress?.Report(size);
            return item;
        }

        var upload = await StartDriveUploadAsync(new DriveUploadRequest(size, ItemId: id, IfMatch: ifMatch), ct);
        return await SendPiecesAsync(upload, source, progress, ct);
    }

    // ---- the Drive: uploads in pieces (the building blocks of the two above) ----

    public Task<DriveUploadState> StartDriveUploadAsync(DriveUploadRequest request, CancellationToken ct)
        => SendDriveAsync<DriveUploadState>(HttpMethod.Post, DriveContract.Uploads, Body(request), null, RequestTimeout, ct);

    /// <summary>How much of an upload arrived (to resume it).</summary>
    public Task<DriveUploadState> GetDriveUploadAsync(Guid uploadId, CancellationToken ct)
        => SendAsync<DriveUploadState>(HttpMethod.Get, DriveContract.Upload(uploadId), null, RequestTimeout, ct);

    public Task<DriveUploadState> SendDriveChunkAsync(Guid uploadId, long offset, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        var content = new ReadOnlyMemoryContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return SendAsync<DriveUploadState>(HttpMethod.Put, DriveContract.UploadChunk(uploadId, offset), content, TransferTimeout, ct);
    }

    public Task<DriveItem> FinishDriveUploadAsync(Guid uploadId, CancellationToken ct)
        => SendDriveAsync(HttpMethod.Post, DriveContract.UploadFinish(uploadId), null, null, TransferTimeout, ct);

    public Task CancelDriveUploadAsync(Guid uploadId, CancellationToken ct)
        => SendAsync<object>(HttpMethod.Delete, DriveContract.Upload(uploadId), null, RequestTimeout, ct, expectBody: false);

    // ---- the Drive: versions ----

    public Task<DriveVersionList> ListDriveVersionsAsync(long id, CancellationToken ct)
        => SendAsync<DriveVersionList>(HttpMethod.Get, DriveContract.Versions(id), null, RequestTimeout, ct);

    public Task<DriveItem> RestoreDriveVersionAsync(long id, long versionId, CancellationToken ct)
        => SendDriveAsync(HttpMethod.Post, DriveContract.VersionRestore(id, versionId), null, null, TransferTimeout, ct);

    // ---- helpers ----

    /// <summary>Sends the pieces from what the cloud has; a broken piece is tried again (three times) after asking the cloud how much arrived.</summary>
    private async Task<DriveItem> SendPiecesAsync(DriveUploadState upload, Stream source, IProgress<long>? progress, CancellationToken ct)
    {
        var start = source.Position;
        var buffer = new byte[Math.Clamp(upload.ChunkBytes, 64 * 1024, DriveContract.MaxChunkBytes)];
        var received = upload.Received;
        var failures = 0;
        while (received < upload.Size)
        {
            source.Position = start + received;
            var length = (int)Math.Min(buffer.Length, upload.Size - received);
            await source.ReadExactlyAsync(buffer.AsMemory(0, length), ct);
            try
            {
                received = (await SendDriveChunkAsync(upload.Id, received, buffer.AsMemory(0, length), ct)).Received;
                failures = 0;
                progress?.Report(received);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested && failures < 3)
            {
                failures++;
                await Task.Delay(TimeSpan.FromSeconds(failures), ct);
                received = (await GetDriveUploadAsync(upload.Id, ct)).Received;
            }
        }

        return await FinishDriveUploadAsync(upload.Id, ct);
    }

    private Task<DriveItem> SendDriveAsync(HttpMethod method, string path, HttpContent? content, string? ifMatch, TimeSpan timeoutAfter, CancellationToken ct)
        => SendDriveAsync<DriveItem>(method, path, content, ifMatch, timeoutAfter, ct);

    /// <summary>Like <see cref="SendAsync{T}"/>, but 409 and 412 become a <see cref="DriveConflictException"/> with the current item.</summary>
    private async Task<T> SendDriveAsync<T>(HttpMethod method, string path, HttpContent? content, string? ifMatch, TimeSpan timeoutAfter, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutAfter);
        using var request = new HttpRequestMessage(method, path.TrimStart('/')) { Content = content };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        using var response = await _http.SendAsync(request, timeout.Token);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            DriveConflict? conflict = null;
            try
            {
                conflict = await response.Content.ReadFromJsonAsync<DriveConflict>(CloudContract.Json, timeout.Token);
            }
            catch (JsonException)
            {
            }

            throw new DriveConflictException(response.StatusCode, conflict?.Message ?? "Die Datei wurde inzwischen geändert.", conflict?.Field, conflict?.Current);
        }

        await ThrowIfFailedAsync(response, timeout.Token);
        return await response.Content.ReadFromJsonAsync<T>(CloudContract.Json, timeout.Token)
               ?? throw new CloudException(response.StatusCode, "Die Cloud hat eine leere Antwort geschickt.");
    }

    /// <summary>A stream whose length is known: the stream itself, or a copy in a temporary file (removed when it is closed).</summary>
    private static async Task<Stream> SeekableAsync(Stream content, CancellationToken ct)
    {
        if (content.CanSeek)
        {
            return new KeepOpenStream(content);
        }

        var copy = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        await content.CopyToAsync(copy, ct);
        copy.Position = 0;
        return copy;
    }

    private static StreamContent Bytes(Stream source)
    {
        var content = new StreamContent(new KeepOpenStream(source), 81920);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = source.Length - source.Position;
        return content;
    }

    private static JsonContent Body<T>(T value) => JsonContent.Create(value, options: CloudContract.Json);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Query(params (string Name, string? Value)[] pairs)
    {
        var parts = pairs.Where(p => !string.IsNullOrEmpty(p.Value)).Select(p => p.Name + "=" + Uri.EscapeDataString(p.Value!)).ToList();
        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
    }

    /// <summary>The caller's stream, which the client reads but never closes.</summary>
    private sealed class KeepOpenStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // the caller's stream stays open
        }

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
