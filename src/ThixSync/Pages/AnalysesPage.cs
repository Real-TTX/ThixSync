using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using MatDesktop;
using ThixSync.Controls;
using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Dialogs;

namespace ThixSync.Pages;

/// <summary>
/// "Analysen": every analysis of every source, newest first, with its size and the change against the analysis before it. Toolbar
/// above (search, source, sort), actions below (Auf der Karte zeigen, Mit vorheriger vergleichen … Löschen).
/// </summary>
public sealed class AnalysesPage : UserControl
{
    private readonly TextBox _search = new() { Width = 240, Watermark = "Analysen suchen" };
    private readonly ComboBox _sourceFilter = new() { MinWidth = 220 };
    private readonly ComboBox _sort = new() { MinWidth = 200, ItemsSource = new[] { "Neueste zuerst", "Älteste zuerst", "Größte zuerst", "Größte Veränderung zuerst" }, SelectedIndex = 0 };
    private readonly ListBox _list = new();
    private readonly TextBlock _count = new();
    private readonly InfoBar _message = new();
    private readonly Button _showButton = new();
    private readonly Button _compareButton = new();
    private readonly Button _deleteButton = new();
    private IReadOnlyList<ScanSource> _sources = [];
    private bool _isFilling;

    public AnalysesPage()
    {
        this.Use(BackgroundProperty, "Mat.Surface");
        _search.Theme = Ui.Theme("MatSearchBox");
        AutomationProperties.SetName(_search, "Analysen suchen");
        AutomationProperties.SetName(_sourceFilter, "Quelle");
        AutomationProperties.SetName(_sort, "Sortierung");
        _search.TextChanged += (_, _) => Refresh();
        _sourceFilter.SelectionChanged += (_, _) =>
        {
            if (!_isFilling)
            {
                Refresh();
            }
        };
        _sort.SelectionChanged += (_, _) => Refresh();

        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        foreach (var control in new Control[] { _search, _sourceFilter, _sort })
        {
            control.Margin = new Thickness(0, 0, 10, 6);
            toolbar.Children.Add(control);
        }

        _list.Theme = Ui.Theme("MatList");
        _list.ItemContainerTheme = Ui.Theme("MatCardRow");
        _list.ItemTemplate = new FuncDataTemplate<Analysis>((analysis, _) => analysis is null ? new Panel() : AnalysisRow(analysis), supportsRecycling: false);
        AutomationProperties.SetName(_list, "Analysen");
        _list.SelectionChanged += (_, _) => UpdateActions();
        _list.DoubleTapped += (_, _) => ShowSelected(compare: false);

        _showButton.Content = new IconLabel { Icon = "grid", Text = "Auf der Karte zeigen" };
        _showButton.Themed("MatButton.Primary");
        _showButton.Click += (_, _) => ShowSelected(compare: false);
        _compareButton.Content = new IconLabel { Icon = "layers", Text = "Mit vorheriger vergleichen" };
        _compareButton.Click += (_, _) => ShowSelected(compare: true);
        _deleteButton.Content = new IconLabel { Icon = "trash", Text = "Löschen" };
        _deleteButton.Themed("MatButton.Danger");
        ActionBar.SetSeparated(_deleteButton, true);
        _deleteButton.Click += async (_, _) => await DeleteSelectedAsync();
        var actions = new ActionBar { Children = { _showButton, _compareButton, _deleteButton } };
        _count.Themed("MatText.Muted");
        _count.VerticalAlignment = VerticalAlignment.Center;
        _count.HorizontalAlignment = HorizontalAlignment.Right;
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 12, 0, 0) };
        bottom.Children.Add(actions);
        Grid.SetColumn(_count, 1);
        bottom.Children.Add(_count);

        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto"), Margin = new Thickness(28, 22, 28, 20) };
        page.Children.Add(new TextBlock { Text = "Analysen", Margin = new Thickness(0, 0, 0, 4) }.Themed("MatText.PageTitle"));
        var intro = new TextBlock { Text = "Jede Analyse bleibt gespeichert – so siehst du, wie eine Quelle wächst. Wie viele je Quelle bleiben, stellst du in den Einstellungen ein.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) }.Themed("MatText.Muted");
        Grid.SetRow(intro, 1);
        page.Children.Add(intro);
        Grid.SetRow(toolbar, 2);
        page.Children.Add(toolbar);
        Grid.SetRow(_message, 3);
        page.Children.Add(_message);
        Grid.SetRow(_list, 4);
        page.Children.Add(_list);
        Grid.SetRow(bottom, 5);
        page.Children.Add(bottom);
        Content = page;

        AnalysisService.Changed += (_, _) => Ui.Post(Refresh);
        SourceService.Changed += (_, _) => Ui.Post(Refresh);
        Refresh();
    }

    public ListBox List => _list;

    /// <summary>Only the analyses of one source.</summary>
    public void FilterSource(Guid? sourceId)
    {
        Refresh();
        var index = sourceId is { } id ? _sources.ToList().FindIndex(source => source.Id == id) + 1 : 0;
        _sourceFilter.SelectedIndex = Math.Max(0, index);
    }

    private Control AnalysisRow(Analysis analysis)
    {
        var previous = AnalysisService.Previous(analysis);
        var source = SourceService.Get(analysis.SourceId);
        var icon = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Child = new MatIcon { Icon = "bar-chart", Size = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }.Use(MatIcon.ForegroundProperty, "Mat.AccentText") };
        icon.Use(Border.BackgroundProperty, "Mat.AccentSoft");
        var details = $"{Ui.FormatCount(analysis.FileCount)} Dateien · {Ui.FormatCount(analysis.FolderCount)} Ordner · {Ui.FormatDuration(analysis.Duration)}";
        if (analysis.DeviceName.Length > 0)
        {
            details += $" · auf {analysis.DeviceName}";
        }

        if (analysis.SkippedCount > 0)
        {
            details += $" · {Ui.FormatCount(analysis.SkippedCount)} Ordner ohne Zugriff";
        }

        var text = new StackPanel
        {
            Margin = new Thickness(14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = $"{source?.Name ?? analysis.SourceName}  ·  {Ui.FormatDateTime(analysis.StartedAt)}", FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = details, TextTrimming = TextTrimming.CharacterEllipsis }.Themed("MatText.Muted"),
            },
        };
        var size = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        size.Children.Add(new TextBlock { Text = Ui.FormatSize(analysis.TotalBytes), FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Right });
        var change = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        if (previous is not null)
        {
            var delta = analysis.TotalBytes - previous.TotalBytes;
            if (delta != 0)
            {
                change.Children.Add(new MatIcon { Icon = delta > 0 ? "arrow-up" : "arrow-down", Size = 12, VerticalAlignment = VerticalAlignment.Center }.Use(MatIcon.ForegroundProperty, "Mat.TextMuted"));
            }

            change.Children.Add(new TextBlock { Text = $"{Ui.FormatDelta(delta)} seit {Ui.FormatDateTime(previous.StartedAt)}", FontSize = MatTokens.Type.FsXs }.Themed("MatText.Muted"));
        }
        else
        {
            change.Children.Add(new TextBlock { Text = "erste Analyse", FontSize = MatTokens.Type.FsXs }.Themed("MatText.Muted"));
        }

        size.Children.Add(change);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        row.Children.Add(icon);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        Grid.SetColumn(size, 2);
        row.Children.Add(size);
        return row;
    }

    private void Refresh()
    {
        _sources = SourceService.List();
        var wanted = _sourceFilter.SelectedIndex > 0 && _sourceFilter.SelectedIndex - 1 < _sources.Count ? _sources[_sourceFilter.SelectedIndex - 1].Id : (Guid?)null;
        _isFilling = true;
        try
        {
            _sourceFilter.ItemsSource = new[] { "Alle Quellen" }.Concat(_sources.Select(source => source.Name)).ToList();
            var index = wanted is { } id ? _sources.ToList().FindIndex(source => source.Id == id) + 1 : 0;
            _sourceFilter.SelectedIndex = Math.Max(0, index);
        }
        finally
        {
            _isFilling = false;
        }

        IEnumerable<Analysis> analyses = AnalysisService.List(wanted);
        var search = (_search.Text ?? "").Trim();
        if (search.Length > 0)
        {
            analyses = analyses.Where(analysis => analysis.SourceName.Contains(search, StringComparison.OrdinalIgnoreCase)
                                                  || analysis.Path.Contains(search, StringComparison.OrdinalIgnoreCase)
                                                  || analysis.DeviceName.Contains(search, StringComparison.OrdinalIgnoreCase)
                                                  || Ui.FormatDateTime(analysis.StartedAt).Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        analyses = _sort.SelectedIndex switch
        {
            1 => analyses.OrderBy(analysis => analysis.StartedAt),
            2 => analyses.OrderByDescending(analysis => analysis.TotalBytes),
            3 => analyses.OrderByDescending(analysis => AnalysisService.Previous(analysis) is { } previous ? Math.Abs(analysis.TotalBytes - previous.TotalBytes) : -1),
            _ => analyses.OrderByDescending(analysis => analysis.StartedAt),
        };

        var selected = (_list.SelectedItem as Analysis)?.Id;
        var list = analyses.ToList();
        _list.ItemsSource = list;
        _list.SelectedItem = list.FirstOrDefault(analysis => analysis.Id == selected) ?? list.FirstOrDefault();
        var all = AnalysisService.List().Count;
        _count.Text = list.Count == all ? $"{all} {(all == 1 ? "Analyse" : "Analysen")}" : $"{list.Count} von {all} Analysen";
        UpdateActions();
    }

    private void UpdateActions()
    {
        var analysis = _list.SelectedItem as Analysis;
        _showButton.IsEnabled = _deleteButton.IsEnabled = analysis is not null;
        _compareButton.IsEnabled = analysis is not null && AnalysisService.Previous(analysis) is not null;
    }

    private void ShowSelected(bool compare)
    {
        if (_list.SelectedItem is not Analysis analysis || Ui.WindowOf(this) is not MainWindow window)
        {
            return;
        }

        window.ShowMap(analysis.SourceId, analysis.Id, compare ? AnalysisService.Previous(analysis)?.Id : null);
    }

    private async Task DeleteSelectedAsync()
    {
        if (_list.SelectedItem is not Analysis analysis)
        {
            return;
        }

        if (!await ConfirmDialog.AskAsync(Ui.WindowOf(this), "Analyse löschen?",
                $"Die Analyse von „{analysis.SourceName}“ vom {Ui.FormatDateTime(analysis.StartedAt)} wird gelöscht – mit der Cloud auf all deinen Computern. Auf dem Laufwerk wird nichts gelöscht.",
                "Löschen", isDestructive: true))
        {
            return;
        }

        try
        {
            AnalysisService.Delete(analysis.Id);
            _message.Show("Die Analyse wurde gelöscht.", InfoSeverity.Success);
        }
        catch (BusinessException exception)
        {
            _message.Show(exception.Message, InfoSeverity.Error);
        }

        Log.Info("Analysis deleted by the user");
    }
}
