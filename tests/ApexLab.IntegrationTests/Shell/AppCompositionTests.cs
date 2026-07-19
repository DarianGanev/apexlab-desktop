using ApexLab.App;
using ApexLab.App.Shell;
using ApexLab.Application.Configuration;
using ApexLab.Persistence.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ApexLab.IntegrationTests.Shell;

[TestClass]
public sealed class AppCompositionTests
{
    [TestMethod]
    public void Host_registers_validated_local_defaults_and_required_singletons()
    {
        var localApplicationData = NewAbsentPath();

        using var host = AppComposition.CreateHost(localApplicationData);

        var options = host.Services.GetRequiredService<ApexLabOptions>();
        Assert.AreEqual(Path.Combine(localApplicationData, "ApexLab"), options.DataRootPath);
        Assert.AreEqual("127.0.0.1", options.BindAddress);
        Assert.AreEqual(20_777, options.UdpPort);
        Assert.IsEmpty(ApexLabOptionsValidator.Validate(options));
        Assert.AreSame(
            host.Services.GetRequiredService<ShellViewModel>(),
            host.Services.GetRequiredService<ShellViewModel>());
        Assert.IsFalse(Directory.Exists(localApplicationData));
    }

    [TestMethod]
    public async Task Fake_settings_override_is_deterministic_and_host_start_performs_no_io()
    {
        var localApplicationData = NewAbsentPath();
        var fake = new RecordingSettingsStore();
        using var host = AppComposition.CreateHost(
            localApplicationData,
            services =>
            {
                services.RemoveAll<ISettingsStore>();
                services.AddSingleton<ISettingsStore>(fake);
            });

        await host.StartAsync();

        Assert.AreSame(fake, host.Services.GetRequiredService<ISettingsStore>());
        Assert.AreEqual(0, fake.LoadCount);
        Assert.AreEqual(0, fake.SaveCount);
        Assert.IsFalse(Directory.Exists(localApplicationData));
        await host.StopAsync();
    }

    [TestMethod]
    public void Production_descriptors_bind_settings_and_window_without_constructing_a_window()
    {
        var services = new ServiceCollection();
        var defaults = AppComposition.CreateValidatedDefaults(NewAbsentPath());

        AppComposition.AddDesktopServices(services, defaults);

        var settingsDescriptor = services.Single(
            descriptor => descriptor.ServiceType == typeof(ISettingsStore));
        Assert.AreEqual(typeof(JsonSettingsStore), settingsDescriptor.ImplementationType);
        Assert.AreEqual(ServiceLifetime.Singleton, settingsDescriptor.Lifetime);

        var windowDescriptor = services.Single(
            descriptor => descriptor.ServiceType == typeof(MainWindow));
        Assert.AreEqual(typeof(MainWindow), windowDescriptor.ImplementationType);
        Assert.AreEqual(ServiceLifetime.Singleton, windowDescriptor.Lifetime);
    }

    [TestMethod]
    public void Main_window_uses_injected_shell_instead_of_inline_xaml_construction()
    {
        var repositoryRoot = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(repositoryRoot, "src", "ApexLab.App", "MainWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(repositoryRoot, "src", "ApexLab.App", "MainWindow.xaml.cs"));

        Assert.IsFalse(xaml.Contains("<Window.DataContext>", StringComparison.Ordinal));
        StringAssert.Contains(codeBehind, "MainWindow(ShellViewModel viewModel)");
        StringAssert.Contains(codeBehind, "DataContext = viewModel;");
    }

    [TestMethod]
    public void Application_owns_host_and_window_lifecycle_without_startup_uri()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appXaml = File.ReadAllText(Path.Combine(repositoryRoot, "src", "ApexLab.App", "App.xaml"));
        var appCode = File.ReadAllText(Path.Combine(repositoryRoot, "src", "ApexLab.App", "App.xaml.cs"));

        Assert.IsFalse(appXaml.Contains("StartupUri", StringComparison.Ordinal));
        StringAssert.Contains(appCode, "AppComposition.CreateHost");
        StringAssert.Contains(appCode, ".StartAsync()");
        StringAssert.Contains(appCode, "GetRequiredService<MainWindow>()");
        StringAssert.Contains(appCode, ".Show()");
        StringAssert.Contains(appCode, ".StopAsync(");
        StringAssert.Contains(appCode, ".Dispose()");
    }

    private static string NewAbsentPath() => Path.Combine(
        Path.GetTempPath(),
        $"ApexLab-Composition-{Guid.NewGuid():N}");

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ApexLab.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the ApexLab repository root.");
    }

    private sealed class RecordingSettingsStore : ISettingsStore
    {
        public int LoadCount { get; private set; }

        public int SaveCount { get; private set; }

        public Task<ApexLabOptions> LoadAsync(CancellationToken cancellationToken)
        {
            LoadCount++;
            throw new InvalidOperationException("The composition test must not load settings.");
        }

        public Task SaveAsync(ApexLabOptions options, CancellationToken cancellationToken)
        {
            SaveCount++;
            throw new InvalidOperationException("The composition test must not save settings.");
        }

        public void Dispose()
        {
        }
    }
}
