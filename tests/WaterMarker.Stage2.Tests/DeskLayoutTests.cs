using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WaterMarker.App;
using WaterMarker.App.Visuals;
using WaterMarker.Rendering;
using Xunit;

namespace WaterMarker.Stage2.Tests;

public sealed class DeskLayoutTests
{
    private sealed class Dialogs(ImageDocument image) : IDesktopDialogs
    {
        public string? OpenImage() => null;
        public string? SaveImage(ImageDocument source) => null;
        public string? ProjectName(string? name) => null;
        public Task<ImageDocument?> CaptureImageAsync() => Task.FromResult<ImageDocument?>(image);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public async Task DeskFitsScaledWorkAreaAndKeepsFieldsAndMediaAccessible(double scale)
    {
        using var workspace = new Workspace();
        var image = await StaWorker.Run(() => Environment.GetEnvironmentVariable("WATERMARKER_DESIGN_PHOTO") is { Length: > 0 } photo
            ? ImageDocument.Load(photo) : ImageDocument.FromCapture(CaptureTests.Pattern(new(0, 0, 640, 480))));
        var vm = new MainViewModel(workspace.Store(), new Dialogs(image)) { Recipient = "Test recipient", Channel = "Other", OtherChannel = "Private portal" };
        await vm.CaptureCommand.ExecuteAsync();
        await StaWorker.Run(() =>
        {
            var window = new MainWindow(vm);
            var content = (FrameworkElement)window.Content;
            var size = new Size(1104, 642);
            content.Measure(size);
            content.Arrange(new Rect(size));
            content.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Assert.IsType<Viewbox>(content);
            Assert.Equal(ResizeMode.CanMinimize, window.ResizeMode);
            Assert.Null(window.FindName("JournalGrid"));
            Assert.Null(window.FindName("FilterInput"));
            Assert.Equal("_Screenshot", Assert.IsType<Button>(window.FindName("ScreenshotButton")).Content);
            var fields = new[] { "ProjectInput", "RecipientInput", "ChannelInput", "OtherInput" };
            var parameters = Assert.IsType<ScrollViewer>(window.FindName("ParametersScroll"));
            var indices = new List<int>();
            foreach (var name in fields)
            {
                var field = Assert.IsAssignableFrom<Control>(window.FindName(name));
                Assert.True(field.Focusable);
                Assert.True(field.ActualHeight >= 30);
                Assert.True(field.ActualWidth >= 120);
                Assert.NotNull(field.FocusVisualStyle);
                indices.Add(field.TabIndex);
                if (name is "ProjectInput" or "RecipientInput" or "ChannelInput" or "OtherInput")
                {
                    var bounds = field.TransformToAncestor(parameters).TransformBounds(new Rect(field.RenderSize));
                    Assert.InRange(bounds.Top, 0, parameters.ViewportHeight);
                    Assert.True(bounds.Bottom <= parameters.ViewportHeight + 1, $"{name} is clipped at {scale * 100}%.");
                }
            }
            Assert.Equal(indices.OrderBy(i => i), indices);
            var preview = Assert.IsType<Image>(window.FindName("PreviewImage"));
            Assert.Same(vm.Preview, preview.Source);
            Assert.Equal(1, preview.Opacity);
            Assert.Null(preview.Effect);
            Assert.Equal(Stretch.Uniform, preview.Stretch);
            // A skin may surround the display, but may not tint or filter the actual image.
            for (DependencyObject? ancestor = preview; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
                if (ancestor is UIElement element) { Assert.Equal(1, element.Opacity); Assert.Null(element.Effect); }
            var tv = Assert.IsType<TelevisionCase>(window.FindName("Television"));
            var aperture = Assert.IsType<Grid>(window.FindName("ScreenAperture"));
            Assert.Equal(vm.TelevisionAspectRatio, tv.ActualWidth / tv.ActualHeight, 6);
            Assert.Equal(vm.TelevisionAspectRatio, aperture.ActualWidth / aperture.ActualHeight, 6);
            Assert.True(tv.AllowDrop);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<TelevisionIdle>(window.FindName("NoSignal")).Visibility);
            var stamp = Assert.IsType<Button>(window.FindName("WatermarkButton"));
            Assert.Same(vm.WatermarkCommand, stamp.Command);
            Assert.True(stamp.Focusable);
            Assert.True(stamp.ActualHeight >= 50);
            Assert.Contains(stamp.Template.Triggers.OfType<Trigger>(), t => t.Property == Button.IsPressedProperty);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<Button>(window.FindName("CancelButton")).Visibility);
            var output = Environment.GetEnvironmentVariable("WATERMARKER_STAGE2_ARTIFACTS");
            if (!string.IsNullOrEmpty(output))
            {
                Directory.CreateDirectory(output);
                var bitmap = new RenderTargetBitmap((int)(1104 * scale), (int)(642 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, $"desk-{scale:0.##}.png"));
                encoder.Save(stream);
                if (scale == 1)
                {
                    var idleModel = new MainViewModel(workspace.Store(), new Dialogs(image));
                    var idleWindow = new MainWindow(idleModel);
                    var idleContent = (FrameworkElement)idleWindow.Content;
                    idleContent.Measure(new Size(1104, 642));
                    idleContent.Arrange(new Rect(0, 0, 1104, 642));
                    idleContent.UpdateLayout();
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    var idleBitmap = new RenderTargetBitmap(1104, 642, 96, 96, PixelFormats.Pbgra32);
                    idleBitmap.Render(idleContent);
                    var idleEncoder = new PngBitmapEncoder();
                    idleEncoder.Frames.Add(BitmapFrame.Create(idleBitmap));
                    using var idleFile = File.Create(Path.Combine(output, "first-launch.png"));
                    idleEncoder.Save(idleFile);
                    idleWindow.Close();
                }
            }
            var journal = new JournalWindow(vm);
            var page = (FrameworkElement)journal.Content;
            page.Measure(new Size(1024, 580));
            page.Arrange(new Rect(0, 0, 1024, 580));
            page.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Assert.Same(vm, journal.DataContext);
            Assert.Same(vm.Journal, Assert.IsType<DataGrid>(journal.FindName("JournalGrid")).ItemsSource);
            foreach (var name in new[] { "FilterInput", "SearchInput" })
            {
                var field = Assert.IsAssignableFrom<Control>(journal.FindName(name));
                Assert.True(field.ActualHeight >= 30);
                Assert.True(field.Focusable);
                Assert.NotNull(field.FocusVisualStyle);
            }
            if (!string.IsNullOrEmpty(output) && scale == 1)
            {
                var bitmap = new RenderTargetBitmap(1024, 580, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(page);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, "journal.png"));
                encoder.Save(file);
            }
            journal.Close();
            window.Close();
            return true;
        });
        vm.ReleasePreview();
    }
}
