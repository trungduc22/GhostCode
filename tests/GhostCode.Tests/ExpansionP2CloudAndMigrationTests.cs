using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GhostCode.Core.Archiver;
using GhostCode.Core.Cloud;
using GhostCode.Core.Models;
using GhostCode.Core.Storage;

namespace GhostCode.Tests;

public sealed class ExpansionP2CloudAndMigrationTests : IDisposable
{
    private readonly string _root;

    public ExpansionP2CloudAndMigrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GhostCode_ExpP2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class FakeOneDriveGraphHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Folder, string Name, byte[] Bytes, string Sha256)> _items = new(StringComparer.Ordinal);
        private int _seq;
        public int TotalRequests { get; private set; }

        public IReadOnlyDictionary<string, (string Folder, string Name, byte[] Bytes, string Sha256)> Items => _items;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            TotalRequests++;
            var uri = request.RequestUri!;
            var url = uri.ToString();

            if (request.Method == HttpMethod.Put && url.Contains("/special/approot:/", StringComparison.Ordinal))
            {
                // Extract path between approot:/ and :/content
                var marker = "/special/approot:/";
                var start = url.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
                var end = url.IndexOf(":/content", StringComparison.Ordinal);
                var rawPath = Uri.UnescapeDataString(url[start..end]);
                var parts = rawPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var name = parts.Last();
                var folder = parts.Length >= 3 ? parts[^2] : string.Empty; // "" for CodeArchive root, "History" for CodeArchive/History
                var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
                var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

                var existing = _items.FirstOrDefault(kv => kv.Value.Folder == folder && kv.Value.Name == name).Key;
                var id = string.IsNullOrEmpty(existing) ? $"od_{++_seq}" : existing;
                _items[id] = (folder, name, bytes, sha);
                return Json(new { id, name, size = bytes.LongLength });
            }

            if (request.Method == HttpMethod.Get && url.Contains("/special/approot:/", StringComparison.Ordinal) && url.EndsWith(":/children", StringComparison.Ordinal))
            {
                var marker = "/special/approot:/";
                var start = url.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
                var end = url.IndexOf(":/children", StringComparison.Ordinal);
                var rawPath = Uri.UnescapeDataString(url[start..end]);
                var segments = rawPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var targetFolder = segments.Length >= 2 ? segments[^1] : string.Empty;

                var matching = _items
                    .Where(kv => string.Equals(kv.Value.Folder, targetFolder, StringComparison.OrdinalIgnoreCase))
                    .Select(kv => new
                    {
                        id = kv.Key,
                        name = kv.Value.Name,
                        size = kv.Value.Bytes.LongLength,
                        lastModifiedDateTime = DateTimeOffset.UtcNow.ToString("O"),
                        file = new
                        {
                            hashes = new
                            {
                                sha256Hash = kv.Value.Sha256
                            }
                        }
                    });
                return Json(new { value = matching });
            }

            if (request.Method == HttpMethod.Get && url.Contains("/me/drive/items/", StringComparison.Ordinal) && url.EndsWith("/content", StringComparison.Ordinal))
            {
                var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var itemId = Uri.UnescapeDataString(segs[^2]);
                return _items.TryGetValue(itemId, out var entry)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(entry.Bytes) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json")
        };
    }

    private sealed class FakeGoogleDriveHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Name, byte[] Bytes, string? Desc, Dictionary<string, string> Props)> _files = new(StringComparer.Ordinal);
        private int _seq;
        public int TotalRequests { get; private set; }
        public int DeleteRequests { get; private set; }

        public IReadOnlyDictionary<string, (string Name, byte[] Bytes, string? Desc, Dictionary<string, string> Props)> Files => _files;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            TotalRequests++;
            if (request.Method == HttpMethod.Delete)
                DeleteRequests++;

            var url = request.RequestUri!.ToString();
            if (url.Contains("alt=media", StringComparison.Ordinal))
            {
                var id = request.RequestUri.AbsolutePath.Split('/').Last();
                return _files.TryGetValue(id, out var f)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(f.Bytes) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (url.StartsWith("https://www.googleapis.com/drive/v3/files", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
            {
                var dec = Uri.UnescapeDataString(url);
                if (dec.Contains("mimeType='application/vnd.google-apps.folder'", StringComparison.Ordinal))
                    return Json(new { files = new[] { new { id = "gd_folder", name = "CodeArchive" } } });

                IEnumerable<KeyValuePair<string, (string Name, byte[] Bytes, string? Desc, Dictionary<string, string> Props)>> items = _files;
                if (dec.Contains("ghostcode_sha256", StringComparison.Ordinal))
                    items = items.Where(x => x.Value.Props.TryGetValue("ghostcode_sha256", out var s) && dec.Contains(s, StringComparison.OrdinalIgnoreCase));
                else if (dec.Contains("name='", StringComparison.Ordinal))
                    items = items.Where(x => dec.Contains($"name='{x.Value.Name}'", StringComparison.OrdinalIgnoreCase));

                return Json(new
                {
                    files = items.Select(kv => new
                    {
                        id = kv.Key,
                        name = kv.Value.Name,
                        size = kv.Value.Bytes.LongLength.ToString(),
                        sha256Checksum = Convert.ToHexString(SHA256.HashData(kv.Value.Bytes)).ToLowerInvariant(),
                        createdTime = DateTimeOffset.UtcNow.ToString("O"),
                        description = kv.Value.Desc,
                        appProperties = kv.Value.Props
                    })
                });
            }

            if (url.StartsWith("https://www.googleapis.com/upload/drive/v3/files", StringComparison.Ordinal)
                && (request.Method == HttpMethod.Post || request.Method == HttpMethod.Patch))
            {
                var mp = (MultipartContent)request.Content!;
                var parts = mp.ToList();
                var metaJson = await parts[0].ReadAsStringAsync(ct);
                var bytes = await parts[1].ReadAsByteArrayAsync(ct);
                using var doc = JsonDocument.Parse(metaJson);
                var root = doc.RootElement;
                var name = root.TryGetProperty("name", out var np) ? (np.GetString() ?? "file") : "file";
                var desc = root.TryGetProperty("description", out var dp) ? dp.GetString() : null;
                var props = new Dictionary<string, string>();
                if (root.TryGetProperty("appProperties", out var ap) && ap.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in ap.EnumerateObject())
                        if (p.Value.GetString() is string v) props[p.Name] = v;
                }

                string id;
                if (request.Method == HttpMethod.Patch)
                    id = request.RequestUri.AbsolutePath.Split('/').Last();
                else
                    id = $"gd_{++_seq}";

                _files[id] = (name, bytes, desc, props);
                return Json(new { id, name });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json")
        };
    }

    private static async Task<DpapiCredentialStore> CreateSeededCredStoreAsync(string filePath, string email)
    {
        var store = new DpapiCredentialStore(filePath);
        await store.SaveAsync(new GoogleAuthTokens(
            AccessToken: "fake-access-token",
            RefreshToken: "fake-refresh-token",
            ExpiresAtUnixMs: DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
            Email: email,
            DisplayName: "Test User"));
        return store;
    }

    [Fact]
    public async Task OneDriveService_UploadsDedupesPublishesManifestAndDownloadsVerifiedVersions()
    {
        var pc1Watch = Path.Combine(_root, "pc1_watch");
        var pc1Archive = Path.Combine(_root, "pc1_archive");
        var pc1Ready = Path.Combine(_root, "pc1_ready");
        Directory.CreateDirectory(pc1Watch);
        Directory.CreateDirectory(pc1Archive);
        Directory.CreateDirectory(pc1Ready);

        var cfg1 = new GhostCodeConfig
        {
            ArchiveDir = pc1Archive,
            ReadyDir = pc1Ready,
            DatabasePath = Path.Combine(pc1Archive, "ghost.db"),
            PrimaryCloudProvider = CloudProviderKind.OneDrive,
            OneDriveOAuthClient = new MicrosoftOAuthClientConfig("ms-client-id-test", "consumers")
        };

        await using var store1 = new SqliteArchiveStore(cfg1.DatabasePath);
        await store1.InitializeAsync();
        var archiver1 = new LosslessArchiver(cfg1, store1);

        var src = Path.Combine(pc1Watch, "solve.cpp");
        const string codeV1 = "#include <bits/stdc++.h>\nint main(){ int n = 5; std::cout << n * 2; return 0; }\n";
        await File.WriteAllTextAsync(src, codeV1);
        var archived = await archiver1.ProcessFileSaveAsync(
            src,
            Encoding.UTF8.GetBytes(codeV1),
            new ProblemContext(OjPlatform.Codeforces, "CF100", "OneDrive Sync", "https://codeforces.com", "extension", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), true, "Edge"));
        Assert.Equal(ArchiveOutcome.CreatedVersion, archived.Outcome);

        var odHandler = new FakeOneDriveGraphHandler();
        var odCreds1 = await CreateSeededCredStoreAsync(Path.Combine(pc1Archive, "onedrive.dpapi"), "student@outlook.com");
        await using var od1 = new OneDriveService(cfg1, store1, odCreds1, new HttpClient(odHandler));

        var (push, pull) = await od1.SyncBidirectionalAsync();
        Assert.True(push.Connected);
        Assert.Equal(1, push.UploadedCount);
        Assert.Equal(0, push.FailedCount);
        Assert.Null(pull.Error);
        Assert.Contains(odHandler.Items.Values, x => x.Name == CloudManifestCodec.ManifestFileName);

        // Fresh PC #2 restoring purely from OneDrive AppFolder
        var pc2Archive = Path.Combine(_root, "pc2_archive");
        var pc2Ready = Path.Combine(_root, "pc2_ready");
        Directory.CreateDirectory(pc2Archive);
        Directory.CreateDirectory(pc2Ready);

        var cfg2 = new GhostCodeConfig
        {
            ArchiveDir = pc2Archive,
            ReadyDir = pc2Ready,
            DatabasePath = Path.Combine(pc2Archive, "ghost.db"),
            PrimaryCloudProvider = CloudProviderKind.OneDrive,
            OneDriveOAuthClient = new MicrosoftOAuthClientConfig("ms-client-id-test", "consumers")
        };
        await using var store2 = new SqliteArchiveStore(cfg2.DatabasePath);
        await store2.InitializeAsync();
        var odCreds2 = await CreateSeededCredStoreAsync(Path.Combine(pc2Archive, "onedrive.dpapi"), "student@outlook.com");
        await using var od2 = new OneDriveService(cfg2, store2, odCreds2, new HttpClient(odHandler));

        var pc2Pull = await od2.PullAndReconcileAsync();
        Assert.True(pc2Pull.Connected);
        Assert.Equal(1, pc2Pull.DiscoveredRemoteVersions);
        Assert.Equal(1, pc2Pull.NewCloudOnlyCount);

        var (dlOk, dlFail, dlErr) = await od2.DownloadAllCloudOnlyVersionsAsync();
        Assert.Equal(1, dlOk);
        Assert.Equal(0, dlFail);
        Assert.Null(dlErr);

        var restored = await store2.QueryVersionsAsync();
        Assert.Single(restored);
        Assert.True(restored[0].IsLocalAvailable);
        Assert.Equal(codeV1, await File.ReadAllTextAsync(restored[0].ArchivePath));
    }

    [Fact]
    public async Task LocalOnlyMode_SkipsAllNetworkCalls_PreservingUploadQueue()
    {
        var watchDir = Path.Combine(_root, "local_watch");
        var archiveDir = Path.Combine(_root, "local_archive");
        var readyDir = Path.Combine(_root, "local_ready");
        Directory.CreateDirectory(watchDir);
        Directory.CreateDirectory(archiveDir);
        Directory.CreateDirectory(readyDir);

        var cfg = new GhostCodeConfig
        {
            ArchiveDir = archiveDir,
            ReadyDir = readyDir,
            DatabasePath = Path.Combine(archiveDir, "ghost.db"),
            PrimaryCloudProvider = CloudProviderKind.LocalOnly,
            OAuthClient = new GoogleOAuthClientConfig("g-id"),
            OneDriveOAuthClient = new MicrosoftOAuthClientConfig("m-id", "consumers")
        };

        await using var store = new SqliteArchiveStore(cfg.DatabasePath);
        await store.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, store);

        var src = Path.Combine(watchDir, "local.py");
        const string pyCode = "def solve(n):\n    return sum(range(n))\nprint(solve(10))\n";
        await File.WriteAllTextAsync(src, pyCode);
        await archiver.ProcessFileSaveAsync(src, Encoding.UTF8.GetBytes(pyCode), null);

        var gdHandler = new FakeGoogleDriveHandler();
        var odHandler = new FakeOneDriveGraphHandler();
        var gdCreds = await CreateSeededCredStoreAsync(Path.Combine(archiveDir, "gd.dpapi"), "g@gmail.com");
        var odCreds = await CreateSeededCredStoreAsync(Path.Combine(archiveDir, "od.dpapi"), "o@outlook.com");

        await using var gd = new GoogleDriveService(cfg, store, gdCreds, new HttpClient(gdHandler));
        await using var od = new OneDriveService(cfg, store, odCreds, new HttpClient(odHandler));

        var gdReport = await gd.SyncPendingAsync();
        var odReport = await od.SyncPendingAsync();

        Assert.Equal(0, gdHandler.TotalRequests);
        Assert.Equal(0, odHandler.TotalRequests);
        Assert.Contains("Local-Only", gdReport.LastError);
        Assert.Contains("Local-Only", odReport.LastError);

        var pending = await store.GetPendingOrRetryableUploadsAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Single(pending);
    }

    [Fact]
    public async Task CloudMigrationService_MigratesGoogleDriveToOneDriveNonDestructively_AndReconstructsFreshPc()
    {
        var pc1Watch = Path.Combine(_root, "mig1_watch");
        var pc1Archive = Path.Combine(_root, "mig1_archive");
        var pc1Ready = Path.Combine(_root, "mig1_ready");
        Directory.CreateDirectory(pc1Watch);
        Directory.CreateDirectory(pc1Archive);
        Directory.CreateDirectory(pc1Ready);

        var cfg1 = new GhostCodeConfig
        {
            ArchiveDir = pc1Archive,
            ReadyDir = pc1Ready,
            DatabasePath = Path.Combine(pc1Archive, "ghost.db"),
            PrimaryCloudProvider = CloudProviderKind.GoogleDrive,
            OAuthClient = new GoogleOAuthClientConfig("g-client"),
            OneDriveOAuthClient = new MicrosoftOAuthClientConfig("m-client", "consumers")
        };

        await using var store1 = new SqliteArchiveStore(cfg1.DatabasePath);
        await store1.InitializeAsync();
        var archiver1 = new LosslessArchiver(cfg1, store1);

        var f1 = Path.Combine(pc1Watch, "p1.cpp");
        const string code1 = "#include <iostream>\nint main(){ int k = 7; std::cout << k; return 0; }\n";
        const string code2 = "#include <iostream>\nint main(){ int k = 14; std::cout << k * k; return 0; }\n";
        await File.WriteAllTextAsync(f1, code1);
        var ctxPtit = new ProblemContext(OjPlatform.Ptit, "J01001", "Migrated Problem", "https://code.ptit.edu.vn/student/question/J01001", "extension", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), true, "Edge");
        var r1 = await archiver1.ProcessFileSaveAsync(f1, Encoding.UTF8.GetBytes(code1), ctxPtit);
        await File.WriteAllTextAsync(f1, code2);
        var r2 = await archiver1.ProcessFileSaveAsync(f1, Encoding.UTF8.GetBytes(code2), ctxPtit);
        Assert.Equal(ArchiveOutcome.CreatedVersion, r1.Outcome);
        Assert.Equal(ArchiveOutcome.CreatedVersion, r2.Outcome);

        // Also seed problem metadata (statement, sample test, submission) to verify full v2 manifest transfer
        var pKey = ProblemStatementRecord.BuildKey("Ptit", "J01001");
        await store1.UpsertProblemStatementAsync(new ProblemStatementRecord(
            pKey, "Ptit", "J01001", "Migrated Problem", "https://code.ptit.edu.vn/student/question/J01001",
            "Print k squared.", "Single integer k", "Square of k", "1 <= k <= 100", "1.0s", "128MB", false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        await store1.UpsertSampleTestAsync(new SampleTestCaseRecord(
            "tc1", pKey, 1, "14\n", "196\n", "https://code.ptit.edu.vn/student/question/J01001", false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        await store1.UpsertSubmissionRecordAsync(new SubmissionRecord(
            "sub1", "Ptit", "J01001", "S999", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "C++17", SubmissionVerdict.Accepted, 12, 1024, r2.Version!.Id, true));

        var gdHandler = new FakeGoogleDriveHandler();
        var odHandler = new FakeOneDriveGraphHandler();
        var gdCreds = await CreateSeededCredStoreAsync(Path.Combine(pc1Archive, "gd.dpapi"), "user@gmail.com");
        var odCreds = await CreateSeededCredStoreAsync(Path.Combine(pc1Archive, "od.dpapi"), "user@outlook.com");

        await using var gd = new GoogleDriveService(cfg1, store1, gdCreds, new HttpClient(gdHandler));
        await using var od = new OneDriveService(cfg1, store1, odCreds, new HttpClient(odHandler));

        var initialSync = await gd.SyncBidirectionalAsync();
        Assert.Equal(2, initialSync.PushReport.UploadedCount);
        int gdFilesBefore = gdHandler.Files.Count;

        // Migrate non-destructively from Google Drive -> OneDrive
        var migrator = new CloudMigrationService(cfg1, store1, archiver1);
        var migration = await migrator.MigrateAsync(gd, od);

        Assert.True(migration.Success);
        Assert.Equal(2, migration.TotalVersions);
        Assert.Equal(2, migration.MigratedCount);
        Assert.True(migration.ManifestPublished);
        Assert.True(migration.ManifestVerifiedOnDestination);
        Assert.Equal(0, gdHandler.DeleteRequests);
        Assert.Equal(gdFilesBefore, gdHandler.Files.Count);

        // Fresh PC #2 restoring purely from OneDrive receives versions + statement + sample + submission + migration event
        var pc2Archive = Path.Combine(_root, "mig2_archive");
        var pc2Ready = Path.Combine(_root, "mig2_ready");
        Directory.CreateDirectory(pc2Archive);
        Directory.CreateDirectory(pc2Ready);
        var cfg2 = new GhostCodeConfig
        {
            ArchiveDir = pc2Archive,
            ReadyDir = pc2Ready,
            DatabasePath = Path.Combine(pc2Archive, "ghost.db"),
            PrimaryCloudProvider = CloudProviderKind.OneDrive,
            OneDriveOAuthClient = new MicrosoftOAuthClientConfig("m-client", "consumers")
        };
        await using var store2 = new SqliteArchiveStore(cfg2.DatabasePath);
        await store2.InitializeAsync();
        var odCreds2 = await CreateSeededCredStoreAsync(Path.Combine(pc2Archive, "od.dpapi"), "user@outlook.com");
        await using var od2 = new OneDriveService(cfg2, store2, odCreds2, new HttpClient(odHandler));

        var pull2 = await od2.PullAndReconcileAsync();
        Assert.True(pull2.Connected);
        Assert.Equal(2, pull2.DiscoveredRemoteVersions);

        var (dlOk, dlFail, _) = await od2.DownloadAllCloudOnlyVersionsAsync();
        Assert.Equal(2, dlOk);
        Assert.Equal(0, dlFail);

        var stmt2 = await store2.GetProblemStatementAsync(pKey);
        var samples2 = await store2.GetSampleTestsAsync(pKey);
        var subs2 = await store2.GetSubmissionsForProblemAsync("J01001", 10);
        var events2 = await store2.GetArchiveHistoryEventsAsync(20);

        Assert.NotNull(stmt2);
        Assert.Single(samples2);
        Assert.Equal("196\n", samples2[0].ExpectedOutput);
        Assert.Single(subs2);
        Assert.Equal(SubmissionVerdict.Accepted, subs2[0].Verdict);
        Assert.Contains(events2, e => e.EventType == "CloudMigrationCompleted");
    }
}
