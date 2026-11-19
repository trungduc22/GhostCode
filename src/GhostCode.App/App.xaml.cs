using GhostCode.App.Diagnostics;
using GhostCode.App.Interop;
using GhostCode.App.ViewModels;
using GhostCode.Core.Ai;
using GhostCode.Core.Archiver;
using GhostCode.Core.Browser;
using GhostCode.Core.Cloud;
using GhostCode.Core.Execution;
using GhostCode.Core.Interop;
using GhostCode.Core.Models;
using GhostCode.Core.Monitoring;
using GhostCode.Core.Notifications;
using GhostCode.Core.Storage;
using GhostCode.Core.Toolchains;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NativeShellKit.Abstractions.Identity;
using NativeShellKit.Abstractions.Lifecycle;
using NativeShellKit.Abstractions.Threading;
using NativeShellKit.Abstractions.Tray;
using NativeShellKit.Diagnostics;
using NativeShellKit.Hosting.Hosting;
using NativeShellKit.Tray;
using NativeShellKit.WinUI.Threading;
using HostRegistryBridge = NativeShellKit.Hosting.DependencyInjection.MicrosoftDependencyInjectionExtensions;

namespace GhostCode.App;

public partial class App : Application
{
    private IAppHost? _host;
    private IServiceProvider? _serviceProvider;
    private MainWindow? _window;
    private IDisposable? _shutdownSignalRegistration;
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

        var initialTheme = config.Theme switch
        {
            ThemePreference.Light => ElementTheme.Light,
            ThemePreference.Dark => ElementTheme.Dark,
            _ => Current.RequestedTheme == ApplicationTheme.Dark ? ElementTheme.Dark : ElementTheme.Light
        };
        GhostCode.App.Theming.GhostThemePalette.Apply(initialTheme, config.AccentPreset, config.CustomAccentHex);

        var appDataDir = Path.GetDirectoryName(configPath)!;
        var credPath = Path.Combine(appDataDir, "credentials.dpapi");
        var selfTestReportPath = Environment.GetEnvironmentVariable("GHOSTCODE_SELF_TEST_REPORT");
        bool isSelfTest = !string.IsNullOrWhiteSpace(selfTestReportPath);

        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSingleton(_ => new SqliteArchiveStore(config.DatabasePath));
        services.AddSingleton(sp => new LosslessArchiver(config, sp.GetRequiredService<SqliteArchiveStore>()));
        services.AddSingleton(_ => new DpapiCredentialStore(credPath));
        services.AddSingleton<HttpClient>(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(45) });
        if (!isSelfTest)
            services.AddSingleton<INativeNotificationPresenter, WindowsAppNotificationPresenter>();
        services.AddSingleton(sp => new NotificationCoordinator(
            config,
            sp.GetRequiredService<SqliteArchiveStore>(),
            sp.GetService<INativeNotificationPresenter>()));
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

        var identity = new AppIdentity("ghostcode", "GhostCode", "trungduc22",
            typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0", appDataDir);
        var launchArguments = Environment.GetCommandLineArgs().Skip(1).ToList();
        if (isSelfTest)
            launchArguments.Add("--self-test");
        var uiDispatcher = new UiDispatcherHelper(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        _host = NativeShellHostBuilder.Create(identity, launchArguments.ToArray())
            .ConfigureServices(registry =>
            {
                HostRegistryBridge.PopulateFrom(registry, services);
                registry.AddSecurityAndDiagnostics();
                registry.AddSingleton<IUiDispatcher>(uiDispatcher);
            })
            .AddNativeTray()
            .AddModule(new HiberDeskPeerModule(string.IsNullOrWhiteSpace(customConfigEnv) ? null : appDataDir,
                (provider, ct) => OpenArchiveForPeerAsync(provider.GetRequiredService<IUiDispatcher>(), ct)))
            .Build();
        await _host.StartAsync();
        _serviceProvider = _host.Services;
        Services = _serviceProvider;

        _window = _serviceProvider.GetRequiredService<MainWindow>();
        _window.AttachTray(_serviceProvider.GetRequiredService<ITrayService>(), () => _ = ShutdownAndExitAsync());
        var dispatcherQueue = _window.DispatcherQueue;
        _shutdownSignalRegistration = ProcessShutdownSignal.RegisterListener(() =>
        {
            try
            {
                if (!dispatcherQueue.TryEnqueue(() => _ = ShutdownAndExitAsync()))
                    _ = ShutdownAndExitAsync();
            }
            catch
            {
                _ = ShutdownAndExitAsync();
            }
        });
        _window.Activate();

        var vmStartupWarnings = new List<string>();
        try
        {
            var store = _serviceProvider.GetRequiredService<SqliteArchiveStore>();
            await store.InitializeAsync();
            try { await _serviceProvider.GetRequiredService<HiberDeskPeer>().InitializeAsync(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                vmStartupWarnings.Add("HiberDesk connection setup is unavailable.");
            }

            try { await UnifiedArchiveMigrationService.MigrateIfNeededAsync(config, configPath, store); }
            catch (Exception ex) { vmStartupWarnings.Add($"Migration: {ex.Message}"); }

            var archiver = _serviceProvider.GetRequiredService<LosslessArchiver>();
            await archiver.ReconcileAfterCrashAsync();

            var bridge = _serviceProvider.GetRequiredService<AuthenticatedBrowserBridge>();
            try { bridge.Start(); }
            catch (Exception ex) { vmStartupWarnings.Add($"Browser Bridge: {ex.Message}"); }
            if (!string.IsNullOrWhiteSpace(bridge.ListenerStartError))
                vmStartupWarnings.Add(bridge.ListenerStartError);

            var monitor = _serviceProvider.GetRequiredService<MultiDirectoryMonitor>();
            try { monitor.Start(); }
            catch (Exception ex) { vmStartupWarnings.Add($"Monitor: {ex.Message}"); }

            try { _serviceProvider.GetRequiredService<GoogleDriveService>().StartBackgroundSync(); }
            catch (Exception ex) { vmStartupWarnings.Add($"Google Drive: {ex.Message}"); }

            try { _serviceProvider.GetRequiredService<OneDriveService>().StartBackgroundSync(); }
            catch (Exception ex) { vmStartupWarnings.Add($"OneDrive: {ex.Message}"); }

            var vm = _serviceProvider.GetRequiredService<MainViewModel>();
            await vm.InitializeAsync();
            if (vmStartupWarnings.Count > 0)
            {
                vm.ShowInfoBar(
                    config.Language == UiLanguage.Vi ? "Cảnh báo khởi động" : "Startup Warning",
                    string.Join(" ", vmStartupWarnings),
                    InfoBarSeverity.Warning);
            }

            _ = Task.Run(async () =>
            {
                try { await monitor.ReconcileOnceAsync(); } catch { }
            });

            if (isSelfTest)
            {
                await UiSelfTestRunner.RunAndWriteReportAsync(
                    selfTestReportPath!,
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

        Interlocked.Exchange(ref _shutdownSignalRegistration, null)?.Dispose();
        _window?.TrayManager?.PrepareForExternalShutdown();

        try
        {
            if (_serviceProvider is not null)
            {
                await _serviceProvider.GetRequiredService<MultiDirectoryMonitor>().StopAsync();
                await _serviceProvider.GetRequiredService<AuthenticatedBrowserBridge>().StopAsync();
                await _serviceProvider.GetRequiredService<GoogleDriveService>().StopAsync();
                await _serviceProvider.GetRequiredService<OneDriveService>().StopAsync();
                await _serviceProvider.GetRequiredService<SqliteArchiveStore>().CheckpointWalAsync();
            }
        }
        catch
        {
            // Ignore shutdown errors
        }
        finally
        {
            _window?.DisposeTray();
            if (_host is not null)
            {
                try { await _host.DisposeAsync(); } catch { }
                _host = null;
                _serviceProvider = null;
            }
            if (_window is { } window)
            {
                window.SystemBackdrop = null;
                window.Content = null;
                window.DispatcherQueue.TryEnqueue(window.Close);
            }
            else
                Exit();
        }
    }

    private async Task<bool> OpenArchiveForPeerAsync(IUiDispatcher dispatcher, CancellationToken ct)
    {
        return await dispatcher.EnqueueAsync(() =>
        {
            if (_shuttingDown || _window is not { } window)
                return false;
            window.TrayManager?.ShowAndActivateWindow();
            return window.NavigateToTag("Archive");
        }, ct).ConfigureAwait(false);
    }
}
