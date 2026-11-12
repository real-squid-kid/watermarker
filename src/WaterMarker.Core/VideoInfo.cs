namespace WaterMarker.Core;

public sealed record VideoInfo(string SourcePath, int Width, int Height, TimeSpan Duration,
    int StreamIndex, IReadOnlyList<string> AudioCodecs, string Codec = "")
{
    public string SourceName => Path.GetFileName(SourcePath);
    public bool RequiresMkv => AudioCodecs.Any(codec => codec is not ("aac" or "mp3" or "ac3" or "eac3" or "alac"));
}

public sealed record VideoProgress(TimeSpan Processed, TimeSpan Duration)
{
    public double Percent => Duration > TimeSpan.Zero ? Math.Clamp(Processed.TotalSeconds / Duration.TotalSeconds * 100, 0, 100) : 0;
}
