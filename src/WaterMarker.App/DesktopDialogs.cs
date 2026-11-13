using System.Windows;
using Microsoft.Win32;
using WaterMarker.Core;
using WaterMarker.Rendering;

namespace WaterMarker.App;

public interface IDesktopDialogs
{
    string? OpenImage();
    string? SaveImage(ImageDocument image);
    string? ProjectName(string? currentName);
    Task<ImageDocument?> CaptureImageAsync() => Task.FromResult<ImageDocument?>(null);
    string? SaveVideo(VideoInfo video) => null;
    Task RunVideoExportAsync(VideoProgressViewModel progress, Func<Task> operation) => operation();
}

public sealed class DesktopDialogs : IDesktopDialogs
{
    private readonly SaveLocations locations;
    public DesktopDialogs(string? dataDirectory = null) => locations = new(dataDirectory ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaterMarker"));

    public async Task RunVideoExportAsync(VideoProgressViewModel progress, Func<Task> operation)
    {
        // Let the command register its running task before entering WPF's nested modal loop.
        await Task.Yield();
        await new VideoProgressWindow(progress) { Owner = Application.Current.MainWindow }.RunAsync(operation);
    }

    public Task<ImageDocument?> CaptureImageAsync() => new CaptureCoordinator(Application.Current.MainWindow).CaptureAsync();
    public string? OpenImage()
    {
        var picker = new OpenFileDialog { Title = "Open media", Filter = "Images and video|*.png;*.jpg;*.jpeg;*.mp4;*.mov;*.mkv|Images|*.png;*.jpg;*.jpeg|Video|*.mp4;*.mov;*.mkv", CheckFileExists = true };
        return picker.ShowDialog(Application.Current.MainWindow) == true ? picker.FileName : null;
    }

    public string? SaveImage(ImageDocument image)
    {
        var picker = new SaveFileDialog
        {
            Title = "Save watermarked copy",
            InitialDirectory = locations.InitialDirectory(image.SourcePath),
            RestoreDirectory = true,
            AddExtension = true,
            OverwritePrompt = false,
            FileName = System.IO.Path.GetFileNameWithoutExtension(image.SourceName) + "_marked",
            Filter = image.SourceType == "JPEG" ? "JPEG image (*.jpg)|*.jpg|PNG image (*.png)|*.png" : "PNG image (*.png)|*.png",
            DefaultExt = image.SourceType == "JPEG" ? ".jpg" : ".png"
        };
        return SelectSave(picker);
    }

    public string? SaveVideo(VideoInfo video) => SelectSave(new SaveFileDialog
    {
        Title = "Save watermarked video",
        InitialDirectory = locations.InitialDirectory(video.SourcePath),
        RestoreDirectory = true,
        AddExtension = true,
        OverwritePrompt = false,
        FileName = System.IO.Path.GetFileNameWithoutExtension(video.SourceName) + "_marked",
        Filter = video.RequiresMkv ? "MKV video (*.mkv)|*.mkv" : !video.SupportsMkv ? "MP4 video (*.mp4)|*.mp4" : "MP4 video (*.mp4)|*.mp4|MKV video (*.mkv)|*.mkv",
        DefaultExt = video.RequiresMkv ? ".mkv" : ".mp4"
    });

    private string? SelectSave(SaveFileDialog picker)
    {
        while (picker.ShowDialog(Application.Current.MainWindow) == true)
        {
            if (!System.IO.File.Exists(picker.FileName) && !System.IO.Directory.Exists(picker.FileName))
            {
                locations.Remember(picker.FileName);
                return picker.FileName;
            }
            MessageBox.Show(Application.Current.MainWindow, "This name is already in use. Choose another file name.", "WaterMarker", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        return null;
    }

    public string? ProjectName(string? currentName)
    {
        var dialog = new ProjectNameWindow(currentName) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true ? dialog.ProjectName : null;
    }
}
