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

    public void ValidateOutput(string path)
    {
        var result = tools.ProbeAsync(path).GetAwaiter().GetResult();
        if (result.Codec != "h264" || result.Width != video.Width || result.Height != video.Height ||
            !result.AudioCodecs.SequenceEqual(video.AudioCodecs) || Math.Abs((result.Duration - video.Duration).TotalSeconds) > .5)
            throw new IOException("The output video could not be verified.");
    }
}
