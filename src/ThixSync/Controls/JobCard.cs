using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MatDesktop;
using ThixSync.Core.Business;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;

namespace ThixSync.Controls;

/// <summary>What the user asked of a job on its card (or in its "…" menu).</summary>
public enum JobAction
{
    Run,
    Stop,
    Schedule,
    Details,
    History,
    Edit,
    Analyse,
    Duplicate,
    TogglePause,
    Delete,
}

/// <summary>
/// The card of a job (page "Synchronisationen", like the mockup): the state icon, name and kind with the "…" menu (Bearbeiten,
/// Analysieren, Duplizieren, Pausieren/Fortsetzen, Löschen); the boxes "Quelle" and "Ziel" with the icon of the connection's type, its
/// name and the folders of the pairs, the arrow of the kind between them; the facts (last sync, schedule, conflicts) with the state
/// pill; while it runs the progress with speed, counts and the time left; and the actions "Jetzt synchronisieren" (or "Anhalten"),
/// "Zeitplan", "Details", "Verlauf".
/// </summary>
public sealed class JobCard : Border
{
    private const int FoldersShown = 3;

    private readonly SyncJob _job;
    private readonly StackPanel _progressBox = new() { Spacing = 8, IsVisible = false };
    private readonly ProgressBar _progressBar = new() { Maximum = 1, Height = 8, MinHeight = 8 };
    private readonly TextBlock _progressText = new() { FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _progressDetail = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _progressPercent = new() { FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Button _runButton = new();
    private readonly StatePill _pill;

    public JobCard(SyncJob job, JobStatus status, Connection? source, Connection? target, bool canRunHere, string notHereReason, bool isLocked)
    {
        _job = job;
        Status = status;
        this.Themed("MatCard");
        Padding = new Thickness(24, 20, 24, 20);
        Margin = new Thickness(0, 0, 0, 16);
        AutomationProperties.SetName(this, job.Name);

        _pill = new StatePill(status.Text, status.Icon, status.Tone, isLarge: true) { HorizontalAlignment = HorizontalAlignment.Right };
        _progressBar.Themed("MatProgress");
        _progressBar.Height = 8;
        _progressDetail.Themed("MatText.Muted");

        var content = new StackPanel
        {
            Children =
            {
                Header(job, status, isLocked),
                Sides(job, source, target),
                Ui.Line(new Thickness(0, 20, 0, 14)),
                Facts(job, status),
                _progressBox,
                Ui.Line(new Thickness(0, 14, 0, 16)),
                Actions(canRunHere, notHereReason),
            },
        };
        _progressBox.Margin = new Thickness(0, 16, 0, 0);
        _progressBox.Children.Add(new DockPanel { Children = { Ui.Docked(_progressPercent, Dock.Right), _progressText } });
        _progressBox.Children.Add(_progressBar);
        _progressBox.Children.Add(_progressDetail);
        Child = content;
        ShowProgress(RunMonitor.ProgressOf(job.Id));
    }

    /// <summary>The user chose an action of this card.</summary>
    public event EventHandler<JobAction>? ActionRequested;

    public Guid JobId => _job.Id;

    public JobStatus Status { get; }

    public string StateText => _pill.Text;

    public Button RunButton => _runButton;

    /// <summary>The progress while the job runs (null: hidden).</summary>
    public void ShowProgress(RunProgress? progress)
    {
        var running = progress is not null || RunMonitor.IsRunning(_job.Id);
        _runButton.Content = running ? new IconLabel { Icon = "stop", Text = "Anhalten" } : new IconLabel { Icon = "refresh", Text = "Jetzt synchronisieren" };
        _progressBox.IsVisible = running;
        if (!running)
        {
            return;
        }

        if (progress is null)
        {
            _progressBar.IsIndeterminate = true;
            _progressText.Text = "Startet …";
            _progressDetail.Text = "";
            _progressPercent.Text = "";
            return;
        }

        _progressBar.IsIndeterminate = progress.Phase != RunPhase.Transferring && progress.Fraction <= 0;
        _progressBar.Value = progress.Fraction;
        _progressText.Text = progress.Text.Length > 0 ? progress.Text : PhaseText(progress.Phase);
        _progressPercent.Text = Formats.Percent(progress.Fraction);
        _progressDetail.Text = progress.Phase == RunPhase.Transferring
            ? $"{Formats.Speed(progress.BytesPerSecond)} · {Formats.FileSpeed(progress.FilesPerSecond)} · {Formats.Count(progress.FilesDone)} von {Formats.Files(progress.FilesTotal)} · {Formats.Bytes(progress.BytesDone)} von {Formats.Bytes(progress.BytesTotal)} · {Formats.Remaining(progress.Remaining)}"
            : $"{Formats.Files(progress.FilesScannedSource)} in der Quelle, {Formats.Files(progress.FilesScannedTarget)} im Ziel angesehen";
    }

    /// <summary>The word of a phase of a run.</summary>
    public static string PhaseText(RunPhase phase) => phase switch
    {
        RunPhase.Connecting => "Verbinde …",
        RunPhase.Scanning => "Durchsuche beide Seiten …",
        RunPhase.Comparing => "Vergleiche …",
        RunPhase.Transferring => "Übertrage …",
        RunPhase.Finishing => "Schließe ab …",
        _ => phase.ToString(),
    };

    /// <summary>The round state mark: a turning ring while it runs, else the icon on the colour of its tone.</summary>
    public static Control StateMark(JobStatus status, double size = 30)
    {
        if (status.State == JobState.Running)
        {
            return new Spinner { Width = size, Height = size };
        }

        var strong = status.Tone is Tone.Muted or Tone.Info;
        var mark = Ui.IconTile(status.Icon, size, size * 0.55, strong ? Tones.Background(status.Tone) : Tones.Strong(status.Tone), strong ? Tones.Text(status.Tone) : "Mat.Surface");
        mark.CornerRadius = new CornerRadius(size / 2);
        if (mark.Child is MatIcon icon)
        {
            icon.StrokeWidth = 3;
        }

        return mark;
    }

    private Control Header(SyncJob job, JobStatus status, bool isLocked)
    {
        var mark = StateMark(status);
        mark.VerticalAlignment = VerticalAlignment.Top;
        mark.Margin = new Thickness(0, 2, 14, 0);
        var title = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = job.Name, FontSize = MatTokens.Type.FsXl, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = JobService.DescribeMode(job.Mode) }.Use(TextBlock.ForegroundProperty, "Mat.TextMuted"),
            },
        };

        var more = new Button { Content = new MatIcon { Icon = "more-horizontal", Size = 18 }, Width = 40, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(more, $"Weitere Aktionen für {job.Name}");
        ToolTip.SetTip(more, "Weitere Aktionen");
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        menu.Items.Add(MenuItem("edit", "Bearbeiten", JobAction.Edit, !isLocked));
        menu.Items.Add(MenuItem("search", "Analysieren (Vorschau)", JobAction.Analyse));
        menu.Items.Add(MenuItem("copy", "Duplizieren", JobAction.Duplicate, !isLocked));
        menu.Items.Add(MenuItem(job.IsPaused ? "play" : "pause", job.IsPaused ? "Fortsetzen" : "Pausieren", JobAction.TogglePause, !isLocked));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("trash", "Löschen", JobAction.Delete, !isLocked));
        more.Flyout = menu;

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
        header.Children.Add(Ui.Docked(more, Dock.Right));
        header.Children.Add(Ui.Docked(mark, Dock.Left));
        header.Children.Add(title);
        return header;
    }

    private MenuItem MenuItem(string icon, string text, JobAction action, bool isEnabled = true)
    {
        var item = new MenuItem { Header = text, Icon = new MatIcon { Icon = icon, Size = 16 }, IsEnabled = isEnabled };
        item.Click += (_, _) => ActionRequested?.Invoke(this, action);
        return item;
    }

    private static Control Sides(SyncJob job, Connection? source, Connection? target)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,72,*") };
        grid.Children.Add(Ui.Cell(Side("Quelle", source, job.Pairs.Select(pair => pair.SourcePath).ToList()), 0, 0));
        grid.Children.Add(Ui.Cell(new SyncArrow { IsTwoWay = job.Mode == SyncMode.TwoWay, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, 0, 1));
        grid.Children.Add(Ui.Cell(Side("Ziel", target, job.Pairs.Select(pair => pair.TargetPath).ToList()), 0, 2));
        return grid;
    }

    /// <summary>A box of one side: the big icon of its type, "Quelle"/"Ziel", the connection and its folders.</summary>
    private static Border Side(string title, Connection? connection, IReadOnlyList<string> folders)
    {
        var type = connection is null ? null : ConnectionCatalog.Get(connection.Kind);
        var rows = new StackPanel { Spacing = 7 };
        rows.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = MatTokens.Type.FsLg, Margin = new Thickness(0, 0, 0, 3) });
        rows.Children.Add(Line(type?.Icon ?? "alert", connection?.Name ?? "Verbindung fehlt", ConnectionDetail(connection)));
        foreach (var folder in folders.Take(FoldersShown))
        {
            rows.Children.Add(Line("folder", FolderName(folder), folder, indent: true));
        }

        if (folders.Count > FoldersShown)
        {
            var more = new TextBlock { Text = $"+ {folders.Count - FoldersShown} weitere", Margin = new Thickness(30, 0, 0, 0) }.Themed("MatText.Muted");
            rows.Children.Add(more);
        }

        var tile = Ui.IconTile(type?.Icon ?? "alert", 60, 30);
        tile.CornerRadius = new CornerRadius(14);
        tile.VerticalAlignment = VerticalAlignment.Top;
        tile.Margin = new Thickness(0, 0, 18, 0);
        var box = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(MatTokens.Size.RadiusLg),
            Padding = new Thickness(18, 16),
            Child = new DockPanel { Children = { Ui.Docked(tile, Dock.Left), rows } },
        };
        box.Use(BorderBrushProperty, "Mat.Border");
        box.Use(BackgroundProperty, "Mat.Surface");
        return box;
    }

    private static Control Line(string icon, string text, string? tip, bool indent = false)
    {
        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(indent ? 0 : 0, 0, 0, 0),
            Children =
            {
                new MatIcon { Icon = icon, Size = 18, StrokeWidth = 1.6 }.Use(MatIcon.ForegroundProperty, "Mat.TextMuted"),
                new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300 },
            },
        };
        if (!string.IsNullOrEmpty(tip) && tip != text)
        {
            ToolTip.SetTip(line, tip);
        }

        return line;
    }

    private static string? ConnectionDetail(Connection? connection) => connection is null ? null : ConnectionService.Address(connection);

    /// <summary>The name of a folder of a pair: its last part ("Dokumente"), "Basisordner" when it is the base itself.</summary>
    public static string FolderName(string path)
    {
        var trimmed = path.Trim().TrimEnd('/', '\\');
        if (trimmed.Length == 0)
        {
            return "Basisordner";
        }

        var name = trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..];
        return name.Length == 0 ? trimmed : name;
    }

    private Control Facts(SyncJob job, JobStatus status)
    {
        var next = SchedulerService.NextRunOf(job);
        var schedule = JobService.DescribeSchedule(job.Schedule);
        var planned = job.IsPaused ? "Pausiert" : schedule;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,Auto,*") };
        grid.Children.Add(Ui.Cell(Fact("clock", "Letzte Synchronisation", status.LastRun?.StartedAt is { } last ? Formats.When(last) : "Noch nie"), 0, 0));
        grid.Children.Add(Ui.Cell(Divider(), 0, 1));
        grid.Children.Add(Ui.Cell(Fact("calendar", "Geplant", planned, tip: next is { } nextRun ? $"Nächster Lauf: {Formats.When(nextRun)}" : null), 0, 2));
        grid.Children.Add(Ui.Cell(Divider(), 0, 3));
        grid.Children.Add(Ui.Cell(Fact("alert", "Konflikte", Formats.Count(status.OpenConflicts), status.OpenConflicts > 0 ? "Mat.WarningText" : "Mat.SuccessText"), 0, 4));
        grid.Children.Add(Ui.Cell(_pill, 0, 5));
        return grid;
    }

    private static Control Fact(string icon, string label, string value, string? colour = null, string? tip = null)
    {
        var text = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = label, FontSize = MatTokens.Type.FsSm }.Use(TextBlock.ForegroundProperty, "Mat.TextMuted"),
                new TextBlock { Text = value, FontWeight = FontWeight.SemiBold, FontSize = MatTokens.Type.FsLg, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 280 },
            },
        };
        if (colour is not null && text.Children[1] is TextBlock valueText)
        {
            valueText.Use(TextBlock.ForegroundProperty, colour);
        }

        var fact = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            Children = { new MatIcon { Icon = icon, Size = 26, StrokeWidth = 1.6 }.Use(MatIcon.ForegroundProperty, "Mat.TextMuted"), text },
        };
        ToolTip.SetTip(fact, tip);
        return fact;
    }

    private static Border Divider() => new Border { Width = 1, Margin = new Thickness(26, 4), VerticalAlignment = VerticalAlignment.Stretch }.Use(BackgroundProperty, "Mat.Border");

    private Control Actions(bool canRunHere, string notHereReason)
    {
        _runButton.Themed("MatButton.Primary");
        _runButton.MinHeight = 40;
        _runButton.Padding = new Thickness(22, 0);
        _runButton.IsEnabled = canRunHere || RunMonitor.IsRunning(_job.Id);
        ToolTip.SetTip(_runButton, canRunHere ? null : notHereReason);
        _runButton.Click += (_, _) => ActionRequested?.Invoke(this, RunMonitor.IsRunning(_job.Id) ? JobAction.Stop : JobAction.Run);
        var bar = new ActionBar { Spacing = 10, Children = { _runButton } };
        foreach (var (icon, text, action) in new[] { ("calendar", "Zeitplan", JobAction.Schedule), ("info", "Details", JobAction.Details), ("list", "Verlauf", JobAction.History) })
        {
            var button = Ui.Button(icon, text);
            button.MinHeight = 40;
            button.Padding = new Thickness(18, 0);
            button.Click += (_, _) => ActionRequested?.Invoke(this, action);
            bar.Children.Add(button);
        }

        return bar;
    }
}
