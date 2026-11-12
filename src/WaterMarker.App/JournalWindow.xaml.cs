using System.Windows;

namespace WaterMarker.App;

public partial class JournalWindow : Window
{
    public JournalWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
