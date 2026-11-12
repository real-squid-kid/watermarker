using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;

namespace WaterMarker.App;

public partial class VideoProgressWindow : Window
{
    private readonly VideoProgressViewModel progress;
    private bool finished;

    public VideoProgressWindow(VideoProgressViewModel progress)
    {
        InitializeComponent();
        this.progress = progress;
        DataContext = progress;
    }

    public Task RunAsync(Func<Task> operation)
    {
        Exception? failure = null;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background,
            (_, _) => progress.Tick(), Dispatcher);
        Loaded += Start;
        Closing += OnClosing;
        try { ShowDialog(); }
        finally { timer.Stop(); Loaded -= Start; Closing -= OnClosing; }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return Task.CompletedTask;

        async void Start(object sender, RoutedEventArgs e)
        {
            try { await operation(); }
            catch (Exception ex) { failure = ex; }
            finally { finished = true; Close(); }
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (finished) return;
        e.Cancel = true;
        if (progress.CancelCommand.CanExecute(null)) progress.CancelCommand.Execute(null);
    }
}
