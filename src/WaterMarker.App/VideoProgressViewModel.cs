using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using WaterMarker.Core;

namespace WaterMarker.App;

public static class ProcessingPrompts
{
    public static IReadOnlyList<string> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WaterMarker.ProcessingPrompts")
            ?? throw new InvalidOperationException("Processing prompts are missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().TrimEnd('.', '…') + "…").Where(line => line.Length > 1).Distinct().ToArray();
    }
}

public sealed class VideoProgressViewModel : INotifyPropertyChanged
{
    private readonly TimeProvider clock;
    private readonly long started;
    private readonly IReadOnlyList<string> prompts;
    private readonly Random random;
    private TimeSpan promptChanged;
    private int promptIndex;
    private TimeSpan processed;
    private bool completed, publishing;
    public event PropertyChangedEventHandler? PropertyChanged;
    public TimeSpan Duration { get; }
    public TimeSpan Elapsed { get; private set; }
    public double Percent => completed ? 100 : Math.Min(99.9, new VideoProgress(processed, Duration).Percent);
    public string PercentText => $"{(completed ? 100 : Math.Floor(Percent)):0}%";
    public string ElapsedText => Format(Elapsed);
    public string ProcessedText => $"{Format(processed)} / {Format(Duration)} of video";
    public string Prompt => prompts[promptIndex];
    public string Phase => publishing ? "Saving copy" : "Processing video";
    public ICommand CancelCommand { get; }

    public VideoProgressViewModel(TimeSpan duration, ICommand cancelCommand, TimeProvider? clock = null,
        IReadOnlyList<string>? prompts = null, Random? random = null)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        Duration = duration;
        CancelCommand = cancelCommand;
        this.clock = clock ?? TimeProvider.System;
        this.prompts = prompts ?? ProcessingPrompts.Load();
        if (this.prompts.Count < 2) throw new ArgumentException("At least two prompts are required.", nameof(prompts));
        this.random = random ?? Random.Shared;
        promptIndex = this.random.Next(this.prompts.Count);
        started = this.clock.GetTimestamp();
    }

    public void Tick()
    {
        if (completed) return;
        Elapsed = clock.GetElapsedTime(started);
        Changed(nameof(ElapsedText));
        if (Elapsed - promptChanged < TimeSpan.FromSeconds(7)) return;
        promptIndex = (promptIndex + 1 + random.Next(prompts.Count - 1)) % prompts.Count;
        promptChanged = Elapsed;
        Changed(nameof(Prompt));
    }

    public void Update(VideoProgress value)
    {
        if (completed) return;
        processed = TimeSpan.FromTicks(Math.Max(processed.Ticks, Math.Clamp(value.Processed.Ticks, 0, Duration.Ticks)));
        Changed(nameof(Percent)); Changed(nameof(PercentText)); Changed(nameof(ProcessedText));
    }

    public void Publishing() { publishing = true; Changed(nameof(Phase)); }
    public void Complete()
    {
        Tick();
        processed = Duration;
        completed = true;
        Changed(nameof(Percent)); Changed(nameof(PercentText)); Changed(nameof(ProcessedText));
    }
    public static string Format(TimeSpan time) => $"{(long)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
