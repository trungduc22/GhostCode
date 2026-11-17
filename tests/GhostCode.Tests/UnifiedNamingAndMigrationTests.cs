using System.Text;
using GhostCode.Core.Analysis;
using GhostCode.Core.Archiver;
using GhostCode.Core.Models;
using GhostCode.Core.Storage;
using Xunit;

namespace GhostCode.Tests;

public sealed class UnifiedNamingAndMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"GhostCode_M1_{Guid.NewGuid():N}");

    public UnifiedNamingAndMigrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void P0_P1_CleanFileNamingAndPlatformFolders_MatchRequiredExamples()
    {
        var ptitCtx = new ProblemContext(OjPlatform.Ptit, "C04032", "Liệt kê và đếm (DLab)", "https://code.ptit.edu.vn/student/question/C04032", "Thorium", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var cContent = "#include <stdio.h>\nint main(){ printf(\"ok\"); return 0; }\n";
        Assert.Equal("C04032_Liet_Ke_Va_Dem_DLab.c", SourceAnalyzer.BuildCleanFileName(ptitCtx, "Untitled1.cpp", cContent, SourceLanguage.Cpp));
        Assert.Equal("ptitcode", SourceAnalyzer.GetCategoryFolder(ptitCtx));

        var cfCtx = new ProblemContext(OjPlatform.Codeforces, "CF_1872A", "Two Vessels", "https://codeforces.com/problemset/problem/1872/A", "Edge", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var cppContent = "#include <iostream>\nint main(){ std::cout << 1; return 0; }\n";
        Assert.Equal("CF_1872A_Two_Vessels.cpp", SourceAnalyzer.BuildCleanFileName(cfCtx, "sol.cpp", cppContent, SourceLanguage.Cpp));
        Assert.Equal("codeforces", SourceAnalyzer.GetCategoryFolder(cfCtx));

        Assert.Equal("vnoj", SourceAnalyzer.GetCategoryFolderForPlatform("Vnoj"));
        Assert.Equal("leetcode", SourceAnalyzer.GetCategoryFolderForPlatform("LeetCode"));
        Assert.Equal("hackerrank", SourceAnalyzer.GetCategoryFolderForPlatform("HackerRank"));
        Assert.Equal("offline", SourceAnalyzer.GetCategoryFolder(null));
    }

    [Fact]
    public async Task P1_ReusedUntitled1AcrossMultipleProblemsAndOffline_KeepsIndependentHistoriesAndCleanPaths()
    {
        var docsDir = Path.Combine(_root, "Docs");
        var watchDir = Path.Combine(_root, "Desktop");
        Directory.CreateDirectory(docsDir);
        Directory.CreateDirectory(watchDir);

        var cfg = new GhostCodeConfig
        {
            ArchiveDir = Path.Combine(docsDir, "GhostCode"),
            ReadyDir = string.Empty,
            DatabasePath = Path.Combine(_root, "ghostcode.db"),
            WatchedFolders = new List<WatchedFolderConfig> { new(watchDir, true, true, "Desktop") }
        };

        await using var store = new SqliteArchiveStore(cfg.DatabasePath);
        await store.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, store);

        var untitled = Path.Combine(watchDir, "Untitled1.cpp");
        var ctxC04032 = new ProblemContext(OjPlatform.Ptit, "C04032", "Liệt kê và đếm (DLab)", "https://code.ptit.edu.vn/student/question/C04032", "Thorium", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var ctxCpp0101 = new ProblemContext(OjPlatform.Ptit, "CPP0101", "Tính tổng hai số", "https://code.ptit.edu.vn/student/question/CPP0101", "Thorium", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var v1 = await archiver.ProcessFileSaveAsync(untitled, Encoding.UTF8.GetBytes("#include <stdio.h>\nint main(){ printf(\"c1\"); }\n"), ctxC04032);
        var v2 = await archiver.ProcessFileSaveAsync(untitled, Encoding.UTF8.GetBytes("#include <stdio.h>\nint main(){ printf(\"c2\"); }\n"), ctxC04032);
        var cppV1 = await archiver.ProcessFileSaveAsync(untitled, Encoding.UTF8.GetBytes("#include <iostream>\nint main(){ int a,b; std::cin>>a>>b; std::cout<<a+b; }\n"), ctxCpp0101);
        var offlineV1 = await archiver.ProcessFileSaveAsync(untitled, Encoding.UTF8.GetBytes("#include <iostream>\nint main(){ std::cout<<\"scratch\"; }\n"), null);

        Assert.Equal("C04032_Liet_Ke_Va_Dem_DLab.c", v1.Version!.FileName);
        Assert.Equal("C04032_Liet_Ke_Va_Dem_DLab.c", v2.Version!.FileName);
        Assert.Equal(1, v1.Version.VersionNum);
        Assert.Equal(2, v2.Version.VersionNum);
        Assert.NotEqual(v1.Version.ArchivePath, v2.Version.ArchivePath);
        Assert.True(File.Exists(v1.Version.ArchivePath));
        Assert.True(File.Exists(v2.Version.ArchivePath));
        Assert.Contains(".ghostcode", v1.Version.ArchivePath);
        Assert.Equal(Path.Combine(cfg.ArchiveDir, "ptitcode", "C04032_Liet_Ke_Va_Dem_DLab.c"), v2.Version.ReadyPath);
        Assert.True(File.Exists(v2.Version.ReadyPath!));

        // Independent CPP0101 chain (starts at v1, does not collide with C04032)
        Assert.Equal(1, cppV1.Version!.VersionNum);
        Assert.Equal("CPP0101_Tinh_Tong_Hai_So.cpp", cppV1.Version.FileName);

        // Offline chain starts cleanly under offline/
        Assert.Equal(1, offlineV1.Version!.VersionNum);
        Assert.Contains(Path.Combine("GhostCode", "offline"), offlineV1.Version.ReadyPath!);

        var c04032History = await store.GetVersionsByProblemAsync("Ptit", "C04032");
        Assert.Equal(2, c04032History.Count);
        var offlineHistory = await store.GetOfflineVersionsBySourceAsync(untitled);
        Assert.Single(offlineHistory);
    }

    [Fact]
    public async Task P1_MigrationPreservesExistingLegacyVersions_CloudIds_AndOriginals_WithSha256Verification()
    {
        var docsDir = Path.Combine(_root, "Documents");
        var legacyArchiveDir = Path.Combine(docsDir, "GhostCodeArchive");
        var legacyReadyDir = Path.Combine(docsDir, "CodePTIT_Ready");
        var legacyHistory = Path.Combine(legacyArchiveDir, "History", "2026-11-17");
        Directory.CreateDirectory(legacyHistory);
        Directory.CreateDirectory(legacyReadyDir);

        var bytesV1 = Encoding.UTF8.GetBytes("#include <stdio.h>\nint main(){ puts(\"v1\"); return 0; }\n");
        var bytesV2 = Encoding.UTF8.GetBytes("#include <stdio.h>\nint main(){ puts(\"v2\"); return 0; }\n");
        var shaV1 = SourceAnalyzer.ComputeSha256Hex(bytesV1);
        var shaV2 = SourceAnalyzer.ComputeSha256Hex(bytesV2);

        var oldPathV1 = Path.Combine(legacyHistory, $"C04032_Liet_Ke_Va_Dem_Dlab_v1_{shaV1[..8]}.c");
        var oldPathV2 = Path.Combine(legacyHistory, $"C04032_Liet_Ke_Va_Dem_Dlab_v2_{shaV2[..8]}.c");
        var oldReady = Path.Combine(legacyReadyDir, "C04032_Liet_Ke_Va_Dem_Dlab.c");
        await File.WriteAllBytesAsync(oldPathV1, bytesV1);
        await File.WriteAllBytesAsync(oldPathV2, bytesV2);
        await File.WriteAllBytesAsync(oldReady, bytesV2);

        var cfg = new GhostCodeConfig
        {
            ArchiveDir = legacyArchiveDir,
            ReadyDir = legacyReadyDir,
            DatabasePath = Path.Combine(_root, "ghostcode.db")
        };
        var cfgPath = Path.Combine(_root, "config.json");
        cfg.Save(cfgPath);

        await using var store = new SqliteArchiveStore(cfg.DatabasePath);
        await store.InitializeAsync();

        var snap = new FileSnapshotRecord("C:\\src\\Untitled1.cpp", shaV2, SourceAnalyzer.ComputeNormalizedHash("v2"), bytesV2, "UTF-8", "C04032", "Liệt kê và đếm (DLab)", "Ptit", 1000);
        var ver1 = new ArchiveVersionRecord("id_v1", 1, "C:\\src\\Untitled1.cpp", oldPathV1, oldReady, Path.GetFileName(oldPathV1), shaV1, "n1", bytesV1.Length, "C04032", "Liệt kê và đếm (DLab)", "Ptit", 1000, true, "drive_id_v1");
        var ver2 = new ArchiveVersionRecord("id_v2", 2, "C:\\src\\Untitled1.cpp", oldPathV2, oldReady, Path.GetFileName(oldPathV2), shaV2, "n2", bytesV2.Length, "C04032", "Liệt kê và đếm (DLab)", "Ptit", 2000, true, "drive_id_v2");
        await store.InsertVersionAndEnqueueAsync(snap, ver1, new UploadQueueItem("id_v1", UploadStatus.Synced, 1, 0, "drive_id_v1", null, 1000));
        await store.InsertVersionAndEnqueueAsync(snap, ver2, new UploadQueueItem("id_v2", UploadStatus.Synced, 1, 0, "drive_id_v2", null, 2000));

        var report = await UnifiedArchiveMigrationService.MigrateIfNeededAsync(cfg, cfgPath, store);
        Assert.True(report.Executed);
        Assert.Equal(2, report.MigratedVersionsCount);
        Assert.Equal(2, report.VerifiedFilesCount);
        Assert.Equal(0, report.MissingSourceCount);

        var unifiedRoot = Path.Combine(docsDir, "GhostCode");
        Assert.Equal(unifiedRoot, cfg.ArchiveDir);
        Assert.Equal(string.Empty, cfg.ReadyDir);
        Assert.True(File.Exists(Path.Combine(unifiedRoot, "ptitcode", "C04032_Liet_Ke_Va_Dem_DLab.c")));

        // Verify originals preserved non-destructively
        Assert.True(File.Exists(oldPathV1));
        Assert.True(File.Exists(oldPathV2));
        Assert.True(File.Exists(oldReady));

        // Verify database paths, clean filenames, and Drive IDs preserved
        var migratedV1 = await store.GetVersionByIdAsync("id_v1");
        var migratedV2 = await store.GetVersionByIdAsync("id_v2");
        Assert.Equal("C04032_Liet_Ke_Va_Dem_DLab.c", migratedV1!.FileName);
        Assert.Equal("C04032_Liet_Ke_Va_Dem_DLab.c", migratedV2!.FileName);
        Assert.Equal("drive_id_v1", migratedV1.DriveFileId);
        Assert.Equal("drive_id_v2", migratedV2.DriveFileId);
        Assert.Equal(bytesV1, await File.ReadAllBytesAsync(migratedV1.ArchivePath));
        Assert.Equal(bytesV2, await File.ReadAllBytesAsync(migratedV2.ArchivePath));

        // Second migration pass must be an idempotent no-op
        var secondPass = await UnifiedArchiveMigrationService.MigrateIfNeededAsync(cfg, cfgPath, store);
        Assert.False(secondPass.Executed);
    }
}
