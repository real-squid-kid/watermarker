using System.IO;
using System.Text.Json;
using WaterMarker.Infrastructure;
using Xunit;

namespace WaterMarker.Stage2.Tests;

public sealed class NativeCaptureTests
{
    [Fact]
    public void GdiCapturesTheActualDesktopWithoutWritingAnUnmarkedImage()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WATERMARKER_NATIVE_CAPTURE") == "1", "Native desktop capture is opt-in; no physical monitor check was requested.");
        DesktopCapture.WaitForComposition();
        var snapshot = DesktopCapture.Capture();
        Assert.NotEmpty(snapshot.Monitors);
        Assert.Equal(snapshot.Monitors.Count, snapshot.ScalePercents.Count);
        Assert.Equal(snapshot.Pixels.Bounds.Width * snapshot.Pixels.Bounds.Height * 4, snapshot.Pixels.Bgra.Length);
        for (var i = 3; i < snapshot.Pixels.Bgra.Length; i += 4) Assert.Equal(255, snapshot.Pixels.Bgra[i]);
        foreach (var monitor in snapshot.Monitors)
        {
            Assert.True(snapshot.Pixels.Bounds.Contains(monitor));
            var area = new Core.PixelRect(monitor.Left + 10, monitor.Top + 10, 100, 100);
            Assert.Equal(40_000, snapshot.Pixels.Crop(area).Bgra.Length);
        }
        var output = Environment.GetEnvironmentVariable("WATERMARKER_STAGE2_ARTIFACTS");
        if (!string.IsNullOrEmpty(output))
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "physical-displays.json"), JsonSerializer.Serialize(new
            {
                Desktop = snapshot.Pixels.Bounds,
                Monitors = snapshot.Monitors.Select((bounds, i) => new { Bounds = bounds, ScalePercent = snapshot.ScalePercents[i] }),
                UnmarkedImageWritten = false
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
