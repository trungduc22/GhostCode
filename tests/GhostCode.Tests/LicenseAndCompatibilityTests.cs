using System.Security.Cryptography;
using System.Text;
using GhostCode.Core.Models;
using GhostCode.Core.Storage;
using Microsoft.Data.Sqlite;

namespace GhostCode.Tests;

public sealed class LicenseAndCompatibilityTests
{
    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "GhostCode.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Unable to locate repository root containing GhostCode.sln.");
    }

    [Fact]
    public void RepositoryLicenseAndNoticesArePresentAndAccurate()
    {
        var root = LocateRepoRoot();
        var licensePath = Path.Combine(root, "LICENSE");
        var noticesPath = Path.Combine(root, "THIRD_PARTY_NOTICES.md");
        var archPath = Path.Combine(root, "docs", "ARCHITECTURE.md");

        Assert.True(File.Exists(licensePath), "Missing root LICENSE file.");
        Assert.True(File.Exists(noticesPath), "Missing root THIRD_PARTY_NOTICES.md file.");
        Assert.True(File.Exists(archPath), "Missing docs/ARCHITECTURE.md.");

        var licenseText = File.ReadAllText(licensePath);
        Assert.Contains("MIT License", licenseText, StringComparison.Ordinal);
        Assert.Contains("Copyright (c) 2026 trungduc22", licenseText, StringComparison.Ordinal);
        Assert.Contains("Permission is hereby granted, free of charge", licenseText, StringComparison.Ordinal);

        var noticesText = File.ReadAllText(noticesPath);
        Assert.Contains("Microsoft Software License Terms", noticesText, StringComparison.Ordinal);
        Assert.Contains("Apache License 2.0", noticesText, StringComparison.Ordinal);
        Assert.Contains("SQLitePCLRaw.bundle_e_sqlite3", noticesText, StringComparison.Ordinal);

        // Verify every direct PackageReference across GhostCode.sln csproj files appears in THIRD_PARTY_NOTICES.md
        var csprojPaths = new[]
        {
            Path.Combine(root, "src", "GhostCode.Core", "GhostCode.Core.csproj"),
            Path.Combine(root, "src", "GhostCode.App", "GhostCode.App.csproj"),
            Path.Combine(root, "tests", "GhostCode.Tests", "GhostCode.Tests.csproj")
        };

        foreach (var csproj in csprojPaths)
        {
            var xml = System.Xml.Linq.XDocument.Load(csproj);
            var pkgNames = xml.Descendants("PackageReference")
                .Select(el => el.Attribute("Include")?.Value)
                .Where(name => !string.IsNullOrWhiteSpace(name));

            foreach (var pkg in pkgNames)
            {
                Assert.Contains(pkg!, noticesText, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task LegacyDatabaseUpgradesNonDestructivelyAndSupportsTrashRestore()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ghostcode_compat_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var dbPath = Path.Combine(tempDir, "legacy_v1.db");
            var archiveDir = Path.Combine(tempDir, "Archives");
            Directory.CreateDirectory(archiveDir);
            var sampleArchive = Path.Combine(archiveDir, "C04032.cpp");
            var sampleBytes = Encoding.UTF8.GetBytes("#include <iostream>\nint main(){return 0;}\n");
            await File.WriteAllBytesAsync(sampleArchive, sampleBytes);
            var sampleSha = Convert.ToHexStringLower(SHA256.HashData(sampleBytes));

            // Create a pre-Phase-1 legacy schema (no is_deleted/trash columns, no archive_tombstones table)
            await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE file_snapshots (
                        source_path TEXT PRIMARY KEY,
                        sha256 TEXT NOT NULL,
                        norm_hash TEXT NOT NULL,
                        raw_bytes BLOB NOT NULL,
                        encoding_name TEXT NOT NULL,
                        problem_code TEXT NULL,
                        problem_title TEXT NULL,
                        platform TEXT NOT NULL,
                        updated_at INTEGER NOT NULL
                    );
                    CREATE TABLE archive_versions (
                        id TEXT PRIMARY KEY,
                        version_num INTEGER NOT NULL,
                        source_path TEXT NOT NULL,
                        archive_path TEXT NOT NULL,
                        ready_path TEXT NULL,
                        file_name TEXT NOT NULL,
                        sha256 TEXT NOT NULL,
                        norm_hash TEXT NOT NULL,
                        size_bytes INTEGER NOT NULL,
                        problem_code TEXT NULL,
                        problem_title TEXT NULL,
                        platform TEXT NOT NULL,
                        created_at INTEGER NOT NULL
                    );
                    CREATE TABLE upload_queue (
                        version_id TEXT PRIMARY KEY,
                        status TEXT NOT NULL,
                        attempt_count INTEGER NOT NULL DEFAULT 0,
                        next_retry_at INTEGER NOT NULL DEFAULT 0,
                        drive_file_id TEXT NULL,
                        last_error TEXT NULL,
                        updated_at INTEGER NOT NULL
                    );
                    INSERT INTO archive_versions VALUES (
                        'legacy-v1-id', 1, 'C:\\Work\\main.cpp', $ap, NULL, 'C04032.cpp',
                        $sha, $sha, $sz, 'C04032', 'Demo Problem', 'Ptit', 1700000000000
                    );
                    INSERT INTO upload_queue VALUES ('legacy-v1-id', 'Synced', 0, 0, 'drive-file-99', NULL, 1700000001000);
                    """;
                cmd.Parameters.AddWithValue("$ap", sampleArchive);
                cmd.Parameters.AddWithValue("$sha", sampleSha);
                cmd.Parameters.AddWithValue("$sz", sampleBytes.Length);
                await cmd.ExecuteNonQueryAsync();
            }

            await using var store = new SqliteArchiveStore(dbPath);
            await store.InitializeAsync();

            var health = await store.CheckDatabaseHealthAsync();
            Assert.True(health.Healthy, health.Detail);

            var existing = await store.QueryVersionsAsync(limit: 50);
            Assert.Single(existing);
            Assert.Equal("legacy-v1-id", existing[0].Id);

            // Verify Trash -> Restore preserves legacy record & archive file cleanly
            var cfg = GhostCodeConfig.CreateDefault(tempDir, archiveDir);
            cfg.ArchiveDir = archiveDir;
            cfg.DatabasePath = dbPath;
            var trashRes = await store.MoveVersionToTrashAsync(
                existing[0].Id,
                cfg.GetInternalTrashRoot(),
                cfg.ArchiveDir,
                deleteRemote: false,
                retentionDays: 30);
            Assert.True(trashRes.Success);
            Assert.Empty(await store.QueryVersionsAsync(limit: 50));

            var restored = await store.RestoreTrashItemAsync("legacy-v1-id");
            Assert.True(restored);
            var afterRestore = await store.QueryVersionsAsync(limit: 50);
            Assert.Single(afterRestore);
            var queueAfterRestore = await store.GetAllUploadQueueAsync(limit: 50);
            Assert.Single(queueAfterRestore);
            Assert.Equal("drive-file-99", queueAfterRestore[0].Queue.DriveFileId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
