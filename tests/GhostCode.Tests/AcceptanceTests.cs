using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GhostCode.Core.Archiver;
using GhostCode.Core.Browser;
using GhostCode.Core.Cloud;
using GhostCode.Core.Models;
using GhostCode.Core.Monitoring;
using GhostCode.Core.Storage;
using Microsoft.Data.Sqlite;

namespace GhostCode.Tests;

public sealed class AcceptanceTests : IDisposable
{
    private readonly string _root;

    public AcceptanceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GhostCode_Accept_" + Guid.NewGuid().ToString("N"));
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
        var desktop = Path.Combine(baseDir, "Desktop");
        var docs = Path.Combine(baseDir, "Documents");
        var archive = Path.Combine(baseDir, "CodeArchive");
        var ready = Path.Combine(baseDir, "CodePTIT_Ready");
        Directory.CreateDirectory(desktop);
        Directory.CreateDirectory(docs);
        Directory.CreateDirectory(archive);
        Directory.CreateDirectory(ready);

        return new GhostCodeConfig
        {
            WatchedFolders = new List<WatchedFolderConfig>
            {
                new(desktop, true, true, "Desktop"),
                new(docs, true, true, "Documents")
            },
            ArchiveDir = archive,
            ReadyDir = ready,
            DatabasePath = Path.Combine(archive, "ghostcode.db"),
            BridgeAuthToken = "accept-token-" + Guid.NewGuid().ToString("N"),
            HttpPort = 0,
            OAuthClient = new GoogleOAuthClientConfig("client-123.apps.googleusercontent.com", "secret-456")
        };
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, int timeoutMs = 6000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (await predicate())
                return;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Condition was not met within {timeoutMs} ms.");
    }

    private sealed class MockDriveHttpHandler : HttpMessageHandler
    {
        public bool FailUploads { get; set; }
        public int UploadCalls { get; private set; }
        public int RevokeCalls { get; private set; }
        public HashSet<string> RemoteSha256s { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            if (url.StartsWith("https://oauth2.googleapis.com/token", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"acc_pkce","refresh_token":"ref_pkce","expires_in":3600}""", Encoding.UTF8, "application/json")
                });
            }

            if (url.StartsWith("https://oauth2.googleapis.com/revoke", StringComparison.Ordinal))
            {
                RevokeCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            if (url.StartsWith("https://www.googleapis.com/oauth2/v2/userinfo", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"email":"duc@ptit.edu.vn","name":"Duc"}""", Encoding.UTF8, "application/json")
                });
            }

            if (url.StartsWith("https://www.googleapis.com/drive/v3/files", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
            {
                if (url.Contains("mimeType", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"files":[{"id":"folder_ca_1","name":"CodeArchive"}]}""", Encoding.UTF8, "application/json")
                    });
                }

                foreach (var sha in RemoteSha256s)
                {
                    if (url.Contains(sha, StringComparison.Ordinal))
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent($$"""{"files":[{"id":"remote_{{sha[..8]}}","name":"existing"}]}""", Encoding.UTF8, "application/json")
                        });
                    }
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"files":[]}""", Encoding.UTF8, "application/json")
                });
            }

            if (url.StartsWith("https://www.googleapis.com/upload/drive/v3/files", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                if (FailUploads)
                    throw new HttpRequestException("Simulated network drop");

                UploadCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""{"id":"drive_id_{{UploadCalls}}","name":"file"}""", Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    // 1. Mandatory real-world Dev-C++ Untitled1.cpp Desktop scenario
    [Fact]
    public async Task Scenario01_RealWorld_DevCpp_Untitled1_Desktop_SaveEditCompileClearRewrite()
    {
        var cfg = MakeConfig();
        await using var db = new SqliteArchiveStore(cfg.DatabasePath);
        await db.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, db);
        await using var monitor = new MultiDirectoryMonitor(cfg, db, archiver, () => null);
        monitor.Start();

        var desktop = cfg.WatchedFolders[0].Path;
        var untitled = Path.Combine(desktop, "Untitled1.cpp");
        var exe = Path.Combine(desktop, "Untitled1.exe");

        var sol1 = "#include <iostream>\nusing namespace std;\nint main(){ cout << 42; return 0; }\n";
        await File.WriteAllTextAsync(untitled, sol1);
        await WaitUntilAsync(async () => (await db.GetVersionsBySourceAsync(untitled)).Count == 1);

        // Simulate compile F11 creating .exe alongside Untitled1.cpp
        await File.WriteAllBytesAsync(exe, new byte[] { 0x4D, 0x5A });
        await Task.Delay(350);

        // Clear/reset to empty template -> MUST NOT wipe v1
        await File.WriteAllTextAsync(untitled, "int main() { return 0; }");
        await Task.Delay(500);
        Assert.Single(await db.GetVersionsBySourceAsync(untitled));

        // Write second distinct solution -> preserved as v2 without overwriting v1
        var sol2 = "#include <bits/stdc++.h>\nusing namespace std;\nint main(){ long long n; cin >> n; cout << n * 2; }\n";
        await File.WriteAllTextAsync(untitled, sol2);
        await WaitUntilAsync(async () => (await db.GetVersionsBySourceAsync(untitled)).Count == 2);

        var versions = await db.GetVersionsBySourceAsync(untitled);
        var v1 = versions.Single(v => v.VersionNum == 1);
        var v2 = versions.Single(v => v.VersionNum == 2);
        Assert.Equal(sol1, await File.ReadAllTextAsync(v1.ArchivePath));
        Assert.Equal(sol2, await File.ReadAllTextAsync(v2.ArchivePath));
    }

    // 2. OneDrive-redirected Desktop
    [Fact]
    public async Task Scenario02_OneDriveRedirectedDesktop_DiscoveredAndMonitored()
    {
        var fakeUser = Path.Combine(_root, "Users", "SinhVienPTIT");
        var oneDriveDesktop = Path.Combine(fakeUser, "OneDrive", "Desktop");
        var oneDriveDocs = Path.Combine(fakeUser, "OneDrive", "Documents");
        Directory.CreateDirectory(oneDriveDesktop);
        Directory.CreateDirectory(oneDriveDocs);

        var discovered = KnownFoldersResolver.DiscoverDefaultWatchedFolders(fakeUser);
        Assert.Contains(discovered, f => string.Equals(f.Path, oneDriveDesktop, StringComparison.OrdinalIgnoreCase));

        var cfg = new GhostCodeConfig
        {
            WatchedFolders = discovered,
            ArchiveDir = Path.Combine(_root, "CodeArchive"),
            ReadyDir = Path.Combine(_root, "Ready"),
            DatabasePath = Path.Combine(_root, "CodeArchive", "ghostcode.db")
        };

        await using var db = new SqliteArchiveStore(cfg.DatabasePath);
        await db.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, db);
        await using var monitor = new MultiDirectoryMonitor(cfg, db, archiver, () => null);
        monitor.Start();

        var file = Path.Combine(oneDriveDesktop, "onedrive_sol.py");
        await File.WriteAllTextAsync(file, "def solve():\n    print(sum(map(int, input().split())))\nsolve()\n");
        await WaitUntilAsync(async () => (await db.GetVersionsBySourceAsync(file)).Count == 1);
    }

    // 3. Nested subdirectories in Documents
    [Fact]
    public async Task Scenario03_NestedSubdirectoriesInDocuments_MonitoredRecursively()
    {
        var cfg = MakeConfig();
        await using var db = new SqliteArchiveStore(cfg.DatabasePath);
        await db.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, db);
        await using var monitor = new MultiDirectoryMonitor(cfg, db, archiver, () => null);
        monitor.Start();

        var nested = Path.Combine(cfg.WatchedFolders[1].Path, "Algorithms", "Graph", "dijkstra.cpp");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        await File.WriteAllTextAsync(nested, "#include <vector>\nint dist[1005];\nint main(){ dist[0] = 1; return 0; }\n");

        await WaitUntilAsync(async () => (await db.GetVersionsBySourceAsync(nested)).Count == 1);
    }

    // 4. Custom user-selected directory + persistence across restart
    [Fact]
    public async Task Scenario04_CustomUserSelectedDirectory_MonitorsAndPersistsAcrossRestart()
    {
        var cfg = MakeConfig();
        var customDir = Path.Combine(_root, "CustomCP", "Round999");
        Directory.CreateDirectory(customDir);
        cfg.WatchedFolders.Add(new WatchedFolderConfig(customDir, Enabled: true, Recursive: true, Label: "Round999"));

        var cfgPath = Path.Combine(_root, "config.json");
        cfg.Save(cfgPath);

        var reloaded = GhostCodeConfig.LoadOrDefault(cfgPath);
        Assert.Contains(reloaded.WatchedFolders, f => string.Equals(f.Path, customDir, StringComparison.OrdinalIgnoreCase));

        await using var db = new SqliteArchiveStore(reloaded.DatabasePath);
        await db.InitializeAsync();
        var archiver = new LosslessArchiver(reloaded, db);
        await using var monitor = new MultiDirectoryMonitor(reloaded, db, archiver, () => null);
        monitor.Start();

        var p = Path.Combine(customDir, "B.cpp");
        await File.WriteAllTextAsync(p, "#include <iostream>\nint main(){ std::cout << \"B\"; return 0; }\n");
        await WaitUntilAsync(async () => (await db.GetVersionsBySourceAsync(p)).Count == 1);
    }

    // 5. No browser open -> still hot-backups eligible files
    [Fact]
    public async Task Scenario05_NoBrowserOpen_StillHotBackupsEligibleSourceFiles()
    {
        var cfg = MakeConfig();
        await using var db = new SqliteArchiveStore(cfg.DatabasePath);
        await db.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, db);
        await using var monitor = new MultiDirectoryMonitor(cfg, db, archiver, () => null);
        monitor.Start();

        var docs = cfg.WatchedFolders[1].Path;
        var fC = Path.Combine(docs, "offline_test.c");
        var fPy = Path.Combine(docs, "offline_test.py");
        await File.WriteAllTextAsync(fC, "#include <stdio.h>\nint main(){ printf(\"offline\"); return 0; }\n");
        await File.WriteAllTextAsync(fPy, "for i in range(5):\n    print(i * i)\n");

        await WaitUntilAsync(async () =>
            (await db.GetVersionsBySourceAsync(fC)).Count == 1 &&
            (await db.GetVersionsBySourceAsync(fPy)).Count == 1);
    }

    // 6. Atomic editor saves + repeated edits + deduplication
    [Fact]
    public async Task Scenario06_AtomicEditorSavesAndRepeatedEdits_DebouncedAndDeduplicated()
    {
        var cfg = MakeConfig();
        await using var db = new SqliteArchiveStore(cfg.DatabasePath);
        await db.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, db);
        await using var monitor = new MultiDirectoryMonitor(cfg, db, archiver, () => null);
        monitor.Start();

        var docs = cfg.WatchedFolders[1].Path;
        var target = Path.Combine(docs, "atomic_edit.cpp");
        var tmp = Path.Combine(docs, "atomic_edit.cpp.tmp123");
        var body1 = "#include <iostream>\nint main(){ std::cout << 100; }\n";

        await File.WriteAllTextAsync(tmp, body1);
        await MoveWithRetryAsync(tmp, target);
        await WaitUntilAsync(async () => (await db.GetVersionsBySourceAsync(target)).Count == 1);

        // Identical rewrite -> deduplicated, still 1 version
        await File.WriteAllTextAsync(target, body1);
        await WaitUntilAsync(async () => (await db.GetRecentMonitorEventsAsync(10)).Count >= 2);
        Assert.Single(await db.GetVersionsBySourceAsync(target));

        // Real modification via atomic rename -> v2
        var body2 = "#include <iostream>\nint main(){ std::cout << 200; }\n";
        await File.WriteAllTextAsync(tmp, body2);
        await MoveWithRetryAsync(tmp, target);
        await WaitUntilAsync(async () => (await db.GetVersionsBySourceAsync(target)).Count == 2);
    }

    private static async Task MoveWithRetryAsync(string source, string dest)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                File.Move(source, dest, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 7 && (ex is IOException || ex is UnauthorizedAccessException))
            {
                await Task.Delay(50 * (attempt + 1));
            }
        }
    }

    // 7. Solution replacement with >50% similarity never overwrites earlier version
    [Fact]
    public async Task Scenario07_SolutionReplacement_GreaterThan50PercentSimilarity_NeverOverwritesOldVersion()
    {
        var cfg = MakeConfig();
        await using var db = new SqliteArchiveStore(cfg.DatabasePath);
        await db.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, db);

        var src = Path.Combine(cfg.WatchedFolders[0].Path, "similar.cpp");
        var header = "#include <bits/stdc++.h>\nusing namespace std;\nint main() {\n    ios::sync_with_stdio(false);\n    cin.tie(nullptr);\n    int n; cin >> n;\n";
        var v1Bytes = Encoding.UTF8.GetBytes(header + "    cout << n + 1 << '\\n';\n    return 0;\n}\n");
        var v2Bytes = Encoding.UTF8.GetBytes(header + "    cout << n * 2 << '\\n';\n    return 0;\n}\n");

        var r1 = await archiver.ProcessFileSaveAsync(src, v1Bytes, null);
        var r2 = await archiver.ProcessFileSaveAsync(src, v2Bytes, null);

        Assert.Equal(ArchiveOutcome.CreatedVersion, r1.Outcome);
        Assert.Equal(ArchiveOutcome.CreatedVersion, r2.Outcome);
        Assert.NotEqual(r1.Version!.ArchivePath, r2.Version!.ArchivePath);
        Assert.Equal(v1Bytes, await File.ReadAllBytesAsync(r1.Version.ArchivePath));
        Assert.Equal(v2Bytes, await File.ReadAllBytesAsync(r2.Version.ArchivePath));
    }

    // 8. Empty or template reset preserves last known good snapshot
    [Fact]
    public async Task Scenario08_EmptyOrTemplateReset_PreservesLastKnownGoodSnapshot()
    {
        var cfg = MakeConfig();
        await using var db = new SqliteArchiveStore(cfg.DatabasePath);
        await db.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, db);

        var src = Path.Combine(cfg.WatchedFolders[0].Path, "reset_guard.cpp");
        var good = Encoding.UTF8.GetBytes("#include <stdio.h>\nint main(){ int a=5,b=7; printf(\"%d\", a+b); }\n");
        await archiver.ProcessFileSaveAsync(src, good, null);

        var emptyRes = await archiver.ProcessFileSaveAsync(src, Array.Empty<byte>(), null);
        var tplRes = await archiver.ProcessFileSaveAsync(src, Encoding.UTF8.GetBytes("int main() { return 0; }"), null);

        Assert.Equal(ArchiveOutcome.PreservedPreviousOnReset, emptyRes.Outcome);
        Assert.Equal(ArchiveOutcome.PreservedPreviousOnReset, tplRes.Outcome);
        var snap = await db.GetSnapshotAsync(src);
        Assert.NotNull(snap);
        Assert.Equal(good, snap!.RawBytes);
    }

    // 9. App restart between edits restores snapshot state cleanly
    [Fact]
    public async Task Scenario09_AppRestartBetweenEdits_RestoresSnapshotStateFromSqlite()
    {
        var cfg = MakeConfig();
        var src = Path.Combine(cfg.WatchedFolders[0].Path, "restart.cpp");

        await using (var db1 = new SqliteArchiveStore(cfg.DatabasePath))
        {
            await db1.InitializeAsync();
            var a1 = new LosslessArchiver(cfg, db1);
            var r1 = await a1.ProcessFileSaveAsync(src, Encoding.UTF8.GetBytes("#include <stdio.h>\nint main(){ puts(\"run1\"); }\n"), null);
            Assert.Equal(1, r1.Version!.VersionNum);
        }

        // Simulate full app restart with new SqliteArchiveStore & LosslessArchiver instances
        await using var db2 = new SqliteArchiveStore(cfg.DatabasePath);
        await db2.InitializeAsync();
        var a2 = new LosslessArchiver(cfg, db2);
        var r2 = await a2.ProcessFileSaveAsync(src, Encoding.UTF8.GetBytes("#include <stdio.h>\nint main(){ puts(\"run2\"); }\n"), null);
        Assert.Equal(2, r2.Version!.VersionNum);
        Assert.Equal(2, (await db2.GetVersionsBySourceAsync(src)).Count);
    }

    // 10. Concurrent saves and Drive sync remain thread-safe
    [Fact]
    public async Task Scenario10_ConcurrentSavesAndDriveSync_ThreadSafeUnderParallelLoad()
    {
        var cfg = MakeConfig();
        await using var db = new SqliteArchiveStore(cfg.DatabasePath);
        await db.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, db);
        var creds = new DpapiCredentialStore(Path.Combine(_root, "creds.dpapi"));
        await creds.SaveAsync(new GoogleAuthTokens("t", "r", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(), "u@e.com", "User"));
        var handler = new MockDriveHttpHandler();
        await using var drive = new GoogleDriveService(cfg, db, creds, new HttpClient(handler));

        var docs = cfg.WatchedFolders[1].Path;
        var tasks = Enumerable.Range(1, 10).Select(i => Task.Run(async () =>
        {
            var path = Path.Combine(docs, $"concurrent_{i}.cpp");
            var bytes = Encoding.UTF8.GetBytes($"#include <iostream>\nint main(){{ std::cout << {i * 17}; return 0; }}\n");
            await archiver.ProcessFileSaveAsync(path, bytes, null);
        })).ToList();

        tasks.Add(drive.SyncPendingAsync());
        await Task.WhenAll(tasks);

        // Drain any versions added after concurrent sync call started
        var finalSync = await drive.SyncPendingAsync();
        Assert.Equal(0, finalSync.FailedCount);
        Assert.Equal(10, (await db.GetStatsAsync()).TotalVersions);
        Assert.Equal(10, (await db.GetStatsAsync()).SyncedUploads);
    }

    // 11. Network failure recovery + exponential backoff + remote SHA dedup
    [Fact]
    public async Task Scenario11_DriveNetworkOutage_ExponentialBackoffRecoveryAndRemoteShaDedup()
    {
        var cfg = MakeConfig();
        await using var db = new SqliteArchiveStore(cfg.DatabasePath);
        await db.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, db);
        var creds = new DpapiCredentialStore(Path.Combine(_root, "creds.dpapi"));
        await creds.SaveAsync(new GoogleAuthTokens("t", "r", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(), "u@e.com", "User"));
        var handler = new MockDriveHttpHandler { FailUploads = true };
        await using var drive = new GoogleDriveService(cfg, db, creds, new HttpClient(handler));

        var src = Path.Combine(cfg.WatchedFolders[0].Path, "outage.cpp");
        var res = await archiver.ProcessFileSaveAsync(src, Encoding.UTF8.GetBytes("#include <iostream>\nint main(){ return 123; }\n"), null);

        var rep1 = await drive.SyncPendingAsync();
        Assert.Equal(1, rep1.FailedCount);
        var q1 = (await db.GetAllUploadQueueAsync()).Single();
        Assert.Equal(UploadStatus.Failed, q1.Queue.Status);
        Assert.True(q1.Queue.NextRetryAtUnixMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        // Crash recovery: file already exists remotely by SHA256 -> deduplicated without re-upload
        handler.FailUploads = false;
        handler.RemoteSha256s.Add(res.Version!.Sha256);
        await db.ResetFailedUploadsForRetryAsync();
        var rep2 = await drive.SyncPendingAsync();
        Assert.Equal(1, rep2.SkippedDuplicateCount);
        Assert.Equal(0, handler.UploadCalls);
    }

    // 12. Graceful cancellation / shutdown leaves zero temp corruption
    [Fact]
    public async Task Scenario12_GracefulCancellationAndShutdown_ZeroTempFileCorruption()
    {
        var cfg = MakeConfig();
        await using var db = new SqliteArchiveStore(cfg.DatabasePath);
        await db.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, db);
        var monitor = new MultiDirectoryMonitor(cfg, db, archiver, () => null);
        monitor.Start();

        var src = Path.Combine(cfg.WatchedFolders[0].Path, "shutdown.cpp");
        await File.WriteAllTextAsync(src, "#include <iostream>\nint main(){ std::cout << 999; }\n");
        await WaitUntilAsync(async () => (await db.GetVersionsBySourceAsync(src)).Count == 1);

        await monitor.StopAsync();

        var temps = Directory.GetFiles(cfg.ArchiveDir, "*.tmp*", SearchOption.AllDirectories);
        Assert.Empty(temps);

        await using var checkConn = new SqliteConnection($"Data Source={cfg.DatabasePath}");
        await checkConn.OpenAsync();
        await using var cmd = checkConn.CreateCommand();
        cmd.CommandText = "PRAGMA quick_check;";
        Assert.Equal("ok", (await cmd.ExecuteScalarAsync())?.ToString());
    }

    // 13. Corrupted DB / config.json recovery
    [Fact]
    public async Task Scenario13_CorruptedDatabaseAndConfigJson_AutoRecoveredCleanly()
    {
        var cfgPath = Path.Combine(_root, "config.json");
        await File.WriteAllTextAsync(cfgPath, "{ invalid json !!!");
        var cfg = GhostCodeConfig.LoadOrDefault(cfgPath, userProfileOverride: _root, localAppDataOverride: _root);
        Assert.NotNull(cfg);
        Assert.Single(Directory.GetFiles(_root, "config.json.corrupt.*.bak"));

        Directory.CreateDirectory(Path.GetDirectoryName(cfg.DatabasePath)!);
        await File.WriteAllTextAsync(cfg.DatabasePath, "CORRUPT_SQLITE_HEADER_GARBAGE");
        await using var db = new SqliteArchiveStore(cfg.DatabasePath);
        await db.InitializeAsync();

        var dbDir = Path.GetDirectoryName(cfg.DatabasePath)!;
        Assert.Single(Directory.GetFiles(dbDir, "*.corrupt.*.bak"));
        Assert.Equal(0, (await db.GetStatsAsync()).TotalVersions);
    }

    // 14. Multiple browser tabs + background rejection + TTL expiration
    [Fact]
    public async Task Scenario14_MultipleBrowserTabs_BackgroundIgnoredAndTtlExpiresStaleContext()
    {
        var cfg = new GhostCodeConfig
        {
            HttpPort = 0,
            ContextExpirationSeconds = 1,
            BridgeAuthToken = "tab-secret-123"
        };
        await using var bridge = new AuthenticatedBrowserBridge(cfg);
        bridge.Start();
        var baseUrl = $"http://127.0.0.1:{bridge.ListeningPort}";
        using var http = new HttpClient();

        var nonce = Guid.NewGuid().ToString("N");
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var hsReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/handshake");
        hsReq.Headers.Add("Origin", "chrome-extension://bifmfiolmohcdfknnicfikhhpipkokbc");
        hsReq.Headers.Add("X-GhostCode-Pairing-Id", AuthenticatedBrowserBridge.DefaultPairingId);
        hsReq.Headers.Add("X-GhostCode-Nonce", nonce);
        hsReq.Headers.Add("X-GhostCode-Timestamp", ts.ToString());
        hsReq.Headers.Add("X-GhostCode-Proof", AuthenticatedBrowserBridge.ComputeHandshakeProof(cfg.BridgeAuthToken, nonce, ts));
        var hsResp = await http.SendAsync(hsReq);
        Assert.Equal(HttpStatusCode.OK, hsResp.StatusCode);
        using var hsDoc = JsonDocument.Parse(await hsResp.Content.ReadAsStringAsync());
        var sessionToken = hsDoc.RootElement.GetProperty("session_token").GetString()!;

        // Active tab PTIT
        var req1 = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/context");
        req1.Headers.Add("X-GhostCode-Token", sessionToken);
        req1.Content = new StringContent("""{"problem_code":"C04015","problem_title":"Dem So Lan","platform":"PTIT","url":"https://code.ptit.edu.vn/student/question/C04015","tab_active":true}""", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(req1)).StatusCode);

        // Background tab Codeforces must NOT overwrite active PTIT context
        var req2 = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/context");
        req2.Headers.Add("X-GhostCode-Token", sessionToken);
        req2.Content = new StringContent("""{"problem_code":"CF_1A","problem_title":"Theatre Square","platform":"Codeforces","url":"https://codeforces.com/problemset/problem/1/A","tab_active":false}""", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Accepted, (await http.SendAsync(req2)).StatusCode);
        Assert.Equal("C04015", bridge.GetActiveContext()!.Code);

        // Wait past 1s TTL -> stale context expires cleanly
        await Task.Delay(1300);
        Assert.Null(bridge.GetActiveContext());
    }

    // 15. End-to-end Google Drive PKCE connect + DPAPI + upload + disconnect
    [Fact]
    public async Task Scenario15_GoogleDriveOAuthDpapi_EncryptsTokensConnectsAndDisconnects()
    {
        var cfg = MakeConfig();
        await using var db = new SqliteArchiveStore(cfg.DatabasePath);
        await db.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, db);
        var creds = new DpapiCredentialStore(Path.Combine(_root, "creds.dpapi"));
        var handler = new MockDriveHttpHandler();
        await using var drive = new GoogleDriveService(cfg, db, creds, new HttpClient(handler));

        // Simulate user approving PKCE OAuth prompt in browser -> loopback callback
        var tokens = await drive.ConnectWithBrowserAsync(authUrl =>
        {
            _ = Task.Run(async () =>
            {
                var uri = new Uri(authUrl);
                var query = uri.Query.TrimStart('?')
                    .Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Split('='))
                    .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
                var redirect = query["redirect_uri"];
                var state = query["state"];
                using var browserSim = new HttpClient();
                await browserSim.GetAsync($"{redirect}?code=pkce_code_789&state={Uri.EscapeDataString(state)}");
            });
        });

        Assert.Equal("duc@ptit.edu.vn", tokens.Email);
        var cipher = await File.ReadAllBytesAsync(creds.FilePath);
        Assert.DoesNotContain("acc_pkce", Encoding.UTF8.GetString(cipher));

        var src = Path.Combine(cfg.WatchedFolders[0].Path, "drive_ok.cpp");
        await archiver.ProcessFileSaveAsync(src, Encoding.UTF8.GetBytes("#include <stdio.h>\nint main(){ printf(\"drive\"); }\n"), null);
        var sync = await drive.SyncPendingAsync();
        Assert.Equal(1, sync.UploadedCount);
        Assert.Equal(1, handler.UploadCalls);

        await drive.DisconnectAsync();
        Assert.Equal(1, handler.RevokeCalls);
        Assert.Null(await creds.LoadAsync());
    }

    // 16. Non-destructive restore keeps original source and restores exact archived bytes
    [Fact]
    public async Task Scenario16_NonDestructiveRestore_OriginalSourceUntouchedAndArchiveByteExact()
    {
        var cfg = MakeConfig();
        await using var db = new SqliteArchiveStore(cfg.DatabasePath);
        await db.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, db);

        var src = Path.Combine(cfg.WatchedFolders[0].Path, "utf8_bom.cpp");
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var code = Encoding.UTF8.GetBytes("// Bình luận tiếng Việt\n#include <iostream>\nint main(){ std::cout << \"Xin chào\"; }\n");
        var exactOriginal = bom.Concat(code).ToArray();
        await File.WriteAllBytesAsync(src, exactOriginal);

        var ctx = new ProblemContext(OjPlatform.Ptit, "CPP0101", "Tinh Tong", "https://code.ptit.edu.vn/student/question/CPP0101", "Extension", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var res = await archiver.ProcessFileSaveAsync(src, exactOriginal, ctx);

        // Original source untouched & Ready file matches lossless bytes
        Assert.Equal(exactOriginal, await File.ReadAllBytesAsync(src));
        Assert.NotNull(res.Version!.ReadyPath);
        Assert.EndsWith(".cpp", res.Version.ReadyPath!);

        var restored = Path.Combine(_root, "RestoredCopy.cpp");
        await archiver.RestoreVersionAsync(res.Version.Id, restored);
        Assert.Equal(exactOriginal, await File.ReadAllBytesAsync(restored));
    }

    // 17. Different Windows username discovery & full drive-root rejection guard
    [Fact]
    public void Scenario17_PortableUserProfileAndDriveRootSafetyGuard()
    {
        var user2 = Path.Combine(_root, "Users", "NguyenVanAn");
        Directory.CreateDirectory(Path.Combine(user2, "Desktop"));
        Directory.CreateDirectory(Path.Combine(user2, "Documents"));

        var cfg = GhostCodeConfig.CreateDefault(userProfileOverride: user2, localAppDataOverride: Path.Combine(user2, "AppData", "Local"));
        Assert.Contains(cfg.WatchedFolders, f => f.Path.StartsWith(user2, StringComparison.OrdinalIgnoreCase) && f.Path.EndsWith("Desktop"));
        Assert.DoesNotContain(cfg.WatchedFolders, f => f.Path.Contains("OneDrive", StringComparison.OrdinalIgnoreCase));
        Assert.True(KnownFoldersResolver.IsDriveRoot(@"C:\"));
        Assert.True(KnownFoldersResolver.IsDriveRoot(@"E:"));
    }

    // 18. Genuine WinUI 3 binary built, self-contained, and launches cleanly serving loopback status
    [Fact]
    public async Task Scenario18_WinUI3ReleaseBinaryExistsAndLaunchesCleanlyWithLiveBridge()
    {
        var repoRoot = FindRepoRoot();
        var releaseExe = Path.Combine(repoRoot, "src", "GhostCode.App", "bin", "x64", "Release", "net10.0-windows10.0.19041.0", "win-x64", "GhostCode.App.exe");
        var debugExe = Path.Combine(repoRoot, "src", "GhostCode.App", "bin", "x64", "Debug", "net10.0-windows10.0.19041.0", "win-x64", "GhostCode.App.exe");

#if DEBUG
        var exePath = File.Exists(debugExe) ? debugExe : releaseExe;
#else
        var exePath = File.Exists(releaseExe) ? releaseExe : debugExe;
#endif
        Assert.True(File.Exists(exePath), $"WinUI 3 executable not found at {exePath}");
        var binDir = Path.GetDirectoryName(exePath)!;
        Assert.True(File.Exists(Path.Combine(binDir, "Assets", "app.ico")));
        Assert.True(File.Exists(Path.Combine(binDir, "Microsoft.WinUI.dll")));

        // Pick free ephemeral port so launch test never collides with an active GhostCode instance
        int freePort;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            freePort = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        var testCfgPath = Path.Combine(_root, "launch_test_config.json");
        var launchCfg = new GhostCodeConfig
        {
            WatchedFolders = new List<WatchedFolderConfig>(),
            ArchiveDir = Path.Combine(_root, "LaunchArchive"),
            ReadyDir = Path.Combine(_root, "LaunchReady"),
            DatabasePath = Path.Combine(_root, "LaunchArchive", "ghostcode.db"),
            HttpPort = freePort,
            BridgeAuthToken = "launch-probe-token",
            DriveEnabled = false,
            MinimizeToTrayOnClose = false
        };
        launchCfg.Save(testCfgPath);

        var psi = new ProcessStartInfo(exePath)
        {
            WorkingDirectory = binDir,
            UseShellExecute = false
        };
        psi.Environment["GHOSTCODE_CONFIG_PATH"] = testCfgPath;

        using var proc = Process.Start(psi)!;
        try
        {
            using var http = new HttpClient();
            string? statusJson = null;
            var deadline = Environment.TickCount64 + 25_000;
            while (Environment.TickCount64 < deadline)
            {
                if (proc.HasExited)
                    throw new InvalidOperationException($"GhostCode.App.exe exited early with code {proc.ExitCode}.");
                try
                {
                    var resp = await http.GetAsync($"http://127.0.0.1:{freePort}/status");
                    if (resp.IsSuccessStatusCode)
                    {
                        statusJson = await resp.Content.ReadAsStringAsync();
                        break;
                    }
                }
                catch
                {
                    await Task.Delay(250);
                }
            }

            Assert.NotNull(statusJson);
            using var statusDoc = JsonDocument.Parse(statusJson);
            Assert.True(statusDoc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(JsonValueKind.Array, statusDoc.RootElement.GetProperty("browser_titles").ValueKind);
            Assert.False(proc.HasExited);
        }
        finally
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch { }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "GhostCode.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate GhostCode.sln above " + AppContext.BaseDirectory);
    }
}
