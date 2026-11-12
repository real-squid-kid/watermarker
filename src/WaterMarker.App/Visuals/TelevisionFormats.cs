using System.Windows;
using System.Windows.Controls;

namespace WaterMarker.App.Visuals;

public static class TelevisionFormats
{
    public const double Default = 4.0 / 3;
    public static IReadOnlyList<double> Allowed { get; } = Array.AsReadOnly(new[] { 1.0, 4.0 / 3, 3.0 / 4, 16.0 / 9, 9.0 / 16 });

    public static double Select(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        var source = (double)width / height;
        // Minimise letterbox area without cropping or distorting the content.
        return Allowed.MaxBy(ratio => Math.Min(source / ratio, ratio / source));
    }

    public static Size Fit(Size available, double ratio)
    {
        var width = Math.Min(available.Width, available.Height * ratio);
        return new(Math.Max(0, width), Math.Max(0, width / ratio));
    }
}

public sealed class TelevisionHost : Decorator
{
    public static readonly DependencyProperty AspectRatioProperty = DependencyProperty.Register(nameof(AspectRatio),
        typeof(double), typeof(TelevisionHost), new FrameworkPropertyMetadata(TelevisionFormats.Default,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange),
        value => value is double ratio && TelevisionFormats.Allowed.Contains(ratio));

    public double AspectRatio { get => (double)GetValue(AspectRatioProperty); set => SetValue(AspectRatioProperty, value); }

    protected override Size MeasureOverride(Size constraint)
    {
        var available = new Size(double.IsInfinity(constraint.Width) ? 700 : constraint.Width,
            double.IsInfinity(constraint.Height) ? 460 : constraint.Height);
        var size = TelevisionFormats.Fit(available, AspectRatio);
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var size = TelevisionFormats.Fit(arrangeSize, AspectRatio);
        Child?.Arrange(new Rect((arrangeSize.Width - size.Width) / 2, (arrangeSize.Height - size.Height) / 2, size.Width, size.Height));
        return arrangeSize;
    }
}
