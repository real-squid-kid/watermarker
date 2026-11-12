using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WaterMarker.Infrastructure;

namespace WaterMarker.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    private bool waitingToClose, mayClose, sizeFixed;
    private JournalWindow? journal;
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        this.viewModel = viewModel;
        Closing += OnClosing;
        Closed += (_, _) => viewModel.ReleasePreview();
        Loaded += (_, _) => FitWorkArea();
    }

    private void FitWorkArea()
    {
        if (sizeFixed) return;
        var area = DesktopCapture.WorkArea(new WindowInteropHelper(this).Handle);
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = area.Width / dpi.DpiScaleX;
        var height = area.Height / dpi.DpiScaleY;
        // Fit once at startup. Importing media never changes the window or the desk layout.
        Width = Math.Min(1120, width);
        Height = Math.Min(680, height);
        MinWidth = MaxWidth = Width;
        MinHeight = MaxHeight = Height;
        sizeFixed = true;
    }

    private void OpenJournalClick(object sender, RoutedEventArgs e) => ShowJournal();

    public void ShowJournal()
    {
        if (journal is not null)
        {
            if (journal.WindowState == WindowState.Minimized) journal.WindowState = WindowState.Normal;
            journal.Activate();
            return;
        }
        viewModel.RefreshJournalCommand.Execute(null);
        journal = new JournalWindow(viewModel) { Owner = this };
        journal.Closed += (_, _) => journal = null;
        journal.Show();
    }

    private static string[] DroppedPaths(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop, false) && e.Data.GetData(DataFormats.FileDrop, false) is string[] paths ? paths : [];

    private void TelevisionDragOver(object sender, DragEventArgs e)
    {
        var canDrop = !viewModel.IsBusy && MainViewModel.CanDropFiles(DroppedPaths(e));
        e.Effects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        Television.IsDropTarget = canDrop;
        e.Handled = true;
    }

    private void TelevisionDragLeave(object sender, DragEventArgs e) => Television.IsDropTarget = false;

    private async void TelevisionDrop(object sender, DragEventArgs e)
    {
        Television.IsDropTarget = false;
        e.Handled = true;
        await viewModel.OpenDroppedFilesAsync(DroppedPaths(e));
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (mayClose || !viewModel.IsExporting) return;
        e.Cancel = true;
        if (waitingToClose) return;
        waitingToClose = true;
        await viewModel.StopForCloseAsync();
        mayClose = true;
        Close();
    }
}
