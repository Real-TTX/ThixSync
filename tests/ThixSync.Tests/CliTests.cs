using System.Text.Json;
using ThixSync.Cli;
using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;
using ThixSync.Core.Storage.Providers.Local;

namespace ThixSync.Tests;

/// <summary>
/// The command line thixsync-cli: parsing, help, exit codes, the JSON of the lists, the progress line, run (with a runner of the
/// test – the engine is not part of these tests), export and import. Data is made through the services in the test's own folder.
/// </summary>
public sealed class CliTests : IDisposable
{
    private readonly TestData _data = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _error = new();

    public void Dispose()
    {
        AccessPolicy.UsePolicyReaderForTests(null);
        _data.Dispose();
    }

    // ---- command line ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_parser_reads_arguments_flags_and_values_in_every_form()
    {
        var parsed = CommandLine.Parse(["run", "Fotos", "--preview", "--json", "-q"], Commands.All);
        Assert.Equal(RequestKind.Command, parsed.Kind);
        Assert.Equal("run", parsed.Command!.Name);
        Assert.Equal(["Fotos"], parsed.Arguments);
        Assert.True(parsed.Has("preview") && parsed.Has("json") && parsed.Has("quiet"));
        Assert.False(parsed.Has("confirm-deletes"));

        var runs = CommandLine.Parse(["RUNS", "--job=Fotos", "-n", "5"], Commands.All);
        Assert.Equal("Fotos", runs.Value("job"));
        Assert.Equal(5, runs.Number("limit", 20, 1, 1000));
        Assert.Equal(20, CommandLine.Parse(["runs"], Commands.All).Number("limit", 20, 1, 1000));

        // "--" ends the options: a job may be called "--preview"
        Assert.Equal(["--preview"], CommandLine.Parse(["run", "--", "--preview"], Commands.All).Arguments);
        Assert.Equal(["-"], CommandLine.Parse(["import", "-"], Commands.All).Arguments);

        Assert.Equal(RequestKind.Help, CommandLine.Parse([], Commands.All).Kind);
        Assert.Equal(RequestKind.Version, CommandLine.Parse(["--version"], Commands.All).Kind);
        Assert.Equal("log", CommandLine.Parse(["help", "log"], Commands.All).Command!.Name);
        var help = CommandLine.Parse(["run", "Fotos", "--help"], Commands.All);
        Assert.Equal((RequestKind.Help, "run"), (help.Kind, help.Command!.Name));
    }

    [Theory]
    [InlineData("frob")]
    [InlineData("--json")]
    [InlineData("run")]
    [InlineData("run", "a", "b")]
    [InlineData("run", "a", "--colour")]
    [InlineData("list", "--json=yes")]
    [InlineData("runs", "--limit")]
    [InlineData("runs", "--limit", "1", "--limit", "2")]
    [InlineData("help", "frob")]
    [InlineData("--version", "list")]
    public void A_wrong_command_line_is_a_usage_error(params string[] args)
    {
        Assert.Throws<UsageException>(() => CommandLine.Parse(args, Commands.All));
    }

    [Fact]
    public void A_number_out_of_range_is_a_usage_error()
    {
        var parsed = CommandLine.Parse(["runs", "--limit", "0"], Commands.All);
        Assert.Throws<UsageException>(() => parsed.Number("limit", 20, 1, 1000));
        Assert.Throws<UsageException>(() => CommandLine.Parse(["runs", "--limit=viele"], Commands.All).Number("limit", 20, 1, 1000));
    }

    [Fact]
    public async Task The_help_names_every_command_and_the_exit_codes()
    {
        Assert.Equal(0, await Run([]));
        var general = _out.ToString();
        foreach (var command in Commands.All)
        {
            Assert.Contains($"  {command.Name}", general);
        }

        Assert.Contains("Exit-Codes:", general);
        Assert.Contains("130  Mit Strg+C abgebrochen", general);
        Assert.Contains(AppPaths.DataDirectory, general);

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, await Run(["help", "run"]));
        var runHelp = _out.ToString();
        Assert.Contains("Aufruf: thixsync-cli run <auftrag>", runHelp);
        Assert.Contains("--confirm-deletes", runHelp);
        Assert.Contains("-q, --quiet", runHelp);

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, await Run(["run", "--help"]));
        Assert.Equal(runHelp, _out.ToString());

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, await Run(["--version"]));
        Assert.Equal($"thixsync-cli {AppInfo.Version} ({AppInfo.Channel})", _out.ToString().Trim());
    }

    [Fact]
    public async Task Errors_become_the_documented_exit_codes()
    {
        Assert.Equal(ExitCodes.Usage, await Run(["frob"]));
        Assert.Contains("Unbekannter Befehl „frob“", _error.ToString());
        Assert.Contains("Hilfe: thixsync-cli help", _error.ToString());

        Assert.Equal(ExitCodes.Usage, await Run(["runs", "--limit", "x"]));
        Assert.Contains("Hilfe: thixsync-cli help runs", _error.ToString());

        Assert.Equal(ExitCodes.NotFound, await Run(["run", "Gibt es nicht"]));
        Assert.Equal(ExitCodes.NotFound, await Run(["test", "Gibt es nicht"]));
        Assert.Equal(ExitCodes.NotFound, await Run(["log", Guid.NewGuid().ToString()]));
        Assert.Equal(ExitCodes.NotFound, await Run(["import", Path.Combine(_data.Folder, "fehlt.json")]));

        // the business layer refuses: a configuration locked by policy
        var job = MakeJob("Fotos");
        AccessPolicy.UsePolicyReaderForTests(name => name == "LockConfiguration");
        _error.GetStringBuilder().Clear();
        Assert.Equal(ExitCodes.BusinessError, await Run(["pause", "Fotos"]));
        Assert.NotEmpty(_error.ToString().Trim());
        Assert.False(JobService.Get(job.Id).IsPaused);
        AccessPolicy.UsePolicyReaderForTests(_ => false);

        // anything unexpected (here: the engine is not there yet) is a failure, not a crash
        Assert.Equal(ExitCodes.Failed, await Run(["run", "Fotos"], host => host with { RunAsync = (_, _, _) => throw new NotImplementedException() }));
    }

    [Theory]
    [InlineData(RunOutcome.Success, 0)]
    [InlineData(RunOutcome.Analysed, 0)]
    [InlineData(RunOutcome.Warnings, 1)]
    [InlineData(RunOutcome.NeedsConfirmation, 1)]
    [InlineData(RunOutcome.Failed, 2)]
    [InlineData(RunOutcome.Running, 2)]
    [InlineData(RunOutcome.Cancelled, 130)]
    public void Every_outcome_has_its_exit_code(RunOutcome outcome, int exitCode)
    {
        Assert.Equal(exitCode, ExitCodes.ForOutcome(outcome));
    }

    // ---- lists -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task List_shows_the_jobs_as_a_table_and_as_json()
    {
        var fotos = MakeJob("Fotos", SyncMode.Mirror);
        MakeJob("Dokumente", SyncMode.Backup);
        var run = SaveRun(fotos, RunOutcome.Success, bytes: 3_435_973_837);

        Assert.Equal(0, await Run(["list"]));
        var table = _out.ToString();
        Assert.Contains("Quelle → Ziel", table);
        Assert.Contains("Spiegelung", table);
        Assert.Contains("Erfolgreich · ", table);
        Assert.True(table.IndexOf("Dokumente", StringComparison.Ordinal) < table.IndexOf("Fotos", StringComparison.Ordinal), "sorted by name");

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, await Run(["list", "--json"]));
        using var json = JsonDocument.Parse(_out.ToString());
        var jobs = json.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, jobs.Count);
        var first = jobs.Single(job => job.GetProperty("name").GetString() == "Fotos");
        Assert.Equal(fotos.Id, first.GetProperty("id").GetGuid());
        Assert.Equal("Mirror", first.GetProperty("mode").GetString());
        Assert.False(first.GetProperty("paused").GetBoolean());
        Assert.Equal("Manuell", first.GetProperty("schedule").GetString());
        Assert.Equal(AppInfo.MachineName, first.GetProperty("source").GetProperty("name").GetString());
        Assert.EndsWith("quelle-Fotos", first.GetProperty("pairs")[0].GetProperty("source").GetString());
        Assert.Equal(run.Id, first.GetProperty("lastRun").GetProperty("id").GetGuid());
        Assert.Equal("Success", first.GetProperty("lastRun").GetProperty("outcome").GetString());
        Assert.Equal(3_435_973_837, first.GetProperty("lastRun").GetProperty("bytesCopied").GetInt64());
        Assert.Equal(JsonValueKind.Null, jobs.Single(job => job.GetProperty("name").GetString() == "Dokumente").GetProperty("lastRun").ValueKind);
    }

    [Fact]
    public async Task Connections_show_type_address_and_state_but_never_a_secret()
    {
        ConnectionService.EnsureThisComputer();
        var server = SaveSftp("Server", "geheim-123");

        Assert.Equal(0, await Run(["connections"]));
        Assert.Contains("SFTP / SSH", _out.ToString());
        Assert.Contains("anna@server.example.com", _out.ToString());

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, await Run(["connections", "--json"]));
        Assert.DoesNotContain("geheim-123", _out.ToString());
        using var json = JsonDocument.Parse(_out.ToString());
        var connections = json.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, connections.Count);
        var sftp = connections.Single(connection => connection.GetProperty("id").GetGuid() == server.Id);
        Assert.Equal("Sftp", sftp.GetProperty("kind").GetString());
        Assert.Equal("Servers", sftp.GetProperty("group").GetString());
        Assert.True(sftp.GetProperty("hasSecret").GetBoolean());
        var local = connections.Single(connection => connection.GetProperty("kind").GetString() == "Local");
        Assert.True(local.GetProperty("usableHere").GetBoolean());
        Assert.Equal("", local.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Test_says_whether_a_connection_works()
    {
        // the "server" is a folder of the test: no network
        var server = SaveSftp("Server", "geheim-123");
        var reachable = true;
        StorageProviders.OverrideForTests(ConnectionKind.Sftp, new FakeProvider(() => reachable
            ? new LocalStorage(_data.MakeTree("server", new Dictionary<string, string> { ["a.txt"] = "a" }), ConnectionKind.Local, "server.example.com")
            : throw new StorageException(StorageErrorKind.Unreachable, "Der Server antwortet nicht.")));

        Assert.Equal(0, await Run(["test", "Server"]));
        Assert.Contains("OK: Verbunden mit server.example.com", _out.ToString());

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, await Run(["test", server.Id.ToString(), "--json"]));
        using (var json = JsonDocument.Parse(_out.ToString()))
        {
            Assert.True(json.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(0, json.RootElement.GetProperty("exitCode").GetInt32());
        }

        reachable = false;
        Assert.Equal(ExitCodes.Failed, await Run(["test", "server"]));
        Assert.Contains("Fehlgeschlagen: Der Server antwortet nicht.", _error.ToString());
    }

    [Fact]
    public async Task Runs_and_log_show_the_protocol()
    {
        var fotos = MakeJob("Fotos");
        var other = MakeJob("Dokumente");
        var older = SaveRun(fotos, RunOutcome.Success, startedMinutesAgo: 60);
        var newer = SaveRun(fotos, RunOutcome.Warnings, startedMinutesAgo: 5);
        SaveRun(other, RunOutcome.Failed, startedMinutesAgo: 1);
        SyncStore.AddEntries(newer.Id,
        [
            new RunEntry { At = newer.StartedAt, Level = RunEntryLevel.Info, Action = PlanAction.CopyToTarget, Path = "2026/a.jpg", Bytes = 4_300_000, Message = "Kopiert" },
            new RunEntry { At = newer.StartedAt, Level = RunEntryLevel.Error, Action = PlanAction.CopyToTarget, Path = "2026/b.jpg", Message = "Zugriff verweigert" },
        ]);

        Assert.Equal(0, await Run(["runs", "--job", "Fotos", "--json"]));
        using (var json = JsonDocument.Parse(_out.ToString()))
        {
            var runs = json.RootElement.EnumerateArray().Select(run => run.GetProperty("id").GetGuid()).ToList();
            Assert.Equal([newer.Id, older.Id], runs);
        }

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, await Run(["runs", "--limit", "1"]));
        Assert.Contains("Fehlgeschlagen", _out.ToString());
        Assert.DoesNotContain("Mit Warnungen", _out.ToString());

        // the eight characters of the list are enough
        _out.GetStringBuilder().Clear();
        Assert.Equal(0, await Run(["log", Lookup.ShortId(newer.Id)]));
        var log = _out.ToString();
        Assert.Contains("Mit Warnungen: Fotos", log);
        Assert.Contains("2026/a.jpg  (4,1 MB)  Kopiert", log);
        Assert.Contains("FEHLER", log);

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, await Run(["log", newer.Id.ToString(), "--problems", "--json"]));
        using (var json = JsonDocument.Parse(_out.ToString()))
        {
            var entries = json.RootElement.GetProperty("entries").EnumerateArray().ToList();
            Assert.Single(entries);
            Assert.Equal("Error", entries[0].GetProperty("level").GetString());
            Assert.Equal("CopyToTarget", entries[0].GetProperty("action").GetString());
        }

        Assert.Equal(ExitCodes.Usage, await Run(["log", "xy"]));
    }

    [Fact]
    public async Task Status_shows_what_the_runner_reports()
    {
        var job = MakeJob("Fotos");
        Assert.Equal(0, await Run(["status"]));
        Assert.Contains("Gerade läuft kein Auftrag.", _out.ToString());

        _out.GetStringBuilder().Clear();
        var progress = Transferring(job.Id);
        Assert.Equal(0, await Run(["status", "--json"], host => host with { Running = () => [progress] }));
        using var json = JsonDocument.Parse(_out.ToString());
        Assert.Equal("Fotos", json.RootElement[0].GetProperty("jobName").GetString());
        Assert.Equal("Transferring", json.RootElement[0].GetProperty("phase").GetString());
        Assert.Equal(1204, json.RootElement[0].GetProperty("filesDone").GetInt64());
    }

    [Fact]
    public async Task Pause_and_resume_change_the_job()
    {
        var job = MakeJob("Fotos");
        Assert.Equal(0, await Run(["pause", "fotos"]));
        Assert.True(JobService.Get(job.Id).IsPaused);
        Assert.Contains("„Fotos“ ist pausiert", _out.ToString());

        Assert.Equal(0, await Run(["resume", job.Id.ToString()]));
        Assert.False(JobService.Get(job.Id).IsPaused);
    }

    // ---- run -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Run_starts_the_job_from_the_cli_shows_progress_and_the_summary()
    {
        var job = MakeJob("Fotos");
        var requests = new List<RunRequest>();
        var exitCode = await Run(["run", "Fotos"], host => FakeRunner(host, job, RunOutcome.Warnings, requests.Add) with { IsTerminal = true, Width = () => 200 });

        Assert.Equal(ExitCodes.Warnings, exitCode);
        var request = Assert.Single(requests);
        Assert.Equal(RunTrigger.Cli, request.Trigger);
        Assert.False(request.AnalyseOnly);
        Assert.False(request.ConfirmDeletes);
        var output = _out.ToString();
        Assert.Contains("\rÜbertragen · [", output);
        Assert.Contains("1.204/8.311 Dateien", output);
        Assert.Contains("Mit Warnungen: Fotos", output);
        Assert.Contains("Kopiert: 1.204 Dateien (3,2 GB)", output);
        Assert.Contains("Protokoll: thixsync-cli log ", output);
    }

    [Fact]
    public async Task Redirected_output_gets_plain_lines_and_quiet_gets_none()
    {
        var job = MakeJob("Fotos");
        Assert.Equal(0, await Run(["run", "Fotos", "--confirm-deletes"], host => FakeRunner(host, job, RunOutcome.Success, request => Assert.True(request.ConfirmDeletes))));
        var lines = _out.ToString().Split(Environment.NewLine);
        Assert.DoesNotContain("\r", _out.ToString().Replace(Environment.NewLine, "\n"), StringComparison.Ordinal);
        Assert.Contains(lines, line => line.StartsWith('[') && line.Contains("] Übertragen · 14 % · 1.204/8.311 Dateien", StringComparison.Ordinal));
        Assert.Single(lines, line => line.Contains("] Übertragen", StringComparison.Ordinal)); // the second update came within 5 seconds

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, await Run(["run", "Fotos", "--quiet"], host => FakeRunner(host, job, RunOutcome.Success)));
        Assert.Equal("", _out.ToString());
        Assert.Equal(ExitCodes.Failed, await Run(["run", "Fotos", "-q"], host => FakeRunner(host, job, RunOutcome.Failed)));
        Assert.Equal("", _out.ToString());
        Assert.Contains("Fehlgeschlagen: Fotos", _error.ToString());
    }

    [Fact]
    public async Task Preview_shows_the_plan_grouped_with_the_first_steps()
    {
        var job = MakeJob("Fotos");
        var plan = new SyncPlan
        {
            JobId = job.Id,
            Items =
            [
                .. Enumerable.Range(1, 24).Select(index => new PlanItem { Action = PlanAction.CopyToTarget, Path = $"neu/{index:00}.jpg", Bytes = 1_048_576, Reason = "Neu in der Quelle" }),
                new PlanItem { Action = PlanAction.DeleteOnTarget, Path = "alt.txt", Reason = "In der Quelle gelöscht" },
                new PlanItem { Action = PlanAction.InStep, Path = "gleich.txt" },
            ],
        };
        CliHost Preview(CliHost host) => FakeRunner(host, job, RunOutcome.Analysed, request => Assert.True(request.AnalyseOnly)) with { LastPlan = _ => plan };

        Assert.Equal(0, await Run(["run", "Fotos", "--preview"], Preview));
        var output = _out.ToString();
        Assert.Contains("Plan: 25 Schritte · 24,0 MB zu kopieren", output);
        Assert.Contains("Kopieren → Ziel       24  24,0 MB", output);
        Assert.Contains("neu/20.jpg (1,0 MB) – Neu in der Quelle", output);
        Assert.DoesNotContain("neu/21.jpg", output);
        Assert.Contains("… und 5 weitere (--all zeigt alle)", output);
        Assert.DoesNotContain("gleich.txt", output);

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, await Run(["run", "Fotos", "-p", "--all"], Preview));
        Assert.Contains("alt.txt – In der Quelle gelöscht", _out.ToString());

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, await Run(["run", "Fotos", "--preview", "--json"], Preview));
        using var json = JsonDocument.Parse(_out.ToString());
        Assert.Equal("Analysed", json.RootElement.GetProperty("run").GetProperty("outcome").GetString());
        Assert.Equal(24, json.RootElement.GetProperty("plan").GetProperty("counts").GetProperty("CopyToTarget").GetInt32());
        Assert.Equal(25, json.RootElement.GetProperty("plan").GetProperty("items").GetArrayLength());

        Assert.Equal(ExitCodes.Usage, await Run(["run", "Fotos", "--all"], Preview));
    }

    [Fact]
    public async Task Ctrl_c_cancels_the_run_cleanly_with_130()
    {
        var job = MakeJob("Fotos");
        Action? pressCtrlC = null;
        var cancelled = new List<Guid>();
        CliHost Cancelling(CliHost host) => host with
        {
            OnCancelKey = action =>
            {
                pressCtrlC = action;
                return new Disposable();
            },
            CancelRun = cancelled.Add,
            RunAsync = async (_, _, token) =>
            {
                pressCtrlC!();
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("not reached");
            },
        };

        Assert.Equal(ExitCodes.Cancelled, await Run(["run", "Fotos"], Cancelling));
        Assert.Equal([job.Id], cancelled);
        Assert.Contains("„Fotos“ abgebrochen.", _error.ToString());

        // the runner may also end the run as Cancelled instead of throwing
        Assert.Equal(ExitCodes.Cancelled, await Run(["run", "Fotos"], host => FakeRunner(host, job, RunOutcome.Cancelled)));
    }

    // ---- progress and formats --------------------------------------------------------------------------------------------------

    [Fact]
    public void The_progress_line_shows_everything_that_fits()
    {
        var progress = Transferring(Guid.NewGuid());
        var wide = ProgressLine.Render(progress, 200);
        Assert.Equal("Übertragen · [███░░░░░░░░░░░░░░░░░] 14 % · 1.204/8.311 Dateien · 3,2 GB/22,5 GB · 48,2 MB/s · 120 Dateien/s · noch 2 min 15 s · 3 Fehler", wide);

        // narrow: the least important parts go first; the phase stays
        var narrow = ProgressLine.Render(progress, 70);
        Assert.True(narrow.Length <= 70);
        Assert.StartsWith("Übertragen · [", narrow);
        Assert.Contains("1.204/8.311 Dateien", narrow);
        Assert.DoesNotContain("Dateien/s", narrow);
        Assert.Equal("Übertragen", ProgressLine.Render(progress, 12));

        var scanning = ProgressLine.Render(new RunProgress { Phase = RunPhase.Scanning, FilesScannedSource = 12_345, FilesScannedTarget = 99, Elapsed = TimeSpan.FromSeconds(7) }, 200);
        Assert.Equal("Durchsuchen · Quelle 12.345 · Ziel 99 Dateien · 7 s", scanning);
        Assert.Equal("[██████████░░░░░░░░░░]", ProgressLine.Bar(0.5));
        Assert.Equal("[████████████████████]", ProgressLine.Bar(1.5));
    }

    [Fact]
    public void Numbers_sizes_and_durations_are_german()
    {
        Assert.Equal("3,2 GB", CliFormat.Bytes(3_435_973_837));
        Assert.Equal("48,2 MB/s", CliFormat.Speed(50_541_363));
        Assert.Equal("512 B", CliFormat.Bytes(512));
        Assert.Equal("1,0 KB", CliFormat.Bytes(1024));
        Assert.Equal("120 MB", CliFormat.Bytes(125_829_120));
        Assert.Equal("1,0 MB", CliFormat.Bytes(1_048_575));
        Assert.Equal("1.204", CliFormat.Count(1204));
        Assert.Equal("1.234.567", CliFormat.Count(1_234_567));
        Assert.Equal("3,5 Dateien/s", CliFormat.FilesPerSecond(3.5));
        Assert.Equal("1.250 Dateien/s", CliFormat.FilesPerSecond(1250));
        Assert.Equal("42 %", CliFormat.Percent(0.4199));
        Assert.Equal("45 s", CliFormat.Duration(TimeSpan.FromSeconds(45)));
        Assert.Equal("2 min 05 s", CliFormat.Duration(TimeSpan.FromSeconds(125)));
        Assert.Equal("1 h 12 min", CliFormat.Duration(TimeSpan.FromMinutes(72)));
        Assert.Equal("2 d 3 h", CliFormat.Duration(TimeSpan.FromHours(51)));
    }

    // ---- export / import -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Export_and_import_carry_jobs_and_connections_without_secrets()
    {
        var job = MakeJob("Fotos");
        var server = SaveSftp("Server", "geheim-123");
        var backup = JobService.Save(job with { Id = Guid.NewGuid(), Name = "Server-Backup", TargetConnectionId = server.Id, Pairs = [new FolderPair { SourcePath = job.Pairs[0].SourcePath, TargetPath = "/backup" }] });
        var file = Path.Combine(_data.Folder, "export.json");

        Assert.Equal(0, await Run(["export", file]));
        Assert.Contains("2 Verbindungen und 2 Aufträge exportiert", _out.ToString());
        var json = await File.ReadAllTextAsync(file);
        Assert.DoesNotContain("geheim-123", json);
        Assert.Contains("\"format\": \"thixsync-configuration\"", json);
        Assert.Contains("\"mode\": \"TwoWay\"", json);

        // another computer's data folder (same machine name): "this computer" exists there under another id
        var thisComputerBefore = ConnectionService.EnsureThisComputer().Id;
        using var other = new TestData();
        var thisComputerThere = ConnectionService.EnsureThisComputer();
        Assert.NotEqual(thisComputerBefore, thisComputerThere.Id);

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, await Run(["import", file]));
        var report = _out.ToString();
        Assert.Contains($"= Verbindung „{thisComputerThere.Name}“ übersprungen: Gibt es hier schon als „{thisComputerThere.Name}“.", report);
        Assert.Contains("+ Verbindung „Server“ importiert: Passwort / Passphrase fehlt noch", report);
        Assert.Contains("+ Auftrag „Server-Backup“ importiert", report);
        Assert.Contains("3 importiert, 1 übersprungen, 0 abgelehnt.", report);

        var imported = JobService.Get(backup.Id);
        Assert.Equal(thisComputerThere.Id, imported.SourceConnectionId);
        Assert.Equal(server.Id, imported.TargetConnectionId);
        var serverThere = ConnectionService.Get(server.Id);
        Assert.False(serverThere.HasSecret);
        Assert.True(ConnectionService.NeedsSecret(serverThere));
        Assert.Equal("anna", serverThere.Get(ConnectionFields.UserName));

        // twice changes nothing
        _out.GetStringBuilder().Clear();
        Assert.Equal(0, await Run(["import", file, "--json"]));
        using var second = JsonDocument.Parse(_out.ToString());
        Assert.Equal(0, second.RootElement.GetProperty("imported").GetInt32());
        Assert.Equal(4, second.RootElement.GetProperty("skipped").GetInt32());
    }

    [Fact]
    public async Task Import_reports_what_the_checks_refuse_and_rejects_other_files()
    {
        var file = Path.Combine(_data.Folder, "export.json");
        var job = MakeJob("Fotos");
        Assert.Equal(0, await Run(["export", file]));

        using var other = new TestData();
        // a job of the same name already exists here (with another id): the import leaves its own out
        var mine = MakeJob("Fotos");
        Assert.NotEqual(job.Id, mine.Id);
        _out.GetStringBuilder().Clear();
        Assert.Equal(ExitCodes.BusinessError, await Run(["import", file]));
        Assert.Contains("! Auftrag „Fotos“ nicht importiert: Einen Auftrag mit diesem Namen gibt es schon.", _out.ToString());

        var notAnExport = Path.Combine(_data.Folder, "anders.json");
        await File.WriteAllTextAsync(notAnExport, """{ "hello": "world" }""");
        Assert.Equal(ExitCodes.BusinessError, await Run(["import", notAnExport]));
        Assert.Contains("kein Export von ThixSync", _error.ToString());

        // export without a file writes to the output
        _out.GetStringBuilder().Clear();
        Assert.Equal(0, await Run(["export"]));
        Assert.Equal("thixsync-configuration", JsonDocument.Parse(_out.ToString()).RootElement.GetProperty("format").GetString());
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------------

    private Task<int> Run(string[] args, Func<CliHost, CliHost>? configure = null)
    {
        var host = new CliHost { Out = _out, Error = _error, Now = () => new DateTimeOffset(2026, 11, 23, 14, 32, 0, TimeSpan.Zero) };
        return CliApp.RunAsync(args, configure?.Invoke(host) ?? host);
    }

    /// <summary>A runner that reports two progresses (one second apart) and ends the run with <paramref name="outcome"/>.</summary>
    private static CliHost FakeRunner(CliHost host, SyncJob job, RunOutcome outcome, Action<RunRequest>? onRequest = null)
    {
        Action<RunProgress>? subscriber = null;
        var clock = new DateTimeOffset(2026, 11, 23, 14, 32, 0, TimeSpan.Zero);
        return host with
        {
            Now = () => clock,
            SubscribeProgress = action =>
            {
                subscriber = action;
                return new Disposable();
            },
            RunAsync = (jobId, request, _) =>
            {
                onRequest?.Invoke(request);
                subscriber?.Invoke(Transferring(jobId));
                clock = clock.AddSeconds(1);
                subscriber?.Invoke(Transferring(jobId) with { FilesDone = 2000 });
                subscriber?.Invoke(Transferring(Guid.NewGuid()) with { FilesDone = 1 }); // another job: not shown
                return Task.FromResult(new SyncRun
                {
                    JobId = jobId,
                    JobName = job.Name,
                    Trigger = request.Trigger,
                    Outcome = outcome,
                    StartedAt = clock.AddMinutes(-1),
                    FinishedAt = clock,
                    FilesCopied = 1204,
                    BytesCopied = 3_435_973_837,
                    FilesScanned = 16_622,
                    Summary = "1.204 Dateien kopiert",
                });
            },
        };
    }

    private static RunProgress Transferring(Guid jobId) => new()
    {
        JobId = jobId,
        Phase = RunPhase.Transferring,
        FilesTotal = 8311,
        FilesDone = 1204,
        BytesTotal = 24_159_191_040,
        BytesDone = 3_435_973_837,
        BytesPerSecond = 50_541_363,
        FilesPerSecond = 120,
        Errors = 3,
        Elapsed = TimeSpan.FromSeconds(30),
        Remaining = TimeSpan.FromSeconds(135),
        Fraction = 0.142,
    };

    private SyncJob MakeJob(string name, SyncMode mode = SyncMode.TwoWay)
    {
        var thisComputer = ConnectionService.EnsureThisComputer();
        var source = Path.Combine(_data.Folder, $"quelle-{name}");
        var target = Path.Combine(_data.Folder, $"ziel-{name}");
        return JobService.Save(new SyncJob
        {
            Name = name,
            Mode = mode,
            SourceConnectionId = thisComputer.Id,
            TargetConnectionId = thisComputer.Id,
            Pairs = [new FolderPair { SourcePath = source, TargetPath = target }],
        });
    }

    private static Connection SaveSftp(string name, string password) =>
        ConnectionService.Save(new Connection
        {
            Name = name,
            Kind = ConnectionKind.Sftp,
            Settings = new Dictionary<string, string> { [ConnectionFields.Host] = "server.example.com", [ConnectionFields.UserName] = "anna" },
        }, password);

    private static SyncRun SaveRun(SyncJob job, RunOutcome outcome, long bytes = 0, int startedMinutesAgo = 10)
    {
        var started = AppClock.UtcNow.AddMinutes(-startedMinutesAgo);
        var run = new SyncRun
        {
            JobId = job.Id,
            JobName = job.Name,
            Trigger = RunTrigger.Schedule,
            Outcome = outcome,
            StartedAt = started,
            FinishedAt = started.AddSeconds(65),
            FilesCopied = 12,
            BytesCopied = bytes,
            Summary = "12 Dateien kopiert",
            DeviceName = AppInfo.MachineName,
        };
        SyncStore.SaveRun(run);
        return run;
    }

    private sealed class Disposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class FakeProvider(Func<IStorage> open) : IStorageProvider
    {
        public IReadOnlyList<ConnectionKind> Kinds { get; } = [ConnectionKind.Sftp];

        public IStorage Open(StorageContext context) => open();
    }
}
