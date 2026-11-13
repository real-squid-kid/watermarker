using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WaterMarker.App;
using WaterMarker.Core;
using WaterMarker.Infrastructure;
using WaterMarker.Rendering;

namespace WaterMarker.FaultHost;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length is not (2 or 3)) return 64;
        var directory = Path.GetFullPath(args[0]);
        var kill = args[1].StartsWith("Kill:", StringComparison.Ordinal);
        var phase = kill ? args[1][5..] : args[1];
        Directory.CreateDirectory(directory);
        void Stop(ExportRecord record)
        {
            File.WriteAllText(Path.Combine(directory, "checkpoint.json"), JsonSerializer.Serialize(record));
            if (kill) Thread.Sleep(Timeout.Infinite);
            // No unwinding: emulate an application that disappears before normal cleanup.
            Environment.Exit(86);
        }
        var store = new JournalStore(Path.Combine(directory, "journal.db"));
        ExportRecord? reserved = null;
        IExportMedia source;
        if (args.Length == 3)
        {
            var tools = new VideoTools();
            var video = tools.ProbeAsync(args[2]).GetAwaiter().GetResult();
            var layout = WatermarkRenderer.Calculate(video.Width, video.Height, null);
            source = new PreparedVideo(video, layout, false, tools, new InlineProgress<VideoProgress>(_ =>
            {
                if (phase == "Encoding") Stop(reserved!);
            }));
        }
        else
        {
            var bytes = new byte[640 * 480 * 4];
            for (var i = 0; i < bytes.Length; i += 4) { bytes[i] = 120; bytes[i + 1] = 150; bytes[i + 2] = 180; bytes[i + 3] = 255; }
            var bitmap = BitmapSource.Create(640, 480, 96, 96, PixelFormats.Bgra32, null, bytes, 640 * 4);
            bitmap.Freeze();
            source = new PreparedImage(new("source.png", "PNG", bitmap), WatermarkRenderer.Calculate(640, 480, null), false);
        }
        var media = new FaultMedia(source, path =>
        {
            if (phase != "Writing") return;
            using var stream = new FileStream(path, FileMode.CreateNew);
            stream.Write("partial image"u8);
            stream.Flush(true);
            Stop(reserved!);
        });
        new ExportService(store, (point, record) =>
        {
            reserved = record;
            if (point.ToString() == phase) Stop(record);
        }).Export(new(null, "Test recipient", "Email", Path.Combine(directory, args.Length == 3 ? "copy.mp4" : "copy.png")), media);
        return 0;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class FaultMedia(IExportMedia image, Action<string> writing) : IExportMedia
    {
        public string SourceName => image.SourceName;
        public string SourceType => image.SourceType;
        public int CodeCount => image.CodeCount;
        public void RenderTo(string path, IReadOnlyList<string> codes, CancellationToken cancellationToken)
        {
            writing(path);
            image.RenderTo(path, codes, cancellationToken);
        }
        public void ValidateOutput(string path) => image.ValidateOutput(path);
        public void ValidateOutput(string path, CancellationToken cancellationToken) => image.ValidateOutput(path, cancellationToken);
    }
}
