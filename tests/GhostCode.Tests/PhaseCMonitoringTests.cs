using System.Text;
using GhostCode.Core.Archiver;
using GhostCode.Core.Models;
using GhostCode.Core.Monitoring;
using GhostCode.Core.Storage;

namespace GhostCode.Tests;

public class PhaseCMonitoringTests : IDisposable
{
    private readonly string _root;

    public PhaseCMonitoringTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GhostCode_PhaseC_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnectionPoolCleanup();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void SqliteConnectionPoolCleanup() =>
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    [Fact]
    public void DifferentWindowsUsernameAndOneDriveRedirection_AreDiscoveredAndDriveRootsRejected()
    {
        var fakeUser = Path.Combine(_root, "Users", "MinhAnh");
        var oneDriveDesktop = Path.Combine(fakeUser, "OneDrive", "Desktop");
        var docs = Path.Combine(fakeUser, "Documents");
        Directory.CreateDirectory(oneDriveDesktop);
        Directory.CreateDirectory(docs);

        var folders = KnownFoldersResolver.DiscoverDefaultWatchedFolders(fakeUser);
        Assert.Contains(folders, f => string.Equals(f.Path, oneDriveDesktop, StringComparison.OrdinalIgnoreCase) && f.Enabled && f.Recursive);
        Assert.Contains(folders, f => string.Equals(f.Path, docs, StringComparison.OrdinalIgnoreCase) && f.Enabled && f.Recursive);
        Assert.True(KnownFoldersResolver.IsDriveRoot(@"C:\"));
        Assert.True(KnownFoldersResolver.IsDriveRoot(@"D:"));
        Assert.False(KnownFoldersResolver.IsDriveRoot(oneDriveDesktop));
    }

    [Fact]
    public async Task MultiDirectoryMonitor_DetectsDesktopNestedDocumentsCustomAndAtomicRenameSaves()
    {
        var desktopDir = Path.Combine(_root, "OneDrive", "Desktop");
        var nestedDocs = Path.Combine(_root, "Documents", "PTIT", "Week05");
        var customDir = Path.Combine(_root, "MyProjects");
        var archiveDir = Path.Combine(_root, "Documents", "GhostCodeArchive");
        var readyDir = Path.Combine(_root, "Documents", "CodePTIT_Ready");
        Directory.CreateDirectory(desktopDir);
        Directory.CreateDirectory(nestedDocs);
        Directory.CreateDirectory(customDir);

        var cfg = new GhostCodeConfig
        {
            ArchiveDir = archiveDir,
            ReadyDir = readyDir,
            DatabasePath = Path.Combine(archiveDir, "ghostcode.db"),
            WatchedFolders = new List<WatchedFolderConfig>
            {
                new(desktopDir, Enabled: true, Recursive: true, Label: "Desktop"),
                new(Path.Combine(_root, "Documents"), Enabled: true, Recursive: true, Label: "Documents"),
                new(customDir, Enabled: true, Recursive: true, Label: "Custom"),
            }
        };

        await using var store = new SqliteArchiveStore(cfg.DatabasePath);
        await store.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, store);

        var archivedPaths = new List<string>();
        var tcsAll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        archiver.VersionArchived += v =>
        {
            lock (archivedPaths)
            {
                archivedPaths.Add(v.SourcePath);
                if (archivedPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 3)
                    tcsAll.TrySetResult();
            }
        };

        await using var monitor = new MultiDirectoryMonitor(cfg, store, archiver, () => null);
        monitor.Start();

        // 1. Save on OneDrive Desktop
        var desktopFile = Path.Combine(desktopDir, "Untitled1.cpp");
        await File.WriteAllTextAsync(desktopFile, "#include <stdio.h>\nint main(){ int x=10; printf(\"%d\", x); return 0; }\n");

        // 2. Save in nested Documents folder
        var nestedFile = Path.Combine(nestedDocs, "bai2.c");
        await File.WriteAllTextAsync(nestedFile, "#include <stdio.h>\nint main(){ int n=5; printf(\"%d\", n*n); return 0; }\n");

        // 3. Atomic save (write temp file + File.Move overwrite) in custom directory
        var customFile = Path.Combine(customDir, "solution.py");
        var tmpFile = Path.Combine(customDir, ".solution.py.tmp");
        await File.WriteAllTextAsync(tmpFile, "n = int(input())\nprint(n * 2)\n");
        File.Move(tmpFile, customFile, overwrite: true);

        await Task.WhenAny(tcsAll.Task, Task.Delay(TimeSpan.FromSeconds(8)));
        await Task.Delay(400);

        Assert.NotEmpty(await store.GetVersionsBySourceAsync(desktopFile));
        Assert.NotEmpty(await store.GetVersionsBySourceAsync(nestedFile));
        Assert.NotEmpty(await store.GetVersionsBySourceAsync(customFile));

        // Ensure archive/ready outputs did NOT trigger recursive archiving of ArchiveDir
        var allVersions = await store.QueryVersionsAsync();
        Assert.DoesNotContain(allVersions, v => v.SourcePath.StartsWith(archiveDir, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allVersions, v => v.SourcePath.StartsWith(readyDir, StringComparison.OrdinalIgnoreCase));

        // 4. Built-in diagnostic self-test
        var diag = await monitor.RunDiagnosticTestAsync(desktopDir);
        Assert.True(diag.Success, diag.Summary);
        Assert.False(File.Exists(diag.ProbeFilePath));
    }

    [Fact]
    public async Task PeriodicReconciliation_CatchesMissedFileChanges()
    {
        var watchDir = Path.Combine(_root, "Watched");
        Directory.CreateDirectory(watchDir);
        var cfg = new GhostCodeConfig
        {
            ArchiveDir = Path.Combine(_root, "CodeArchive"),
            ReadyDir = Path.Combine(_root, "Ready"),
            DatabasePath = Path.Combine(_root, "CodeArchive", "ghostcode.db"),
            WatchedFolders = new List<WatchedFolderConfig> { new(watchDir, true, true, "Watched") }
        };

        var preExisting = Path.Combine(watchDir, "PreSaved.cpp");
        await File.WriteAllTextAsync(preExisting, "#include <stdio.h>\nint main(){ printf(\"reconciled\"); return 0; }\n");

        await using var store = new SqliteArchiveStore(cfg.DatabasePath);
        await store.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, store);
        await using var monitor = new MultiDirectoryMonitor(cfg, store, archiver, () => null);

        int caught = await monitor.ReconcileOnceAsync();
        Assert.True(caught >= 1);
        Assert.NotEmpty(await store.GetVersionsBySourceAsync(preExisting));
    }
}
