using System.Windows;
using Microsoft.Win32;
using WaterMarker.Rendering;

namespace WaterMarker.App;

public interface IDesktopDialogs
{
    string? OpenImage();
    string? SaveImage(ImageDocument image);
    string? ProjectName(string? currentName);
}

public sealed class DesktopDialogs : IDesktopDialogs
{
    public string? OpenImage()
    {
        var picker = new OpenFileDialog { Title = "Open image", Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg", CheckFileExists = true };
        return picker.ShowDialog(Application.Current.MainWindow) == true ? picker.FileName : null;
    }

    public string? SaveImage(ImageDocument image)
    {
        var picker = new SaveFileDialog
        {
            Title = "Save watermarked copy",
            AddExtension = true,
            OverwritePrompt = false,
            FileName = System.IO.Path.GetFileNameWithoutExtension(image.SourceName) + "_marked",
            Filter = image.SourceType == "PNG" ? "PNG image (*.png)|*.png" : "JPEG image (*.jpg)|*.jpg|PNG image (*.png)|*.png",
            DefaultExt = image.SourceType == "PNG" ? ".png" : ".jpg"
        };
        while (picker.ShowDialog(Application.Current.MainWindow) == true)
        {
            if (!System.IO.File.Exists(picker.FileName) && !System.IO.Directory.Exists(picker.FileName)) return picker.FileName;
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
