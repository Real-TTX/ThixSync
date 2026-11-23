using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using MatDesktop;
using ThixSync.Controls;
using ThixSync.Core.Business;
using ThixSync.Core.Models;

namespace ThixSync.Pages;

/// <summary>
/// The tab "Übersicht" (<see cref="ProtocolService.GetOverview"/>): tiles with the numbers (jobs, scheduled, paused, connections, runs,
/// files and bytes of the last 7 days, jobs with problems), the bytes copied per day of the last 14 days as bars, and the newest runs
/// (a click opens one).
/// </summary>
public sealed class OverviewView : UserControl
{
    private readonly StatTile _jobs = new("refresh", "Synchronisationen");
    private readonly StatTile _scheduled = new("calendar", "Geplant");
    private readonly StatTile _paused = new("pause", "Pausiert", Tone.Muted);
    private readonly StatTile _connections = new("monitor", "Geräte & Verbindungen");
    private readonly StatTile _runs = new("activity", "Läufe in 7 Tagen");
    private readonly StatTile _files = new("file", "Dateien kopiert (7 Tage)");
    private readonly StatTile _bytes = new("upload", "Übertragen (7 Tage)", Tone.Success);
    private readonly StatTile _problems = new("alert", "Mit Problemen", Tone.Warning);
    private readonly BarChart _chart = new() { FormatValue = value => Formats.Bytes((long)value) };
    private readonly TextBlock _chartTotal = new();
    private readonly StackPanel _recent = new() { Spacing = 2 };

    public OverviewView()
    {
        var tiles = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, -14, 0) };
        foreach (var tile in new[] { _jobs, _scheduled, _paused, _connections, _runs, _files, _bytes, _problems })
        {
            tile.Margin = new Thickness(0, 0, 14, 14);
            tiles.Children.Add(tile);
        }

        _chartTotal.Themed("MatText.Muted");
        var chart = Ui.Card(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "Übertragen pro Tag", FontWeight = FontWeight.SemiBold, FontSize = MatTokens.Type.FsLg },
                _chartTotal,
                new Border { Child = _chart, Margin = new Thickness(0, 14, 0, 0) },
            },
        });
        var recent = Ui.Card(new StackPanel
        {
            Spacing = 10,
            Children = { new TextBlock { Text = "Letzte Läufe", FontWeight = FontWeight.SemiBold, FontSize = MatTokens.Type.FsLg }, _recent },
        });

        var lower = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,16,2*") };
        lower.Children.Add(Ui.Cell(chart, 0, 0));
        lower.Children.Add(Ui.Cell(recent, 0, 2));
        var content = new StackPanel { Children = { tiles, lower } };
        Content = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 14, 0) };
    }

    /// <summary>The value of the tile "Synchronisationen" (tests).</summary>
    public string JobsValue => _jobs.Value;

    public void Refresh()
    {
        var overview = ProtocolService.GetOverview();
        _jobs.Show(Formats.Count(overview.Jobs));
        _scheduled.Show(Formats.Count(overview.ScheduledJobs), "laufen von selbst");
        _paused.Show(Formats.Count(overview.PausedJobs));
        _connections.Show(Formats.Count(overview.Connections));
        _runs.Show(Formats.Count(overview.RunsLastWeek));
        _files.Show(Formats.Count(overview.FilesCopiedLastWeek));
        _bytes.Show(Formats.Bytes(overview.BytesCopiedLastWeek));
        _problems.Show(Formats.Count(overview.ProblemJobs), overview.ProblemJobs == 0 ? "alles in Ordnung" : "brauchen deinen Blick");

        _chart.Bars = [.. overview.BytesPerDay.Select(day => (day.Day.ToString("dd.", Formats.German), (double)day.Bytes))];
        var total = overview.BytesPerDay.Sum(day => day.Bytes);
        _chartTotal.Text = $"{Formats.Bytes(total)} in den letzten 14 Tagen";

        _recent.Children.Clear();
        foreach (var run in overview.RecentRuns.Take(7))
        {
            _recent.Children.Add(RunRow(run));
        }

        if (overview.RecentRuns.Count == 0)
        {
            _recent.Children.Add(new TextBlock { Text = "Noch keine Läufe." }.Themed("MatText.Muted"));
        }
    }

    private Control RunRow(SyncRun run)
    {
        var (text, icon, tone) = JobStatus.OfOutcome(run.Outcome);
        var mark = new MatIcon { Icon = icon, Size = 18, Margin = new Thickness(0, 0, 12, 0) }.Use(MatIcon.ForegroundProperty, Tones.Text(tone));
        ToolTip.SetTip(mark, text);
        var name = new TextBlock { Text = run.JobName, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }.Use(TextBlock.ForegroundProperty, "Mat.Text");
        var detail = new TextBlock { Text = $"{Formats.When(run.StartedAt)} · {(run.BytesCopied > 0 ? Formats.Bytes(run.BytesCopied) : text)}", TextTrimming = TextTrimming.CharacterEllipsis };
        detail.Themed("MatText.Muted");
        var button = new Button
        {
            Content = new DockPanel { Children = { Ui.Docked(mark, Dock.Left), new StackPanel { Spacing = 1, Children = { name, detail } } } },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 6),
        }.Themed("MatButton.Ghost");
        button.FontWeight = FontWeight.Normal;
        button.Click += (_, _) => Ui.MainOf(this)?.ShowRun(run.Id);
        return button;
    }
}
