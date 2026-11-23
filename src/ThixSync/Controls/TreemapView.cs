using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using MatDesktop;
using ThixSync.Core.Business;
using ThixSync.Core.Models;

namespace ThixSync.Controls;

/// <summary>
/// The map: a squarified treemap of <see cref="Root"/> (<see cref="TreemapLayout"/>). Every file is a tile as large as its share,
/// folders frame what is inside them, the colour says the kind of file (or, with <see cref="Coloring"/> = ByChange, what grew and
/// what shrank since <see cref="Comparison"/>). The tiles are drawn once into a bitmap (a drive has hundreds of thousands of them);
/// hovering and choosing only draw the outline on top. A click chooses a tile (<see cref="NodeSelected"/>), a double click asks to
/// zoom into its folder (<see cref="NodeActivated"/>), the right button asks for the menu (<see cref="ContextRequested"/>).
/// Several maps on one page work: nothing here is static but the colours.
/// </summary>
public sealed class TreemapView : Control
{
    public static readonly StyledProperty<ScanNode?> RootProperty = AvaloniaProperty.Register<TreemapView, ScanNode?>(nameof(Root));

    public static readonly StyledProperty<ScanNode?> SelectedProperty = AvaloniaProperty.Register<TreemapView, ScanNode?>(nameof(Selected));

    public static readonly StyledProperty<MapColoring> ColoringProperty = AvaloniaProperty.Register<TreemapView, MapColoring>(nameof(Coloring));

    public static readonly StyledProperty<TreeComparison?> ComparisonProperty = AvaloniaProperty.Register<TreemapView, TreeComparison?>(nameof(Comparison));

    public static readonly StyledProperty<FileCategory?> HighlightProperty = AvaloniaProperty.Register<TreemapView, FileCategory?>(nameof(Highlight));

    private List<TreemapTile> _tiles = [];
    private RenderTargetBitmap? _bitmap;
    private Size _laidOutSize;
    private double _laidOutScale;
    private bool _isDirty = true;
    private TreemapTile? _hover;
    private Point _pointer;

    static TreemapView()
    {
        AffectsRender<TreemapView>(SelectedProperty);
        RootProperty.Changed.AddClassHandler<TreemapView>((view, _) => view.Invalidate(relayout: true));
        ColoringProperty.Changed.AddClassHandler<TreemapView>((view, _) => view.Invalidate(relayout: false));
        ComparisonProperty.Changed.AddClassHandler<TreemapView>((view, _) => view.Invalidate(relayout: false));
        HighlightProperty.Changed.AddClassHandler<TreemapView>((view, _) => view.Invalidate(relayout: false));
        FocusableProperty.OverrideDefaultValue<TreemapView>(true);
    }

    public TreemapView()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        AutomationProperties.SetName(this, "Speicherkarte");
        ActualThemeVariantChanged += (_, _) => Invalidate(relayout: false);
    }

    /// <summary>A tile was chosen with a click (or the right button).</summary>
    public event EventHandler<ScanNode>? NodeSelected;

    /// <summary>A double click: zoom into the folder of the tile.</summary>
    public event EventHandler<ScanNode>? NodeActivated;

    /// <summary>The right button on a tile: the page opens its menu there.</summary>
    public event EventHandler<ScanNode>? MenuRequested;

    /// <summary>The tile under the mouse changed (null: none).</summary>
    public event EventHandler<ScanNode?>? HoverChanged;

    /// <summary>The folder the map shows (the root of an analysis or a folder zoomed into).</summary>
    public ScanNode? Root
    {
        get => GetValue(RootProperty);
        set => SetValue(RootProperty, value);
    }

    public ScanNode? Selected
    {
        get => GetValue(SelectedProperty);
        set => SetValue(SelectedProperty, value);
    }

    public MapColoring Coloring
    {
        get => GetValue(ColoringProperty);
        set => SetValue(ColoringProperty, value);
    }

    public TreeComparison? Comparison
    {
        get => GetValue(ComparisonProperty);
        set => SetValue(ComparisonProperty, value);
    }

    /// <summary>Only this kind stands out, the others fade (the legend).</summary>
    public FileCategory? Highlight
    {
        get => GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, value);
    }

    /// <summary>The tiles of the last layout (tests).</summary>
    public IReadOnlyList<TreemapTile> Tiles => _tiles;

    /// <summary>Draws again (after a file was deleted out of the tree).</summary>
    public void Invalidate(bool relayout)
    {
        _isDirty = true;
        if (relayout)
        {
            _laidOutSize = default;
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        context.FillRectangle(Brush("Mat.Surface2"), new Rect(size));
        if (Root is not { Size: > 0 } root || size.Width < 4 || size.Height < 4)
        {
            return;
        }

        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        if (_isDirty || _bitmap is null || size != _laidOutSize || Math.Abs(scale - _laidOutScale) > 0.01)
        {
            if (size != _laidOutSize || _isDirty)
            {
                _tiles = TreemapLayout.Layout(root, size.Width, size.Height);
            }

            DrawTiles(size, scale);
            _laidOutSize = size;
            _laidOutScale = scale;
            _isDirty = false;
        }

        context.DrawImage(_bitmap!, new Rect(size));

        if (_hover is { } hover && !ReferenceEquals(hover.Node, Selected))
        {
            context.DrawRectangle(null, new Pen(Brush("Mat.Text"), 1.5), Inner(hover, 0.75));
        }

        if (Selected is { } selected && FindTile(selected) is { } tile)
        {
            context.DrawRectangle(null, new Pen(Brush("Mat.Surface"), 4), Inner(tile, 1));
            context.DrawRectangle(null, new Pen(Brush("Mat.Accent"), 2.5), Inner(tile, 1));
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _pointer = e.GetPosition(this);
        var tile = TreemapLayout.HitTest(_tiles, _pointer.X, _pointer.Y);
        if (!Equals(tile?.Node, _hover?.Node))
        {
            _hover = tile;
            HoverChanged?.Invoke(this, tile?.Node);
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        HoverChanged?.Invoke(this, null);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        if (TreemapLayout.HitTest(_tiles, point.Position.X, point.Position.Y) is not { } tile)
        {
            return;
        }

        e.Handled = true;
        Selected = tile.Node;
        NodeSelected?.Invoke(this, tile.Node);
        if (point.Properties.IsRightButtonPressed)
        {
            MenuRequested?.Invoke(this, tile.Node);
        }
        else if (e.ClickCount == 2)
        {
            NodeActivated?.Invoke(this, tile.Node);
        }
    }

    /// <summary>The tile of a node, or of the nearest folder above it that has one.</summary>
    private TreemapTile? FindTile(ScanNode node)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            foreach (var tile in _tiles)
            {
                if (ReferenceEquals(tile.Node, current))
                {
                    return tile;
                }
            }
        }

        return null;
    }

    private void DrawTiles(Size size, double scale)
    {
        var pixels = new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width * scale)), Math.Max(1, (int)Math.Ceiling(size.Height * scale)));
        if (_bitmap is null || _bitmap.PixelSize != pixels)
        {
            _bitmap?.Dispose();
            _bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
        }

        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var gap = Brush("Mat.Surface");
        var frame = Brush("Mat.Surface3");
        var labelBack = Brush("Mat.Surface");
        var labelInk = Brush("Mat.Text");
        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
        var smallTypeface = new Typeface(FontFamily.Default);
        var comparison = Comparison;
        var byChange = Coloring == MapColoring.ByChange && comparison is not null;
        var highlight = Highlight;
        var labelled = 0;

        using var context = _bitmap.CreateDrawingContext();
        context.FillRectangle(gap, new Rect(size));
        foreach (var tile in _tiles)
        {
            var rect = new Rect(tile.X, tile.Y, tile.Width, tile.Height);
            if (!tile.IsLeaf)
            {
                if (tile.Depth > 0)
                {
                    context.FillRectangle(frame, rect, tile.Width > 12 && tile.Height > 12 ? 3 : 0);
                }

                continue;
            }

            var fill = byChange ? ChangeColor(tile.Node, comparison!, dark) : TileColor(tile.Node, dark);
            var faded = highlight is { } wanted && (!tile.Node.IsFile || FileCategories.Of(tile.Node) != wanted);
            var inner = tile.Width > 3 && tile.Height > 3 ? rect.Deflate(0.5) : rect;
            var radius = inner.Width > 14 && inner.Height > 14 ? 2.5 : 0;
            using (faded ? context.PushOpacity(0.18) : default(DrawingContext.PushedState?))
            {
                if (inner.Width >= 18 && inner.Height >= 18)
                {
                    // the soft light of a large tile: a little lighter at the top left, a little darker at the bottom right
                    var light = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                        GradientStops = { new GradientStop(DataColors.Shade(fill, 0.16), 0), new GradientStop(DataColors.Shade(fill, -0.10), 1) },
                    };
                    context.FillRectangle(light, inner, (float)radius);
                }
                else
                {
                    context.FillRectangle(new SolidColorBrush(fill), inner, (float)radius);
                }

                if (inner.Width >= 72 && inner.Height >= 34 && labelled < 400)
                {
                    labelled++;
                    var ink = new SolidColorBrush(DataColors.LabelOn(fill));
                    DrawText(context, tile.Node.Name, typeface, 12, ink, inner.TopLeft + new Point(6, 4), inner.Width - 12);
                    DrawText(context, Ui.FormatSize(tile.Node.Size), smallTypeface, 11, ink, inner.TopLeft + new Point(6, 20), inner.Width - 12, opacity: 0.85);
                }
            }
        }

        // the names of the large folders, on a small plate at their top left (after the tiles, so nothing covers them)
        foreach (var tile in _tiles.Where(tile => !tile.IsLeaf && tile.Depth == 1 && tile.Width >= 110 && tile.Height >= 60))
        {
            var text = $"{tile.Node.Name}  ·  {Ui.FormatSize(tile.Node.Size)}";
            var formatted = Formatted(text, typeface, 11.5, labelInk, tile.Width - 26);
            var plate = new Rect(tile.X + 5, tile.Y + 5, Math.Min(formatted.WidthIncludingTrailingWhitespace + 16, tile.Width - 10), formatted.Height + 6);
            using (context.PushOpacity(0.92))
            {
                context.FillRectangle(labelBack, plate, 5);
            }

            context.DrawText(formatted, plate.TopLeft + new Point(8, 3));
        }

    }

    private static Color TileColor(ScanNode node, bool dark) => node.Kind switch
    {
        NodeKind.File => DataColors.Category(FileCategories.Of(node), dark),
        // a folder whose files are too small to be drawn alone, or the sum of small things: the neutral of "Sonstige", a step lighter
        _ => DataColors.Shade(DataColors.Category(FileCategory.Other, dark), dark ? -0.15 : 0.25),
    };

    private static Color ChangeColor(ScanNode node, TreeComparison comparison, bool dark)
    {
        var change = comparison.ChangeOf(node);
        var strength = change.Kind switch
        {
            ChangeKind.New => 1,
            ChangeKind.Grown => node.Size > 0 ? (double)change.Delta / node.Size : 0,
            ChangeKind.Shrunk => node.Size - change.Delta > 0 ? (double)-change.Delta / (node.Size - change.Delta) : 0,
            _ => 0,
        };
        return DataColors.Change(change.Kind, strength, dark);
    }

    private static void DrawText(DrawingContext context, string text, Typeface typeface, double size, IBrush ink, Point at, double maxWidth, double opacity = 1)
    {
        if (maxWidth < 12)
        {
            return;
        }

        using (opacity < 1 ? context.PushOpacity(opacity) : default(DrawingContext.PushedState?))
        {
            context.DrawText(Formatted(text, typeface, size, ink, maxWidth), at);
        }
    }

    private static FormattedText Formatted(string text, Typeface typeface, double size, IBrush ink, double maxWidth)
    {
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, ink)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        return formatted;
    }

    private static Rect Inner(TreemapTile tile, double inset)
    {
        var rect = new Rect(tile.X, tile.Y, tile.Width, tile.Height);
        return rect.Width > inset * 2 + 1 && rect.Height > inset * 2 + 1 ? rect.Deflate(inset) : rect;
    }

    /// <summary>A brush of MatDesktop's theme in the current mode.</summary>
    private IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes0.Transparent;

    /// <summary>The empty brush when a key is missing (it never is: MatDesktop defines every one).</summary>
    private static class Brushes0
    {
        public static readonly IBrush Transparent = new ImmutableSolidColorBrush(default(Color));
    }
}
