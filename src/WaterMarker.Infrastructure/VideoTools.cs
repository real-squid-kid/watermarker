using System.Globalization;
using System.Text.Json;
using WaterMarker.Core;

namespace WaterMarker.Infrastructure;

public sealed class VideoTools
{
    private static string Tool(string name)
    {
        var directory = Environment.GetEnvironmentVariable("WATERMARKER_FFMPEG_DIRECTORY");
        return string.IsNullOrWhiteSpace(directory) ? name : Path.Combine(directory, name + ".exe");
    }

    public async Task EnsureAvailableAsync(CancellationToken token = default)
    {
        var encoders = await MediaProcess.RunAsync(Tool("ffmpeg"), ["-hide_banner", "-encoders"], token).ConfigureAwait(false);
        if (!encoders.Split('\n').Any(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) == "libx264"))
            throw new NotSupportedException("This FFmpeg build has no libx264 encoder. Images and Screenshot are still available.");
    }

    public async Task<VideoInfo> ProbeAsync(string path, CancellationToken token = default)
    {
        var json = await MediaProcess.RunAsync(Tool("ffprobe"),
            ["-v", "error", "-show_streams", "-show_format", "-of", "json", Path.GetFullPath(path)], token).ConfigureAwait(false);
        return ParseProbe(path, json);
    }

    public static VideoInfo ParseProbe(string path, string json)
    {
        using var document = JsonDocument.Parse(json);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var videos = streams.Where(s => Text(s, "codec_type") == "video" &&
            !(s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var a) && a.GetInt32() == 1)).ToArray();
        if (videos.Length != 1) throw new NotSupportedException("Choose a file with exactly one main video stream.");
        var video = videos[0];
        if (Text(video, "color_transfer") is "smpte2084" or "arib-std-b67" || Text(video, "color_primaries") == "bt2020")
            throw new NotSupportedException("HDR video is not supported. Choose an SDR video.");
        if (Text(video, "sample_aspect_ratio") is { } sar && sar is not ("1:1" or "0:1"))
            throw new NotSupportedException("This video uses non-square pixels. Convert it to square pixels first.");
        var width = video.GetProperty("width").GetInt32();
        var height = video.GetProperty("height").GetInt32();
        var rotation = 0.0;
        if (video.TryGetProperty("side_data_list", out var sideData))
            foreach (var side in sideData.EnumerateArray())
                if (side.TryGetProperty("rotation", out var r)) rotation = r.GetDouble();
        if (Math.Abs(rotation % 90) > .01) throw new NotSupportedException("Only right-angle video rotation is supported.");
        if (Math.Abs(rotation % 180) > .01) (width, height) = (height, width);
        if (width < 1 || height < 1 || (long)width * height > 100_000_000) throw new NotSupportedException("Unsupported video dimensions.");
        var duration = Number(video, "duration");
        if (duration <= 0 && video.TryGetProperty("tags", out var tags) && Text(tags, "DURATION") is { } taggedDuration)
        {
            // Matroska writes nanoseconds; TimeSpan accepts seven fractional digits.
            var dot = taggedDuration.IndexOf('.');
            if (dot >= 0 && taggedDuration.Length > dot + 8) taggedDuration = taggedDuration[..(dot + 8)];
            if (TimeSpan.TryParse(taggedDuration, CultureInfo.InvariantCulture, out var tagged)) duration = tagged.TotalSeconds;
        }
        // A container may include longer audio. It is not a reliable video-length denominator.
        if (!double.IsFinite(duration) || duration <= 0 || duration > TimeSpan.MaxValue.TotalSeconds / 2)
            throw new NotSupportedException("The video duration could not be determined. Choose a complete local video file.");
        return new(Path.GetFullPath(path), width + width % 2, height + height % 2, TimeSpan.FromSeconds(duration),
            video.GetProperty("index").GetInt32(), streams.Where(s => Text(s, "codec_type") == "audio").Select(s => Text(s, "codec_name") ?? "unknown").ToArray(),
            Text(video, "codec_name") ?? "unknown");
    }

    public async Task<byte[]> PreviewAsync(VideoInfo video, CancellationToken token = default)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WaterMarker", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var frame = Path.Combine(directory, "frame.png");
            await MediaProcess.RunAsync(Tool("ffmpeg"), ["-hide_banner", "-loglevel", "error", "-nostdin", "-i", video.SourcePath,
                "-map", $"0:{video.StreamIndex}", "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-frames:v", "1", "-update", "1", "-n", frame], token).ConfigureAwait(false);
            return await File.ReadAllBytesAsync(frame, token).ConfigureAwait(false);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    public async Task EncodeAsync(VideoInfo video, byte[] watermark, string path, bool mkv,
        IProgress<VideoProgress>? progress, CancellationToken token)
    {
        if (!mkv && video.RequiresMkv) throw new NotSupportedException("The original audio needs MKV. Choose an MKV output to keep every audio track.");
        var parser = new FfmpegProgressParser(video.Duration, progress);
        string[] arguments = ["-hide_banner", "-loglevel", "error", "-nostdin", "-nostats", "-stats_period", "0.25", "-progress", "pipe:1",
            "-i", video.SourcePath, "-f", "image2pipe", "-c:v", "png", "-i", "pipe:0",
            "-filter_complex", $"[0:{video.StreamIndex}]pad=ceil(iw/2)*2:ceil(ih/2)*2[base];[base][1:v]overlay=eof_action=repeat:repeatlast=1:alpha=straight[out]",
            "-map", "[out]", "-map", "0:a?", "-map_metadata", "-1", "-map_chapters", "-1", "-c:v", "libx264", "-preset", "medium", "-crf", "18",
            "-pix_fmt", "yuv420p", "-fps_mode", "passthrough", "-c:a", "copy", "-metadata:s:v:0", "rotate=0",
            "-f", mkv ? "matroska" : "mp4", "-n", path];
        await MediaProcess.RunAsync(Tool("ffmpeg"), arguments, token, parser.Accept, watermark).ConfigureAwait(false);
        progress?.Report(new(video.Duration, video.Duration));
    }

    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var item) ? item.ToString() : null;
    private static double Number(JsonElement value, string name) => double.TryParse(Text(value, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0;
}

public sealed class FfmpegProgressParser(TimeSpan duration, IProgress<VideoProgress>? progress)
{
    private long latest;
    public void Accept(string line)
    {
        const string key = "out_time_us=";
        if (!line.StartsWith(key, StringComparison.Ordinal) || !long.TryParse(line.AsSpan(key.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds)) return;
        var ticks = Math.Clamp(microseconds, 0, duration.Ticks / 10) * 10;
        latest = Math.Max(latest, ticks);
        progress?.Report(new(TimeSpan.FromTicks(latest), duration));
    }
}
