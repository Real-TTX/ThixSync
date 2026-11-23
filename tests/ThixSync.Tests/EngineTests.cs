using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;

namespace ThixSync.Tests;

/// <summary>
/// The engine on in-memory storages: two-way, mirror and backup, deletions, conflicts with every policy, moves, filters, the safety
/// stop, storages without modification times or with coarse times, case collisions, unreadable folders, retries, failures that stop
/// a run, cancelling and resuming, the kept plan and its resolutions.
/// </summary>
public sealed class EngineTests : EngineTestBase
{
    [Fact]
    public async Task The_first_two_way_run_merges_both_sides_and_the_next_has_nothing_to_do()
    {
        var job = MemoryJob(SyncMode.TwoWay);
        var source = MemoryProvider.Drives["quelle"];
        var target = MemoryProvider.Drives["ziel"];
        source.Put("a.txt", "nur in der Quelle", At(1));
        source.Put("gleich.txt", "gleich", At(2));
        target.Put("b.txt", "nur im Ziel", At(3));
        target.Put("gleich.txt", "gleich", At(2));

        var run = await RunAsync(job);

        Assert.Equal(RunOutcome.Success, run.Outcome);
        Assert.Equal(2, run.FilesCopied);
        Assert.Equal(["a.txt", "b.txt", "gleich.txt"], source.Files);
        Assert.Equal(["a.txt", "b.txt", "gleich.txt"], target.Files);
        Assert.Equal("nur im Ziel", source.Read("b.txt"));
        Assert.Equal(1, target.Count("written"));
        Assert.Equal(At(1), target.Get("a.txt")!.Modified);

        source.ClearOperations();
        target.ClearOperations();
        var again = await RunAsync(job, new RunRequest { AnalyseOnly = true });
        Assert.Equal(RunOutcome.Analysed, again.Outcome);
        Assert.True(JobRunner.LastPlan(job.Id)!.IsInStep);
        var third = await RunAsync(job);
        Assert.Equal((RunOutcome.Success, 0L), (third.Outcome, third.FilesCopied));
        Assert.Equal(0, source.Count("written") + target.Count("written"));
    }

    [Fact]
    public async Task Changes_go_in_both_directions()
    {
        var job = MemoryJob(SyncMode.TwoWay);
        var source = MemoryProvider.Drives["quelle"];
        var target = MemoryProvider.Drives["ziel"];
        source.Put("a.txt", "eins", At(1));
        target.Put("b.txt", "zwei", At(2));
        await RunAsync(job);

        source.Put("a.txt", "eins geändert", At(10));
        var forward = await RunAsync(job);
        Assert.Equal("eins geändert", target.Read("a.txt"));
        Assert.Equal(1, forward.FilesCopied);

        target.Put("b.txt", "zwei geändert", At(11));
        var back = await RunAsync(job);
        Assert.Equal("zwei geändert", source.Read("b.txt"));
        Assert.Equal(1, back.FilesCopied);
        Assert.Contains(Entries(back), entry => entry.Action == PlanAction.CopyToSource && entry.Path == "b.txt");
    }

    [Fact]
    public async Task Deletions_are_carried_over_and_restored_when_switched_off()
    {
        var job = MemoryJob(SyncMode.TwoWay);
        var source = MemoryProvider.Drives["quelle"];
        var target = MemoryProvider.Drives["ziel"];
        source.Put("a.txt", "a", At(1));
        source.Put("b.txt", "b", At(2));
        await RunAsync(job);

        source.Remove("a.txt");
        var deleted = await RunAsync(job);
        Assert.Equal(1, deleted.FilesDeleted);
        Assert.False(target.Exists("a.txt"));

        job = Change(job, value => value with { Options = value.Options with { PropagateDeletes = false } });
        target.Remove("b.txt");
        var restored = await RunAsync(job);
        Assert.Equal(0, restored.FilesDeleted);
        Assert.True(source.Exists("b.txt"));
        Assert.Equal("b", target.Read("b.txt"));
    }

    [Fact]
    public async Task A_file_changed_on_one_side_and_deleted_on_the_other_is_kept()
    {
        var job = MemoryJob(SyncMode.TwoWay);
        var source = MemoryProvider.Drives["quelle"];
        var target = MemoryProvider.Drives["ziel"];
        source.Put("a.txt", "alt", At(1));
        await RunAsync(job);

        source.Put("a.txt", "neu", At(5));
        target.Remove("a.txt");
        var run = await RunAsync(job);

        Assert.Equal(0, run.FilesDeleted);
        Assert.Equal("neu", target.Read("a.txt"));
        Assert.Equal("neu", source.Read("a.txt"));
    }

    [Theory]
    [InlineData(ConflictPolicy.NewerWins, "Ziel neu", "Ziel neu")]
    [InlineData(ConflictPolicy.SourceWins, "Quelle neu", "Quelle neu")]
    [InlineData(ConflictPolicy.TargetWins, "Ziel neu", "Ziel neu")]
    public async Task Conflicts_follow_the_policy(ConflictPolicy policy, string expectedSource, string expectedTarget)
    {
        var job = MemoryJob(SyncMode.TwoWay, options => options with { Conflicts = policy });
        var source = MemoryProvider.Drives["quelle"];
        var target = MemoryProvider.Drives["ziel"];
        source.Put("f.txt", "alt", At(1));
        await RunAsync(job);

        source.Put("f.txt", "Quelle neu", At(10));
        target.Put("f.txt", "Ziel neu", At(20));
        var run = await RunAsync(job);

        Assert.Equal(RunOutcome.Success, run.Outcome);
        Assert.Equal(1, run.Conflicts);
        Assert.Equal(expectedSource, source.Read("f.txt"));
        Assert.Equal(expectedTarget, target.Read("f.txt"));
        Assert.True((await RunAsync(job)).FilesCopied == 0, "the conflict is settled");
    }

    [Fact]
    public async Task Keep_both_renames_the_target_version_on_both_sides()
    {
        AppClock.Pin(new DateTimeOffset(2026, 11, 23, 10, 24, 0, TimeSpan.Zero));
        var job = MemoryJob(SyncMode.TwoWay, options => options with { Conflicts = ConflictPolicy.KeepBoth });
        var source = MemoryProvider.Drives["quelle"];
        var target = MemoryProvider.Drives["ziel"];
        source.Put("Ordner/Bericht.docx", "alt", At(1));
        await RunAsync(job);

        source.Put("Ordner/Bericht.docx", "Quelle neu", At(10));
        target.Put("Ordner/Bericht.docx", "Ziel neu", At(20));
        var run = await RunAsync(job);

        var stamp = AppClock.UtcNow.ToLocalTime().ToString("yyyy-MM-dd HH-mm", System.Globalization.CultureInfo.InvariantCulture);
        var conflictName = $"Ordner/Bericht (Konflikt {AppInfo.MachineName} {stamp}).docx";
        Assert.Equal(RunOutcome.Success, run.Outcome);
        Assert.Equal("Quelle neu", source.Read("Ordner/Bericht.docx"));
        Assert.Equal("Quelle neu", target.Read("Ordner/Bericht.docx"));
        Assert.Equal("Ziel neu", source.Read(conflictName));
        Assert.Equal("Ziel neu", target.Read(conflictName));
        Assert.Equal(0, (await RunAsync(job)).FilesCopied);
    }

    [Fact]
    public async Task Ask_leaves_the_conflict_open_until_the_user_resolves_it()
    {
        var job = MemoryJob(SyncMode.TwoWay, options => options with { Conflicts = ConflictPolicy.Ask });
        var source = MemoryProvider.Drives["quelle"];
        var target = MemoryProvider.Drives["ziel"];
        source.Put("f.txt", "alt", At(1));
        await RunAsync(job);
        source.Put("f.txt", "Quelle neu", At(10));
        target.Put("f.txt", "Ziel neu", At(20));

        var analysis = await RunAsync(job, new RunRequest { AnalyseOnly = true });
        Assert.Equal(RunOutcome.Analysed, analysis.Outcome);
        Assert.Equal(1, JobRunner.LastPlan(job.Id)!.OpenConflicts);

        var run = await RunAsync(job);
        Assert.Equal(RunOutcome.Warnings, run.Outcome);
        Assert.Equal("Quelle neu", source.Read("f.txt"));
        Assert.Equal("Ziel neu", target.Read("f.txt"));
        var conflict = Assert.Single(JobRunner.LastPlan(job.Id)!.Items, item => item.Action == PlanAction.Conflict);

        JobRunner.Resolve(job.Id, conflict.PairIndex, conflict.Path, ConflictChoice.KeepTarget);
        Assert.Equal(0, JobRunner.LastPlan(job.Id)!.OpenConflicts);
        var resolved = await RunAsync(job, new RunRequest { UseLastPlan = true });

        Assert.Equal(RunOutcome.Success, resolved.Outcome);
        Assert.Equal("Ziel neu", source.Read("f.txt"));
        Assert.Null(JobRunner.LastPlan(job.Id));
    }

    [Fact]
    public async Task A_changed_job_drops_its_kept_plan()
    {
        var job = MemoryJob(SyncMode.TwoWay);
        MemoryProvider.Drives["quelle"].Put("a.txt", "a", At(1));
        await RunAsync(job, new RunRequest { AnalyseOnly = true });
        Assert.NotNull(JobRunner.LastPlan(job.Id));

        Change(job, value => value with { Description = "geändert" });

        Assert.Null(JobRunner.LastPlan(job.Id));
        await Assert.ThrowsAsync<BusinessException>(() => RunAsync(job, new RunRequest { UseLastPlan = true }));
    }

    [Fact]
    public async Task Mirror_removes_what_the_source_does_not_have_and_keeps_versions()
    {
        AppClock.Pin(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero));
        var job = MemoryJob(SyncMode.Mirror, options => options with { KeepVersions = true });
        var source = MemoryProvider.Drives["quelle"];
        var target = MemoryProvider.Drives["ziel"];
        source.Put("a.txt", "a", At(1));
        target.Put("a.txt", "im Ziel geändert", At(5));
        target.Put("extra.txt", "extra", At(2));
        target.Put("alt/x.txt", "x", At(2));
        target.Put("alt/y.txt", "y", At(2));

        var run = await RunAsync(job);

        Assert.Equal(RunOutcome.Success, run.Outcome);
        Assert.Equal("a", target.Read("a.txt"));
        Assert.False(target.Exists("extra.txt"));
        Assert.False(target.Exists("alt/x.txt"));
        var stamp = AppClock.UtcNow.ToLocalTime().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("extra", target.Read($".thixsync/versions/{stamp}/extra.txt"));
        Assert.Equal("im Ziel geändert", target.Read($".thixsync/versions/{stamp}/a.txt"));
        Assert.Equal("x", target.Read($".thixsync/versions/{stamp}/alt/x.txt"));
        Assert.Equal(3, run.FilesDeleted); // extra.txt and the two files of "alt" (a.txt was replaced, not deleted)
        Assert.Equal(["a.txt"], source.Files);
    }

    [Fact]
    public async Task Backup_never_deletes_and_keeps_only_the_newest_versions()
    {
        var job = MemoryJob(SyncMode.Backup, options => options with { KeepVersions = true, KeepVersionsCount = 2, KeepVersionsDays = 0 });
        var source = MemoryProvider.Drives["quelle"];
        var target = MemoryProvider.Drives["ziel"];
        source.Put("doc.txt", "v1", At(1));
        AppClock.Pin(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero));
        await RunAsync(job);

        for (var version = 2; version <= 5; version++)
        {
            source.Put("doc.txt", $"v{version}", At(version * 10));
            AppClock.Pin(new DateTimeOffset(2026, 11, 23, 9, version, 0, TimeSpan.Zero));
            var run = await RunAsync(job);
            Assert.Equal(RunOutcome.Success, run.Outcome);
        }

        Assert.Equal("v5", target.Read("doc.txt"));
        var versions = target.Files.Where(path => path.StartsWith(".thixsync/versions/", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, versions.Count);
        Assert.Equal(["v3", "v4"], versions.Select(path => target.Read(path)).Order());

        source.Remove("doc.txt");
        var afterDelete = await RunAsync(job);
        Assert.Equal(0, afterDelete.FilesDeleted);
        Assert.Equal("v5", target.Read("doc.txt"));
    }

    [Fact]
    public async Task A_renamed_file_is_moved_instead_of_copied_again()
    {
        var job = MemoryJob(SyncMode.TwoWay);
        var source = MemoryProvider.Drives["quelle"];
        var target = MemoryProvider.Drives["ziel"];
        source.PutBytes("alt/bild.jpg", new byte[5000], At(1));
        source.Put("andere.txt", "x", At(1));
        await RunAsync(job);

        source.Rename("alt/bild.jpg", "neu/bild.jpg");
        var run = await RunAsync(job);

        Assert.Equal(RunOutcome.Success, run.Outcome);
        Assert.Equal((1L, 0L), (run.FilesMoved, run.FilesCopied));
        Assert.True(target.Exists("neu/bild.jpg"));
        Assert.False(target.Exists("alt/bild.jpg"));
        Assert.Equal(0, (await RunAsync(job)).FilesCopied + (await RunAsync(job)).FilesMoved);
    }

    [Fact]
    public async Task The_filter_leaves_out_patterns_folders_hidden_and_large_files_on_both_sides()
    {
        var filter = new JobFilter
        {
            Exclude = [.. JobFilter.DefaultExcludes, "node_modules/", "Archiv/Alt/"],
            SkipHidden = true,
            MaxFileSizeBytes = 100,
        };
        var job = MemoryJob(SyncMode.Mirror, filter: filter);
        var source = MemoryProvider.Drives["quelle"];
        var target = MemoryProvider.Drives["ziel"];
        foreach (var path in new[] { "keep.txt", "a.tmp", "node_modules/x.js", "src/node_modules/y.js", "src/main.cs", ".hidden.txt",
                     "Fotos/2024/a.jpg", "Fotos/2024/b.png", "Archiv/Alt/z.txt", "Archiv/neu.txt", "~$Bericht.docx" })
        {
            source.Put(path, "inhalt", At(1));
        }

        source.PutBytes("big.bin", new byte[200], At(1));
        target.Put("a.tmp", "bleibt im Ziel", At(1));
        target.PutBytes("big.bin", new byte[300], At(1));

        var run = await RunAsync(job);

        Assert.Equal(RunOutcome.Success, run.Outcome);
        Assert.Equal(["Archiv/neu.txt", "Fotos/2024/a.jpg", "Fotos/2024/b.png", "a.tmp", "big.bin", "keep.txt", "src/main.cs"], target.Files);
        Assert.Equal("bleibt im Ziel", target.Read("a.tmp"));
        Assert.Equal(300, target.Get("big.bin")!.Content.Length);
    }

    [Fact]
    public async Task An_include_filter_takes_only_matching_files()
    {
        var job = MemoryJob(SyncMode.Mirror, filter: new JobFilter { Include = ["Fotos/**/*.jpg", "Wichtig/"] });
        var source = MemoryProvider.Drives["quelle"];
        foreach (var path in new[] { "Fotos/2024/a.jpg", "Fotos/2024/Urlaub/b.jpg", "Fotos/2024/c.png", "d.jpg", "Wichtig/brief.txt", "Wichtig/x/y.pdf" })
        {
            source.Put(path, "inhalt", At(1));
        }

        await RunAsync(job);

        Assert.Equal(["Fotos/2024/Urlaub/b.jpg", "Fotos/2024/a.jpg", "Wichtig/brief.txt", "Wichtig/x/y.pdf"], MemoryProvider.Drives["ziel"].Files);
    }

    [Fact]
    public async Task Too_many_deletions_stop_the_run_until_confirmed()
    {
        var job = MemoryJob(SyncMode.Mirror);
        var source = MemoryProvider.Drives["quelle"];
        var target = MemoryProvider.Drives["ziel"];
        for (var index = 0; index < 20; index++)
        {
            source.Put($"datei{index:00}.txt", "x", At(1));
        }

        await RunAsync(job);
        for (var index = 0; index < 15; index++)
        {
            source.Remove($"datei{index:00}.txt");
        }

        var stopped = await RunAsync(job);
        Assert.Equal(RunOutcome.NeedsConfirmation, stopped.Outcome);
        Assert.Contains("15 Dateien", stopped.Summary, StringComparison.Ordinal);
        Assert.Equal(20, target.Files.Count);
        Assert.Equal(15, JobRunner.LastPlan(job.Id)!.Count(PlanAction.DeleteOnTarget));

        var confirmed = await RunAsync(job, new RunRequest { ConfirmDeletes = true });
        Assert.Equal((RunOutcome.Success, 15L), (confirmed.Outcome, confirmed.FilesDeleted));
        Assert.Equal(5, target.Files.Count);
    }

    [Fact]
    public async Task The_policy_DisableDelete_keeps_extra_files()
    {
        var job = MemoryJob(SyncMode.Mirror);
        AccessPolicy.UsePolicyReaderForTests(name => name == "DisableDelete");
        MemoryProvider.Drives["ziel"].Put("extra.txt", "x", At(1));

        var run = await RunAsync(job);

        Assert.Equal(0, run.FilesDeleted);
        Assert.True(MemoryProvider.Drives["ziel"].Exists("extra.txt"));
    }

    [Theory]
    [InlineData(SyncMode.Mirror)]
    [InlineData(SyncMode.TwoWay)]
    [InlineData(SyncMode.Backup)]
    public async Task A_target_without_modification_times_is_compared_with_itself(SyncMode mode)
    {
        Drive("ziel", StorageFeatures.Move | StorageFeatures.RealFolders);
        var job = MemoryJob(mode);
        var source = MemoryProvider.Drives["quelle"];
        var target = MemoryProvider.Drives["ziel"];
        source.Put("a.txt", "a", At(1));
        source.Put("ordner/b.txt", "b", At(2));

        var first = await RunAsync(job);
        Assert.Equal(2, first.FilesCopied);
        Assert.NotEqual(At(1), target.Get("a.txt")!.Modified);

        source.ClearOperations();
        target.ClearOperations();
        var second = await RunAsync(job);
        Assert.Equal((RunOutcome.Success, 0L), (second.Outcome, second.FilesCopied));
        Assert.Equal(0, source.Count("written") + target.Count("written"));

        source.Put("a.txt", "a2", At(30));
        var third = await RunAsync(job);
        Assert.Equal(1, third.FilesCopied);
        Assert.Equal("a2", target.Read("a.txt"));
    }

    [Fact]
    public async Task A_target_with_coarse_times_does_not_copy_again()
    {
        Drive("ziel", StorageFeatures.SetModifiedTime | StorageFeatures.CoarseTime | StorageFeatures.Move | StorageFeatures.RealFolders);
        var job = MemoryJob(SyncMode.TwoWay, options => options with { TimeToleranceSeconds = 0 });
        var source = MemoryProvider.Drives["quelle"];
        source.Put("a.txt", "a", At(1).AddMilliseconds(1500));

        await RunAsync(job);
        var second = await RunAsync(job);

        Assert.Equal(0, second.FilesCopied);
    }

    [Fact]
    public async Task Storages_with_hashes_compare_by_content()
    {
        Drive("quelle", hashKind: "md5");
        Drive("ziel", hashKind: "md5");
        var job = MemoryJob(SyncMode.TwoWay);
        var source = MemoryProvider.Drives["quelle"];
        var target = MemoryProvider.Drives["ziel"];
        source.Put("a.txt", "gleich", At(1));
        target.Put("a.txt", "gleich", At(50));

        var first = await RunAsync(job);
        Assert.Equal((RunOutcome.Success, 0L, 0L), (first.Outcome, first.FilesCopied, first.Conflicts));

        source.Put("a.txt", "gleich", At(60)); // touched only
        Assert.Equal(0, (await RunAsync(job)).FilesCopied);
    }

    [Fact]
    public async Task Names_that_differ_only_in_case_are_skipped_on_a_case_insensitive_pair()
    {
        Drive("quelle", StorageFeatures.SetModifiedTime | StorageFeatures.Move | StorageFeatures.RealFolders | StorageFeatures.CaseSensitive);
        var job = MemoryJob(SyncMode.Mirror);
        var source = MemoryProvider.Drives["quelle"];
        source.Put("Bericht.txt", "groß", At(1));
        source.Put("bericht.txt", "klein", At(2));
        source.Put("ok.txt", "ok", At(3));

        var run = await RunAsync(job);

        Assert.Equal(RunOutcome.Warnings, run.Outcome);
        Assert.Equal(["ok.txt"], MemoryProvider.Drives["ziel"].Files);
        Assert.Contains(Entries(run), entry => entry.Level == RunEntryLevel.Warning && entry.Message.Contains("Groß-/Kleinschreibung", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unreadable_folder_never_causes_deletions()
    {
        var job = MemoryJob(SyncMode.TwoWay);
        var source = MemoryProvider.Drives["quelle"];
        var target = MemoryProvider.Drives["ziel"];
        source.Put("docs/a.txt", "a", At(1));
        source.Put("docs/b.txt", "b", At(1));
        source.Put("top.txt", "t", At(1));
        await RunAsync(job);

        source.Unreadable["docs"] = true;
        var run = await RunAsync(job);

        Assert.Equal(RunOutcome.Warnings, run.Outcome);
        Assert.Equal(0, run.FilesDeleted);
        Assert.True(target.Exists("docs/a.txt"));
        Assert.True(target.Exists("docs/b.txt"));
        Assert.Contains(Entries(run), entry => entry.Level == RunEntryLevel.Warning && entry.Path == "docs");

        source.Unreadable.Clear();
        Assert.Equal(RunOutcome.Success, (await RunAsync(job)).Outcome);
    }

    [Fact]
    public async Task Passing_errors_are_retried_and_too_many_requests_slow_down()
    {
        var job = MemoryJob(SyncMode.Mirror);
        var source = MemoryProvider.Drives["quelle"];
        var target = MemoryProvider.Drives["ziel"];
        for (var index = 0; index < 10; index++)
        {
            source.Put($"f{index}.txt", "x", At(1));
        }

        target.Fail("write", "f1.txt", 2, () => new StorageException(StorageErrorKind.Locked, "Gesperrt."));
        target.Fail("write", "f2.txt", 1, () => new StorageException(StorageErrorKind.RateLimited, "Zu viele Anfragen.") { RetryAfter = TimeSpan.FromMilliseconds(50) });
        target.Fail("write", "f3.txt", 1, () => new IOException("Verbindung zurückgesetzt"));

        var run = await RunAsync(job);

        Assert.Equal((RunOutcome.Success, 10L, 0L), (run.Outcome, run.FilesCopied, run.Errors));
        Assert.Equal(10, target.Files.Count);
    }

    [Fact]
    public async Task A_file_that_cannot_be_written_is_skipped_and_tried_again_next_time()
    {
        var job = MemoryJob(SyncMode.Mirror);
        var source = MemoryProvider.Drives["quelle"];
        var target = MemoryProvider.Drives["ziel"];
        source.Put("a.txt", "a", At(1));
        source.Put("b.txt", "b", At(1));
        target.Fail("write", "b.txt", 1, () => new StorageException(StorageErrorKind.AccessDenied, "Kein Zugriff."));

        var run = await RunAsync(job);

        Assert.Equal((RunOutcome.Warnings, 1L, 1L), (run.Outcome, run.FilesCopied, run.Errors));
        Assert.Contains(Entries(run), entry => entry.Level == RunEntryLevel.Error && entry.Path == "b.txt");
        var next = await RunAsync(job);
        Assert.Equal((RunOutcome.Success, 1L), (next.Outcome, next.FilesCopied));
    }

    [Fact]
    public async Task A_refused_sign_in_stops_the_whole_run()
    {
        var job = MemoryJob(SyncMode.Mirror);
        var source = MemoryProvider.Drives["quelle"];
        for (var index = 0; index < 20; index++)
        {
            source.Put($"f{index:00}.txt", "x", At(1));
        }

        MemoryProvider.Drives["ziel"].Fail("write", "", 1, () => new StorageException(StorageErrorKind.Authentication, "Anmeldung abgelehnt."));

        var run = await RunAsync(job);

        Assert.Equal(RunOutcome.Failed, run.Outcome);
        Assert.StartsWith("Fehlgeschlagen: Anmeldung abgelehnt.", run.Summary, StringComparison.Ordinal);
        Assert.False(JobRunner.IsRunning(job.Id));
    }

    [Fact]
    public async Task A_side_that_is_not_there_fails_the_run()
    {
        var job = MemoryJob(SyncMode.Mirror);
        job = Change(job, value => value with { Pairs = [new FolderPair { SourcePath = "gibt-es-nicht", TargetPath = "" }] });

        var run = await RunAsync(job);

        Assert.Equal(RunOutcome.Failed, run.Outcome);
    }

    [Fact]
    public async Task A_cancelled_run_keeps_what_it_finished_and_the_next_run_goes_on()
    {
        var job = MemoryJob(SyncMode.Mirror, options => options with { Parallelism = 2 });
        var source = MemoryProvider.Drives["quelle"];
        var target = MemoryProvider.Drives["ziel"];
        for (var index = 0; index < 30; index++)
        {
            source.Put($"f{index:00}.txt", $"inhalt {index}", At(1));
        }

        using var cancellation = new CancellationTokenSource();
        var written = 0;
        target.WriteDelay = TimeSpan.FromMilliseconds(20);
        target.OnWrite = _ =>
        {
            if (Interlocked.Increment(ref written) == 6)
            {
                cancellation.Cancel();
            }
        };

        var cancelled = await RunAsync(job, cancellationToken: cancellation.Token);
        Assert.Equal(RunOutcome.Cancelled, cancelled.Outcome);
        var firstWrites = target.Count("written");
        Assert.InRange(firstWrites, 6, 29);
        Assert.Equal(firstWrites, cancelled.FilesCopied);

        target.WriteDelay = TimeSpan.Zero;
        target.OnWrite = null;
        target.ClearOperations();
        var resumed = await RunAsync(job);

        Assert.Equal(RunOutcome.Success, resumed.Outcome);
        Assert.Equal(30 - firstWrites, target.Count("written"));
        Assert.Equal(30, target.Files.Count);
    }

    [Fact]
    public async Task A_job_runs_once_at_a_time()
    {
        var job = MemoryJob(SyncMode.Mirror);
        var source = MemoryProvider.Drives["quelle"];
        source.Put("a.txt", "a", At(1));
        MemoryProvider.Drives["ziel"].WriteDelay = TimeSpan.FromMilliseconds(300);

        Assert.True(JobRunner.Start(job.Id, new RunRequest()));
        Assert.True(JobRunner.IsRunning(job.Id));
        Assert.False(JobRunner.Start(job.Id, new RunRequest()));
        await Assert.ThrowsAsync<BusinessException>(() => RunAsync(job));
        await WaitUntilAsync(() => !JobRunner.IsRunning(job.Id));
    }

    [Fact]
    public async Task A_job_another_process_runs_is_refused()
    {
        var job = MemoryJob(SyncMode.Mirror);
        using (Core.Business.Engine.JobLock.TryAcquire(job.Id))
        {
            var refused = await Assert.ThrowsAsync<BusinessException>(() => RunAsync(job));
            Assert.Contains("anderen", refused.Message, StringComparison.Ordinal);
            Assert.False(JobRunner.IsRunning(job.Id));
        }

        Assert.Equal(RunOutcome.Success, (await RunAsync(job)).Outcome);
    }

    [Fact]
    public async Task The_bandwidth_limit_slows_streamed_copies()
    {
        Core.Business.Engine.BandwidthThrottle.LimitForTests = () => 256 * 1024;
        var job = MemoryJob(SyncMode.Mirror);
        MemoryProvider.Drives["quelle"].PutBytes("gross.bin", new byte[256 * 1024], At(1));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var run = await RunAsync(job);

        Assert.Equal(RunOutcome.Success, run.Outcome);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(600), $"256 KB at 256 KB/s took only {watch.Elapsed}");
    }

    [Fact]
    public async Task Progress_moves_forward_with_speed_and_history()
    {
        var job = MemoryJob(SyncMode.Mirror, options => options with { Parallelism = 3 });
        var source = MemoryProvider.Drives["quelle"];
        for (var index = 0; index < 200; index++)
        {
            source.PutBytes($"f{index:000}.bin", new byte[2048], At(1));
        }

        MemoryProvider.Drives["ziel"].WriteDelay = TimeSpan.FromMilliseconds(30);
        var snapshots = new List<RunProgress>();
        void OnProgress(object? sender, RunProgress progress)
        {
            if (progress.JobId == job.Id)
            {
                lock (snapshots)
                {
                    snapshots.Add(progress);
                }
            }
        }

        JobRunner.ProgressChanged += OnProgress;
        try
        {
            var run = await RunAsync(job);
            Assert.Equal(RunOutcome.Success, run.Outcome);
        }
        finally
        {
            JobRunner.ProgressChanged -= OnProgress;
        }

        var transferring = snapshots.Where(snapshot => snapshot.Phase == RunPhase.Transferring).ToList();
        Assert.True(transferring.Count >= 3, $"only {transferring.Count} snapshots while transferring");
        for (var index = 1; index < snapshots.Count; index++)
        {
            Assert.True(snapshots[index].Fraction >= snapshots[index - 1].Fraction, "the fraction never goes back");
        }

        Assert.Contains(transferring, snapshot => snapshot.SpeedHistory.Count > 0 && snapshot.FilesPerSecond > 0 && snapshot.BytesPerSecond > 0);
        Assert.Contains(transferring, snapshot => snapshot.Text.StartsWith("Kopiere ", StringComparison.Ordinal) && snapshot.Text.Contains("Dateien/s", StringComparison.Ordinal));
        Assert.Contains(transferring, snapshot => snapshot.Remaining is not null);
        Assert.Equal(1, snapshots[^1].Fraction);
        Assert.Equal(200, snapshots[^1].FilesTotal);
    }
}
