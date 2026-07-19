using ApexLab.App.Shell;
using ApexLab.Application.Configuration;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ApexLab.App;

public static class AppComposition
{
    public static IHost CreateHost(
        string localApplicationDataDirectory,
        Action<IServiceCollection>? configureOverrides = null)
    {
        var defaults = CreateValidatedDefaults(localApplicationDataDirectory);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
        });

        AddDesktopServices(builder.Services, defaults);
        configureOverrides?.Invoke(builder.Services);
        return builder.Build();
    }

    public static ApexLabOptions CreateValidatedDefaults(string localApplicationDataDirectory)
    {
        var paths = ApplicationPaths.FromLocalApplicationData(localApplicationDataDirectory);
        var defaults = new ApexLabOptions(paths.RootDirectory);
        var failures = ApexLabOptionsValidator.Validate(defaults);
        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                $"ApexLab default settings are invalid: {string.Join(" ", failures.Select(failure => failure.Message))}");
        }

        return defaults;
    }

    public static void AddDesktopServices(
        IServiceCollection services,
        ApexLabOptions defaults)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(defaults);

        var failures = ApexLabOptionsValidator.Validate(defaults);
        if (failures.Count != 0)
        {
            throw new ArgumentException("Desktop services require valid ApexLab settings.", nameof(defaults));
        }

        services.AddSingleton(defaults);
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MainWindow>();
    }
}
