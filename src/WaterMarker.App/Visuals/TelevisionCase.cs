using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WaterMarker.App.Visuals;

public sealed class TelevisionCase : Border
{
    public const double Inset = .065;
    public static readonly DependencyProperty IsDropTargetProperty = DependencyProperty.Register(nameof(IsDropTarget),
        typeof(bool), typeof(TelevisionCase), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool IsDropTarget { get => (bool)GetValue(IsDropTargetProperty); set => SetValue(IsDropTargetProperty, value); }

    protected override Size MeasureOverride(Size constraint)
    {
        Child?.Measure(new Size(Math.Max(0, constraint.Width * (1 - Inset * 2)), Math.Max(0, constraint.Height * (1 - Inset * 2))));
        return constraint;
    }

    protected override Size ArrangeOverride(Size size)
    {
        // Equal proportional insets keep both the receiver and its aperture in the same format.
        Child?.Arrange(new Rect(size.Width * Inset, size.Height * Inset, size.Width * (1 - Inset * 2), size.Height * (1 - Inset * 2)));
        return size;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 30 || h < 30) return;
        var radius = Math.Min(w, h) * .045;
        var body = new Rect(0, 0, w, h);
        Materials.Shadow(dc, body, radius, 4);
        dc.DrawRoundedRectangle(Materials.Gradient("#A87844", "#56351F"), new Pen(Materials.Brush("#301F16"), 1.5), body, radius, radius);
        dc.PushClip(new RectangleGeometry(body, radius, radius));
        for (var y = 5.0; y < h; y += 4.7)
        {
            var grain = new StreamGeometry();
            using (var context = grain.Open())
            {
                context.BeginFigure(new Point(0, y), false, false);
                context.BezierTo(new Point(w * .25, y - 4), new Point(w * .7, y + 3), new Point(w, y - 1), true, false);
            }
            dc.DrawGeometry(null, new Pen(Materials.Brush("#26301C10"), .65), grain);
        }
        dc.Pop();
        dc.DrawRoundedRectangle(null, new Pen(Materials.Brush("#73E5BB7D"), .8), new Rect(4, 4, w - 8, h - 8), radius * .8, radius * .8);
        var aperture = new Rect(w * Inset, h * Inset, w * (1 - 2 * Inset), h * (1 - 2 * Inset));
        var surround = aperture;
        surround.Inflate(4, 4);
        dc.DrawRoundedRectangle(Materials.Gradient("#C1B896", "#766F55"), new Pen(Materials.Brush("#342E20"), 1), surround, radius * .7, radius * .7);
        dc.DrawRectangle(Materials.Brush("#0B1310"), null, aperture);
        var dialRadius = Math.Min(12, w * .024);
        var x = w * (1 - Inset / 2);
        Dial(dc, new Point(x, h * .24), dialRadius, -25);
        Dial(dc, new Point(x, h * .42), dialRadius * .8, 30);
        for (var y = h * .57; y < h * .81; y += 5)
            dc.DrawRoundedRectangle(Materials.Brush("#39271B"), null, new Rect(x - dialRadius, y, dialRadius * 2, 1.7), .8, .8);
        dc.DrawEllipse(Materials.Brush("#263B2D"), new Pen(Materials.Brush("#D4B17A"), .6), new Point(x, h * .88), 2.8, 2.8);
        dc.DrawEllipse(Materials.Brush("#B3D889"), null, new Point(x, h * .88), 1.5, 1.5);
        Materials.Screw(dc, new Point(w * Inset / 2, h * Inset / 2), 1.7);
        Materials.Screw(dc, new Point(w * Inset / 2, h * (1 - Inset / 2)), 1.7);
        if (IsDropTarget) dc.DrawRoundedRectangle(null, new Pen(Materials.Brush("#FFE0A2"), 3), new Rect(1, 1, w - 2, h - 2), radius, radius);
    }

    private static void Dial(DrawingContext dc, Point centre, double radius, double angle)
    {
        dc.DrawEllipse(Materials.Brush("#382E21"), new Pen(Materials.Brush("#C4A573"), .6), centre, radius + 1.5, radius + 1.5);
        dc.DrawEllipse(Materials.Gradient("#E3D8B2", "#8E8667"), new Pen(Materials.Brush("#302D22"), .6), centre, radius, radius);
        for (var i = 0; i < 16; i++)
        {
            var a = i * Math.PI / 8;
            dc.DrawLine(new Pen(Materials.Brush("#665F48"), .5),
                new Point(centre.X + Math.Cos(a) * (radius - 1), centre.Y + Math.Sin(a) * (radius - 1)),
                new Point(centre.X + Math.Cos(a) * (radius - 3), centre.Y + Math.Sin(a) * (radius - 3)));
        }
        dc.PushTransform(new RotateTransform(angle, centre.X, centre.Y));
        dc.DrawLine(new Pen(Materials.Brush("#4B4A39"), 1.1), centre, new Point(centre.X, centre.Y - radius + 3));
        dc.Pop();
    }
}

public sealed class TelevisionIdle : FrameworkElement
{
    private static readonly BitmapSource Snow = CreateSnow();
    public static BitmapSource StaticSignal => Snow;

    private static BitmapSource CreateSnow()
    {
        const int width = 768, height = 576;
        var bytes = new byte[width * height * 4];
        var random = new Random(1977);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var moire = 34 * Math.Sin(y * .22 + 5 * Math.Sin(x * .014)) + 18 * Math.Sin((x + y) * .47);
                var grey = (byte)Math.Clamp(122 + random.Next(-84, 85) + moire, 0, 255);
                var i = (y * width + x) * 4;
                bytes[i] = bytes[i + 1] = bytes[i + 2] = grey;
                bytes[i + 3] = 255;
            }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    protected override void OnRender(DrawingContext dc)
    {
        // A single frozen analogue-noise frame: no timer, clock or render-loop animation.
        if (ActualWidth > 0 && ActualHeight > 0) dc.DrawImage(Snow, new Rect(0, 0, ActualWidth, ActualHeight));
    }
}
