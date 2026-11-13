using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WaterMarker.App;
using WaterMarker.App.Visuals;
using WaterMarker.Core;
using WaterMarker.Infrastructure;
using WaterMarker.Rendering;
using Xunit;

namespace WaterMarker.Stage2.Tests;

public sealed class ProcessingUiTests
{
    private sealed class Clock : TimeProvider
    {
        public long Now { get; set; }
        public override long GetTimestamp() => Now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds).Ticks;
    }

    [Fact]
    public void RealFfmpegTimesAreMonotonicAndClampedWithoutConfusingMicrosecondsWithMilliseconds()
    {
        var updates = new List<VideoProgress>();
        var parser = new FfmpegProgressParser(TimeSpan.FromSeconds(40), new InlineProgress<VideoProgress>(updates.Add));
        foreach (var line in new[] { "frame=400", "out_time_us=N/A", "out_time_us=-100", "out_time_us=10000000", "out_time_us=5000000", "out_time_us=90000000" })
            parser.Accept(line);
        Assert.Equal(new[] { 0.0, 25, 25, 100 }, updates.Select(p => p.Percent));
        Assert.Equal(TimeSpan.FromSeconds(10), updates[1].Processed);
        var shifted = new List<VideoProgress>();
        var delayedVideo = new FfmpegProgressParser(TimeSpan.FromSeconds(40), new InlineProgress<VideoProgress>(shifted.Add), TimeSpan.FromSeconds(.8));
        delayedVideo.Accept("out_time_us=500000");
        delayedVideo.Accept("out_time_us=10800000");
        Assert.Equal(new[] { 0.0, 25 }, shifted.Select(p => p.Percent));
    }

    [Fact]
    public void StopwatchPromptsAndPublicationFollowTheActualOperation()
    {
        var prompts = ProcessingPrompts.Load();
        Assert.Equal(50, prompts.Count);
        Assert.All(prompts, text => { Assert.EndsWith("…", text); Assert.DoesNotContain("……", text); });
        var clock = new Clock();
        var cancelled = false;
        var vm = new VideoProgressViewModel(TimeSpan.FromSeconds(100), new RelayCommand(() => cancelled = true), clock, prompts, new Random(17));
        var first = vm.Prompt;
        clock.Advance(6.999);
        vm.Tick();
        Assert.Equal(first, vm.Prompt);
        clock.Advance(.001);
        vm.Tick();
        Assert.NotEqual(first, vm.Prompt);
        Assert.Equal("00:00:07", vm.ElapsedText);
        for (var i = 0; i < 20; i++)
        {
            var previous = vm.Prompt;
            clock.Advance(7);
            vm.Tick();
            Assert.NotEqual(previous, vm.Prompt);
        }
        vm.Update(new(TimeSpan.FromSeconds(38), vm.Duration));
        Assert.Equal(38, vm.Percent);
        Assert.Equal("00:00:38 / 00:01:40 of video", vm.ProcessedText);
        vm.Update(new(TimeSpan.FromSeconds(10), vm.Duration));
        Assert.Equal(38, vm.Percent);
        vm.Update(new(vm.Duration, vm.Duration));
        Assert.Equal("99%", vm.PercentText);
        vm.Publishing();
        Assert.Equal("Saving copy", vm.Phase);
        vm.CancelCommand.Execute(null);
        Assert.True(cancelled);
        vm.Complete();
        Assert.Equal("100%", vm.PercentText);
        var finishedAt = vm.ElapsedText;
        clock.Advance(50);
        vm.Tick();
        Assert.Equal(finishedAt, vm.ElapsedText);
    }

    [Fact]
    public void SaveDirectoryStartsAtSourceThenRemembersLastSelectionAcrossRestarts()
    {
        using var workspace = new Workspace();
        var sourceDirectory = Directory.CreateDirectory(workspace.File("Исходники с пробелами")).FullName;
        var destination = Directory.CreateDirectory(workspace.File("Другой каталог")).FullName;
        var source = Path.Combine(sourceDirectory, "video.mp4");
        File.WriteAllText(source, "fixture");
        var preferences = new SaveLocations(workspace.File("settings"));
        Assert.Equal(sourceDirectory, preferences.InitialDirectory(source));
        preferences.Remember(Path.Combine(destination, "copy.mp4"));
        Assert.Equal(destination, preferences.InitialDirectory(source));
        Assert.Equal(destination, new SaveLocations(workspace.File("settings")).InitialDirectory("Screenshot.png"));
        Directory.Delete(destination);
        Assert.Equal(sourceDirectory, new SaveLocations(workspace.File("settings")).InitialDirectory(source));
        File.WriteAllText(workspace.File("settings/save-location.json"), "bad json");
        Assert.Equal(sourceDirectory, new SaveLocations(workspace.File("settings")).InitialDirectory(source));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public async Task ModalShowsLegibleProgressAndEntireStampIsClickable(double scale)
    {
        await StaWorker.Run(() =>
        {
            var clock = new Clock();
            var model = new VideoProgressViewModel(TimeSpan.FromMinutes(3), new RelayCommand(() => { }), clock);
            clock.Advance(67);
            model.Tick();
            model.Update(new(TimeSpan.FromSeconds(115), model.Duration));
            var dialog = new VideoProgressWindow(model);
            var page = (FrameworkElement)dialog.Content;
            var size = new Size(584, 312);
            page.Measure(size); page.Arrange(new Rect(size)); page.UpdateLayout();
            var progress = Assert.IsType<ProgressBar>(dialog.FindName("VideoProgress"));
            Assert.Equal(model.Percent, progress.Value);
            Assert.False(progress.IsIndeterminate);
            var cancel = Assert.IsType<Button>(dialog.FindName("CancelButton"));
            Assert.Same(model.CancelCommand, cancel.Command);
            Assert.True(cancel.IsEnabled);
            Assert.True(cancel.ActualHeight >= 30);
            foreach (var name in new[] { "Elapsed", "Prompt", "VideoProgress", "CancelButton" })
            {
                var element = Assert.IsAssignableFrom<FrameworkElement>(dialog.FindName(name));
                var bounds = element.TransformToAncestor(page).TransformBounds(new Rect(element.RenderSize));
                Assert.True(bounds.Top >= 0 && bounds.Bottom <= size.Height && bounds.Right <= size.Width, $"{name} is clipped");
            }
            var resources = new ResourceDictionary { Source = new Uri("/WaterMarker.App;component/Themes/Desk.xaml", UriKind.Relative) };
            var stamp = new Button { Content = "Watermark", Style = (Style)resources["Stamp"] };
            stamp.Measure(new Size(180, 90)); stamp.Arrange(new Rect(0, 0, 180, 90)); stamp.UpdateLayout();
            var shape = Assert.IsType<StampBody>(stamp.Template.FindName("StampShape", stamp));
            var hover = Assert.Single(stamp.Template.Triggers.OfType<Trigger>(), t => t.Property == UIElement.IsMouseOverProperty);
            Assert.Contains(hover.Setters.OfType<Setter>(), setter => setter.TargetName == "StampShape" && setter.Property == StampBody.BrightnessProperty && Equals(setter.Value, 1.15));
            foreach (var point in new[] { new Point(90, 12), new Point(90, 40), new Point(90, 64) })
            {
                var hit = VisualTreeHelper.HitTest(stamp, point)?.VisualHit;
                while (hit is not null && hit != stamp) hit = VisualTreeHelper.GetParent(hit);
                Assert.Same(stamp, hit);
            }
            Save(page, (int)(584 * scale), (int)(312 * scale), scale, $"progress-{scale:0.##}.png");
            Save(stamp, 180, 90, 1, "stamp.png");
            shape.Brightness = 1.15;
            stamp.UpdateLayout();
            Save(stamp, 180, 90, 1, "stamp-hover.png");
            dialog.Close();
            return true;
        });
    }

    private static void Save(Visual visual, int width, int height, double scale, string name)
    {
        var directory = Environment.GetEnvironmentVariable("WATERMARKER_STAGE2_ARTIFACTS");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name));
        encoder.Save(file);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealModalLoopRunsWorkAndWaitsForCancellationBeforeClosing(bool cancel)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    var cancelled = false;
                    var model = new VideoProgressViewModel(TimeSpan.FromSeconds(20), new RelayCommand(() => cancelled = true));
                    var window = new VideoProgressWindow(model) { Opacity = 0, ShowActivated = false };
                    var workFinished = false;
                    await window.RunAsync(async () =>
                    {
                        Assert.True(window.IsVisible);
                        model.Update(new(TimeSpan.FromSeconds(5), model.Duration));
                        if (cancel)
                        {
                            window.Close();
                            Assert.True(cancelled);
                            Assert.True(window.IsVisible);
                        }
                        await Task.Delay(50, TestContext.Current.CancellationToken);
                        workFinished = true;
                    });
                    Assert.True(workFinished);
                    Assert.False(window.IsVisible);
                    completion.SetResult();
                }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
    }
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
