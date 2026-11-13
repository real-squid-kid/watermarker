using System.Globalization;
using System.Text.Json;
using WaterMarker.Core;

namespace WaterMarker.Infrastructure;

public sealed class VideoTools
{
    private static string Tool(string name)
    {
        var directory = Environment.GetEnvironmentVariable("WATERMARKER_FFMPEG_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(directory)) return Path.Combine(directory, name + ".exe");
        var bundledDirectory = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        // Keep the two executables together; a partial bundle must not silently mix versions with PATH.
        return File.Exists(Path.Combine(bundledDirectory, "ffmpeg.exe")) || File.Exists(Path.Combine(bundledDirectory, "ffprobe.exe"))
            ? Path.Combine(bundledDirectory, name + ".exe") : name;
    }

    public async Task EnsureAvailableAsync(CancellationToken token = default)
    {
        var ffmpegVersion = await MediaProcess.RunAsync(Tool("ffmpeg"), ["-version"], token).ConfigureAwait(false);
        var ffprobeVersion = await MediaProcess.RunAsync(Tool("ffprobe"), ["-version"], token).ConfigureAwait(false);
        ValidateToolVersions(ffmpegVersion, ffprobeVersion);
        var encoders = await MediaProcess.RunAsync(Tool("ffmpeg"), ["-hide_banner", "-encoders"], token).ConfigureAwait(false);
        if (!encoders.Split('\n').Any(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) == "libx264"))
            throw new NotSupportedException("This FFmpeg build has no libx264 encoder. Images and Screenshot are still available.");
        try
        {
            // Check the actual build's timestamp options before the user starts a long export.
            await MediaProcess.RunAsync(Tool("ffmpeg"), ["-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi",
                "-i", "color=c=black:s=16x16:r=1:d=1", "-frames:v", "1", "-c:v", "libx264", "-fps_mode", "passthrough",
                "-enc_time_base", "demux", "-bf", "0", "-f", "null", "-"], token).ConfigureAwait(false);
        }
        catch (MediaCommandException ex)
        {
            throw new NotSupportedException("This FFmpeg build cannot preserve video timestamps. Use a current build with libx264.", ex);
        }
    }

    public static void ValidateToolVersions(string ffmpegBanner, string ffprobeBanner)
    {
        static string? Version(string banner, string name)
        {
            var line = banner.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            var fields = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields is { Length: >= 3 } && fields[0] == name && fields[1] == "version" ? fields[2] : null;
        }
        var ffmpeg = Version(ffmpegBanner, "ffmpeg");
        var ffprobe = Version(ffprobeBanner, "ffprobe");
        if (ffmpeg is null || ffprobe is null || !string.Equals(ffmpeg, ffprobe, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("FFmpeg and FFprobe must come from the same build. Check the bundled files or configured folder.");
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
        if (Text(video, "color_transfer") is "smpte2084" or "arib-std-b67" || Text(video, "color_primaries") == "bt2020" ||
            video.TryGetProperty("side_data_list", out var hdrSideData) && hdrSideData.EnumerateArray().Any(side =>
                (Text(side, "side_data_type") ?? "").Contains("DOVI", StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException("HDR and BT.2020 video are not supported. Choose an SDR video.");
        if (Text(video, "sample_aspect_ratio") is { } sar && sar is not ("1:1" or "0:1"))
            throw new NotSupportedException("This video uses non-square pixels. Convert it to square pixels first.");
        var width = video.GetProperty("width").GetInt32();
        var height = video.GetProperty("height").GetInt32();
        var rotation = 0.0;
        if (video.TryGetProperty("side_data_list", out var sideData))
            foreach (var side in sideData.EnumerateArray())
                if (side.TryGetProperty("rotation", out var r)) rotation = r.GetDouble();
        var quarterTurns = Math.Round(rotation / 90);
        if (!double.IsFinite(rotation) || Math.Abs(rotation - quarterTurns * 90) > .01) throw new NotSupportedException("Only right-angle video rotation is supported.");
        if (Math.Abs(quarterTurns % 2) > .01) (width, height) = (height, width);
        if (width < 1 || height < 1 || (long)width * height > 100_000_000) throw new NotSupportedException("Unsupported video dimensions.");
        var containerStart = document.RootElement.TryGetProperty("format", out var format) ? Number(format, "start_time") : 0;
        if (!double.IsFinite(containerStart)) containerStart = 0;
        var startKnown = TryNumber(video, "start_time", out var streamStart) && double.IsFinite(streamStart);
        var videoStart = startKnown ? streamStart : containerStart;
        var duration = Number(video, "duration");
        if (duration <= 0 && video.TryGetProperty("tags", out var tags) && Text(tags, "DURATION") is { } taggedDuration)
        {
            // Matroska writes nanoseconds; TimeSpan accepts seven fractional digits.
            var dot = taggedDuration.IndexOf('.');
            if (dot >= 0 && taggedDuration.Length > dot + 8) taggedDuration = taggedDuration[..(dot + 8)];
            // Matroska's DURATION tag is the stream's end timestamp, not its length.
            if (TimeSpan.TryParse(taggedDuration, CultureInfo.InvariantCulture, out var tagged)) duration = tagged.TotalSeconds - videoStart;
        }
        // A container may include longer audio. It is not a reliable video-length denominator.
        if (!double.IsFinite(duration) || duration <= 0 || duration > TimeSpan.MaxValue.TotalSeconds / 2)
            throw new NotSupportedException("The video duration could not be determined. Choose a complete local video file.");
        var startOffset = double.IsFinite(videoStart) && double.IsFinite(containerStart) ? Math.Max(0, videoStart - containerStart) : 0;
        if (!double.IsFinite(startOffset) || startOffset > TimeSpan.MaxValue.TotalSeconds / 2)
            throw new NotSupportedException("The video timestamps are outside the supported range.");
        var audio = streams.Where(s => Text(s, "codec_type") == "audio").Select(s => new VideoAudioStream(
            s.GetProperty("index").GetInt32(), Text(s, "codec_name") ?? "unknown",
            OptionalNumber(s, "start_time"), OptionalNumber(s, "duration"), Text(s, "time_base"))).ToArray();
        return new(Path.GetFullPath(path), width + width % 2, height + height % 2, TimeSpan.FromSeconds(duration),
            video.GetProperty("index").GetInt32(), audio.Select(s => s.Codec).ToArray(), Text(video, "codec_name") ?? "unknown", startOffset,
            StreamStartSeconds: videoStart, ContainerStartSeconds: containerStart, TimeBase: Text(video, "time_base"),
            FrameRate: Text(video, "r_frame_rate"), AverageFrameRate: Text(video, "avg_frame_rate"), PixelFormat: Text(video, "pix_fmt"),
            ColorTransfer: Text(video, "color_transfer"), Rotation: (int)((quarterTurns % 4) * 90), AudioStreams: audio, StreamStartKnown: startKnown);
    }

    public async Task<VideoInfo> CheckContainersAsync(VideoInfo video, CancellationToken token = default)
    {
        if (video.AudioCodecs.Count > 0 && (!video.StreamStartKnown || video.AudioStreams?.Any(stream => stream.StartSeconds is null) == true))
            throw new NotSupportedException("The starting times of the video and audio tracks could not be determined. Choose another file.");
        var end = video.StreamStartSeconds + video.Duration.TotalSeconds;
        var interval = Math.Max(video.StreamStartSeconds, end - 2).ToString("0.######", CultureInfo.InvariantCulture);
        double? lastFrame = null;
        await MediaProcess.RunAsync(Tool("ffprobe"), ["-v", "error", "-select_streams", video.StreamIndex.ToString(CultureInfo.InvariantCulture),
            "-read_intervals", interval + "%", "-show_frames", "-show_entries", "frame=best_effort_timestamp_time", "-of", "csv=p=0", video.SourcePath], token,
            line =>
            {
                if (double.TryParse(line.Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                    double.IsFinite(value) && (lastFrame is null || value > lastFrame)) lastFrame = value;
            }).ConfigureAwait(false);
        if (lastFrame is null || lastFrame >= end - .000001)
            throw new NotSupportedException("The video duration conflicts with its frame timestamps. Choose a complete video with consistent timing.");
        if (video.AudioCodecs.Count == 0) return video with { Mp4AudioCompatible = true, MkvAudioCompatible = true };
        var mp4 = await CanMuxAudioAsync(video, false, token).ConfigureAwait(false);
        var mkv = await CanMuxAudioAsync(video, true, token).ConfigureAwait(false);
        if (!mp4 && !mkv) throw new NotSupportedException("These audio tracks cannot be copied to MP4 or MKV. Choose another video.");
        return video with { Mp4AudioCompatible = mp4, MkvAudioCompatible = mkv };
    }

    private static async Task<bool> CanMuxAudioAsync(VideoInfo video, bool mkv, CancellationToken token)
    {
        // Mux a short sample to a discarded pipe; the muxer validates all audio stream headers.
        // No unmarked audio or partial file is written to disk.
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-i", video.SourcePath,
            "-map", "0:a?", "-vn", "-c:a", "copy", "-t", "0.2", "-map_metadata", "-1", "-map_metadata:s", "-1" };
        if (!mkv) args.AddRange(["-movflags", "frag_keyframe+empty_moov"]);
        args.AddRange(["-f", mkv ? "matroska" : "mp4", "pipe:1"]);
        try
        {
            await MediaProcess.RunAsync(Tool("ffmpeg"), args, token, discardOutput: true).ConfigureAwait(false);
            return true;
        }
        catch (MediaCommandException) { return false; }
    }

    public async Task<byte[]> PreviewAsync(VideoInfo video, CancellationToken token = default, TimeSpan? position = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WaterMarker", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var frame = Path.Combine(directory, "frame.png");
            var selected = Math.Clamp((position ?? TimeSpan.Zero).TotalSeconds, 0, Math.Max(0, video.Duration.TotalSeconds - .001));
            var timestamp = video.StreamStartSeconds + selected;
            double? preceding = null;
            var interval = timestamp.ToString("0.######", CultureInfo.InvariantCulture) + "%" + (timestamp + 5).ToString("0.######", CultureInfo.InvariantCulture);
            await MediaProcess.RunAsync(Tool("ffprobe"), ["-v", "error", "-select_streams", video.StreamIndex.ToString(CultureInfo.InvariantCulture),
                "-read_intervals", interval, "-show_frames", "-show_entries", "frame=best_effort_timestamp_time", "-of", "csv=p=0", video.SourcePath], token,
                line =>
                {
                    var field = line.Split(',')[0];
                    if (double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                        double.IsFinite(value) && value <= timestamp + .000001 && (preceding is null || value > preceding)) preceding = value;
                }).ConfigureAwait(false);
            // A position between frames displays the frame whose presentation began before it.
            // Subtract one microsecond so rounding in ffprobe's timestamps cannot skip that frame.
            var seconds = preceding is null ? selected + video.StartOffsetSeconds
                : Math.Max(0, preceding.Value - video.ContainerStartSeconds - .000001);
            await MediaProcess.RunAsync(Tool("ffmpeg"), ["-hide_banner", "-loglevel", "error", "-nostdin", "-ss", seconds.ToString("0.######", CultureInfo.InvariantCulture), "-i", video.SourcePath,
                "-map", $"0:{video.StreamIndex}", "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-frames:v", "1", "-update", "1", "-n", frame], token).ConfigureAwait(false);
            return await File.ReadAllBytesAsync(frame, token).ConfigureAwait(false);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    public async Task EncodeAsync(VideoInfo video, byte[] watermark, string path, bool mkv,
        IProgress<VideoProgress>? progress, CancellationToken token)
    {
        if (!mkv && video.RequiresMkv) throw new NotSupportedException("The original audio needs MKV. Choose an MKV output to keep every audio track.");
        if (mkv && !video.SupportsMkv) throw new NotSupportedException("These original audio tracks cannot be copied to MKV. Choose MP4.");
        var parser = new FfmpegProgressParser(video.Duration, progress, TimeSpan.FromSeconds(video.StartOffsetSeconds));
        string[] arguments = ["-hide_banner", "-loglevel", "error", "-nostdin", "-nostats", "-stats_period", "0.25", "-progress", "pipe:1",
            "-i", video.SourcePath, "-f", "image2pipe", "-c:v", "png", "-i", "pipe:0",
            "-filter_complex", $"[0:{video.StreamIndex}]pad=ceil(iw/2)*2:ceil(ih/2)*2[base];[base][1:v]overlay=eof_action=repeat:repeatlast=1:alpha=straight[out]",
            "-map", "[out]", "-map", "0:a?", "-map_metadata", "-1", "-map_metadata:s", "-1", "-map_chapters", "-1", "-c:v", "libx264", "-preset", "medium", "-crf", "18",
            // B-frame reordering can change packet durations for VFR sources even when PTS matches.
            "-pix_fmt", "yuv420p", "-fps_mode", "passthrough", "-enc_time_base", "demux", "-bf", "0", "-c:a", "copy", "-metadata:s:v:0", "rotate=0",
            "-f", mkv ? "matroska" : "mp4", "-n", path];
        await MediaProcess.RunAsync(Tool("ffmpeg"), arguments, token, parser.Accept, watermark).ConfigureAwait(false);
        progress?.Report(new(video.Duration, video.Duration));
    }

    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var item) ? item.ToString() : null;
    private static double Number(JsonElement value, string name) => TryNumber(value, name, out var result) ? result : 0;
    private static bool TryNumber(JsonElement value, string name, out double result) => double.TryParse(Text(value, name), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    private static double? OptionalNumber(JsonElement value, string name) => TryNumber(value, name, out var number) && double.IsFinite(number) ? number : null;
}

public sealed class FfmpegProgressParser(TimeSpan duration, IProgress<VideoProgress>? progress, TimeSpan startOffset = default)
{
    private long latest;
    public void Accept(string line)
    {
        const string key = "out_time_us=";
        if (!line.StartsWith(key, StringComparison.Ordinal) || !long.TryParse(line.AsSpan(key.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds)) return;
        var offsetMicroseconds = Math.Max(0, startOffset.Ticks / 10);
        var elapsed = microseconds <= offsetMicroseconds ? 0 : microseconds - offsetMicroseconds;
        var ticks = Math.Clamp(elapsed, 0, duration.Ticks / 10) * 10;
        latest = Math.Max(latest, ticks);
        progress?.Report(new(TimeSpan.FromTicks(latest), duration));
    }
}
