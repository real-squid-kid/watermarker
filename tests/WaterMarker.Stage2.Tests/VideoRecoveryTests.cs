using System.Diagnostics;
using System.IO;
using System.Text.Json;
using WaterMarker.Core;
using WaterMarker.Infrastructure;
using Xunit;

namespace WaterMarker.Stage2.Tests;

public sealed class VideoRecoveryTests
{
    [Theory]
    [InlineData("Reserved", false)]
    [InlineData("Writing", false)]
    [InlineData("Kill:Encoding", false)]
    [InlineData("Rendered", false)]
    [InlineData("PublicationStarting", false)]
    [InlineData("Publishing", true)]
    [InlineData("Published", true)]
    public async Task RestartRecoversVideoAfterProcessTermination(string phase, bool ready)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WATERMARKER_VIDEO_TESTS") == "1", "Set WATERMARKER_VIDEO_TESTS=1 for FFmpeg integration tests.");
        using var workspace = new Workspace();
        var token = TestContext.Current.CancellationToken;
        var source = workspace.File("источник.mp4");
        var input = phase == "Kill:Encoding" ? "testsrc2=size=1280x720:rate=25:duration=12" : "testsrc2=size=320x240:rate=10:duration=1";
        await MediaProcess.RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", input,
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-n", source], token);
        var sourceHash = ExportService.FileChecksum(source);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "FaultHost", "WaterMarker.FaultHost.dll"));
        start.ArgumentList.Add(workspace.Root);
        start.ArgumentList.Add(phase);
        start.ArgumentList.Add(source);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            if (phase.StartsWith("Kill:", StringComparison.Ordinal))
            {
                while (!File.Exists(workspace.File("checkpoint.json")))
                {
                    Assert.False(process.HasExited, "Fault host exited before encoding started.");
                    await Task.Delay(25, timeout.Token);
                }
                while (true)
                {
                    try { _ = JsonSerializer.Deserialize<ExportRecord>(await File.ReadAllTextAsync(workspace.File("checkpoint.json"), timeout.Token)); break; }
                    catch (Exception ex) when (ex is JsonException or IOException) { await Task.Delay(25, timeout.Token); }
                }
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync(timeout.Token);
            var stderr = await errors;
            Assert.NotEqual(0, process.ExitCode);
            Assert.True(File.Exists(workspace.File("checkpoint.json")), $"Fault host missed checkpoint: {stderr}");
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(token); } }
        var record = JsonSerializer.Deserialize<ExportRecord>(await File.ReadAllTextAsync(workspace.File("checkpoint.json"), token))!;
        var store = workspace.Store();
        Assert.Equal(0, store.Recover().Pending);
        var restored = store.Find(record.Codes[0])!;
        Assert.Equal(ready ? ExportState.Ready : ExportState.Interrupted, restored.State);
        Assert.False(File.Exists(record.TemporaryPath));
        Assert.Equal(ready, File.Exists(record.TargetPath));
        Assert.Equal(sourceHash, ExportService.FileChecksum(source));
        foreach (var code in record.Codes) Assert.Equal(record.Id, store.Find(code)!.Id);
        if (ready)
        {
            Assert.Equal(restored.Checksum, ExportService.FileChecksum(record.TargetPath));
            Assert.Equal("h264", (await new VideoTools().ProbeAsync(record.TargetPath, token)).Codec);
        }
        Assert.Equal(new RecoveryResult(0, 0, 0), store.Recover());
    }
}
