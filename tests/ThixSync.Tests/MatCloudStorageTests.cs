using System.Security.Cryptography;
using System.Text;
using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;
using ThixSync.Core.Storage.Providers.MatCloud;

namespace ThixSync.Tests;

/// <summary>
/// The storage "MatCloud" (ThixDrive of the signed-in account) against <see cref="FakeDrive"/>: the root below the base folder, listing
/// (pages, folders in parallel), reading, writing new files and new versions with If-Match, pieces for large files, changes of other
/// devices in between, the trash, moving, and the errors (sign-in gone, Drive not granted, full, policy).
/// </summary>
public sealed class MatCloudStorageTests : IDisposable
{
    private const string ServerUrl = "https://cloud.test";

    private readonly TestData _data = new();
    private readonly FakeDrive _drive = new();

    public MatCloudStorageTests()
    {
        CloudClient.HandlerForTests = () => _drive;
        var protectedToken = TokenStore.Save($"anna@{ServerUrl}", FakeDrive.Token);
        SettingsService.UpdateCloud(cloud => cloud with { ServerUrl = ServerUrl, UserName = "anna", DeviceId = 7, ProtectedToken = protectedToken });
    }

    public void Dispose() => _data.Dispose();

    [Fact]
    public async Task The_root_lies_below_the_base_folder_and_is_made_only_when_asked()
    {
        await using var storage = Open("Fotos/2024");
        var missing = await Assert.ThrowsAsync<StorageException>(() => storage.ConnectAsync(createRoot: false, CancellationToken.None));
        Assert.Equal(StorageErrorKind.NotFound, missing.Kind);

        await storage.ConnectAsync(createRoot: true, CancellationToken.None);
        Assert.NotNull(_drive.Find("Backup/Fotos/2024"));
        Assert.Equal("MatCloud (anna)/Backup/Fotos/2024", storage.DisplayName);
        Assert.Equal("sha256", storage.Capabilities.HashKind);
        Assert.False(storage.Capabilities.Has(StorageFeatures.SetModifiedTime));
        Assert.True(storage.Capabilities.Has(StorageFeatures.Move | StorageFeatures.RealFolders));
        Assert.Equal(2L << 30, storage.Capabilities.MaxFileSize);
    }

    [Fact]
    public async Task Files_are_written_listed_found_and_read()
    {
        _drive.Put("Backup/Fotos/alt.txt", "alt"u8.ToArray());
        await using var storage = await ConnectedAsync("Fotos");

        var progress = new SumProgress();
        var written = await storage.WriteAsync("Urlaub/Strand/bild.jpg", new MemoryStream(Encoding.UTF8.GetBytes("Sonne")), 5, DateTimeOffset.UtcNow, progress,
            CancellationToken.None);
        Assert.Equal("Urlaub/Strand/bild.jpg", written.Path);
        Assert.Equal(5, written.Size);
        Assert.Equal($"sha256:{Convert.ToHexStringLower(SHA256.HashData("Sonne"u8))}", written.Hash);
        Assert.Equal(_drive.Find("Backup/Fotos/Urlaub/Strand/bild.jpg")!.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), written.NativeId);
        Assert.Equal(5, progress.Total);
        await storage.CreateFolderAsync("Leer/Tief", CancellationToken.None);

        var listed = await ListAsync(storage);
        Assert.Equal(["Leer", "Leer/Tief", "Urlaub", "Urlaub/Strand", "Urlaub/Strand/bild.jpg", "alt.txt"], listed.Select(entry => entry.Path).Order(StringComparer.Ordinal));
        Assert.True(listed.Single(entry => entry.Path == "Urlaub").IsFolder);
        Assert.Equal(3, listed.Single(entry => entry.Path == "alt.txt").Size);

        var found = await storage.GetAsync("urlaub/strand/BILD.jpg", CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(written.NativeId, found.Value.NativeId);
        Assert.Null(await storage.GetAsync("Urlaub/fehlt.jpg", CancellationToken.None));
        Assert.Null(await storage.GetAsync("Fehlt/fehlt.jpg", CancellationToken.None));

        Assert.Equal("alt", await ReadTextAsync(storage, "alt.txt"));
        Assert.Equal("Sonne", await ReadTextAsync(storage, "Urlaub/Strand/bild.jpg"));
        await Assert.ThrowsAsync<StorageException>(() => storage.OpenReadAsync("fehlt.txt", CancellationToken.None));
    }

    [Fact]
    public async Task A_new_version_is_saved_with_the_ETag_it_knows()
    {
        await using var storage = await ConnectedAsync("");
        var first = await WriteTextAsync(storage, "brief.txt", "eins");
        var second = await WriteTextAsync(storage, "brief.txt", "zwei");
        Assert.Equal(first.NativeId, second.NativeId);
        Assert.Equal("zwei", Encoding.UTF8.GetString(_drive.ContentOf("Backup/brief.txt")!));
        Assert.Equal(1, _drive.Count("PUT", "/api/v1/drive/items/"));
        Assert.Contains(_drive.Requests, request => request.StartsWith("PUT /api/v1/drive/items/", StringComparison.Ordinal) && request.Contains("If-Match:\""));
    }

    [Fact]
    public async Task A_change_of_another_device_in_between_is_retried_with_what_the_Drive_has_now()
    {
        _drive.Put("Backup/brief.txt", "alt"u8.ToArray());
        await using var storage = await ConnectedAsync("");
        Assert.NotNull(await storage.GetAsync("brief.txt", CancellationToken.None));
        _drive.Put("Backup/brief.txt", "vom Handy"u8.ToArray()); // a new version the storage does not know

        var changed = await Assert.ThrowsAsync<StorageException>(() => WriteTextAsync(storage, "brief.txt", "vom PC"));
        Assert.Equal(StorageErrorKind.Transient, changed.Kind);
        Assert.Equal("vom Handy", Encoding.UTF8.GetString(_drive.ContentOf("Backup/brief.txt")!));

        await WriteTextAsync(storage, "brief.txt", "vom PC");
        Assert.Equal("vom PC", Encoding.UTF8.GetString(_drive.ContentOf("Backup/brief.txt")!));

        // a name taken in between (409): the next try saves a version of that file
        await storage.CreateFolderAsync("neu", CancellationToken.None);
        _drive.Put("Backup/neu/gleichzeitig.txt", "anderes Gerät"u8.ToArray());
        var taken = await Assert.ThrowsAsync<StorageException>(() => WriteTextAsync(storage, "neu/gleichzeitig.txt", "dieser PC"));
        Assert.Equal(StorageErrorKind.Transient, taken.Kind);
        await WriteTextAsync(storage, "neu/gleichzeitig.txt", "dieser PC");
        Assert.Equal("dieser PC", Encoding.UTF8.GetString(_drive.ContentOf("Backup/neu/gleichzeitig.txt")!));
    }

    [Fact]
    public async Task Deleting_goes_to_the_trash_and_moving_renames_or_moves()
    {
        await using var storage = await ConnectedAsync("");
        await WriteTextAsync(storage, "a/eins.txt", "1");
        await WriteTextAsync(storage, "a/zwei.txt", "2");
        await WriteTextAsync(storage, "b/drei.txt", "3");
        await WriteTextAsync(storage, "b/im-weg.txt", "alt");

        await storage.MoveAsync("a/eins.txt", "a/umbenannt.txt", CancellationToken.None);
        await storage.MoveAsync("a/zwei.txt", "b/zwei.txt", CancellationToken.None);
        await storage.MoveAsync("b/drei.txt", "c/tief/vier.txt", CancellationToken.None);
        await storage.MoveAsync("a/umbenannt.txt", "b/im-weg.txt", CancellationToken.None);
        await storage.MoveAsync("b/zwei.txt", "b/ZWEI.txt", CancellationToken.None);

        Assert.Null(_drive.Find("Backup/a/eins.txt"));
        Assert.Equal("ZWEI.txt", _drive.Find("Backup/b/zwei.txt")!.Name);
        Assert.Equal("3", Encoding.UTF8.GetString(_drive.ContentOf("Backup/c/tief/vier.txt")!));
        Assert.Equal("1", Encoding.UTF8.GetString(_drive.ContentOf("Backup/b/im-weg.txt")!));
        Assert.Equal("1", await ReadTextAsync(storage, "b/im-weg.txt"));
        Assert.Equal(["a", "b", "b/ZWEI.txt", "b/im-weg.txt", "c", "c/tief", "c/tief/vier.txt"], (await ListAsync(storage)).Select(entry => entry.Path).Order(StringComparer.Ordinal));

        var folder = _drive.Find("Backup/c")!;
        await storage.DeleteAsync("c", isFolder: true, CancellationToken.None);
        await storage.DeleteAsync("gibt-es-nicht.txt", isFolder: false, CancellationToken.None);
        Assert.True(_drive.IsInTrash(folder.Id));
        Assert.Null(await storage.GetAsync("c/tief/vier.txt", CancellationToken.None));
        await Assert.ThrowsAsync<StorageException>(() => storage.DeleteAsync("", true, CancellationToken.None));
    }

    [Fact]
    public async Task A_large_file_goes_in_pieces_even_from_a_stream_that_cannot_seek()
    {
        await using var storage = await ConnectedAsync("");
        var content = new byte[(int)MatCloud.Contract.Drive.DriveContract.MaxSimpleUploadBytes + 12_345];
        Random.Shared.NextBytes(content);
        var progress = new SumProgress();
        var written = await storage.WriteAsync("gross.bin", new ForwardOnlyStream(content), content.Length, DateTimeOffset.UtcNow, progress, CancellationToken.None);

        Assert.Equal(content.Length, written.Size);
        Assert.Equal(content.Length, progress.Total);
        Assert.Equal(9, _drive.Count("PUT", "/api/v1/drive/uploads/"));
        Assert.Equal(SHA256.HashData(content), SHA256.HashData(_drive.ContentOf("Backup/gross.bin")!));
    }

    [Fact]
    public async Task Big_folders_are_listed_page_by_page()
    {
        for (var index = 0; index < 1_234; index++)
        {
            _drive.Put($"Backup/viele/{index:D4}.txt", [(byte)index]);
        }

        for (var index = 0; index < 20; index++)
        {
            _drive.Put($"Backup/ordner/{index}/{index}/datei.txt", [1]);
        }

        await using var storage = await ConnectedAsync("");
        var listed = await ListAsync(storage);
        Assert.Equal(1_234, listed.Count(entry => entry.Path.StartsWith("viele/", StringComparison.Ordinal)));
        Assert.Equal(20, listed.Count(entry => entry.Path.EndsWith("/datei.txt", StringComparison.Ordinal) && entry.Path.StartsWith("ordner/", StringComparison.Ordinal)));
        Assert.Equal(listed.Count, listed.Select(entry => entry.Path).Distinct().Count());

        var folders = await new MatCloudProvider().ListFoldersAsync(Context(""), "ordner", CancellationToken.None);
        Assert.Equal(20, folders.Count);
    }

    [Fact]
    public async Task A_renamed_item_is_found_again_under_its_new_name()
    {
        _drive.Put("Backup/alt.txt", "x"u8.ToArray());
        await using var storage = await ConnectedAsync("");
        Assert.NotNull(await storage.GetAsync("alt.txt", CancellationToken.None));
        _drive.Rename("Backup/alt.txt", "neu.txt");
        Assert.Null(await storage.GetAsync("alt.txt", CancellationToken.None));
        Assert.NotNull(await storage.GetAsync("neu.txt", CancellationToken.None));
    }

    [Fact]
    public async Task The_errors_of_the_Drive_become_the_kinds_of_the_engine()
    {
        await using var storage = await ConnectedAsync("");

        _drive.IsFull = true;
        var full = await Assert.ThrowsAsync<StorageException>(() => WriteTextAsync(storage, "voll.txt", "x"));
        Assert.Equal(StorageErrorKind.QuotaExceeded, full.Kind);
        _drive.IsFull = false;

        var invalid = await Assert.ThrowsAsync<StorageException>(() => WriteTextAsync(storage, "ende.", "x"));
        Assert.Equal(StorageErrorKind.InvalidName, invalid.Kind);

        _drive.IsForbidden = true;
        var forbidden = await Assert.ThrowsAsync<StorageException>(() => storage.GetAsync("x.txt", CancellationToken.None));
        Assert.Equal(StorageErrorKind.AccessDenied, forbidden.Kind);
        Assert.Contains("ThixSync", forbidden.Message);
        _drive.IsForbidden = false;

        _drive.IsSignedOut = true;
        var signedOut = await Assert.ThrowsAsync<StorageException>(() => ListAsync(storage));
        Assert.Equal(StorageErrorKind.Authentication, signedOut.Kind);
        Assert.Equal("Die MatCloud-Anmeldung ist abgelaufen – Einstellungen › Cloud", signedOut.Message);
        _drive.IsSignedOut = false;

        AccessPolicy.UsePolicyReaderForTests(name => name == "DisableCloud");
        var policy = await Assert.ThrowsAsync<StorageException>(() => storage.GetAsync("x.txt", CancellationToken.None));
        Assert.Equal(StorageErrorKind.AccessDenied, policy.Kind);
        await using var another = Open("");
        await Assert.ThrowsAsync<StorageException>(() => another.ConnectAsync(false, CancellationToken.None));
    }

    [Fact]
    public async Task Without_the_sign_in_of_the_app_there_is_no_Drive()
    {
        SettingsService.UpdateCloud(_ => new CloudSettings());
        await using var storage = Open("");
        var refused = await Assert.ThrowsAsync<StorageException>(() => storage.ConnectAsync(false, CancellationToken.None));
        Assert.Equal(StorageErrorKind.Authentication, refused.Kind);
    }

    // ---- helpers ----

    private StorageContext Context(string folder) => new()
    {
        Connection = new Connection
        {
            Name = "ThixDrive",
            Kind = ConnectionKind.MatCloud,
            Settings = new Dictionary<string, string> { [ConnectionFields.BasePath] = "/Backup" },
        },
        Folder = folder,
    };

    private IStorage Open(string folder) => new MatCloudProvider().Open(Context(folder));

    private async Task<IStorage> ConnectedAsync(string folder)
    {
        var storage = Open(folder);
        await storage.ConnectAsync(createRoot: true, CancellationToken.None);
        return storage;
    }

    private static Task<StorageEntry> WriteTextAsync(IStorage storage, string path, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return storage.WriteAsync(path, new MemoryStream(bytes), bytes.Length, DateTimeOffset.UtcNow, null, CancellationToken.None);
    }

    private static async Task<List<StorageEntry>> ListAsync(IStorage storage, string folder = "")
    {
        var entries = new List<StorageEntry>();
        try
        {
            await foreach (var entry in storage.ListAsync(folder, null, CancellationToken.None))
            {
                entries.Add(entry);
            }
        }
        catch (StorageException exception) when (exception.Kind == StorageErrorKind.NotFound && folder.Length > 0)
        {
            // the folder is not there (yet)
        }

        return entries;
    }

    private static async Task<string> ReadTextAsync(IStorage storage, string path)
    {
        await using var stream = await storage.OpenReadAsync(path, CancellationToken.None);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private sealed class SumProgress : IProgress<long>
    {
        private long _total;

        public long Total => Interlocked.Read(ref _total);

        public void Report(long value) => Interlocked.Add(ref _total, value);
    }

    /// <summary>A source that can only be read forward (a download from another storage).</summary>
    private sealed class ForwardOnlyStream(byte[] content) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, content.Length - _position);
            Array.Copy(content, _position, buffer, offset, read);
            _position += read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
