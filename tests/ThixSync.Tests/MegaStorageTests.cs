using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using CG.Web.MegaApiClient;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;
using ThixSync.Core.Storage.Providers.Mega;

namespace ThixSync.Tests;

/// <summary>
/// MEGA without the network: the tree of nodes as paths (duplicates, folders before files on the way), and the storage against
/// an in-memory MegaApiClient (no encryption involved: that is MegaApiClient's part).
/// </summary>
public sealed class MegaStorageTests
{
    [Fact]
    public void The_tree_maps_nodes_to_paths()
    {
        var tree = new MegaTree();
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var recent = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        tree.Load(
        [
            FakeNode.Folder("root", null, "Cloud Drive", NodeType.Root),
            FakeNode.Folder("trash", null, "Rubbish Bin", NodeType.Trash),
            FakeNode.Folder("backup", "root", "Backup"),
            FakeNode.Folder("fotos", "backup", "Fotos"),
            FakeNode.File("a1", "fotos", "a.jpg", 10, old),
            FakeNode.File("a2", "fotos", "a.jpg", 20, recent), // the same name twice: the newest counts
            FakeNode.File("x", "backup", "Fotos", 5, recent), // a file named like the folder on the way
            FakeNode.File("t", "trash", "weg.txt", 1, old),
        ]);

        var root = tree.Root!;
        Assert.Equal("a2", tree.Find(root, "Backup/Fotos/a.jpg")!.Id);
        Assert.Equal("fotos", tree.Find(root, "Backup/Fotos")!.Id); // a folder wins over a newer file of the same name
        Assert.Null(tree.Find(root, "Backup/fotos/a.jpg")); // case-sensitive like MEGA
        Assert.Null(tree.Find(root, "Rubbish Bin/weg.txt"));

        var duplicates = new List<string>();
        var walked = tree.Walk(tree.Find(root, "Backup")!, "", duplicates.Add);
        Assert.Contains(("Fotos/a.jpg", "a2"), walked.Select(item => (item.Path, item.Node.Id)));
        Assert.Contains("Fotos/a.jpg", duplicates);
        Assert.Contains("Fotos", duplicates);

        tree.Remove(tree.Find(root, "Backup/Fotos/a.jpg")!);
        Assert.Equal("a1", tree.Find(root, "Backup/Fotos/a.jpg")!.Id);
        tree.Remove(tree.Find(root, "Backup")!);
        Assert.Null(tree.Find(root, "Backup/Fotos/a.jpg"));
    }

    [Fact]
    public void Times_of_megaapiclient_are_local_and_become_utc()
    {
        var local = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc).ToLocalTime();

        Assert.Equal(new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero), MegaTree.TimeOf(FakeNode.File("f", "p", "n", 1, local)));
    }

    [Fact]
    public async Task Writes_replaces_lists_reads_moves_and_deletes()
    {
        var mega = FakeMega.Create();
        await using var storage = new MegaStorage(Context(), (IMegaApiClient)mega);
        await storage.ConnectAsync(createRoot: true, CancellationToken.None);
        var fake = (FakeMega)mega;
        Assert.Equal(["Backup", "Fotos"], fake.Nodes.Values.Where(node => node.Type == NodeType.Directory).Select(node => node.Name).Order());

        var modified = new DateTimeOffset(2023, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var reported = 0L;
        var data = Encoding.UTF8.GetBytes("Hallo MEGA");
        var written = await storage.WriteAsync("Urlaub/Strand.txt", new NonSeekableStream(data), data.Length, modified,
            new SyncProgress(value => Interlocked.Add(ref reported, value)), CancellationToken.None);
        Assert.Equal(modified, written.Modified);
        Assert.Equal(data.Length, reported);

        await S3StorageTests.WriteTextAsync(storage, "Urlaub/Strand.txt", "Neue Fassung", modified.AddDays(1));
        Assert.Single(fake.Nodes.Values, node => node.Name == "Strand.txt"); // the old node is gone after the new one is there

        var listed = await S3StorageTests.ListAsync(storage, "");
        Assert.Equal(["Urlaub", "Urlaub/Strand.txt"], listed.Select(entry => entry.Path).Order(StringComparer.Ordinal));
        Assert.Equal("Neue Fassung", await S3StorageTests.ReadTextAsync(storage, "Urlaub/Strand.txt"));
        Assert.Equal(modified.AddDays(1), (await storage.GetAsync("Urlaub/Strand.txt", CancellationToken.None))!.Value.Modified);

        await storage.MoveAsync("Urlaub/Strand.txt", "Archiv/2023/Meer.txt", CancellationToken.None);
        await storage.MoveAsync("Urlaub", "Archiv/Urlaub alt", CancellationToken.None);
        var moved = await S3StorageTests.ListAsync(storage, "");
        Assert.Equal(["Archiv", "Archiv/2023", "Archiv/2023/Meer.txt", "Archiv/Urlaub alt"], moved.Select(entry => entry.Path).Order(StringComparer.Ordinal));

        await storage.DeleteAsync("Archiv", isFolder: true, CancellationToken.None);
        await storage.DeleteAsync("gibt es nicht", isFolder: false, CancellationToken.None);
        Assert.Empty(await S3StorageTests.ListAsync(storage, ""));
    }

    [Fact]
    public async Task A_missing_root_is_reported_and_a_wrong_password_refused()
    {
        var mega = FakeMega.Create();
        await using var storage = new MegaStorage(Context(), (IMegaApiClient)mega);
        var missing = await Assert.ThrowsAsync<StorageException>(() => storage.ConnectAsync(createRoot: false, CancellationToken.None));
        Assert.Equal(StorageErrorKind.NotFound, missing.Kind);

        var refusing = FakeMega.Create();
        ((FakeMega)refusing).Password = "anders";
        await using var refused = new MegaStorage(Context(), (IMegaApiClient)refusing);
        var error = await Assert.ThrowsAsync<StorageException>(() => refused.ConnectAsync(createRoot: false, CancellationToken.None));
        Assert.Equal(StorageErrorKind.Authentication, error.Kind);
        Assert.DoesNotContain("geheim", error.Message);
    }

    [Theory]
    [InlineData(ApiResultCode.ResourceNotExists, StorageErrorKind.NotFound)]
    [InlineData(ApiResultCode.AccessDenied, StorageErrorKind.AccessDenied)]
    [InlineData(ApiResultCode.BadSessionId, StorageErrorKind.Authentication)]
    [InlineData(ApiResultCode.QuotaExceeded, StorageErrorKind.QuotaExceeded)]
    [InlineData(ApiResultCode.TooManyRequests, StorageErrorKind.RateLimited)]
    [InlineData(ApiResultCode.RequestFailedRetry, StorageErrorKind.Transient)]
    [InlineData(ApiResultCode.TwoFactorAuthenticationError, StorageErrorKind.Authentication)]
    public void Errors_of_megaapiclient_are_mapped(ApiResultCode code, StorageErrorKind kind)
    {
        var exception = (ApiException)Activator.CreateInstance(typeof(ApiException), BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, null, [code], null)!;

        Assert.Equal(kind, MegaStorage.Map(exception, "a.txt").Kind);
    }

    [Fact]
    public void The_provider_is_found()
    {
        Assert.IsType<MegaProvider>(StorageProviders.Find(ConnectionKind.Mega));
    }

    private static StorageContext Context() =>
        S3StorageTests.Context(ConnectionKind.Mega, ("user", "anna@example.com"), ("base", "Backup")) with { Secret = "geheim", Folder = "Fotos" };
}

internal sealed class FakeNode : INode
{
    public required string Id { get; init; }

    public NodeType Type { get; init; }

    public string Name { get; init; } = "";

    public long Size { get; init; }

    public DateTime? ModificationDate { get; init; }

    public string Fingerprint { get; init; } = "";

    public string? ParentId { get; init; }

    public DateTime? CreationDate { get; init; }

    public string Owner { get; init; } = "me";

    public IFileAttribute[] FileAttributes { get; init; } = [];

    public byte[] Data { get; init; } = [];

    public static FakeNode Folder(string id, string? parent, string name, NodeType type = NodeType.Directory) =>
        new() { Id = id, ParentId = parent, Name = name, Type = type, CreationDate = DateTime.UnixEpoch };

    public static FakeNode File(string id, string parent, string name, long size, DateTime modified) =>
        new() { Id = id, ParentId = parent, Name = name, Type = NodeType.File, Size = size, ModificationDate = modified, CreationDate = modified };

    public bool Equals(INode? other) => other?.Id == Id;
}

/// <summary>An in-memory MegaApiClient (the calls MegaStorage makes), as a DispatchProxy of <see cref="IMegaApiClient"/>.</summary>
public class FakeMega : DispatchProxy
{
    private int _ids;

    internal ConcurrentDictionary<string, FakeNode> Nodes { get; } = new();

    internal string Password { get; set; } = "geheim";

    internal bool LoggedIn { get; private set; }

    internal static object Create()
    {
        var proxy = DispatchProxy.Create<IMegaApiClient, FakeMega>();
        var fake = (FakeMega)(object)proxy;
        fake.Nodes["root"] = FakeNode.Folder("root", null, "Cloud Drive", NodeType.Root);
        fake.Nodes["trash"] = FakeNode.Folder("trash", null, "Rubbish Bin", NodeType.Trash);
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var arguments = args ?? [];
        switch (targetMethod!.Name)
        {
            case "get_IsLoggedIn":
                return LoggedIn;
            case "LoginAsync" when arguments.Length >= 2 && arguments[0] is string:
                if ((string)arguments[1]! != Password)
                {
                    return Task.FromException<MegaApiClient.LogonSessionToken>(NewApiException(ApiResultCode.ResourceNotExists));
                }

                LoggedIn = true;
                return Task.FromResult<MegaApiClient.LogonSessionToken>(null!);
            case "LogoutAsync":
                LoggedIn = false;
                return Task.CompletedTask;
            case "GetNodesAsync" when arguments.Length == 0:
                return Task.FromResult<IEnumerable<INode>>([.. Nodes.Values]);
            case "CreateFolderAsync":
                return Task.FromResult<INode>(Store(new FakeNode { Id = NextId(), ParentId = ((INode)arguments[1]!).Id, Name = (string)arguments[0]!, Type = NodeType.Directory, CreationDate = DateTime.UtcNow }));
            case "UploadAsync":
                return UploadAsync((Stream)arguments[0]!, (string)arguments[1]!, (INode)arguments[2]!, (DateTime?)arguments[4]);
            case "DownloadAsync" when arguments[0] is INode node:
                return Task.FromResult<Stream>(new MemoryStream(Nodes[node.Id].Data));
            case "DeleteAsync":
                Delete(((INode)arguments[0]!).Id);
                return Task.CompletedTask;
            case "MoveAsync":
                return Task.FromResult<INode>(Store(Copy((INode)arguments[0]!) with { ParentId = ((INode)arguments[1]!).Id }));
            case "RenameAsync":
                return Task.FromResult<INode>(Store(Copy((INode)arguments[0]!) with { Name = (string)arguments[1]! }));
            default:
                throw new NotSupportedException($"FakeMega: {targetMethod.Name}");
        }
    }

    private async Task<INode> UploadAsync(Stream stream, string name, INode parent, DateTime? modified)
    {
        // MegaApiClient reads the stream and asks for its length
        Assert.True(stream.Length >= 0);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        Assert.Equal(stream.Length, copy.Length);
        var local = modified?.ToLocalTime(); // MegaApiClient gives times back as local times
        return Store(new FakeNode { Id = NextId(), ParentId = parent.Id, Name = name, Type = NodeType.File, Size = copy.Length, Data = copy.ToArray(), ModificationDate = local, CreationDate = DateTime.Now });
    }

    private void Delete(string id)
    {
        Nodes.TryRemove(id, out _);
        foreach (var child in Nodes.Values.Where(node => node.ParentId == id).ToList())
        {
            Delete(child.Id);
        }
    }

    private FakeNodeRecord Copy(INode node) => new(Nodes[node.Id]);

    private FakeNode Store(FakeNodeRecord record) => Store(record.ToNode());

    private FakeNode Store(FakeNode node)
    {
        Nodes[node.Id] = node;
        return node;
    }

    private string NextId() => $"n{Interlocked.Increment(ref _ids)}";

    private static ApiException NewApiException(ApiResultCode code) =>
        (ApiException)Activator.CreateInstance(typeof(ApiException), BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, null, [code], null)!;

    private sealed record FakeNodeRecord(string Id, string? ParentId, string Name, FakeNode Original)
    {
        public FakeNodeRecord(FakeNode node) : this(node.Id, node.ParentId, node.Name, node)
        {
        }

        public FakeNode ToNode() => new()
        {
            Id = Id,
            ParentId = ParentId,
            Name = Name,
            Type = Original.Type,
            Size = Original.Size,
            Data = Original.Data,
            ModificationDate = Original.ModificationDate,
            CreationDate = Original.CreationDate,
        };
    }
}
