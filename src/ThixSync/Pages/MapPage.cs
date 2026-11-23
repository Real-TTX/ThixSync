using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MatDesktop;
using ThixSync.Controls;
using ThixSync.Core.Business;
using ThixSync.Core.Models;
using ThixSync.Dialogs;

namespace ThixSync.Pages;

/// <summary>
/// "Karte": the map of one analysis. At the top the toolbar (source, analysis, comparison, search, kind, colouring) and the
/// numbers as tiles; at the left the lists in tabs (Ordner as a tree, Größte Dateien, Veränderungen, Dateitypen), at the right the
/// treemap with its path above and its legend below; the actions below, left-aligned from positive to negative with "Löschen" apart.
/// The list and the map choose the same thing; a double click zooms in, the path zooms out. While a source is analysed the
/// progress takes the place of the map.
/// </summary>
public sealed class MapPage : UserControl
{
    private const int ChildrenPerFolder = 300;
    private const int FlatRows = 500;

    private readonly ComboBox _sourceChoice = new() { MinWidth = 220, MaxWidth = 320 };
    private readonly ComboBox _analysisChoice = new() { MinWidth = 210 };
    private readonly ComboBox _compareChoice = new() { MinWidth = 210 };
    private readonly TextBox _search = new() { Width = 250, Watermark = "Dateien und Ordner suchen" };
    private readonly ComboBox _typeChoice = new() { MinWidth = 160 };
    private readonly ComboBox _coloringChoice = new() { MinWidth = 170 };
    private readonly Button _scanButton = new();
    private readonly IconLabel _scanLabel = new() { Icon = "refresh", Text = "Analysieren" };
    private readonly WrapPanel _stats = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _breadcrumb = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    private readonly TabControl _tabs = new();
    private readonly NodeList _tree = new() { EmptyText = "Hier ist nichts." };
    private readonly NodeList _largest = new() { EmptyText = "Keine Dateien gefunden." };
    private readonly NodeList _changes = new() { EmptyText = "Wähle oben eine frühere Analyse zum Vergleich." };
    private readonly StackPanel _typeList = new() { Spacing = 4 };
    private readonly TabItem _changesTab;
    private readonly TreemapView _map = new();
    private readonly WrapPanel _legend = new() { Orientation = Orientation.Horizontal };
    private readonly Button _openButton = new();
    private readonly Button _revealButton = new();
    private readonly Button _copyButton = new();
    private readonly Button _zoomButton = new();
    private readonly Button _deleteButton = new();
    private readonly TextBlock _status = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly InfoBar _message = new();
    private readonly Grid _content = new();
    private readonly Border _progressCard = new();
    private readonly Border _emptyCard = new();
    private readonly HashSet<ScanNode> _expanded = [];
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly ColumnDefinition _listColumn;

    private IReadOnlyList<ScanSource> _sources = [];
    private IReadOnlyList<Analysis> _analyses = [];
    private IReadOnlyList<Analysis?> _comparable = [];
    private ScanSource? _source;
    private Analysis? _analysis;
    private ScanNode? _root;
    private ScanNode? _selected;
    private TreeComparison? _comparison;
    private int _loadVersion;
    private int _treeVersion;
    private (TreeComparison? Comparison, int Version, List<ChangeEntry> Entries)? _changeCache;
    private (ScanNode? Root, int Version, List<CategoryTotal> Totals)? _totalsCache;
    private bool _isFilling;
    private bool _searchPosted;

    public MapPage()
    {
        this.Use(BackgroundProperty, "Mat.Surface");
        _listColumn = new ColumnDefinition(SettingsService.Get().TreeWidth, GridUnitType.Pixel) { MinWidth = 260 };
        _changesTab = new TabItem { Header = "Veränderungen", Content = _changes };
        BuildLayout();
        Wire();
        ReloadSources();
    }

    /// <summary>The analysis shown (null: none).</summary>
    public Analysis? Analysis => _analysis;

    /// <summary>The folder the map shows.</summary>
    public ScanNode? MapRoot => _map.Root;

    public ScanNode? Selected => _selected;

    public TreemapView Map => _map;

    public NodeList Tree => _tree;

    /// <summary>The message above the map (tests).</summary>
    public InfoBar Message => _message;

    /// <summary>What the colours of the map say (the choice in the toolbar follows).</summary>
    public void SetColoring(MapColoring coloring) => _coloringChoice.SelectedIndex = (int)coloring;

    /// <summary>Chooses a file or folder as a click on the map would.</summary>
    public void SelectNode(ScanNode node) => Choose(node, fromMap: true);

    /// <summary>Chooses a tab of the lists (0 Ordner, 1 Größte Dateien, 2 Veränderungen, 3 Dateitypen).</summary>
    public void SelectListTab(int index) => _tabs.SelectedIndex = index;

    /// <summary>Shows a source (its newest analysis, or a specific one) on the map.</summary>
    public void ShowSource(Guid sourceId, Guid? analysisId = null, Guid? compareWith = null)
    {
        ReloadSources();
        var index = _sources.ToList().FindIndex(source => source.Id == sourceId);
        if (index < 0)
        {
            return;
        }

        _isFilling = true;
        _sourceChoice.SelectedIndex = index;
        _isFilling = false;
        OnSourceChosen(analysisId, compareWith);
    }

    /// <summary>Starts an analysis of the source shown (or of <paramref name="sourceId"/>).</summary>
    public async Task ScanAsync(Guid? sourceId = null)
    {
        if (sourceId is { } id && id != _source?.Id)
        {
            ShowSource(id);
        }

        if (_source is null)
        {
            return;
        }

        _message.Hide();
        try
        {
            await ScanService.ScanAsync(_source.Id);
        }
        catch (BusinessException exception)
        {
            _message.Show(exception.Message, InfoSeverity.Error);
        }
    }

    // ---- Layout ----------------------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        // the toolbar: what is shown (source, analysis, comparison) and how (search, kind, colouring); the primary action at its end
        AutomationProperties.SetName(_sourceChoice, "Quelle");
        AutomationProperties.SetName(_analysisChoice, "Analyse");
        AutomationProperties.SetName(_compareChoice, "Vergleichen mit");
        AutomationProperties.SetName(_search, "Dateien und Ordner suchen");
        AutomationProperties.SetName(_typeChoice, "Dateityp");
        AutomationProperties.SetName(_coloringChoice, "Färbung");
        _search.Theme = Ui.Theme("MatSearchBox");
        _typeChoice.ItemsSource = new[] { "Alle Dateitypen" }.Concat(FileCategories.Palette.Select(info => info.Name)).ToList();
        _typeChoice.SelectedIndex = 0;
        _coloringChoice.ItemsSource = new[] { "Farbe: Dateityp", "Farbe: Veränderung" };
        _coloringChoice.SelectedIndex = (int)SettingsService.Get().MapColoring;
        _scanButton.Content = _scanLabel;
        _scanButton.Themed("MatButton.Primary");
        ToolTip.SetTip(_scanButton, "Die Quelle jetzt neu analysieren (F5)");

        var title = new TextBlock { Text = "Karte" }.Themed("MatText.PageTitle");
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(title);
        Grid.SetColumn(_scanButton, 1);
        header.Children.Add(_scanButton);

        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var (label, control) in new (string, Control)[]
                 {
                     ("Quelle", _sourceChoice), ("Analyse", _analysisChoice), ("Vergleich", _compareChoice), ("", _search), ("", _typeChoice), ("", _coloringChoice),
                 })
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 14, 6) };
            if (label.Length > 0)
            {
                item.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }.Themed("MatText.Muted"));
            }

            item.Children.Add(control);
            toolbar.Children.Add(item);
        }

        _stats.Margin = new Thickness(0, 0, 0, 6);

        // the lists in tabs
        var typesScroll = new ScrollViewer { Content = _typeList, Padding = new Thickness(0, 4, 8, 4) };
        _tabs.ItemsSource = new[]
        {
            new TabItem { Header = "Ordner", Content = _tree },
            new TabItem { Header = "Größte Dateien", Content = _largest },
            _changesTab,
            new TabItem { Header = "Dateitypen", Content = typesScroll },
        };
        _tabs.SelectedIndex = 0;
        var listCard = new Border { Padding = new Thickness(10, 6, 6, 8), Child = _tabs }.Themed("MatCard");

        // the map with its path above and its legend below
        var zoomOut = new Button { Content = new MatIcon { Icon = "zoom-out", Size = 16 }, Padding = new Thickness(6, 4) }.Themed("MatButton.Ghost");
        AutomationProperties.SetName(zoomOut, "Eine Ebene höher");
        ToolTip.SetTip(zoomOut, "Eine Ebene höher (Rücktaste)");
        zoomOut.Click += (_, _) => ZoomOut();
        var pathRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };
        DockPanel.SetDock(zoomOut, Dock.Left);
        pathRow.Children.Add(zoomOut);
        pathRow.Children.Add(new ScrollViewer { Content = _breadcrumb, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });

        var mapFrame = new Border { ClipToBounds = true, Child = _map };
        mapFrame.Use(Border.CornerRadiusProperty, "Mat.Radius");
        _legend.Margin = new Thickness(0, 10, 0, 0);
        var mapGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        mapGrid.Children.Add(pathRow);
        Grid.SetRow(mapFrame, 1);
        mapGrid.Children.Add(mapFrame);
        Grid.SetRow(_legend, 2);
        mapGrid.Children.Add(_legend);
        var mapCard = new Border { Padding = new Thickness(12), Child = mapGrid }.Themed("MatCard");

        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Width = 12, Background = null };
        splitter.DragCompleted += (_, _) => SettingsService.Remember(settings => settings with { TreeWidth = _listColumn.ActualWidth });
        _content.ColumnDefinitions = [_listColumn, new ColumnDefinition(12, GridUnitType.Pixel), new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 280 }];
        _content.Children.Add(listCard);
        Grid.SetColumn(splitter, 1);
        _content.Children.Add(splitter);
        Grid.SetColumn(mapCard, 2);
        _content.Children.Add(mapCard);

        BuildProgressCard();
        BuildEmptyCard();
        var middle = new Panel { MinHeight = 280, Children = { _content, _progressCard, _emptyCard } };

        // the actions: positive to negative, "Löschen" apart
        _openButton.Content = new IconLabel { Icon = "external", Text = "Öffnen" };
        _revealButton.Content = new IconLabel { Icon = "folder-open", Text = OperatingSystem.IsMacOS() ? "Im Finder zeigen" : "Im Explorer zeigen" };
        _copyButton.Content = new IconLabel { Icon = "copy", Text = "Pfad kopieren" };
        _zoomButton.Content = new IconLabel { Icon = "zoom-in", Text = "Hineinzoomen" };
        _deleteButton.Content = new IconLabel { Icon = "trash", Text = "Löschen …" };
        _deleteButton.Themed("MatButton.Danger");
        ActionBar.SetSeparated(_deleteButton, true);
        ToolTip.SetTip(_openButton, "Mit dem zugehörigen Programm öffnen (Enter)");
        ToolTip.SetTip(_zoomButton, "Die Karte auf diesen Ordner beschränken (Doppelklick)");
        ToolTip.SetTip(_deleteButton, "In den Papierkorb verschieben (Entf)");
        var actions = new ActionBar { Children = { _openButton, _revealButton, _copyButton, _zoomButton, _deleteButton } };
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 12, 0, 0) };
        bottom.Children.Add(actions);
        _status.Margin = new Thickness(20, 0, 0, 0);
        _status.HorizontalAlignment = HorizontalAlignment.Right;
        _status.Themed("MatText.Muted");
        _status.TextWrapping = TextWrapping.NoWrap;
        Grid.SetColumn(_status, 1);
        bottom.Children.Add(_status);

        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto"), Margin = new Thickness(28, 22, 28, 20) };
        page.Children.Add(header);
        Grid.SetRow(toolbar, 1);
        page.Children.Add(toolbar);
        Grid.SetRow(_message, 2);
        page.Children.Add(_message);
        Grid.SetRow(_stats, 3);
        page.Children.Add(_stats);
        Grid.SetRow(middle, 4);
        page.Children.Add(middle);
        Grid.SetRow(bottom, 5);
        page.Children.Add(bottom);
        Content = page;

        // A low window: the numbers make room for the map (it never gets less than its minimum height).
        SizeChanged += (_, e) => _stats.MaxHeight = e.NewSize.Height < 700 ? 0 : double.PositiveInfinity;
    }

    private readonly TextBlock _progressTitle = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progressBar = new() { Maximum = 1, Height = 6 };
    private readonly TextBlock _progressNumbers = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _progressPath = new() { TextTrimming = TextTrimming.PathSegmentEllipsis };
    private readonly Button _cancelButton = new() { Content = new IconLabel { Icon = "stop", Text = "Abbrechen" } };

    private void BuildProgressCard()
    {
        _progressBar.Theme = Ui.Theme("MatProgress");
        _progressPath.Themed("MatText.Faint");
        _progressNumbers.FontSize = MatTokens.Type.FsLg;
        _cancelButton.Click += (_, _) => ScanService.Current?.Cancel();
        _progressCard.Themed("MatCard");
        _progressCard.Padding = new Thickness(28, 24);
        _progressCard.VerticalAlignment = VerticalAlignment.Top;
        _progressCard.MaxWidth = 640;
        _progressCard.HorizontalAlignment = HorizontalAlignment.Left;
        _progressCard.IsVisible = false;
        _progressCard.Child = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { new MatIcon { Icon = "hard-drive", Size = 22 }.Use(MatIcon.ForegroundProperty, "Mat.AccentText"), _progressTitle } },
                _progressBar,
                _progressNumbers,
                _progressPath,
                new ActionBar { Margin = new Thickness(0, 6, 0, 0), Children = { _cancelButton } },
            },
        };
    }

    private readonly TextBlock _emptyTitle = new() { FontSize = MatTokens.Type.FsXl, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _emptyText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _emptyAction = new();

    private void BuildEmptyCard()
    {
        _emptyText.Themed("MatText.Muted");
        _emptyAction.Themed("MatButton.Primary");
        _emptyAction.Click += async (_, _) =>
        {
            if (_source is null)
            {
                (Ui.WindowOf(this) as MainWindow)?.ShowSources(createNew: true);
            }
            else
            {
                await ScanAsync();
            }
        };
        _emptyCard.Themed("MatCard");
        _emptyCard.Padding = new Thickness(32, 28);
        _emptyCard.MaxWidth = 620;
        _emptyCard.HorizontalAlignment = HorizontalAlignment.Left;
        _emptyCard.VerticalAlignment = VerticalAlignment.Top;
        _emptyCard.IsVisible = false;
        _emptyCard.Child = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new Image { Source = Ui.Logo, Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Left },
                _emptyTitle,
                _emptyText,
                new ActionBar { Margin = new Thickness(0, 8, 0, 0), Children = { _emptyAction } },
            },
        };
    }

    // ---- Events ----------------------------------------------------------------------------------------------------

    private void Wire()
    {
        _sourceChoice.SelectionChanged += (_, _) =>
        {
            if (!_isFilling)
            {
                OnSourceChosen(null, null);
            }
        };
        _analysisChoice.SelectionChanged += (_, _) =>
        {
            if (!_isFilling && _analysisChoice.SelectedIndex >= 0 && _analysisChoice.SelectedIndex < _analyses.Count)
            {
                _ = LoadAnalysisAsync(_analyses[_analysisChoice.SelectedIndex], null);
            }
        };
        _compareChoice.SelectionChanged += (_, _) =>
        {
            if (!_isFilling && _compareChoice.SelectedIndex >= 0 && _compareChoice.SelectedIndex < _comparable.Count)
            {
                _ = LoadComparisonAsync(_comparable[_compareChoice.SelectedIndex]);
            }
        };
        _search.TextChanged += (_, _) =>
        {
            // coalesced: a burst of keys is one search
            if (_searchPosted)
            {
                return;
            }

            _searchPosted = true;
            Dispatcher.UIThread.Post(() =>
            {
                _searchPosted = false;
                RefreshLists();
            }, DispatcherPriority.Background);
        };
        _typeChoice.SelectionChanged += (_, _) =>
        {
            _map.Highlight = SelectedCategory;
            RefreshLists();
            RefreshLegend();
        };
        _coloringChoice.SelectionChanged += (_, _) =>
        {
            var coloring = (MapColoring)Math.Max(0, _coloringChoice.SelectedIndex);
            _map.Coloring = coloring;
            SettingsService.Remember(settings => settings with { MapColoring = coloring });
            RefreshLegend();
        };
        _scanButton.Click += async (_, _) => await ScanAsync();

        _map.NodeSelected += (_, node) => Choose(node, fromMap: true);
        _map.NodeActivated += (_, node) => ZoomTo(node.IsFolder ? node : node.Parent);
        _map.HoverChanged += (_, node) => ShowStatus(node ?? _selected);
        _map.MenuRequested += (_, node) => OpenContextMenu(_map);
        foreach (var list in new[] { _tree, _largest, _changes })
        {
            list.NodeSelected += (_, node) => Choose(node, fromMap: false);
            list.NodeActivated += (_, node) =>
            {
                if (node.IsFolder)
                {
                    ZoomTo(node);
                }
                else
                {
                    RunAction(() => FileActionService.Open(node));
                }
            };
            list.MenuRequested += (_, _) => OpenContextMenu(list);
        }

        _tree.ExpandRequested += (_, request) =>
        {
            if (request.Expand)
            {
                _expanded.Add(request.Node);
            }
            else
            {
                _expanded.Remove(request.Node);
            }

            RefreshTree();
        };

        _openButton.Click += (_, _) => RunOnSelected(FileActionService.Open);
        _revealButton.Click += (_, _) => RunOnSelected(FileActionService.Reveal);
        _copyButton.Click += async (_, _) =>
        {
            if (_selected is { } node)
            {
                await Ui.CopyTextAsync(this, node.FullPath);
                _status.Text = $"Pfad kopiert: {node.FullPath}";
            }
        };
        _zoomButton.Click += (_, _) => ZoomTo(_selected is { IsFolder: true } folder ? folder : _selected?.Parent);
        _deleteButton.Click += async (_, _) => await DeleteSelectedAsync();
        KeyDown += OnKeyDown;

        SourceService.Changed += (_, _) => Ui.Post(() =>
        {
            var current = _source?.Id;
            ReloadSources();
            if (current is { } id && _sources.All(source => source.Id != id))
            {
                OnSourceChosen(null, null);
            }
        });
        AnalysisService.Changed += (_, _) => Ui.Post(OnAnalysesChanged);
        AnalysisService.TreeChanged += (_, id) => Ui.Post(() =>
        {
            if (id == _analysis?.Id)
            {
                _treeVersion++;
                _map.Invalidate(relayout: true);
                RefreshLists();
                RefreshStats();
            }
        });
        ScanService.Started += (_, job) => Ui.Post(() => OnScanStarted(job));
        ScanService.Finished += (_, result) => Ui.Post(() => OnScanFinished(result.Job, result.Analysis, result.Error));
        _progressTimer.Tick += (_, _) => RefreshProgress();
    }

    private FileCategory? SelectedCategory => _typeChoice.SelectedIndex > 0 ? (FileCategory)(_typeChoice.SelectedIndex - 1) : null;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.F5:
                e.Handled = true;
                _ = ScanAsync();
                break;
            case Key.Delete:
            case Key.Back when OperatingSystem.IsMacOS() && e.KeyModifiers.HasFlag(KeyModifiers.Meta):
                e.Handled = true;
                _ = DeleteSelectedAsync();
                break;
            case Key.Back:
                e.Handled = true;
                ZoomOut();
                break;
        }
    }

    // ---- Sources and analyses --------------------------------------------------------------------------------------

    private void ReloadSources()
    {
        _sources = SourceService.List();
        _isFilling = true;
        try
        {
            _sourceChoice.ItemsSource = _sources.Select(source => $"{source.Name}  ·  {SourceService.PlaceOf(source)}").ToList();
            var wanted = _source?.Id ?? SettingsService.Get().LastSourceId;
            var index = _sources.ToList().FindIndex(source => source.Id == wanted);
            _sourceChoice.SelectedIndex = index >= 0 ? index : _sources.Count > 0 ? 0 : -1;
        }
        finally
        {
            _isFilling = false;
        }

        if (_source is null && _sourceChoice.SelectedIndex >= 0)
        {
            OnSourceChosen(null, null);
        }
        else if (_sources.Count == 0)
        {
            _source = null;
            ShowEmpty();
        }
    }

    private void OnSourceChosen(Guid? analysisId, Guid? compareWith)
    {
        _source = _sourceChoice.SelectedIndex >= 0 && _sourceChoice.SelectedIndex < _sources.Count ? _sources[_sourceChoice.SelectedIndex] : null;
        if (_source is { } source && SettingsService.Get().LastSourceId != source.Id)
        {
            SettingsService.Remember(settings => settings with { LastSourceId = source.Id });
        }

        _message.Hide();
        FillAnalyses(analysisId);
        var analysis = _analysisChoice.SelectedIndex >= 0 && _analysisChoice.SelectedIndex < _analyses.Count ? _analyses[_analysisChoice.SelectedIndex] : null;
        if (analysis is null)
        {
            _analysis = null;
            _root = null;
            _map.Root = null;
            ShowEmpty();
            return;
        }

        _ = LoadAnalysisAsync(analysis, compareWith);
    }

    private void FillAnalyses(Guid? wanted)
    {
        _analyses = _source is null ? [] : AnalysisService.List(_source.Id);
        _isFilling = true;
        try
        {
            _analysisChoice.ItemsSource = _analyses.Select(AnalysisText).ToList();
            var index = wanted is { } id ? _analyses.ToList().FindIndex(analysis => analysis.Id == id) : _analysis is { } current && current.SourceId == _source?.Id
                ? _analyses.ToList().FindIndex(analysis => analysis.Id == current.Id)
                : -1;
            _analysisChoice.SelectedIndex = index >= 0 ? index : _analyses.Count > 0 ? 0 : -1;
            _analysisChoice.IsEnabled = _analyses.Count > 1;
        }
        finally
        {
            _isFilling = false;
        }

        _scanButton.IsEnabled = _source is not null && SourceService.CanScanHere(_source) && !ScanService.IsScanning;
        ToolTip.SetTip(_scanButton, _source is not null && !SourceService.CanScanHere(_source)
            ? $"„{_source.Name}“ lässt sich nur auf {_source.DeviceName} analysieren."
            : "Die Quelle jetzt neu analysieren (F5)");
    }

    private void OnAnalysesChanged()
    {
        var current = _analysis?.Id;
        FillAnalyses(current);
        if (current is { } id && _analyses.All(analysis => analysis.Id != id))
        {
            OnSourceChosen(null, null); // the analysis shown was deleted
            return;
        }

        if (_analysis is null && _analyses.Count > 0)
        {
            _ = LoadAnalysisAsync(_analyses[0], null);
            return;
        }

        FillComparable(_comparison?.Previous.Id);
    }

    private static string AnalysisText(Analysis analysis)
    {
        var text = $"{Ui.FormatDateTime(analysis.StartedAt)}  ·  {Ui.FormatSize(analysis.TotalBytes)}";
        return string.Equals(analysis.DeviceName, Core.Infrastructure.AppInfo.MachineName, StringComparison.OrdinalIgnoreCase) || analysis.DeviceName.Length == 0
            ? text
            : $"{text}  ·  {analysis.DeviceName}";
    }

    private async Task LoadAnalysisAsync(Analysis analysis, Guid? compareWith)
    {
        var version = ++_loadVersion;
        _analysis = analysis;
        _status.Text = "Lade die Analyse …";
        var tree = await Task.Run(() => AnalysisService.LoadTree(analysis.Id));
        if (version != _loadVersion)
        {
            return;
        }

        if (tree is null)
        {
            _root = null;
            _map.Root = null;
            _message.Show("Der Inhalt dieser Analyse ist auf diesem Computer nicht vorhanden.", InfoSeverity.Warning);
            ShowEmpty();
            return;
        }

        _root = tree;
        _treeVersion++;
        _selected = null;
        _expanded.Clear();
        _expanded.Add(tree);
        _map.Selected = null;
        _map.Root = tree;
        ShowContent();
        RefreshStats();
        RefreshBreadcrumb();
        RefreshTree();
        RefreshLists();
        RefreshLegend();
        UpdateActions();
        ShowStatus(null);
        if (!AnalysisService.IsComplete(analysis.Id))
        {
            var own = string.Equals(analysis.DeviceName, Core.Infrastructure.AppInfo.MachineName, StringComparison.OrdinalIgnoreCase);
            _status.Text = own
                ? "Gespeicherte Analyse: kleine Dateien sind zusammengefasst. Für alle Einzelheiten neu analysieren."
                : $"Analyse von {analysis.DeviceName}: kleine Dateien sind zusammengefasst.";
        }

        FillComparable(compareWith);
        var previous = compareWith is { } wantedId ? AnalysisService.Get(wantedId) : AnalysisService.Previous(analysis);
        await LoadComparisonAsync(previous);
    }

    private void FillComparable(Guid? wanted)
    {
        var older = _analysis is null ? [] : _analyses.Where(other => other.StartedAt < _analysis.StartedAt).ToList();
        _comparable = [null, .. older];
        _isFilling = true;
        try
        {
            _compareChoice.ItemsSource = new[] { "Kein Vergleich" }.Concat(older.Select(AnalysisText)).ToList();
            var index = wanted is { } id ? older.FindIndex(analysis => analysis.Id == id) + 1 : _comparison is { } current ? older.FindIndex(analysis => analysis.Id == current.Previous.Id) + 1 : 0;
            _compareChoice.SelectedIndex = Math.Max(0, index);
            _compareChoice.IsEnabled = older.Count > 0;
        }
        finally
        {
            _isFilling = false;
        }
    }

    private async Task LoadComparisonAsync(Analysis? previous)
    {
        if (_analysis is not { } analysis || previous is null || previous.Id == analysis.Id)
        {
            SetComparison(null);
            return;
        }

        var version = _loadVersion;
        var comparison = await Task.Run(() => AnalysisService.Compare(analysis, previous));
        if (version == _loadVersion)
        {
            SetComparison(comparison);
            FillComparable(previous.Id);
        }
    }

    private void SetComparison(TreeComparison? comparison)
    {
        _comparison = comparison;
        _map.Comparison = comparison;
        ToolTip.SetTip(_changesTab, comparison is null ? "Was seit einer früheren Analyse gewachsen oder geschrumpft ist" : $"Seit {Ui.FormatDateTime(comparison.Previous.StartedAt)}");
        RefreshStats();
        RefreshTree();
        RefreshLists();
        RefreshLegend();
    }

    // ---- Lists -----------------------------------------------------------------------------------------------------

    private void RefreshTree()
    {
        if (_root is null)
        {
            _tree.SetRows([], null);
            return;
        }

        var rows = new List<NodeRow>();
        var search = (_search.Text ?? "").Trim();
        if (search.Length > 0)
        {
            foreach (var node in TreeTools.Search(_root, search, SelectedCategory, FlatRows))
            {
                rows.Add(FlatRow(node, _root.Size));
            }

            _tree.EmptyText = $"Nichts gefunden für „{search}“.";
            _tree.SetRows(rows, _selected);
            return;
        }

        AddTreeRows(rows, _root, 0);
        _tree.EmptyText = "Hier ist nichts.";
        _tree.SetRows(rows, _selected);
    }

    private void AddTreeRows(List<NodeRow> rows, ScanNode node, int depth)
    {
        rows.Add(TreeRow(node, depth));
        if (!_expanded.Contains(node))
        {
            return;
        }

        var shown = 0;
        foreach (var child in node.Children)
        {
            if (shown++ >= ChildrenPerFolder)
            {
                rows.Add(new NodeRow { Node = node, Name = $"… {node.Children.Count - ChildrenPerFolder} weitere (siehe Karte)", Depth = depth + 1, IsNote = true, Icon = "more-horizontal" });
                break;
            }

            AddTreeRows(rows, child, depth + 1);
        }
    }

    private NodeRow TreeRow(ScanNode node, int depth)
    {
        var change = _comparison?.ChangeOf(node) ?? NodeChange.Unknown;
        return new NodeRow
        {
            Node = node,
            Name = node.IsRoot ? node.Name : node.Name,
            Depth = depth,
            CanExpand = node.HasChildren && node.IsFolder,
            IsExpanded = _expanded.Contains(node),
            Size = node.Size,
            Share = node.Parent is { Size: > 0 } parent ? (double)node.Size / parent.Size : 1,
            SizeText = Ui.FormatSize(node.Size),
            ChangeText = ChangeText(change),
            Change = change.Kind,
            Icon = node.Kind switch { NodeKind.Folder => node.IsRoot ? "hard-drive" : "folder", NodeKind.Rest => "more-horizontal", _ => FileCategories.Info(FileCategories.Of(node)).Icon },
            Category = node.IsFile ? FileCategories.Of(node) : null,
        };
    }

    private NodeRow FlatRow(ScanNode node, long whole)
    {
        var change = _comparison?.ChangeOf(node) ?? NodeChange.Unknown;
        var folder = node.Parent is { } parent ? parent.IsRoot ? parent.Name : parent.RelativeKey.Replace('/', Path.DirectorySeparatorChar) : "";
        return new NodeRow
        {
            Node = node,
            Name = node.Name,
            Detail = folder,
            Size = node.Size,
            Share = whole > 0 ? (double)node.Size / whole : 0,
            SizeText = Ui.FormatSize(node.Size),
            ChangeText = ChangeText(change),
            Change = change.Kind,
            Icon = node.IsFolder ? "folder" : FileCategories.Info(FileCategories.Of(node)).Icon,
            Category = node.IsFile ? FileCategories.Of(node) : null,
        };
    }

    private static string ChangeText(NodeChange change) => change.Kind switch
    {
        ChangeKind.New => "neu",
        ChangeKind.Grown or ChangeKind.Shrunk => Ui.FormatDelta(change.Delta),
        _ => "",
    };

    private void RefreshLists()
    {
        if (_root is null)
        {
            _largest.SetRows([], null);
            _changes.SetRows([], null);
            _typeList.Children.Clear();
            return;
        }

        RefreshTree();
        var search = (_search.Text ?? "").Trim();
        var files = search.Length > 0
            ? TreeTools.Search(_root, search, SelectedCategory, FlatRows).Where(node => node.IsFile).ToList()
            : TreeTools.LargestFiles(_root, SelectedCategory, FlatRows);
        _largest.SetRows(files.Select(node => FlatRow(node, _root.Size)).ToList(), _selected);

        if (_comparison is { } comparison)
        {
            var minimum = Math.Max(1024 * 1024, _root.Size / 2000);
            if (_changeCache is not { } cached || cached.Comparison != comparison || cached.Version != _treeVersion)
            {
                cached = (comparison, _treeVersion, comparison.TopChanges(_root, 200, minimum));
                _changeCache = cached;
            }

            var entries = cached.Entries;
            _changes.EmptyText = $"Seit {Ui.FormatDateTime(comparison.Previous.StartedAt)} hat sich nichts Nennenswertes verändert.";
            _changes.SetRows(entries.Select(entry => new NodeRow
            {
                Node = entry.Node,
                Name = entry.Name,
                Detail = entry.Key.Contains('/') ? entry.Key[..entry.Key.LastIndexOf('/')].Replace('/', Path.DirectorySeparatorChar) : _root.Name,
                Size = entry.Size,
                Share = Math.Abs(entry.Delta) / (double)Math.Max(1, entries.Max(other => Math.Abs(other.Delta))),
                SizeText = entry.Kind == ChangeKind.Removed ? "gelöscht" : Ui.FormatSize(entry.Size),
                ChangeText = entry.Kind == ChangeKind.New ? "neu " + Ui.FormatDelta(entry.Delta) : Ui.FormatDelta(entry.Delta),
                Change = entry.Kind,
                Icon = entry.IsFolder ? "folder" : FileCategories.Info(FileCategories.Of(FileCategories.ExtensionOf(entry.Name))).Icon,
                Category = entry.IsFolder ? null : FileCategories.Of(FileCategories.ExtensionOf(entry.Name)),
                IsNote = entry.Node is null,
            }).ToList(), _selected);
        }
        else
        {
            _changes.EmptyText = _analyses.Count > 1 ? "Wähle oben bei „Vergleich“ eine frühere Analyse." : "Nach der nächsten Analyse dieser Quelle siehst du hier, was gewachsen und was geschrumpft ist.";
            _changes.SetRows([], null);
        }

        RefreshTypes();
    }

    private void RefreshTypes()
    {
        _typeList.Children.Clear();
        if (_root is null)
        {
            return;
        }

        var totalsRoot = _map.Root ?? _root;
        if (_totalsCache is not { } cachedTotals || cachedTotals.Root != totalsRoot || cachedTotals.Version != _treeVersion)
        {
            cachedTotals = (totalsRoot, _treeVersion, TreeTools.Totals(totalsRoot));
            _totalsCache = cachedTotals;
        }

        var totals = cachedTotals.Totals;
        var whole = Math.Max(1, totals.Sum(total => total.Bytes));
        var dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        foreach (var total in totals)
        {
            var category = total.Category.Category;
            var swatch = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(4), Background = DataColors.CategoryBrush(category, dark), VerticalAlignment = VerticalAlignment.Center };
            var bar = new Border { Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
            bar.Use(Border.BackgroundProperty, "Mat.Surface3");
            bar.Child = new Border { Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(2, 120.0 * total.Bytes / whole), Background = DataColors.CategoryBrush(category, dark) };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,130,Auto"), Margin = new Thickness(4, 2) };
            row.Children.Add(swatch);
            var name = new StackPanel
            {
                Margin = new Thickness(10, 0),
                Children =
                {
                    new TextBlock { Text = total.Category.Name, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = $"{Ui.FormatCount(total.Files)} Dateien · {Ui.FormatShare(total.Bytes, whole)}", FontSize = MatTokens.Type.FsXs }.Themed("MatText.Muted"),
                },
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            Grid.SetColumn(bar, 2);
            row.Children.Add(bar);
            var size = new TextBlock { Text = Ui.FormatSize(total.Bytes), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 70, TextAlignment = TextAlignment.Right };
            Grid.SetColumn(size, 3);
            row.Children.Add(size);
            var button = new Button { Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(6, 6) }.Themed("MatButton.Ghost");
            if (SelectedCategory == category)
            {
                button.Use(Button.BackgroundProperty, "Mat.AccentSoft");
            }

            AutomationProperties.SetName(button, $"{total.Category.Name}: {Ui.FormatSize(total.Bytes)}");
            ToolTip.SetTip(button, SelectedCategory == category ? "Wieder alle Dateitypen zeigen" : $"Nur {total.Category.Name} auf der Karte hervorheben");
            button.Click += (_, _) => _typeChoice.SelectedIndex = SelectedCategory == category ? 0 : (int)category + 1;
            _typeList.Children.Add(button);
        }
    }

    // ---- Map, path, legend, numbers --------------------------------------------------------------------------------

    private void Choose(ScanNode node, bool fromMap)
    {
        _selected = node;
        _map.Selected = node;
        if (!fromMap && _map.Root is { } mapRoot && !IsBelow(node, mapRoot))
        {
            ZoomTo(_root);
        }

        if (fromMap)
        {
            // open the folders above it in the tree, so the row is there
            var changed = false;
            for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            {
                changed |= _expanded.Add(parent);
            }

            if (changed && (_search.Text ?? "").Length == 0)
            {
                RefreshTree();
            }

            _tree.Select(node);
            _largest.Select(node);
            _changes.Select(node);
        }

        UpdateActions();
        ShowStatus(node);
    }

    private static bool IsBelow(ScanNode node, ScanNode folder)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, folder))
            {
                return true;
            }
        }

        return false;
    }

    private void ZoomTo(ScanNode? folder)
    {
        if (folder is null || !folder.IsFolder || folder.Size <= 0)
        {
            return;
        }

        _map.Root = folder;
        _expanded.Add(folder);
        for (var parent = folder.Parent; parent is not null; parent = parent.Parent)
        {
            _expanded.Add(parent);
        }

        RefreshBreadcrumb();
        RefreshTree();
        RefreshTypes();
        _tree.Select(_selected ?? folder);
    }

    private void ZoomOut() => ZoomTo(_map.Root?.Parent);

    private void RefreshBreadcrumb()
    {
        _breadcrumb.Children.Clear();
        if (_map.Root is not { } mapRoot)
        {
            return;
        }

        var path = mapRoot.PathFromRoot();
        for (var index = 0; index < path.Count; index++)
        {
            var node = path[index];
            if (index > 0)
            {
                _breadcrumb.Children.Add(new MatIcon { Icon = "chevron-right", Size = 12, VerticalAlignment = VerticalAlignment.Center }.Use(MatIcon.ForegroundProperty, "Mat.TextFaint"));
            }

            var isLast = index == path.Count - 1;
            var label = index == 0 ? (_source?.Name ?? node.Name) : node.Name;
            var crumb = new Button
            {
                Content = new TextBlock { Text = isLast ? $"{label}  ·  {Ui.FormatSize(node.Size)}" : label, FontWeight = isLast ? FontWeight.SemiBold : FontWeight.Normal },
                Padding = new Thickness(6, 2),
            }.Themed(isLast ? "MatButton.Ghost" : "MatButton.Link");
            ToolTip.SetTip(crumb, node.FullPath);
            crumb.Click += (_, _) => ZoomTo(node);
            _breadcrumb.Children.Add(crumb);
        }
    }

    private void RefreshLegend()
    {
        _legend.Children.Clear();
        var dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        if (_map.Coloring == MapColoring.ByChange)
        {
            if (_comparison is null)
            {
                _legend.Children.Add(new TextBlock { Text = "Zum Färben nach Veränderung oben eine frühere Analyse zum Vergleich wählen." }.Themed("MatText.Muted"));
                return;
            }

            AddLegend(DataColors.Change(ChangeKind.Shrunk, 1, dark), "geschrumpft");
            AddLegend(DataColors.Change(ChangeKind.Same, 0, dark), "unverändert / unbekannt");
            AddLegend(DataColors.Change(ChangeKind.Grown, 0.3, dark), "gewachsen");
            AddLegend(DataColors.Change(ChangeKind.New, 1, dark), $"neu seit {Ui.FormatDateTime(_comparison.Previous.StartedAt)}");
            return;
        }

        foreach (var info in FileCategories.Palette)
        {
            var item = AddLegend(DataColors.Category(info.Category, dark), info.Name);
            if (SelectedCategory is { } wanted && wanted != info.Category)
            {
                item.Opacity = 0.45;
            }
        }
    }

    private Control AddLegend(Color color, string text)
    {
        var item = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 2, 16, 2),
            Children =
            {
                new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = text, FontSize = MatTokens.Type.FsSm, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        _legend.Children.Add(item);
        return item;
    }

    private void RefreshStats()
    {
        _stats.Children.Clear();
        if (_analysis is not { } analysis || _root is null)
        {
            _stats.IsVisible = false;
            return;
        }

        _stats.IsVisible = true;
        AddStat("Belegt", Ui.FormatSize(_root.Size), analysis.VolumeTotalBytes is { } volume ? $"von {Ui.FormatSize(volume)} des Laufwerks" : $"in {Ui.FormatCount(_root.FileCount)} Dateien");
        if (analysis.VolumeFreeBytes is { } free)
        {
            AddStat("Frei", Ui.FormatSize(free), analysis.VolumeTotalBytes is { } total ? $"{Ui.FormatShare(free, total)} des Laufwerks" : "");
        }

        AddStat("Dateien", Ui.FormatCount(_root.FileCount), $"in {Ui.FormatCount(_root.FolderCount)} Ordnern");
        if (_comparison is { } comparison)
        {
            var delta = _root.Size - comparison.PreviousTotal;
            AddStat("Veränderung", Ui.FormatDelta(delta), $"seit {Ui.FormatDateTime(comparison.Previous.StartedAt)}", delta > 0 ? "arrow-up" : delta < 0 ? "arrow-down" : null);
        }

        var age = Ui.FormatDateTime(analysis.StartedAt);
        AddStat("Analysiert", age, analysis.SkippedCount > 0
            ? $"{Ui.FormatCount(analysis.SkippedCount)} Ordner ohne Zugriff"
            : $"in {Ui.FormatDuration(analysis.Duration)}");
    }

    private void AddStat(string label, string value, string detail, string? icon = null)
    {
        var valueRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (icon is not null)
        {
            valueRow.Children.Add(new MatIcon { Icon = icon, Size = 16, VerticalAlignment = VerticalAlignment.Center });
        }

        valueRow.Children.Add(new TextBlock { Text = value, FontSize = MatTokens.Type.FsXl, FontWeight = FontWeight.SemiBold });
        var tile = new Border
        {
            MinWidth = 150,
            Padding = new Thickness(14, 10),
            Margin = new Thickness(0, 0, 10, 8),
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = label, FontSize = MatTokens.Type.FsXs }.Themed("MatText.Muted"),
                    valueRow,
                    new TextBlock { Text = detail, FontSize = MatTokens.Type.FsXs }.Themed("MatText.Faint"),
                },
            },
        }.Themed("MatCard");
        _stats.Children.Add(tile);
    }

    private void ShowStatus(ScanNode? node)
    {
        if (node is null)
        {
            _status.Text = _root is null ? "" : "Klicken wählt, Doppelklick zoomt hinein, Rechtsklick öffnet das Menü.";
            return;
        }

        var parts = new List<string> { node.IsRoot ? node.Name : node.FullPath, Ui.FormatSize(node.Size) };
        if (node.Parent is { Size: > 0 } parent)
        {
            parts.Add($"{Ui.FormatShare(node.Size, parent.Size)} von {parent.Name}");
        }

        if (node.IsFile)
        {
            parts.Add(FileCategories.Info(FileCategories.Of(node)).Name);
        }
        else if (node.IsFolder)
        {
            parts.Add($"{Ui.FormatCount(node.FileCount)} Dateien");
        }

        if (_comparison?.ChangeOf(node) is { IsKnown: true } change && ChangeText(change) is { Length: > 0 } text)
        {
            parts.Add(text);
        }

        _status.Text = string.Join("  ·  ", parts);
    }

    // ---- Actions ---------------------------------------------------------------------------------------------------

    private void UpdateActions()
    {
        var node = _selected;
        var real = node is not null && node.Kind != NodeKind.Rest;
        _openButton.IsEnabled = real;
        _revealButton.IsEnabled = real;
        _copyButton.IsEnabled = real;
        _zoomButton.IsEnabled = node is not null && (node.IsFolder ? !ReferenceEquals(node, _map.Root) : node.Parent is not null && !ReferenceEquals(node.Parent, _map.Root));
        _deleteButton.IsEnabled = FileActionService.CanDelete(node) && _analysis is not null;
        _deleteButton.IsVisible = !Core.Business.AccessPolicy.IsDeleteDisabled;
        if (_analysis is { } analysis)
        {
            ToolTip.SetTip(_deleteButton, FileActionService.UsesTrash(analysis) ? "In den Papierkorb verschieben (Entf)" : "Endgültig löschen – eine Netzwerkfreigabe hat keinen Papierkorb (Entf)");
        }
    }

    private void RunOnSelected(Action<ScanNode> action)
    {
        if (_selected is { } node)
        {
            RunAction(() => action(node));
        }
    }

    private void RunAction(Action action)
    {
        try
        {
            _message.Hide();
            action();
        }
        catch (BusinessException exception)
        {
            _message.Show(exception.Message, InfoSeverity.Error);
        }
    }

    private async Task DeleteSelectedAsync()
    {
        if (_selected is not { } node || _analysis is not { } analysis || !FileActionService.CanDelete(node))
        {
            return;
        }

        var trash = FileActionService.UsesTrash(analysis);
        if (SettingsService.Get().ConfirmDelete)
        {
            var what = node.IsFolder ? $"den Ordner „{node.Name}“ mit {Ui.FormatCount(node.FileCount)} Dateien" : $"„{node.Name}“";
            var confirmed = await ConfirmDialog.AskAsync(Ui.WindowOf(this),
                trash ? "In den Papierkorb verschieben?" : "Endgültig löschen?",
                trash
                    ? $"ThixSync verschiebt {what} ({Ui.FormatSize(node.Size)}) in den Papierkorb. Von dort lässt es sich wiederherstellen.\n\n{node.FullPath}"
                    : $"{what} ({Ui.FormatSize(node.Size)}) liegt auf einer Netzwerkfreigabe ohne Papierkorb und wird endgültig gelöscht.\n\n{node.FullPath}",
                trash ? "In den Papierkorb" : "Endgültig löschen",
                isDestructive: true);
            if (!confirmed)
            {
                return;
            }
        }

        try
        {
            _message.Hide();
            var parent = node.Parent;
            var result = FileActionService.Delete(analysis, node);
            _selected = null;
            _map.Selected = null;
            _message.Show(result.MovedToTrash
                ? $"„{result.Name}“ liegt im Papierkorb – {Ui.FormatSize(result.Bytes)} werden frei, sobald er geleert ist."
                : $"„{result.Name}“ wurde gelöscht – {Ui.FormatSize(result.Bytes)} frei.", InfoSeverity.Success);
            if (parent is not null)
            {
                Choose(parent, fromMap: true);
            }
        }
        catch (BusinessException exception)
        {
            _message.Show(exception.Message, InfoSeverity.Error);
        }
    }

    private void OpenContextMenu(Control target)
    {
        if (_selected is not { } node)
        {
            return;
        }

        var real = node.Kind != NodeKind.Rest;
        var items = new List<MenuItem>
        {
            MenuEntry("Öffnen", "external", real, () => RunOnSelected(FileActionService.Open)),
            MenuEntry(OperatingSystem.IsMacOS() ? "Im Finder zeigen" : "Im Explorer zeigen", "folder-open", real, () => RunOnSelected(FileActionService.Reveal)),
            MenuEntry("Pfad kopieren", "copy", real, () => _ = Ui.CopyTextAsync(this, node.FullPath)),
            MenuEntry("Hineinzoomen", "zoom-in", node.IsFolder || node.Parent is not null, () => ZoomTo(node.IsFolder ? node : node.Parent)),
        };
        if (!Core.Business.AccessPolicy.IsDeleteDisabled)
        {
            items.Add(MenuEntry("Löschen …", "trash", FileActionService.CanDelete(node), () => _ = DeleteSelectedAsync()));
        }

        var menu = new ContextMenu { ItemsSource = items, Placement = PlacementMode.Pointer };
        menu.Open(target);
    }

    private static MenuItem MenuEntry(string text, string icon, bool enabled, Action action)
    {
        var item = new MenuItem { Header = text, Icon = new MatIcon { Icon = icon, Size = 16 }, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    // ---- Scanning --------------------------------------------------------------------------------------------------

    private void OnScanStarted(ScanJob job)
    {
        _scanButton.IsEnabled = false;
        if (job.SourceId != _source?.Id)
        {
            if (_analysis is null)
            {
                ShowSource(job.SourceId);
            }
            else
            {
                return;
            }
        }

        _progressTitle.Text = $"Analysiere „{job.Source.Name}“ …";
        _progressCard.IsVisible = true;
        _content.IsVisible = false;
        _emptyCard.IsVisible = false;
        _stats.IsVisible = false;
        RefreshProgress();
        _progressTimer.Start();
    }

    private void RefreshProgress()
    {
        if (ScanService.Current is not { } job)
        {
            return;
        }

        var progress = job.Progress;
        _progressBar.IsIndeterminate = progress.ExpectedBytes is not > 0;
        if (progress.ExpectedBytes is { } expected and > 0)
        {
            _progressBar.Value = Math.Clamp((double)progress.Bytes / expected, 0, 1);
        }

        _progressNumbers.Text = $"{Ui.FormatSize(progress.Bytes)}  ·  {Ui.FormatCount(progress.Files)} Dateien  ·  {Ui.FormatCount(progress.Folders)} Ordner  ·  {Ui.FormatDuration(Core.Infrastructure.AppClock.UtcNow - job.StartedAt)}";
        _progressPath.Text = progress.CurrentPath;
    }

    private void OnScanFinished(ScanJob job, Analysis? analysis, string? error)
    {
        _progressTimer.Stop();
        _progressCard.IsVisible = false;
        _scanButton.IsEnabled = _source is not null && SourceService.CanScanHere(_source);
        if (job.SourceId != _source?.Id)
        {
            if (_root is not null)
            {
                ShowContent();
            }

            return;
        }

        if (analysis is not null)
        {
            ShowSource(job.SourceId, analysis.Id);
            var previous = AnalysisService.Previous(analysis);
            _message.Show(previous is null
                ? $"Analyse fertig: {Ui.FormatSize(analysis.TotalBytes)} in {Ui.FormatCount(analysis.FileCount)} Dateien ({Ui.FormatDuration(analysis.Duration)})."
                : $"Analyse fertig: {Ui.FormatSize(analysis.TotalBytes)}, {Ui.FormatDelta(analysis.TotalBytes - previous.TotalBytes)} seit {Ui.FormatDateTime(previous.StartedAt)}.", InfoSeverity.Success);
            return;
        }

        if (error is not null)
        {
            _message.Show(error, InfoSeverity.Error);
        }
        else
        {
            _message.Show("Die Analyse wurde abgebrochen.", InfoSeverity.Info);
        }

        if (_root is not null)
        {
            ShowContent();
        }
        else
        {
            ShowEmpty();
        }
    }

    private void ShowContent()
    {
        if (ScanService.Current is { } job && job.SourceId == _source?.Id)
        {
            return;
        }

        _content.IsVisible = true;
        _emptyCard.IsVisible = false;
        _progressCard.IsVisible = false;
        _stats.IsVisible = true;
    }

    private void ShowEmpty()
    {
        _content.IsVisible = false;
        _stats.IsVisible = false;
        if (ScanService.Current is { } job && job.SourceId == _source?.Id)
        {
            _emptyCard.IsVisible = false;
            return;
        }

        _emptyCard.IsVisible = true;
        _status.Text = "";
        if (_source is null)
        {
            _emptyTitle.Text = "Noch keine Quelle";
            _emptyText.Text = "Füge ein Laufwerk, einen Ordner oder eine Netzwerkfreigabe (SMB) hinzu. ThixSync zeigt dann auf einer Karte, was wie viel Platz belegt.";
            _emptyAction.Content = new IconLabel { Icon = "plus", Text = "Quelle hinzufügen" };
            _emptyAction.IsEnabled = true;
        }
        else
        {
            var canScan = SourceService.CanScanHere(_source);
            _emptyTitle.Text = $"„{_source.Name}“ ist noch nicht analysiert";
            _emptyText.Text = canScan
                ? $"Die Analyse liest {_source.Path} mit allen Unterordnern. Das dauert je nach Größe Sekunden bis wenige Minuten; danach zeigt die Karte, was den Platz belegt."
                : $"Diese Quelle gehört zu {_source.DeviceName} und lässt sich nur dort analysieren. Sobald es dort eine Analyse gibt, erscheint sie hier über die Cloud.";
            _emptyAction.Content = new IconLabel { Icon = "refresh", Text = "Jetzt analysieren" };
            _emptyAction.IsEnabled = canScan && !ScanService.IsScanning;
        }
    }
}
