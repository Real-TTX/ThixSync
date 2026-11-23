using System.Net;
using System.Net.Sockets;
using System.Text;
using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Remote;
using ThixSync.Core.Storage;
using ThixSync.Core.Storage.Providers.Remote;

namespace ThixSync.Tests;

/// <summary>
/// ThixSync Remote with the server and the client in one process on 127.0.0.1: pairing, the device key, the pinned certificate, every
/// operation of the storage, read-only shares, paths that try to leave the share, removed peers, the pool under load and cancellation.
/// </summary>
public sealed class RemoteTests : IDisposable
{
    private readonly TestData _data = new();
    private readonly string _shareFolder;
    private readonly string _readOnlyFolder;
    private readonly int _port;

    public RemoteTests()
    {
        _shareFolder = _data.MakeTree("share", new Dictionary<string, string>
        {
            ["a.txt"] = "A",
            ["sub/b.txt"] = "BB",
            ["sub/deep/c.txt"] = "CCC",
        });
        _readOnlyFolder = _data.MakeTree("readonly", new Dictionary<string, string> { ["r.txt"] = "R" });
        _port = FreeTcpPort();
        RemoteServer.DiscoveryPort = FreeUdpPort();
        SettingsService.UpdateRemote(remote => remote with
        {
            IsEnabled = true,
            Port = _port,
            Shares =
            [
                new RemoteShare { Name = "Daten", LocalPath = _shareFolder },
                new RemoteShare { Name = "Lesen", LocalPath = _readOnlyFolder, IsReadOnly = true },
            ],
        });
        RemoteServer.Start();
    }

    public void Dispose()
    {
        RemoteServer.ResetForTests();
        RemoteServer.DiscoveryPort = RemoteProtocol.DefaultPort;
        _data.Dispose();
    }

    [Fact]
    public async Task Pairing_with_the_right_code_keeps_the_key_the_fingerprint_and_the_peer()
    {
        Assert.True(RemoteServer.IsListening);
        var code = RemoteServer.CreatePairingCode();
        Assert.Matches(@"^\d{4}-\d{4}$", code.Code);

        var client = new Client(_port, "Daten", code.Code);
        await using (var storage = client.Open())
        {
            await storage.ConnectAsync(createRoot: false, CancellationToken.None);
        }

        Assert.StartsWith("key:", client.Secret);
        Assert.Equal(RemoteServer.Fingerprint, client.Setting(ConnectionFields.Fingerprint));
        Assert.Equal(AppInfo.MachineName, client.Setting(ConnectionFields.RemoteDevice));
        var peer = Assert.Single(SettingsService.Get().Remote.Peers);
        Assert.Equal("Laptop", peer.DeviceName);
        Assert.Equal(client.Secret!.Split(':')[1], peer.KeyId);
        Assert.NotNull(peer.LastSeen);

        // the second connection signs in with the key
        await using (var again = client.Open())
        {
            await again.ConnectAsync(createRoot: false, CancellationToken.None);
            Assert.NotNull(await again.GetAsync("a.txt", CancellationToken.None));
        }

        // the code paired one computer and is gone
        var other = new Client(_port, "Daten", code.Code);
        await using var otherStorage = other.Open();
        var refused = await Assert.ThrowsAsync<RemoteRefusedException>(() => otherStorage.ConnectAsync(false, CancellationToken.None));
        Assert.Equal(StorageErrorKind.Authentication, refused.Kind);
    }

    [Fact]
    public async Task A_wrong_code_is_refused_and_five_wrong_tries_void_the_code()
    {
        var code = RemoteServer.CreatePairingCode();
        var wrong = code.Code == "0000-0000" ? "1111-1111" : "0000-0000";
        for (var attempt = 0; attempt < RemoteProtocol.PairingAttempts; attempt++)
        {
            await using var storage = new Client(_port, "Daten", wrong).Open();
            var refused = await Assert.ThrowsAsync<RemoteRefusedException>(() => storage.ConnectAsync(false, CancellationToken.None));
            Assert.Equal(StorageErrorKind.Authentication, refused.Kind);
            Assert.Equal("Der Kopplungscode stimmt nicht.", refused.Message);
        }

        await using var right = new Client(_port, "Daten", code.Code).Open();
        var voided = await Assert.ThrowsAsync<RemoteRefusedException>(() => right.ConnectAsync(false, CancellationToken.None));
        Assert.Equal(StorageErrorKind.Authentication, voided.Kind);
        Assert.Contains("kein Kopplungscode", voided.Message);
        Assert.Empty(SettingsService.Get().Remote.Peers);
    }

    [Fact]
    public async Task Every_operation_works_on_a_folder_inside_the_share()
    {
        var client = await PairAsync();
        await using var storage = client.Open("sub");
        await storage.ConnectAsync(createRoot: false, CancellationToken.None);
        Assert.True(storage.Capabilities.Has(StorageFeatures.SetModifiedTime | StorageFeatures.Move | StorageFeatures.RealFolders));
        Assert.False(storage.Capabilities.Has(StorageFeatures.LocalFileSystem));

        var listed = await ListAsync(storage);
        Assert.Equal(["b.txt", "deep", "deep/c.txt"], listed.Select(entry => entry.Path).Order(StringComparer.Ordinal));
        Assert.Equal(3, listed.Single(entry => entry.Path == "deep/c.txt").Size);

        var entry = await storage.GetAsync("deep/c.txt", CancellationToken.None);
        Assert.NotNull(entry);
        Assert.False(entry.Value.IsFolder);
        Assert.Null(await storage.GetAsync("missing.txt", CancellationToken.None));

        Assert.Equal("BB", await ReadTextAsync(storage, "b.txt"));

        var modified = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);
        var content = Encoding.UTF8.GetBytes(new string('x', 700_000));
        var progress = new SumProgress();
        var written = await storage.WriteAsync("neu/datei.txt", new MemoryStream(content), content.Length, modified, progress, CancellationToken.None);
        Assert.Equal("neu/datei.txt", written.Path);
        Assert.Equal(content.Length, written.Size);
        Assert.Equal(modified, written.Modified);
        Assert.Equal(content.Length, progress.Total);
        var local = Path.Combine(_shareFolder, "sub", "neu", "datei.txt");
        Assert.Equal(content, await File.ReadAllBytesAsync(local));
        Assert.Equal(modified.UtcDateTime, File.GetLastWriteTimeUtc(local));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(local)!, ".~thixsync-*"));

        await storage.CreateFolderAsync("leer/tief", CancellationToken.None);
        Assert.True(Directory.Exists(Path.Combine(_shareFolder, "sub", "leer", "tief")));

        await storage.MoveAsync("neu/datei.txt", "umbenannt/x.txt", CancellationToken.None);
        Assert.False(File.Exists(local));
        Assert.True(File.Exists(Path.Combine(_shareFolder, "sub", "umbenannt", "x.txt")));

        await storage.DeleteAsync("umbenannt/x.txt", isFolder: false, CancellationToken.None);
        await storage.DeleteAsync("leer", isFolder: true, CancellationToken.None);
        await storage.DeleteAsync("gibt-es-nicht.txt", isFolder: false, CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(_shareFolder, "sub", "umbenannt", "x.txt")));
        Assert.False(Directory.Exists(Path.Combine(_shareFolder, "sub", "leer")));

        var folders = await new RemoteProvider().ListFoldersAsync(client.Context(""), "sub", CancellationToken.None);
        Assert.Contains("deep", folders);
    }

    [Fact]
    public async Task A_missing_root_is_made_only_when_asked()
    {
        var client = await PairAsync();
        await using var storage = client.Open("neu/ordner");
        var missing = await Assert.ThrowsAsync<StorageException>(() => storage.ConnectAsync(createRoot: false, CancellationToken.None));
        Assert.Equal(StorageErrorKind.NotFound, missing.Kind);

        await storage.ConnectAsync(createRoot: true, CancellationToken.None);
        Assert.True(Directory.Exists(Path.Combine(_shareFolder, "neu", "ordner")));

        await using var unknownShare = new Client(_port, "Gibt es nicht", client.Secret, client.Setting(ConnectionFields.Fingerprint)).Open();
        var noShare = await Assert.ThrowsAsync<RemoteRefusedException>(() => unknownShare.ConnectAsync(false, CancellationToken.None));
        Assert.Equal(StorageErrorKind.NotFound, noShare.Kind);
    }

    [Fact]
    public async Task A_read_only_share_can_be_read_but_never_changed()
    {
        var client = await PairAsync("Lesen");
        await using var storage = client.Open();
        await storage.ConnectAsync(createRoot: false, CancellationToken.None);
        Assert.Equal("R", await ReadTextAsync(storage, "r.txt"));

        var write = await Assert.ThrowsAsync<RemoteRefusedException>(() =>
            storage.WriteAsync("neu.txt", new MemoryStream([1, 2, 3]), 3, DateTimeOffset.UtcNow, null, CancellationToken.None));
        Assert.Equal(StorageErrorKind.AccessDenied, write.Kind);
        await Assert.ThrowsAsync<RemoteRefusedException>(() => storage.DeleteAsync("r.txt", false, CancellationToken.None));
        await Assert.ThrowsAsync<RemoteRefusedException>(() => storage.MoveAsync("r.txt", "s.txt", CancellationToken.None));
        await Assert.ThrowsAsync<RemoteRefusedException>(() => storage.CreateFolderAsync("ordner", CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(_readOnlyFolder, "r.txt")));
        Assert.False(File.Exists(Path.Combine(_readOnlyFolder, "neu.txt")));
        Assert.Equal("R", await ReadTextAsync(storage, "r.txt")); // the connection is still fine
    }

    [Theory]
    [InlineData("../x.txt")]
    [InlineData("sub/../../x.txt")]
    [InlineData("..\\x.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("C:x.txt")]
    [InlineData("a.txt:stream")]
    public async Task A_path_that_leaves_the_share_is_refused(string path)
    {
        var client = await PairAsync();
        var key = (RemoteCredentials.DeviceKey)RemoteCredentials.Parse(client.Secret)!;
        var (connection, _) = await RemoteClientConnection.OpenAsync("127.0.0.1", _port, key, client.Setting(ConnectionFields.Fingerprint), "Laptop", CancellationToken.None);
        await using (connection)
        {
            foreach (var op in new[] { "get", "read", "list", "mkdir", "delete" })
            {
                await connection.Channel.WriteJsonAsync(RemoteFrameType.Request, new RemoteRequest { Op = op, Share = "Daten", Path = path },
                    RemoteJson.Default.RemoteRequest, CancellationToken.None);
                var refused = await Assert.ThrowsAsync<RemoteRefusedException>(() =>
                    connection.Channel.ReadJsonAsync(RemoteFrameType.Response, RemoteJson.Default.RemoteReply, CancellationToken.None));
                Assert.Equal(StorageErrorKind.AccessDenied, refused.Kind);
            }

            await connection.Channel.WriteJsonAsync(RemoteFrameType.Request, new RemoteRequest { Op = "move", Share = "Daten", Path = "a.txt", To = path },
                RemoteJson.Default.RemoteRequest, CancellationToken.None);
            await Assert.ThrowsAsync<RemoteRefusedException>(() =>
                connection.Channel.ReadJsonAsync(RemoteFrameType.Response, RemoteJson.Default.RemoteReply, CancellationToken.None));
        }

        Assert.True(File.Exists(Path.Combine(_shareFolder, "a.txt")));
    }

    [Fact]
    public async Task Another_certificate_than_the_pinned_one_is_refused()
    {
        var client = await PairAsync();
        var changed = new Client(_port, "Daten", client.Secret, string.Join(':', Enumerable.Repeat("AB", 32)));
        await using var storage = changed.Open();
        var refused = await Assert.ThrowsAsync<StorageException>(() => storage.ConnectAsync(false, CancellationToken.None));
        Assert.Equal(StorageErrorKind.Authentication, refused.Kind);
        Assert.Contains("Fingerabdruck", refused.Message);
    }

    [Fact]
    public async Task A_removed_peer_is_refused()
    {
        var client = await PairAsync();
        var keyId = client.Secret!.Split(':')[1];
        RemoteServer.RemovePeer(keyId);
        Assert.Empty(SettingsService.Get().Remote.Peers);

        await using var storage = client.Open();
        var refused = await Assert.ThrowsAsync<RemoteRefusedException>(() => storage.ConnectAsync(false, CancellationToken.None));
        Assert.Equal(StorageErrorKind.Authentication, refused.Kind);
    }

    [Fact]
    public async Task Switching_Remote_off_ends_the_access()
    {
        var client = await PairAsync();
        SettingsService.UpdateRemote(remote => remote with { IsEnabled = false });
        Assert.False(RemoteServer.IsListening);
        Assert.Throws<BusinessException>(() => RemoteServer.CreatePairingCode());

        await using var storage = client.Open();
        var refused = await Assert.ThrowsAsync<StorageException>(() => storage.ConnectAsync(false, CancellationToken.None));
        Assert.Equal(StorageErrorKind.Unreachable, refused.Kind);

        AccessPolicy.UsePolicyReaderForTests(name => name == "DisableRemote");
        await using var forbidden = client.Open();
        var policy = await Assert.ThrowsAsync<StorageException>(() => forbidden.ConnectAsync(false, CancellationToken.None));
        Assert.Equal(StorageErrorKind.AccessDenied, policy.Kind);
    }

    [Fact]
    public async Task Many_transfers_at_once_share_the_pool()
    {
        var client = await PairAsync();
        await using var storage = client.Open();
        await storage.ConnectAsync(createRoot: false, CancellationToken.None);

        var files = Enumerable.Range(0, 24).Select(index => (Name: $"viele/{index:D2}.bin", Content: RandomBytes(index * 50_000 + 17))).ToList();
        await Task.WhenAll(files.Select(file =>
            storage.WriteAsync(file.Name, new MemoryStream(file.Content), file.Content.Length, DateTimeOffset.UtcNow, null, CancellationToken.None)));
        var read = await Task.WhenAll(files.Select(async file =>
        {
            await using var stream = await storage.OpenReadAsync(file.Name, CancellationToken.None);
            var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            return copy.ToArray();
        }));

        for (var index = 0; index < files.Count; index++)
        {
            Assert.Equal(files[index].Content, read[index]);
        }

        Assert.Equal(files.Count + 1, (await ListAsync(storage)).Count(entry => entry.Path.StartsWith("viele", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Cancelling_or_leaving_a_read_early_keeps_the_storage_usable()
    {
        var client = await PairAsync();
        await using var storage = client.Open();
        await storage.ConnectAsync(createRoot: false, CancellationToken.None);
        var big = RandomBytes(5 * 1024 * 1024);
        await File.WriteAllBytesAsync(Path.Combine(_shareFolder, "gross.bin"), big);

        for (var round = 0; round < RemoteStorage.PoolSize + 2; round++)
        {
            using var cancel = new CancellationTokenSource();
            await using (var stream = await storage.OpenReadAsync("gross.bin", CancellationToken.None))
            {
                var buffer = new byte[64 * 1024];
                Assert.True(await stream.ReadAsync(buffer, cancel.Token) > 0);
                if (round % 2 == 0)
                {
                    await cancel.CancelAsync();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    {
                        while (await stream.ReadAsync(buffer, cancel.Token) > 0)
                        {
                        }
                    });
                }
            }
        }

        Assert.Equal("A", await ReadTextAsync(storage, "a.txt"));
        await using var whole = await storage.OpenReadAsync("gross.bin", CancellationToken.None);
        var copy = new MemoryStream();
        await whole.CopyToAsync(copy);
        Assert.Equal(big, copy.ToArray());
    }

    [Fact]
    public async Task A_write_whose_source_ends_early_fails_and_leaves_no_file()
    {
        var client = await PairAsync();
        await using var storage = client.Open();
        await storage.ConnectAsync(createRoot: false, CancellationToken.None);
        var failed = await Assert.ThrowsAsync<StorageException>(() =>
            storage.WriteAsync("kurz.txt", new MemoryStream([1, 2, 3]), 10, DateTimeOffset.UtcNow, null, CancellationToken.None));
        Assert.Equal(StorageErrorKind.ChangedWhileReading, failed.Kind);
        Assert.False(File.Exists(Path.Combine(_shareFolder, "kurz.txt")));
        Assert.Equal("A", await ReadTextAsync(storage, "a.txt"));
        await WaitUntilAsync(() => Directory.GetFiles(_shareFolder, ".~thixsync-*").Length == 0);
    }

    [Fact]
    public async Task The_discovery_finds_the_server()
    {
        var found = await RemoteServer.DiscoverAsync(TimeSpan.FromSeconds(1), [new IPEndPoint(IPAddress.Loopback, RemoteServer.DiscoveryPort)], includeSelf: true,
            CancellationToken.None);
        var computer = Assert.Single(found);
        Assert.Equal(AppInfo.MachineName, computer.DeviceName);
        Assert.Equal(_port, computer.Port);
        Assert.Equal("127.0.0.1", computer.Host);

        SettingsService.UpdateRemote(remote => remote with { IsEnabled = false });
        Assert.Empty(await RemoteServer.DiscoverAsync(TimeSpan.FromMilliseconds(500), [new IPEndPoint(IPAddress.Loopback, RemoteServer.DiscoveryPort)],
            includeSelf: true, CancellationToken.None));
    }

    [Fact]
    public void The_identity_stays_the_same_and_the_secret_forms_are_understood()
    {
        var fingerprint = RemoteServer.Fingerprint;
        Assert.Matches("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", fingerprint);
        Assert.Equal(fingerprint, RemoteServer.Fingerprint);
        Assert.True(File.Exists(Path.Combine(AppPaths.RemoteDirectory, "identity.pfx")));

        Assert.IsType<RemoteCredentials.Pairing>(RemoteCredentials.Parse("1234-5678"));
        Assert.IsType<RemoteCredentials.Pairing>(RemoteCredentials.Parse("1234 5678"));
        Assert.IsType<RemoteCredentials.Pairing>(RemoteCredentials.Parse("12345678"));
        Assert.IsType<RemoteCredentials.DeviceKey>(RemoteCredentials.Parse("key:abc:" + Convert.ToBase64String(new byte[32])));
        Assert.Null(RemoteCredentials.Parse("1234-567"));
        Assert.Null(RemoteCredentials.Parse("Passwort"));
    }

    // ---- helpers ----

    private async Task<Client> PairAsync(string share = "Daten")
    {
        var client = new Client(_port, share, RemoteServer.CreatePairingCode().Code);
        await using var storage = client.Open();
        await storage.ConnectAsync(createRoot: false, CancellationToken.None);
        return client;
    }

    private static async Task<List<StorageEntry>> ListAsync(IStorage storage)
    {
        var entries = new List<StorageEntry>();
        await foreach (var entry in storage.ListAsync("", null, CancellationToken.None))
        {
            entries.Add(entry);
        }

        return entries;
    }

    private static async Task<string> ReadTextAsync(IStorage storage, string path)
    {
        await using var stream = await storage.OpenReadAsync(path, CancellationToken.None);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static int FreeUdpPort()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }

    private sealed class SumProgress : IProgress<long>
    {
        private long _total;

        public long Total => Interlocked.Read(ref _total);

        public void Report(long value) => Interlocked.Add(ref _total, value);
    }

    /// <summary>The other computer: its connection, the secret and the fields the provider learned.</summary>
    private sealed class Client
    {
        private readonly Lock _gate = new();
        private Connection _connection;

        public Client(int port, string share, string? secret, string? fingerprint = null)
        {
            _connection = new Connection
            {
                Name = "Remote",
                Kind = ConnectionKind.ThixSyncRemote,
                Settings = new Dictionary<string, string>
                {
                    [ConnectionFields.Host] = "127.0.0.1",
                    [ConnectionFields.Port] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    [ConnectionFields.RemoteShare] = share,
                    [ConnectionFields.Fingerprint] = fingerprint ?? "",
                },
            };
            Secret = secret;
        }

        public string? Secret { get; private set; }

        public string Setting(string key)
        {
            lock (_gate)
            {
                return _connection.Get(key);
            }
        }

        public StorageContext Context(string folder)
        {
            lock (_gate)
            {
                return new StorageContext
                {
                    Connection = _connection,
                    Folder = folder,
                    Secret = Secret,
                    DeviceName = "Laptop",
                    SaveSecretAsync = (secret, _) =>
                    {
                        lock (_gate)
                        {
                            Secret = secret;
                        }

                        return Task.CompletedTask;
                    },
                    SaveSettingAsync = (key, value, _) =>
                    {
                        lock (_gate)
                        {
                            _connection = _connection.With(key, value);
                        }

                        return Task.CompletedTask;
                    },
                };
            }
        }

        public IStorage Open(string folder = "") => new RemoteProvider().Open(Context(folder));
    }
}
