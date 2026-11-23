using ThixSync.Core.Business;
using ThixSync.Core.Business.Engine;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;

namespace ThixSync.Tests;

/// <summary>
/// When jobs run by themselves (interval, daily, weekly, missed runs, at start), the line of jobs (at most N at once), the job state
/// file, and the German formatting helper.
/// </summary>
public sealed class SchedulerTests : EngineTestBase
{
    private static readonly DateTime Monday = new(2026, 11, 23, 0, 0, 0, DateTimeKind.Local);

    private static DateTimeOffset Local(DateTime day, int hour, int minute = 0) => new(day.AddHours(hour).AddMinutes(minute));

    [Fact]
    public void An_interval_job_is_due_one_interval_after_its_last_run()
    {
        var job = new SyncJob { Schedule = new JobSchedule { Kind = ScheduleKind.Interval, IntervalMinutes = 60 }, UpdateDate = Local(Monday, 1) };
        var now = Local(Monday, 12);

        Assert.True(SchedulerService.IsDue(job, now, now, now.AddMinutes(-61), now.AddHours(-1)));
        Assert.False(SchedulerService.IsDue(job, now, now, now.AddMinutes(-30), now.AddHours(-1)));
        Assert.True(SchedulerService.IsDue(job, now, now, null, now.AddHours(-1)), "a job that never ran is due one interval after it was set up");
        Assert.False(SchedulerService.IsDue(job with { IsPaused = true }, now, now, now.AddDays(-1), now.AddHours(-1)));

        var noCatchUp = job with { Schedule = job.Schedule with { CatchUpMissed = false } };
        Assert.False(SchedulerService.IsDue(noCatchUp, now, now, now.AddDays(-1), now.AddMinutes(-5)), "missed runs are not made up right at start");
        Assert.True(SchedulerService.IsDue(noCatchUp, now, now.AddMinutes(56), now.AddDays(-1), now.AddMinutes(-5)));
    }

    [Fact]
    public void A_daily_job_is_due_when_its_time_passed_since_the_last_look()
    {
        var job = new SyncJob { Schedule = new JobSchedule { Kind = ScheduleKind.Daily, TimeOfDay = new TimeOnly(20, 0) } };

        Assert.True(SchedulerService.IsDue(job, Local(Monday, 19, 59), Local(Monday, 20, 1), null, Local(Monday, 8)));
        Assert.False(SchedulerService.IsDue(job, Local(Monday, 20, 1), Local(Monday, 21), null, Local(Monday, 8)));
        Assert.False(SchedulerService.IsDue(job, Local(Monday, 10), Local(Monday, 19, 59), null, Local(Monday, 8)));
    }

    [Fact]
    public void A_missed_scheduled_time_is_made_up_once_at_start()
    {
        var job = new SyncJob
        {
            Schedule = new JobSchedule { Kind = ScheduleKind.Weekly, Days = [DayOfWeek.Saturday], TimeOfDay = new TimeOnly(9, 0) },
            UpdateDate = Local(Monday.AddDays(-30), 9),
        };
        var appStart = Local(Monday, 8);
        var lastRun = Local(Monday.AddDays(-9), 9); // the Saturday before last

        var since = SchedulerService.FirstReference(job, appStart, lastRun);
        Assert.True(SchedulerService.IsDue(job, since, appStart, lastRun, appStart), "last Saturday was missed");

        var noCatchUp = job with { Schedule = job.Schedule with { CatchUpMissed = false } };
        since = SchedulerService.FirstReference(noCatchUp, appStart, lastRun);
        Assert.False(SchedulerService.IsDue(noCatchUp, since, appStart, lastRun, appStart));
    }

    [Fact]
    public void The_next_run_of_an_interval_job_counts_from_its_last_run()
    {
        AppClock.Pin(Local(Monday, 12));
        var job = MemoryJob(SyncMode.Mirror) with { Schedule = new JobSchedule { Kind = ScheduleKind.Interval, IntervalMinutes = 15 } };

        Assert.Equal(Local(Monday, 12, 15), SchedulerService.NextRunOf(job));
        Assert.Null(SchedulerService.NextRunOf(job with { IsPaused = true }));
        Assert.Null(SchedulerService.NextRunOf(job with { RunsOnDevice = "ANDERER-PC" }));
    }

    [Fact]
    public async Task A_job_that_runs_at_start_is_started_by_the_scheduler()
    {
        var job = MemoryJob(SyncMode.Mirror);
        Change(job, value => value with { Schedule = new JobSchedule { RunAtStartup = true } });
        MemoryProvider.Drives["quelle"].Put("a.txt", "a", At(1));

        SchedulerService.Start();
        SchedulerService.Start();
        Assert.True(SchedulerService.IsRunning);
        await WaitUntilAsync(() => SyncStore.LoadRuns(job.Id, 10).Any(run => run.Outcome != RunOutcome.Running));
        SchedulerService.Stop();

        var run = Assert.Single(SyncStore.LoadRuns(job.Id, 10));
        Assert.Equal((RunTrigger.Startup, RunOutcome.Success), (run.Trigger, run.Outcome));
        Assert.True(MemoryProvider.Drives["ziel"].Exists("a.txt"));
        Assert.False(SchedulerService.IsRunning);
    }

    [Fact]
    public async Task Paused_scheduling_starts_nothing()
    {
        var job = MemoryJob(SyncMode.Mirror);
        Change(job, value => value with { Schedule = new JobSchedule { RunAtStartup = true } });
        SchedulerService.PauseAll();
        try
        {
            SchedulerService.Start();
            await Task.Delay(300);
            Assert.Empty(SyncStore.LoadRuns(job.Id, 10));
            Assert.True(SchedulerService.IsPausedAll);
        }
        finally
        {
            SchedulerService.Stop();
            SchedulerService.ResumeAll();
        }
    }

    [Fact]
    public async Task At_most_the_set_number_of_jobs_run_at_once()
    {
        JobQueue.LimitForTests = () => 1;
        var first = MemoryJob(SyncMode.Mirror, options => options with { Parallelism = 2 }, name: "Erster", source: "q1", target: "z1");
        var second = MemoryJob(SyncMode.Mirror, options => options with { Parallelism = 2 }, name: "Zweiter", source: "q2", target: "z2");
        foreach (var name in new[] { "q1", "q2" })
        {
            for (var index = 0; index < 8; index++)
            {
                MemoryProvider.Drives[name].Put($"f{index}.txt", "x", At(1));
            }
        }

        MemoryProvider.Drives["z1"].WriteDelay = TimeSpan.FromMilliseconds(60);
        MemoryProvider.Drives["z2"].WriteDelay = TimeSpan.FromMilliseconds(60);

        Assert.True(JobRunner.Start(first.Id, new RunRequest()));
        Assert.True(JobRunner.Start(second.Id, new RunRequest()));
        Assert.True(JobRunner.IsRunning(first.Id) && JobRunner.IsRunning(second.Id));
        await WaitUntilAsync(() => JobRunner.Running().Any(progress => progress.Text.StartsWith("Wartet", StringComparison.Ordinal)));
        await WaitUntilAsync(() => !JobRunner.IsRunning(first.Id) && !JobRunner.IsRunning(second.Id));

        var a = Assert.Single(SyncStore.LoadRuns(first.Id, 10));
        var b = Assert.Single(SyncStore.LoadRuns(second.Id, 10));
        Assert.Equal((RunOutcome.Success, RunOutcome.Success), (a.Outcome, b.Outcome));
        Assert.True(a.FinishedAt <= b.StartedAt || b.FinishedAt <= a.StartedAt, "the runs did not overlap");
    }

    [Fact]
    public void The_job_state_keeps_many_rows_and_can_be_forgotten()
    {
        var jobId = Guid.NewGuid();
        var rows = Enumerable.Range(0, 100_000)
            .Select(index => new StateRow(0, $"ordner{index % 100}/datei{index}.txt", false, index, At(index % 1000), null, index, At(1), $"md5:{index}"))
            .ToList();
        using (var store = JobStateStore.Open(jobId))
        {
            store.ResetPair(0, "fingerprint");
            store.Save(rows, []);
            store.Save([rows[0] with { SourceSize = 42 }], [(0, rows[1].Path)]);
            var loaded = store.LoadRows(0);
            Assert.Equal(99_999, loaded.Count);
            Assert.Equal(42, loaded.Single(row => row.Path == rows[0].Path).SourceSize);
            Assert.Equal(At(5), loaded.Single(row => row.Path == rows[5].Path).SourceModified);
            Assert.Equal("fingerprint", store.LoadPairs()[0].Fingerprint);
        }

        Assert.True(JobStateStore.Exists(jobId));
        JobRunner.ForgetState(jobId);
        Assert.False(JobStateStore.Exists(jobId));
    }

    [Fact]
    public void Numbers_sizes_speeds_and_durations_are_written_the_German_way()
    {
        Assert.Equal("0 Bytes", Format.Bytes(0));
        Assert.Equal("1 Byte", Format.Bytes(1));
        Assert.Equal("1,5 KB", Format.Bytes(1536));
        Assert.Equal("3,2 GB", Format.Bytes((long)(3.2 * 1024 * 1024 * 1024)));
        Assert.Equal("512 MB", Format.Bytes(512L * 1024 * 1024));
        Assert.Equal("48,2 MB/s", Format.Speed(48.2 * 1024 * 1024));
        Assert.Equal("2 Min. 10 Sek.", Format.Duration(TimeSpan.FromSeconds(130)));
        Assert.Equal("45 Sek.", Format.Duration(TimeSpan.FromSeconds(45)));
        Assert.Equal("1 Std. 5 Min.", Format.Duration(TimeSpan.FromMinutes(65)));
        Assert.Equal("2 Tage 3 Std.", Format.Duration(TimeSpan.FromHours(51)));
        Assert.Equal("1.204", Format.Count(1204));
        Assert.Equal("1.234.567", Format.Count(1_234_567));
    }
}
