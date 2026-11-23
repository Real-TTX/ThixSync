using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MatDesktop;
using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Platform;
using ThixSync.Pages;

namespace ThixSync;

/// <summary>
/// The main window: the map, the sources, the analyses, the settings and About in one window (DESK-03/04). Closing it only hides it:
/// ThixSync keeps running in the tray / menu bar (automatic analyses). Keys: Ctrl/Cmd+1/2/3 the sections, +, settings, +B fold the
/// menu, +N a new source, F5 analyse again, Esc back to the map.
/// </summary>
public partial class MainWindow : MatWindow
{
    private readonly MapPage _map = new();
    private readonly Lazy<SourcesPage> _sources = new(() => new SourcesPage());
    private readonly Lazy<AnalysesPage> _analyses = new(() => new AnalysesPage());
    private readonly Lazy<SettingsPage> _settings = new(() => new SettingsPage());
    private readonly Lazy<AboutPage> _about = new(() => new AboutPage());
    private readonly DispatcherTimer _sizeTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _scanTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly MatNavItem _navMap;
    private readonly MatNavItem _navSources;
    private readonly MatNavItem _navAnalyses;
    private readonly MatNavItem _navSettings;

    public MainWindow()
    {
        InitializeComponent();
        UseOwnTitleBar();
        var settings = SettingsService.Get();
        Width = settings.WindowWidth;
        Height = settings.WindowHeight;

        TitleBar.Logo = Ui.Logo;
        TitleBar.CloseRequested += (_, e) =>
        {
            e.Handled = true;
            Hide();
        };

        var command = Ui.CommandKey.Name;
        _navMap = new MatNavItem("grid", "Karte", $"{command}+1") { IsCurrent = true };
        _navSources = new MatNavItem("hard-drive", "Quellen", $"{command}+2");
        _navAnalyses = new MatNavItem("bar-chart", "Analysen", $"{command}+3");
        _navSettings = new MatNavItem("settings", "Einstellungen", $"{command}+,");
        Sidebar.Items.AddRange([_navMap, _navSources, _navAnalyses]);
        Sidebar.Settings = _navSettings;
        Sidebar.Version = AppInfo.Version;
        Sidebar.Channel = AppInfo.Channel;
        Sidebar.TitleBar = TitleBar;
        Sidebar.IsFolded = settings.IsSidebarCollapsed;
        Sidebar.FoldShortcut = $"{command}+B";
        Sidebar.ItemInvoked += (_, item) => OnNavInvoked(item);
        Sidebar.AboutRequested += (_, _) => ShowAbout();
        Sidebar.UpdateRequested += (_, _) => ShowAbout();
        Sidebar.FoldChanged += (_, folded) =>
        {
            SettingsService.Remember(current => current with { IsSidebarCollapsed = folded });
            ApplySidebar();
        };
        AddToMenuFoot();
        PageHost.Content = _map;

        _sizeTimer.Tick += (_, _) =>
        {
            _sizeTimer.Stop();
            if (WindowState == WindowState.Normal)
            {
                SettingsService.Remember(current => current with { WindowWidth = Width, WindowHeight = Height });
            }
        };
        Resized += (_, _) =>
        {
            _sizeTimer.Stop();
            _sizeTimer.Start();
        };

        _scanTimer.Tick += (_, _) => RefreshScanStatus();
        ScanService.Started += (_, _) => Ui.Post(() =>
        {
            RefreshScanStatus();
            _scanTimer.Start();
        });
        ScanService.Finished += (_, _) => Ui.Post(() =>
        {
            _scanTimer.Stop();
            RefreshScanStatus();
        });

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        UpdateCoordinator.Checked += (_, _) => Ui.Post(ApplySidebar);
        SyncService.StatusChanged += (_, _) => Ui.Post(RefreshCloudStatus);
        SettingsService.Changed += (_, _) => Ui.Post(RefreshCloudStatus);
        ApplySidebar();
    }

    /// <summary>Set by App when the application ends: then the window really closes.</summary>
    public bool IsExiting { get; set; }

    public bool IsSidebarCollapsed => Sidebar.IsFolded;

    public Button SidebarToggle => Sidebar.FoldButton;

    /// <summary>The version at the foot of the menu (<c>Version 1.0.0 · release</c>, SETUP-07): it opens About.</summary>
    public Button VersionButton => Sidebar.VersionButton;

    public TextBlock CloudText { get; private set; } = null!;

    public MapPage MapPage => _map;

    public SourcesPage SourcesPage => _sources.Value;

    public AnalysesPage AnalysesPage => _analyses.Value;

    public SettingsPage SettingsPage => _settings.Value;

    public object? CurrentPage => PageHost.Content;

    private Button CloudStatus { get; set; } = null!;

    private Ellipse CloudDot { get; set; } = null!;

    public void ToggleSidebar() => Sidebar.IsFolded = !Sidebar.IsFolded;

    /// <summary>Tray, second start, Dock: show the window in front.</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        WindowActivation.BringToFront(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
    }

    /// <summary>The map; of a source, an analysis and a comparison when given.</summary>
    public void ShowMap(Guid? sourceId = null, Guid? analysisId = null, Guid? compareWith = null)
    {
        Sidebar.Select(_navMap);
        PageHost.Content = _map;
        if (sourceId is { } id)
        {
            _map.ShowSource(id, analysisId, compareWith);
        }
    }

    public void ShowSources(bool createNew = false)
    {
        Sidebar.Select(_navSources);
        PageHost.Content = _sources.Value;
        if (createNew)
        {
            _sources.Value.ShowNew();
        }
        else
        {
            _sources.Value.ShowList();
        }
    }

    public void ShowAnalyses(Guid? sourceId = null)
    {
        Sidebar.Select(_navAnalyses);
        PageHost.Content = _analyses.Value;
        _analyses.Value.FilterSource(sourceId);
    }

    public void ShowSettings(string? tab = null)
    {
        Sidebar.Select(_navSettings);
        PageHost.Content = _settings.Value;
        if (tab is not null)
        {
            _settings.Value.SelectTab(tab);
        }
    }

    public void ShowAbout()
    {
        Sidebar.Select(null);
        PageHost.Content = _about.Value;
    }

    /// <summary>The tray's "Analysieren": the map of the source, and its analysis starts.</summary>
    public void ScanSource(Guid sourceId) => _ = ScanSourceAsync(sourceId);

    public async Task ScanSourceAsync(Guid sourceId)
    {
        ShowMap(sourceId);
        await _map.ScanAsync(sourceId);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // The X (and Alt+F4) only hides: ThixSync keeps running in the tray / menu bar.
        if (!IsExiting)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var withCommand = e.KeyModifiers.HasFlag(Ui.CommandKey.Modifier);
        switch (e.Key)
        {
            case Key.D1 when withCommand:
                e.Handled = true;
                ShowMap();
                break;
            case Key.D2 when withCommand:
                e.Handled = true;
                ShowSources();
                break;
            case Key.D3 when withCommand:
                e.Handled = true;
                ShowAnalyses();
                break;
            case Key.OemComma when withCommand:
                e.Handled = true;
                ShowSettings();
                break;
            case Key.B when withCommand:
                e.Handled = true;
                ToggleSidebar();
                break;
            case Key.N when withCommand:
                e.Handled = true;
                ShowSources(createNew: true);
                break;
            case Key.Escape when OwnedWindows.Count == 0 && PageHost.Content != _map && e.Source is not TextBox:
                e.Handled = true;
                ShowMap();
                break;
        }
    }

    private void OnNavInvoked(MatNavItem item)
    {
        if (item == _navSettings)
        {
            ShowSettings();
        }
        else if (item == _navSources)
        {
            ShowSources();
        }
        else if (item == _navAnalyses)
        {
            ShowAnalyses();
        }
        else
        {
            ShowMap();
        }
    }

    /// <summary>The cloud state at the foot of the menu (above the version); a waiting update is MatNav's own link.</summary>
    private void AddToMenuFoot()
    {
        CloudDot = new Ellipse { Width = 8, Height = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -2, -1) };
        CloudText = new TextBlock { FontSize = MatTokens.Type.FsSm, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 170 };
        CloudStatus = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 0),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 9,
                Children = { new Panel { Children = { new MatIcon { Icon = "cloud", Size = 18 }, CloudDot } }, CloudText },
            },
        }.Themed("MatButton.Ghost");
        CloudStatus.Click += (_, _) => ShowSettings(SettingsPage.CloudTab);
        Sidebar.Foot = new StackPanel { Spacing = 10, Margin = new Thickness(0, 12, 0, 10), Children = { CloudStatus } };
    }

    private void ApplySidebar()
    {
        var folded = IsSidebarCollapsed;
        CloudText.IsVisible = !folded;
        CloudStatus.HorizontalContentAlignment = folded ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        Sidebar.UpdateVersion = UpdateService.LastResult is { IsUpdateAvailable: true, Manifest: { } manifest } ? manifest.Version : null;
        RefreshCloudStatus();
    }

    private void RefreshCloudStatus()
    {
        var cloud = SettingsService.Get().Cloud;
        var status = SyncService.Status;
        (CloudText.Text, var dot) = status.State switch
        {
            CloudState.Online => ($"Synchronisiert · {cloud.UserName}", "Mat.Success"),
            CloudState.Syncing => ("Synchronisiere …", "Mat.Accent"),
            CloudState.Connecting => ("Verbinde …", "Mat.Accent"),
            CloudState.Offline => ("Cloud nicht erreichbar", "Mat.Warning"),
            CloudState.SignedOut => ("Abgemeldet – neu anmelden", "Mat.Danger"),
            _ => cloud.IsConnected ? ("Cloud verbunden", "Mat.Success") : ("Mit Cloud verbinden", (string?)null),
        };
        CloudDot.IsVisible = dot is not null;
        if (dot is not null)
        {
            CloudDot.Use(Shape.FillProperty, dot);
        }

        var tip = status.LastSync is { } last ? $"{status.Message} (zuletzt {last:HH:mm:ss})" : status.Message;
        ToolTip.SetTip(CloudStatus, IsSidebarCollapsed ? $"{CloudText.Text}\n{tip}" : tip);
        AutomationProperties.SetName(CloudStatus, CloudText.Text);
    }

    /// <summary>The running analysis in the middle of the title bar: its source, how far, how much so far.</summary>
    private void RefreshScanStatus()
    {
        if (ScanService.Current is not { } job)
        {
            ScanStatus.IsVisible = false;
            return;
        }

        var progress = job.Progress;
        ScanStatus.IsVisible = true;
        ScanProgress.IsIndeterminate = progress.ExpectedBytes is not > 0;
        if (progress.ExpectedBytes is { } expected and > 0)
        {
            ScanProgress.Value = Math.Clamp((double)progress.Bytes / expected, 0, 1);
        }

        ScanText.Text = $"Analysiere {job.Source.Name} · {Ui.FormatSize(progress.Bytes)} · {Ui.FormatCount(progress.Files)} Dateien";
        ToolTip.SetTip(ScanStatus, "Zur Karte");
    }

    private void OnScanStatusClick(object? sender, RoutedEventArgs e)
    {
        if (ScanService.Current is { } job)
        {
            ShowMap(job.SourceId);
        }
    }
}
