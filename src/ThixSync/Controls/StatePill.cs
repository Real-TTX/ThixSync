using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MatDesktop;

namespace ThixSync.Controls;

/// <summary>The tone of a state: which tint of MatDesktop a pill, an icon or a tile takes (never the colour alone – always with a word).</summary>
public enum Tone
{
    Success,
    Warning,
    Danger,
    Info,
    Accent,
    Muted,
}

/// <summary>The resource keys of a tone: the tint behind, the text and icon colour, the strong colour.</summary>
public static class Tones
{
    public static string Background(Tone tone) => tone switch
    {
        Tone.Success => "Mat.SuccessBg",
        Tone.Warning => "Mat.WarningBg",
        Tone.Danger => "Mat.DangerBg",
        Tone.Info => "Mat.InfoBg",
        Tone.Accent => "Mat.AccentSoft",
        _ => "Mat.Surface2",
    };

    public static string Text(Tone tone) => tone switch
    {
        Tone.Success => "Mat.SuccessText",
        Tone.Warning => "Mat.WarningText",
        Tone.Danger => "Mat.DangerText",
        Tone.Info => "Mat.InfoText",
        Tone.Accent => "Mat.AccentText",
        _ => "Mat.TextMuted",
    };

    public static string Strong(Tone tone) => tone switch
    {
        Tone.Success => "Mat.Success",
        Tone.Warning => "Mat.Warning",
        Tone.Danger => "Mat.Danger",
        Tone.Info => "Mat.Info",
        Tone.Accent => "Mat.Accent",
        _ => "Mat.TextMuted",
    };
}

/// <summary>A state as a pill: an icon and a word on the tint of its tone ("✓ Aktuell", "Läuft …", "Fehler").</summary>
public sealed class StatePill : Border
{
    private readonly MatIcon _icon = new() { Size = 15 };
    private readonly TextBlock _text = new() { FontWeight = FontWeight.SemiBold, FontSize = MatTokens.Type.FsSm, VerticalAlignment = VerticalAlignment.Center };

    public StatePill()
    {
        CornerRadius = new CornerRadius(999);
        Padding = new Thickness(12, 5, 14, 5);
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Left;
        Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { _icon, _text } };
    }

    public StatePill(string text, string icon, Tone tone, bool isLarge = false)
        : this()
    {
        if (isLarge)
        {
            Padding = new Thickness(16, 7, 18, 7);
            _text.FontSize = MatTokens.Type.FsMd;
            _icon.Size = 17;
        }

        Show(text, icon, tone);
    }

    public string Text => _text.Text ?? "";

    public void Show(string text, string icon, Tone tone)
    {
        _text.Text = text;
        _icon.Icon = icon;
        this.Use(BackgroundProperty, Tones.Background(tone));
        _text.Use(TextBlock.ForegroundProperty, Tones.Text(tone));
        _icon.Use(MatIcon.ForegroundProperty, Tones.Text(tone));
    }
}

/// <summary>
/// A number of the overview as a tile: an icon in its tint, the big value and what it counts ("Aufträge", "kopiert in 7 Tagen").
/// </summary>
public sealed class StatTile : Border
{
    private readonly TextBlock _value = new() { FontSize = MatTokens.Type.Fs2xl, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _label = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap };

    public StatTile(string icon, string label, Tone tone = Tone.Accent, bool isCompact = false)
    {
        this.Themed("MatCard");
        Padding = isCompact ? new Thickness(16, 10) : new Thickness(18, 16);
        if (isCompact)
        {
            _value.FontSize = MatTokens.Type.FsXl;
        }

        _label.Text = label;
        _label.Themed("MatText.Muted");
        _detail.Themed("MatText.Faint");
        _detail.IsVisible = false;
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, isCompact ? 4 : 10) };
        head.Children.Add(Ui.Docked(Ui.IconTile(icon, isCompact ? 28 : 32, isCompact ? 15 : 17, Tones.Background(tone), Tones.Text(tone)), Dock.Right));
        head.Children.Add(_label);
        _label.VerticalAlignment = VerticalAlignment.Center;
        Child = new StackPanel { Children = { head, _value, _detail } };
    }

    public string Value => _value.Text ?? "";

    public void Show(string value, string? detail = null)
    {
        _value.Text = value;
        _detail.Text = detail;
        _detail.IsVisible = !string.IsNullOrEmpty(detail);
    }
}

/// <summary>An empty list says what to do: an icon in a soft circle, a heading, a sentence and an optional button.</summary>
public sealed class EmptyState : StackPanel
{
    private readonly TextBlock _title = new() { FontSize = MatTokens.Type.FsXl, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _text = new() { HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, MaxWidth = 460, TextWrapping = TextWrapping.Wrap };

    public EmptyState(string icon, string title, string text, Button? action = null)
    {
        Spacing = 10;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(24, 40);
        _title.Text = title;
        _text.Text = text;
        _text.Use(TextBlock.ForegroundProperty, "Mat.TextMuted");
        var circle = Ui.IconTile(icon, 72, 34);
        circle.CornerRadius = new CornerRadius(36);
        circle.HorizontalAlignment = HorizontalAlignment.Center;
        circle.Margin = new Thickness(0, 0, 0, 6);
        Children.Add(circle);
        Children.Add(_title);
        Children.Add(_text);
        if (action is not null)
        {
            action.HorizontalAlignment = HorizontalAlignment.Center;
            action.Margin = new Thickness(0, 10, 0, 0);
            Children.Add(action);
        }
    }

    public string Title => _title.Text ?? "";
}
