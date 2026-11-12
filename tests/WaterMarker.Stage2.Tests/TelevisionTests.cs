using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WaterMarker.App;
using WaterMarker.App.Visuals;
using WaterMarker.Core;
using WaterMarker.Rendering;
using Xunit;

namespace WaterMarker.Stage2.Tests;

public sealed class TelevisionTests
{
    [Theory]
    [InlineData(500, 500, 1, 1)]
    [InlineData(640, 480, 4, 3)]
    [InlineData(480, 640, 3, 4)]
    [InlineData(640, 360, 16, 9)]
    [InlineData(360, 640, 9, 16)]
    [InlineData(600, 500, 4, 3)]
    [InlineData(2400, 1000, 16, 9)]
    [InlineData(1000, 2400, 9, 16)]
    public void SelectsThePresetWithTheLeastUnusedScreenArea(int width, int height, int x, int y)
    {
        var selected = TelevisionFormats.Select(width, height);
        Assert.Equal((double)x / y, selected, 10);
        Assert.Contains(selected, TelevisionFormats.Allowed);
        var coverage = Math.Min((double)width / height / selected, selected * height / width);
        foreach (var ratio in TelevisionFormats.Allowed)
            Assert.True(coverage + 1e-10 >= Math.Min((double)width / height / ratio, ratio * height / width));
    }

    private sealed class Dialogs : IDesktopDialogs
    {
        public ImageDocument? Image { get; set; }
        public string? OpenImage() => null;
        public string? SaveImage(ImageDocument image) => null;
        public string? ProjectName(string? currentName) => null;
        public Task<ImageDocument?> CaptureImageAsync() => Task.FromResult(Image);
    }

    private static ImageDocument Solid(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        { pixels[i] = 208; pixels[i + 1] = 144; pixels[i + 2] = 48; pixels[i + 3] = 255; }
        return ImageDocument.FromCapture(new(new(0, 0, width, height), pixels));
    }

    private static void Layout(MainWindow window)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1104, 642));
        content.Arrange(new Rect(0, 0, 1104, 642));
        content.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }

    private static RenderTargetBitmap Render(FrameworkElement visual, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static void Save(BitmapSource bitmap, string name)
    {
        var directory = Environment.GetEnvironmentVariable("WATERMARKER_STAGE2_ARTIFACTS");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }

    [Fact]
    public async Task ReceiverAndApertureKeepAllFiveRatiosWithoutResizingTheWindowOrAddingBars()
    {
        using var workspace = new Workspace();
        await OnDispatcher(async () =>
        {
            var dialogs = new Dialogs();
            var vm = new MainViewModel(workspace.Store(), dialogs);
            var window = new MainWindow(vm);
            try
            {
                var dimensions = (window.Width, window.Height);
                Layout(window);
                Assert.Equal(Visibility.Visible, ((TelevisionIdle)window.FindName("NoSignal")).Visibility);
                Save(Render((FrameworkElement)window.Content, 1104, 642), "first-launch.png");
                vm.Recipient = "Test recipient";
                foreach (var (width, height, name) in new[] { (480, 480, "1x1"), (640, 480, "4x3"), (480, 640, "3x4"), (640, 360, "16x9"), (360, 640, "9x16") })
                {
                    dialogs.Image = Solid(width, height);
                    await vm.CaptureCommand.ExecuteAsync();
                    Layout(window);
                    Assert.Equal(dimensions, (window.Width, window.Height));
                    var receiver = (TelevisionCase)window.FindName("Television");
                    var aperture = (Grid)window.FindName("ScreenAperture");
                    var image = (Image)window.FindName("PreviewImage");
                    Assert.Equal((double)width / height, receiver.ActualWidth / receiver.ActualHeight, 6);
                    Assert.Equal((double)width / height, aperture.ActualWidth / aperture.ActualHeight, 6);
                    Assert.Equal(Visibility.Collapsed, ((TelevisionIdle)window.FindName("NoSignal")).Visibility);
                    var bitmap = Render((FrameworkElement)window.Content, 1104, 642);
                    var pixels = new byte[1104 * 642 * 4];
                    bitmap.CopyPixels(pixels, 1104 * 4, 0);
                    var bounds = image.TransformToAncestor((Visual)window.Content).TransformBounds(new Rect(image.RenderSize));
                    foreach (var point in new[] { new Point(bounds.Left + 3, bounds.Top + 3), new Point(bounds.Right - 3, bounds.Top + 3),
                        new Point(bounds.Left + 3, bounds.Bottom - 3), new Point(bounds.Right - 3, bounds.Bottom - 3) })
                    {
                        var index = ((int)point.Y * 1104 + (int)point.X) * 4;
                        Assert.True(pixels[index] + pixels[index + 1] + pixels[index + 2] > 25, $"Unexpected black bar in {name}.");
                    }
                    Save(bitmap, $"receiver-{name}.png");
                }
                dialogs.Image = Solid(600, 500);
                await vm.CaptureCommand.ExecuteAsync();
                Layout(window);
                Assert.Equal(4.0 / 3, vm.TelevisionAspectRatio);
                Assert.Equal(600, vm.Preview!.PixelWidth);
                Assert.Equal(500, vm.Preview.PixelHeight);
                Assert.Equal(dimensions, (window.Width, window.Height));
                Save(Render((FrameworkElement)window.Content, 1104, 642), "receiver-unmatched.png");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task AnalogueNoiseIsGrayscaleFrozenAndUnchangedBetweenFrames()
    {
        await OnDispatcher(async () =>
        {
            Assert.True(TelevisionIdle.StaticSignal.IsFrozen);
            var view = new TelevisionIdle();
            view.Measure(new Size(320, 240));
            view.Arrange(new Rect(0, 0, 320, 240));
            var before = new byte[320 * 240 * 4];
            Render(view, 320, 240).CopyPixels(before, 320 * 4, 0);
            await Task.Delay(90, TestContext.Current.CancellationToken);
            view.InvalidateVisual();
            view.UpdateLayout();
            var after = new byte[before.Length];
            Render(view, 320, 240).CopyPixels(after, 320 * 4, 0);
            Assert.Equal(before, after);
            Assert.True(before.Min() < 40);
            for (var i = 0; i < before.Length; i += 4)
            { Assert.Equal(before[i], before[i + 1]); Assert.Equal(before[i], before[i + 2]); }
        });
    }

    [Fact]
    public async Task DroppedFileUsesTheImagePipelineAndInvalidDropsPreserveTheSource()
    {
        using var workspace = new Workspace();
        var path = workspace.File("Dropped image.PNG");
        await StaWorker.Run(() =>
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Solid(640, 360).Pixels));
            using var stream = File.Create(path);
            encoder.Save(stream);
            return true;
        });
        var hash = ExportService.FileChecksum(path);
        var vm = new MainViewModel(workspace.Store(), new Dialogs()) { Recipient = "Recipient", Channel = "Email" };
        await vm.OpenDroppedFilesAsync([path]);
        Assert.Contains("Dropped image.PNG", vm.SourceDisplay);
        Assert.Equal(16.0 / 9, vm.TelevisionAspectRatio);
        var preview = vm.Preview;
        await vm.OpenDroppedFilesAsync([path, path]);
        Assert.Equal("Drop one PNG, JPEG, MP4, MOV or MKV file.", vm.Status);
        Assert.Same(preview, vm.Preview);
        Assert.False(MainViewModel.CanDropFiles([workspace.Root]));
        Assert.False(MainViewModel.CanDropFiles([]));
        var corrupt = workspace.File("bad.png");
        File.WriteAllText(corrupt, "not an image");
        await vm.OpenDroppedFilesAsync([corrupt]);
        Assert.Same(preview, vm.Preview);
        Assert.Equal("Recipient", vm.Recipient);
        Assert.Equal("Email", vm.Channel);
        Assert.Equal(hash, ExportService.FileChecksum(path));
        vm.ReleasePreview();
    }

    private static Task OnDispatcher(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
