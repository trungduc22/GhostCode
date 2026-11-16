using System.Net;
using System.Text;
using System.Text.Json;
using GhostCode.Core.Archiver;
using GhostCode.Core.Cloud;
using GhostCode.Core.Models;
using GhostCode.Core.Monitoring;
using GhostCode.Core.Notifications;
using GhostCode.Core.Storage;

namespace GhostCode.Tests;

public sealed class ExpansionP0ReliabilityTests : IDisposable
{
    private readonly string _root;

    public ExpansionP0ReliabilityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GhostCode_ExpP0_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class RecordingPresenter : INativeNotificationPresenter
    {
        public List<(string Category, string Severity, string Title, string Message)> Delivered { get; } = new();
        public void Present(string category, string severity, string title, string message, string actionHint) =>
            Delivered.Add((category, severity, title, message));
    }

    private sealed class FakeRecoveryDriveHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Name, byte[] Bytes, string Sha)> _remote = new();
        private int _seq;
        public bool FailAllUploads { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("alt=media", StringComparison.Ordinal))
            {
                var id = request.RequestUri.AbsolutePath.Split('/').Last();
                return _remote.TryGetValue(id, out var f)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(f.Bytes) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (url.StartsWith("https://www.googleapis.com/drive/v3/files", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
            {
                var dec = Uri.UnescapeDataString(url);
                if (dec.Contains("mimeType='application/vnd.google-apps.folder'", StringComparison.Ordinal))
                    return Json(new { files = new[] { new { id = "fld_1", name = "CodeArchive" } } });

                IEnumerable<KeyValuePair<string, (string Name, byte[] Bytes, string Sha)>> items = _remote;
                if (dec.Contains("ghostcode_sha256", StringComparison.Ordinal))
                    items = items.Where(x => dec.Contains(x.Value.Sha, StringComparison.OrdinalIgnoreCase));

                return Json(new
                {
                    files = items.Select(kv => new
                    {
                        id = kv.Key,
                        name = kv.Value.Name,
                        size = kv.Value.Bytes.LongLength.ToString(),
                        createdTime = DateTimeOffset.UtcNow.ToString("O"),
                        appProperties = new Dictionary<string, string> { ["ghostcode_sha256"] = kv.Value.Sha }
                    })
                });
            }

            if (url.StartsWith("https://www.googleapis.com/upload/drive/v3/files", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                if (FailAllUploads)
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Offline test") };

                var mp = (MultipartContent)request.Content!;
                var parts = mp.ToList();
                var meta = await parts[0].ReadAsStringAsync(ct);
                var bytes = await parts[1].ReadAsByteArrayAsync(ct);
                using var doc = JsonDocument.Parse(meta);
                var name = doc.RootElement.GetProperty("name").GetString() ?? "file";
                var sha = doc.RootElement.GetProperty("appProperties").GetProperty("ghostcode_sha256").GetString() ?? "";
                var id = $"rem_{++_seq}";
                _remote[id] = (name, bytes, sha);
                return Json(new { id, name });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json")
        };
    }

    [Fact]
    public async Task BackupHealthAndRecovery_DetectsInactiveWatchesCorruptAndMissingFilesAndVerifiesRemote()
    {
        var watchDir = Path.Combine(_root, "Watched");
        var archiveDir = Path.Combine(_root, "Archive");
        var readyDir = Path.Combine(_root, "Ready");
        Directory.CreateDirectory(watchDir);
        Directory.CreateDirectory(archiveDir);
        Directory.CreateDirectory(readyDir);

        var cfg = new GhostCodeConfig
        {
            WatchedFolders = new List<WatchedFolderConfig>(), // Initially zero active folders!
            ArchiveDir = archiveDir,
            ReadyDir = readyDir,
            DatabasePath = Path.Combine(archiveDir, "ghost.db"),
            OAuthClient = new GoogleOAuthClientConfig("test.apps.googleusercontent.com", "")
        };

        await using var store = new SqliteArchiveStore(cfg.DatabasePath);
        await store.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, store);
        await using var monitor = new MultiDirectoryMonitor(cfg, store, archiver, () => null);
        var notif = new NotificationCoordinator(cfg, store);
        var healthSvc = new BackupHealthAndRecoveryService(cfg, store, monitor, notif);

        // 1. No active watched folders -> NEVER report Protected
        monitor.Start();
        var emptySnap = await healthSvc.EvaluateHealthAsync();
        Assert.NotEqual(BackupProtectionState.Protected, emptySnap.ProtectionState);
        Assert.False(emptySnap.MonitoringActive);

        // 2. Add real watched folder -> becomes Protected
        cfg.WatchedFolders.Add(new WatchedFolderConfig(watchDir, true, true, "Watched"));
        monitor.ReloadWatches();
        var healthySnap = await healthSvc.EvaluateHealthAsync();
        Assert.Equal(BackupProtectionState.Protected, healthySnap.ProtectionState);
        Assert.True(healthySnap.MonitoringActive);
        Assert.True(healthySnap.DatabaseHealthy);

        // 3. Archive 2 files, upload to fake Drive, verify clean Recovery Check
        var f1 = Path.Combine(watchDir, "sol1.cpp");
        var f2 = Path.Combine(watchDir, "sol2.cpp");
        var r1 = await archiver.ProcessFileSaveAsync(f1, Encoding.UTF8.GetBytes("int main(){return 1;}"), null);
        var r2 = await archiver.ProcessFileSaveAsync(f2, Encoding.UTF8.GetBytes("int main(){return 2;}"), null);

        var creds = new DpapiCredentialStore(Path.Combine(_root, "creds.dpapi"));
        await creds.SaveAsync(new GoogleAuthTokens("tok", "ref", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(), "u@test.com", "User"));
        var handler = new FakeRecoveryDriveHandler();
        await using var drive = new GoogleDriveService(cfg, store, creds, new HttpClient(handler), notif);

        var syncRes = await drive.SyncPendingAsync();
        Assert.Equal(2, syncRes.UploadedCount);

        var checkClean = await healthSvc.RunRecoveryCheckAsync(drive, maxRemoteSamples: 2);
        Assert.Equal(2, checkClean.TotalVersionsChecked);
        Assert.Equal(2, checkClean.LocalVerifiedOk);
        Assert.Equal(0, checkClean.LocalMissingCount);
        Assert.Equal(0, checkClean.LocalCorruptedCount);
        Assert.Equal(2, checkClean.RemoteVerifiedCount);
        Assert.Equal(2, checkClean.RemoteSampleByteVerifiedCount);

        // 4. Corrupt v1 and delete v2 local archive copy -> non-destructive check catches both accurately
        await File.WriteAllTextAsync(r1.Version!.ArchivePath, "// CORRUPTED BY BITROT");
        File.Delete(r2.Version!.ArchivePath);

        var checkDirty = await healthSvc.RunRecoveryCheckAsync(drive, maxRemoteSamples: 1);
        Assert.Equal(1, checkDirty.LocalCorruptedCount);
        Assert.Equal(1, checkDirty.LocalMissingCount);
        Assert.Contains(checkDirty.Issues, i => i.IssueKind == "CorruptedLocalFile");
        Assert.Contains(checkDirty.Issues, i => i.IssueKind == "MissingLocalFile");
    }

    [Fact]
    public async Task NotificationCoordinator_DeduplicatesFailuresRespectsLevelsAndTriggersOnRecovery()
    {
        var archiveDir = Path.Combine(_root, "Archive2");
        Directory.CreateDirectory(archiveDir);
        var cfg = new GhostCodeConfig
        {
            ArchiveDir = archiveDir,
            ReadyDir = Path.Combine(_root, "Ready2"),
            DatabasePath = Path.Combine(archiveDir, "ghost.db"),
            NotificationLevel = NotificationLevel.All,
            AlertCloudConnection = true,
            QuietHoursEnabled = false
        };

        await using var store = new SqliteArchiveStore(cfg.DatabasePath);
        await store.InitializeAsync();
        var presenter = new RecordingPresenter();
        var coordinator = new NotificationCoordinator(cfg, store, presenter)
        {
            DedupCooldown = TimeSpan.FromMinutes(10)
        };

        // 1st failure -> below threshold (no alert spam yet)
        var d1 = await coordinator.NotifyRepeatedFailureOrRecoveryAsync("cloud", "drv", failed: true, "Sync Lost", "Fail 1", "Recovered", "Back online", thresholdBeforeAlert: 2);
        Assert.False(d1.PresentedNatively);

        // 2nd failure -> fires grouped warning alert once
        var d2 = await coordinator.NotifyRepeatedFailureOrRecoveryAsync("cloud", "drv", failed: true, "Sync Lost", "Fail 2", "Recovered", "Back online", thresholdBeforeAlert: 2);
        Assert.True(d2.PresentedNatively);

        // 3rd failure within cooldown -> deduplicated (not re-presented natively)
        var d3 = await coordinator.NotifyRepeatedFailureOrRecoveryAsync("cloud", "drv", failed: true, "Sync Lost", "Fail 3", "Recovered", "Back online", thresholdBeforeAlert: 2);
        Assert.False(d3.PresentedNatively);
        Assert.Equal("DeduplicatedCooldown", d3.SuppressedReason);

        // Recovery -> clears failure & presents recovery alert
        var d4 = await coordinator.NotifyRepeatedFailureOrRecoveryAsync("cloud", "drv", failed: false, "Sync Lost", "", "Recovered", "Back online", thresholdBeforeAlert: 2);
        Assert.True(d4.PresentedNatively);
        Assert.Equal(2, presenter.Delivered.Count);

        // Disabled mode still records notification_history in SQLite for missed alerts, but does not present natively
        cfg.NotificationLevel = NotificationLevel.Disabled;
        var d5 = await coordinator.NotifyAsync("verdict", "Info", "AC", "Accepted CPP0101", "v_ac");
        Assert.True(d5.Persisted);
        Assert.False(d5.PresentedNatively);
        var recent = await store.GetRecentNotificationsAsync(20);
        Assert.Contains(recent, n => n.Title == "AC");
    }

    [Fact]
    public async Task SyncPausedManually_PreservesLocalArchivingAndSkipsCloudUploadWithoutDroppingQueue()
    {
        var archiveDir = Path.Combine(_root, "Archive3");
        Directory.CreateDirectory(archiveDir);
        var cfg = new GhostCodeConfig
        {
            ArchiveDir = archiveDir,
            ReadyDir = Path.Combine(_root, "Ready3"),
            DatabasePath = Path.Combine(archiveDir, "ghost.db"),
            SyncPausedManually = true
        };

        await using var store = new SqliteArchiveStore(cfg.DatabasePath);
        await store.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, store);
        var creds = new DpapiCredentialStore(Path.Combine(_root, "creds3.dpapi"));
        await creds.SaveAsync(new GoogleAuthTokens("tok", "ref", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(), "u@test.com", "User"));
        var handler = new FakeRecoveryDriveHandler();
        await using var drive = new GoogleDriveService(cfg, store, creds, new HttpClient(handler));

        var src = Path.Combine(_root, "local.cpp");
        await archiver.ProcessFileSaveAsync(src, Encoding.UTF8.GetBytes("#include <iostream>\nint main(){ int x = 42; std::cout << x; return 0; }\n"), null);

        var pausedReport = await drive.SyncPendingAsync();
        Assert.Equal(0, pausedReport.UploadedCount);
        var statsWhilePaused = await store.GetStatsAsync();
        Assert.Equal(1, statsWhilePaused.PendingUploads);

        cfg.SyncPausedManually = false;
        var resumedReport = await drive.SyncPendingAsync();
        Assert.Equal(1, resumedReport.UploadedCount);
        var statsAfter = await store.GetStatsAsync();
        Assert.Equal(0, statsAfter.PendingUploads);
        Assert.Equal(1, statsAfter.SyncedUploads);
    }
}
