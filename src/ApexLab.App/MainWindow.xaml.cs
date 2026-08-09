using System.Windows;
using ApexLab.App.Shell;

namespace ApexLab.App;

public partial class MainWindow : Window
{
    public MainWindow(ShellViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;
    }
}
