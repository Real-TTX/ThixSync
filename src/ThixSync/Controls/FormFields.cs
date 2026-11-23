using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using MatDesktop;

namespace ThixSync.Controls;

/// <summary>
/// The fields of a form with tabs: each <see cref="MatFormField"/> (label, hint, error) is registered with the name the business layer
/// uses in <c>BusinessException.Field</c> and the tab it sits on. <see cref="ShowError"/> opens that tab and shows the message right at
/// the field – also when the tab was not open (its fields are not in the window then, so <see cref="FormErrors"/> could not find them).
/// </summary>
public sealed class FormFields
{
    private readonly List<(string Field, MatFormField FormField, TabItem? Tab)> _fields = [];

    /// <summary>A labelled field (with an optional hint) for <paramref name="field"/>, on <paramref name="tab"/>.</summary>
    public MatFormField Add(string field, string label, Control content, string? hint = null, TabItem? tab = null)
    {
        var formField = new MatFormField { Label = label, Hint = hint, Content = content };
        Register(field, formField, tab);
        return formField;
    }

    /// <summary>Registers a field made elsewhere.</summary>
    public void Register(string field, MatFormField formField, TabItem? tab = null)
    {
        _fields.Add((field, formField, tab));
        FormErrors.SetField(formField, field);
    }

    /// <summary>Removes the fields of one tab (a form that draws its fields again for another type).</summary>
    public void RemoveWhere(Func<MatFormField, bool> predicate) => _fields.RemoveAll(entry => predicate(entry.FormField));

    /// <summary>The field registered for a name (tests).</summary>
    public MatFormField? Find(string field) => _fields.FirstOrDefault(entry => string.Equals(entry.Field, field, StringComparison.OrdinalIgnoreCase)).FormField;

    /// <summary>Clears every error.</summary>
    public void Clear()
    {
        foreach (var (_, formField, _) in _fields)
        {
            formField.Error = null;
        }
    }

    /// <summary>Clears every error and shows the message at its field (opening its tab). False when no visible field has that name.</summary>
    public bool ShowError(string? field, string message)
    {
        Clear();
        if (field is null)
        {
            return false;
        }

        var match = _fields.FirstOrDefault(entry => string.Equals(entry.Field, field, StringComparison.OrdinalIgnoreCase) && entry.FormField.IsVisible);
        if (match.FormField is null)
        {
            return false;
        }

        if (match.Tab is { } tab && tab.Parent is TabControl tabs)
        {
            tabs.SelectedItem = tab;
        }

        match.FormField.Error = message;
        return true;
    }

    /// <summary>A heading inside a form tab ("Löschungen", "Versionen").</summary>
    public static TextBlock Section(string title, bool isFirst = false)
    {
        var text = new TextBlock { Text = title, FontWeight = Avalonia.Media.FontWeight.SemiBold, FontSize = MatTokens.Type.FsLg, Margin = new Thickness(0, isFirst ? 0 : 18, 0, 10) };
        return text;
    }

    /// <summary>A setting row with a switch (title, sentence, switch at the right) for a form tab.</summary>
    public static MatSettingRow SwitchRow(string title, string? description, ToggleSwitch toggle, bool isLast = false)
    {
        toggle.OnContent = "";
        toggle.OffContent = "";
        Avalonia.Automation.AutomationProperties.SetName(toggle, title);
        var row = new MatSettingRow { Title = title, Description = description, Content = toggle };
        if (isLast)
        {
            row.BorderThickness = new Thickness(0);
        }

        return row;
    }

    /// <summary>A setting row with any control at the right.</summary>
    public static MatSettingRow Row(string title, string? description, Control control, bool isLast = false)
    {
        var row = new MatSettingRow { Title = title, Description = description, Content = control };
        if (isLast)
        {
            row.BorderThickness = new Thickness(0);
        }

        return row;
    }

    /// <summary>Two fields side by side (each half the width).</summary>
    public static Grid Pair(Control left, Control right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,*") };
        grid.Children.Add(Ui.Cell(left, 0, 0));
        grid.Children.Add(Ui.Cell(right, 0, 2));
        left.VerticalAlignment = right.VerticalAlignment = VerticalAlignment.Top;
        return grid;
    }
}
