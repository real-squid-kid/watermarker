using System.IO;
using WaterMarker.App;
using WaterMarker.Core;
using WaterMarker.Infrastructure;
using WaterMarker.Rendering;
using Xunit;

namespace WaterMarker.Stage2.Tests;

public sealed class VideoWorkflowTests
{
    [Fact]
    public void ProbeUsesVideoDurationAndRejectsUnknownDurationOrHdr()
    {
        const string json = """{"streams":[{"index":0,"codec_type":"video","codec_name":"h264","width":641,"height":359,"duration":"12.5"},{"index":1,"codec_type":"audio","codec_name":"pcm_s16le"}],"format":{"duration":"14.0"}}""";
        var info = VideoTools.ParseProbe("source.mov", json);
        Assert.Equal(TimeSpan.FromSeconds(12.5), info.Duration);
        Assert.Equal(642, info.Width);
        Assert.Equal(360, info.Height);
        Assert.True(info.RequiresMkv);
        var tagged = json.Replace("\"duration\":\"12.5\"", "\"duration\":\"N/A\",\"tags\":{\"DURATION\":\"00:00:12.500000000\"}");
        Assert.Equal(TimeSpan.FromSeconds(12.5), VideoTools.ParseProbe("source.mkv", tagged).Duration);
        Assert.Throws<NotSupportedException>(() => VideoTools.ParseProbe("source.mov", json.Replace("\"12.5\"", "\"N/A\"")));
        Assert.Throws<NotSupportedException>(() => VideoTools.ParseProbe("source.mov", json.Replace("\"12.5\"", "\"N/A\"").Replace("\"14.0\"", "\"N/A\"")));
        Assert.Throws<NotSupportedException>(() => VideoTools.ParseProbe("source.mov", json.Replace("\"width\":641", "\"color_transfer\":\"smpte2084\",\"width\":641")));
    }

    private sealed class Dialogs(string target, bool cancel) : IDesktopDialogs
    {
        public VideoProgressViewModel? Progress { get; private set; }
        public string? OpenImage() => null;
        public string? SaveImage(ImageDocument image) => throw new InvalidOperationException("Video must not open the image picker.");
        public string? SaveVideo(VideoInfo video) => target;
        public string? ProjectName(string? current) => null;
        public async Task RunVideoExportAsync(VideoProgressViewModel progress, Func<Task> operation)
        {
            Progress = progress;
            var task = operation();
            if (cancel) progress.CancelCommand.Execute(null);
            await task;
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ActualVideoExportUsesDurationAndModalAndCancellationPreservesSource(bool cancel, bool mkv)
    {
        RequireFfmpeg();
        using var workspace = new Workspace();
        var source = workspace.File("Исходник & с пробелами" + (mkv ? ".mov" : ".mp4"));
        await FixtureAsync(source, mkv);
        var original = ExportService.FileChecksum(source);
        var target = workspace.File("Копия & output" + (mkv ? ".mkv" : ".mp4"));
        var dialogs = new Dialogs(target, cancel);
        var model = new MainViewModel(workspace.Store(), dialogs) { Recipient = "Test recipient" };
        await model.LoadMediaAsync(source);
        Assert.Contains("00:00:04", model.SourceDisplay);
        Assert.NotNull(model.Preview);
        await model.WatermarkCommand.ExecuteAsync();
        Assert.NotNull(dialogs.Progress);
        Assert.Equal(TimeSpan.FromSeconds(4), dialogs.Progress.Duration);
        Assert.Equal(cancel ? "Cancelled." : "Copy saved.", model.Status);
        Assert.False(model.IsBusy);
        Assert.Equal(!cancel, File.Exists(target));
        Assert.Empty(Directory.GetFiles(workspace.Root, ".watermarker-*"));
        Assert.Equal(original, ExportService.FileChecksum(source));
        Assert.Equal("Test recipient", model.Recipient);
        Assert.NotNull(model.Preview);
        if (cancel) Assert.Empty(model.Journal);
        else
        {
            Assert.Equal(100, dialogs.Progress.Percent);
            Assert.Equal("Video", Assert.Single(model.Journal).SourceType);
            var output = await new VideoTools().ProbeAsync(target, TestContext.Current.CancellationToken);
            Assert.Equal("h264", output.Codec);
            Assert.Equal(new[] { mkv ? "pcm_s16le" : "aac" }, output.AudioCodecs);
        }
        model.ReleasePreview();
    }

    [Fact]
    public async Task EncoderReportsRealProgressAndCanBeStoppedAfterItStarts()
    {
        RequireFfmpeg();
        using var workspace = new Workspace();
        var source = workspace.File("source.mp4");
        await FixtureAsync(source);
        var tools = new VideoTools();
        var video = await tools.ProbeAsync(source, TestContext.Current.CancellationToken);
        var updates = new List<VideoProgress>();
        var layer = await StaWorker.Run(() =>
        {
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(
                WatermarkRenderer.RenderLayer(WatermarkRenderer.Calculate(video.Width, video.Height, null))));
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            return buffer.ToArray();
        });
        await tools.EncodeAsync(video, layer, workspace.File("direct.mp4"), false,
            new InlineProgress<VideoProgress>(updates.Add), TestContext.Current.CancellationToken);
        Assert.True(updates.Count >= 2);
        Assert.Contains(updates, p => p.Processed > TimeSpan.Zero);
        Assert.Equal(100, updates[^1].Percent);
        using var cancellation = new CancellationTokenSource();
        var reported = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.EncodeAsync(video, layer, workspace.File("interrupted.mp4"), false,
            new InlineProgress<VideoProgress>(_ => { reported = true; cancellation.Cancel(); }), cancellation.Token));
        Assert.True(reported);
    }

    private static Task<string> FixtureAsync(string path, bool pcm = false) => MediaProcess.RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-nostdin",
        "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=4", "-f", "lavfi", "-i", "sine=frequency=440:duration=4",
        "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", pcm ? "pcm_s16le" : "aac", "-n", path], TestContext.Current.CancellationToken);

    private static void RequireFfmpeg() => Assert.SkipUnless(Environment.GetEnvironmentVariable("WATERMARKER_VIDEO_TESTS") == "1", "Set WATERMARKER_VIDEO_TESTS=1 to run real FFmpeg integration tests.");
}
