using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WaterMarker.Core;
using Xunit;

namespace WaterMarker.Stage1.Tests;

public sealed class VisualAcceptanceTests
{
    [Fact]
    public async Task GenerateAcceptanceMatrixWhenRequested()
    {
        var output = Environment.GetEnvironmentVariable("WATERMARKER_ACCEPTANCE_DIR");
        if (string.IsNullOrEmpty(output)) return;
        var photoPath = Environment.GetEnvironmentVariable("WATERMARKER_PHOTO_FIXTURE");
        Assert.True(File.Exists(photoPath), "Set WATERMARKER_PHOTO_FIXTURE to a local photograph.");
        Directory.CreateDirectory(output);
        await Rendering.StaWorker.Run(() =>
        {
            var report = new StringBuilder("image,crop,whole_codes,example_code\n");
            foreach (var (width, height) in new[] { (1920, 1080), (1080, 1920) })
                foreach (var kind in new[] { "light", "dark", "photo" })
                    foreach (var project in new string?[] { null, ImageTests.LongProject })
                    {
                        var name = $"{kind}-{width}x{height}-{(project is null ? "none" : "long")}";
                        BitmapSource source;
                        if (kind == "photo")
                        {
                            var photo = Rendering.ImageDocument.Load(photoPath!).Pixels;
                            var visual = new DrawingVisual();
                            using (var drawing = visual.RenderOpen()) drawing.DrawImage(photo, new Rect(0, 0, width, height));
                            var resized = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                            resized.Render(visual);
                            resized.Freeze();
                            source = resized;
                        }
                        else source = ImageTests.Synthetic(width, height, dark: kind == "dark");
                        ImageTests.SavePng(source, Path.Combine(output, name + "-source.png"));
                        var layout = Rendering.WatermarkRenderer.Calculate(width, height, project);
                        var codes = layout.Labels.Select(_ => new CodeGenerator().Next()).ToArray();
                        var image = Rendering.WatermarkRenderer.Compose(source, Rendering.WatermarkRenderer.RenderLayer(layout, codes));
                        ImageTests.SavePng(image, Path.Combine(output, name + ".png"));
                        var crops = new[] { new Int32Rect(0, 0, width / 2, height / 2), new Int32Rect(width / 2, 0, width / 2, height / 2),
                            new Int32Rect(0, height / 2, width / 2, height / 2), new Int32Rect(width / 2, height / 2, width / 2, height / 2),
                            new Int32Rect(width / 4, height / 4, width / 2, height / 2) };
                        for (var i = 0; i < crops.Length; i++)
                        {
                            var crop = crops[i];
                            var bounds = new Rect(crop.X, crop.Y, crop.Width, crop.Height);
                            var complete = layout.Labels.Select((label, index) => (label, index)).Where(v => bounds.Contains(v.label.CodeBounds)).ToArray();
                            Assert.NotEmpty(complete);
                            report.AppendLine($"{name},{i + 1},{complete.Length},{codes[complete[0].index]}");
                            ImageTests.SavePng(new CroppedBitmap(image, crop), Path.Combine(output, $"{name}-crop-{i + 1}.png"));
                            var codeBounds = complete[0].label.CodeBounds;
                            var left = Math.Max(0, (int)Math.Floor(codeBounds.Left) - 3);
                            var top = Math.Max(0, (int)Math.Floor(codeBounds.Top) - 3);
                            var right = Math.Min(width, (int)Math.Ceiling(codeBounds.Right) + 3);
                            var bottom = Math.Min(height, (int)Math.Ceiling(codeBounds.Bottom) + 3);
                            ImageTests.SavePng(new CroppedBitmap(image, new Int32Rect(left, top, right - left, bottom - top)),
                                Path.Combine(output, $"{name}-code-{i + 1}.png"));
                        }
                    }
            File.WriteAllText(Path.Combine(output, "coverage.csv"), report.ToString());
            return true;
        });
    }
}
