using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WaterMarker.App;
using WaterMarker.Core;
using WaterMarker.Rendering;
using Xunit;

namespace WaterMarker.Stage2.Tests;

public sealed class CaptureTests
{
    internal static ScreenPixels Pattern(PixelRect bounds)
    {
        var pixels = new byte[bounds.Width * bounds.Height * 4];
        for (var y = 0; y < bounds.Height; y++)
            for (var x = 0; x < bounds.Width; x++)
            {
                var i = (y * bounds.Width + x) * 4;
                pixels[i] = (byte)x; pixels[i + 1] = (byte)y; pixels[i + 2] = (byte)(x + y); pixels[i + 3] = 255;
            }
        return new(bounds, pixels);
    }

    [Theory]
    [InlineData(-1920, 0, 1.0)]
    [InlineData(-1920, -1080, 1.25)]
    [InlineData(1920, 0, 1.5)]
    public async Task PhysicalCropSurvivesNegativeCoordinatesAndDisplayScale(int left, int top, double scale)
    {
        var monitor = new PixelRect(left, top, 1920, 1080);
        var desktop = Pattern(monitor);
        var selection = new CaptureSelection(monitor);
        selection.Begin(new(left + 263, top + 148));
        var area = selection.Finish(new(left + 520, top + 269))!.Value;
        Assert.Equal(new PixelRect(left + 263, top + 148, 257, 121), area);
        var crop = desktop.Crop(area);
        Assert.Equal(257 * 121 * 4, crop.Bgra.Length);
        Assert.Equal((byte)7, crop.Bgra[0]);
        Assert.Equal((byte)148, crop.Bgra[1]);
        await StaWorker.Run(() =>
        {
            var document = ImageDocument.FromCapture(crop);
            Assert.Equal("Screenshot", document.SourceType);
            Assert.Equal(257, document.Pixels.PixelWidth);
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
            using (var drawing = visual.RenderOpen()) drawing.DrawImage(document.Pixels, new Rect(0, 0, 257 / scale, 121 / scale));
            var displayed = new RenderTargetBitmap(257, 121, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            displayed.Render(visual);
            var bytes = new byte[crop.Bgra.Length];
            displayed.CopyPixels(bytes, 257 * 4, 0);
            Assert.Equal(crop.Bgra, bytes);
            return true;
        });
    }

    [Fact]
    public void DragIsClampedToTheMonitorWhereItStarted()
    {
        var monitor = new PixelRect(-1920, 0, 1920, 1080);
        var selection = new CaptureSelection(monitor);
        selection.Begin(new(-1500, 100));
        Assert.Equal(new PixelRect(-1500, 100, 1500, 980), selection.Finish(new(900, 2000)));
        selection.Begin(new(-500, 500));
        Assert.Equal(new PixelRect(-1920, 0, 1420, 500), selection.Finish(new(-3000, -200)));
        Assert.Throws<ArgumentException>(() => selection.Begin(new(10, 10)));
        selection.Begin(new(-200, 200));
        Assert.Null(selection.Finish(new(-200, 200)));
    }

    private sealed class Dialogs : IDesktopDialogs
    {
        public ImageDocument? Capture { get; set; }
        public bool Fail { get; set; }
        public string? Target { get; set; }
        public string? OpenPath { get; set; }
        public string? OpenImage() => OpenPath;
        public string? SaveImage(ImageDocument image) => Target;
        public string? ProjectName(string? currentName) => null;
        public Task<ImageDocument?> CaptureImageAsync() => Fail ? throw new IOException("Отказано в доступе.") : Task.FromResult(Capture);
    }

    [Fact]
    public async Task CancelAndCaptureFailureKeepThePreviousImageAndFields()
    {
        using var workspace = new Workspace();
        var dialogs = new Dialogs { Capture = await StaWorker.Run(() => ImageDocument.FromCapture(Pattern(new(0, 0, 640, 480)))) };
        var vm = new MainViewModel(workspace.Store(), dialogs) { Recipient = "Recipient", Channel = "Discord" };
        await vm.CaptureCommand.ExecuteAsync();
        var preview = vm.Preview;
        var source = vm.SourceDisplay;
        dialogs.Capture = null;
        await vm.CaptureCommand.ExecuteAsync();
        Assert.Same(preview, vm.Preview);
        Assert.Equal(source, vm.SourceDisplay);
        dialogs.Fail = true;
        await vm.CaptureCommand.ExecuteAsync();
        Assert.Equal("Screen capture failed. Try again.", vm.Status);
        Assert.Same(preview, vm.Preview);
        Assert.Equal("Recipient", vm.Recipient);
        Assert.Equal("Discord", vm.Channel);
        dialogs.Fail = false;
        dialogs.OpenPath = workspace.File("не существует.png");
        await vm.OpenCommand.ExecuteAsync();
        Assert.Equal("The media could not be opened. Check its location and format.", vm.Status);
        Assert.Same(preview, vm.Preview);
        Assert.Equal(source, vm.SourceDisplay);
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.png"));
        vm.ReleasePreview();
    }

    [Fact]
    public async Task CapturedPixelsExportDirectlyFromMemoryWithoutAnUnmarkedFile()
    {
        using var workspace = new Workspace();
        var dialogs = new Dialogs
        {
            Capture = await StaWorker.Run(() => ImageDocument.FromCapture(Pattern(new(-1920, 0, 640, 480)))),
            Target = workspace.File("copy.png")
        };
        var vm = new MainViewModel(workspace.Store(), dialogs) { Recipient = "A" };
        await vm.CaptureCommand.ExecuteAsync();
        await vm.WatermarkCommand.ExecuteAsync();
        var record = Assert.Single(vm.Journal);
        Assert.Equal("Screenshot", record.SourceType);
        Assert.Equal("Screenshot.png", record.SourceName);
        Assert.Equal(new[] { dialogs.Target }, Directory.GetFiles(workspace.Root, "*.png"));
        var size = await StaWorker.Run(() => ImageDocument.Load(dialogs.Target!).Pixels.PixelWidth);
        Assert.Equal(640, size);
        vm.ReleasePreview();
    }
}
