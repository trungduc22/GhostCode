using GhostCode.App.Diagnostics;
using GhostCode.App.Interop;
using GhostCode.App.ViewModels;
using GhostCode.Core.Ai;
using GhostCode.Core.Archiver;
using GhostCode.Core.Browser;
using GhostCode.Core.Cloud;
using GhostCode.Core.Execution;
using GhostCode.Core.Models;
using GhostCode.Core.Monitoring;
using GhostCode.Core.Notifications;
using GhostCode.Core.Storage;
using GhostCode.Core.Toolchains;
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
        var customConfigEnv = Environment.GetEnvironmentVariable("GHOSTCODE_CONFIG_PATH");
        var configPath = string.IsNullOrWhiteSpace(customConfigEnv)
            ? GhostCodeConfig.DefaultConfigPath()
            : customConfigEnv;

        var config = GhostCodeConfig.LoadOrDefault(configPath);
        if (string.IsNullOrWhiteSpace(customConfigEnv))
        {
            config.PairingFilePath ??= FindExtensionPairingPath();
        }
        config.Save(configPath);

        var appDataDir = Path.GetDirectoryName(configPath)!;
        var credPath = Path.Combine(appDataDir, "credentials.dpapi");

        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSingleton(_ => new SqliteArchiveStore(config.DatabasePath));
        services.AddSingleton(sp => new LosslessArchiver(config, sp.GetRequiredService<SqliteArchiveStore>()));
        services.AddSingleton(_ => new DpapiCredentialStore(credPath));
        services.AddSingleton<HttpClient>(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(45) });
        services.AddSingleton<INativeNotificationPresenter, WindowsAppNotificationPresenter>();
        services.AddSingleton(sp => new NotificationCoordinator(
            config,
            sp.GetRequiredService<SqliteArchiveStore>(),
            sp.GetRequiredService<INativeNotificationPresenter>()));
        services.AddSingleton(sp => new GoogleDriveService(
            config,
            sp.GetRequiredService<SqliteArchiveStore>(),
            sp.GetRequiredService<DpapiCredentialStore>(),
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<NotificationCoordinator>()));
        services.AddSingleton(sp => new OneDriveService(
            config,
            sp.GetRequiredService<SqliteArchiveStore>(),
            new DpapiCredentialStore(appDataDir, "onedrive_oauth_tokens.dpapi"),
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<NotificationCoordinator>()));
        services.AddSingleton(sp => new CloudMigrationService(
            config,
            sp.GetRequiredService<SqliteArchiveStore>(),
            sp.GetRequiredService<LosslessArchiver>(),
            sp.GetRequiredService<NotificationCoordinator>()));
        services.AddSingleton(_ => new BrowserIntegrationService());
        services.AddSingleton(sp => new AuthenticatedBrowserBridge(
            config,
            sp.GetRequiredService<BrowserIntegrationService>(),
            sp.GetRequiredService<SqliteArchiveStore>(),
            sp.GetRequiredService<NotificationCoordinator>()));
        services.AddSingleton(_ => new ToolchainDiscoveryService());
        services.AddSingleton(sp => new IdeLauncherService(
            config,
            sp.GetRequiredService<ToolchainDiscoveryService>(),
            sp.GetRequiredService<LosslessArchiver>()));
        services.AddSingleton(sp => new CodeExecutionService(
            config,
            sp.GetRequiredService<SqliteArchiveStore>(),
            sp.GetRequiredService<LosslessArchiver>(),
            sp.GetRequiredService<ToolchainDiscoveryService>()));
        services.AddSingleton(sp => new SampleTestRunnerService(
            config,
            sp.GetRequiredService<SqliteArchiveStore>(),
            sp.GetRequiredService<CodeExecutionService>()));
        services.AddSingleton(_ => new CodingTimeTracker(config));
        services.AddSingleton(sp => new CopilotAssistanceService(config, sp.GetRequiredService<HttpClient>()));
        services.AddSingleton(sp => new MultiDirectoryMonitor(
            config,
            sp.GetRequiredService<SqliteArchiveStore>(),
            sp.GetRequiredService<LosslessArchiver>(),
            () => sp.GetRequiredService<AuthenticatedBrowserBridge>().GetActiveContext()));
        services.AddSingleton(sp => new BackupHealthAndRecoveryService(
            config,
            sp.GetRequiredService<SqliteArchiveStore>(),
            sp.GetRequiredService<MultiDirectoryMonitor>(),
            sp.GetRequiredService<NotificationCoordinator>()));
        services.AddSingleton(sp => new MainViewModel(
            config,
            configPath,
            sp.GetRequiredService<SqliteArchiveStore>(),
            sp.GetRequiredService<LosslessArchiver>(),
            sp.GetRequiredService<MultiDirectoryMonitor>(),
            sp.GetRequiredService<AuthenticatedBrowserBridge>(),
            sp.GetRequiredService<GoogleDriveService>(),
            sp.GetRequiredService<DpapiCredentialStore>(),
            sp.GetRequiredService<BrowserIntegrationService>(),
            sp.GetRequiredService<ToolchainDiscoveryService>(),
            sp.GetRequiredService<CodeExecutionService>(),
            sp.GetRequiredService<BackupHealthAndRecoveryService>(),
            sp.GetRequiredService<IdeLauncherService>(),
            sp.GetRequiredService<NotificationCoordinator>(),
            sp.GetRequiredService<OneDriveService>(),
            sp.GetRequiredService<CloudMigrationService>(),
            sp.GetRequiredService<SampleTestRunnerService>(),
            sp.GetRequiredService<CodingTimeTracker>(),
            sp.GetRequiredService<CopilotAssistanceService>()));
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

            var archiver = _serviceProvider.GetRequiredService<LosslessArchiver>();
            await archiver.ReconcileAfterCrashAsync();

            var bridge = _serviceProvider.GetRequiredService<AuthenticatedBrowserBridge>();
            bridge.Start();

            var monitor = _serviceProvider.GetRequiredService<MultiDirectoryMonitor>();
            monitor.Start();

            var drive = _serviceProvider.GetRequiredService<GoogleDriveService>();
            drive.StartBackgroundSync();

            var oneDrive = _serviceProvider.GetRequiredService<OneDriveService>();
            oneDrive.StartBackgroundSync();

            var vm = _serviceProvider.GetRequiredService<MainViewModel>();
            await vm.InitializeAsync();

            _ = Task.Run(async () =>
            {
                try { await monitor.ReconcileOnceAsync(); } catch { }
            });

            var selfTestReportPath = Environment.GetEnvironmentVariable("GHOSTCODE_SELF_TEST_REPORT");
            if (!string.IsNullOrWhiteSpace(selfTestReportPath))
            {
                await UiSelfTestRunner.RunAndWriteReportAsync(
                    selfTestReportPath,
                    _window,
                    vm,
                    config,
                    configPath,
                    _serviceProvider.GetRequiredService<DpapiCredentialStore>(),
                    Path.GetDirectoryName(configPath)!);
                await ShutdownAndExitAsync();
            }
            else
            {
                _ = _window.PromptBrowserOnboardingIfNeededAsync();
            }
        }
        catch (Exception ex)
        {
            var vm = _serviceProvider.GetRequiredService<MainViewModel>();
            vm.ShowInfoBar("Startup Warning", ex.Message, InfoBarSeverity.Error);
        }
    }

    private static string? FindExtensionPairingPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "browser-extension");
            if (Directory.Exists(candidate))
                return Path.Combine(candidate, "pairing.generated.json");
            dir = dir.Parent;
        }
        return null;
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
                await _serviceProvider.GetRequiredService<OneDriveService>().StopAsync();
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
