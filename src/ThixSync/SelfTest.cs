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
/// single-file packaging could break silently: JSON of settings, sync and update manifest, SQLite, the
/// protected token store, the scanner and the store of the analyses, the treemap, Windows shortcuts (COM) and the windows of the app with their
/// bindings. It runs in a fresh temporary data folder (Program sets it), never touches the user's data, writes a report
/// and ends with exit code 0 (all passed) or 1. CI runs it on every Windows build.
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
            SettingsService.Update(settings => settings with { KeepAnalyses = 10, Theme = ThemeChoice.Dark });
            SettingsStore.Reset();
            var read = SettingsService.Get();
            Ensure(read is { KeepAnalyses: 10, Theme: ThemeChoice.Dark }, "settings did not survive a save");
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
            var item = new SyncItemDto { Collection = "sources", Key = Guid.NewGuid().ToString("D"), Data = "{\"name\":\"x\"}", UpdatedAt = now, Revision = 3 };
            var json = JsonSerializer.Serialize(new SyncChangeSet([item], 3, false), CloudContract.Json);
            var back = JsonSerializer.Deserialize<SyncChangeSet>(json, CloudContract.Json);
            Ensure(back is { Latest: 3 } && back.Items[0].Key == item.Key, "change set not read back");
            Ensure(CloudApps.Find(CloudApps.Space)?.Name == "ThixSync", "app id missing in the contract");
            return Task.FromResult("");
        });

        await Check("analysis (scanner, SQLite, tree, comparison)", async () =>
        {
            // a small folder of its own inside the temporary data folder: analysed twice, the second time with a new file
            var folder = Path.Combine(AppPaths.DataDirectory, "probe");
            Directory.CreateDirectory(Path.Combine(folder, "Bilder"));
            await File.WriteAllBytesAsync(Path.Combine(folder, "Bilder", "foto.jpg"), new byte[300_000]);
            await File.WriteAllBytesAsync(Path.Combine(folder, "notiz.txt"), new byte[1_000]);
            var source = SourceService.Create(new SourceInput("Selbsttest", SourceKind.Local, folder));
            var first = await ScanService.ScanAsync(source.Id) ?? throw new InvalidOperationException("first analysis cancelled");
            Ensure(first is { FileCount: 2, TotalBytes: 301_000 }, $"counted {first.FileCount} files, {first.TotalBytes} bytes");
            await File.WriteAllBytesAsync(Path.Combine(folder, "Bilder", "neu.png"), new byte[50_000]);
            var second = await ScanService.ScanAsync(source.Id) ?? throw new InvalidOperationException("second analysis cancelled");
            AnalysisService.ResetForTests(); // read the trees back from SQLite
            var comparison = AnalysisService.Compare(second, first) ?? throw new InvalidOperationException("no comparison");
            var tree = AnalysisService.LoadTree(second.Id) ?? throw new InvalidOperationException("tree not stored");
            var change = comparison.ChangeOf(tree.FindByKey("Bilder")!);
            Ensure(change is { Kind: ChangeKind.Grown, Delta: 50_000 }, $"change {change}");
            Ensure(TreemapLayout.Layout(tree, 800, 500).Count >= 4, "no map laid out");
            return $"{AnalysisService.List().Count} analyses";
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
            var window = new MainWindow();
            window.ShowFromTray();
            await Settle();
            for (var wait = 0; wait < 50 && (window.MapPage.Analysis is null || window.MapPage.Map.Tiles.Count == 0); wait++)
            {
                await Settle();
            }

            Ensure(window.MapPage.Analysis is not null && window.MapPage.Map.Tiles.Count > 0, $"the map shows no analysis ({window.MapPage.Analysis?.SourceName ?? "none"}, {window.MapPage.Map.Tiles.Count} tiles, map {window.MapPage.Map.Bounds.Size}, visible {window.MapPage.Map.IsEffectivelyVisible}, root {window.MapPage.MapRoot?.Size})");
            window.ShowSources();
            await Settle();
            Ensure(window.SourcesPage.List.ItemCount > 0, "no source listed");
            window.ShowSources(createNew: true);
            await Settle();
            window.ShowAnalyses();
            await Settle();
            Ensure(window.AnalysesPage.List.ItemCount > 0, "no analysis listed");
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
            return "";
        });

        report.AppendLine(failures == 0 ? "PASSED" : $"FAILED ({failures})");
        await File.WriteAllTextAsync(reportPath, report.ToString(), Encoding.UTF8);

        // The temporary data folder goes again (unless the report was written into it).
        SpaceStore.Close();
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
