using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MatDesktop;
using ThixSync.Controls;
using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Platform;
using ThixSync.Pages;

namespace ThixSync;

/// <summary>
/// The main window: Synchronisationen, Geräte, Zeitpläne, Protokolle, Einstellungen and About in one window (DESK-03/04). Forms and
/// details open on top of a section and "Zurück" (or Esc) returns to where they were opened. Closing the window only hides it:
/// ThixSync keeps running in the tray / menu bar (schedules, Remote). Keys: Ctrl/Cmd+1…4 the sections, +, the settings, +B fold the
/// menu, +N a new synchronisation, F5 runs the chosen one, Esc back.
/// </summary>
public partial class MainWindow : MatWindow
{
    private readonly JobsPage _jobs = new();
    private readonly Lazy<ConnectionsPage> _connections = new(() => new ConnectionsPage());
    private readonly Lazy<SchedulesPage> _schedules = new(() => new SchedulesPage());
    private readonly Lazy<ProtocolPage> _protocol = new(() => new ProtocolPage());
    private readonly Lazy<SettingsPage> _settings = new(() => new SettingsPage());
    private readonly Lazy<AboutPage> _about = new(() => new AboutPage());
    private readonly Lazy<JobForm> _jobForm = new(() => new JobForm());
    private readonly Lazy<JobDetailsPage> _jobDetails = new(() => new JobDetailsPage());
    private readonly Lazy<ConnectionForm> _connectionForm = new(() => new ConnectionForm());
    private readonly Lazy<RunPage> _run = new(() => new RunPage());
    private readonly Stack<(Control Page, MatNavItem? Nav)> _history = new();
    private readonly Avalonia.Threading.DispatcherTimer _sizeTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly MatNavItem _navJobs;
    private readonly MatNavItem _navConnections;
    private readonly MatNavItem _navSchedules;
    private readonly MatNavItem _navProtocol;
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
        _navJobs = new MatNavItem("refresh", "Synchronisationen", $"{command}+1") { IsCurrent = true };
        _navConnections = new MatNavItem("monitor", "Geräte", $"{command}+2");
        _navSchedules = new MatNavItem("calendar", "Zeitpläne", $"{command}+3");
        _navProtocol = new MatNavItem("file-text", "Protokolle", $"{command}+4");
        _navSettings = new MatNavItem("settings", "Einstellungen", $"{command}+,");
        Sidebar.Items.AddRange([_navJobs, _navConnections, _navSchedules, _navProtocol]);
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
        PageHost.Content = _jobs;

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

        _jobForm.Value.Closed = message =>
        {
            GoBack();
            if (message is not null)
            {
                _jobs.ShowMessage(message, InfoSeverity.Success);
            }
        };

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        RunMonitor.Start();
        RunMonitor.Changed += OnRuns;
        RunMonitor.RunsChanged += OnRuns;
        UpdateCoordinator.Checked += (_, _) => Ui.Post(ApplySidebar);
        CloudSyncService.StatusChanged += (_, _) => Ui.Post(RefreshCloudStatus);
        SettingsService.Changed += (_, _) => Ui.Post(RefreshCloudStatus);
        Closed += (_, _) =>
        {
            RunMonitor.Changed -= OnRuns;
            RunMonitor.RunsChanged -= OnRuns;
        };
        ApplySidebar();
        RefreshRunStatus();
    }

    /// <summary>Set by App when the application ends: then the window really closes.</summary>
    public bool IsExiting { get; set; }

    public bool IsSidebarCollapsed => Sidebar.IsFolded;

    public Button SidebarToggle => Sidebar.FoldButton;

    /// <summary>The version at the foot of the menu (<c>Version 1.0.0 · release</c>, SETUP-07): it opens About.</summary>
    public Button VersionButton => Sidebar.VersionButton;

    public TextBlock CloudText { get; private set; } = null!;

    /// <summary>The running jobs in the middle of the title bar ("2 Aufträge · 48,2 MB/s"); hidden while nothing runs.</summary>
    public Button RunStatusButton => RunStatus;

    public string RunStatusText => RunText.Text ?? "";

    public JobsPage JobsPage => _jobs;

    public ConnectionsPage ConnectionsPage => _connections.Value;

    public SchedulesPage SchedulesPage => _schedules.Value;

    public ProtocolPage ProtocolPage => _protocol.Value;

    public SettingsPage SettingsPage => _settings.Value;

    public JobForm JobForm => _jobForm.Value;

    public JobDetailsPage JobDetailsPage => _jobDetails.Value;

    public ConnectionForm ConnectionForm => _connectionForm.Value;

    public RunPage RunPage => _run.Value;

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

    // ---------------------------------------------------------------------------------------------------------------- sections

    /// <summary>"Synchronisationen" on a tab (<see cref="JobsPage.JobsTab"/>, <see cref="JobsPage.ActivityTab"/>, <see cref="JobsPage.OverviewTab"/>).</summary>
    public void ShowJobs(string? tab = null)
    {
        ShowSection(_navJobs, _jobs);
        if (tab is not null)
        {
            _jobs.SelectTab(tab);
        }
    }

    public void ShowActivity() => ShowJobs(JobsPage.ActivityTab);

    public void ShowConnections() => ShowSection(_navConnections, _connections.Value);

    public void ShowSchedules() => ShowSection(_navSchedules, _schedules.Value);

    /// <summary>"Protokolle", optionally of one job.</summary>
    public void ShowProtocol(Guid? jobId = null)
    {
        ShowSection(_navProtocol, _protocol.Value);
        if (jobId is not null)
        {
            _protocol.Value.FilterJob(jobId);
            _protocol.Value.Refresh();
        }
    }

    public void ShowSettings(string? tab = null)
    {
        ShowSection(_navSettings, _settings.Value);
        if (tab is not null)
        {
            _settings.Value.SelectTab(tab);
        }
    }

    public void ShowAbout() => ShowSection(null, _about.Value);

    // ---------------------------------------------------------------------------------------------------------------- pages on top

    /// <summary>The form of a job (null: a new one) on a tab, on top of the page shown now.</summary>
    public void ShowJobForm(Guid? jobId, string? tab = null)
    {
        _jobForm.Value.Load(jobId, tab);
        Push(_jobForm.Value);
    }

    /// <summary>The details of a job (Vorschau, Verlauf, Einstellungen); <paramref name="analyse"/> starts an analysis at once.</summary>
    public void ShowJobDetails(Guid jobId, string? tab = null, bool analyse = false)
    {
        _jobDetails.Value.Load(jobId, tab, analyse);
        Push(_jobDetails.Value);
    }

    /// <summary>The form of a connection (null: a new one, starting with the type picker); <paramref name="onSaved"/> gets the saved one.</summary>
    public void ShowConnectionForm(Guid? connectionId, Action<Connection>? onSaved = null, ConnectionKind? kind = null)
    {
        _connectionForm.Value.Load(connectionId, kind, onSaved);
        Push(_connectionForm.Value);
    }

    /// <summary>One run of the protocol.</summary>
    public void ShowRun(Guid runId)
    {
        _run.Value.Load(runId);
        Push(_run.Value);
    }

    /// <summary>Back to the page a form or detail was opened from (the start page when there is none).</summary>
    public void GoBack()
    {
        if (_history.Count == 0)
        {
            ShowJobs();
            return;
        }

        var (page, nav) = _history.Pop();
        Sidebar.Select(nav);
        PageHost.Content = page;
        if (page == _jobs)
        {
            _jobs.Refresh();
        }
    }

    private void ShowSection(MatNavItem? nav, Control page)
    {
        _history.Clear();
        Sidebar.Select(nav);
        PageHost.Content = page;
    }

    private void Push(Control page)
    {
        if (PageHost.Content is Control current && current != page)
        {
            _history.Push((current, Sidebar.Current));
        }

        PageHost.Content = page;
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
                ShowJobs();
                break;
            case Key.D2 when withCommand:
                e.Handled = true;
                ShowConnections();
                break;
            case Key.D3 when withCommand:
                e.Handled = true;
                ShowSchedules();
                break;
            case Key.D4 when withCommand:
                e.Handled = true;
                ShowProtocol();
                break;
            case Key.OemComma when withCommand:
                e.Handled = true;
                ShowSettings();
                break;
            case Key.B when withCommand:
                e.Handled = true;
                ToggleSidebar();
                break;
            case Key.N when withCommand && !AccessPolicy.IsConfigurationLocked:
                e.Handled = true;
                ShowJobForm(null);
                break;
            case Key.F5:
                e.Handled = true;
                RunChosen();
                break;
            case Key.Escape when OwnedWindows.Count == 0 && (_history.Count > 0 || PageHost.Content != _jobs):
                e.Handled = true;
                GoBack();
                break;
        }
    }

    /// <summary>F5: the job of the page shown (details, form) or the first card – runs now.</summary>
    private void RunChosen()
    {
        var jobId = PageHost.Content switch
        {
            JobDetailsPage details => details.JobId,
            JobsPage jobs when jobs.Cards.Count > 0 => jobs.Cards[0].JobId,
            _ => Guid.Empty,
        };
        if (jobId == Guid.Empty || RunMonitor.IsRunning(jobId))
        {
            return;
        }

        _ = JobCommands.RunAsync(this, jobId, _jobs.ShowMessage);
    }

    private void OnNavInvoked(MatNavItem item)
    {
        if (item == _navSettings)
        {
            ShowSettings();
        }
        else if (item == _navConnections)
        {
            ShowConnections();
        }
        else if (item == _navSchedules)
        {
            ShowSchedules();
        }
        else if (item == _navProtocol)
        {
            ShowProtocol();
        }
        else
        {
            ShowJobs();
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
        var status = CloudSyncService.Status;
        (CloudText.Text, var dot) = status.State switch
        {
            CloudState.Online => ($"Abgeglichen · {cloud.UserName}", "Mat.Success"),
            CloudState.Syncing => ("Gleiche ab …", "Mat.Accent"),
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

        var tip = status.LastSync is { } last ? $"{status.Message} (zuletzt {last.ToLocalTime():HH:mm:ss})" : status.Message;
        ToolTip.SetTip(CloudStatus, IsSidebarCollapsed ? $"{CloudText.Text}\n{tip}" : tip);
        AutomationProperties.SetName(CloudStatus, CloudText.Text);
    }

    private void OnRuns(object? sender, EventArgs e) => RefreshRunStatus();

    /// <summary>The running jobs in the middle of the title bar: how many, how fast together.</summary>
    private void RefreshRunStatus()
    {
        var running = RunMonitor.Running;
        RunStatus.IsVisible = running.Count > 0;
        if (running.Count == 0)
        {
            return;
        }

        var name = running.Count == 1 ? JobService.Find(running[0].JobId)?.Name ?? "1 Auftrag" : $"{running.Count} Aufträge";
        RunText.Text = $"{name} · {Formats.Speed(RunMonitor.TotalBytesPerSecond)}";
        ToolTip.SetTip(RunStatus, "Zur Aktivität");
    }

    private void OnRunStatusClick(object? sender, RoutedEventArgs e) => ShowActivity();
}
