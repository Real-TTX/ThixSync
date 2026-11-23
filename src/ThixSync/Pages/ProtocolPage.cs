using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MatDesktop;
using ThixSync.Controls;
using ThixSync.Core.Business;
using ThixSync.Core.Models;

namespace ThixSync.Pages;

/// <summary>
/// "Protokolle": every run of this computer as a table (<see cref="ProtocolService.Query"/>) with the toolbar above it (search, outcome,
/// sort, job) and below it the action "Lauf öffnen" at the left and the pagination at the right. A double click opens the run.
/// </summary>
public sealed class ProtocolPage : UserControl
{
    private static readonly RunFilterKind[] Filters = [RunFilterKind.All, RunFilterKind.Success, RunFilterKind.Problems, RunFilterKind.Failed, RunFilterKind.Analyses];
    private static readonly RunSort[] Sorts = [RunSort.Newest, RunSort.Oldest, RunSort.Job, RunSort.Duration, RunSort.Bytes];

    private readonly ListToolbar _toolbar = new("Protokoll durchsuchen");
    private readonly ComboBox _filter;
    private readonly ComboBox _sort;
    private readonly ComboBox _job;
    private readonly DataTable<SyncRun> _table = new("Läufe");
    private readonly Pager _pager = new();
    private readonly Button _open = Ui.Button("file-text", "Lauf öffnen", "MatButton.Primary");
    private List<SyncJob> _jobs = [];
    private int _page;
    private bool _isLoading;

    public ProtocolPage()
    {
        this.Use(BackgroundProperty, "Mat.Surface");
        _filter = _toolbar.AddChoice("Ergebnis", ["Alle Läufe", "Erfolgreich", "Mit Problemen", "Fehlgeschlagen", "Analysen"], 180);
        _sort = _toolbar.AddChoice("Sortierung", ["Neueste zuerst", "Älteste zuerst", "Nach Auftrag", "Längste zuerst", "Meiste Daten"], 180);
        _job = _toolbar.AddChoice("Auftrag", ["Alle Aufträge"], 200);
        _toolbar.Changed += (_, _) =>
        {
            if (_isLoading)
            {
                return;
            }

            _page = 0;
            Refresh();
        };
        _pager.PageChanged += (_, page) =>
        {
            _page = page;
            Refresh();
        };
        _table.EmptyText = "Noch keine Läufe – sobald eine Synchronisation gelaufen ist, steht hier, was sie getan hat.";
        AddRunColumns(_table, withJob: true);
        _table.ItemActivated += (_, run) => Ui.MainOf(this)?.ShowRun(run.Id);
        _table.SelectionChanged += (_, _) => _open.IsEnabled = _table.SelectedItem is not null;
        _open.Click += (_, _) =>
        {
            if (_table.SelectedItem is { } run)
            {
                Ui.MainOf(this)?.ShowRun(run.Id);
            }
        };

        var intro = new TextBlock
        {
            Text = "Jeder Lauf dieses Computers: was kopiert, gelöscht und verschoben wurde, was nicht ging und warum. Ältere Läufe gehen nach der Aufbewahrungszeit (Einstellungen › Allgemein).",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 900,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 18),
        }.Themed("MatText.Muted");
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 14, 0, 0) };
        bottom.Children.Add(new ActionBar { Children = { _open } });
        bottom.Children.Add(Ui.Cell(_pager, 0, 1));
        var page = new DockPanel { Margin = new Thickness(36, 28, 36, 24) };
        page.Children.Add(Ui.Docked(new TextBlock { Text = "Protokolle" }.Themed("MatText.PageTitle"), Dock.Top));
        page.Children.Add(Ui.Docked(intro, Dock.Top));
        page.Children.Add(Ui.Docked(_toolbar, Dock.Top));
        page.Children.Add(Ui.Docked(bottom, Dock.Bottom));
        page.Children.Add(_table);
        Content = page;

        Ui.WhileShown(this, () => ProtocolService.Changed += OnRunWritten, () => ProtocolService.Changed -= OnRunWritten, LoadJobsAndRefresh);
    }

    public DataTable<SyncRun> Table => _table;

    public Pager Pager => _pager;

    public ListToolbar Toolbar => _toolbar;

    public ComboBox FilterChoice => _filter;

    public ComboBox JobChoice => _job;

    /// <summary>Only the runs of one job (null: all).</summary>
    public void FilterJob(Guid? jobId)
    {
        LoadJobs();
        _job.SelectedIndex = jobId is { } id ? _jobs.FindIndex(job => job.Id == id) + 1 : 0;
    }

    /// <summary>The columns of a table of runs (here and in the details of a job).</summary>
    internal static void AddRunColumns(DataTable<SyncRun> table, bool withJob)
    {
        table.AddColumn("Ergebnis", "170", run =>
        {
            var (text, icon, tone) = JobStatus.OfOutcome(run.Outcome);
            return new StatePill(text, icon, tone) { Padding = new Thickness(9, 3, 11, 3) };
        });
        if (withJob)
        {
            table.AddColumn("Auftrag", "1.6*", run => DataTable<SyncRun>.TextCell(run.JobName, isStrong: true));
        }

        table.AddColumn("Gestartet", "150", run => DataTable<SyncRun>.TextCell(Formats.When(run.StartedAt)))
            .AddColumn("Dauer", "100", run => DataTable<SyncRun>.TextCell(Formats.Duration(run.Duration), "Mat.TextMuted"))
            .AddColumn("Dateien", "80", run => DataTable<SyncRun>.TextCell(Formats.Count(run.FilesCopied + run.FilesDeleted + run.FilesMoved)), alignRight: true)
            .AddColumn("Daten", "90", run => DataTable<SyncRun>.TextCell(run.BytesCopied > 0 ? Formats.Bytes(run.BytesCopied) : "–"), alignRight: true)
            .AddColumn("Zusammenfassung", "2*", run => DataTable<SyncRun>.TextCell(run.Summary, "Mat.TextMuted"));
    }

    public void Refresh()
    {
        Guid? jobId = _job.SelectedIndex > 0 && _job.SelectedIndex - 1 < _jobs.Count ? _jobs[_job.SelectedIndex - 1].Id : null;
        var page = ProtocolService.Query(_toolbar.SearchText, Filters[Math.Max(0, _filter.SelectedIndex)], Sorts[Math.Max(0, _sort.SelectedIndex)], jobId, _page);
        _page = page.PageIndex;
        _table.SetItems(page.Items, run => run.Id);
        _pager.Show(page.PageIndex, page.PageCount, page.TotalCount, page.PageSize);
        _open.IsEnabled = _table.SelectedItem is not null;
    }

    private void LoadJobsAndRefresh()
    {
        LoadJobs();
        Refresh();
    }

    private void LoadJobs()
    {
        _isLoading = true;
        try
        {
            var selected = _job.SelectedIndex > 0 && _job.SelectedIndex - 1 < _jobs.Count ? _jobs[_job.SelectedIndex - 1].Id : (Guid?)null;
            _jobs = [.. JobService.List().OrderBy(job => job.Name, StringComparer.CurrentCultureIgnoreCase)];
            _job.ItemsSource = new[] { "Alle Aufträge" }.Concat(_jobs.Select(job => job.Name)).ToList();
            _job.SelectedIndex = selected is { } id ? _jobs.FindIndex(job => job.Id == id) + 1 : 0;
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void OnRunWritten(object? sender, SyncRun run) => Ui.Post(Refresh);
}

/// <summary>
/// One run: its facts (job, trigger, start, duration, counts, bytes, summary, device) and its lines (<see cref="ProtocolService.Entries"/>)
/// as a table with a toolbar (search, "Nur Probleme") and pages. Button: Zurück.
/// </summary>
public sealed class RunPage : UserControl
{
    private readonly TextBlock _title = new();
    private readonly ContentControl _outcome = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly MatFacts _left = new() { LabelWidth = 110 };
    private readonly MatFacts _middle = new() { LabelWidth = 110 };
    private readonly MatFacts _right = new() { LabelWidth = 140 };
    private readonly ListToolbar _toolbar = new("Pfad oder Meldung suchen");
    private readonly CheckBox _onlyProblems;
    private readonly DataTable<RunEntry> _table = new("Zeilen des Laufs");
    private readonly Pager _pager = new();
    private readonly InfoBar _message = new();
    private Guid _runId;
    private int _page;

    public RunPage()
    {
        this.Use(BackgroundProperty, "Mat.Surface");
        _title.Themed("MatText.PageTitle");
        _onlyProblems = _toolbar.AddSwitch("Nur Probleme");
        _toolbar.Changed += (_, _) =>
        {
            _page = 0;
            ShowEntries();
        };
        _pager.PageChanged += (_, page) =>
        {
            _page = page;
            ShowEntries();
        };
        _table.EmptyText = "Keine Zeile passt zu Suche und Filter.";
        _table.AddColumn("Zeit", "90", entry => DataTable<RunEntry>.TextCell(entry.At.ToLocalTime().ToString("HH:mm:ss", Formats.German), "Mat.TextMuted"))
            .AddColumn("", "34", entry => LevelCell(entry.Level))
            .AddColumn("Aktion", "160", entry => DataTable<RunEntry>.TextCell(ActionText(entry.Action)))
            .AddColumn("Pfad", "2.5*", entry =>
            {
                var path = DataTable<RunEntry>.TextCell(entry.Path.Length > 0 ? entry.Path : "–");
                path.TextTrimming = TextTrimming.PathSegmentEllipsis;
                return path;
            })
            .AddColumn("Größe", "90", entry => DataTable<RunEntry>.TextCell(entry.Bytes > 0 ? Formats.Bytes(entry.Bytes) : "", "Mat.TextMuted"), alignRight: true)
            .AddColumn("Meldung", "2*", entry => DataTable<RunEntry>.TextCell(entry.Message, entry.Level == RunEntryLevel.Error ? "Mat.DangerText" : entry.Level == RunEntryLevel.Warning ? "Mat.WarningText" : "Mat.TextMuted"));

        var back = Ui.Button("back", "Zurück");
        back.Click += (_, _) => Ui.MainOf(this)?.GoBack();
        var facts = new Grid { ColumnDefinitions = new ColumnDefinitions("*,28,*,28,*") };
        facts.Children.Add(Ui.Cell(_left, 0, 0));
        facts.Children.Add(Ui.Cell(_middle, 0, 2));
        facts.Children.Add(Ui.Cell(_right, 0, 4));
        _summary.Themed("MatText.Muted");
        _summary.FontSize = MatTokens.Type.FsMd;
        var factsCard = new Border { Child = facts, Padding = new Thickness(22, 6), Margin = new Thickness(0, 16, 0, 18) }.Themed("MatCard");
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 14, 0, 0) };
        bottom.Children.Add(new ActionBar { Children = { back } });
        bottom.Children.Add(Ui.Cell(_pager, 0, 1));

        var page = new DockPanel { Margin = new Thickness(36, 28, 36, 24) };
        var outcome = new DockPanel { Margin = new Thickness(0, 8, 0, 0), Children = { Ui.Docked(_outcome, Dock.Left), _summary } };
        _outcome.Margin = new Thickness(0, 0, 14, 0);
        page.Children.Add(Ui.Docked(new StackPanel { Children = { _title, outcome } }, Dock.Top));
        page.Children.Add(Ui.Docked(_message, Dock.Top));
        page.Children.Add(Ui.Docked(factsCard, Dock.Top));
        page.Children.Add(Ui.Docked(new TextBlock { Text = "Was passiert ist", FontWeight = FontWeight.SemiBold, FontSize = MatTokens.Type.FsLg, Margin = new Thickness(0, 0, 0, 10) }, Dock.Top));
        page.Children.Add(Ui.Docked(_toolbar, Dock.Top));
        page.Children.Add(Ui.Docked(bottom, Dock.Bottom));
        page.Children.Add(_table);
        Content = page;
    }

    public DataTable<RunEntry> Table => _table;

    public Pager Pager => _pager;

    public CheckBox OnlyProblems => _onlyProblems;

    public MatFacts Facts => _left;

    public void Load(Guid runId)
    {
        _runId = runId;
        _page = 0;
        _toolbar.SearchBox.Text = "";
        _onlyProblems.IsChecked = false;
        _message.Hide();
        if (ProtocolService.Find(runId) is not { } run)
        {
            _title.Text = "Lauf";
            _message.Show("Diesen Lauf gibt es nicht mehr (das Protokoll wurde aufgeräumt).", InfoSeverity.Warning);
            return;
        }

        var (text, icon, tone) = JobStatus.OfOutcome(run.Outcome);
        _title.Text = $"Lauf von „{run.JobName}“";
        _outcome.Content = new StatePill(text, icon, tone);
        _summary.Text = run.Summary;
        _left.Facts =
        [
            new MatFact("Auftrag", run.JobName),
            new MatFact("Ausgelöst", JobStatus.OfTrigger(run.Trigger)),
            new MatFact("Gestartet", Formats.When(run.StartedAt)),
            new MatFact("Dauer", Formats.Duration(run.Duration)),
        ];
        _middle.Facts =
        [
            new MatFact("Kopiert", $"{Formats.Files(run.FilesCopied)} · {Formats.Bytes(run.BytesCopied)}"),
            new MatFact("Gelöscht", Formats.Files(run.FilesDeleted)),
            new MatFact("Verschoben", Formats.Files(run.FilesMoved)),
            new MatFact("Ordner", Formats.Count(run.FoldersCreated)),
        ];
        _right.Facts =
        [
            new MatFact("Konflikte", Formats.Count(run.Conflicts)),
            new MatFact("Fehler", Formats.Count(run.Errors)),
            new MatFact("Angesehen", Formats.Files(run.FilesScanned)),
            new MatFact("Gerät", run.DeviceName),
        ];
        ShowEntries();
    }

    private void ShowEntries()
    {
        var page = ProtocolService.Entries(_runId, _toolbar.SearchText, _onlyProblems.IsChecked == true, _page);
        _page = page.PageIndex;
        _table.SetItems(page.Items, selectFirst: false);
        _pager.Show(page.PageIndex, page.PageCount, page.TotalCount, page.PageSize);
    }

    private static Control LevelCell(RunEntryLevel level)
    {
        var (icon, tone, word) = level switch
        {
            RunEntryLevel.Error => ("x-circle", Tone.Danger, "Fehler"),
            RunEntryLevel.Warning => ("alert", Tone.Warning, "Warnung"),
            _ => ("check", Tone.Muted, "Info"),
        };
        var mark = new MatIcon { Icon = icon, Size = 16 }.Use(MatIcon.ForegroundProperty, Tones.Text(tone));
        ToolTip.SetTip(mark, word);
        Avalonia.Automation.AutomationProperties.SetName(mark, word);
        return mark;
    }

    private static string ActionText(PlanAction? action) => action switch
    {
        PlanAction.CopyToTarget => "→ Ins Ziel",
        PlanAction.CopyToSource => "← In die Quelle",
        PlanAction.DeleteOnTarget => "Im Ziel gelöscht",
        PlanAction.DeleteOnSource => "In der Quelle gelöscht",
        PlanAction.CreateFolderOnTarget or PlanAction.CreateFolderOnSource => "Ordner angelegt",
        PlanAction.MoveOnTarget or PlanAction.MoveOnSource => "Verschoben",
        PlanAction.Conflict => "Konflikt",
        PlanAction.InStep => "Gleich",
        _ => "–",
    };
}
