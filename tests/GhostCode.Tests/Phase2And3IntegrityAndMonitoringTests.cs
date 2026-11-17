using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using GhostCode.Core.Analysis;
using GhostCode.Core.Archiver;
using GhostCode.Core.Models;
using GhostCode.Core.Monitoring;
using GhostCode.Core.Storage;
using Microsoft.Data.Sqlite;

namespace GhostCode.Tests;

public sealed class Phase2And3IntegrityAndMonitoringTests : IDisposable
{
    private readonly string _root;

    public Phase2And3IntegrityAndMonitoringTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GhostCode_P23_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private GhostCodeConfig MakeConfig(string? suffix = null)
    {
        var baseDir = suffix is null ? _root : Path.Combine(_root, suffix);
        var watched = Path.Combine(baseDir, "Workspace");
        var archive = Path.Combine(baseDir, "CodeArchive");
        var ready = Path.Combine(baseDir, "CodePTIT_Ready");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(archive);
        Directory.CreateDirectory(ready);
        return new GhostCodeConfig
        {
            WatchedFolders = new List<WatchedFolderConfig> { new(watched, true, true, "Workspace") },
            ArchiveDir = archive,
            ReadyDir = ready,
            DatabasePath = Path.Combine(archive, "ghostcode.db"),
            BridgeAuthToken = "test-token",
            HttpPort = 0
        };
    }

    [Fact]
    public async Task BugA_MissingHistoricalArchive_MustThrowFileNotFoundAndNeverRestoreNewerVersion()
    {
        var cfg = MakeConfig();
        await using var db = new SqliteArchiveStore(cfg.DatabasePath);
        await db.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, db);

        var src = Path.Combine(cfg.WatchedFolders[0].Path, "Untitled1.cpp");
        var v1Bytes = Encoding.UTF8.GetBytes("#include <stdio.h>\nint main(){ printf(\"V1\"); }\n");
        var v2Bytes = Encoding.UTF8.GetBytes("#include <stdio.h>\nint main(){ printf(\"V2_NEWER\"); }\n");

        var r1 = await archiver.ProcessFileSaveAsync(src, v1Bytes, null);
        var r2 = await archiver.ProcessFileSaveAsync(src, v2Bytes, null);
        Assert.Equal(1, r1.Version!.VersionNum);
        Assert.Equal(2, r2.Version!.VersionNum);

        // Simulate missing v1 historical file on disk
        File.Delete(r1.Version.ArchivePath);

        var dest = Path.Combine(_root, "Restored_V1.cpp");
        await Assert.ThrowsAsync<FileNotFoundException>(() => archiver.RestoreVersionAsync(r1.Version.Id, dest));
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task BugA_CorruptedHistoricalArchive_MustFailSha256IntegrityCheckOnRestore()
    {
        var cfg = MakeConfig();
        await using var db = new SqliteArchiveStore(cfg.DatabasePath);
        await db.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, db);

        var src = Path.Combine(cfg.WatchedFolders[0].Path, "corrupt_check.cpp");
        var v1Bytes = Encoding.UTF8.GetBytes("#include <iostream>\nint main(){ std::cout << 42; }\n");
        var r1 = await archiver.ProcessFileSaveAsync(src, v1Bytes, null);

        // Tamper 1 byte in historical file on disk
        var tampered = (byte[])v1Bytes.Clone();
        tampered[5] ^= 0x01;
        await File.WriteAllBytesAsync(r1.Version!.ArchivePath, tampered);

        var dest = Path.Combine(_root, "Restored_Corrupted.cpp");
        await Assert.ThrowsAsync<InvalidDataException>(() => archiver.RestoreVersionAsync(r1.Version.Id, dest));
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task BugB_ExistingHistoricalFileMustNeverBeOverwrittenEvenOnPathCollision()
    {
        var cfg = MakeConfig();
        await using var db = new SqliteArchiveStore(cfg.DatabasePath);
        await db.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, db);

        var dirA = Path.Combine(cfg.WatchedFolders[0].Path, "A");
        var dirB = Path.Combine(cfg.WatchedFolders[0].Path, "B");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        var srcA = Path.Combine(dirA, "sol.cpp");
        var srcB = Path.Combine(dirB, "sol.cpp");
        var bytesA = Encoding.UTF8.GetBytes("#include <iostream>\nint main(){ std::cout << \"FILE_A\"; }\n");
        var bytesB = Encoding.UTF8.GetBytes("#include <iostream>\nint main(){ std::cout << \"FILE_B\"; }\n");

        // Save File A -> v1
        var rA = await archiver.ProcessFileSaveAsync(srcA, bytesA, null);

        // Pre-place an immutable file at the exact clean slot path that srcB would compute if no collision guard existed
        var shaB = SourceAnalyzer.ComputeSha256Hex(bytesB);
        var cleanNameB = SourceAnalyzer.BuildCleanFileName(null, srcB, Encoding.UTF8.GetString(bytesB), SourceLanguage.Cpp);
        var slotDirB = Path.Combine(cfg.GetInternalHistoryRoot(), DateTime.Now.ToString("yyyy-MM-dd"), $"v1_{shaB[..8]}");
        Directory.CreateDirectory(slotDirB);
        var collidingPath = Path.Combine(slotDirB, cleanNameB);
        var sentinelBytes = Encoding.UTF8.GetBytes("// DO NOT OVERWRITE ME\n");
        await File.WriteAllBytesAsync(collidingPath, sentinelBytes);

        // Saving File B must pick a collision-free unique historical filename instead of overwriting sentinelBytes
        var rB = await archiver.ProcessFileSaveAsync(srcB, bytesB, null);
        Assert.Equal(sentinelBytes, await File.ReadAllBytesAsync(collidingPath));
        Assert.NotEqual(collidingPath, rB.Version!.ArchivePath);
        Assert.Equal(bytesB, await File.ReadAllBytesAsync(rB.Version.ArchivePath));
    }

    [Fact]
    public async Task BugC_CrashedUploadingQueueRows_AreResetToPendingOnRestart()
    {
        var cfg = MakeConfig();
        await using (var db1 = new SqliteArchiveStore(cfg.DatabasePath))
        {
            await db1.InitializeAsync();
            var archiver = new LosslessArchiver(cfg, db1);
            var res = await archiver.ProcessFileSaveAsync(
                Path.Combine(cfg.WatchedFolders[0].Path, "crash_upload.cpp"),
                Encoding.UTF8.GetBytes("#include <stdio.h>\nint main(){ puts(\"crash\"); }\n"),
                null);
            await db1.MarkUploadAttemptAsync(res.Version!.Id, UploadStatus.Uploading, null, null, 0);
        }

        // Simulate app restart after mid-upload crash
        await using var db2 = new SqliteArchiveStore(cfg.DatabasePath);
        await db2.InitializeAsync();
        var pending = await db2.GetPendingOrRetryableUploadsAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Single(pending);
        Assert.Equal(UploadStatus.Pending, pending[0].Queue.Status);
    }

    [Fact]
    public async Task Mon1_OverlappingSubfoldersAndOutputDirectories_AreDeduplicatedAndExcluded()
    {
        var cfg = MakeConfig();
        var parent = cfg.WatchedFolders[0].Path;
        var child = Path.Combine(parent, "ContestA");
        Directory.CreateDirectory(child);

        cfg.WatchedFolders.Add(new WatchedFolderConfig(child, Enabled: true, Recursive: true, Label: "NestedChild"));
        cfg.WatchedFolders.Add(new WatchedFolderConfig(cfg.ArchiveDir, Enabled: true, Recursive: true, Label: "ArchiveSelf"));
        cfg.WatchedFolders.Add(new WatchedFolderConfig(cfg.ReadyDir, Enabled: true, Recursive: true, Label: "ReadySelf"));

        await using var db = new SqliteArchiveStore(cfg.DatabasePath);
        await db.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, db);
        await using var monitor = new MultiDirectoryMonitor(cfg, db, archiver, () => null);
        monitor.Start();

        var watches = monitor.GetActiveWatches();
        // Only the parent folder should hold an active OS FileSystemWatcher
        Assert.Equal(1, watches.Count(w => w.IsWatching));
        Assert.Contains(watches, w => !w.IsWatching && string.Equals(w.FolderPath, cfg.ArchiveDir, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(watches, w => !w.IsWatching && string.Equals(w.FolderPath, cfg.ReadyDir, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Mon2_DiagnosticTest_CleansUpProbeFromArchiveReadyAndDatabase()
    {
        var cfg = MakeConfig();
        await using var db = new SqliteArchiveStore(cfg.DatabasePath);
        await db.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, db);
        await using var monitor = new MultiDirectoryMonitor(cfg, db, archiver, () => null);
        monitor.Start();

        var diag = await monitor.RunDiagnosticTestAsync();
        Assert.True(diag.Success, diag.Summary);

        var stats = await db.GetStatsAsync();
        Assert.Equal(0, stats.TotalVersions);
        Assert.Empty(Directory.GetFiles(cfg.ReadyDir, "GhostCode_DiagProbe*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Phase3_RealWindowsDesktopKnownFolderAndDevCppCompilerWorkflow_ProtectsUserFilesAndArchivesAllSolutions()
    {
        // Guard: Verify user's real C:\Users\Duc\OneDrive\Desktop\Untitled1.cpp (if present) stays untouched
        var realDesktop = KnownFoldersResolver.DiscoverDefaultWatchedFolders()
            .FirstOrDefault(w => w.Label.Contains("Desktop", StringComparison.OrdinalIgnoreCase))?.Path;
        Assert.False(string.IsNullOrWhiteSpace(realDesktop));
        Assert.True(Directory.Exists(realDesktop));

        var realUntitled = Path.Combine(realDesktop!, "Untitled1.cpp");
        byte[]? originalUserHash = File.Exists(realUntitled) ? SHA256.HashData(await File.ReadAllBytesAsync(realUntitled)) : null;

        var sandboxSub = Path.Combine(realDesktop!, "GhostCode_LiveVerify_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandboxSub);

        try
        {
            var cfg = MakeConfig("LiveDesktop");
            cfg.WatchedFolders = new List<WatchedFolderConfig>
            {
                new(realDesktop!, Enabled: true, Recursive: true, Label: "RealDesktop")
            };

            await using var db = new SqliteArchiveStore(cfg.DatabasePath);
            await db.InitializeAsync();
            var archiver = new LosslessArchiver(cfg, db);
            await using var monitor = new MultiDirectoryMonitor(cfg, db, archiver, () => null);
            monitor.Start();

            // 1. Save Untitled1.cpp inside real Windows Known Folder Desktop (recursive watch)
            var testSrc = Path.Combine(sandboxSub, "Untitled1.cpp");
            var sol1 = Encoding.UTF8.GetBytes("#include <stdio.h>\nint main(){ printf(\"SOL1\"); return 0; }\n");
            await File.WriteAllBytesAsync(testSrc, sol1);

            await WaitForCountAsync(() => db.GetVersionsBySourceAsync(testSrc), 1);

            // 2. Edit + Ctrl+S -> creates v2
            var sol2 = Encoding.UTF8.GetBytes("#include <stdio.h>\nint main(){ printf(\"SOL2_EDIT\"); return 0; }\n");
            await File.WriteAllBytesAsync(testSrc, sol2);
            await WaitForCountAsync(() => db.GetVersionsBySourceAsync(testSrc), 2);

            // 3. Real Dev-C++ MinGW64 compiler check when installed
            const string devCppGpp = @"C:\Program Files (x86)\Dev-Cpp\MinGW64\bin\g++.exe";
            if (File.Exists(devCppGpp))
            {
                var exePath = Path.Combine(sandboxSub, "Untitled1.exe");
                using var comp = Process.Start(new ProcessStartInfo(devCppGpp, $"\"{testSrc}\" -o \"{exePath}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                })!;
                await comp.WaitForExitAsync();
                Assert.Equal(0, comp.ExitCode);
                Assert.True(File.Exists(exePath));
            }

            // 4. Template reset -> previous versions preserved
            await File.WriteAllTextAsync(testSrc, "int main() {\n\treturn 0;\n}\n");
            await Task.Delay(500);
            Assert.Equal(2, (await db.GetVersionsBySourceAsync(testSrc)).Count);

            // 5. Write third unrelated solution -> v3 archived; v1, v2, v3 independently restorable
            var sol3 = Encoding.UTF8.GetBytes("#include <stdio.h>\nint main(){ int x=7; printf(\"%d\", x*x); return 0; }\n");
            await File.WriteAllBytesAsync(testSrc, sol3);
            await WaitForCountAsync(() => db.GetVersionsBySourceAsync(testSrc), 3);

            var versions = await db.GetVersionsBySourceAsync(testSrc);
            var r1 = Path.Combine(_root, "r1.cpp");
            var r2 = Path.Combine(_root, "r2.cpp");
            var r3 = Path.Combine(_root, "r3.cpp");
            await archiver.RestoreVersionAsync(versions.Single(v => v.VersionNum == 1).Id, r1);
            await archiver.RestoreVersionAsync(versions.Single(v => v.VersionNum == 2).Id, r2);
            await archiver.RestoreVersionAsync(versions.Single(v => v.VersionNum == 3).Id, r3);
            Assert.Equal(sol1, await File.ReadAllBytesAsync(r1));
            Assert.Equal(sol2, await File.ReadAllBytesAsync(r2));
            Assert.Equal(sol3, await File.ReadAllBytesAsync(r3));
        }
        finally
        {
            try { Directory.Delete(sandboxSub, recursive: true); } catch { }
            if (originalUserHash is not null)
            {
                var afterHash = SHA256.HashData(await File.ReadAllBytesAsync(realUntitled));
                Assert.Equal(originalUserHash, afterHash);
            }
        }
    }

    [Fact]
    public async Task BugB_ConcurrentArchiveCreation_NeverOverwritesOrLosesVersions()
    {
        var cfg = MakeConfig("Concurrent");
        await using var db = new SqliteArchiveStore(cfg.DatabasePath);
        await db.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, db);

        var tasks = Enumerable.Range(1, 16).Select(i => Task.Run(async () =>
        {
            var dir = Path.Combine(cfg.WatchedFolders[0].Path, $"P{i}");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "Untitled1.cpp");
            var bytes = Encoding.UTF8.GetBytes($"#include <iostream>\nint main(){{ std::cout << {i * 31}; return 0; }}\n");
            return await archiver.ProcessFileSaveAsync(file, bytes, null);
        })).ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Equal(ArchiveOutcome.CreatedVersion, r.Outcome));
        var uniquePaths = results.Select(r => r.Version!.ArchivePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Assert.Equal(16, uniquePaths);
        Assert.Equal(16, (await db.GetStatsAsync()).TotalVersions);
    }

    [Fact]
    public async Task BugC_LeftoverTempFilesAndOrphanHistoricalFiles_AreCleanedAndRecoveredOnCrashReconcile()
    {
        var cfg = MakeConfig("CrashReconcile");
        await using var db = new SqliteArchiveStore(cfg.DatabasePath);
        await db.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, db);

        var historyDir = Path.Combine(cfg.ArchiveDir, "History", "2026-11-17");
        Directory.CreateDirectory(historyDir);

        // Simulate leftover crash temp files in ArchiveDir and ReadyDir
        var tmpArchive = Path.Combine(historyDir, ".tmp.aborted123");
        var tmpReady = Path.Combine(cfg.ReadyDir, "aborted.cpp.tmp.456");
        await File.WriteAllTextAsync(tmpArchive, "partial");
        await File.WriteAllTextAsync(tmpReady, "partial");

        // Simulate completed historical file orphaned on disk before SQLite commit finished
        var orphanFile = Path.Combine(historyDir, "Orphan_Sol_v3_deadbeef.cpp");
        var orphanCode = Encoding.UTF8.GetBytes("#include <iostream>\nint main(){ std::cout << \"orphan\"; }\n");
        await File.WriteAllBytesAsync(orphanFile, orphanCode);

        int recovered = await archiver.ReconcileAfterCrashAsync();
        Assert.Equal(1, recovered);
        Assert.False(File.Exists(tmpArchive));
        Assert.False(File.Exists(tmpReady));

        var ver = await db.GetVersionByArchivePathAsync(orphanFile);
        Assert.NotNull(ver);
        Assert.Equal(3, ver!.VersionNum);
        Assert.Equal(orphanCode, await archiver.ReadAndVerifyVersionBytesAsync(ver.Id));
    }

    [Fact]
    public async Task Phase3_DynamicAddRemoveFolderWithoutRestart_UpdatesWatchesAndDiagnostics()
    {
        var cfg = MakeConfig("DynamicWatches");
        var customDir = Path.Combine(_root, "CustomDynamic");
        Directory.CreateDirectory(customDir);

        await using var db = new SqliteArchiveStore(cfg.DatabasePath);
        await db.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, db);
        await using var monitor = new MultiDirectoryMonitor(cfg, db, archiver, () => null);
        monitor.Start();

        // Add folder at runtime without restarting monitor
        cfg.WatchedFolders.Add(new WatchedFolderConfig(customDir, true, true, "Dynamic"));
        monitor.ReloadWatches();

        var customFile = Path.Combine(customDir, "dyn.py");
        await File.WriteAllTextAsync(customFile, "print('hello dynamic folder')\n");
        await WaitForCountAsync(() => db.GetVersionsBySourceAsync(customFile), 1);

        Assert.Contains("dyn.py", monitor.LastDetectedFile);
        Assert.Contains("v1", monitor.LastSuccessfulBackup);

        // Remove folder dynamically without restarting -> subsequent edits are not captured
        cfg.WatchedFolders.RemoveAll(w => string.Equals(w.Path, customDir, StringComparison.OrdinalIgnoreCase));
        monitor.ReloadWatches();

        var customFile2 = Path.Combine(customDir, "ignored_after_remove.py");
        await File.WriteAllTextAsync(customFile2, "print('should not be archived')\n");
        await Task.Delay(500);
        Assert.Empty(await db.GetVersionsBySourceAsync(customFile2));
    }

    private static async Task WaitForCountAsync<T>(Func<Task<List<T>>> getter, int expectedCount)
    {
        var deadline = Environment.TickCount64 + 6000;
        while (Environment.TickCount64 < deadline)
        {
            if ((await getter()).Count >= expectedCount)
                return;
            await Task.Delay(60);
        }
        throw new TimeoutException($"Timed out waiting for {expectedCount} items.");
    }
}
