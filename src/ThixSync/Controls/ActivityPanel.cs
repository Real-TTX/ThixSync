using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MatDesktop;
using ThixSync.Core.Models;

namespace ThixSync.Controls;

/// <summary>
/// The live panel of one running job (tab "Aktivität"): its name and phase with "Anhalten", the speed in big numbers (MB/s and
/// Dateien/s), the time left and the workers, the speed of the last minute as a small graph, the progress with the counts, and the list
/// "Aktive Übertragungen" with the progress of every file being copied. <see cref="Show"/> updates it in place (several times a second).
/// </summary>
public sealed class ActivityPanel : Border
{
    private readonly TextBlock _phase = new();
    private readonly TextBlock _speed = new();
    private readonly TextBlock _fileSpeed = new();
    private readonly TextBlock _remaining = new();
    private readonly TextBlock _workers = new();
    private readonly Sparkline _graph = new() { Height = 70 };
    private readonly ProgressBar _bar = new() { Maximum = 1 };
    private readonly TextBlock _percent = new() { FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _counts = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _transfers = new() { Spacing = 10 };
    private readonly TextBlock _noTransfers = new() { Text = "Gerade wird keine Datei übertragen." };

    public ActivityPanel(SyncJob job)
    {
        JobId = job.Id;
        this.Themed("MatCard");
        Padding = new Thickness(24, 20);
        Margin = new Thickness(0, 0, 0, 16);
        AutomationProperties.SetName(this, $"Aktivität {job.Name}");
        _phase.Themed("MatText.Muted");
        _bar.Themed("MatProgress");
        _bar.Height = 8;
        _counts.Themed("MatText.Muted");
        _noTransfers.Themed("MatText.Muted");

        var stop = Ui.Button("stop", "Anhalten");
        stop.Click += (_, _) => StopRequested?.Invoke(this, EventArgs.Empty);
        var title = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = job.Name, FontSize = MatTokens.Type.FsXl, FontWeight = FontWeight.SemiBold }, _phase },
        };
        var spinner = new Spinner { Width = 28, Height = 28, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 18), Children = { Ui.Docked(stop, Dock.Right), Ui.Docked(spinner, Dock.Left), title } };

        var numbers = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,*"), Margin = new Thickness(0, 0, 0, 14) };
        numbers.Children.Add(Ui.Cell(Number(_speed, "Übertragung", isBig: true), 0, 0));
        numbers.Children.Add(Ui.Cell(Number(_fileSpeed, "Dateien", isBig: true), 0, 1));
        numbers.Children.Add(Ui.Cell(Number(_remaining, "Verbleibend"), 0, 2));
        numbers.Children.Add(Ui.Cell(Number(_workers, "Gleichzeitig"), 0, 3));

        var graph = new Border { Padding = new Thickness(0, 4, 0, 0), Child = _graph, Margin = new Thickness(0, 0, 0, 16) };
        var progress = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new DockPanel { Children = { Ui.Docked(_percent, Dock.Right), new TextBlock { Text = "Fortschritt", FontWeight = FontWeight.SemiBold } } },
                _bar,
                _counts,
            },
        };

        var transfers = new StackPanel
        {
            Margin = new Thickness(0, 20, 0, 0),
            Spacing = 10,
            Children = { new TextBlock { Text = "Aktive Übertragungen", FontWeight = FontWeight.SemiBold }, _transfers, _noTransfers },
        };

        Child = new StackPanel { Children = { header, numbers, graph, progress, transfers } };
    }

    /// <summary>"Anhalten" was chosen.</summary>
    public event EventHandler? StopRequested;

    public Guid JobId { get; }

    public string SpeedText => _speed.Text ?? "";

    public int TransferCount => _transfers.Children.Count;

    public void Show(RunProgress progress)
    {
        _phase.Text = progress.Text.Length > 0 ? progress.Text : JobCard.PhaseText(progress.Phase);
        _speed.Text = Formats.Speed(progress.BytesPerSecond);
        _fileSpeed.Text = Formats.FileSpeed(progress.FilesPerSecond);
        _remaining.Text = progress.Remaining is { } left ? Formats.Duration(left) : "–";
        _workers.Text = progress.Workers == 1 ? "1 Datei" : $"{progress.Workers} Dateien";
        _graph.Values = progress.SpeedHistory;
        _bar.IsIndeterminate = progress.Phase != RunPhase.Transferring && progress.Fraction <= 0;
        _bar.Value = progress.Fraction;
        _percent.Text = Formats.Percent(progress.Fraction);
        _counts.Text = $"{Formats.Count(progress.FilesDone)} von {Formats.Files(progress.FilesTotal)} · {Formats.Bytes(progress.BytesDone)} von {Formats.Bytes(progress.BytesTotal)}"
                       + $" · {(progress.Errors == 1 ? "1 Fehler" : $"{Formats.Count(progress.Errors)} Fehler")} · seit {Formats.Duration(progress.Elapsed)}";

        _transfers.Children.Clear();
        foreach (var transfer in progress.Active)
        {
            _transfers.Children.Add(Transfer(transfer));
        }

        _noTransfers.IsVisible = progress.Active.Count == 0;
    }

    private static Control Number(TextBlock value, string label, bool isBig = false)
    {
        value.FontSize = isBig ? MatTokens.Type.Fs3xl : MatTokens.Type.FsXl;
        value.FontWeight = FontWeight.SemiBold;
        if (isBig)
        {
            value.Use(TextBlock.ForegroundProperty, "Mat.AccentText");
        }

        return new StackPanel
        {
            Margin = new Thickness(0, 0, 40, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Children = { new TextBlock { Text = label }.Themed("MatText.Muted"), value },
        };
    }

    private static Control Transfer(ActiveTransfer transfer)
    {
        var (icon, direction) = transfer.Action switch
        {
            PlanAction.CopyToSource => ("arrow-down", "zur Quelle"),
            _ => ("upload", "zum Ziel"),
        };
        var fraction = transfer.Bytes <= 0 ? 1 : Math.Clamp((double)transfer.Done / transfer.Bytes, 0, 1);
        var bar = new ProgressBar { Maximum = 1, Value = fraction, Width = 160, VerticalAlignment = VerticalAlignment.Center }.Themed("MatProgress");
        var amount = new TextBlock { Text = $"{Formats.Bytes(transfer.Done)} von {Formats.Bytes(transfer.Bytes)}", Width = 150, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center }.Themed("MatText.Muted");
        amount.TextWrapping = TextWrapping.NoWrap;
        var path = new TextBlock { Text = transfer.Path, TextTrimming = TextTrimming.PathSegmentEllipsis, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(path, $"{transfer.Path} ({direction})");
        var mark = Ui.IconTile(icon, 28, 15);
        mark.Margin = new Thickness(0, 0, 12, 0);
        var row = new DockPanel();
        row.Children.Add(Ui.Docked(mark, Dock.Left));
        row.Children.Add(Ui.Docked(amount, Dock.Right));
        row.Children.Add(Ui.Docked(new Border { Child = bar, Margin = new Thickness(16, 0, 0, 0) }, Dock.Right));
        row.Children.Add(path);
        return row;
    }
}
