using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;

namespace ThixSync.Tests;

/// <summary>
/// The sync with MatCloud's general sync against <see cref="FakeCloud"/>: two computers (two data folders and computer names) that keep
/// their connections and jobs the same – new, renamed, deleted – the secret of a connection that never leaves its computer, and the
/// sign-in with a password and in the browser.
/// </summary>
public sealed class SyncTests : IDisposable
{
    private const string Server = "https://cloud.example.test";

    private readonly TestData _data = new();
    private readonly FakeCloud _cloud = new();
    private readonly Device _anna;
    private readonly Device _laptop;

    public SyncTests()
    {
        CloudClient.HandlerForTests = () => _cloud;
        _anna = new Device(Path.Combine(_data.Folder, "anna-pc"), "ANNA-PC");
        _laptop = new Device(Path.Combine(_data.Folder, "laptop"), "LAPTOP");
    }

    public void Dispose()
    {
        AppInfo.Pin(null);
        _data.Dispose();
    }

    [Fact]
    public async Task Two_computers_share_connections_and_jobs_but_never_a_secret()
    {
        _anna.Use();
        var folder = _data.MakeTree("dokumente", new Dictionary<string, string> { ["brief.txt"] = "Hallo" });
        var thisComputer = ConnectionService.EnsureThisComputer();
        var nas = ConnectionService.Save(new Connection
        {
            Name = "NAS",
            Kind = ConnectionKind.Sftp,
            Settings = new Dictionary<string, string> { [ConnectionFields.Host] = "nas.local", [ConnectionFields.UserName] = "anna" },
        }, "streng-geheim");
        var job = JobService.Save(new SyncJob
        {
            Name = "Dokumente",
            Mode = SyncMode.Backup,
            SourceConnectionId = thisComputer.Id,
            TargetConnectionId = nas.Id,
            Pairs = [new FolderPair { SourcePath = folder, TargetPath = "backup/dokumente" }],
            Schedule = new JobSchedule { Kind = ScheduleKind.Daily, TimeOfDay = new TimeOnly(20, 0) },
        });
        await _anna.ConnectAsync();
        await _anna.SyncAsync();

        Assert.Equal(2, _cloud.Live("connections").Count);
        Assert.Single(_cloud.Live("jobs"));
        Assert.DoesNotContain(_cloud.Items.Values, item => item.Data?.Contains("streng-geheim", StringComparison.Ordinal) == true);

        await _laptop.ConnectAsync();
        await _laptop.SyncAsync();
        var copy = JobService.Get(job.Id);
        Assert.Equal(("Dokumente", SyncMode.Backup, "ANNA-PC"), (copy.Name, copy.Mode, copy.RunsOnDevice));
        Assert.Equal(folder, Assert.Single(copy.Pairs).SourcePath);
        Assert.False(JobService.RunsHere(copy), "the job runs by itself on Anna's computer only");
        var remoteNas = ConnectionService.Get(nas.Id);
        Assert.False(remoteNas.HasSecret, "the password stays on Anna's computer");
        Assert.True(ConnectionService.NeedsSecret(remoteNas));
        Assert.False(ConnectionService.IsUsableHere(ConnectionService.Get(thisComputer.Id), out _), "Anna's folders are reachable on her computer only");

        // Renamed on the laptop: arrives on Anna's computer, which keeps its password.
        ConnectionService.Save(remoteNas with { Name = "NAS im Keller" }, null);
        await _laptop.SyncAsync();
        _anna.Use();
        await _anna.SyncAsync();
        Assert.Equal("NAS im Keller", ConnectionService.Get(nas.Id).Name);
        Assert.True(ConnectionService.Get(nas.Id).HasSecret, "a change from elsewhere keeps the secret of this computer");

        // A job deleted on Anna's computer goes on the laptop too.
        JobService.Delete(job.Id);
        await _anna.SyncAsync();
        Assert.Empty(_cloud.Live("jobs"));
        _laptop.Use();
        await _laptop.SyncAsync();
        Assert.Null(JobService.Find(job.Id));
    }

    [Fact]
    public void Connections_and_jobs_are_checked_in_the_business_layer()
    {
        _anna.Use();
        var error = Assert.Throws<BusinessException>(() => ConnectionService.Save(new Connection { Name = "S3", Kind = ConnectionKind.S3 }, "x"));
        Assert.Equal(ConnectionFields.Bucket, error.Field);
        var local = ConnectionService.EnsureThisComputer();
        var overlap = Assert.Throws<BusinessException>(() => JobService.Save(new SyncJob
        {
            Name = "Kreis",
            SourceConnectionId = local.Id,
            TargetConnectionId = local.Id,
            Pairs = [new FolderPair { SourcePath = _data.Folder, TargetPath = Path.Combine(_data.Folder, "innen") }],
        }));
        Assert.Equal(nameof(SyncJob.Pairs), overlap.Field);
        AccessPolicy.UsePolicyReaderForTests(name => name == "DisableCloudStorage");
        Assert.Throws<BusinessException>(() => ConnectionService.Save(new Connection
        {
            Name = "Drive",
            Kind = ConnectionKind.GoogleDrive,
        }, null));
        AccessPolicy.UsePolicyReaderForTests(name => name == "LockConfiguration");
        Assert.Throws<BusinessException>(() => ConnectionService.Delete(local.Id));
    }

    [Fact]
    public async Task Signing_in_in_the_browser_waits_for_the_code_to_be_confirmed()
    {
        _anna.Use();
        var codes = new List<string>();
        var opened = new List<string>();
        var task = CloudSyncService.ConnectInBrowserAsync(Server, "ANNA-PC", start => codes.Add(start.Code), opened.Add);
        while (codes.Count == 0)
        {
            await Task.Delay(10);
        }

        _cloud.Approve(codes[0]);
        Assert.True(await task);
        Assert.True(SettingsService.Get().Cloud.IsConnected);
        Assert.Single(opened);
        await CloudSyncService.DisconnectAsync();
        Assert.False(SettingsService.Get().Cloud.IsConnected);
    }

    [Fact]
    public async Task A_wrong_password_shows_at_its_field()
    {
        _anna.Use();
        var error = await Assert.ThrowsAsync<BusinessException>(() => CloudSyncService.ConnectAsync(Server, FakeCloud.UserName, "falsch", "ANNA-PC"));
        Assert.Equal("Password", error.Field);
        AccessPolicy.UsePolicyReaderForTests(name => name == "DisableCloud");
        await Assert.ThrowsAsync<BusinessException>(() => CloudSyncService.ConnectAsync(Server, FakeCloud.UserName, FakeCloud.Password, "ANNA-PC"));
    }

    /// <summary>One computer: its own data folder and name. <see cref="Use"/> switches the process over to it.</summary>
    private sealed class Device(string folder, string name)
    {
        public void Use()
        {
            CloudSyncService.Stop();
            SyncStore.Close();
            AppPaths.UseDirectoryForTests(folder);
            Directory.CreateDirectory(folder);
            SettingsStore.Reset();
            ConnectionService.ResetForTests();
            JobService.ResetForTests();
            AppInfo.Pin("1.0.0", "release", 1, name);
        }

        public async Task ConnectAsync()
        {
            Use();
            await CloudSyncService.ConnectAsync(Server, FakeCloud.UserName, FakeCloud.Password, name);
            CloudSyncService.Stop(); // the tests sync step by step
        }

        public async Task SyncAsync()
        {
            Use();
            await CloudSyncService.SyncOnceAsync();
            ConnectionService.ResetForTests();
            JobService.ResetForTests();
        }
    }
}
