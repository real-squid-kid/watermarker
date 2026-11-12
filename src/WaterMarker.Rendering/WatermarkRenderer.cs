using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WaterMarker.Rendering;

public sealed record WatermarkLabel(Point Origin, Rect CodeBounds);

public sealed record WatermarkLayout(int Width, int Height, double FontSize, string Header, bool CodeOnNewLine,
    double CodeX, double CodeY, IReadOnlyList<WatermarkLabel> Labels);

public static class WatermarkRenderer
{
    public const string PlaceholderCode = "0000000000";
    private static readonly Typeface Font = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private static FormattedText Text(string value, double size) => new(value, CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight, Font, size, Brushes.White, 1);

    public static WatermarkLayout Calculate(int width, int height, string? projectName)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
        var fontSize = Math.Clamp(Math.Min(width, height) / 65.0, 12, 24);
        var header = "* CONFIDENTIAL *" + (projectName is null ? "" : " " + projectName.Trim() + " *");
        var maxLineWidth = Math.Max(180, Math.Min(width, height) * .38);
        var codeText = Text(PlaceholderCode, fontSize);
        var headerText = Text(header, fontSize);
        var wrap = headerText.Width + codeText.Width + fontSize > maxLineWidth;
        if (wrap)
        {
            // Explicit wrapping also handles a single long word; the code always stays intact.
            var lines = new List<string>();
            var line = "";
            foreach (var character in header)
            {
                if (Text(line + character, fontSize).Width > maxLineWidth && line.Length > 0)
                {
                    lines.Add(line.TrimEnd());
                    line = "";
                }
                line += character;
            }
            if (line.Length > 0) lines.Add(line.TrimEnd());
            header = string.Join('\n', lines);
            headerText = Text(header, fontSize);
        }
        var codeX = wrap ? 0 : headerText.Width + fontSize * .6;
        var codeY = wrap ? headerText.Height + fontSize * .15 : 0;
        var labelWidth = Math.Max(headerText.Width, codeX + codeText.Width);
        var labelHeight = Math.Max(headerText.Height, codeY + codeText.Height);
        var rotation = new RotateTransform(-30);
        var diagonal = Math.Sqrt((double)width * width + (double)height * height);
        var stepX = labelWidth + fontSize * 3;
        var stepY = labelHeight + fontSize * 3.8;
        var labels = new List<WatermarkLabel>();
        var row = 0;
        // Grid lives in rotated coordinates, so adjacent labels never overlap after rotation.
        for (var y = -diagonal; y <= diagonal; y += stepY, row++)
        {
            var shift = row % 2 == 0 ? 0 : stepX / 2;
            for (var x = -diagonal + shift; x <= diagonal; x += stepX)
            {
                var origin = rotation.Transform(new Point(x, y));
                origin.Offset(width / 2.0, height / 2.0);
                var codeBounds = rotation.TransformBounds(new Rect(codeX, codeY, codeText.Width, codeText.Height));
                codeBounds.Offset(origin.X, origin.Y);
                var labelBounds = rotation.TransformBounds(new Rect(0, 0, labelWidth, labelHeight));
                labelBounds.Offset(origin.X, origin.Y);
                if (labelBounds.IntersectsWith(new Rect(0, 0, width, height)))
                    labels.Add(new(origin, codeBounds));
            }
        }
        if (labels.Count == 0) throw new InvalidOperationException("The image is too small for a watermark.");
        return new(width, height, fontSize, header, wrap, codeX, codeY, labels);
    }

    public static BitmapSource RenderLayer(WatermarkLayout layout, IReadOnlyList<string>? codes = null)
    {
        if (codes is not null && codes.Count != layout.Labels.Count) throw new ArgumentException("Code count does not match layout.");
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var fill = new SolidColorBrush(Color.FromArgb(92, 255, 255, 255));
            var outline = new Pen(new SolidColorBrush(Color.FromArgb(92, 0, 0, 0)), Math.Max(.65, layout.FontSize * .045));
            var headerGeometry = Text(layout.Header, layout.FontSize).BuildGeometry(new Point(0, 0));
            for (var i = 0; i < layout.Labels.Count; i++)
            {
                context.PushTransform(new TranslateTransform(layout.Labels[i].Origin.X, layout.Labels[i].Origin.Y));
                context.PushTransform(new RotateTransform(-30));
                context.DrawGeometry(null, outline, headerGeometry);
                context.DrawGeometry(fill, null, headerGeometry);
                var codeGeometry = Text(codes?[i] ?? PlaceholderCode, layout.FontSize)
                    .BuildGeometry(new Point(layout.CodeX, layout.CodeY));
                context.DrawGeometry(null, outline, codeGeometry);
                context.DrawGeometry(fill, null, codeGeometry);
                context.Pop();
                context.Pop();
            }
        }
        var layer = new RenderTargetBitmap(layout.Width, layout.Height, 96, 96, PixelFormats.Pbgra32);
        layer.Render(visual);
        layer.Freeze();
        return layer;
    }

    public static BitmapSource Compose(BitmapSource source, BitmapSource layer, CancellationToken cancellationToken = default)
    {
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[checked(stride * height)];
        var overlay = new byte[pixels.Length];
        new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, stride, 0);
        layer.CopyPixels(overlay, stride, 0);
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < stride; x += 4)
            {
                var i = y * stride + x;
                if (pixels[i + 3] == 0) continue;
                var inverse = 255 - overlay[i + 3];
                // Source-atop composition keeps the original alpha, including partial transparency.
                for (var c = 0; c < 3; c++)
                    pixels[i + c] = (byte)Math.Min(255, (pixels[i + c] * inverse + 127) / 255 + overlay[i + c]);
            }
        }
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }
}
