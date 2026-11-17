using System.Security.Cryptography;
using System.Text;
using GhostCode.Core.Analysis;
using GhostCode.Core.Archiver;
using GhostCode.Core.Models;
using GhostCode.Core.Monitoring;
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
}
