using System.Windows;

namespace WaterMarker.App;

public partial class ProjectNameWindow : Window
{
    public string ProjectName => NameInput.Text;
    public ProjectNameWindow(string? currentName)
    {
        InitializeComponent();
        Title = currentName is null ? "New project" : "Rename project";
        NameInput.Text = currentName ?? "";
        Loaded += (_, _) => { NameInput.Focus(); NameInput.SelectAll(); };
    }
    private void SaveClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
