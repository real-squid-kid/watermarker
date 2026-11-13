using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using WaterMarker.Infrastructure;

namespace WaterMarker.App;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private bool ownsMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var dataDirectory = e.Args.Length == 2 && e.Args[0] == "--data-directory"
                ? System.IO.Path.GetFullPath(e.Args[1])
                : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaterMarker");
            var databasePath = System.IO.Path.Combine(dataDirectory, "watermarker.db");
            // Startup recovery must not interrupt an export in another instance using this database.
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(databasePath.ToUpperInvariant())));
            instanceMutex = new Mutex(false, @"Local\WaterMarker-" + identity);
            try { ownsMutex = instanceMutex.WaitOne(0); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex)
            {
                MessageBox.Show("WaterMarker is already running with this database.", "WaterMarker", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
            var store = await Task.Run(() =>
            {
                var journal = new JournalStore(databasePath);
                var recovery = journal.Recover();
                return (Journal: journal, Recovery: recovery);
            });
            var viewModel = new MainViewModel(store.Journal, new DesktopDialogs(dataDirectory));
            viewModel.ShowRecoveryResult(store.Recovery);
            MainWindow = new MainWindow(viewModel);
            MainWindow.Show();
            await PrepareVideoAsync(MainWindow);
        }
        catch (Exception)
        {
            MessageBox.Show("WaterMarker could not open its local database. Check access to Local AppData and available disk space.", "WaterMarker", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static async Task PrepareVideoAsync(Window owner)
    {
        var tools = new VideoTools();
        try { await tools.EnsureAvailableAsync(); return; }
        catch (NotSupportedException) { }
        if (!owner.IsVisible) return;
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WATERMARKER_FFMPEG_DIRECTORY")))
        {
            MessageBox.Show(owner, "The FFmpeg folder configured in WATERMARKER_FFMPEG_DIRECTORY is missing or incompatible. Check the folder and restart WaterMarker. Images and Screenshot are still available.",
                "Video setup", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var choice = MessageBox.Show(owner,
            "Video needs FFmpeg and FFprobe with libx264. Install the separate Gyan.FFmpeg package using Windows Package Manager now?\n\nAn installer window will open and may ask you to accept its terms. Images and Screenshot work without it.",
            "Video setup", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (choice != MessageBoxResult.Yes) return;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("winget.exe", "install --id Gyan.FFmpeg --exact --source winget --scope user")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            }) ?? throw new InvalidOperationException("Could not start Windows Package Manager.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException("The FFmpeg installation did not complete.");
            await tools.EnsureAvailableAsync();
            if (owner.IsVisible) MessageBox.Show(owner, "FFmpeg is ready. You can now open video files.", "Video setup", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            if (owner.IsVisible) MessageBox.Show(owner,
                "FFmpeg could not be installed or verified. Check Windows Package Manager and your internet connection, then restart WaterMarker. You can also install FFmpeg manually; see README. Images and Screenshot remain available.",
                "Video setup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsMutex) instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
