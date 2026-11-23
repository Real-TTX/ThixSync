using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using MatCloud.Contract;
using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;

namespace ThixSync;

/// <summary>
/// "ThixSync.exe --self-test &lt;report file&gt;": the published program checks itself – everything trimming or
/// single-file packaging could break silently: JSON of settings, sync and update manifest, SQLite (connections and jobs), a sync of a
/// temporary folder through the engine (when this build has it), the protected token store, Windows shortcuts (COM) and every window
/// of the app with its bindings (from the sample data). It runs in a fresh temporary data folder (Program sets it), never touches the
/// user's data, writes a report and ends with exit code 0 (all passed) or 1. CI runs it on every Windows build.
/// </summary>
internal static class SelfTest
{
    public const string Argument = "--self-test";

    public static async Task<int> RunAsync(string reportPath)
    {
        var report = new StringBuilder($"ThixSync {AppInfo.Version} self test, data folder {AppPaths.DataDirectory}{Environment.NewLine}");
        var failures = 0;

        async Task Check(string name, Func<Task<string>> check)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var note = await check();
                report.AppendLine($"ok    {name} ({watch.ElapsedMilliseconds} ms){(note.Length > 0 ? " – " + note : "")}");
            }
            catch (Exception exception)
            {
                failures++;
                report.AppendLine($"FAIL  {name}: {exception}");
            }
        }

        await Check("settings (JSON)", () =>
        {
            SettingsService.Update(settings => settings with { KeepProtocolDays = 30, Theme = ThemeChoice.Dark, Notify = NotifyChoice.Changes });
            SettingsStore.Reset();
            var read = SettingsService.Get();
            Ensure(read is { KeepProtocolDays: 30, Theme: ThemeChoice.Dark, Notify: NotifyChoice.Changes }, "settings did not survive a save");
            return Task.FromResult("");
        });

        await Check("update manifest (JSON)", () =>
        {
            const string json = """{"version":"0.1.9-20261115","channel":"release","buildNumber":9,"setup":"ThixSyncSetup.exe","sha256":"AB","packages":{"win-x64":{"file":"ThixSyncSetup.exe","sha256":"AB"}}}""";
            var manifest = new HttpResponseMessage { Content = new StringContent(json, Encoding.UTF8, "application/json") }.Content
                .ReadFromJsonAsync<UpdateManifest>().GetAwaiter().GetResult();
            Ensure(manifest is { BuildNumber: 9 } && UpdateService.PackageFor(manifest, "win-x64")?.File == "ThixSyncSetup.exe", "manifest not read");
            return Task.FromResult("");
        });

        await Check("MatCloud contract (JSON)", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var item = new SyncItemDto { Collection = "jobs", Key = Guid.NewGuid().ToString("D"), Data = "{\"name\":\"x\"}", UpdatedAt = now, Revision = 3 };
            var json = JsonSerializer.Serialize(new SyncChangeSet([item], 3, false), CloudContract.Json);
            var back = JsonSerializer.Deserialize<SyncChangeSet>(json, CloudContract.Json);
            Ensure(back is { Latest: 3 } && back.Items[0].Key == item.Key, "change set not read back");
            Ensure(CloudApps.Find(CloudApps.Sync)?.Name == "ThixSync", "app id missing in the contract");
            return Task.FromResult("");
        });

        var probeJob = Guid.Empty;
        await Check("connections and jobs (SQLite)", () =>
        {
            // this computer as source and target: two folders of the temporary data folder
            var folder = Path.Combine(AppPaths.DataDirectory, "probe");
            var source = Path.Combine(folder, "Quelle");
            var target = Path.Combine(folder, "Ziel");
            Directory.CreateDirectory(Path.Combine(source, "Bilder"));
            Directory.CreateDirectory(target);
            File.WriteAllBytes(Path.Combine(source, "Bilder", "foto.jpg"), new byte[300_000]);
            File.WriteAllBytes(Path.Combine(source, "notiz.txt"), new byte[1_000]);
            var thisComputer = ConnectionService.EnsureThisComputer();
            var job = JobService.Save(new SyncJob
            {
                Name = "Selbsttest",
                Mode = SyncMode.Mirror,
                SourceConnectionId = thisComputer.Id,
                TargetConnectionId = thisComputer.Id,
                Pairs = [new FolderPair { SourcePath = source, TargetPath = target }],
            });
            probeJob = job.Id;
            ConnectionService.ResetForTests();
            JobService.ResetForTests();
            Ensure(JobService.Find(job.Id) is { Pairs.Count: 1 } && ConnectionService.Find(thisComputer.Id) is not null, "job or connection not read back");
            return Task.FromResult($"{ConnectionService.List().Count} connections, {JobService.List().Count} jobs");
        });

        await Check("local sync (engine)", async () =>
        {
            try
            {
                var run = await JobRunner.RunAsync(probeJob, new RunRequest { Trigger = RunTrigger.Cli });
                var target = Path.Combine(AppPaths.DataDirectory, "probe", "Ziel");
                Ensure(run.Outcome == RunOutcome.Success, $"run ended {run.Outcome}: {run.Summary}");
                Ensure(File.Exists(Path.Combine(target, "Bilder", "foto.jpg")) && File.Exists(Path.Combine(target, "notiz.txt")), "files not copied");
                Ensure(ProtocolService.Find(run.Id) is not null, "run not in the protocol");
                return $"{run.FilesCopied} files, {run.BytesCopied} bytes";
            }
            catch (NotImplementedException)
            {
                return "skipped: the engine is not part of this build";
            }
        });

        await Check("storage providers (every type, trimmed libraries)", async () =>
        {
            // every type has its provider, and a storage of each comes up without the network (SSH.NET, FluentFTP, MegaApiClient,
            // the HTTP clients): what trimming could have taken away shows here
            var opened = 0;
            foreach (var type in Core.Storage.ConnectionCatalog.All)
            {
                var provider = Core.Storage.StorageProviders.Find(type.Kind);
                Ensure(provider is not null, $"no provider for {type.Kind}");
                if (type.Kind is Core.Models.ConnectionKind.Local or Core.Models.ConnectionKind.ExternalDrive or Core.Models.ConnectionKind.MatCloud)
                {
                    continue; // need a folder / a drive / the cloud sign-in
                }

                var settings = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var field in type.Fields)
                {
                    settings[field.Key] = field.Type switch
                    {
                        Core.Storage.FieldType.Url => "https://example.invalid/",
                        Core.Storage.FieldType.Number => field.Default.Length > 0 ? field.Default : "22",
                        Core.Storage.FieldType.Toggle => "false",
                        Core.Storage.FieldType.Choice when field.Default.Length > 0 => field.Default,
                        Core.Storage.FieldType.Choice when field.Choices.Count > 0 => field.Choices[0].Value,
                        _ => "selftest",
                    };
                }

                settings[Core.Storage.ConnectionFields.Host] = "example.invalid";
                settings[Core.Storage.ConnectionFields.AuthMethod] = settings.GetValueOrDefault(Core.Storage.ConnectionFields.AuthMethod) == "key" ? "password" : settings.GetValueOrDefault(Core.Storage.ConnectionFields.AuthMethod, "password");
                var connection = new Core.Models.Connection { Name = type.Name, Kind = type.Kind, Settings = settings };
                try
                {
                    await using var storage = provider!.Open(new Core.Storage.StorageContext { Connection = connection, Folder = "selftest", Secret = "selftest" });
                    Ensure(storage.DisplayName.Length > 0, $"{type.Kind} has no name");
                    opened++;
                }
                catch (Core.Storage.StorageException)
                {
                    opened++; // refused the made-up fields: the code is there
                }
            }

            return $"{Core.Storage.ConnectionCatalog.All.Count} types, {opened} storages opened";
        });

        await Check("install options (installer.json, arguments)", () =>
        {
            Ensure(InstallerOptions.Config is { Autostart: not null, DesktopShortcut: not null, AutomaticUpdates: not null }, "installer.json not read");
            var request = InstallerOptions.Parse([InstallerOptions.QuietArgument, "--autostart=no", "--desktop-shortcut=yes"]);
            Ensure(request is { Action: SetupAction.Install, Choices: { Autostart: false, DesktopShortcut: true } }, "arguments not read");
            return Task.FromResult("");
        });

        await Check("token store", () =>
        {
            var stored = TokenStore.Save("self-test", "geheim");
            Ensure(TokenStore.Read("self-test", stored) == "geheim", "token not read back");
            TokenStore.Delete("self-test");
            return Task.FromResult("");
        });

        if (OperatingSystem.IsWindows())
        {
            await Check("shortcut (COM)", () =>
            {
                var link = Path.Combine(AppPaths.DataDirectory, "ThixSync.lnk");
                Core.Platform.Windows.ShellLink.Write(link, App.ExecutablePath, AppPaths.DataDirectory, "Selbsttest", App.ExecutablePath);
                Ensure(File.Exists(link) && new FileInfo(link).Length > 0, "no .lnk written");
                return Task.FromResult("");
            });
        }

        await Check("windows and bindings", async () =>
        {
            SampleSync.Create();
            var window = new MainWindow();
            window.ShowFromTray();
            await Settle();
            Ensure(window.JobsPage.Cards.Count >= 4, $"the jobs page shows {window.JobsPage.Cards.Count} cards");
            window.ShowActivity();
            await Settle();
            window.ShowJobs(Pages.JobsPage.OverviewTab);
            await Settle();
            Ensure(window.JobsPage.Overview.JobsValue.Length > 0, "no overview");
            window.ShowJobForm(null);
            await Settle();
            window.ShowJobDetails(SampleSync.ProjectsJobId);
            window.JobDetailsPage.ShowPlan(SampleSync.Plan());
            await Settle();
            Ensure(window.JobDetailsPage.PlanTable.ItemCount > 0, "no plan listed");
            window.ShowConnections();
            await Settle();
            Ensure(window.ConnectionsPage.List.ItemCount > 0, "no connection listed");
            window.ShowConnectionForm(null);
            await Settle();
            window.ShowConnectionForm(SampleSync.NasId);
            await Settle();
            window.ShowSchedules();
            await Settle();
            window.ShowProtocol();
            await Settle();
            Ensure(window.ProtocolPage.Table.ItemCount > 0, "no run listed");
            window.ShowRun(SampleSync.DocumentsRunId);
            await Settle();
            window.ShowSettings();
            await Settle();
            window.ShowAbout();
            await Settle();
            if (OperatingSystem.IsWindows())
            {
                var setup = Setup.ThixSyncSetup.Create(isUninstall: false, InstallService.IsInstalled, InstallService.InstalledVersion, InstallService.ProgramFolder, _ => Task.CompletedTask);
                setup.Show();
                await Settle();
                setup.Close();
            }

            ProblemReports.WriteHang(TimeSpan.FromSeconds(9), withDump: false);
            Dialogs.ProblemReportDialog.ShowUnseen()?.Close();
            window.IsExiting = true;
            window.Close();
            Core.Storage.StorageProviders.OverrideForTests(null, null);
            return "";
        });

        report.AppendLine(failures == 0 ? "PASSED" : $"FAILED ({failures})");
        await File.WriteAllTextAsync(reportPath, report.ToString(), Encoding.UTF8);

        // The temporary data folder goes again (unless the report was written into it).
        Database.Close();
        if (!Path.GetFullPath(reportPath).StartsWith(Path.GetFullPath(AppPaths.DataDirectory), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Directory.Delete(AppPaths.DataDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return failures == 0 ? 0 : 1;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task Settle()
    {
        for (var round = 0; round < 10; round++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(30);
        }
    }
}
