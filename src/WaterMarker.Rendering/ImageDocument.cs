using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WaterMarker.Rendering;

public sealed record ImageDocument(string SourcePath, string SourceType, BitmapSource Pixels)
{
    public string SourceName => Path.GetFileName(SourcePath);

    public static ImageDocument Load(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder is not PngBitmapDecoder && decoder is not JpegBitmapDecoder)
            throw new NotSupportedException("Choose a PNG or JPEG image.");
        if (decoder.Frames.Count != 1) throw new NotSupportedException("Multi-frame images are not supported.");
        var frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > 100_000_000)
            throw new NotSupportedException("The image is too large (maximum 100 megapixels).");
        var orientation = 1;
        if (frame.Metadata is BitmapMetadata metadata)
        {
            foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
            {
                try
                {
                    if (metadata.ContainsQuery(query)) orientation = Convert.ToInt32(metadata.GetQuery(query));
                }
                catch (NotSupportedException) { /* some decoders do not expose EXIF queries */ }
            }
        }
        var matrix = orientation switch
        {
            2 => new Matrix(-1, 0, 0, 1, 0, 0),
            3 => new Matrix(-1, 0, 0, -1, 0, 0),
            4 => new Matrix(1, 0, 0, -1, 0, 0),
            5 => new Matrix(0, 1, 1, 0, 0, 0),
            6 => new Matrix(0, 1, -1, 0, 0, 0),
            7 => new Matrix(0, -1, -1, 0, 0, 0),
            8 => new Matrix(0, -1, 1, 0, 0, 0),
            _ => Matrix.Identity
        };
        BitmapSource oriented = matrix.IsIdentity ? frame : new TransformedBitmap(frame, new MatrixTransform(matrix));
        var converted = new FormatConvertedBitmap(oriented, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var bytes = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(bytes, stride, 0);
        // Work in physical pixels; embedded DPI must not change the watermark geometry.
        var pixels = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32, null, bytes, stride);
        pixels.Freeze();
        return new(Path.GetFullPath(path), decoder is PngBitmapDecoder ? "PNG" : "JPEG", pixels);
    }
}
