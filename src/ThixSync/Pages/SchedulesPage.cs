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
/// "Zeitpläne": every job with its schedule, its next and last run, whether it runs on changes and whether it is paused (toolbar: search,
/// geplant / manuell / pausiert, sort by next run or name). Below the table: Bearbeiten (the job form on its tab "Zeitplan"),
/// Pausieren/Fortsetzen and – with a gap – "Alle pausieren"/"Alle fortsetzen" for every automatic run of this computer.
/// </summary>
public sealed class SchedulesPage : UserControl
{
    private readonly ListToolbar _toolbar = new("Zeitpläne suchen");
    private readonly ComboBox _filter;
    private readonly ComboBox _sort;
    private readonly DataTable<SyncJob> _table = new("Zeitpläne");
    private readonly InfoBar _message = new();
    private readonly InfoBar _pausedAll = new();
    private readonly Button _edit = Ui.Button("edit", "Bearbeiten", "MatButton.Primary");
    private readonly Button _pause = Ui.Button("pause", "Pausieren");
    private readonly Button _all = Ui.Button("pause", "Alle pausieren", "MatButton.Warning");
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
    private IReadOnlyDictionary<Guid, SyncRun> _latest = new Dictionary<Guid, SyncRun>();

    public SchedulesPage()
    {
        this.Use(BackgroundProperty, "Mat.Surface");
        _filter = _toolbar.AddChoice("Filter", ["Alle Aufträge", "Geplant", "Manuell", "Pausiert"], 170);
        _sort = _toolbar.AddChoice("Sortierung", ["Nächster Lauf zuerst", "Sortiert nach Name"], 200);
        _toolbar.Changed += (_, _) => Refresh();
        _count.Themed("MatText.Muted");
        _table.EmptyText = "Kein Auftrag passt zu Suche und Filter.";
        _table.AddColumn("Auftrag", "1.6*", job => NameCell(job))
            .AddColumn("Zeitplan", "1.4*", job => DataTable<SyncJob>.TextCell(JobService.DescribeSchedule(job.Schedule)))
            .AddColumn("Nächster Lauf", "1.1*", job => DataTable<SyncJob>.TextCell(job.IsPaused ? "pausiert" : Formats.When(SchedulerService.NextRunOf(job)), job.IsPaused ? "Mat.TextMuted" : null, isStrong: !job.IsPaused))
            .AddColumn("Letzter Lauf", "1.1*", job => DataTable<SyncJob>.TextCell(_latest.TryGetValue(job.Id, out var run) ? Formats.When(run.StartedAt) : "noch nie", "Mat.TextMuted"))
            .AddColumn("Bei Änderungen", "120", job => OnChangeCell(job))
            .AddColumn("Status", "140", job => job.IsPaused ? new StatePill("Pausiert", "pause", Tone.Muted) { Padding = new Thickness(9, 3, 11, 3) } : new StatePill("Aktiv", "check", Tone.Success) { Padding = new Thickness(9, 3, 11, 3) });
        _table.SelectionChanged += (_, _) => ApplyButtons();
        _table.ItemActivated += (_, job) => Ui.MainOf(this)?.ShowJobForm(job.Id, JobForm.ScheduleTab);

        _edit.Click += (_, _) =>
        {
            if (_table.SelectedItem is { } job)
            {
                Ui.MainOf(this)?.ShowJobForm(job.Id, JobForm.ScheduleTab);
            }
        };
        _pause.Click += (_, _) =>
        {
            if (_table.SelectedItem is { } job)
            {
                JobCommands.TogglePause(job.Id, _message.Show);
            }
        };
        ActionBar.SetSeparated(_all, true);
        _all.Click += (_, _) =>
        {
            if (SchedulerService.IsPausedAll)
            {
                SchedulerService.ResumeAll();
                _message.Show("Alle Aufträge laufen wieder nach ihrem Zeitplan.", InfoSeverity.Success);
            }
            else
            {
                SchedulerService.PauseAll();
                _message.Show("Alle automatischen Läufe sind pausiert, bis du sie fortsetzt. Von Hand starten geht weiter.", InfoSeverity.Info);
            }

            Refresh();
        };

        var intro = new TextBlock
        {
            Text = "Wann deine Synchronisationen von selbst laufen. Sie laufen nur auf dem Computer, der bei ihnen eingetragen ist – nach Zeitplan, bei Änderungen oder beim Start.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 900,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 18),
        }.Themed("MatText.Muted");
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 14, 0, 0) };
        bottom.Children.Add(new ActionBar { Children = { _edit, _pause, _all } });
        bottom.Children.Add(Ui.Cell(_count, 0, 1));
        var page = new DockPanel { Margin = new Thickness(36, 28, 36, 24) };
        page.Children.Add(Ui.Docked(new TextBlock { Text = "Zeitpläne" }.Themed("MatText.PageTitle"), Dock.Top));
        page.Children.Add(Ui.Docked(intro, Dock.Top));
        page.Children.Add(Ui.Docked(_toolbar, Dock.Top));
        page.Children.Add(Ui.Docked(new StackPanel { Children = { _pausedAll, _message } }, Dock.Top));
        page.Children.Add(Ui.Docked(bottom, Dock.Bottom));
        page.Children.Add(_table);
        Content = page;

        Ui.WhileShown(this, () => JobService.Changed += OnChanged, () => JobService.Changed -= OnChanged, Refresh);
    }

    public DataTable<SyncJob> Table => _table;

    public ComboBox FilterChoice => _filter;

    public Button PauseButton => _pause;

    public Button AllButton => _all;

    public void Refresh()
    {
        var sort = _sort.SelectedIndex == 1 ? JobSort.Name : JobSort.NextRun;
        IEnumerable<SyncJob> jobs = _filter.SelectedIndex switch
        {
            1 => JobService.Query(_toolbar.SearchText, JobFilterKind.Scheduled, sort).Where(job => !job.IsPaused),
            2 => JobService.Query(_toolbar.SearchText, JobFilterKind.All, sort).Where(job => job.Schedule.Kind == ScheduleKind.Manual && !job.Schedule.RunOnChange),
            3 => JobService.Query(_toolbar.SearchText, JobFilterKind.Paused, sort),
            _ => JobService.Query(_toolbar.SearchText, JobFilterKind.All, sort),
        };
        _latest = ProtocolService.LatestRuns();
        var list = jobs.ToList();
        _table.SetItems(list, job => job.Id);
        var all = JobService.List().Count;
        _count.Text = list.Count == all ? (all == 1 ? "1 Auftrag" : $"{all} Aufträge") : $"{list.Count} von {all} Aufträgen";
        if (SchedulerService.IsPausedAll)
        {
            _pausedAll.Show("Alle automatischen Läufe sind pausiert. Von Hand starten geht weiter.", InfoSeverity.Warning);
        }
        else
        {
            _pausedAll.Hide();
        }

        ApplyButtons();
    }

    private void ApplyButtons()
    {
        var job = _table.SelectedItem;
        var locked = AccessPolicy.IsConfigurationLocked;
        _edit.IsEnabled = job is not null && !locked;
        _pause.IsEnabled = job is not null && !locked;
        _pause.Content = new IconLabel { Icon = job?.IsPaused == true ? "play" : "pause", Text = job?.IsPaused == true ? "Fortsetzen" : "Pausieren" };
        var pausedAll = SchedulerService.IsPausedAll;
        _all.Content = new IconLabel { Icon = pausedAll ? "play" : "pause", Text = pausedAll ? "Alle fortsetzen" : "Alle pausieren" };
    }

    private void OnChanged(object? sender, EventArgs e) => Ui.Post(Refresh);

    private static Control NameCell(SyncJob job)
    {
        var mark = Ui.IconTile(job.Mode switch { SyncMode.Backup => "archive", SyncMode.Mirror => "arrow-right", _ => "refresh" }, 30, 15);
        mark.Margin = new Thickness(0, 0, 12, 0);
        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = job.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = job.RunsOnDevice.Length > 0 ? $"auf {job.RunsOnDevice}" : "", FontSize = MatTokens.Type.FsXs }.Use(TextBlock.ForegroundProperty, "Mat.TextMuted"),
            },
        };
        return new DockPanel { Children = { Ui.Docked(mark, Dock.Left), text } };
    }

    private static Control OnChangeCell(SyncJob job)
    {
        if (!job.Schedule.RunOnChange)
        {
            return DataTable<SyncJob>.TextCell("–", "Mat.TextMuted");
        }

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new MatIcon { Icon = "check", Size = 16 }.Use(MatIcon.ForegroundProperty, "Mat.SuccessText"),
                new TextBlock { Text = "Ja", VerticalAlignment = VerticalAlignment.Center },
            },
        };
    }
}
