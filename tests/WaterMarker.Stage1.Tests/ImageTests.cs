using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WaterMarker.Core;
using WaterMarker.Rendering;
using Xunit;

namespace WaterMarker.Stage1.Tests;

public sealed class ImageTests
{
    public const string LongProject = "Дизайн упаковки коллекции: согласование иллюстраций и вариантов оформления";

    internal static BitmapSource Synthetic(int width, int height, bool dark = false, bool transparency = false)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(dark ? Brushes.Black : Brushes.White, null, new Rect(0, 0, width, height));
            for (var y = 20; y < height; y += 28)
            {
                context.DrawText(new FormattedText("WaterMarker · Project 2026 · Small text / Мелкий текст 0123456789",
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 14,
                    dark ? Brushes.White : Brushes.Black, 1), new Point(20, y));
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        if (!transparency) { bitmap.Freeze(); return bitmap; }
        var bytes = new byte[width * height * 4];
        new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0).CopyPixels(bytes, width * 4, 0);
        for (var i = 0; i < bytes.Length; i += 4) bytes[i + 3] = (byte)((i / 4) % 256);
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4);
        result.Freeze();
        return result;
    }

    internal static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    [Theory]
    [InlineData(1920, 1080, null)]
    [InlineData(1080, 1920, null)]
    [InlineData(1920, 1080, LongProject)]
    [InlineData(1080, 1920, LongProject)]
    public async Task EveryQuarterAndCenterContainAWholeCode(int width, int height, string? project)
    {
        await StaWorker.Run(() =>
        {
            var layout = WatermarkRenderer.Calculate(width, height, project);
            var crops = new[] { new Rect(0, 0, width / 2, height / 2), new Rect(width / 2, 0, width / 2, height / 2),
                new Rect(0, height / 2, width / 2, height / 2), new Rect(width / 2, height / 2, width / 2, height / 2),
                new Rect(width / 4, height / 4, width / 2, height / 2) };
            foreach (var crop in crops) Assert.Contains(layout.Labels, label => crop.Contains(label.CodeBounds));
            Assert.DoesNotContain('\n', WatermarkRenderer.PlaceholderCode);
            return true;
        });
    }

    [Fact]
    public async Task PngExportPreservesEveryAlphaValueAndDoesNotChangeSource()
    {
        using var workspace = new TestWorkspace();
        await StaWorker.Run(() =>
        {
            var path = workspace.File("alpha.png");
            SavePng(Synthetic(640, 480, transparency: true), path);
            var originalHash = ExportService.FileChecksum(path);
            var document = ImageDocument.Load(path);
            var layout = WatermarkRenderer.Calculate(640, 480, null);
            var output = workspace.File("copy.png");
            var record = new ExportService(workspace.Store()).Export(new(null, "A", "Email", output), new PreparedImage(document, layout, false));
            var actual = ImageDocument.Load(output).Pixels;
            var before = new byte[640 * 480 * 4];
            var after = new byte[before.Length];
            document.Pixels.CopyPixels(before, 640 * 4, 0);
            actual.CopyPixels(after, 640 * 4, 0);
            for (var i = 3; i < before.Length; i += 4) Assert.Equal(before[i], after[i]);
            Assert.NotEqual(before, after);
            Assert.Equal(originalHash, ExportService.FileChecksum(path));
            Assert.Equal(ExportState.Ready, record.State);
            Assert.Equal(TimeSpan.Zero, record.ExportedAt!.Value.Offset);
            Assert.False(File.Exists(record.TemporaryPath));
            return true;
        });
    }

    [Theory]
    [InlineData(2, 320, 160)]
    [InlineData(3, 320, 160)]
    [InlineData(4, 320, 160)]
    [InlineData(5, 160, 320)]
    [InlineData(6, 160, 320)]
    [InlineData(7, 160, 320)]
    [InlineData(8, 160, 320)]
    public async Task JpegExifOrientationIsAppliedBeforeExport(int orientation, int width, int height)
    {
        using var workspace = new TestWorkspace();
        await StaWorker.Run(() =>
        {
            var metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)orientation);
            var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
            var pixels = new byte[320 * 160 * 4];
            for (var y = 0; y < 160; y++)
                for (var x = 0; x < 320; x++)
                {
                    var i = (y * 320 + x) * 4;
                    var color = y < 80 ? x < 160 ? Colors.Red : Colors.Lime : x < 160 ? Colors.Blue : Colors.White;
                    pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255;
                }
            var quadrants = BitmapSource.Create(320, 160, 96, 96, PixelFormats.Bgra32, null, pixels, 320 * 4);
            encoder.Frames.Add(BitmapFrame.Create(quadrants, null, metadata, null));
            var path = workspace.File("oriented.jpg");
            using (var stream = File.Create(path)) encoder.Save(stream);
            var hash = ExportService.FileChecksum(path);
            var document = ImageDocument.Load(path);
            Assert.Equal(width, document.Pixels.PixelWidth);
            Assert.Equal(height, document.Pixels.PixelHeight);
            var topLeft = new byte[4];
            document.Pixels.CopyPixels(new Int32Rect(10, 10, 1, 1), topLeft, 4, 0);
            var expected = orientation switch { 2 or 8 => Colors.Lime, 3 or 7 => Colors.White, 4 or 6 => Colors.Blue, _ => Colors.Red };
            Assert.InRange((int)topLeft[0], Math.Max(0, expected.B - 5), Math.Min(255, expected.B + 5));
            Assert.InRange((int)topLeft[1], Math.Max(0, expected.G - 5), Math.Min(255, expected.G + 5));
            Assert.InRange((int)topLeft[2], Math.Max(0, expected.R - 5), Math.Min(255, expected.R + 5));
            foreach (var jpeg in new[] { true, false })
            {
                var layout = WatermarkRenderer.Calculate(width, height, null);
                var output = workspace.File(jpeg ? "out.jpg" : "out.png");
                new ExportService(workspace.Store()).Export(new(null, "A", "Email", output), new PreparedImage(document, layout, jpeg));
                var decoded = ImageDocument.Load(output);
                Assert.Equal(width, decoded.Pixels.PixelWidth);
                Assert.Equal(height, decoded.Pixels.PixelHeight);
                Assert.Equal(jpeg ? "JPEG" : "PNG", decoded.SourceType);
            }
            Assert.Equal(hash, ExportService.FileChecksum(path));
            return true;
        });
    }

    [Fact]
    public async Task RepeatedExportsHaveDisjointCodesAndSearchSurvivesDeletedMedia()
    {
        using var workspace = new TestWorkspace();
        await StaWorker.Run(() =>
        {
            var path = workspace.File("source.png");
            SavePng(Synthetic(640, 480), path);
            var document = ImageDocument.Load(path);
            var layout = WatermarkRenderer.Calculate(640, 480, "Photo Review");
            var store = workspace.Store();
            var project = store.CreateProject("Photo Review");
            var records = new List<ExportRecord>();
            foreach (var recipient in new[] { "A", "B", "A" })
            {
                var request = new ExportRequest(project, recipient, "Discord", workspace.File(Guid.NewGuid() + ".png"));
                records.Add(new ExportService(store).Export(request, new PreparedImage(document, layout, false)));
            }
            File.Delete(path);
            foreach (var record in records) File.Delete(record.TargetPath);
            store = workspace.Store();
            Assert.Equal(3, store.List(new(true)).Count);
            Assert.Equal(layout.Labels.Count * 3, records.SelectMany(r => r.Codes).Distinct().Count());
            foreach (var record in records)
                foreach (var code in record.Codes) Assert.Equal(record.Id, store.Find(code)!.Id);
            return true;
        });
    }

    [Fact]
    public async Task PreviewAndExportUseIdenticalPixelsForIdenticalCodes()
    {
        using var workspace = new TestWorkspace();
        await StaWorker.Run(() =>
        {
            var source = Synthetic(640, 480);
            var layout = WatermarkRenderer.Calculate(640, 480, LongProject);
            var codes = Enumerable.Repeat(WatermarkRenderer.PlaceholderCode, layout.Labels.Count).ToArray();
            var preview = WatermarkRenderer.Compose(source, WatermarkRenderer.RenderLayer(layout));
            var path = workspace.File("out.png");
            new PreparedImage(new("source.png", "PNG", source), layout, false).RenderTo(path, codes, default);
            var actual = ImageDocument.Load(path).Pixels;
            var a = new byte[640 * 480 * 4];
            var b = new byte[a.Length];
            preview.CopyPixels(a, 640 * 4, 0);
            actual.CopyPixels(b, 640 * 4, 0);
            Assert.Equal(a, b);
            return true;
        });
    }
}
