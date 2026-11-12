using System.IO;
using System.Windows.Media.Imaging;
using WaterMarker.Core;

namespace WaterMarker.Rendering;

public sealed class PreparedImage(ImageDocument document, WatermarkLayout layout, bool jpeg) : IExportMedia
{
    public string SourceName => document.SourceName;
    public string SourceType => document.SourceType;
    public int CodeCount => layout.Labels.Count;

    public void RenderTo(string path, IReadOnlyList<string> codes, CancellationToken cancellationToken)
    {
        var output = WatermarkRenderer.Compose(document.Pixels, WatermarkRenderer.RenderLayer(layout, codes), cancellationToken);
        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = 95 } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(output));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
        stream.Flush(flushToDisk: true);
    }

    public void ValidateOutput(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1 || decoder.Frames[0].PixelWidth != layout.Width || decoder.Frames[0].PixelHeight != layout.Height)
            throw new IOException("The output image could not be verified.");
        // Force full decoding before publishing the temporary file.
        var frame = decoder.Frames[0];
        var stride = (frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8;
        frame.CopyPixels(new byte[checked(stride * frame.PixelHeight)], stride, 0);
    }
}
