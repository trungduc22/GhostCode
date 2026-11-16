using System.Text;
using GhostCode.Core.Analysis;
using GhostCode.Core.Archiver;
using GhostCode.Core.Models;
using GhostCode.Core.Storage;

namespace GhostCode.Tests;

public class PhaseBArchiverTests : IDisposable
{
    private readonly string _root;
    private readonly string _archiveDir;
    private readonly string _readyDir;
    private readonly string _dbPath;

    public PhaseBArchiverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GhostCode_PhaseB_" + Guid.NewGuid().ToString("N"));
        _archiveDir = Path.Combine(_root, "CodeArchive");
        _readyDir = Path.Combine(_root, "CodePTIT_Ready");
        _dbPath = Path.Combine(_archiveDir, "ghostcode.db");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private GhostCodeConfig MakeConfig() => new()
    {
        ArchiveDir = _archiveDir,
        ReadyDir = _readyDir,
        DatabasePath = _dbPath,
        WatchedFolders = new List<WatchedFolderConfig>
        {
            new(_root, Enabled: true, Recursive: true, Label: "TestRoot")
        }
    };

    [Fact]
    public async Task SaveWithoutBrowserContext_CreatesLosslessImmutableBackup()
    {
        var cfg = MakeConfig();
        await using var store = new SqliteArchiveStore(cfg.DatabasePath);
        await store.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, store);

        var srcPath = Path.Combine(_root, "Untitled1.cpp");
        const string code = "#include <stdio.h>\r\nint main() {\r\n    printf(\"Hello PTIT\\n\");\r\n    return 0;\r\n}\r\n";
        var raw = Encoding.UTF8.GetBytes(code);
        await File.WriteAllBytesAsync(srcPath, raw);

        var res = await archiver.ProcessFileSaveAsync(srcPath, raw, activeContext: null);

        Assert.Equal(ArchiveOutcome.CreatedVersion, res.Outcome);
        Assert.NotNull(res.Version);
        Assert.Equal(1, res.Version!.VersionNum);
        Assert.True(File.Exists(res.Version.ArchivePath));
        Assert.Equal(raw, await File.ReadAllBytesAsync(res.Version.ArchivePath));

        var pending = await store.GetPendingOrRetryableUploadsAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1000);
        Assert.Single(pending);
    }

    [Fact]
    public async Task RepeatedEditsAbove50PercentSimilarity_CreateImmutableVersionsWithoutOverwriting()
    {
        var cfg = MakeConfig();
        await using var store = new SqliteArchiveStore(cfg.DatabasePath);
        await store.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, store);

        var srcPath = Path.Combine(_root, "Untitled1.cpp");
        var v1Bytes = Encoding.UTF8.GetBytes("#include <stdio.h>\nint main(){\n    int a=1,b=2;\n    printf(\"%d\", a+b);\n    return 0;\n}\n");
        var v2Bytes = Encoding.UTF8.GetBytes("#include <stdio.h>\nint main(){\n    int a=1,b=2;\n    printf(\"Sum=%d\\n\", a+b);\n    return 0;\n}\n");

        var r1 = await archiver.ProcessFileSaveAsync(srcPath, v1Bytes, null);
        // Duplicate exact bytes -> deduplicated
        var rDup = await archiver.ProcessFileSaveAsync(srcPath, v1Bytes, null);
        // Edit (>50% similar) -> MUST create immutable v2, keeping v1 intact!
        var r2 = await archiver.ProcessFileSaveAsync(srcPath, v2Bytes, null);

        Assert.Equal(ArchiveOutcome.CreatedVersion, r1.Outcome);
        Assert.Equal(ArchiveOutcome.Deduplicated, rDup.Outcome);
        Assert.Equal(ArchiveOutcome.CreatedVersion, r2.Outcome);
        Assert.Equal(1, r1.Version!.VersionNum);
        Assert.Equal(2, r2.Version!.VersionNum);
        Assert.NotEqual(r1.Version.ArchivePath, r2.Version.ArchivePath);
        Assert.Equal(v1Bytes, await File.ReadAllBytesAsync(r1.Version.ArchivePath));
        Assert.Equal(v2Bytes, await File.ReadAllBytesAsync(r2.Version.ArchivePath));

        var history = await store.GetVersionsBySourceAsync(srcPath);
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public async Task ClearAndTemplateReset_PreservesPreviousVersionAndRecoversAcrossRestart()
    {
        var cfg = MakeConfig();
        var srcPath = Path.Combine(_root, "Untitled1.cpp");
        var usefulBytes = Encoding.UTF8.GetBytes("#include <stdio.h>\nint main(){\n    int n; scanf(\"%d\",&n);\n    printf(\"%d\", n*2);\n    return 0;\n}\n");
        var ctx = new ProblemContext(OjPlatform.Ptit, "C01001", "GAP DOI", "https://code.ptit.edu.vn/student/question/C01001", "Extension", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        await using (var store1 = new SqliteArchiveStore(cfg.DatabasePath))
        {
            await store1.InitializeAsync();
            var archiver1 = new LosslessArchiver(cfg, store1);
            var r1 = await archiver1.ProcessFileSaveAsync(srcPath, usefulBytes, ctx);
            Assert.Equal(ArchiveOutcome.CreatedVersion, r1.Outcome);

            // User clears file back to empty template
            var templateBytes = Encoding.UTF8.GetBytes("#include <stdio.h>\nint main() {\n    return 0;\n}\n");
            var rClear = await archiver1.ProcessFileSaveAsync(srcPath, templateBytes, null);
            Assert.Equal(ArchiveOutcome.PreservedPreviousOnReset, rClear.Outcome);
        }

        // Simulate app restart with fresh SQLite store instance
        await using (var store2 = new SqliteArchiveStore(cfg.DatabasePath))
        {
            await store2.InitializeAsync();
            var snap = await store2.GetSnapshotAsync(srcPath);
            Assert.NotNull(snap);
            Assert.Equal(usefulBytes, snap!.RawBytes);

            var versions = await store2.GetVersionsBySourceAsync(srcPath);
            Assert.Single(versions);
            var archiver2 = new LosslessArchiver(cfg, store2);
            var restoredPath = Path.Combine(_root, "Recovered.cpp");
            await archiver2.RestoreVersionAsync(versions[0].Id, restoredPath);
            Assert.Equal(usefulBytes, await File.ReadAllBytesAsync(restoredPath));
        }
    }

    [Fact]
    public async Task CorruptedDatabaseAndConfig_RecoverGracefully()
    {
        var cfgPath = Path.Combine(_root, "config.json");
        await File.WriteAllTextAsync(cfgPath, "{ invalid json :::");
        var loadedCfg = GhostCodeConfig.LoadOrDefault(cfgPath);
        Assert.NotEmpty(loadedCfg.WatchedFolders);

        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        await File.WriteAllTextAsync(_dbPath, "NOT A VALID SQLITE FILE HEADER 1234567890");
        await using var store = new SqliteArchiveStore(_dbPath);
        await store.InitializeAsync();
        var stats = await store.GetStatsAsync();
        Assert.Equal(0, stats.TotalVersions);
        Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(_dbPath)!, "*.corrupt.*.bak"));
    }

    [Fact]
    public async Task ConcurrentSavesAndQueueUpdates_LoseZeroRecords()
    {
        var cfg = MakeConfig();
        await using var store = new SqliteArchiveStore(cfg.DatabasePath);
        await store.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, store);

        var tasks = Enumerable.Range(1, 20).Select(async i =>
        {
            var p = Path.Combine(_root, $"Task{i}.cpp");
            var bytes = Encoding.UTF8.GetBytes($"#include <stdio.h>\nint main(){{ printf(\"{i}\"); return 0; }}\n");
            var res = await archiver.ProcessFileSaveAsync(p, bytes, null);
            Assert.Equal(ArchiveOutcome.CreatedVersion, res.Outcome);
            await store.MarkUploadAttemptAsync(res.Version!.Id, UploadStatus.Synced, $"drive_{i}", null, 0);
        });

        await Task.WhenAll(tasks);

        var stats = await store.GetStatsAsync();
        Assert.Equal(20, stats.TotalVersions);
        Assert.Equal(20, stats.SyncedUploads);
        Assert.Equal(0, stats.PendingUploads);
    }

    [Fact]
    public async Task NonUtf8Bytes_ArePreservedByteForByteOnArchiveAndRestore()
    {
        var cfg = MakeConfig();
        await using var store = new SqliteArchiveStore(cfg.DatabasePath);
        await store.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, store);

        var srcPath = Path.Combine(_root, "LegacyAnsi.c");
        // Include Windows-1258/1252 non-UTF8 comment bytes + valid C code
        var prefix = new byte[] { (byte)'/', (byte)'/', (byte)' ', 0xC4, 0xD0, 0xE9, (byte)'\n' };
        var body = Encoding.ASCII.GetBytes("#include <stdio.h>\nint main(){ int x=42; printf(\"%d\", x); return 0; }\n");
        var raw = prefix.Concat(body).ToArray();
        await File.WriteAllBytesAsync(srcPath, raw);

        var res = await archiver.ProcessFileSaveAsync(srcPath, raw, null);
        Assert.Equal(ArchiveOutcome.CreatedVersion, res.Outcome);
        Assert.Equal(raw, await File.ReadAllBytesAsync(res.Version!.ArchivePath));

        var restoreDest = Path.Combine(_root, "RestoredAnsi.c");
        await archiver.RestoreVersionAsync(res.Version.Id, restoreDest);
        Assert.Equal(raw, await File.ReadAllBytesAsync(restoreDest));
        // Original untouched
        Assert.Equal(raw, await File.ReadAllBytesAsync(srcPath));
    }
}
