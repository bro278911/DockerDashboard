using DockerDashboard.Services;
using DockerDashboard.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DockerDashboard;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<UpdateService>();
        services.AddSingleton<DockerCliService>();
        services.AddSingleton<IDockerCliService>(sp => sp.GetRequiredService<DockerCliService>());
        services.AddSingleton<IGitService, GitService>();
        services.AddSingleton<ComposeFileScanner>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ContainerMonitorService>();
        services.AddSingleton<WatchRebuildService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
