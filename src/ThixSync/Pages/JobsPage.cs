using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MatDesktop;
using ThixSync.Controls;
using ThixSync.Core.Business;
using ThixSync.Core.Models;
using ThixSync.Dialogs;

namespace ThixSync.Pages;

/// <summary>
/// The start page "Synchronisationen" (like the mockup): the header with the big logo, "ThixSync – Dateien sichern und abgleichen" and
/// "Synchronisation erstellen"; the tabs "Synchronisationen" (toolbar: search, filter, sort; the cards of the jobs; below them "Neue
/// Synchronisation"), "Aktivität" (every running job live) and "Übersicht" (numbers, the bytes of 14 days, the newest runs).
/// </summary>
public sealed class JobsPage : UserControl
{
    public const string JobsTab = "jobs";
    public const string ActivityTab = "activity";
    public const string OverviewTab = "overview";

    private static readonly JobFilterKind[] Filters = [JobFilterKind.All, JobFilterKind.TwoWay, JobFilterKind.Mirror, JobFilterKind.Backup, JobFilterKind.Problems, JobFilterKind.Scheduled, JobFilterKind.Paused];
    private static readonly JobSort[] Sorts = [JobSort.Name, JobSort.LastRun, JobSort.NextRun, JobSort.Created];

    private readonly ListToolbar _toolbar = new("Synchronisationen suchen");
    private readonly ComboBox _filter;
    private readonly ComboBox _sort;
    private readonly StackPanel _cards = new();
    private readonly ScrollViewer _scroller;
    private readonly InfoBar _message = new();
    private readonly InfoBar _policy = new();
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Button _newButton;
    private readonly Button _createButton;
    private readonly TabControl _tabs = new();
    private readonly TabItem _jobsTab;
    private readonly TabItem _activityTab;
    private readonly TabItem _overviewTab;
    private readonly ActivityView _activity = new();
    private readonly OverviewView _overview = new();

    public JobsPage()
    {
        this.Use(BackgroundProperty, "Mat.Surface");
        _filter = _toolbar.AddChoice("Filter", ["Alle Synchronisationen", "Bidirektional", "Spiegeln", "Backup", "Mit Problemen", "Geplant", "Pausiert"], 210);
        _sort = _toolbar.AddChoice("Sortierung", ["Sortiert nach Name", "Zuletzt synchronisiert", "Nächster Lauf", "Neueste zuerst"], 200);
        _toolbar.Changed += (_, _) => Refresh();
        _count.Themed("MatText.Muted");

        _createButton = Ui.Button("plus", "Synchronisation erstellen", "MatButton.Primary");
        _createButton.MinHeight = 44;
        _createButton.Padding = new Thickness(22, 0);
        _createButton.FontSize = MatTokens.Type.FsLg;
        _createButton.VerticalAlignment = VerticalAlignment.Center;
        _createButton.Click += (_, _) => Ui.MainOf(this)?.ShowJobForm(null);
        _newButton = Ui.Button("plus", "Neue Synchronisation", "MatButton.Primary");
        _newButton.Click += (_, _) => Ui.MainOf(this)?.ShowJobForm(null);

        _scroller = new ScrollViewer { Content = _cards, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 14, 0) };
        _jobsTab = new TabItem { Header = TabHeader("Synchronisationen"), Content = ListView() };
        _activityTab = new TabItem { Header = TabHeader("Aktivität"), Content = _activity };
        _overviewTab = new TabItem { Header = TabHeader("Übersicht"), Content = _overview };
        _activity.JobsRequested += (_, _) => SelectTab(JobsTab);
        _tabs.Items.Add(_jobsTab);
        _tabs.Items.Add(_activityTab);
        _tabs.Items.Add(_overviewTab);
        _tabs.Padding = new Thickness(0, 22, 0, 0);
        _tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source == _tabs)
            {
                RefreshTab();
            }
        };

        var page = new DockPanel { Margin = new Thickness(36, 28, 36, 24) };
        page.Children.Add(Ui.Docked(Header(), Dock.Top));
        page.Children.Add(_tabs);
        Content = page;

        Ui.WhileShown(this, Subscribe, Unsubscribe, Refresh);
    }

    public TextBox SearchBox => _toolbar.SearchBox;

    public ComboBox FilterChoice => _filter;

    public ComboBox SortChoice => _sort;

    public InfoBar Message => _message;

    /// <summary>The cards shown now.</summary>
    public IReadOnlyList<JobCard> Cards => [.. _cards.Children.OfType<JobCard>()];

    public ActivityView Activity => _activity;

    public OverviewView Overview => _overview;

    public void SelectTab(string tab) => _tabs.SelectedItem = tab switch
    {
        ActivityTab => _activityTab,
        OverviewTab => _overviewTab,
        _ => _jobsTab,
    };

    public string SelectedTab => _tabs.SelectedItem == _activityTab ? ActivityTab : _tabs.SelectedItem == _overviewTab ? OverviewTab : JobsTab;

    /// <summary>A message above the cards (after saving, deleting, starting).</summary>
    public void ShowMessage(string text, InfoSeverity severity) => _message.Show(text, severity);

    /// <summary>Draws the cards again from the business layer.</summary>
    public void Refresh()
    {
        var locked = AccessPolicy.IsConfigurationLocked;
        _createButton.IsEnabled = _newButton.IsEnabled = !locked;
        if (locked)
        {
            _policy.Show("Von deiner Organisation festgelegt: Synchronisationen lassen sich hier nicht anlegen oder ändern (Richtlinie LockConfiguration). Sie laufen weiter.", InfoSeverity.Info);
        }
        else
        {
            _policy.Hide();
        }

        var jobs = JobService.Query(_toolbar.SearchText, Filters[Math.Max(0, _filter.SelectedIndex)], Sorts[Math.Max(0, _sort.SelectedIndex)]);
        var all = JobService.List().Count;
        var latest = ProtocolService.LatestRuns();
        _cards.Children.Clear();
        foreach (var job in jobs)
        {
            var source = ConnectionService.Find(job.SourceConnectionId);
            var target = ConnectionService.Find(job.TargetConnectionId);
            var canRun = JobService.CanRunHere(job, out var reason);
            var card = new JobCard(job, JobStatus.Of(job, latest), source, target, canRun, reason, locked);
            card.ActionRequested += (_, action) => _ = OnActionAsync(job.Id, action);
            _cards.Children.Add(card);
        }

        if (all == 0)
        {
            var create = Ui.Button("plus", "Erste Synchronisation erstellen", "MatButton.Primary");
            create.IsEnabled = !locked;
            create.Click += (_, _) => Ui.MainOf(this)?.ShowJobForm(null);
            _cards.Children.Add(new EmptyState("refresh", "Noch keine Synchronisation",
                "Eine Synchronisation hält zwei Orte gleich – zum Beispiel deine Dokumente mit dem NAS, der Cloud oder einem anderen Computer. Oder sie sichert sie als Backup mit Versionen.",
                create));
        }
        else if (jobs.Count == 0)
        {
            _cards.Children.Add(new EmptyState("search", "Nichts gefunden", "Keine Synchronisation passt zu Suche und Filter."));
        }

        _count.Text = jobs.Count == all ? (all == 1 ? "1 Synchronisation" : $"{all} Synchronisationen") : $"{jobs.Count} von {all} Synchronisationen";
        RefreshTab();
    }

    private void RefreshTab()
    {
        if (_tabs.SelectedItem == _activityTab)
        {
            _activity.Refresh();
        }
        else if (_tabs.SelectedItem == _overviewTab)
        {
            _overview.Refresh();
        }
    }

    private void Subscribe()
    {
        JobService.Changed += OnChanged;
        ConnectionService.Changed += OnChanged;
        ProtocolService.Changed += OnRunWritten;
        ProtocolService.ExternalChange += OnExternalChange;
        RunMonitor.Changed += OnProgress;
        RunMonitor.RunsChanged += OnRunsChanged;
    }

    private void Unsubscribe()
    {
        JobService.Changed -= OnChanged;
        ConnectionService.Changed -= OnChanged;
        ProtocolService.Changed -= OnRunWritten;
        ProtocolService.ExternalChange -= OnExternalChange;
        RunMonitor.Changed -= OnProgress;
        RunMonitor.RunsChanged -= OnRunsChanged;
    }

    private void OnChanged(object? sender, EventArgs e) => Ui.Post(Refresh);

    private void OnRunWritten(object? sender, SyncRun run) => Ui.Post(Refresh);

    /// <summary>The CLI (another process) wrote runs.</summary>
    private void OnExternalChange(object? sender, EventArgs e) => Ui.Post(Refresh);

    private void OnRunsChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>New numbers: the cards and the activity follow in place.</summary>
    private void OnProgress(object? sender, EventArgs e)
    {
        foreach (var card in Cards)
        {
            card.ShowProgress(RunMonitor.ProgressOf(card.JobId));
        }

        if (_tabs.SelectedItem == _activityTab)
        {
            _activity.Refresh();
        }
    }

    private Control Header()
    {
        var logo = new Image { Source = Ui.Logo, Width = 104, Height = 104, Margin = new Thickness(0, 0, 26, 0) };
        RenderOptions.SetBitmapInterpolationMode(logo, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        var title = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "ThixSync", FontSize = 34, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Dateien sichern und abgleichen", FontSize = MatTokens.Type.FsXl }.Use(TextBlock.ForegroundProperty, "Mat.TextMuted"),
            },
        };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        header.Children.Add(Ui.Docked(_createButton, Dock.Right));
        header.Children.Add(Ui.Docked(logo, Dock.Left));
        header.Children.Add(title);
        return header;
    }

    private static TextBlock TabHeader(string text) => new() { Text = text, FontSize = MatTokens.Type.FsLg };

    private Control ListView()
    {
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 14, 0, 0) };
        bottom.Children.Add(new ActionBar { Children = { _newButton } });
        bottom.Children.Add(Ui.Cell(_count, 0, 1));
        var top = new StackPanel { Children = { _toolbar, _policy, _message } };
        var view = new DockPanel();
        view.Children.Add(Ui.Docked(top, Dock.Top));
        view.Children.Add(Ui.Docked(bottom, Dock.Bottom));
        view.Children.Add(_scroller);
        return view;
    }

    private async Task OnActionAsync(Guid jobId, JobAction action)
    {
        var window = Ui.MainOf(this);
        switch (action)
        {
            case JobAction.Run:
                await JobCommands.RunAsync(this, jobId, _message.Show);
                break;
            case JobAction.Stop:
                RunMonitor.Cancel(jobId);
                _message.Show("Der Auftrag wird angehalten. Was schon kopiert ist, bleibt.", InfoSeverity.Info);
                break;
            case JobAction.Schedule:
                window?.ShowJobForm(jobId, JobForm.ScheduleTab);
                break;
            case JobAction.Details:
                window?.ShowJobDetails(jobId);
                break;
            case JobAction.History:
                window?.ShowJobDetails(jobId, JobDetailsPage.HistoryTab);
                break;
            case JobAction.Edit:
                window?.ShowJobForm(jobId);
                break;
            case JobAction.Analyse:
                window?.ShowJobDetails(jobId, JobDetailsPage.PreviewTab, analyse: true);
                break;
            case JobAction.Duplicate:
                JobCommands.Duplicate(jobId, _message.Show);
                break;
            case JobAction.TogglePause:
                JobCommands.TogglePause(jobId, _message.Show);
                break;
            case JobAction.Delete:
                await JobCommands.DeleteAsync(this, jobId, _message.Show);
                break;
        }
    }
}

/// <summary>The commands of a job that several pages offer (cards, details, schedules, tray): each tells its result as a message.</summary>
internal static class JobCommands
{
    /// <summary>"Jetzt synchronisieren": runs the job and tells how it ended.</summary>
    public static async Task RunAsync(Control owner, Guid jobId, Action<string, InfoSeverity> tell, RunRequest? request = null)
    {
        var name = JobService.Find(jobId)?.Name ?? "Der Auftrag";
        tell($"„{name}“ läuft …", InfoSeverity.Info);
        var (run, error) = await RunMonitor.RunAsync(jobId, request ?? new RunRequest { Trigger = RunTrigger.Manual });
        if (error is not null || run is null)
        {
            tell(error ?? "Der Auftrag konnte nicht starten.", InfoSeverity.Error);
            return;
        }

        var severity = run.Outcome switch
        {
            RunOutcome.Success or RunOutcome.Analysed => InfoSeverity.Success,
            RunOutcome.Failed => InfoSeverity.Error,
            RunOutcome.Cancelled => InfoSeverity.Info,
            _ => InfoSeverity.Warning,
        };
        tell($"{name}: {(run.Summary.Length > 0 ? run.Summary : JobStatus.OfOutcome(run.Outcome).Text)}", severity);
    }

    public static void Duplicate(Guid jobId, Action<string, InfoSeverity> tell)
    {
        try
        {
            var copy = JobService.Duplicate(jobId);
            tell($"„{copy.Name}“ wurde angelegt (pausiert, bis du ihn fortsetzt).", InfoSeverity.Success);
        }
        catch (BusinessException exception)
        {
            tell(exception.Message, InfoSeverity.Error);
        }
    }

    public static void TogglePause(Guid jobId, Action<string, InfoSeverity> tell)
    {
        try
        {
            var job = JobService.Get(jobId);
            var saved = JobService.SetPaused(jobId, !job.IsPaused);
            tell(saved.IsPaused ? $"„{saved.Name}“ ist pausiert und läuft nicht mehr von selbst." : $"„{saved.Name}“ läuft wieder nach Zeitplan.", InfoSeverity.Success);
        }
        catch (BusinessException exception)
        {
            tell(exception.Message, InfoSeverity.Error);
        }
    }

    /// <summary>Asks first; true when the job was deleted.</summary>
    public static async Task<bool> DeleteAsync(Control owner, Guid jobId, Action<string, InfoSeverity> tell)
    {
        if (JobService.Find(jobId) is not { } job)
        {
            return false;
        }

        if (!await ConfirmDialog.AskAsync(Ui.WindowOf(owner), "Synchronisation löschen?",
                $"„{job.Name}“ wird gelöscht – mit der Cloud auf all deinen Geräten. Die Dateien in Quelle und Ziel bleiben, wie sie sind; das Protokoll bleibt, bis es abläuft.",
                "Löschen", isDestructive: true))
        {
            return false;
        }

        try
        {
            JobService.Delete(jobId);
            tell($"„{job.Name}“ wurde gelöscht.", InfoSeverity.Success);
            return true;
        }
        catch (BusinessException exception)
        {
            tell(exception.Message, InfoSeverity.Error);
            return false;
        }
    }
}
