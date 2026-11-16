using GhostCode.App.ViewModels;
using GhostCode.Core.Archiver;
using GhostCode.Core.Browser;
using GhostCode.Core.Cloud;
using GhostCode.Core.Models;
using GhostCode.Core.Monitoring;
using GhostCode.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GhostCode.App;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private MainWindow? _window;
    private bool _shuttingDown;

    public App()
    {
        InitializeComponent();
    }

    public static IServiceProvider Services { get; private set; } = null!;
    public static IntPtr MainWindowHandle { get; private set; }

    internal static void SetMainWindowHandle(IntPtr hwnd) => MainWindowHandle = hwnd;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var configPath = Environment.GetEnvironmentVariable("GHOSTCODE_CONFIG_PATH");
        if (string.IsNullOrWhiteSpace(configPath))
            configPath = GhostCodeConfig.DefaultConfigPath();

        var config = GhostCodeConfig.LoadOrDefault(configPath);
        config.Save(configPath);

        var credPath = Path.Combine(Path.GetDirectoryName(configPath)!, "credentials.dpapi");

        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSingleton(_ => new SqliteArchiveStore(config.DatabasePath));
        services.AddSingleton(sp => new LosslessArchiver(config, sp.GetRequiredService<SqliteArchiveStore>()));
        services.AddSingleton(_ => new DpapiCredentialStore(credPath));
        services.AddSingleton<HttpClient>(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(45) });
        services.AddSingleton(sp => new GoogleDriveService(
            config,
            sp.GetRequiredService<SqliteArchiveStore>(),
            sp.GetRequiredService<DpapiCredentialStore>(),
            sp.GetRequiredService<HttpClient>()));
        services.AddSingleton(_ => new AuthenticatedBrowserBridge(config));
        services.AddSingleton(sp => new MultiDirectoryMonitor(
            config,
            sp.GetRequiredService<SqliteArchiveStore>(),
            sp.GetRequiredService<LosslessArchiver>(),
            () => sp.GetRequiredService<AuthenticatedBrowserBridge>().GetActiveContext()));
        services.AddSingleton(sp => new MainViewModel(
            config,
            configPath,
            sp.GetRequiredService<SqliteArchiveStore>(),
            sp.GetRequiredService<LosslessArchiver>(),
            sp.GetRequiredService<MultiDirectoryMonitor>(),
            sp.GetRequiredService<AuthenticatedBrowserBridge>(),
            sp.GetRequiredService<GoogleDriveService>(),
            sp.GetRequiredService<DpapiCredentialStore>()));
        services.AddSingleton<MainWindow>();

        _serviceProvider = services.BuildServiceProvider();
        Services = _serviceProvider;

        _window = _serviceProvider.GetRequiredService<MainWindow>();
        _window.AttachTray(() => _ = ShutdownAndExitAsync());
        _window.Activate();

        try
        {
            var store = _serviceProvider.GetRequiredService<SqliteArchiveStore>();
            await store.InitializeAsync();

            var bridge = _serviceProvider.GetRequiredService<AuthenticatedBrowserBridge>();
            bridge.Start();

            var monitor = _serviceProvider.GetRequiredService<MultiDirectoryMonitor>();
            monitor.Start();
            _ = Task.Run(async () =>
            {
                try { await monitor.ReconcileOnceAsync(); } catch { }
            });

            var drive = _serviceProvider.GetRequiredService<GoogleDriveService>();
            drive.StartBackgroundSync();

            var vm = _serviceProvider.GetRequiredService<MainViewModel>();
            await vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            var vm = _serviceProvider.GetRequiredService<MainViewModel>();
            vm.ShowInfoBar("Startup Warning", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task ShutdownAndExitAsync()
    {
        if (_shuttingDown)
            return;
        _shuttingDown = true;

        try
        {
            if (_serviceProvider is not null)
            {
                await _serviceProvider.GetRequiredService<MultiDirectoryMonitor>().StopAsync();
                await _serviceProvider.GetRequiredService<AuthenticatedBrowserBridge>().StopAsync();
                await _serviceProvider.GetRequiredService<GoogleDriveService>().StopAsync();
                _serviceProvider.GetRequiredService<MainViewModel>().Dispose();
                _window?.DisposeTray();
                await _serviceProvider.DisposeAsync();
            }
        }
        catch
        {
            // Ignore shutdown errors
        }
        finally
        {
            Exit();
        }
    }
}
