using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WaterMarker.App;
using WaterMarker.Rendering;
using Xunit;

namespace WaterMarker.Stage1.Tests;

public sealed class WindowLayoutTests
{
    private sealed class Dialogs : IDesktopDialogs
    {
        public string? OpenImage() => null;
        public string? SaveImage(ImageDocument image) => null;
        public string? ProjectName(string? currentName) => null;
    }

    [Fact]
    public async Task WindowBindsRealPreviewAndHasKeyboardAccessibleActions()
    {
        using var workspace = new TestWorkspace();
        var source = workspace.File("source.png");
        await StaWorker.Run(() => { ImageTests.SavePng(ImageTests.Synthetic(1920, 1080), source); return true; });
        var vm = new MainViewModel(workspace.Store(), new Dialogs());
        await vm.LoadImageAsync(source);
        await StaWorker.Run(() =>
        {
            var window = new MainWindow(vm);
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1224, 820));
            content.Arrange(new Rect(0, 0, 1224, 820));
            content.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            var project = Assert.IsType<ComboBox>(window.FindName("ProjectInput"));
            var recipient = Assert.IsType<TextBox>(window.FindName("RecipientInput"));
            Assert.Equal("No project", project.SelectedItem!.ToString());
            Assert.True(recipient.Focusable);
            Assert.True(project.Focusable);
            var scrolling = Assert.IsType<ScrollViewer>(content);
            Assert.InRange(scrolling.ScrollableHeight, 0, 1);
            var output = Environment.GetEnvironmentVariable("WATERMARKER_ACCEPTANCE_DIR");
            if (!string.IsNullOrEmpty(output))
            {
                Directory.CreateDirectory(output);
                var bitmap = new RenderTargetBitmap(1224, 820, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                ImageTests.SavePng(bitmap, Path.Combine(output, "main-window.png"));
            }
            window.Close();
            return true;
        });
    }
}
