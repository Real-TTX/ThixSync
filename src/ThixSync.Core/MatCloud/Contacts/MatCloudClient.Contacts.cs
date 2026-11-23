// MatCloud.Contract 1.7.0 – a copy from MatCloud, do not edit here
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MatCloud.Contract.Contacts;

namespace MatCloud.Contract;

public sealed partial class MatCloudClient
{
    // ---- the general sync of another app (a grant: ThixMail reads and writes the contacts and the calendar of its account) ----

    /// <summary>The changes of <paramref name="app"/> (null: the device's own app) above a revision (<see cref="SyncContract.AppParameter"/>).</summary>
    public Task<SyncChangeSet> GetChangesAsync(string? app, long since, int limit, CancellationToken ct) =>
        SendAsync<SyncChangeSet>(HttpMethod.Get, $"{SyncContract.Changes}?since={since}&limit={limit}{AppQuery(app)}", null, RequestTimeout, ct);

    public Task<WaitResult> WaitAsync(string? app, long since, int timeoutSeconds, CancellationToken ct) =>
        SendAsync<WaitResult>(HttpMethod.Get, $"{SyncContract.Wait}?since={since}&timeout={timeoutSeconds}{AppQuery(app)}", null, TimeSpan.FromSeconds(timeoutSeconds + 20), ct);

    public Task<SyncPushResponse> PushAsync(string? app, SyncPushRequest request, CancellationToken ct) =>
        SendAsync<SyncPushResponse>(HttpMethod.Post, SyncContract.Push + (app is null ? string.Empty : "?" + AppQuery(app)[1..]),
            JsonContent.Create(request, options: CloudContract.Json), TransferTimeout, ct);

    // ---- the pictures of contacts ----

    /// <summary>Uploads the picture of a contact (≤ <see cref="ContactsContract.MaxPhotoBytes"/>; JPEG, PNG, GIF or WebP) → its id for <see cref="ContactData.PhotoId"/>.</summary>
    public Task<ContactPhotoInfo> UploadContactPhotoAsync(byte[] bytes, string contentType, CancellationToken ct)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return SendAsync<ContactPhotoInfo>(HttpMethod.Post, ContactsContract.Photos, content, TransferTimeout, ct);
    }

    /// <summary>The bytes of a picture; null when the cloud has none with this id (404).</summary>
    public Task<byte[]?> DownloadContactPhotoAsync(string id, CancellationToken ct) => GetBytesAsync(ContactsContract.Photo(id), ct);

    private static string AppQuery(string? app) => app is null ? string.Empty : "&" + SyncContract.AppParameter + "=" + Uri.EscapeDataString(app);
}
