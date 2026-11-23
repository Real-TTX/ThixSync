using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using MatDesktop;

namespace ThixSync.Controls;

/// <summary>
/// A table in a card: a header row with the column names, then the rows (virtualised, a row is chosen by click or keys, a double click
/// or Enter opens it: <see cref="ItemActivated"/>). Columns have a width ("2*", "120") and a cell made by code; a right-aligned column
/// suits numbers. The toolbar above and the actions and pagination below belong to the page (<see cref="ListToolbar"/>,
/// <see cref="Pager"/>). An empty table says so in <see cref="EmptyText"/>.
/// </summary>
public sealed class DataTable<T> : Border
    where T : class
{
    private readonly List<Column> _columns = [];
    private readonly Grid _header = new() { Margin = new Thickness(16, 10, 16, 10) };
    private readonly ListBox _list = new();
    private readonly TextBlock _empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 36), TextAlignment = TextAlignment.Center };

    public DataTable(string name)
    {
        this.Themed("MatCard");
        Padding = new Thickness(0);
        ClipToBounds = true;
        _list.Themed("MatList");
        _list.Padding = new Thickness(6, 4, 6, 6);
        _list.ItemContainerTheme = RowTheme();
        _list.ItemTemplate = new FuncDataTemplate<T>((item, _) => item is null ? new Panel() : Row(item), supportsRecycling: false);
        AutomationProperties.SetName(_list, name);
        _list.SelectionChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
        _list.DoubleTapped += (_, _) => Activate();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _list.SelectedItem is not null)
            {
                e.Handled = true;
                Activate();
            }
        };
        _empty.Themed("MatText.Muted");
        EmptyText = "Keine Einträge.";

        var headerLine = Ui.Docked(new Border { Child = _header, BorderThickness = new Thickness(0, 0, 0, 1) }.Use(Border.BorderBrushProperty, "Mat.Border"), Dock.Top);
        headerLine.Use(Border.BackgroundProperty, "Mat.Surface2");
        var body = new Panel { Children = { _list, _empty } };
        Child = new DockPanel { Children = { headerLine, body } };
    }

    /// <summary>The chosen row changed.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>A row was opened (double click, Enter).</summary>
    public event EventHandler<T>? ItemActivated;

    public ListBox List => _list;

    public T? SelectedItem
    {
        get => _list.SelectedItem as T;
        set => _list.SelectedItem = value;
    }

    public int ItemCount => _list.ItemCount;

    /// <summary>What an empty table says ("Noch keine Läufe.").</summary>
    public string EmptyText
    {
        get => _empty.Text ?? "";
        set => _empty.Text = value;
    }

    /// <summary>The rows; the chosen one stays chosen when it is still there (by <paramref name="keyOf"/>), else the first is chosen.</summary>
    public void SetItems(IReadOnlyList<T> items, Func<T, object>? keyOf = null, bool selectFirst = true)
    {
        var selectedKey = SelectedItem is { } selected && keyOf is not null ? keyOf(selected) : null;
        _list.ItemsSource = items;
        _list.SelectedItem = selectedKey is null ? (selectFirst ? items.FirstOrDefault() : null) : items.FirstOrDefault(item => Equals(keyOf!(item), selectedKey)) ?? (selectFirst ? items.FirstOrDefault() : null);
        _empty.IsVisible = items.Count == 0;
    }

    /// <summary>Adds a column: its name, its width ("2*", "140") and the cell of a row.</summary>
    public DataTable<T> AddColumn(string header, string width, Func<T, Control> cell, bool alignRight = false)
    {
        var column = new Column(header, GridLength.Parse(width), cell, alignRight);
        _columns.Add(column);
        _header.ColumnDefinitions.Add(new ColumnDefinition(column.Width));
        var title = new TextBlock { Text = header, FontSize = MatTokens.Type.FsSm, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 12, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        title.Use(TextBlock.ForegroundProperty, "Mat.TextMuted");
        title.HorizontalAlignment = alignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _header.Children.Add(Ui.Cell(title, 0, _columns.Count - 1));
        return this;
    }

    /// <summary>A plain text cell (trimmed with an ellipsis, the whole text as tool tip).</summary>
    public static TextBlock TextCell(string text, string? colour = null, bool isStrong = false)
    {
        var block = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        if (colour is not null)
        {
            block.Use(TextBlock.ForegroundProperty, colour);
        }

        if (isStrong)
        {
            block.FontWeight = FontWeight.SemiBold;
        }

        ToolTip.SetTip(block, text.Length > 40 ? text : null);
        return block;
    }

    private Control Row(T item)
    {
        var grid = new Grid { MinHeight = 38 };
        for (var index = 0; index < _columns.Count; index++)
        {
            var column = _columns[index];
            grid.ColumnDefinitions.Add(new ColumnDefinition(column.Width));
            var cell = column.Cell(item);
            cell.Margin = new Thickness(0, 4, 12, 4);
            cell.VerticalAlignment = VerticalAlignment.Center;
            if (column.AlignRight)
            {
                cell.HorizontalAlignment = HorizontalAlignment.Right;
            }

            grid.Children.Add(Ui.Cell(cell, 0, index));
        }

        return grid;
    }

    private void Activate()
    {
        if (SelectedItem is { } item)
        {
            ItemActivated?.Invoke(this, item);
        }
    }

    /// <summary>A row of the table: MatDesktop's choice row without the gap between rows, aligned with the header.</summary>
    private static ControlTheme RowTheme() => new(typeof(ListBoxItem))
    {
        BasedOn = Ui.Theme("MatChoiceRow"),
        Setters =
        {
            new Setter(MarginProperty, new Thickness(0, 0, 0, 1)),
            new Setter(PaddingProperty, new Thickness(10, 0)),
            new Setter(MinHeightProperty, 40.0),
        },
    };

    private sealed record Column(string Header, GridLength Width, Func<T, Control> Cell, bool AlignRight);
}
