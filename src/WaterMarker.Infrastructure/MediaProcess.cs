using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace WaterMarker.Infrastructure;

public sealed class MediaCommandException(string message, Exception inner) : NotSupportedException(message, inner);

public static class MediaProcess
{
    public static async Task<string> RunAsync(string executable, IEnumerable<string> arguments,
        CancellationToken token = default, Action<string>? onLine = null, byte[]? input = null, bool discardOutput = false)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception ex) { throw new NotSupportedException("Install FFmpeg and FFprobe, or set WATERMARKER_FFMPEG_DIRECTORY to their folder.", ex); }
        using var registration = token.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        });
        var output = new StringBuilder();
        var errors = new StringBuilder();
        var readOutput = discardOutput ? process.StandardOutput.BaseStream.CopyToAsync(Stream.Null)
            : ReadAsync(process.StandardOutput, onLine is null ? output : null, onLine);
        var readErrors = ReadAsync(process.StandardError, errors, null);
        var writeInput = WriteInputAsync();
        await Task.WhenAll(readOutput, readErrors, writeInput, process.WaitForExitAsync()).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new MediaCommandException("This video could not be processed. Check that it is complete and its codecs are supported.",
            new InvalidOperationException(errors.ToString().Trim()));
        return output.ToString();

        async Task WriteInputAsync()
        {
            if (input is null) return;
            try { await process.StandardInput.BaseStream.WriteAsync(input, token).ConfigureAwait(false); }
            catch (IOException) { /* The exit code and stderr explain an early decoder/encoder failure. */ }
            finally { process.StandardInput.Close(); }
        }
    }

    private static async Task ReadAsync(StreamReader reader, StringBuilder? text, Action<string>? onLine)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            // Probe JSON is small; bound diagnostics rather than accumulating a long encode log.
            if (text is not null && text.Length < 1_048_576) text.AppendLine(line);
            onLine?.Invoke(line);
        }
    }
}
