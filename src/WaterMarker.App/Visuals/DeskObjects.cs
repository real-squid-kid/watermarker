using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WaterMarker.App.Visuals;

// Materials are vector drawings in DIPs; no skin is ever composited over the user's media.
internal static class Materials
{
    public static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    public static LinearGradientBrush Gradient(string light, string dark, bool horizontal = false)
    {
        var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(light),
            (Color)ColorConverter.ConvertFromString(dark), horizontal ? 0 : 90);
        brush.Freeze();
        return brush;
    }

    public static readonly DrawingBrush Felt = Fibres();
    public static readonly DrawingBrush Leather = LeatherPores();

    private static DrawingBrush Fibres()
    {
        var drawing = new DrawingGroup();
        var random = new Random(73);
        using (var dc = drawing.Open())
        {
            for (var i = 0; i < 180; i++)
            {
                var x = random.NextDouble() * 96;
                var y = random.NextDouble() * 96;
                var pen = new Pen(Brush(i % 2 == 0 ? "#164CA18B" : "#19031F19"), .45);
                dc.DrawLine(pen, new Point(x, y), new Point(x + random.NextDouble() * 3 - 1.5, y + random.NextDouble() * 2 + .5));
            }
        }
        return Tile(drawing, 96, 96);
    }

    private static DrawingBrush LeatherPores()
    {
        var drawing = new DrawingGroup();
        var random = new Random(19);
        using (var dc = drawing.Open())
            for (var i = 0; i < 90; i++)
                dc.DrawEllipse(Brush(i % 2 == 0 ? "#1E160A05" : "#14FFD69C"), null,
                    new Point(random.NextDouble() * 64, random.NextDouble() * 64), .55, .35);
        return Tile(drawing, 64, 64);
    }

    private static DrawingBrush Tile(Drawing drawing, double width, double height)
    {
        var brush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, width, height),
            Viewbox = new Rect(0, 0, width, height)
        };
        brush.Freeze();
        return brush;
    }

    public static void Shadow(DrawingContext dc, Rect rect, double radius, double depth = 4)
    {
        for (var i = 7; i >= 1; i--)
        {
            var spread = i * .65;
            var shadow = rect;
            shadow.Inflate(spread, spread * .65);
            shadow.Offset(1, depth);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb((byte)(i > 4 ? 4 : 10), 5, 14, 10)), null, shadow, radius + spread, radius + spread);
        }
    }

    public static void Screw(DrawingContext dc, Point centre, double radius = 2.5)
    {
        dc.DrawEllipse(Gradient("#E3D5AA", "#776740"), new Pen(Brush("#493D27"), .7), centre, radius, radius);
        dc.DrawLine(new Pen(Brush("#54452A"), .8), new Point(centre.X - radius * .5, centre.Y + .6), new Point(centre.X + radius * .5, centre.Y - .6));
    }

    public static StreamGeometry Polygon(params Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], true, true);
            context.PolyLineTo(points.Skip(1).ToArray(), true, false);
        }
        geometry.Freeze();
        return geometry;
    }
}

public sealed class DeskSurface : Border
{
    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 40 || h < 40) return;
        dc.DrawRectangle(Materials.Gradient("#513C29", "#271E17"), null, new Rect(0, 0, w, h));
        dc.DrawRoundedRectangle(Materials.Gradient("#B49766", "#685135"), new Pen(Materials.Brush("#D0B68A"), 1), new Rect(4, 4, w - 8, h - 8), 13, 13);
        var mat = new Rect(11, 11, w - 22, h - 22);
        dc.DrawRoundedRectangle(Materials.Gradient("#15594A", "#0D3B31"), new Pen(Materials.Brush("#092B23"), 2), mat, 9, 9);
        dc.DrawRoundedRectangle(Materials.Felt, null, mat, 9, 9);
        dc.DrawRoundedRectangle(null, new Pen(Materials.Brush("#486FAF95"), 1), new Rect(14, 14, w - 28, h - 28), 7, 7);
        // Reflections stay at the glass edges, behind all work objects.
        dc.DrawRoundedRectangle(Materials.Gradient("#25FFFFFF", "#00FFFFFF"), null, new Rect(16, 16, w - 32, 18), 5, 5);
        dc.DrawLine(new Pen(Materials.Brush("#83C9D8BA"), .7), new Point(25, 18), new Point(Math.Min(w - 25, 270), 18));
        dc.DrawLine(new Pen(Materials.Brush("#395B9B81"), 1), new Point(25, h - 18), new Point(w - 25, h - 18));
        foreach (var point in new[] { new Point(7, 7), new Point(w - 7, 7), new Point(7, h - 7), new Point(w - 7, h - 7) })
            Materials.Screw(dc, point, 2);
    }
}

public sealed class PaperSheet : Border
{
    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 40 || h < 40) return;
        var paper = new Rect(3, 3, w - 8, h - 10);
        Materials.Shadow(dc, paper, 1, 4);
        dc.DrawRectangle(Materials.Brush("#D5C7A6"), new Pen(Materials.Brush("#B5A584"), .7), new Rect(6, 7, w - 9, h - 9));
        dc.DrawRectangle(Materials.Brush("#EFE5CC"), new Pen(Materials.Brush("#D1C2A3"), .6), new Rect(1, 5, w - 8, h - 10));
        dc.DrawRectangle(Materials.Gradient("#FFFCED", "#F1E6CC"), new Pen(Materials.Brush("#D2C1A1"), .8), paper);
        for (var y = 49.0; y < h - 18; y += 28)
            dc.DrawLine(new Pen(Materials.Brush("#237A98A0"), .5), new Point(13, y), new Point(w - 15, y));
        dc.DrawLine(new Pen(Materials.Brush("#3EA56C60"), .7), new Point(14, 30), new Point(14, h - 18));
        dc.DrawGeometry(Materials.Gradient("#E0D0AC", "#FFF9E7"), new Pen(Materials.Brush("#C7B592"), .5),
            Materials.Polygon(new Point(w - 21, h - 7), new Point(w - 5, h - 23), new Point(w - 5, h - 7)));
        // A bent wire clip, with its own shadow and specular edge.
        var clip = Geometry.Parse(string.Create(CultureInfo.InvariantCulture, $"M {w - 52},24 L {w - 39},5 C {w - 33},-3 {w - 23},3 {w - 28},11 L {w - 40},29 C {w - 44},35 {w - 51},30 {w - 47},24 L {w - 35},7"));
        dc.PushTransform(new TranslateTransform(1, 1.5));
        dc.DrawGeometry(null, new Pen(Materials.Brush("#36000000"), 3), clip);
        dc.Pop();
        dc.DrawGeometry(null, new Pen(Materials.Brush("#8D8E7C"), 2.7), clip);
        dc.DrawGeometry(null, new Pen(Materials.Brush("#F0EDD6"), 1), clip);
    }
}

public sealed class LedgerBook : Border
{
    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 100 || h < 70) return;
        var cover = new Rect(2, 3, w - 5, h - 7);
        Materials.Shadow(dc, cover, 8, 4);
        dc.DrawRoundedRectangle(Materials.Gradient("#8B5435", "#4F2D20"), new Pen(Materials.Brush("#3A231A"), 1), cover, 8, 8);
        dc.DrawRoundedRectangle(Materials.Leather, null, cover, 8, 8);
        var stitch = new Pen(Materials.Brush("#B0D5AA79"), .7) { DashStyle = new DashStyle([2, 3], 0) };
        dc.DrawRoundedRectangle(null, stitch, new Rect(8, 9, w - 17, h - 19), 4, 4);
        dc.DrawRectangle(Materials.Gradient("#44251B", "#A7673F", true), null, new Rect(14, 6, 20, h - 13));
        dc.DrawLine(new Pen(Materials.Brush("#E59A6544"), .8), new Point(34, 9), new Point(34, h - 10));
        for (var y = 34.0; y < h - 25; y += 42)
        {
            dc.DrawEllipse(Materials.Brush("#2C1A13"), null, new Point(23, y), 2.8, 3.3);
            dc.DrawLine(new Pen(Materials.Brush("#C5AC83"), 1.8), new Point(22, y - 4), new Point(26, y + 5));
        }
        for (var i = 3; i >= 1; i--)
            dc.DrawRoundedRectangle(Materials.Brush(i % 2 == 0 ? "#C8BA98" : "#E1D4B6"), new Pen(Materials.Brush("#B5A47F"), .5),
                new Rect(40 + i * .7, 12 + i * 1.4, w - 53, h - 27), 2, 2);
        dc.DrawRectangle(Materials.Gradient("#FCF7E7", "#F1E6CD"), new Pen(Materials.Brush("#CABA97"), .65), new Rect(40, 11, w - 54, h - 28));
        for (var y = 44.0; y < h - 23; y += 26)
            dc.DrawLine(new Pen(Materials.Brush("#29748E9C"), .55), new Point(47, y), new Point(w - 22, y));
        dc.DrawLine(new Pen(Materials.Brush("#4EAA655B"), .7), new Point(51, 20), new Point(51, h - 22));
        dc.DrawGeometry(Materials.Gradient("#A45242", "#6F302B"), new Pen(Materials.Brush("#5B2B24"), .5),
            Materials.Polygon(new Point(w - 59, h - 26), new Point(w - 43, h - 26), new Point(w - 43, h - 3),
                new Point(w - 51, h - 9), new Point(w - 59, h - 3)));
    }
}

public sealed class StampBody : FrameworkElement
{
    public static readonly DependencyProperty BrightnessProperty = DependencyProperty.Register(nameof(Brightness),
        typeof(double), typeof(StampBody), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Brightness { get => (double)GetValue(BrightnessProperty); set => SetValue(BrightnessProperty, value); }

    private Color Brighten(string value)
    {
        var color = (Color)ColorConverter.ConvertFromString(value);
        return Color.FromArgb(color.A, (byte)Math.Min(255, Math.Round(color.R * Brightness)),
            (byte)Math.Min(255, Math.Round(color.G * Brightness)), (byte)Math.Min(255, Math.Round(color.B * Brightness)));
    }
    private SolidColorBrush Brush(string value) => new(Brighten(value));
    private LinearGradientBrush Gradient(string light, string dark, bool horizontal = false) => new(Brighten(light), Brighten(dark), horizontal ? 0 : 90);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 80 || h < 50) return;
        var c = w / 2;
        Materials.Shadow(dc, new Rect(8, h - 22, w - 16, 13), 4, 2);
        dc.DrawRoundedRectangle(Gradient("#743C37", "#3C2421"), new Pen(Brush("#291D18"), 1), new Rect(8, h - 18, w - 16, 13), 3, 3);
        dc.DrawRoundedRectangle(Gradient("#E4BF81", "#9A6737"), new Pen(Brush("#674629"), 1), new Rect(5, h - 37, w - 10, 26), 4, 4);
        dc.DrawLine(new Pen(Brush("#F6DAA0"), .8), new Point(12, h - 34), new Point(w - 12, h - 34));
        dc.DrawRoundedRectangle(Brush("#F2DFC0"), new Pen(Brush("#AF8B55"), .5), new Rect(18, h - 33, w - 36, 18), 1, 1);
        dc.DrawRectangle(Gradient("#DDD3A7", "#807048", true), new Pen(Brush("#6C5735"), .7), new Rect(c - 10, 29, 20, Math.Max(4, h - 66)));
        var handle = new StreamGeometry();
        using (var context = handle.Open())
        {
            context.BeginFigure(new Point(c - 14, 35), true, true);
            context.BezierTo(new Point(c - 12, 23), new Point(c - 32, 23), new Point(c - 31, 12), true, false);
            context.BezierTo(new Point(c - 30, -3), new Point(c + 30, -3), new Point(c + 31, 12), true, false);
            context.BezierTo(new Point(c + 32, 23), new Point(c + 12, 23), new Point(c + 14, 35), true, false);
        }
        dc.DrawGeometry(Gradient("#AD693B", "#4D2F20", true), new Pen(Brush("#3E281C"), 1), handle);
        dc.DrawEllipse(Gradient("#C58B52", "#87502D"), new Pen(Brush("#E0AD70"), .6), new Point(c, 7), 25, 5);
        dc.DrawLine(new Pen(Brush("#7CCE965D"), 1.5), new Point(c - 8, 25), new Point(c - 8, 32));
    }
}
