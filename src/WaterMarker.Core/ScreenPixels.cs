namespace WaterMarker.Core;

public readonly record struct PixelPoint(int X, int Y);

public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => checked(Left + Width);
    public int Bottom => checked(Top + Height);
    public bool Contains(PixelPoint point) => point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;
    public bool Contains(PixelRect rect) => rect.Width > 0 && rect.Height > 0 && rect.Left >= Left && rect.Top >= Top && rect.Right <= Right && rect.Bottom <= Bottom;
}

public sealed record ScreenPixels(PixelRect Bounds, byte[] Bgra)
{
    public ScreenPixels Crop(PixelRect area)
    {
        if (!Bounds.Contains(area)) throw new ArgumentException("The selection must stay inside one monitor.");
        if (Bgra.Length != checked(Bounds.Width * Bounds.Height * 4)) throw new ArgumentException("Invalid screen buffer.");
        var bytes = new byte[checked(area.Width * area.Height * 4)];
        for (var y = 0; y < area.Height; y++)
            Buffer.BlockCopy(Bgra, checked(((area.Top - Bounds.Top + y) * Bounds.Width + area.Left - Bounds.Left) * 4),
                bytes, y * area.Width * 4, area.Width * 4);
        return new(area, bytes);
    }
}

public sealed class CaptureSelection(PixelRect monitor)
{
    private PixelPoint? start;
    public PixelRect Area { get; private set; }
    public bool IsDragging => start is not null;
    public void Begin(PixelPoint point)
    {
        if (!monitor.Contains(point)) throw new ArgumentException("The selection must start on this monitor.");
        start = point;
        Area = new(point.X, point.Y, 0, 0);
    }
    public void Move(PixelPoint point)
    {
        if (start is not { } origin) return;
        var x = Math.Clamp(point.X, monitor.Left, monitor.Right);
        var y = Math.Clamp(point.Y, monitor.Top, monitor.Bottom);
        Area = new(Math.Min(origin.X, x), Math.Min(origin.Y, y), Math.Abs(x - origin.X), Math.Abs(y - origin.Y));
    }
    public PixelRect? Finish(PixelPoint point)
    {
        if (start is null) return null;
        Move(point);
        start = null;
        return Area.Width > 0 && Area.Height > 0 ? Area : null;
    }
}
