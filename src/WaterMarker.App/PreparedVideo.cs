using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using WaterMarker.Core;
using WaterMarker.Infrastructure;
using WaterMarker.Rendering;

namespace WaterMarker.App;

public sealed class PreparedVideo(VideoInfo video, WatermarkLayout layout, bool mkv,
    VideoTools tools, IProgress<VideoProgress> progress) : IExportMedia
{
    public string SourceName => video.SourceName;
    public string SourceType => "Video";
    public int CodeCount => layout.Labels.Count;

    public void RenderTo(string path, IReadOnlyList<string> codes, CancellationToken cancellationToken)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(WatermarkRenderer.RenderLayer(layout, codes)));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        tools.EncodeAsync(video, buffer.ToArray(), path, mkv, progress, cancellationToken).GetAwaiter().GetResult();
    }

    public void ValidateOutput(string path) => ValidateOutput(path, CancellationToken.None);

    public void ValidateOutput(string path, CancellationToken cancellationToken)
    {
        var result = tools.ProbeAsync(path, cancellationToken).GetAwaiter().GetResult();
        if (result.Codec != "h264" || result.Width != video.Width || result.Height != video.Height ||
            !result.AudioCodecs.SequenceEqual(video.AudioCodecs) || Math.Abs((result.Duration - video.Duration).TotalSeconds) >
            Math.Max(TimeBase(video.TimeBase), TimeBase(result.TimeBase)) * 1.5 + .000001)
            throw new IOException("The output video could not be verified.");
        if (video.AudioStreams is not null && result.AudioStreams is not null)
            for (var i = 0; i < video.AudioStreams.Count; i++)
            {
                var original = video.AudioStreams[i];
                var copy = result.AudioStreams[i];
                if (!result.StreamStartKnown || original.StartSeconds is not { } inputStart || copy.StartSeconds is not { } outputStart)
                    throw new IOException("The output audio timing could not be verified.");
                var inputDelay = inputStart - video.StreamStartSeconds;
                var outputDelay = outputStart - result.StreamStartSeconds;
                var tolerance = new[] { TimeBase(video.TimeBase), TimeBase(result.TimeBase), TimeBase(original.TimeBase), TimeBase(copy.TimeBase) }.Max() * 1.5 + .000001;
                if (Math.Abs(inputDelay - outputDelay) > tolerance) throw new IOException("The output audio timing could not be verified.");
            }
    }

    private static double TimeBase(string? value)
    {
        var parts = value?.Split('/');
        return parts is { Length: 2 } && double.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var denominator)
            && numerator > 0 && denominator > 0 ? numerator / denominator : .001;
    }
}
