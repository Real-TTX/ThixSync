using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using MatDesktop;
using ThixSync.Controls;
using ThixSync.Core.Business;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;

namespace ThixSync.Pages;

/// <summary>
/// The details of a job (its card's "Details" and "Verlauf") in tabs: Vorschau (Analysieren compares both sides without copying; the plan
/// as a table with toolbar – search, kind of action, sort – and pages, the totals, conflicts resolved per row; then "Synchronisieren"
/// carries the plan out, "Löschungen bestätigen" when a run stopped at the safety limit), Verlauf (the runs of the job, a double click
/// opens one) and Einstellungen (everything the job is set to, in short).
/// </summary>
public sealed class JobDetailsPage : UserControl
{
    public const string PreviewTab = "preview";
    public const string HistoryTab = "history";
    public const string SettingsTab = "settings";

    private const int PlanPageSize = 100;
    private const int HistoryPageSize = 50;

    private static readonly string[] Groups = ["Alle Aktionen", "Ins Ziel kopieren →", "In die Quelle kopieren ←", "Löschen", "Verschieben", "Konflikte", "Ordner anlegen"];
    private static readonly RunFilterKind[] RunFilters = [RunFilterKind.All, RunFilterKind.Success, RunFilterKind.Problems, RunFilterKind.Failed, RunFilterKind.Analyses];
    private static readonly RunSort[] RunSorts = [RunSort.Newest, RunSort.Oldest, RunSort.Duration, RunSort.Bytes];

    private readonly ContentControl _mark = new() { Margin = new Thickness(0, 4, 14, 0), VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _title = new();
    private readonly TextBlock _subtitle = new();
    private readonly InfoBar _message = new();
    private readonly TabControl _tabs = new();
    private readonly TabItem _previewTab = new() { Header = "Vorschau" };
    private readonly TabItem _historyTab = new() { Header = "Verlauf" };
    private readonly TabItem _settingsTab = new() { Header = "Einstellungen" };

    // ---- Vorschau
    private readonly ListToolbar _planToolbar = new("Pfad suchen");
    private readonly ComboBox _planGroup;
    private readonly ComboBox _planSort;
    private readonly DataTable<PlanItem> _planTable = new("Vorschau");
    private readonly Pager _planPager = new();
    private readonly UniformGrid _planTiles = new() { Rows = 1, Margin = new Thickness(0, 0, -12, 14) };
    private readonly TextBlock _planTotals = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
    private readonly ProgressBar _analysing = new() { IsIndeterminate = true, IsVisible = false, Margin = new Thickness(0, 0, 0, 12) };
    private readonly Control _planView;
    private readonly EmptyState _noPlan;
    private readonly Panel _previewHost = new();

    // ---- Verlauf
    private readonly ListToolbar _historyToolbar = new("Läufe suchen");
    private readonly ComboBox _historyFilter;
    private readonly ComboBox _historySort;
    private readonly DataTable<SyncRun> _historyTable = new("Verlauf");
    private readonly Pager _historyPager = new();

    // ---- Einstellungen
    private readonly MatFacts _facts = new() { LabelWidth = 200 };

    // ---- Buttons
    private readonly Button _run = Ui.Button("refresh", "Synchronisieren", "MatButton.Primary");
    private readonly Button _analyse = Ui.Button("search", "Analysieren");
    private readonly Button _confirm = Ui.Button("check", "Löschungen bestätigen", "MatButton.Warning");
    private readonly Button _openRun = Ui.Button("file-text", "Lauf öffnen");
    private readonly Button _edit = Ui.Button("edit", "Bearbeiten");
    private readonly Button _back = Ui.Button("back", "Zurück");

    private Guid _jobId;
    private SyncPlan? _plan;
    private int _planPage;
    private int _historyPage;
    private bool _isBusy;

    public JobDetailsPage()
    {
        this.Use(BackgroundProperty, "Mat.Surface");
        _title.Themed("MatText.PageTitle");
        _subtitle.Themed("MatText.Muted");
        _planTotals.Themed("MatText.Muted");
        _analysing.Themed("MatProgress");

        _planGroup = _planToolbar.AddChoice("Aktion", Groups, 210);
        _planSort = _planToolbar.AddChoice("Sortierung", ["Konflikte zuerst", "Sortiert nach Pfad", "Größte zuerst"], 190);
        _planToolbar.Changed += (_, _) =>
        {
            _planPage = 0;
            ShowPlanPage();
        };
        _planPager.PageChanged += (_, page) =>
        {
            _planPage = page;
            ShowPlanPage();
        };
        _planTable.EmptyText = "Keine Aktion passt zu Suche und Filter.";
        _planTable.AddColumn("Aktion", "210", item => ActionCell(item.Action))
            .AddColumn("Pfad", "3*", PathCell)
            .AddColumn("Größe", "100", item => DataTable<PlanItem>.TextCell(item.IsFolder || item.Bytes == 0 ? "–" : Formats.Bytes(item.Bytes), "Mat.TextMuted"), alignRight: true)
            .AddColumn("Grund", "2*", item => DataTable<PlanItem>.TextCell(item.Reason, "Mat.TextMuted"))
            .AddColumn("Entscheidung", "200", ResolutionCell);

        var analyseNow = Ui.Button("search", "Jetzt analysieren", "MatButton.Primary");
        analyseNow.Click += (_, _) => _ = AnalyseAsync();
        _noPlan = new EmptyState("search", "Noch keine Vorschau",
            "„Analysieren“ vergleicht Quelle und Ziel, ohne etwas zu kopieren: du siehst vorher, was kopiert, gelöscht und verschoben würde – und entscheidest Konflikte selbst.",
            analyseNow);
        var planTop = new StackPanel { Children = { _planTiles, _planTotals, _planToolbar } };
        var planView = new DockPanel();
        planView.Children.Add(Ui.Docked(planTop, Dock.Top));
        planView.Children.Add(Ui.Docked(new Border { Child = _planPager, Margin = new Thickness(0, 10, 0, 0) }, Dock.Bottom));
        planView.Children.Add(_planTable);
        _planView = planView;
        _previewHost.Children.Add(_planView);
        _previewHost.Children.Add(_noPlan);
        _previewTab.Content = new DockPanel { Children = { Ui.Docked(_analysing, Dock.Top), _previewHost } };

        _historyFilter = _historyToolbar.AddChoice("Filter", ["Alle Läufe", "Erfolgreich", "Mit Problemen", "Fehlgeschlagen", "Analysen"], 190);
        _historySort = _historyToolbar.AddChoice("Sortierung", ["Neueste zuerst", "Älteste zuerst", "Längste zuerst", "Meiste Daten"], 180);
        _historyToolbar.Changed += (_, _) =>
        {
            _historyPage = 0;
            ShowHistory();
        };
        _historyPager.PageChanged += (_, page) =>
        {
            _historyPage = page;
            ShowHistory();
        };
        _historyTable.EmptyText = "Noch keine Läufe.";
        ProtocolPage.AddRunColumns(_historyTable, withJob: false);
        _historyTable.ItemActivated += (_, run) => Ui.MainOf(this)?.ShowRun(run.Id);
        _historyTable.SelectionChanged += (_, _) => _openRun.IsEnabled = _historyTable.SelectedItem is not null;
        var history = new DockPanel();
        history.Children.Add(Ui.Docked(_historyToolbar, Dock.Top));
        history.Children.Add(Ui.Docked(new Border { Child = _historyPager, Margin = new Thickness(0, 10, 0, 0) }, Dock.Bottom));
        history.Children.Add(_historyTable);
        _historyTab.Content = history;

        _settingsTab.Content = new ScrollViewer
        {
            Content = new Border { Child = _facts, Padding = new Thickness(22, 8), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left }.Themed("MatCard"),
        };

        _tabs.Items.Add(_previewTab);
        _tabs.Items.Add(_historyTab);
        _tabs.Items.Add(_settingsTab);
        _tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source == _tabs)
            {
                ApplyButtons();
            }
        };

        _run.Click += (_, _) => _ = RunAsync(new RunRequest { Trigger = RunTrigger.Manual, UseLastPlan = _plan is not null });
        _analyse.Click += (_, _) => _ = AnalyseAsync();
        _confirm.Click += (_, _) => _ = RunAsync(new RunRequest { Trigger = RunTrigger.Manual, ConfirmDeletes = true, UseLastPlan = _plan is not null });
        _openRun.Click += (_, _) =>
        {
            if (_historyTable.SelectedItem is { } run)
            {
                Ui.MainOf(this)?.ShowRun(run.Id);
            }
        };
        _edit.Click += (_, _) => Ui.MainOf(this)?.ShowJobForm(_jobId);
        _back.Click += (_, _) => Ui.MainOf(this)?.GoBack();

        var head = new DockPanel { Children = { Ui.Docked(_mark, Dock.Left), new StackPanel { Spacing = 4, Children = { _title, _subtitle } } } };
        var actions = new ActionBar { Margin = new Thickness(0, 16, 0, 0), Children = { _run, _analyse, _confirm, _openRun, _edit, _back } };
        var page = new DockPanel { Margin = new Thickness(36, 28, 36, 24) };
        page.Children.Add(Ui.Docked(head, Dock.Top));
        page.Children.Add(Ui.Docked(actions, Dock.Bottom));
        _message.Margin = new Thickness(0, 14, 0, 0);
        page.Children.Add(Ui.Docked(_message, Dock.Top));
        _tabs.Margin = new Thickness(0, 14, 0, 0);
        page.Children.Add(_tabs);
        Content = page;
    }

    public Guid JobId => _jobId;

    public DataTable<PlanItem> PlanTable => _planTable;

    public DataTable<SyncRun> HistoryTable => _historyTable;

    public ListToolbar PlanToolbar => _planToolbar;

    public ComboBox PlanGroup => _planGroup;

    public Pager PlanPager => _planPager;

    public Pager HistoryPager => _historyPager;

    public InfoBar Message => _message;

    public SyncPlan? Plan => _plan;

    /// <summary>Shows a job on a tab; <paramref name="analyse"/> starts an analysis at once.</summary>
    public void Load(Guid jobId, string? tab = null, bool analyse = false)
    {
        _jobId = jobId;
        _message.Hide();
        _planPage = _historyPage = 0;
        _planToolbar.SearchBox.Text = "";
        _historyToolbar.SearchBox.Text = "";
        _planGroup.SelectedIndex = 0;
        if (JobService.Find(jobId) is not { } job)
        {
            _message.Show("Diesen Auftrag gibt es nicht mehr.", InfoSeverity.Error);
            return;
        }

        var status = JobStatus.Of(job, ProtocolService.LatestRuns());
        _mark.Content = JobCard.StateMark(status, 34);
        _title.Text = job.Name;
        var source = ConnectionService.Find(job.SourceConnectionId);
        var target = ConnectionService.Find(job.TargetConnectionId);
        _subtitle.Text = $"{JobService.DescribeMode(job.Mode)} · {source?.Name ?? "?"} {(job.Mode == SyncMode.TwoWay ? "⇄" : "→")} {target?.Name ?? "?"} · {status.Text}";
        if (status.State == JobState.NeedsConfirmation)
        {
            _message.Show($"Der letzte Lauf hat angehalten: {status.LastRun?.Summary} Prüfe die Vorschau und bestätige die Löschungen, wenn sie richtig sind.", InfoSeverity.Warning);
        }

        ShowPlan(JobStatus.LastPlan(jobId));
        ShowHistory();
        ShowSettings(job, source, target);
        _tabs.SelectedItem = tab switch
        {
            HistoryTab => _historyTab,
            SettingsTab => _settingsTab,
            _ => _previewTab,
        };
        ApplyButtons();
        if (analyse)
        {
            _ = AnalyseAsync();
        }
    }

    /// <summary>Shows a plan (the kept one of the engine; the snapshots and tests give their own).</summary>
    public void ShowPlan(SyncPlan? plan)
    {
        _plan = plan;
        _planPage = 0;
        _noPlan.IsVisible = plan is null;
        _planView.IsVisible = plan is not null;
        if (plan is null)
        {
            ApplyButtons();
            return;
        }

        _planTiles.Children.Clear();
        var copyTarget = plan.Items.Where(item => item.Action == PlanAction.CopyToTarget).ToList();
        var copySource = plan.Items.Where(item => item.Action == PlanAction.CopyToSource).ToList();
        AddTile("arrow-right", "Ins Ziel", Formats.Count(copyTarget.Count), Formats.Bytes(copyTarget.Sum(item => item.Bytes)), Tone.Accent);
        AddTile("back", "In die Quelle", Formats.Count(copySource.Count), Formats.Bytes(copySource.Sum(item => item.Bytes)), Tone.Accent);
        AddTile("trash", "Löschen", Formats.Count(plan.Count(PlanAction.DeleteOnTarget) + plan.Count(PlanAction.DeleteOnSource)), "in die Versionen", Tone.Danger);
        AddTile("route", "Verschieben", Formats.Count(plan.Count(PlanAction.MoveOnTarget) + plan.Count(PlanAction.MoveOnSource)), "statt neu kopieren", Tone.Info);
        var conflicts = plan.Count(PlanAction.Conflict);
        AddTile("alert", "Konflikte", Formats.Count(conflicts), plan.OpenConflicts == 0 ? "alle entschieden" : $"{plan.OpenConflicts} offen", plan.OpenConflicts > 0 ? Tone.Warning : Tone.Success);

        var work = plan.Items.Count(item => item.Action != PlanAction.InStep);
        _planTotals.Text = plan.IsInStep
            ? $"Beide Seiten sind gleich – nichts zu tun. Analysiert {Formats.When(plan.CreatedAt)}."
            : $"{Formats.Files(work)} zu bearbeiten, {Formats.Bytes(plan.BytesToCopy)} zu übertragen · Quelle {Formats.Files(plan.SourceFiles)} ({Formats.Bytes(plan.SourceBytes)}), Ziel {Formats.Files(plan.TargetFiles)} ({Formats.Bytes(plan.TargetBytes)})"
              + (plan.Skipped > 0 ? $" · {Formats.Count(plan.Skipped)} ausgelassen" : "") + $" · analysiert {Formats.When(plan.CreatedAt)}";
        ShowPlanPage();
        ApplyButtons();
    }

    private void AddTile(string icon, string label, string value, string detail, Tone tone)
    {
        var tile = new StatTile(icon, label, tone, isCompact: true) { Margin = new Thickness(0, 0, 12, 0) };
        tile.Show(value, detail);
        _planTiles.Children.Add(tile);
    }

    private void ShowPlanPage()
    {
        if (_plan is null)
        {
            return;
        }

        IEnumerable<PlanItem> items = _plan.Items.Where(item => item.Action != PlanAction.InStep);
        var search = _planToolbar.SearchText;
        if (search.Length > 0)
        {
            items = items.Where(item => item.Path.Contains(search, StringComparison.CurrentCultureIgnoreCase) || (item.FromPath?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false));
        }

        items = _planGroup.SelectedIndex switch
        {
            1 => items.Where(item => item.Action == PlanAction.CopyToTarget),
            2 => items.Where(item => item.Action == PlanAction.CopyToSource),
            3 => items.Where(item => item.Action is PlanAction.DeleteOnTarget or PlanAction.DeleteOnSource),
            4 => items.Where(item => item.Action is PlanAction.MoveOnTarget or PlanAction.MoveOnSource),
            5 => items.Where(item => item.Action == PlanAction.Conflict),
            6 => items.Where(item => item.Action is PlanAction.CreateFolderOnTarget or PlanAction.CreateFolderOnSource),
            _ => items,
        };
        items = _planSort.SelectedIndex switch
        {
            1 => items.OrderBy(item => item.Path, StringComparer.CurrentCultureIgnoreCase),
            2 => items.OrderByDescending(item => item.Bytes),
            _ => items.OrderBy(item => item.Action == PlanAction.Conflict ? -1 : (int)item.Action).ThenBy(item => item.Path, StringComparer.CurrentCultureIgnoreCase),
        };

        var all = items.ToList();
        var pageCount = Math.Max(1, (all.Count + PlanPageSize - 1) / PlanPageSize);
        _planPage = Math.Clamp(_planPage, 0, pageCount - 1);
        _planTable.SetItems([.. all.Skip(_planPage * PlanPageSize).Take(PlanPageSize)], item => (item.PairIndex, item.Path), selectFirst: false);
        _planPager.Show(_planPage, pageCount, all.Count, PlanPageSize);
    }

    private void ShowHistory()
    {
        var page = ProtocolService.Query(_historyToolbar.SearchText, RunFilters[Math.Max(0, _historyFilter.SelectedIndex)], RunSorts[Math.Max(0, _historySort.SelectedIndex)], _jobId, _historyPage, HistoryPageSize);
        _historyPage = page.PageIndex;
        _historyTable.SetItems(page.Items, run => run.Id);
        _historyPager.Show(page.PageIndex, page.PageCount, page.TotalCount, page.PageSize);
        ApplyButtons();
    }

    private void ShowSettings(SyncJob job, Connection? source, Connection? target)
    {
        string Side(Connection? connection) => connection is null ? "Verbindung fehlt" : $"{connection.Name} ({ConnectionCatalog.Get(connection.Kind).Name}, {ConnectionService.Address(connection)})";
        var options = job.Options;
        var filter = job.Filter;
        var next = SchedulerService.NextRunOf(job);
        var policy = options.Conflicts switch
        {
            ConflictPolicy.Ask => "Mich fragen",
            ConflictPolicy.NewerWins => "Neuere Datei gewinnt",
            ConflictPolicy.SourceWins => "Quelle gewinnt",
            ConflictPolicy.TargetWins => "Ziel gewinnt",
            _ => "Beide behalten (umbenennen)",
        };
        var filterText = new List<string>();
        if (filter.Include.Count > 0)
        {
            filterText.Add($"nur {string.Join(", ", filter.Include)}");
        }

        filterText.Add(filter.Exclude.Count == 0 ? "nichts ausgeschlossen" : $"ohne {string.Join(", ", filter.Exclude.Take(6))}{(filter.Exclude.Count > 6 ? $" (+{filter.Exclude.Count - 6})" : "")}");
        if (filter.MaxFileSizeBytes > 0)
        {
            filterText.Add($"höchstens {Formats.Bytes(filter.MaxFileSizeBytes)}");
        }

        if (filter.MaxAgeDays > 0)
        {
            filterText.Add($"höchstens {filter.MaxAgeDays} Tage alt");
        }

        _facts.Facts =
        [
            new MatFact("Art", JobService.DescribeMode(job.Mode)),
            new MatFact("Quelle", Side(source)),
            new MatFact("Ziel", Side(target)),
            new MatFact("Ordnerpaare", string.Join(Environment.NewLine, job.Pairs.Select(pair => $"{Show(pair.SourcePath)}  {(job.Mode == SyncMode.TwoWay ? "⇄" : "→")}  {Show(pair.TargetPath)}"))),
            new MatFact("Filter", string.Join(" · ", filterText)),
            new MatFact("Zeitplan", JobService.DescribeSchedule(job.Schedule) + (job.IsPaused ? " (pausiert)" : "")),
            new MatFact("Nächster Lauf", Formats.When(next)),
            new MatFact("Läuft automatisch auf", job.RunsOnDevice),
            new MatFact("Konflikte", policy),
            new MatFact("Löschungen", job.Mode == SyncMode.Backup ? "Ein Backup löscht nie im Ziel" : options.PropagateDeletes ? $"werden übertragen, Sicherheitsgrenze {options.SafetyDeletePercent} %" : "werden nicht übertragen"),
            new MatFact("Versionen", options.KeepVersions ? $"behalten, {(options.KeepVersionsDays == 0 ? "für immer" : $"{options.KeepVersionsDays} Tage")}, {(options.KeepVersionsCount == 0 ? "ohne Grenze" : $"höchstens {options.KeepVersionsCount} je Datei")}" : "keine"),
            new MatFact("Übertragung", $"{(options.Parallelism == 0 ? "automatisch viele" : $"{options.Parallelism}")} gleichzeitig{(options.DetectMoves ? " · Verschiebungen erkennen" : "")}{(options.VerifyAfterCopy ? " · danach prüfen" : "")}{(options.CompareContent ? " · Inhalt vergleichen" : "")}"),
        ];
    }

    private static string Show(string path) => path.Length == 0 ? "(Basisordner)" : path;

    private void ApplyButtons()
    {
        var tab = _tabs.SelectedItem;
        var job = JobService.Find(_jobId);
        var running = RunMonitor.IsRunning(_jobId);
        var canRun = job is not null && JobService.CanRunHere(job, out _);
        var status = job is null ? null : JobStatus.Of(job, ProtocolService.LatestRuns());
        _run.IsVisible = _analyse.IsVisible = tab == _previewTab;
        _confirm.IsVisible = tab == _previewTab && status?.State == JobState.NeedsConfirmation;
        _openRun.IsVisible = tab == _historyTab;
        _openRun.IsEnabled = _historyTable.SelectedItem is not null;
        _edit.IsVisible = tab != _historyTab;
        _edit.IsEnabled = !AccessPolicy.IsConfigurationLocked;
        _run.IsEnabled = _analyse.IsEnabled = _confirm.IsEnabled = canRun && !running && !_isBusy;
        ((IconLabel)_run.Content!).Text = _plan is null ? "Synchronisieren" : "Vorschau synchronisieren";
    }

    private async Task AnalyseAsync()
    {
        _isBusy = true;
        _analysing.IsVisible = true;
        ApplyButtons();
        _message.Show("Analysiere beide Seiten – es wird nichts kopiert …", InfoSeverity.Info);
        var (run, error) = await RunMonitor.RunAsync(_jobId, new RunRequest { Trigger = RunTrigger.Manual, AnalyseOnly = true });
        _isBusy = false;
        _analysing.IsVisible = false;
        if (error is not null)
        {
            _message.Show(error, InfoSeverity.Error);
            ApplyButtons();
            return;
        }

        ShowPlan(JobStatus.LastPlan(_jobId));
        ShowHistory();
        _message.Show(run?.Summary is { Length: > 0 } summary ? summary : "Analyse fertig.", run?.Outcome == RunOutcome.Failed ? InfoSeverity.Error : InfoSeverity.Success);
    }

    private async Task RunAsync(RunRequest request)
    {
        _isBusy = true;
        ApplyButtons();
        await JobCommands.RunAsync(this, _jobId, _message.Show, request);
        _isBusy = false;
        ShowPlan(JobStatus.LastPlan(_jobId));
        ShowHistory();
    }

    private static Control ActionCell(PlanAction action)
    {
        var (text, icon, tone) = action switch
        {
            PlanAction.CopyToTarget => ("Ins Ziel kopieren", "arrow-right", Tone.Accent),
            PlanAction.CopyToSource => ("In die Quelle kopieren", "back", Tone.Accent),
            PlanAction.DeleteOnTarget => ("Im Ziel löschen", "trash", Tone.Danger),
            PlanAction.DeleteOnSource => ("In der Quelle löschen", "trash", Tone.Danger),
            PlanAction.CreateFolderOnTarget => ("Ordner im Ziel", "folder", Tone.Muted),
            PlanAction.CreateFolderOnSource => ("Ordner in der Quelle", "folder", Tone.Muted),
            PlanAction.MoveOnTarget => ("Im Ziel verschieben", "route", Tone.Info),
            PlanAction.MoveOnSource => ("In der Quelle verschieben", "route", Tone.Info),
            PlanAction.Conflict => ("Konflikt", "alert", Tone.Warning),
            _ => ("Gleich", "check", Tone.Success),
        };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                Ui.IconTile(icon, 26, 14, Tones.Background(tone), Tones.Text(tone)),
                new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        };
    }

    private static Control PathCell(PlanItem item)
    {
        var path = new TextBlock { Text = item.Path, TextTrimming = TextTrimming.PathSegmentEllipsis, FontWeight = item.Action == PlanAction.Conflict ? FontWeight.SemiBold : FontWeight.Normal };
        ToolTip.SetTip(path, item.Path);
        if (item.FromPath is null)
        {
            return path;
        }

        var from = new TextBlock { Text = $"von {item.FromPath}", FontSize = MatTokens.Type.FsXs, TextTrimming = TextTrimming.PathSegmentEllipsis }.Use(TextBlock.ForegroundProperty, "Mat.TextMuted");
        return new StackPanel { Children = { path, from } };
    }

    /// <summary>A conflict is decided right in its row: Quelle behalten, Ziel behalten, Beide behalten, Überspringen.</summary>
    private Control ResolutionCell(PlanItem item)
    {
        if (item.Action != PlanAction.Conflict)
        {
            return new Panel();
        }

        var choice = new ComboBox
        {
            ItemsSource = new[] { "Offen – bitte wählen", "Quelle behalten", "Ziel behalten", "Beide behalten", "Überspringen" },
            SelectedIndex = item.Resolution is { } resolution ? (int)resolution + 1 : 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(choice, $"Entscheidung für {item.Path}");
        choice.SelectionChanged += (_, _) =>
        {
            if (choice.SelectedIndex <= 0)
            {
                return;
            }

            var chosen = (ConflictChoice)(choice.SelectedIndex - 1);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Resolve(item, chosen));
        };
        return choice;
    }

    /// <summary>Keeps the decision in the plan of the engine (the next "Synchronisieren" carries it out) and in the table.</summary>
    internal void Resolve(PlanItem item, ConflictChoice choice)
    {
        try
        {
            JobRunner.Resolve(_jobId, item.PairIndex, item.Path, choice);
        }
        catch (NotImplementedException)
        {
        }
        catch (BusinessException exception)
        {
            _message.Show(exception.Message, InfoSeverity.Error);
            return;
        }

        if (_plan is null)
        {
            return;
        }

        var kept = JobStatus.LastPlan(_jobId);
        _plan = kept ?? _plan with
        {
            Items = [.. _plan.Items.Select(candidate => candidate.PairIndex == item.PairIndex && candidate.Path == item.Path && candidate.Action == PlanAction.Conflict ? candidate with { Resolution = choice } : candidate)],
        };
        var page = _planPage;
        ShowPlan(_plan);
        _planPage = page;
        ShowPlanPage();
    }
}
