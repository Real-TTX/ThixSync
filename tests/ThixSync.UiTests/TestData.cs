using MatCloud.Contract;
using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Storage;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ThixSync.UiTests;

/// <summary>
/// Every test gets its own data folder, no connections or jobs, a token store in memory, no machine policies and nothing running. Nothing a
/// test does touches the real data, settings, Keychain or registry, and no browser or folder is opened by the system.
/// </summary>
internal sealed class TestData : IDisposable
{
    public TestData()
    {
        Folder = Path.Combine(Path.GetTempPath(), "thixsync-uitests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        Database.Close();
        AppPaths.UseDirectoryForTests(Folder);
        SettingsStore.Reset();
        TokenStore.UseMemoryForTests(true);
        CloudHandoff.Folder = Path.Combine(Folder, "handoff");
        AccessPolicy.UsePolicyReaderForTests(_ => false);
        AutostartService.UseInMemoryEntry(false);
        CloudClient.HandlerForTests = null;
        CloudSyncService.ResetForTests();
        ConnectionService.ResetForTests();
        JobService.ResetForTests();
        StorageProviders.OverrideForTests(null, null);
        RunMonitor.Clear();
        Ui.OpenExternalForTests = Opened.Add;
    }

    public string Folder { get; }

    /// <summary>What would have been opened in the browser or file manager.</summary>
    public List<string> Opened { get; } = [];

    public void Dispose()
    {
        RunMonitor.Clear();
        Ui.OpenExternalForTests = null;
        CloudSyncService.Stop();
        Database.Close();
        CloudClient.HandlerForTests = null;
        AccessPolicy.UsePolicyReaderForTests(null);
        AutostartService.UseInMemoryEntry(null);
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

/// <summary>Where the sources are, for the tests that read them: the folder of ThixSync.slnx above the test run.</summary>
internal static class Repository
{
    public static string Root { get; } = Find();

    private static string Find()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "ThixSync.slnx")))
            {
                return folder.FullName;
            }
        }

        throw new DirectoryNotFoundException("ThixSync.slnx was not found above " + AppContext.BaseDirectory);
    }
}
