using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using MatDesktop;
using ThixSync.Core.Infrastructure;

namespace ThixSync;

/// <summary>
/// Small helpers for code-built UI: the app icon and logo, the UI thread, opening addresses, the life of a page's subscriptions and
/// a few text and layout shortcuts. Colours, sizes and named looks are MatDesktop's (<c>control.Use(property, "Mat.TextMuted")</c>,
/// <c>control.Themed("MatButton.Primary")</c>), icons are <c>MatIcon</c> with the name of the sprite; numbers and times are
/// <see cref="Controls.Formats"/>.
/// </summary>
internal static class Ui
{
    private static WindowIcon? _appIcon;
    private static Bitmap? _logo;

    /// <summary>The icon of every window of ThixSync (the task bar, Alt+Tab): MatThemeOptions.Icon gives it to every MatWindow.</summary>
    public static WindowIcon AppIcon => _appIcon ??= new WindowIcon(AssetLoader.Open(new Uri("avares://ThixSync/Assets/thixsync.ico")));

    /// <summary>The logo (the mark of the app), for the title bar, the header of the jobs and the setup.</summary>
    public static Bitmap Logo => _logo ??= new Bitmap(AssetLoader.Open(new Uri("avares://ThixSync/Assets/logo-256.png")));

    /// <summary>
    /// A named look of MatDesktop (a control theme such as "MatButton.Primary" or "MatChoiceRow") for
    /// an object initializer; anywhere else <c>control.Themed(key)</c>. A control theme is the same in light and dark.
    /// </summary>
    public static ControlTheme Theme(string key) =>
        Application.Current!.TryGetResource(key, null, out var value) && value is ControlTheme theme
            ? theme
            : throw new InvalidOperationException($"The control theme {key} is missing (MatDesktop/Themes/Controls.axaml).");

    /// <summary>Runs the action on the UI thread (events of the business layer come from any thread).</summary>
    public static void Post(Action action) => Dispatcher.UIThread.Post(action);

    /// <summary>The window that hosts a control (null while it is not shown).</summary>
    public static Window? WindowOf(Control control) => TopLevel.GetTopLevel(control) as Window;

    /// <summary>The main window that hosts a control (null while it is not shown).</summary>
    public static MainWindow? MainOf(Control control) => TopLevel.GetTopLevel(control) as MainWindow;

    /// <summary>Ctrl on Windows, Cmd on macOS: the modifier of the app's own shortcuts.</summary>
    public static KeyModifiersName CommandKey => OperatingSystem.IsMacOS() ? new("⌘", Avalonia.Input.KeyModifiers.Meta) : new("Strg", Avalonia.Input.KeyModifiers.Control);

    /// <summary>Tests see what would be opened instead of starting the browser.</summary>
    internal static Action<string>? OpenExternalForTests { get; set; }

    /// <summary>
    /// Subscribes to the events of the business layer only while <paramref name="control"/> is in a window: <paramref name="subscribe"/>
    /// when it comes (and then <paramref name="refresh"/>), <paramref name="unsubscribe"/> when it goes. A page that is not shown neither
    /// works nor keeps a closed window alive.
    /// </summary>
    public static void WhileShown(Control control, Action subscribe, Action unsubscribe, Action refresh)
    {
        control.AttachedToVisualTree += (_, _) =>
        {
            subscribe();
            refresh();
        };
        control.DetachedFromVisualTree += (_, _) => unsubscribe();
    }

    /// <summary>Opens a web address or a folder with the program the system uses for it (browser, Explorer, Finder).</summary>
    public static void OpenExternal(string pathOrUrl)
    {
        if (OpenExternalForTests is { } open)
        {
            open(pathOrUrl);
            return;
        }

        try
        {
            var startInfo = OperatingSystem.IsMacOS()
                ? new System.Diagnostics.ProcessStartInfo("open") { ArgumentList = { pathOrUrl }, UseShellExecute = false }
                : OperatingSystem.IsLinux()
                    ? new System.Diagnostics.ProcessStartInfo("xdg-open") { ArgumentList = { pathOrUrl }, UseShellExecute = false }
                    : new System.Diagnostics.ProcessStartInfo(pathOrUrl) { UseShellExecute = true };
            System.Diagnostics.Process.Start(startInfo)?.Dispose();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warning($"Could not open {pathOrUrl}: {exception.Message}");
        }
    }

    /// <summary>A text in a named look ("MatText.Muted", "MatText.SectionTitle" …).</summary>
    public static TextBlock Text(string text, string? theme = null, double? size = null, FontWeight? weight = null)
    {
        var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        if (theme is not null)
        {
            block.Themed(theme);
        }

        if (size is { } fontSize)
        {
            block.FontSize = fontSize;
        }

        if (weight is { } fontWeight)
        {
            block.FontWeight = fontWeight;
        }

        return block;
    }

    /// <summary>A text in a colour of the theme ("Mat.TextMuted", "Mat.SuccessText" …).</summary>
    public static TextBlock Colored(string text, string colour, double? size = null, FontWeight? weight = null)
    {
        var block = Text(text, null, size, weight);
        block.Use(TextBlock.ForegroundProperty, colour);
        return block;
    }

    /// <summary>A button with an icon and a text (and a named look).</summary>
    public static Button Button(string icon, string text, string? theme = null)
    {
        var button = new Button { Content = new Controls.IconLabel { Icon = icon, Text = text } };
        if (theme is not null)
        {
            button.Themed(theme);
        }

        return button;
    }

    /// <summary>The control placed in a cell of a grid (row, column, span).</summary>
    public static T Cell<T>(T control, int row, int column, int columnSpan = 1)
        where T : Control
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        if (columnSpan > 1)
        {
            Grid.SetColumnSpan(control, columnSpan);
        }

        return control;
    }

    /// <summary>The control docked at a side of a DockPanel.</summary>
    public static T Docked<T>(T control, Dock dock)
        where T : Control
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    /// <summary>A rounded square with an icon in the accent tint (lists, cards, the type picker).</summary>
    public static Border IconTile(string icon, double size = 40, double iconSize = 20, string background = "Mat.AccentSoft", string foreground = "Mat.AccentText")
    {
        var tile = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 4),
            Child = new MatIcon { Icon = icon, Size = iconSize, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                .Use(MatIcon.ForegroundProperty, foreground),
        };
        tile.Use(Border.BackgroundProperty, background);
        return tile;
    }

    /// <summary>A hairline of the theme (between rows and areas of a card).</summary>
    public static Border Line(Thickness margin) => new Border { Height = 1, Margin = margin }.Use(Border.BackgroundProperty, "Mat.Border");

    /// <summary>A card of MatDesktop (surface, hairline, large radius) around the content.</summary>
    public static Border Card(Control child, Thickness? padding = null) => new Border { Child = child, Padding = padding ?? new Thickness(20) }.Themed("MatCard");

    /// <summary>Copies a text to the clipboard of the window of <paramref name="control"/>.</summary>
    public static async Task CopyTextAsync(Control control, string text)
    {
        if (TopLevel.GetTopLevel(control)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}

/// <summary>The name and the modifier of the command key of the platform.</summary>
internal sealed record KeyModifiersName(string Name, Avalonia.Input.KeyModifiers Modifier);
