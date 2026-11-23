using Microsoft.Data.Sqlite;
using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;

namespace ThixSync.Tests;

/// <summary>Another process (the CLI) changes the database: the app's lists see it and the watcher tells about it.</summary>
public sealed class StoreWatcherTests : IDisposable
{
    private readonly TestData _data = new();

    public void Dispose()
    {
        StoreWatcher.Stop();
        _data.Dispose();
    }

    [Fact]
    public void A_change_of_another_process_reaches_the_cached_lists_and_raises_changed()
    {
        var local = ConnectionService.EnsureThisComputer();
        var job = JobService.Save(new SyncJob
        {
            Name = "Dokumente",
            SourceConnectionId = local.Id,
            TargetConnectionId = local.Id,
            Pairs = [new FolderPair { SourcePath = Path.Combine(_data.Folder, "a"), TargetPath = Path.Combine(_data.Folder, "b") }],
        });
        Assert.False(JobService.Get(job.Id).IsPaused);
        StoreWatcher.Check(); // remembers the current version

        var raised = 0;
        void OnChanged(object? sender, EventArgs e) => raised++;
        JobService.Changed += OnChanged;
        try
        {
            using (var other = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = AppPaths.DatabaseFile, Pooling = false }.ToString()))
            {
                other.Open();
                using var command = other.CreateCommand();
                command.CommandText = "UPDATE Job SET IsPaused = 1";
                command.ExecuteNonQuery();
            }

            Assert.True(JobService.Get(job.Id).IsPaused, "the cache is dropped when another connection wrote");
            StoreWatcher.Check();
            Assert.Equal(1, raised);
            StoreWatcher.Check();
            Assert.Equal(1, raised);
        }
        finally
        {
            JobService.Changed -= OnChanged;
        }
    }
}
