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
                journal.Recover();
                return journal;
            });
            MainWindow = new MainWindow(new MainViewModel(store, new DesktopDialogs()));
            MainWindow.Show();
        }
        catch (Exception)
        {
            MessageBox.Show("WaterMarker could not open its local database. Check access to Local AppData and available disk space.", "WaterMarker", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsMutex) instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
