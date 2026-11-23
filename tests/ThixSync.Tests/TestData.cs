using MatCloud.Contract;
using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Platform;
using ThixSync.Core.Storage;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ThixSync.Tests;

/// <summary>
/// Every test gets its own data folder, no connections or jobs, a token store in memory and no machine policies. Nothing a test does
/// touches the real data, settings, Keychain or registry, and no file is opened, shown or moved to the trash by the system.
/// </summary>
internal sealed class TestData : IDisposable
{
    public TestData()
    {
        Folder = Path.Combine(Path.GetTempPath(), "thixsync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        SyncStore.Close();
        AppPaths.UseDirectoryForTests(Folder);
        SettingsStore.Reset();
        TokenStore.UseMemoryForTests(true);
        CloudHandoff.Folder = Path.Combine(Folder, "handoff"); // never the sign-in a real ThixLauncher left
        AccessPolicy.UsePolicyReaderForTests(_ => false);
        CloudClient.HandlerForTests = null;
        FileSystemActions.ForTests = (action, path) => Actions.Add((action, path));
        CloudSyncService.ResetForTests();
        ConnectionService.ResetForTests();
        JobService.ResetForTests();
        StorageProviders.OverrideForTests(null, null);
    }

    public string Folder { get; }

    /// <summary>What the system would have done (open, reveal, trash) with which path.</summary>
    public List<(string Action, string Path)> Actions { get; } = [];

    /// <summary>A folder with files: <c>name → content</c> with '/' in the names.</summary>
    public string MakeTree(string name, IReadOnlyDictionary<string, string> files)
    {
        var root = Path.Combine(Folder, name);
        Directory.CreateDirectory(root);
        foreach (var (file, content) in files)
        {
            var path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        return root;
    }

    public void Dispose()
    {
        CloudSyncService.Stop();
        SyncStore.Close();
        CloudClient.HandlerForTests = null;
        FileSystemActions.ForTests = null;
        AccessPolicy.UsePolicyReaderForTests(null);
        TokenStore.UseMemoryForTests(false);
        CloudHandoff.Folder = CloudHandoff.DefaultFolder();
        SettingsStore.Reset();
        AppPaths.UseDirectoryForTests(null);
        ConnectionService.ResetForTests();
        JobService.ResetForTests();
        StorageProviders.OverrideForTests(null, null);
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
