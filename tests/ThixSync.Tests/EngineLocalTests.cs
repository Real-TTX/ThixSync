using System.Diagnostics;
using System.Text.RegularExpressions;
using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using Xunit.Abstractions;

namespace ThixSync.Tests;

/// <summary>
/// The engine between real folders of this computer (LocalProvider): two-way with versions, the copy check, and the throughput with
/// thousands of small files (many workers at once).
/// </summary>
public sealed partial class EngineLocalTests(ITestOutputHelper output) : EngineTestBase
{
    [Fact]
    public async Task Two_folders_of_this_computer_are_kept_in_step()
    {
        var source = Data.MakeTree("quelle", new Dictionary<string, string> { ["a.txt"] = "a", ["sub/b.txt"] = "b", ["leer/.keep"] = "" });
        var target = Data.MakeTree("ziel", new Dictionary<string, string> { ["c.txt"] = "c" });
        Directory.CreateDirectory(Path.Combine(source, "nur-ordner"));
        var job = LocalJob(SyncMode.TwoWay, source, target, options => options with { KeepVersions = true });

        var first = await RunAsync(job);

        Assert.Equal(RunOutcome.Success, first.Outcome);
        Assert.Equal("b", File.ReadAllText(Path.Combine(target, "sub", "b.txt")));
        Assert.Equal("c", File.ReadAllText(Path.Combine(source, "c.txt")));
        Assert.True(Directory.Exists(Path.Combine(target, "nur-ordner")), "empty folders are copied");
        Assert.Equal(File.GetLastWriteTimeUtc(Path.Combine(source, "a.txt")), File.GetLastWriteTimeUtc(Path.Combine(target, "a.txt")));

        var file = Path.Combine(source, "a.txt");
        File.WriteAllText(file, "a geändert");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(1));
        File.Delete(Path.Combine(target, "c.txt"));
        var second = await RunAsync(job);

        Assert.Equal(RunOutcome.Success, second.Outcome);
        Assert.Equal((1L, 1L), (second.FilesCopied, second.FilesDeleted));
        Assert.Equal("a geändert", File.ReadAllText(Path.Combine(target, "a.txt")));
        Assert.False(File.Exists(Path.Combine(source, "c.txt")));
        var kept = Directory.GetFiles(Path.Combine(source, ".thixsync", "versions"), "c.txt", SearchOption.AllDirectories);
        Assert.Equal("c", File.ReadAllText(Assert.Single(kept)));
        Assert.Empty(Directory.GetFiles(target, ".~thixsync-*", SearchOption.AllDirectories));

        var third = await RunAsync(job);
        Assert.Equal((0L, 0L), (third.FilesCopied, third.FilesDeleted));
    }

    [Fact]
    public async Task The_copy_check_compares_the_content()
    {
        var source = Data.MakeTree("quelle", new Dictionary<string, string> { ["a.txt"] = "Inhalt", ["b/c.txt"] = new string('x', 100_000) });
        var target = Path.Combine(Data.Folder, "ziel");
        var job = LocalJob(SyncMode.Mirror, source, target, options => options with { VerifyAfterCopy = true });

        var run = await RunAsync(job);

        Assert.Equal((RunOutcome.Success, 2L), (run.Outcome, run.FilesCopied));
        Assert.Equal(new string('x', 100_000), File.ReadAllText(Path.Combine(target, "b", "c.txt")));
    }

    [Fact]
    public async Task Thousands_of_small_files_are_copied_by_many_workers_at_once()
    {
        var files = new Dictionary<string, string>();
        for (var index = 0; index < 5000; index++)
        {
            files[$"ordner{index % 50:00}/datei{index:0000}.txt"] = $"Inhalt der Datei {index}";
        }

        var source = Data.MakeTree("quelle", files);
        var target = Path.Combine(Data.Folder, "ziel");
        var job = LocalJob(SyncMode.Mirror, source, target);
        var watch = Stopwatch.StartNew();

        var run = await RunAsync(job);

        watch.Stop();
        Assert.Equal((RunOutcome.Success, 5000L), (run.Outcome, run.FilesCopied));
        Assert.Equal(5000, Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(90), $"took {watch.Elapsed}");
        var line = Entries(run).Select(entry => entry.Message).First(message => message.StartsWith("Bis zu ", StringComparison.Ordinal));
        var peak = int.Parse(PeakPattern().Match(line).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(peak > 1, $"only {peak} at once: {line}");
        output.WriteLine($"5000 files in {run.Duration.TotalMilliseconds:0} ms ({5000 / Math.Max(0.001, run.Duration.TotalSeconds):0} files/s); {line}");

        var again = await RunAsync(job);
        Assert.Equal(0, again.FilesCopied);
    }

    private static SyncJob LocalJob(SyncMode mode, string source, string target, Func<JobOptions, JobOptions>? options = null)
    {
        var thisComputer = ConnectionService.EnsureThisComputer();
        return JobService.Save(new SyncJob
        {
            Name = "Lokal",
            Mode = mode,
            SourceConnectionId = thisComputer.Id,
            TargetConnectionId = thisComputer.Id,
            Pairs = [new FolderPair { SourcePath = source, TargetPath = target }],
            Options = (options ?? (value => value))(new JobOptions { KeepVersions = false }),
        });
    }

    [GeneratedRegex(@"^Bis zu (\d+) ")]
    private static partial Regex PeakPattern();
}
