using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using MatDesktop;
using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Remote;
using ThixSync.Pages;

namespace ThixSync;

/// <summary>
/// "ThixSync --snapshot &lt;folder&gt;": draws the windows of ThixSync into PNG files without a screen (Avalonia headless with Skia, the
/// real windows, themes and fonts of the machine), in light and dark, at 1x and their natural size – the pictures of docs/images and of
/// ThixLauncher's store, in MatDesktop's look. It works in a temporary data folder (Program sets it) filled with <see cref="SampleSync"/>
/// (connections of every group, the four jobs of the mockup, two weeks of runs), with a pinned clock, version 1.0.0 on the release
/// channel and a fixed computer name; the running state and the plan with conflicts are shown without an engine. No real drive, tray
/// icon, registry value, network or update check is touched.
/// </summary>
internal static class Snapshot
{
    public const string Argument = "--snapshot";

    /// <summary>What every picture shows as version, channel and computer.</summary>
    public const string Version = "1.0.0";

    public const string Channel = "release";

    public const string MachineName = "ANNA-PC";

    /// <summary>"Now" in every picture (local time): Thursday, 12 November 2026, 14:32.</summary>
    public static readonly DateTimeOffset Moment = new(new DateTime(2026, 11, 12, 14, 32, 0, DateTimeKind.Local));

    private const string ProgramFolder = @"C:\Users\Anna\AppData\Local\Programs\ThixSync";
    private const string DataFolder = @"C:\Users\Anna\AppData\Local\ThixSync";
    private const string CloudAddress = "https://cloud.example.com";

    private static readonly string[] Themes = ["light", "dark"];

    private static readonly string[] JobFormTabs = [JobForm.GeneralTab, JobForm.PairsTab, JobForm.FilterTab, JobForm.ScheduleTab, JobForm.OptionsTab];

    private static readonly string[] SettingsTabs = [SettingsPage.GeneralTab, SettingsPage.TransferTab, SettingsPage.RemoteTab, SettingsPage.CloudTab, SettingsPage.UpdatesTab, SettingsPage.AdvancedTab];

    private static readonly string[] ThemedPictures =
    [
        "setup-welcome", "setup-options", "setup-progress", "setup-finish", "setup-update", "setup-uninstall",
        "main-jobs", "main-jobs-running", "main-activity", "main-overview",
        .. JobFormTabs.Select(tab => $"main-job-form-{tab}"),
        "main-job-details", "main-job-history", "main-connections", "main-connection-types", "main-connection-form", "main-schedules", "main-protocol", "main-run",
        .. SettingsTabs.Select(tab => $"main-settings-{tab}"),
        "main-about", "main-folded", "toast",
    ];

    /// <summary>Every file a snapshot writes (the tray icons are the same in both themes).</summary>
    public static IReadOnlyList<string> FileNames { get; } =
    [
        .. ThemedPictures.SelectMany(name => Themes.Select(theme => $"{name}-{theme}.png")),
        "tray-icon-windows.png", "tray-icon-windows-running.png", "tray-icon-macos.png", "tray-icon-macos-running.png",
    ];

    /// <summary>True while the pictures are drawn (a page shows the state of a service that does not run here, e.g. Remote listening).</summary>
    public static bool IsDrawing { get; private set; }

    /// <summary>The command: sets Avalonia up headless, draws every picture into the folder, ends with 0 (all written) or 1.</summary>
    public static int Run(string[] args)
    {
        var index = Array.IndexOf(args, Argument);
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"Usage: ThixSync {Argument} <folder>");
            return 2;
        }

        var folder = Path.GetFullPath(args[index + 1]);
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        AvaloniaSynchronizationContext.InstallIfNeeded();

        int exitCode;
        try
        {
            var report = Render(folder);
            foreach (var file in report.Written)
            {
                Console.WriteLine($"written  {Path.Combine(folder, file)}");
            }

            foreach (var (file, reason) in report.Skipped)
            {
                Console.WriteLine($"skipped  {file}: {reason}");
            }

            exitCode = report.Written.Count + report.Skipped.Count == FileNames.Count ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Snapshot failed: {exception}");
            exitCode = 1;
        }

        // The temporary data folder goes again.
        Database.Close();
        try
        {
            Directory.Delete(AppPaths.DataDirectory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return exitCode;
    }

    /// <summary>
    /// Draws every picture into <paramref name="folder"/> (Avalonia must run headless, the data folder must be a fresh
    /// one: the command and the UI tests take care of both). Existing files of the same names are replaced.
    /// </summary>
    public static SnapshotReport Render(string folder)
    {
        Directory.CreateDirectory(folder);
        var report = new SnapshotReport();
        var previousTheme = Application.Current!.RequestedThemeVariant;
        using (Isolate())
        {
            ConnectionService.ResetForTests();
            JobService.ResetForTests();
            SampleSync.Create();
            foreach (var theme in Themes)
            {
                Application.Current.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
                RenderSetup(folder, theme, report);
                RenderMainWindow(folder, theme, report);
                RenderToast(folder, theme, report);
            }

            RenderTrayIcons(folder, report);
        }

        Application.Current.RequestedThemeVariant = previousTheme;
        return report;
    }

    private static void RenderSetup(string folder, string theme, SnapshotReport report)
    {
        foreach (var page in Enum.GetValues<SetupSnapshotPage>())
        {
            var name = $"setup-{page.ToString().ToLowerInvariant()}";
            if (!OperatingSystem.IsWindows())
            {
                report.Skipped.Add(($"{name}-{theme}.png", "the setup assistant exists on Windows only (macOS: the app bundle and the dmg)"));
                continue;
            }

            RenderSetupPage(folder, $"{name}-{theme}.png", page, report);
        }
    }

    /// <summary>
    /// One page of the setup with fixed values (MatSetupWindow, ThixSyncSetup.Create): nothing is installed, removed or asked
    /// from the system. Progress shows the steps of an installation two thirds along (the running bar would show wherever its
    /// animation happens to be), Finish the end of one that worked.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RenderSetupPage(string folder, string file, SetupSnapshotPage page, SnapshotReport report)
    {
        // An update brings 1.0.0 over 0.9.0; the uninstall removes the installed 1.0.0.
        var installed = page == SetupSnapshotPage.Update ? "0.9.0" : Version;
        var hold = new TaskCompletionSource();
        var window = Setup.ThixSyncSetup.Create(page == SetupSnapshotPage.Uninstall, page == SetupSnapshotPage.Update, installed, ProgramFolder, async run =>
        {
            if (page != SetupSnapshotPage.Progress)
            {
                return;
            }

            run.Progress.Report(new MatSetupReport($"Installiere ThixSync nach {ProgramFolder}", MatSetupStepState.Done));
            run.Progress.Report(new MatSetupReport("Programmdatei kopiert", MatSetupStepState.Done));
            run.Progress.Report(new MatSetupReport("Startmenü-Eintrag angelegt", MatSetupStepState.Done));
            run.Progress.Report(new MatSetupReport("Autostart bei der Windows-Anmeldung eingerichtet", MatSetupStepState.Running, 65));
            await hold.Task;
        });
        window.Show();
        Settle(window);
        if (page == SetupSnapshotPage.Options)
        {
            window.GoTo(MatSetupStepKind.Options);
        }
        else if (page is SetupSnapshotPage.Progress or SetupSnapshotPage.Finish)
        {
            _ = window.RunAsync();
        }

        Settle(window);
        Save(window, folder, file, report);
        hold.TrySetResult();
        Settle(window);
        window.Close();
    }

    private static void RenderMainWindow(string folder, string theme, SnapshotReport report)
    {
        var settings = SettingsService.Get();
        var window = new MainWindow();
        window.ShowFromTray();
        window.Width = settings.WindowWidth;
        window.Height = settings.WindowHeight;

        // Synchronisationen: the cards like the mockup, then two jobs running (the newest first) and their live panels
        window.ShowJobs(JobsPage.JobsTab);
        Settle(window, waitLonger: true);
        Save(window, folder, $"main-jobs-{theme}.png", report);

        foreach (var progress in SampleSync.RunningProgress())
        {
            RunMonitor.Show(progress);
        }

        window.JobsPage.SortChoice.SelectedIndex = 1;
        Settle(window, waitLonger: true);
        Save(window, folder, $"main-jobs-running-{theme}.png", report);
        window.JobsPage.SortChoice.SelectedIndex = 0;

        window.ShowActivity();
        Settle(window, waitLonger: true);
        Save(window, folder, $"main-activity-{theme}.png", report);
        RunMonitor.Clear();

        window.ShowJobs(JobsPage.OverviewTab);
        Settle(window, waitLonger: true);
        Save(window, folder, $"main-overview-{theme}.png", report);
        window.ShowJobs(JobsPage.JobsTab);

        // The form of "Dokumente & Fotos", every tab
        foreach (var tab in JobFormTabs)
        {
            window.ShowJobs();
            window.ShowJobForm(SampleSync.DocumentsJobId, tab);
            Settle(window);
            Save(window, folder, $"main-job-form-{tab}-{theme}.png", report);
        }

        // The details of "Projekte NAS": the preview of an analysis with conflicts, then its history
        window.ShowJobs();
        window.ShowJobDetails(SampleSync.ProjectsJobId);
        window.JobDetailsPage.ShowPlan(SampleSync.Plan());
        Settle(window);
        Save(window, folder, $"main-job-details-{theme}.png", report);
        window.ShowJobs();
        window.ShowJobDetails(SampleSync.ProjectsJobId, JobDetailsPage.HistoryTab);
        Settle(window);
        Save(window, folder, $"main-job-history-{theme}.png", report);

        // Geräte: the list, the type picker, the form of the NAS
        window.ShowConnections();
        Settle(window);
        Save(window, folder, $"main-connections-{theme}.png", report);
        window.ShowConnectionForm(null);
        Settle(window);
        Save(window, folder, $"main-connection-types-{theme}.png", report);
        window.ShowConnections();
        window.ShowConnectionForm(SampleSync.NasId);
        Settle(window);
        Save(window, folder, $"main-connection-form-{theme}.png", report);

        window.ShowSchedules();
        Settle(window);
        Save(window, folder, $"main-schedules-{theme}.png", report);

        window.ShowProtocol();
        Settle(window);
        Save(window, folder, $"main-protocol-{theme}.png", report);
        window.ShowRun(SampleSync.DocumentsRunId);
        Settle(window);
        Save(window, folder, $"main-run-{theme}.png", report);

        // Einstellungen, every tab: Remote with a pairing code, Cloud with the address typed
        var settingsPage = window.SettingsPage;
        settingsPage.ServerUrlBox.Text = CloudAddress;
        foreach (var tab in SettingsTabs)
        {
            window.ShowSettings(tab);
            if (tab == SettingsPage.RemoteTab)
            {
                settingsPage.ShowPairingCode(new PairingCode("482 913", Moment.AddMinutes(5)));
            }

            // the data folder of a typical installation, not the temporary one of the pictures
            settingsPage.DataFolderRow.Description = OperatingSystem.IsMacOS() ? "/Users/anna/Library/Application Support/ThixSync" : DataFolder;

            Settle(window);
            Save(window, folder, $"main-settings-{tab}-{theme}.png", report);
        }

        // The about page tells this machine's system and folder: the pictures show a typical installation instead.
        window.ShowAbout();
        var about = (AboutPage)window.CurrentPage!;
        about.SystemText.Text = OperatingSystem.IsMacOS()
            ? $"macOS 15.5 ({UpdateService.PlatformKey}), .NET 10.0.0"
            : $"Microsoft Windows 10.0.26100 ({UpdateService.PlatformKey}), .NET 10.0.0";
        about.DataText.Text = OperatingSystem.IsMacOS() ? "/Users/anna/Library/Application Support/ThixSync" : DataFolder;
        Settle(window);
        Save(window, folder, $"main-about-{theme}.png", report);

        window.ShowJobs();
        window.ToggleSidebar();
        Settle(window, waitLonger: true);
        Save(window, folder, $"main-folded-{theme}.png", report);
        window.ToggleSidebar();
        Settle(window);

        window.IsExiting = true;
        window.Close();
    }

    private static void RenderToast(string folder, string theme, SnapshotReport report)
    {
        var toast = MatNotification.Show("Fotos Backup: Fehlgeschlagen", "Ziel nicht erreichbar: der Bucket antwortet nicht (Zeitüberschreitung).", isWarning: true);
        Settle(toast);
        Save(toast, folder, $"toast-{theme}.png", report);
        toast.Close();
        Settle(toast);
    }

    /// <summary>
    /// The tray / menu bar icons at 64 px, as MatDesktop draws them (TRAY-02): the mark with the dot of the state – ready green,
    /// running in the accent – and on macOS the template image.
    /// </summary>
    private static void RenderTrayIcons(string folder, SnapshotReport report)
    {
        foreach (var (template, platform) in new[] { (false, "windows"), (true, "macos") })
        {
            foreach (var running in new[] { false, true })
            {
                var name = running ? $"tray-icon-{platform}-running.png" : $"tray-icon-{platform}.png";
                var state = running ? MatState.Custom("Synchronisiert 2 Aufträge …", "--mat-accent", MatStateMark.Dot) : MatState.Running;
                using var mark = Tray.TrayIcon.Mark(template);
                File.WriteAllBytes(Path.Combine(folder, name), MatTrayIcons.Png(mark, state, template));
                report.Written.Add(name);
            }
        }
    }

    /// <summary>Lets the dispatcher, the layout and the render timer run; longer for the background image decoding and animations.</summary>
    private static void Settle(TopLevel topLevel, bool waitLonger = false)
    {
        for (var round = 0; round < (waitLonger ? 40 : 8); round++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(waitLonger ? 40 : 15);
        }

        Dispatcher.UIThread.RunJobs();
        topLevel.UpdateLayout();
    }

    private static void Save(TopLevel topLevel, string folder, string name, SnapshotReport report)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = topLevel.CaptureRenderedFrame() ?? throw new InvalidOperationException($"{name}: no frame was rendered");
        frame.Save(Path.Combine(folder, name));
        report.Written.Add(name);
    }

    /// <summary>Pins clock, version and computer name and keeps tokens, policies and autostart in memory.</summary>
    private static IDisposable Isolate()
    {
        IsDrawing = true;
        AppClock.Pin(Moment);
        AppInfo.Pin(Version, Channel, buildNumber: 0, MachineName);
        AutostartService.UseInMemoryEntry(true);
        TokenStore.UseMemoryForTests(true);
        AccessPolicy.UsePolicyReaderForTests(_ => false);
        return new Restore(() =>
        {
            RunMonitor.Clear();
            Core.Storage.StorageProviders.OverrideForTests(null, null);
            AccessPolicy.UsePolicyReaderForTests(null);
            TokenStore.UseMemoryForTests(false);
            AutostartService.UseInMemoryEntry(null);
            AppInfo.Pin(null);
            AppClock.Pin(null);
            IsDrawing = false;
        });
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

/// <summary>What a snapshot wrote (file names) and what it left out, with the reason.</summary>
internal sealed class SnapshotReport
{
    public List<string> Written { get; } = [];

    public List<(string File, string Reason)> Skipped { get; } = [];
}

/// <summary>The pages of the setup that "ThixSync --snapshot" draws (setup-welcome ... setup-uninstall).</summary>
internal enum SetupSnapshotPage
{
    Welcome,
    Options,
    Progress,
    Finish,
    Update,
    Uninstall,
}
