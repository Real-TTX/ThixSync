using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using MatCloud.Contract;
using MatDesktop;
using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Platform;

namespace ThixSync;

/// <summary>
/// "ThixSync --snapshot &lt;folder&gt;": draws the windows of ThixSync into PNG files without a screen (Avalonia headless
/// with Skia, the real windows, themes and fonts of the machine), in light and dark, at 1x and their natural size – the
/// pictures of the design system and of docs/images, in MatDesktop's look. It works in a temporary data folder (Program sets it) filled with
/// the sample sources and analyses, with a pinned clock, version 1.0.0 on the release channel and a fixed computer name: no real
/// drive, tray icon, registry value, network or update check is touched.
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

    private static readonly string[] ThemedPictures =
    [
        "setup-welcome", "setup-options", "setup-progress", "setup-finish", "setup-update", "setup-uninstall",
        "main-map", "main-map-changes", "main-sources", "main-source", "main-analyses", "main-settings", "main-cloud", "main-cloud-code", "main-about", "main-folded", "toast",
    ];

    /// <summary>Every file a snapshot writes (the tray icons are the same in both themes).</summary>
    public static IReadOnlyList<string> FileNames { get; } =
    [
        .. ThemedPictures.SelectMany(name => Themes.Select(theme => $"{name}-{theme}.png")),
        "tray-icon-windows.png", "tray-icon-windows-scanning.png", "tray-icon-macos.png", "tray-icon-macos-scanning.png",
    ];

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
        SpaceStore.Close();
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
            AnalysisService.ResetForTests();
            SampleSpace.Create();
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
        window.ShowMap(SampleSpace.DriveId);
        Settle(window, waitLonger: true);

        // The map of C: compared with a week ago, the Videos folder chosen.
        var map = window.MapPage;
        if (map.Analysis is { } analysis && AnalysisService.LoadTree(analysis.Id) is { } tree && tree.FindByKey("Users/Anna/Videos") is { } videos)
        {
            map.SelectNode(videos);
        }

        Settle(window, waitLonger: true);
        Save(window, folder, $"main-map-{theme}.png", report);

        // The same map coloured by what changed, with the list of changes.
        map.SetColoring(MapColoring.ByChange);
        map.SelectListTab(2);
        Settle(window, waitLonger: true);
        Save(window, folder, $"main-map-changes-{theme}.png", report);
        map.SetColoring(MapColoring.ByType);
        map.SelectListTab(0);

        window.ShowSources();
        Settle(window);
        Save(window, folder, $"main-sources-{theme}.png", report);

        window.SourcesPage.ShowEdit(SampleSpace.NasId);
        Settle(window);
        Save(window, folder, $"main-source-{theme}.png", report);
        window.SourcesPage.ShowList();

        window.ShowAnalyses();
        Settle(window);
        Save(window, folder, $"main-analyses-{theme}.png", report);

        window.ShowSettings(Pages.SettingsPage.AnalysisTab);
        Settle(window);
        Save(window, folder, $"main-settings-{theme}.png", report);

        // The cloud tab: the sign-in in the browser first, then the code it shows while the person confirms it there (no network: the code is
        // a fixed one).
        window.ShowSettings(Pages.SettingsPage.CloudTab);
        var settingsPage = window.SettingsPage;
        settingsPage.ServerUrlBox.Text = CloudAddress;
        Settle(window);
        Save(window, folder, $"main-cloud-{theme}.png", report);
        settingsPage.ShowBrowserCode(new DeviceLinkStart("KXQ4-7MPA", $"{CloudAddress}/Connect?code=KXQ4-7MPA", "", 600, 2));
        Settle(window);
        Save(window, folder, $"main-cloud-code-{theme}.png", report);
        settingsPage.ShowSignInWay(password: false);
        Settle(window);

        // The about page tells this machine's system and folder: the pictures show a typical installation instead.
        window.ShowAbout();
        var about = (Pages.AboutPage)window.CurrentPage!;
        about.SystemText.Text = OperatingSystem.IsMacOS()
            ? $"macOS 15.5 ({UpdateService.PlatformKey}), .NET 10.0.0"
            : $"Microsoft Windows 10.0.26100 ({UpdateService.PlatformKey}), .NET 10.0.0";
        about.DataText.Text = OperatingSystem.IsMacOS() ? "/Users/anna/Library/Application Support/ThixSync" : DataFolder;
        Settle(window);
        Save(window, folder, $"main-about-{theme}.png", report);

        window.ShowMap();
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
        var toast = MatNotification.Show("Update verfügbar", "ThixSync 1.0.1 kann installiert werden (Einstellungen › Updates).");
        Settle(toast);
        Save(toast, folder, $"toast-{theme}.png", report);
        toast.Close();
        Settle(toast);
    }

    /// <summary>
    /// The tray / menu bar icons at 64 px, as MatDesktop draws them (TRAY-02): the mark with the dot of the state – running
    /// green, analysing in the accent – and on macOS the template image.
    /// </summary>
    private static void RenderTrayIcons(string folder, SnapshotReport report)
    {
        foreach (var (template, platform) in new[] { (false, "windows"), (true, "macos") })
        {
            foreach (var scanning in new[] { false, true })
            {
                var name = scanning ? $"tray-icon-{platform}-scanning.png" : $"tray-icon-{platform}.png";
                var state = scanning ? MatState.Custom("Analysiert System (C:) …", "--mat-accent", MatStateMark.Dot) : MatState.Running;
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
        AppClock.Pin(Moment);
        AppInfo.Pin(Version, Channel, buildNumber: 0, MachineName);
        AutostartService.UseInMemoryEntry(true);
        TokenStore.UseMemoryForTests(true);
        AccessPolicy.UsePolicyReaderForTests(_ => false);
        return new Restore(() =>
        {
            AccessPolicy.UsePolicyReaderForTests(null);
            TokenStore.UseMemoryForTests(false);
            AutostartService.UseInMemoryEntry(null);
            AppInfo.Pin(null);
            AppClock.Pin(null);
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
