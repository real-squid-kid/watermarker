using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WaterMarker.Core;
using WaterMarker.Infrastructure;
using WaterMarker.Rendering;

namespace WaterMarker.App;

public sealed class CaptureCoordinator(Window owner)
{
    public async Task<ImageDocument?> CaptureAsync()
    {
        var windows = new List<Window>();
        var owned = owner.OwnedWindows.Cast<Window>().Where(window => window.IsVisible).ToArray();
        try
        {
            foreach (var window in owned) window.Hide();
            owner.Hide();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var snapshot = await Task.Run(() =>
            {
                DesktopCapture.WaitForComposition();
                return DesktopCapture.Capture();
            });
            var completion = new TaskCompletionSource<PixelRect?>(TaskCreationOptions.RunContinuationsAsynchronously);
            foreach (var monitor in snapshot.Monitors)
            {
                var pixels = snapshot.Pixels.Crop(monitor);
                var bitmap = ImageDocument.FromCapture(pixels).Pixels;
                var surface = new CaptureSurface(monitor, bitmap, area => completion.TrySetResult(area), error => completion.TrySetException(error));
                var overlay = new Window
                {
                    Title = "Screenshot",
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowInTaskbar = false,
                    Topmost = true,
                    ShowActivated = false,
                    Background = Brushes.Black,
                    Content = surface,
                    Cursor = Cursors.Cross,
                    UseLayoutRounding = true
                };
                void Position()
                {
                    try { DesktopCapture.PositionOverlay(new WindowInteropHelper(overlay).Handle, monitor); }
                    catch (IOException ex) { completion.TrySetException(ex); }
                }
                overlay.SourceInitialized += (_, _) => Position();
                overlay.ContentRendered += (_, _) => Position();
                overlay.PreviewKeyDown += (_, e) =>
                {
                    if (e.Key == Key.Escape) { e.Handled = true; completion.TrySetResult(null); }
                };
                overlay.Closed += (_, _) => completion.TrySetResult(null);
                windows.Add(overlay);
                overlay.Show();
            }
            var pointer = DesktopCapture.CursorPosition();
            var active = snapshot.Monitors.ToList().FindIndex(m => m.Contains(pointer));
            windows[Math.Max(0, active)].Activate();
            var selection = await completion.Task;
            return selection is { } area ? ImageDocument.FromCapture(snapshot.Pixels.Crop(area)) : null;
        }
        finally
        {
            Mouse.Capture(null);
            foreach (var window in windows) window.Close();
            owner.Show();
            foreach (var window in owned) window.Show();
            owner.Activate();
        }
    }
}

public sealed class CaptureSurface : FrameworkElement
{
    private readonly PixelRect monitor;
    private readonly BitmapSource snapshot;
    private readonly CaptureSelection selection;
    private readonly Action<PixelRect?> finish;
    private readonly Action<Exception> fail;

    public CaptureSurface(PixelRect monitor, BitmapSource snapshot, Action<PixelRect?> finish, Action<Exception>? fail = null)
    {
        this.monitor = monitor;
        this.snapshot = snapshot;
        this.finish = finish;
        this.fail = fail ?? (_ => finish(null));
        selection = new(monitor);
        Focusable = true;
        Cursor = Cursors.Cross;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        drawing.DrawImage(snapshot, bounds);
        var area = selection.Area;
        var region = new Rect((area.Left - monitor.Left) * ActualWidth / monitor.Width,
            (area.Top - monitor.Top) * ActualHeight / monitor.Height,
            area.Width * ActualWidth / monitor.Width, area.Height * ActualHeight / monitor.Height);
        var shade = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(bounds), new RectangleGeometry(region));
        drawing.DrawGeometry(new SolidColorBrush(Color.FromArgb(100, 0, 0, 0)), null, shade);
        if (area.Width > 0 && area.Height > 0)
            drawing.DrawRectangle(null, new Pen(Brushes.White, 1), region);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!ReadPointer(out var point)) return;
        if (!monitor.Contains(point)) return;
        Focus();
        selection.Begin(point);
        if (!CaptureMouse()) { selection.Finish(point); finish(null); return; }
        InvalidateVisual();
        e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!selection.IsDragging) return;
        if (!ReadPointer(out var point)) return;
        selection.Move(point);
        InvalidateVisual();
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!selection.IsDragging) return;
        if (!ReadPointer(out var point)) return;
        var area = selection.Finish(point);
        ReleaseMouseCapture();
        InvalidateVisual();
        if (area is not null) finish(area);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (selection.IsDragging) finish(null);
    }

    private bool ReadPointer(out PixelPoint point)
    {
        try { point = DesktopCapture.CursorPosition(); return true; }
        catch (IOException ex) { point = default; fail(ex); return false; }
    }
}
