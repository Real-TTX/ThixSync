using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using MatDesktop;
using ThixSync.Core.Business;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;

namespace ThixSync.Controls;

/// <summary>One choice of <see cref="ChoiceCards"/>: its key, icon, title and the sentence that explains it.</summary>
public sealed record CardChoice(string Key, string Icon, string Title, string Description);

/// <summary>
/// A choice among a few options as cards side by side (the kind of a job: Bidirektional, Spiegeln, Backup): icon, title and a short
/// explanation; the chosen card has the accent tint and line. Keys and arrows move the choice like in a list.
/// </summary>
public sealed class ChoiceCards : ListBox
{
    public ChoiceCards(string name, IReadOnlyList<CardChoice> choices)
    {
        this.Themed("MatList");
        ItemsPanel = new FuncTemplate<Panel?>(() => new UniformGrid { Rows = 1 });
        ItemContainerTheme = new ControlTheme(typeof(ListBoxItem))
        {
            BasedOn = Ui.Theme("MatCardRow"),
            Setters = { new Setter(MarginProperty, new Thickness(0, 0, 10, 0)), new Setter(PaddingProperty, new Thickness(16, 14)) },
        };
        ItemTemplate = new FuncDataTemplate<CardChoice>((choice, _) => choice is null ? new Panel() : Card(choice), supportsRecycling: false);
        ItemsSource = choices;
        AutomationProperties.SetName(this, name);
    }

    protected override Type StyleKeyOverride => typeof(ListBox);

    /// <summary>The key of the chosen card.</summary>
    public string? SelectedKey
    {
        get => (SelectedItem as CardChoice)?.Key;
        set => SelectedItem = (ItemsSource as IEnumerable<CardChoice>)?.FirstOrDefault(choice => choice.Key == value);
    }

    private static Control Card(CardChoice choice)
    {
        var text = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = choice.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = choice.Description, TextWrapping = TextWrapping.Wrap }.Themed("MatText.Muted"),
            },
        };
        var tile = Ui.IconTile(choice.Icon, 36, 18);
        tile.VerticalAlignment = VerticalAlignment.Top;
        tile.Margin = new Thickness(0, 0, 12, 0);
        return new DockPanel { Children = { Ui.Docked(tile, Dock.Left), text } };
    }
}

/// <summary>The days of a weekly schedule as seven toggles Mo … So.</summary>
public sealed class WeekdayPicker : StackPanel
{
    private static readonly DayOfWeek[] Order = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];
    private readonly Dictionary<DayOfWeek, ToggleButton> _toggles = [];

    public WeekdayPicker()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 6;
        foreach (var day in Order)
        {
            var name = Formats.German.DateTimeFormat.GetAbbreviatedDayName(day).TrimEnd('.');
            var toggle = new ToggleButton { Content = name, MinWidth = 44, HorizontalContentAlignment = HorizontalAlignment.Center };
            AutomationProperties.SetName(toggle, Formats.German.DateTimeFormat.GetDayName(day));
            _toggles[day] = toggle;
            Children.Add(toggle);
        }
    }

    public IReadOnlyList<DayOfWeek> Days
    {
        get => [.. Order.Where(day => _toggles[day].IsChecked == true)];
        set
        {
            foreach (var day in Order)
            {
                _toggles[day].IsChecked = value.Contains(day);
            }
        }
    }

    public ToggleButton Toggle(DayOfWeek day) => _toggles[day];
}

/// <summary>
/// The choice of a connection (source or target of a job): the connections grouped like the type picker (Lokal &amp; Netzwerk, Server
/// &amp; Protokolle, Cloud-Speicher, ThixApps) with a heading per group, each with the icon of its type, its name and its address.
/// </summary>
public sealed class ConnectionPicker : ComboBox
{
    private List<Entry> _entries = [];

    public ConnectionPicker(string name)
    {
        MinWidth = 280;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(this, name);
        PlaceholderText = "Verbindung wählen …";
        ItemTemplate = new FuncDataTemplate<Entry>((entry, _) => entry switch
        {
            { Connection: { } connection } => Row(connection),
            { Heading: { } heading } => Heading(heading),
            _ => new Panel(),
        }, supportsRecycling: false);

        // the headings of the groups are no choice
        ContainerPrepared += (_, e) => e.Container.IsEnabled = e.Container.DataContext is not Entry { Heading: not null };
    }

    protected override Type StyleKeyOverride => typeof(ComboBox);

    /// <summary>The id of the chosen connection (empty when none).</summary>
    public Guid SelectedId
    {
        get => SelectedConnection?.Id ?? Guid.Empty;
        set => SelectedItem = _entries.FirstOrDefault(entry => entry.Connection?.Id == value);
    }

    public Connection? SelectedConnection => (SelectedItem as Entry)?.Connection;

    /// <summary>Fills the list again (after a connection was made or changed), keeping the choice.</summary>
    public void Load(IReadOnlyList<Connection> connections)
    {
        var selected = SelectedId;
        var entries = new List<Entry>();
        foreach (var group in connections.GroupBy(connection => ConnectionCatalog.Get(connection.Kind).Group).OrderBy(group => group.Key))
        {
            entries.Add(new Entry(null, ConnectionCatalog.GroupName(group.Key)));
            entries.AddRange(group.OrderBy(connection => connection.Name, StringComparer.CurrentCultureIgnoreCase).Select(connection => new Entry(connection, null)));
        }

        _entries = entries;
        ItemsSource = entries;
        SelectedId = selected;
    }

    /// <summary>The icon, name and address of a connection in one line (also the closed box).</summary>
    public static Control Row(Connection connection)
    {
        var type = ConnectionCatalog.Get(connection.Kind);
        var address = ConnectionService.Address(connection);
        var icon = new MatIcon { Icon = type.Icon, Size = 16 }.Use(MatIcon.ForegroundProperty, "Mat.AccentText");
        var name = new TextBlock { Text = connection.Name, VerticalAlignment = VerticalAlignment.Center };
        var detail = new TextBlock { Text = address, VerticalAlignment = VerticalAlignment.Center, FontSize = MatTokens.Type.FsSm, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 };
        detail.Use(TextBlock.ForegroundProperty, "Mat.TextMuted");
        detail.IsVisible = address.Length > 0 && address != connection.Name;
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { icon, name, detail } };
    }

    private static TextBlock Heading(string text)
    {
        var heading = new TextBlock { Text = text, FontSize = MatTokens.Type.FsXs, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) };
        heading.Use(TextBlock.ForegroundProperty, "Mat.TextMuted");
        return heading;
    }

    /// <summary>A line of the list: a connection or the heading of a group.</summary>
    private sealed record Entry(Connection? Connection, string? Heading);
}

/// <summary>
/// The folder pairs of a job: a row per pair – the folder of the source, the arrow of the mode, the folder of the target, each with
/// "Durchsuchen …", and a button that removes the pair; below them "Ordnerpaar hinzufügen". <see cref="Browse"/> picks a folder (the
/// page knows how: the system's folder picker for this computer, the folder browser of the connection otherwise).
/// </summary>
public sealed class FolderPairsEditor : StackPanel
{
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private readonly List<(TextBox Source, TextBox Target, SyncArrow Arrow)> _pairs = [];
    private readonly Button _add;
    private bool _isTwoWay = true;
    private string _sourceHint = "";
    private string _targetHint = "";

    public FolderPairsEditor()
    {
        Spacing = 10;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,56,*,40") };
        header.Children.Add(Ui.Cell(Ui.Text("Ordner der Quelle", "MatText.FieldLabel"), 0, 0));
        header.Children.Add(Ui.Cell(Ui.Text("Ordner des Ziels", "MatText.FieldLabel"), 0, 2));
        _add = Ui.Button("plus", "Ordnerpaar hinzufügen");
        _add.HorizontalAlignment = HorizontalAlignment.Left;
        _add.Click += (_, _) =>
        {
            AddRow(new FolderPair());
            _pairs[^1].Source.Focus();
        };
        Children.Add(header);
        Children.Add(_rows);
        Children.Add(_add);
    }

    /// <summary>Picks a folder: (is it the source side, the folder written now) → the chosen folder or null.</summary>
    public Func<bool, string, Task<string?>>? Browse { get; set; }

    public int Count => _pairs.Count;

    public Button AddButton => _add;

    /// <summary>The pairs as written (the business layer checks them).</summary>
    public IReadOnlyList<FolderPair> Pairs
    {
        get => [.. _pairs.Select(pair => new FolderPair { SourcePath = pair.Source.Text ?? "", TargetPath = pair.Target.Text ?? "" })];
        set
        {
            _pairs.Clear();
            _rows.Children.Clear();
            foreach (var pair in value.Count == 0 ? [new FolderPair()] : value)
            {
                AddRow(pair);
            }
        }
    }

    public TextBox SourceBox(int index) => _pairs[index].Source;

    public TextBox TargetBox(int index) => _pairs[index].Target;

    /// <summary>⇄ for two-way, → otherwise.</summary>
    public void SetMode(bool isTwoWay)
    {
        _isTwoWay = isTwoWay;
        foreach (var pair in _pairs)
        {
            pair.Arrow.IsTwoWay = isTwoWay;
        }
    }

    /// <summary>The way folders are written on each side ("C:\Users\…\Dokumente", "/backup/Fotos").</summary>
    public void SetHints(string sourceHint, string targetHint)
    {
        _sourceHint = sourceHint;
        _targetHint = targetHint;
        foreach (var pair in _pairs)
        {
            pair.Source.Watermark = sourceHint;
            pair.Target.Watermark = targetHint;
        }
    }

    private void AddRow(FolderPair pair)
    {
        var source = new TextBox { Text = pair.SourcePath, Watermark = _sourceHint };
        var target = new TextBox { Text = pair.TargetPath, Watermark = _targetHint };
        var number = _pairs.Count + 1;
        AutomationProperties.SetName(source, $"Ordner der Quelle {number}");
        AutomationProperties.SetName(target, $"Ordner des Ziels {number}");
        var arrow = new SyncArrow { IsTwoWay = _isTwoWay, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var remove = new Button { Content = new MatIcon { Icon = "x", Size = 16 } }.Themed("MatButton.Icon");
        AutomationProperties.SetName(remove, $"Ordnerpaar {number} entfernen");
        ToolTip.SetTip(remove, "Ordnerpaar entfernen");

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,56,*,40") };
        row.Children.Add(Ui.Cell(WithBrowse(source, isSource: true), 0, 0));
        row.Children.Add(Ui.Cell(arrow, 0, 1));
        row.Children.Add(Ui.Cell(WithBrowse(target, isSource: false), 0, 2));
        row.Children.Add(Ui.Cell(remove, 0, 3));
        remove.HorizontalAlignment = HorizontalAlignment.Right;
        var entry = (source, target, arrow);
        remove.Click += (_, _) =>
        {
            if (_pairs.Count <= 1)
            {
                source.Text = target.Text = "";
                return;
            }

            _pairs.Remove(entry);
            _rows.Children.Remove(row);
        };
        _pairs.Add(entry);
        _rows.Children.Add(row);
    }

    private Control WithBrowse(TextBox box, bool isSource)
    {
        var browse = new Button { Content = new IconLabel { Icon = "folder-open", Text = "Durchsuchen …" }, Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += async (_, _) =>
        {
            if (Browse is { } pick && await pick(isSource, box.Text ?? "") is { } folder)
            {
                box.Text = folder;
            }
        };
        return new DockPanel { Children = { Ui.Docked(browse, Dock.Right), box } };
    }
}
