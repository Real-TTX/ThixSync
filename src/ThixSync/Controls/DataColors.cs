using Avalonia.Media;
using Avalonia.Media.Immutable;
using ThixSync.Core.Business;
using ThixSync.Core.Models;

namespace ThixSync.Controls;

/// <summary>
/// The colours of the DATA on the map (the kinds of files, growth and shrinkage), read from <see cref="FileCategories"/> in Core:
/// the validated palette of the data-viz method, light and dark stepped apart. The chrome of the window never uses them; it takes
/// MatDesktop's theme. Brushes are made once per mode.
/// </summary>
internal static class DataColors
{
    private static readonly Dictionary<(FileCategory, bool), ImmutableSolidColorBrush> CategoryBrushes = [];
    private static readonly Dictionary<string, Color> Parsed = [];

    public static Color Category(FileCategory category, bool dark)
    {
        var info = FileCategories.Info(category);
        return FromHex(dark ? info.DarkColor : info.LightColor);
    }

    public static IBrush CategoryBrush(FileCategory category, bool dark)
    {
        if (!CategoryBrushes.TryGetValue((category, dark), out var brush))
        {
            brush = new ImmutableSolidColorBrush(Category(category, dark));
            CategoryBrushes[(category, dark)] = brush;
        }

        return brush;
    }

    /// <summary>The colour of a change: grey for none, toward red for growth, toward blue for shrinkage (<paramref name="strength"/> 0..1).</summary>
    public static Color Change(ChangeKind kind, double strength, bool dark)
    {
        var (shrunk, neutral, grown) = dark ? FileCategories.ChangeDark : FileCategories.ChangeLight;
        var start = FromHex(neutral);
        if (kind is not (ChangeKind.Grown or ChangeKind.New or ChangeKind.Shrunk) || strength <= 0)
        {
            return start;
        }

        var end = FromHex(kind == ChangeKind.Shrunk ? shrunk : grown);
        var t = Math.Clamp(0.25 + 0.75 * Math.Pow(strength, 0.7), 0, 1);
        return Mix(start, end, t);
    }

    /// <summary>The ink of a label inside a filled tile: dark on light fills, white on dark ones.</summary>
    public static Color LabelOn(Color fill) => Luminance(fill) > 0.45 ? FromHex(FileCategories.LabelOnLightFill) : FromHex(FileCategories.LabelOnDarkFill);

    /// <summary>A lighter (positive) or darker (negative) step of a colour, for the soft light of a large tile.</summary>
    public static Color Shade(Color color, double amount)
    {
        var target = amount >= 0 ? (byte)255 : (byte)0;
        var t = Math.Abs(amount);
        return new Color(color.A, Lerp(color.R, target, t), Lerp(color.G, target, t), Lerp(color.B, target, t));
    }

    public static Color Mix(Color a, Color b, double t) => new(255, Lerp(a.R, b.R, t), Lerp(a.G, b.G, t), Lerp(a.B, b.B, t));

    /// <summary>"#rrggbb" of the data palette.</summary>
    public static Color FromHex(string hex)
    {
        if (Parsed.TryGetValue(hex, out var color))
        {
            return color;
        }

        var value = Convert.ToUInt32(hex.TrimStart('#'), 16);
        color = new Color(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        Parsed[hex] = color;
        return color;
    }

    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static byte Lerp(byte from, byte to, double t) => (byte)Math.Round(from + (to - from) * t);
}
