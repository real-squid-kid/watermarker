using System.Globalization;
using System.IO;
using System.Text.Json;
using WaterMarker.App;
using WaterMarker.Core;
using WaterMarker.Infrastructure;
using WaterMarker.Rendering;
using Xunit;

namespace WaterMarker.Stage2.Tests;

public sealed class VideoMatrixTests
{
    [Fact]
    public async Task RejectsFrameAtOrBeyondDeclaredEndBeforeExport()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WATERMARKER_VIDEO_TESTS") == "1", "Set WATERMARKER_VIDEO_TESTS=1 for FFmpeg integration tests.");
        using var workspace = new Workspace();
        var source = workspace.File("ambiguous-duration.mp4");
        await CreateFixture(source, "vfr-bframes");
        var tools = new VideoTools();
        var video = await tools.ProbeAsync(source, TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => tools.CheckContainersAsync(video, TestContext.Current.CancellationToken));
        Assert.Contains("duration", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("multiple")]
    [InlineData("incompatible")]
    [InlineData("offset")]
    [InlineData("video-offset")]
    [InlineData("video-second")]
    [InlineData("silent-vfr")]
    [InlineData("vfr-bframes")]
    [InlineData("odd")]
    [InlineData("rotated")]
    public async Task RealExportPreservesStreamsTimingAndSource(string kind)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WATERMARKER_VIDEO_TESTS") == "1", "Set WATERMARKER_VIDEO_TESTS=1 for FFmpeg integration tests.");
        using var workspace = new Workspace();
        var mkv = kind is "multiple" or "incompatible" or "vfr-bframes";
        var source = workspace.File("source " + kind + (kind == "incompatible" ? ".mov" : mkv ? ".mkv" : ".mp4"));
        var target = workspace.File("copy " + kind + (mkv ? ".mkv" : ".mp4"));
        await CreateFixture(source, kind);
        var sourceHash = ExportService.FileChecksum(source);
        var tools = new VideoTools();
        var token = TestContext.Current.CancellationToken;
        var video = await tools.ProbeAsync(source, token);
        video = await tools.CheckContainersAsync(video, token);
        Assert.True(video.SupportsMkv);
        if (kind == "incompatible") Assert.True(video.RequiresMkv);
        if (!mkv) Assert.False(video.RequiresMkv);
        if (kind == "video-offset")
        {
            Assert.InRange(video.StartOffsetSeconds, .79, .81);
            var selected = await tools.PreviewAsync(video, token, TimeSpan.FromSeconds(2));
            var expected = workspace.File("expected-frame.png");
            await MediaProcess.RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-nostdin", "-ss", "2.8", "-i", source,
                "-map", $"0:{video.StreamIndex}", "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-frames:v", "1", "-update", "1", "-n", expected], token);
            Assert.Equal(await File.ReadAllBytesAsync(expected, token), selected);
        }
        if (kind == "video-second")
        {
            Assert.Equal(1, video.StreamIndex);
            Assert.NotEmpty(await tools.PreviewAsync(video, token, TimeSpan.FromSeconds(1)));
        }
        if (kind is "silent-vfr" or "vfr-bframes")
        {
            await AssertPreviewFrame(tools, video, workspace, 2.1, 2.0);
            await AssertPreviewFrame(tools, video, workspace, video.Duration.TotalSeconds - .001, 5.7);
        }
        if (kind == "odd")
        {
            var bytes = await tools.PreviewAsync(video, token);
            await StaWorker.Run(() =>
            {
                using var input = new MemoryStream(bytes);
                var frame = System.Windows.Media.Imaging.BitmapDecoder.Create(input,
                    System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
                Assert.Equal(322, frame.PixelWidth);
                Assert.Equal(240, frame.PixelHeight);
                return true;
            });
        }
        var sourceProbe = await Probe(source);
        if (kind == "rotated") Assert.Contains("rotation", sourceProbe.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var store = workspace.Store();
        var record = await StaWorker.Run(() =>
        {
            var layout = WatermarkRenderer.Calculate(video.Width, video.Height, null);
            var media = new PreparedVideo(video, layout, mkv, tools, new InlineProgress<VideoProgress>(_ => { }));
            return new ExportService(store).Export(new(null, "Recipient", "Email", target), media, token);
        });
        Assert.Equal(ExportState.Ready, record.State);
        Assert.Equal(sourceHash, ExportService.FileChecksum(source));
        var output = await tools.ProbeAsync(target, token);
        Assert.Equal("h264", output.Codec);
        Assert.Equal(video.AudioCodecs, output.AudioCodecs);
        Assert.Equal(video.Width, output.Width);
        Assert.Equal(video.Height, output.Height);
        if (kind == "odd") { Assert.Equal(322, output.Width); Assert.Equal(240, output.Height); }
        if (kind == "rotated") { Assert.Equal(240, output.Width); Assert.Equal(320, output.Height); }
        var targetProbe = await Probe(target);
        if (kind == "multiple")
        {
            Assert.Contains("WM_SECRET", sourceProbe.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("WM_SECRET", targetProbe.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        var inStreams = sourceProbe.GetProperty("streams").EnumerateArray().ToArray();
        var outStreams = targetProbe.GetProperty("streams").EnumerateArray().ToArray();
        if (kind == "vfr-bframes") Assert.True(inStreams.First(s => s.GetProperty("codec_type").GetString() == "video").GetProperty("has_b_frames").GetInt32() > 0);
        if (kind == "multiple") Assert.Contains(inStreams, s => s.GetProperty("codec_type").GetString() == "attachment");
        Assert.All(outStreams, s => Assert.Contains(s.GetProperty("codec_type").GetString(), new[] { "video", "audio" }));
        Assert.InRange(Math.Abs(StartTime(inStreams.First(s => s.GetProperty("codec_type").GetString() == "video")) -
            StartTime(outStreams.First(s => s.GetProperty("codec_type").GetString() == "video"))), 0, .002);
        Assert.Equal(video.AudioCodecs.Count, outStreams.Count(s => s.GetProperty("codec_type").GetString() == "audio"));
        for (var i = 0; i < video.AudioCodecs.Count; i++)
        {
            Assert.Equal(await AudioHash(source, i), await AudioHash(target, i));
            var inputAudio = inStreams.Where(s => s.GetProperty("codec_type").GetString() == "audio").ElementAt(i);
            var outputAudio = outStreams.Where(s => s.GetProperty("codec_type").GetString() == "audio").ElementAt(i);
            Assert.InRange(Math.Abs(StartTime(inputAudio) - StartTime(outputAudio)), 0, .025);
        }
        if (kind is "silent-vfr" or "vfr-bframes")
        {
            var inputFrames = await Frames(source);
            var outputFrames = await Frames(target);
            Assert.Equal(inputFrames.Length, outputFrames.Length);
            Assert.True(inputFrames.Zip(inputFrames.Skip(1)).Select(p => Math.Round(p.Second.Timestamp - p.First.Timestamp, 2)).Distinct().Count() > 1);
            for (var i = 0; i < inputFrames.Length; i++)
            {
                Assert.InRange(Math.Abs((inputFrames[i].Timestamp - inputFrames[0].Timestamp) - (outputFrames[i].Timestamp - outputFrames[0].Timestamp)), 0, .002);
                // B-frame packet durations are in decode order, so presentation intervals are derived from PTS.
                if (kind == "silent-vfr" || i == inputFrames.Length - 1)
                    Assert.InRange(Math.Abs(inputFrames[i].Duration - outputFrames[i].Duration), 0, .002);
            }
        }
    }

    private static double StartTime(JsonElement stream) => stream.TryGetProperty("start_time", out var value)
        && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var time) ? time : 0;

    private static async Task<JsonElement> Probe(string path)
    {
        var json = await MediaProcess.RunAsync("ffprobe", ["-v", "error", "-show_streams", "-show_format", "-of", "json", path], TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static async Task<(double Timestamp, double Duration)[]> Frames(string path)
    {
        var json = await MediaProcess.RunAsync("ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_frames",
            "-show_entries", "frame=best_effort_timestamp_time,duration_time", "-of", "json", path], TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("frames").EnumerateArray().Select(f => (
            double.Parse(f.GetProperty("best_effort_timestamp_time").GetString()!, CultureInfo.InvariantCulture),
            double.Parse(f.GetProperty("duration_time").GetString()!, CultureInfo.InvariantCulture))).ToArray();
    }

    private static Task<string> AudioHash(string path, int stream) => MediaProcess.RunAsync("ffmpeg",
        ["-hide_banner", "-loglevel", "error", "-i", path, "-map", $"0:a:{stream}", "-c:a", "pcm_s16le", "-f", "hash", "-hash", "sha256", "pipe:1"],
        TestContext.Current.CancellationToken);

    private static async Task AssertPreviewFrame(VideoTools tools, VideoInfo video, Workspace workspace, double selected, double expectedTime)
    {
        var token = TestContext.Current.CancellationToken;
        var actual = await tools.PreviewAsync(video, token, TimeSpan.FromSeconds(selected));
        var expected = workspace.File($"frame-{expectedTime.ToString(CultureInfo.InvariantCulture)}.png");
        await MediaProcess.RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-nostdin", "-ss",
            expectedTime.ToString(CultureInfo.InvariantCulture), "-i", video.SourcePath, "-map", $"0:{video.StreamIndex}",
            "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-frames:v", "1", "-update", "1", "-n", expected], token);
        Assert.Equal(await File.ReadAllBytesAsync(expected, token), actual);
    }

    private static async Task CreateFixture(string path, string kind)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        if (kind == "video-offset") args.AddRange(["-itsoffset", "0.75"]);
        args.AddRange(["-f", "lavfi", "-i",
            kind == "odd" ? "testsrc=size=321x239:rate=10:duration=4" : "testsrc2=size=320x240:rate=10:duration=4"]);
        if (kind == "offset") args.AddRange(["-itsoffset", "0.75", "-f", "lavfi", "-i", "sine=frequency=440:duration=4"]);
        if (kind is "video-offset" or "video-second") args.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:duration=4"]);
        if (kind == "incompatible") args.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:duration=4"]);
        if (kind == "multiple") args.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:duration=4", "-f", "lavfi", "-i", "sine=frequency=880:duration=4"]);
        if (kind == "video-second") args.AddRange(["-map", "1:a", "-map", "0:v"]);
        else args.AddRange(["-map", "0:v"]);
        if (kind == "multiple") args.AddRange(["-map", "1:a", "-map", "2:a"]);
        if (kind is "offset" or "video-offset" or "incompatible") args.AddRange(["-map", "1:a"]);
        if (kind is "silent-vfr" or "vfr-bframes") args.AddRange(["-vf", "setpts='if(lt(N,20),N/(10*TB),(2+(N-20)/5)/TB)'", "-fps_mode", "vfr"]);
        args.AddRange(["-c:v", "libx264", "-preset", kind == "vfr-bframes" ? "medium" : "ultrafast", "-pix_fmt", kind == "odd" ? "yuv444p" : "yuv420p"]);
        if (kind == "multiple") args.AddRange(["-c:a", "pcm_s16le", "-metadata", "title=WM_SECRET_CONTAINER",
            "-metadata:s:a:0", "title=WM_SECRET_TRACK", "-metadata:s:a:1", "comment=WM_SECRET_COMMENT"]);
        if (kind == "multiple")
        {
            var attachment = Path.Combine(Path.GetDirectoryName(path)!, "private-attachment.txt");
            await File.WriteAllTextAsync(attachment, "WM_SECRET_ATTACHMENT", TestContext.Current.CancellationToken);
            args.AddRange(["-attach", attachment, "-metadata:s:t", "mimetype=text/plain"]);
        }
        if (kind == "incompatible") args.AddRange(["-c:a", "pcm_alaw"]);
        if (kind is "offset" or "video-offset" or "video-second") args.AddRange(["-c:a", "aac"]);
        var encoded = kind == "rotated" ? path + ".unrotated.mp4" : path;
        args.AddRange(["-n", encoded]);
        await MediaProcess.RunAsync("ffmpeg", args, TestContext.Current.CancellationToken);
        if (kind == "rotated")
            await MediaProcess.RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-display_rotation:v:0", "90", "-i", encoded,
                "-map", "0", "-c", "copy", "-n", path], TestContext.Current.CancellationToken);
    }
}
