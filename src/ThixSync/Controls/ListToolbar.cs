using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using MatDesktop;

namespace ThixSync.Controls;

/// <summary>
/// The toolbar above a list or table: the search field first, then the filters and the sort order (combo boxes), then anything else a
/// page adds (a switch "Nur Probleme"). Every change raises <see cref="Changed"/>; in a narrow window the items continue on the next
/// line. Several toolbars can sit on one page (a table of runs and the table of its lines).
/// </summary>
public sealed class ListToolbar : WrapPanel
{
    private readonly TextBox _search;

    public ListToolbar(string searchWatermark)
    {
        Orientation = Orientation.Horizontal;
        Margin = new Thickness(0, 0, 0, 14);
        _search = new TextBox { Width = 260, Watermark = searchWatermark };
        _search.Themed("MatSearchBox");
        AutomationProperties.SetName(_search, searchWatermark);
        _search.TextChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        AddItem(_search);
    }

    /// <summary>The search text, a filter or the sort order changed.</summary>
    public event EventHandler? Changed;

    public TextBox SearchBox => _search;

    /// <summary>The search text without blanks around it (empty: no search).</summary>
    public string SearchText => (_search.Text ?? "").Trim();

    /// <summary>Adds a choice (filter or sort) with its entries; the first one is chosen.</summary>
    public ComboBox AddChoice(string name, IReadOnlyList<string> entries, double minWidth = 180)
    {
        var choice = new ComboBox { MinWidth = minWidth, ItemsSource = entries, SelectedIndex = 0 };
        AutomationProperties.SetName(choice, name);
        ToolTip.SetTip(choice, name);
        choice.SelectionChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        AddItem(choice);
        return choice;
    }

    /// <summary>Adds a switch ("Nur Probleme").</summary>
    public CheckBox AddSwitch(string text)
    {
        var box = new CheckBox { Content = text, VerticalAlignment = VerticalAlignment.Center };
        box.IsCheckedChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        AddItem(box);
        return box;
    }

    /// <summary>Adds a control of the page's own (it keeps the spacing of the toolbar).</summary>
    public void AddItem(Control control)
    {
        control.Margin = new Thickness(0, 0, 10, 6);
        control.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(control);
    }
}
