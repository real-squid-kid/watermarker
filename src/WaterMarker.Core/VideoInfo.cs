namespace WaterMarker.Core;

public sealed record VideoAudioStream(int Index, string Codec, double? StartSeconds, double? DurationSeconds, string? TimeBase);

public sealed record VideoInfo(string SourcePath, int Width, int Height, TimeSpan Duration,
    int StreamIndex, IReadOnlyList<string> AudioCodecs, string Codec = "", double StartOffsetSeconds = 0,
    bool? Mp4AudioCompatible = null, bool? MkvAudioCompatible = null,
    double StreamStartSeconds = 0, double ContainerStartSeconds = 0,
    string? TimeBase = null, string? FrameRate = null, string? AverageFrameRate = null,
    string? PixelFormat = null, string? ColorTransfer = null, int Rotation = 0,
    IReadOnlyList<VideoAudioStream>? AudioStreams = null, bool StreamStartKnown = true)
{
    public string SourceName => Path.GetFileName(SourcePath);
    public bool RequiresMkv => Mp4AudioCompatible.HasValue ? !Mp4AudioCompatible.Value
        : AudioCodecs.Any(codec => codec is not ("aac" or "mp3" or "ac3" or "eac3" or "alac"));
    public bool SupportsMkv => MkvAudioCompatible != false;
}

public sealed record VideoProgress(TimeSpan Processed, TimeSpan Duration)
{
    public double Percent => Duration > TimeSpan.Zero ? Math.Clamp(Processed.TotalSeconds / Duration.TotalSeconds * 100, 0, 100) : 0;
}
