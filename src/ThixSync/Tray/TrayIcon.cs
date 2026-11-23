using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MatDesktop;
using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;

namespace ThixSync.Tray;

/// <summary>
/// The icon in the notification area (Windows) or the menu bar (macOS), with MatDesktop's tray (TRAY-01 … TRAY-04):
/// the menu in the order of every Mat tray – "ThixSync 1.0.0", the state in words, "ThixSync öffnen", ThixSync's own items
/// (the sources of this computer to analyse, a running analysis to stop, sync now), "Einstellungen …", "Log-Ordner öffnen",
/// "Update installieren …" while one waits, "Beenden". The state is the dot of the icon: ready green, analysing in the accent.
/// Windows: a left click opens the window, the right button the menu; macOS: every click opens the menu.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int SourcesInMenu = 6;

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
        SourceService.Changed += OnChanged;
        ScanService.Started += OnScanStarted;
        ScanService.Finished += OnScanFinished;
    }

    /// <summary>The model of the menu (MatTray builds the menu from it).</summary>
    public MatTrayMenu Menu => _menu;

    /// <summary>The state of the tray: ready, or analysing (a state of ThixSync's own, TRAY-02) with the source in words.</summary>
    public static MatState State => ScanService.Current is { } job
        ? MatState.Custom($"Analysiert {job.Source.Name} …", "--mat-accent", MatStateMark.Dot)
        : MatState.Running;

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
        SourceService.Changed -= OnChanged;
        ScanService.Started -= OnScanStarted;
        ScanService.Finished -= OnScanFinished;
        _tray.Dispose();
    }

    private void OnScanStarted(object? sender, ScanJob job) => OnChanged(sender, job);

    private void OnScanFinished(object? sender, (ScanJob Job, Analysis? Analysis, string? Error) result) => OnChanged(sender, null);

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
        var settings = SettingsService.Get();
        _menu.State = State;
        _menu.TooltipText = ScanService.Current is { } job ? $"Analysiert {job.Source.Name} …" : "Speicher analysieren";
        _menu.InstallUpdate = _updateAvailable ? () => _app.ShowMainWindow(window => window.ShowSettings(Pages.SettingsPage.UpdatesTab)) : null;
        _menu.ClearOwn();

        if (ScanService.Current is { } running)
        {
            _menu.AddOwn("Analyse abbrechen", running.Cancel);
        }
        else
        {
            var sources = SourceService.List().Where(SourceService.CanScanHere).Take(SourcesInMenu).ToList();
            _menu.AddOwn("Analysieren", null);
            if (sources.Count == 0)
            {
                _menu.AddOwn("Noch keine Quelle", null);
            }

            foreach (var source in sources)
            {
                var id = source.Id;
                _menu.AddOwn(MenuText(source), () => _app.ShowMainWindow(window => window.ScanSource(id)));
            }
        }

        if (settings.Cloud.IsConnected)
        {
            _menu.AddOwnSeparator();
            _menu.AddOwn("Jetzt synchronisieren", SyncService.Kick);
        }
    }

    private static string MenuText(ScanSource source)
    {
        var latest = AnalysisService.Latest(source.Id);
        var name = source.Name.Length <= 36 ? source.Name : source.Name[..35] + "…";
        return latest is null ? name : $"{name} · {Ui.FormatSize(latest.TotalBytes)}";
    }
}
