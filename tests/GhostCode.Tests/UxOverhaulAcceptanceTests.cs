using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GhostCode.Core.Analysis;
using GhostCode.Core.Archiver;
using GhostCode.Core.Browser;
using GhostCode.Core.Cloud;
using GhostCode.Core.Models;
using GhostCode.Core.Monitoring;
using GhostCode.Core.Notifications;
using GhostCode.Core.Storage;

namespace GhostCode.Tests;

public sealed class UxOverhaulAcceptanceTests : IDisposable
{
    private readonly string _root;

    public UxOverhaulAcceptanceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GhostCode_UXOverhaul_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task P0_ScenarioD_LocalSourceArchiving_Untitled1_VersionsAndByteExactRestore_WithoutBrowserOrCloud()
    {
        var watchedRoot = Path.Combine(_root, "MyCode");
        var archiveDir = Path.Combine(_root, "CodeArchive");
        var readyDir = Path.Combine(_root, "Ready");
        Directory.CreateDirectory(watchedRoot);
        Directory.CreateDirectory(archiveDir);
        Directory.CreateDirectory(readyDir);

        var config = new GhostCodeConfig
        {
            WatchedFolders = new List<WatchedFolderConfig>
            {
                new(watchedRoot, Enabled: true, Recursive: true, Label: "MyCode")
            },
            ArchiveDir = archiveDir,
            ReadyDir = readyDir,
            DatabasePath = Path.Combine(archiveDir, "ghostcode.db"),
            PrimaryCloudProvider = CloudProviderKind.LocalOnly,
            DriveEnabled = false,
            Language = UiLanguage.Vi
        };

        await using var store = new SqliteArchiveStore(config.DatabasePath);
        await store.InitializeAsync();
        var archiver = new LosslessArchiver(config, store);
        await using var monitor = new MultiDirectoryMonitor(config, store, archiver, () => null);
        var health = new BackupHealthAndRecoveryService(config, store, monitor);

        int reconciledEventCount = -1;
        monitor.ReconciliationCompleted += c => reconciledEventCount = c;

        // Version A already on disk before monitor startup
        var untitled = Path.Combine(watchedRoot, "Untitled1.cpp");
        const string codeV1 = "#include <iostream>\nint main(){ std::cout << \"v1\\n\"; return 0; }\n";
        await File.WriteAllBytesAsync(untitled, Encoding.UTF8.GetBytes(codeV1));

        monitor.Start();
        int initialArchived = await monitor.ReconcileOnceAsync();
        Assert.Equal(1, initialArchived);
        Assert.Equal(1, reconciledEventCount);

        var snapHealth = await health.EvaluateHealthAsync(attemptWatchRecovery: false);
        Assert.Equal(BackupProtectionState.Protected, snapHealth.ProtectionState);
        Assert.True(snapHealth.MonitoringActive);
        Assert.Equal(0, snapHealth.ErroredWatchCount);

        // Save Version B (simulate editor modify in real watched folder)
        const string codeV2 = "#include <iostream>\nint main(){ std::cout << \"v2 updated\\n\"; return 0; }\n";
        await File.WriteAllBytesAsync(untitled, Encoding.UTF8.GetBytes(codeV2));

        var deadline = Environment.TickCount64 + 6000;
        while (Environment.TickCount64 < deadline)
        {
            var versionsNow = await store.GetVersionsBySourceAsync(untitled);
            if (versionsNow.Count >= 2)
                break;
            await Task.Delay(120);
        }

        var versions = await store.GetVersionsBySourceAsync(untitled);
        Assert.Equal(2, versions.Count);

        var v1 = versions.Single(v => v.VersionNum == 1);
        var v2 = versions.Single(v => v.VersionNum == 2);
        Assert.NotEqual(v1.Sha256, v2.Sha256);
        Assert.Equal(SourceAnalyzer.ComputeSha256Hex(Encoding.UTF8.GetBytes(codeV1)), v1.Sha256);
        Assert.Equal(SourceAnalyzer.ComputeSha256Hex(Encoding.UTF8.GetBytes(codeV2)), v2.Sha256);

        // Non-destructive byte-exact restore of v1
        var restoreV1Path = Path.Combine(_root, "Restored_v1.cpp");
        await archiver.RestoreVersionAsync(v1.Id, restoreV1Path);
        Assert.Equal(codeV1, await File.ReadAllTextAsync(restoreV1Path));
        // Original Untitled1.cpp must remain untouched (still codeV2)
        Assert.Equal(codeV2, await File.ReadAllTextAsync(untitled));

        var recCheck = await health.RunRecoveryCheckAsync();
        Assert.Equal(2, recCheck.LocalVerifiedOk);
        Assert.Equal(0, recCheck.LocalCorruptedCount);
        Assert.Equal(0, recCheck.LocalMissingCount);
    }

    [Fact]
    public async Task P0_ReconcileOnceAsync_DoesNotMisclassifyCoveredChildWatchAsError_AndTraversesRealSubdirs()
    {
        var parentDir = Path.Combine(_root, "Documents");
        var childDir = Path.Combine(parentDir, "Subfolder");
        var archiveDir = Path.Combine(_root, "CodeArchive");
        var readyDir = Path.Combine(_root, "Ready");
        Directory.CreateDirectory(childDir);
        Directory.CreateDirectory(archiveDir);
        Directory.CreateDirectory(readyDir);

        var subFile = Path.Combine(childDir, "nested.cpp");
        await File.WriteAllTextAsync(subFile, "int main(){ return 42; }\n");

        var config = new GhostCodeConfig
        {
            WatchedFolders = new List<WatchedFolderConfig>
            {
                new(parentDir, Enabled: true, Recursive: true, Label: "Documents"),
                new(childDir, Enabled: true, Recursive: true, Label: "Subfolder")
            },
            ArchiveDir = archiveDir,
            ReadyDir = readyDir,
            DatabasePath = Path.Combine(archiveDir, "ghostcode.db"),
            Language = UiLanguage.Vi
        };

        await using var store = new SqliteArchiveStore(config.DatabasePath);
        await store.InitializeAsync();
        var archiver = new LosslessArchiver(config, store);
        await using var monitor = new MultiDirectoryMonitor(config, store, archiver, () => null);
        var health = new BackupHealthAndRecoveryService(config, store, monitor);

        monitor.Start();
        var count = await monitor.ReconcileOnceAsync();
        Assert.Equal(1, count);

        var watches = monitor.GetActiveWatches();
        Assert.Contains(watches, w => w.IsWatching);
        Assert.Contains(watches, w => w.IsCoveredByParent);

        var snap = await health.EvaluateHealthAsync(attemptWatchRecovery: false);
        Assert.Equal(BackupProtectionState.Protected, snap.ProtectionState);
        Assert.Equal(0, snap.ErroredWatchCount);
        Assert.Contains("Đang bảo vệ", snap.StatusSummary);
    }

    private sealed class FixedBrowserProbe : IBrowserEnvironmentProbe
    {
        public HashSet<string> ExistingPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RunningProcesses { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool FileExists(string path) => ExistingPaths.Contains(path);
        public IReadOnlyCollection<string> GetRunningProcessNames() => RunningProcesses;
        public string? GetFileVersion(string path) => "130.0.0.0";
        public string? ReadRegistryString(string fullKeyPath, string valueName) => null;
    }

    [Fact]
    public async Task P1_ScenarioC_BrowserStagingAndNativeMessaging_IdentifiesThoriumAndEdgeAndTransitionsOnlyOnHandshake()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var probe = new FixedBrowserProbe();
        probe.ExistingPaths.Add(@"C:\Mock\msedge.exe");
        probe.ExistingPaths.Add(@"C:\Mock\thorium.exe");

        var cfg = new GhostCodeConfig
        {
            HttpPort = 0,
            BridgeAuthToken = "p1-bridge-secret",
            CustomBrowserPaths =
            {
                ["Edge"] = @"C:\Mock\msedge.exe",
                ["Thorium"] = @"C:\Mock\thorium.exe"
            }
        };

        var integration = new BrowserIntegrationService(probe);
        await using var bridge = new AuthenticatedBrowserBridge(cfg, integration);
        bridge.Start();
        cfg.HttpPort = bridge.ListeningPort;

        var appDataRoot = Path.Combine(_root, "LocalAppData");
        var thResult = integration.LaunchAssistedInstall(
            BrowserKind.Thorium,
            cfg,
            bridge.ListeningPort,
            launchExternalProcesses: false,
            hostExecutablePath: @"C:\Mock\GhostCode.App.exe",
            appDataOverride: appDataRoot);

        Assert.True(thResult.Success);
        Assert.Equal("chrome://extensions", thResult.ExtensionsPageUrl);
        Assert.True(File.Exists(Path.Combine(thResult.ExtensionDirectory, "pairing.generated.json")));
        Assert.True(File.Exists(Path.Combine(thResult.ExtensionDirectory, "manifest.json")));

        var pairingText = await File.ReadAllTextAsync(Path.Combine(thResult.ExtensionDirectory, "pairing.generated.json"));
        Assert.Contains("\"browser_name\":\"Thorium\"", pairingText);

        var manifestText = await File.ReadAllTextAsync(Path.Combine(thResult.ExtensionDirectory, "manifest.json"));
        Assert.Contains("\"alarms\"", manifestText);

        var nmManifest = await File.ReadAllTextAsync(thResult.NativeManifestPath);
        Assert.Contains("--native-messaging-host", File.ReadAllText(Path.Combine(Path.GetDirectoryName(thResult.NativeManifestPath)!, "com.ghostcode.bridge.thorium.cmd")));
        Assert.Contains("com.ghostcode.bridge.thorium.cmd", nmManifest);

        // Before handshake: Thorium is GuidedInstalling, Edge is Installed (neither Connected)
        var initialStates = bridge.GetBrowserConnectionStates();
        Assert.Equal(BrowserConnectionState.GuidedInstalling, initialStates.Single(b => b.Kind == BrowserKind.Thorium).ConnectionState);
        Assert.Equal(BrowserConnectionState.Installed, initialStates.Single(b => b.Kind == BrowserKind.Edge).ConnectionState);

        // Simulate Chrome/Thorium launching GhostCode.App.exe --native-messaging-host over stdio
        Assert.True(NativeMessagingHost.IsNativeMessagingInvocation(new[] { "--native-messaging-host", "--browser", "Thorium" }));
        Assert.True(NativeMessagingHost.IsNativeMessagingInvocation(new[] { BrowserIntegrationService.ChromiumDeterministicExtensionOrigin }));

        using var inputStream = new MemoryStream();
        using var outputStream = new MemoryStream();
        await NativeMessagingHost.WriteMessageAsync(inputStream, """{"type":"handshake"}""");
        inputStream.Position = 0;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        int exitCode = await NativeMessagingHost.RunStdioBridgeStreamsAsync(inputStream, outputStream, cfg, "Thorium", http);
        Assert.Equal(0, exitCode);

        outputStream.Position = 0;
        var lenBuf = new byte[4];
        Assert.Equal(4, await outputStream.ReadAsync(lenBuf));
        int respLen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
        var respBytes = new byte[respLen];
        Assert.Equal(respLen, await outputStream.ReadAsync(respBytes));
        using var respDoc = JsonDocument.Parse(respBytes);
        Assert.True(respDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("Thorium", respDoc.RootElement.GetProperty("browser_name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(respDoc.RootElement.GetProperty("session_token").GetString()));

        // After real handshake: Thorium transitions to ExtensionConnected while Edge stays Installed
        var afterStates = bridge.GetBrowserConnectionStates();
        Assert.Equal(BrowserConnectionState.ExtensionConnected, afterStates.Single(b => b.Kind == BrowserKind.Thorium).ConnectionState);
        Assert.Equal(BrowserConnectionState.Installed, afterStates.Single(b => b.Kind == BrowserKind.Edge).ConnectionState);
        Assert.Contains("Thorium", cfg.KnownConnectedBrowsers);
    }

    [Fact]
    public void P2_UserFriendlyErrorService_MapsAll17CategoriesAndRedactsSecretsInBothLanguages()
    {
        var allCategories = Enum.GetValues<ErrorCategory>();
        Assert.Equal(17, allCategories.Length);

        const string secretSample = "Authorization: Bearer ya29.super-secret-oauth-token-12345 pairing_secret=abc999 client_secret=\"topsecret\"";
        foreach (var cat in allCategories)
        {
            var vi = UserFriendlyErrorService.MapError(cat, secretSample, UiLanguage.Vi);
            var en = UserFriendlyErrorService.MapError(cat, secretSample, UiLanguage.En);

            Assert.False(string.IsNullOrWhiteSpace(vi.Title));
            Assert.False(string.IsNullOrWhiteSpace(vi.Explanation));
            Assert.False(string.IsNullOrWhiteSpace(vi.ActionLabel));
            Assert.DoesNotContain("ya29.super-secret", vi.RedactedTechnicalDetails);
            Assert.DoesNotContain("abc999", vi.RedactedTechnicalDetails);
            Assert.DoesNotContain("topsecret", vi.RedactedTechnicalDetails);
            Assert.Contains("[REDACTED]", vi.RedactedTechnicalDetails);

            Assert.False(string.IsNullOrWhiteSpace(en.Title));
            Assert.False(string.IsNullOrWhiteSpace(en.Explanation));
            Assert.False(string.IsNullOrWhiteSpace(en.ActionLabel));
            Assert.NotEqual(vi.Title, en.Title);
        }

        // Exception classifier maps common exceptions accurately
        var netMapped = UserFriendlyErrorService.ClassifyAndMap(new HttpRequestException("Name or service not known"), UiLanguage.Vi);
        Assert.Equal(ErrorCategory.NetworkOffline, netMapped.Category);
        Assert.Equal("Không có kết nối mạng", netMapped.Title);

        var authMapped = UserFriendlyErrorService.ClassifyAndMap(new Exception("401 Unauthorized invalid_grant"), UiLanguage.Vi);
        Assert.Equal(ErrorCategory.AuthenticationExpired, authMapped.Category);
    }

    [Fact]
    public async Task P3_CloudProviderSwitchToLocalOnly_PreservesCredentialsAndSkipsSyncWithoutDeletingArchives()
    {
        var archiveDir = Path.Combine(_root, "Archive");
        var readyDir = Path.Combine(_root, "Ready");
        var credPath = Path.Combine(_root, "credentials.dpapi");
        Directory.CreateDirectory(archiveDir);
        Directory.CreateDirectory(readyDir);

        var cfg = new GhostCodeConfig
        {
            ArchiveDir = archiveDir,
            ReadyDir = readyDir,
            DatabasePath = Path.Combine(archiveDir, "ghostcode.db"),
            PrimaryCloudProvider = CloudProviderKind.GoogleDrive,
            OAuthClient = new GoogleOAuthClientConfig("client-id.apps.googleusercontent.com", ""),
            Language = UiLanguage.Vi
        };

        await using var store = new SqliteArchiveStore(cfg.DatabasePath);
        await store.InitializeAsync();
        var creds = new DpapiCredentialStore(credPath);
        await creds.SaveAsync(new GoogleAuthTokens("tok", "ref", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(), "dev@example.com", "Dev"));

        var archiver = new LosslessArchiver(cfg, store);
        var src = Path.Combine(_root, "sample.cpp");
        await File.WriteAllTextAsync(src, "#include <iostream>\nint main(){ int x = 42; std::cout << x << \"\\n\"; return 0; }\n");
        var res = await archiver.ProcessFileSaveAsync(src, await File.ReadAllBytesAsync(src), null);
        Assert.Equal(ArchiveOutcome.CreatedVersion, res.Outcome);

        // Switching to LocalOnly must not delete saved credentials or archived versions
        cfg.PrimaryCloudProvider = CloudProviderKind.LocalOnly;
        var loadedTok = await creds.LoadAsync();
        Assert.NotNull(loadedTok);
        Assert.Equal("dev@example.com", loadedTok!.Email);

        var stats = await store.GetStatsAsync();
        Assert.Equal(1, stats.TotalVersions);

        await using var drive = new GoogleDriveService(cfg, store, creds);
        var acct = await drive.GetAccountInfoAsync();
        Assert.True(acct.IsConnected);
    }
}
