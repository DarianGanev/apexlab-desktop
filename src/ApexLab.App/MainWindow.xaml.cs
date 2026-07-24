using System.Windows;
using ApexLab.App.Capture;
using ApexLab.App.Shell;

namespace ApexLab.App;

public partial class MainWindow : Window
{
    public MainWindow(
        ShellViewModel viewModel,
        CaptureViewModel capture)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(capture);
        viewModel.AttachCapture(capture);

        InitializeComponent();
        DataContext = viewModel;
    }
}
