using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using MatDesktop;

namespace ThixSync.Controls;

/// <summary>
/// The pagination below a table: "1–50 von 1.204" and the buttons previous, "Seite 2 von 25", next. A change raises
/// <see cref="PageChanged"/>; the page loads that page (the business layer pages, <c>Page&lt;T&gt;</c>) and calls <see cref="Show"/>.
/// It hides itself while everything fits on one page.
/// </summary>
public sealed class Pager : StackPanel
{
    private readonly TextBlock _range = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBlock _position = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), MinWidth = 96, TextAlignment = Avalonia.Media.TextAlignment.Center };
    private readonly Button _previous = IconButton("chevron-left", "Vorherige Seite");
    private readonly Button _next = IconButton("chevron-right", "Nächste Seite");
    private readonly bool _hideWhenSingle;

    public Pager(bool hideWhenSingle = false)
    {
        _hideWhenSingle = hideWhenSingle;
        Orientation = Orientation.Horizontal;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Center;
        _range.Themed("MatText.Muted");
        _range.TextWrapping = Avalonia.Media.TextWrapping.NoWrap;
        _previous.Click += (_, _) => GoTo(PageIndex - 1);
        _next.Click += (_, _) => GoTo(PageIndex + 1);
        Children.Add(_range);
        Children.Add(_previous);
        Children.Add(_position);
        Children.Add(_next);
        Show(0, 1, 0, 50);
    }

    /// <summary>The user chose another page (its index).</summary>
    public event EventHandler<int>? PageChanged;

    public int PageIndex { get; private set; }

    public int PageCount { get; private set; } = 1;

    public int TotalCount { get; private set; }

    public Button NextButton => _next;

    public Button PreviousButton => _previous;

    /// <summary>"Seite 2 von 25" (tests).</summary>
    public string PositionText => _position.Text ?? "";

    /// <summary>Shows where the table is: the page, how many pages, how many items and the size of a page.</summary>
    public void Show(int pageIndex, int pageCount, int totalCount, int pageSize)
    {
        PageIndex = pageIndex;
        PageCount = Math.Max(1, pageCount);
        TotalCount = totalCount;
        var from = totalCount == 0 ? 0 : pageIndex * pageSize + 1;
        var to = Math.Min(totalCount, (pageIndex + 1) * pageSize);
        _range.Text = totalCount == 0 ? "Keine Einträge" : $"{Formats.Count(from)}–{Formats.Count(to)} von {Formats.Count(totalCount)}";
        _position.Text = $"Seite {pageIndex + 1} von {PageCount}";
        _previous.IsEnabled = pageIndex > 0;
        _next.IsEnabled = pageIndex + 1 < PageCount;
        var paged = PageCount > 1;
        _previous.IsVisible = _position.IsVisible = _next.IsVisible = paged;
        IsVisible = !_hideWhenSingle || paged;
    }

    private void GoTo(int pageIndex)
    {
        var index = Math.Clamp(pageIndex, 0, PageCount - 1);
        if (index != PageIndex)
        {
            PageChanged?.Invoke(this, index);
        }
    }

    private static Button IconButton(string icon, string name)
    {
        var button = new Button { Content = new MatIcon { Icon = icon, Size = 16 } }.Themed("MatButton.Icon");
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, name);
        return button;
    }
}
