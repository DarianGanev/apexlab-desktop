using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace ApexLab.App;

public partial class App : System.Windows.Application
{
    private AppHostLifetime? _lifetime;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var localApplicationDataDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var host = AppComposition.CreateHost(localApplicationDataDirectory);
        var lifetime = new AppHostLifetime(
            host,
            services =>
            {
                MainWindow = services.GetRequiredService<MainWindow>();
                MainWindow.Show();
            });
        _lifetime = lifetime;

        try
        {
            lifetime.Start();
        }
        catch
        {
            _lifetime = null;
            throw;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            try
            {
                _lifetime?.Dispose();
            }
            finally
            {
                _lifetime = null;
            }
        }
        finally
        {
            base.OnExit(e);
        }
    }
}
