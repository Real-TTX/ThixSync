using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using MatDesktop;
using ThixSync.Core.Business;
using ThixSync.Core.Models;

namespace ThixSync.Controls;

/// <summary>One row of a <see cref="NodeList"/>: a file or folder (or a line of the changes) with what the columns show.</summary>
public sealed class NodeRow
{
    public required ScanNode? Node { get; init; }

    public required string Name { get; init; }

    /// <summary>A second line: the folder of a file in a flat list (empty in the tree).</summary>
    public string Detail { get; init; } = "";

    public int Depth { get; init; }

    public bool CanExpand { get; init; }

    public bool IsExpanded { get; init; }

    public long Size { get; init; }

    /// <summary>The share of the parent (tree) or of the whole (flat lists), 0..1.</summary>
    public double Share { get; init; }

    public string SizeText { get; init; } = "";

    public string ChangeText { get; init; } = "";

    public ChangeKind Change { get; init; }

    public string Icon { get; init; } = "file";

    /// <summary>The colour swatch of the kind (files) or null (folders).</summary>
    public FileCategory? Category { get; init; }

    /// <summary>A row that only says something ("… 120 weitere"), not a thing.</summary>
    public bool IsNote { get; init; }
}

/// <summary>
/// A list of files and folders in columns (name with icon or colour of its kind, share bar, size, change), virtualised so that a
/// folder with thousands of things scrolls smoothly. In tree mode a row expands with its arrow (or →/←). ONE code base for
/// every list of the map page (the tree, the largest files, the changes); several on one page work.
/// </summary>
public sealed class NodeList : UserControl
{
    private readonly ListBox _list;
    private readonly TextBlock _empty;
    private bool _isSettingSelection;

    public NodeList()
    {
        _list = new ListBox
        {
            Theme = Ui.Theme("MatList"),
            ItemContainerTheme = Ui.Theme("MatChoiceRow"),
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new FuncDataTemplate<NodeRow>((row, _) => row is null ? new Panel() : BuildRow(row), supportsRecycling: false),
        };
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(ListBoxItem.MinHeightProperty, 30.0),
                new Setter(ListBoxItem.MarginProperty, new Thickness(0, 0, 0, 1)),
                new Setter(ListBoxItem.PaddingProperty, new Thickness(6, 0, 8, 0)),
            },
        });
        AutomationProperties.SetName(_list, "Dateien und Ordner");
        _list.SelectionChanged += (_, _) =>
        {
            if (!_isSettingSelection && _list.SelectedItem is NodeRow { Node: { } node, IsNote: false })
            {
                NodeSelected?.Invoke(this, node);
            }
        };
        _list.DoubleTapped += (_, _) =>
        {
            if (_list.SelectedItem is NodeRow { Node: { } node, IsNote: false })
            {
                NodeActivated?.Invoke(this, node);
            }
        };
        _list.KeyDown += OnListKeyDown;
        _empty = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 12), IsVisible = false }.Themed("MatText.Muted");
        Content = new Panel { Children = { _list, _empty } };
    }

    /// <summary>A row was chosen.</summary>
    public event EventHandler<ScanNode>? NodeSelected;

    /// <summary>Double click or Enter: the page zooms the map there.</summary>
    public event EventHandler<ScanNode>? NodeActivated;

    /// <summary>The arrow of a row (or → ←): expand or collapse.</summary>
    public event EventHandler<(ScanNode Node, bool Expand)>? ExpandRequested;

    /// <summary>The right button on a row.</summary>
    public event EventHandler<ScanNode>? MenuRequested;

    /// <summary>The text when there are no rows.</summary>
    public string EmptyText { get; set; } = "";

    public IReadOnlyList<NodeRow> Rows { get; private set; } = [];

    public ListBox ListBox => _list;

    public void SetRows(IReadOnlyList<NodeRow> rows, ScanNode? selected)
    {
        Rows = rows;
        _isSettingSelection = true;
        try
        {
            _list.ItemsSource = rows;
            _list.SelectedItem = selected is null ? null : rows.FirstOrDefault(row => ReferenceEquals(row.Node, selected));
        }
        finally
        {
            _isSettingSelection = false;
        }

        _empty.Text = EmptyText;
        _empty.IsVisible = rows.Count == 0 && EmptyText.Length > 0;
        if (_list.SelectedItem is { } item)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _list.ScrollIntoView(item), Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>Chooses the row of a node (without raising <see cref="NodeSelected"/>).</summary>
    public void Select(ScanNode? node)
    {
        _isSettingSelection = true;
        try
        {
            var row = node is null ? null : Rows.FirstOrDefault(candidate => ReferenceEquals(candidate.Node, node));
            _list.SelectedItem = row;
            if (row is not null)
            {
                _list.ScrollIntoView(row);
            }
        }
        finally
        {
            _isSettingSelection = false;
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (_list.SelectedItem is not NodeRow { Node: { } node } row || row.IsNote)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Right when row.CanExpand && !row.IsExpanded:
                e.Handled = true;
                ExpandRequested?.Invoke(this, (node, true));
                break;
            case Key.Left when row.CanExpand && row.IsExpanded:
                e.Handled = true;
                ExpandRequested?.Invoke(this, (node, false));
                break;
            case Key.Left when node.Parent is { } parent && Rows.Any(candidate => ReferenceEquals(candidate.Node, parent)):
                e.Handled = true;
                Select(parent);
                NodeSelected?.Invoke(this, parent);
                break;
            case Key.Enter:
                e.Handled = true;
                NodeActivated?.Invoke(this, node);
                break;
        }
    }

    private Control BuildRow(NodeRow row)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,64,84,96"), Height = row.Detail.Length > 0 ? 44 : 28 };

        // indent and the arrow
        var arrowSlot = new Panel { Width = 18 + row.Depth * 14, HorizontalAlignment = HorizontalAlignment.Left };
        if (row.CanExpand)
        {
            var arrow = new Button
            {
                Theme = Ui.Theme("MatButton.Ghost"),
                Padding = new Thickness(0),
                Width = 18,
                Height = 18,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Content = new MatIcon { Icon = row.IsExpanded ? "chevron-down" : "chevron-right", Size = 14 },
                Focusable = false,
            };
            AutomationProperties.SetName(arrow, row.IsExpanded ? "Zuklappen" : "Aufklappen");
            arrow.Click += (_, e) =>
            {
                e.Handled = true;
                if (row.Node is { } node)
                {
                    ExpandRequested?.Invoke(this, (node, !row.IsExpanded));
                }
            };
            arrowSlot.Children.Add(arrow);
        }

        grid.Children.Add(arrowSlot);

        // the icon of a folder, or the colour of the kind of a file
        Control mark = row.Category is { } category && row.Node?.IsFile == true
            ? new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(4, 0, 9, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = DataColors.CategoryBrush(category, ActualThemeVariant == ThemeVariant.Dark),
            }
            : new MatIcon { Icon = row.Icon, Size = 16, Margin = new Thickness(2, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        if (row.IsNote || row.Node?.Kind == NodeKind.Rest)
        {
            mark.Opacity = 0.6;
        }

        Grid.SetColumn(mark, 1);
        grid.Children.Add(mark);

        var name = new TextBlock { Text = row.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        if (row.IsNote || row.Node?.Kind == NodeKind.Rest)
        {
            name.Themed("MatText.Muted");
        }

        Control nameCell = name;
        if (row.Detail.Length > 0)
        {
            nameCell = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    name,
                    new TextBlock { Text = row.Detail, TextTrimming = TextTrimming.PathSegmentEllipsis, FontSize = MatTokens.Type.FsXs }.Themed("MatText.Faint"),
                },
            };
        }

        ToolTip.SetTip(nameCell, row.Detail.Length > 0 ? $"{row.Name}\n{row.Detail}" : row.Name);
        Grid.SetColumn(nameCell, 2);
        grid.Children.Add(nameCell);

        if (!row.IsNote)
        {
            // the share as a thin bar (in the accent: it is the size, not a kind)
            var bar = new Border { Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(8, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            bar.Use(Border.BackgroundProperty, "Mat.Surface3");
            var fill = new Border { Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(row.Share > 0 ? 2 : 0, 50 * Math.Clamp(row.Share, 0, 1)) };
            fill.Use(Border.BackgroundProperty, "Mat.Accent");
            bar.Child = fill;
            Grid.SetColumn(bar, 3);
            grid.Children.Add(bar);
            ToolTip.SetTip(bar, $"{row.Share * 100:0.#} %");

            var size = new TextBlock { Text = row.SizeText, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontFeatures = TabularFigures };
            Grid.SetColumn(size, 4);
            grid.Children.Add(size);

            if (row.ChangeText.Length > 0)
            {
                var change = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                var icon = row.Change switch
                {
                    ChangeKind.Grown or ChangeKind.New => "arrow-up",
                    ChangeKind.Shrunk or ChangeKind.Removed => "arrow-down",
                    _ => null,
                };
                if (icon is not null)
                {
                    change.Children.Add(new MatIcon { Icon = icon, Size = 12 }.Use(MatIcon.ForegroundProperty, "Mat.TextMuted"));
                }

                change.Children.Add(new TextBlock { Text = row.ChangeText, FontSize = MatTokens.Type.FsSm, FontFeatures = TabularFigures }.Themed("MatText.Muted"));
                Grid.SetColumn(change, 5);
                grid.Children.Add(change);
            }
        }

        if (row.Node is { } rowNode && !row.IsNote)
        {
            grid.ContextRequested += (_, e) =>
            {
                e.Handled = true;
                Select(rowNode);
                NodeSelected?.Invoke(this, rowNode);
                MenuRequested?.Invoke(this, rowNode);
            };
        }

        return grid;
    }

    private static readonly FontFeatureCollection TabularFigures = [new FontFeature { Tag = "tnum", Value = 1 }];
}
