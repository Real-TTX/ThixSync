using ThixSync.Core.Models;
using ThixSync.Core.Storage;
using ThixSync.Core.Storage.Providers.Network;
using ThixSync.Core.Storage.Providers.Seafile;

namespace ThixSync.Tests;

/// <summary>
/// The Seafile storage against <see cref="FakeSeafileServer"/>: sign-in, the library by name, recursive listing (and folder by
/// folder on older servers), upload with UTF-8 names, download, folders made once even by parallel writers, rename/move without
/// "Name (1)", deleting, a renewed token, and the errors.
/// </summary>
public sealed class SeafileStorageTests : IDisposable
{
    public void Dispose() => HttpStorageClient.HandlerForTests = null;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Seafile_lists_reads_writes_moves_and_deletes(bool recursive)
    {
        var server = Use(new FakeSeafileServer { SupportsRecursive = recursive });
        server.AddFolder("/Fotos");
        server.AddFolder("/Fotos/2024");
        server.AddFile("/Fotos/2024/strand.jpg", "sand");
        server.AddFile("/anderswo.txt", "nicht im Ordner");
        await using var storage = Open("Fotos");
        await storage.ConnectAsync(createRoot: false, CancellationToken.None);

        var listed = await WebDavStorageTests.ListAsync(storage);
        Assert.Equal(["2024", "2024/strand.jpg"], listed.Select(entry => entry.Path));
        Assert.Equal(4, listed[1].Size);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_790_000_000), listed[1].Modified);
        Assert.Equal("sand", await WebDavStorageTests.ReadAsync(storage, "2024/strand.jpg"));

        var progress = new WebDavStorageTests.CountingProgress();
        var written = await WebDavStorageTests.WriteAsync(storage, "Neu/Grüße #1.txt", "Hallo", progress);
        Assert.Equal("Hallo", server.Text("/Fotos/Neu/Grüße #1.txt"));
        Assert.Contains("Grüße #1.txt", server.UploadedNames);
        Assert.Equal(5, progress.Total);
        Assert.Equal(5, written.Size);

        await storage.MoveAsync("Neu/Grüße #1.txt", "Archiv/umbenannt.txt", CancellationToken.None);
        Assert.Null(server.Text("/Fotos/Neu/Grüße #1.txt"));
        Assert.Equal("Hallo", server.Text("/Fotos/Archiv/umbenannt.txt"));

        await WebDavStorageTests.WriteAsync(storage, "Archiv/alt.txt", "alt");
        await storage.MoveAsync("Archiv/umbenannt.txt", "Archiv/alt.txt", CancellationToken.None);
        Assert.Equal("Hallo", server.Text("/Fotos/Archiv/alt.txt"));
        await storage.MoveAsync("2024", "Archiv/2024", CancellationToken.None);
        Assert.Equal("sand", server.Text("/Fotos/Archiv/2024/strand.jpg"));

        await storage.DeleteAsync("Archiv/alt.txt", isFolder: false, CancellationToken.None);
        await storage.DeleteAsync("Neu", isFolder: true, CancellationToken.None);
        Assert.Equal(["/", "/Fotos", "/Fotos/Archiv", "/Fotos/Archiv/2024", "/Fotos/Archiv/2024/strand.jpg", "/anderswo.txt"], server.Paths());
        Assert.Equal("Archiv/2024", (await storage.GetAsync("Archiv/2024", CancellationToken.None))?.Path);
        Assert.Null(await storage.GetAsync("Archiv/nichts", CancellationToken.None));
    }

    [Fact]
    public async Task Parallel_writers_make_a_new_folder_only_once()
    {
        var server = Use(new FakeSeafileServer());
        await using var storage = Open("Sicherung");
        await storage.ConnectAsync(createRoot: true, CancellationToken.None);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(index => WebDavStorageTests.WriteAsync(storage, $"Parallel/Tief/datei{index}.txt", $"{index}")));

        Assert.DoesNotContain(server.Paths(), path => path.Contains(" (1)", StringComparison.Ordinal));
        Assert.Equal(6, server.Paths().Count(path => path.StartsWith("/Sicherung/Parallel/Tief/datei", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task An_expired_token_is_renewed_once()
    {
        var server = Use(new FakeSeafileServer());
        server.AddFile("/a.txt", "a");
        await using var storage = Open("");
        await storage.ConnectAsync(createRoot: false, CancellationToken.None);

        server.ExpireToken();

        Assert.Single(await WebDavStorageTests.ListAsync(storage));
        Assert.Equal(2, server.SignIns);
    }

    [Fact]
    public async Task Errors_say_what_happened()
    {
        Use(new FakeSeafileServer { Password = "anders" });
        await using var refused = Open("");
        var signIn = await Assert.ThrowsAsync<StorageException>(() => refused.ConnectAsync(createRoot: false, CancellationToken.None));
        Assert.Equal(StorageErrorKind.Authentication, signIn.Kind);

        Use(new FakeSeafileServer());
        await using var otherLibrary = Open("", library: "Gibt es nicht");
        var library = await Assert.ThrowsAsync<StorageException>(() => otherLibrary.ConnectAsync(createRoot: false, CancellationToken.None));
        Assert.Equal(StorageErrorKind.NotFound, library.Kind);
        Assert.Contains("Gibt es nicht", library.Message, StringComparison.Ordinal);

        await using var missingRoot = Open("Fehlt");
        var root = await Assert.ThrowsAsync<StorageException>(() => missingRoot.ConnectAsync(createRoot: false, CancellationToken.None));
        Assert.Equal(StorageErrorKind.NotFound, root.Kind);
    }

    private static FakeSeafileServer Use(FakeSeafileServer server)
    {
        HttpStorageClient.HandlerForTests = () => server;
        return server;
    }

    private static IStorage Open(string folder, string library = "Meine Bibliothek") => new SeafileProvider().Open(new StorageContext
    {
        Connection = new Connection
        {
            Name = "Seafile",
            Kind = ConnectionKind.Seafile,
            Settings = new Dictionary<string, string>
            {
                [ConnectionFields.Url] = FakeSeafileServer.Host,
                [ConnectionFields.UserName] = "anna@example.com",
                [ConnectionFields.Library] = library,
            },
        },
        Folder = folder,
        Secret = "geheim",
    });
}
