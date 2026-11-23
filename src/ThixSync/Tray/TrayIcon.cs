using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MatDesktop;
using ThixSync.Controls;
using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;

namespace ThixSync.Tray;

/// <summary>
/// The icon in the notification area (Windows) or the menu bar (macOS), with MatDesktop's tray (TRAY-01 … TRAY-04): the menu in the
/// order of every Mat tray – "ThixSync 1.0.0", the state in words, "ThixSync öffnen", ThixSync's own items (the jobs of this computer
/// to run now, a running job to stop, "Alle pausieren" / "Alle fortsetzen"), "Einstellungen …", "Log-Ordner öffnen", "Update
/// installieren …" while one waits, "Beenden". The state is the dot of the icon: ready green, running in the accent, paused amber.
/// Windows: a left click opens the window, the right button the menu; macOS: every click opens the menu.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int JobsInMenu = 8;

    private readonly App _app;
    private readonly MatTrayMenu _menu;
    private readonly MatTray _tray;
    private bool _updateAvailable;
    private bool _refreshPosted;

    public TrayIcon(App app)
    {
        _app = app;
        _menu = new MatTrayMenu(AppInfo.ProductName, AppInfo.Version, () => _app.ExitApplication())
        {
            Open = () => _app.ShowMainWindow(),
            Settings = () => _app.ShowMainWindow(window => window.ShowSettings()),
            LogFolder = () => Ui.OpenExternal(AppPaths.LogDirectory),
        };
        Update();
        _tray = new MatTray(app, _menu, Mark());
        _tray.MenuOpening += (_, _) => Update();
        SettingsService.Changed += OnChanged;
        JobService.Changed += OnChanged;
        RunMonitor.RunsChanged += OnChanged;
    }

    /// <summary>The model of the menu (MatTray builds the menu from it).</summary>
    public MatTrayMenu Menu => _menu;

    /// <summary>The state of the tray: ready, running (a state of ThixSync's own, TRAY-02) with the jobs in words, or everything paused.</summary>
    public static MatState State
    {
        get
        {
            var running = RunMonitor.Running;
            if (running.Count > 0)
            {
                return MatState.Custom(RunningText(running), "--mat-accent", MatStateMark.Dot);
            }

            return SchedulerService.IsPausedAll ? MatState.Custom("Alle Aufträge pausiert", "--mat-warning", MatStateMark.Ring) : MatState.Running;
        }
    }

    /// <summary>The mark the tray icons are drawn from: the logo on Windows, the template image (black on transparent) on macOS.</summary>
    public static Bitmap Mark(bool? template = null) =>
        new(AssetLoader.Open(new Uri((template ?? OperatingSystem.IsMacOS()) ? "avares://ThixSync/Assets/tray-macos.png" : "avares://ThixSync/Assets/logo-64.png")));

    public void SetUpdateAvailable(bool available)
    {
        _updateAvailable = available;
        Refresh();
    }

    /// <summary>A notification of ThixSync's own (TRAY-04: only for what the user did not cause); a click opens the window.</summary>
    public void ShowNotification(string title, string text, bool isWarning = false, Action<MainWindow>? navigate = null) =>
        MatNotification.Show(title, text, isWarning, () => _app.ShowMainWindow(navigate));

    public void Dispose()
    {
        SettingsService.Changed -= OnChanged;
        JobService.Changed -= OnChanged;
        RunMonitor.RunsChanged -= OnChanged;
        _tray.Dispose();
    }

    private static string RunningText(IReadOnlyList<RunProgress> running) => running.Count == 1
        ? $"Synchronisiert {JobService.Find(running[0].JobId)?.Name ?? "einen Auftrag"} …"
        : $"Synchronisiert {running.Count} Aufträge …";

    /// <summary>Something changed: the menu, the tooltip and (only when the state looks different) the icon follow; coalesced.</summary>
    private void OnChanged(object? sender, object? e)
    {
        if (_refreshPosted)
        {
            return;
        }

        _refreshPosted = true;
        Ui.Post(() =>
        {
            _refreshPosted = false;
            Refresh();
        });
    }

    private void Refresh()
    {
        Update();
        _tray.Refresh();
    }

    /// <summary>The model: the state, ThixSync's own items and whether an update waits.</summary>
    private void Update()
    {
        var running = RunMonitor.Running;
        _menu.State = State;
        _menu.TooltipText = running.Count > 0 ? $"{RunningText(running)} {Formats.Speed(RunMonitor.TotalBytesPerSecond)}" : "Dateien sichern und abgleichen";
        _menu.InstallUpdate = _updateAvailable ? () => _app.ShowMainWindow(window => window.ShowSettings(Pages.SettingsPage.UpdatesTab)) : null;
        _menu.ClearOwn();

        foreach (var progress in running)
        {
            var jobId = progress.JobId;
            _menu.AddOwn($"Anhalten: {MenuName(JobService.Find(jobId)?.Name ?? "Auftrag")}", () => RunMonitor.Cancel(jobId));
        }

        var jobs = JobService.List().Where(job => !RunMonitor.IsRunning(job.Id)).OrderBy(job => job.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _menu.AddOwn("Jetzt synchronisieren", null, enabled: false);
        if (jobs.Count == 0)
        {
            _menu.AddOwn("Noch keine Synchronisation", null, enabled: false);
        }

        foreach (var job in jobs.Take(JobsInMenu))
        {
            var jobId = job.Id;
            _menu.AddOwn($"   {MenuName(job.Name)}", () => RunNow(jobId), enabled: JobService.CanRunHere(job, out _));
        }

        if (jobs.Count > JobsInMenu)
        {
            _menu.AddOwn($"   Alle {jobs.Count} anzeigen …", () => _app.ShowMainWindow(window => window.ShowJobs()));
        }

        _menu.AddOwnSeparator();
        if (SchedulerService.IsPausedAll)
        {
            _menu.AddOwn("Alle fortsetzen", () =>
            {
                SchedulerService.ResumeAll();
                Refresh();
            });
        }
        else
        {
            _menu.AddOwn("Alle pausieren", () =>
            {
                SchedulerService.PauseAll();
                Refresh();
            });
        }
    }

    /// <summary>
    /// Runs a job from the menu. When it cannot start, a notification says why (the window may be hidden); how it ended is told by the
    /// app's notification of finished runs.
    /// </summary>
    private void RunNow(Guid jobId) => _ = RunNowAsync(jobId);

    private async Task RunNowAsync(Guid jobId)
    {
        var (_, error) = await RunMonitor.RunAsync(jobId, new RunRequest { Trigger = RunTrigger.Manual });
        if (error is not null)
        {
            ShowNotification(JobService.Find(jobId)?.Name ?? "ThixSync", error, isWarning: true, navigate: window => window.ShowJobs());
        }
    }

    private static string MenuName(string name) => name.Length <= 36 ? name : name[..35] + "…";
}
