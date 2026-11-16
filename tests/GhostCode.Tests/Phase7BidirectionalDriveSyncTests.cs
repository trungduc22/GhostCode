using System.Net;
using System.Text;
using System.Text.Json;
using GhostCode.Core.Archiver;
using GhostCode.Core.Cloud;
using GhostCode.Core.Execution;
using GhostCode.Core.Models;
using GhostCode.Core.Storage;
using GhostCode.Core.Toolchains;

namespace GhostCode.Tests;

public sealed class Phase7BidirectionalDriveSyncTests : IDisposable
{
    private readonly string _root;

    public Phase7BidirectionalDriveSyncTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GhostCode_P7Drive_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class StoredDriveFile
    {
        public required string Id { get; init; }
        public required string Name { get; set; }
        public string? Description { get; set; }
        public Dictionary<string, string> AppProperties { get; set; } = new();
        public byte[] Bytes { get; set; } = Array.Empty<byte>();
        public string CreatedTime { get; init; } = DateTimeOffset.UtcNow.ToString("O");
    }

    private sealed class InMemoryDriveHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, StoredDriveFile> _files = new(StringComparer.Ordinal);
        private int _seq;
        public const string FolderId = "ca_folder_1";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("alt=media", StringComparison.Ordinal))
            {
                var idPart = request.RequestUri.AbsolutePath.Split('/').Last();
                if (_files.TryGetValue(idPart, out var file))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file.Bytes) };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (url.StartsWith("https://www.googleapis.com/drive/v3/files", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
            {
                var decodedQuery = Uri.UnescapeDataString(url);
                if (decodedQuery.Contains("mimeType='application/vnd.google-apps.folder'", StringComparison.Ordinal))
                {
                    return JsonResp(new { files = new[] { new { id = FolderId, name = "CodeArchive" } } });
                }
                IEnumerable<StoredDriveFile> matches = _files.Values;
                if (decodedQuery.Contains("ghostcode_sha256", StringComparison.Ordinal))
                {
                    matches = matches.Where(f =>
                        f.AppProperties.TryGetValue("ghostcode_sha256", out var sha)
                        && decodedQuery.Contains(sha, StringComparison.OrdinalIgnoreCase));
                }
                else if (decodedQuery.Contains("name='ghostcode_archive_manifest.v2.json'", StringComparison.Ordinal))
                {
                    matches = matches.Where(f => f.Name == "ghostcode_archive_manifest.v2.json");
                }

                return JsonResp(new
                {
                    files = matches.Select(f => new
                    {
                        id = f.Id,
                        name = f.Name,
                        size = f.Bytes.LongLength.ToString(),
                        createdTime = f.CreatedTime,
                        description = f.Description,
                        appProperties = f.AppProperties
                    })
                });
            }

            if (url.StartsWith("https://www.googleapis.com/upload/drive/v3/files", StringComparison.Ordinal)
                && (request.Method == HttpMethod.Post || request.Method == HttpMethod.Patch))
            {
                var mp = (MultipartContent)request.Content!;
                var parts = mp.ToList();
                var metaJson = await parts[0].ReadAsStringAsync(ct);
                var fileBytes = await parts[1].ReadAsByteArrayAsync(ct);

                using var mdoc = JsonDocument.Parse(metaJson);
                var name = mdoc.RootElement.TryGetProperty("name", out var np) ? np.GetString() ?? "file" : "file";
                var desc = mdoc.RootElement.TryGetProperty("description", out var dp) ? dp.GetString() : null;
                var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (mdoc.RootElement.TryGetProperty("appProperties", out var ap) && ap.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in ap.EnumerateObject())
                        if (p.Value.GetString() is string s) props[p.Name] = s;
                }

                string id;
                if (request.Method == HttpMethod.Patch)
                {
                    id = request.RequestUri.AbsolutePath.Split('/').Last();
                    if (_files.TryGetValue(id, out var existing))
                    {
                        existing.Bytes = fileBytes;
                        existing.Description = desc;
                        existing.AppProperties = props;
                    }
                }
                else
                {
                    id = $"drv_{++_seq}";
                    _files[id] = new StoredDriveFile { Id = id, Name = name, Description = desc, AppProperties = props, Bytes = fileBytes };
                }

                return JsonResp(new { id, name });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage JsonResp(object obj) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json")
        };
    }

    [Fact]
    public async Task BidirectionalSync_ReconstructsEmptyPcDatabaseAndHistory_DownloadsAndExecutesVerifiedVersion_AndPreservesConflicts()
    {
        var handler = new InMemoryDriveHandler();
        using var http = new HttpClient(handler);

        // === PC-A: Archive 2 versions of Tổng hai số, sync bidirectionally ===
        var pcADir = Path.Combine(_root, "PC_A");
        var cfgA = new GhostCodeConfig
        {
            ArchiveDir = Path.Combine(pcADir, "Archive"),
            ReadyDir = Path.Combine(pcADir, "Ready"),
            DatabasePath = Path.Combine(pcADir, "Archive", "ghostcode.db"),
            OAuthClient = new GoogleOAuthClientConfig("test-client.apps.googleusercontent.com", "")
        };
        await using var dbA = new SqliteArchiveStore(cfgA.DatabasePath);
        await dbA.InitializeAsync();
        var archA = new LosslessArchiver(cfgA, dbA);
        var credsA = new DpapiCredentialStore(Path.Combine(pcADir, "creds.dpapi"));
        await credsA.SaveAsync(new GoogleAuthTokens("tok-a", "ref-a", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(), "duc@example.com", "Duc"));

        var srcA = Path.Combine(pcADir, "Desktop", "Untitled1.cpp");
        Directory.CreateDirectory(Path.GetDirectoryName(srcA)!);
        var ptitCtx = new ProblemContext(
            OjPlatform.Ptit,
            "CPP0101",
            "Tính Tổng Hai Số",
            "https://code.ptit.edu.vn/student/question/CPP0101",
            "Extension",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TabActive: true,
            BrowserName: "Edge");

        var r1 = await archA.ProcessFileSaveAsync(srcA, Encoding.UTF8.GetBytes("#include <iostream>\nint main(){ std::cout << \"OLD_V1\"; return 0; }\n"), ptitCtx);
        var r2 = await archA.ProcessFileSaveAsync(srcA, Encoding.UTF8.GetBytes("#include <iostream>\nint main(){ long long a,b; std::cin>>a>>b; std::cout<<a+b; return 0; }\n"), ptitCtx);
        Assert.NotNull(r1.Version);
        Assert.NotNull(r2.Version);

        await using var driveA = new GoogleDriveService(cfgA, dbA, credsA, http);
        var (pushA, _) = await driveA.SyncBidirectionalAsync();
        Assert.Equal(2, pushA.UploadedCount);
        Assert.Equal(0, pushA.FailedCount);

        // === PC-B: Completely fresh DB and empty local Archive dir ===
        var pcBDir = Path.Combine(_root, "PC_B");
        var cfgB = new GhostCodeConfig
        {
            ArchiveDir = Path.Combine(pcBDir, "Archive"),
            ReadyDir = Path.Combine(pcBDir, "Ready"),
            DatabasePath = Path.Combine(pcBDir, "Archive", "ghostcode.db"),
            OAuthClient = new GoogleOAuthClientConfig("test-client.apps.googleusercontent.com", "")
        };
        await using var dbB = new SqliteArchiveStore(cfgB.DatabasePath);
        await dbB.InitializeAsync();
        var archB = new LosslessArchiver(cfgB, dbB);
        var credsB = new DpapiCredentialStore(Path.Combine(pcBDir, "creds.dpapi"));
        await credsB.SaveAsync(new GoogleAuthTokens("tok-b", "ref-b", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(), "duc@example.com", "Duc"));

        await using var driveB = new GoogleDriveService(cfgB, dbB, credsB, http);
        var pullB = await driveB.PullAndReconcileFromDriveAsync();
        Assert.True(pullB.Connected);
        Assert.Null(pullB.Error);
        Assert.True(pullB.DiscoveredRemoteVersions >= 2);
        Assert.Equal(2, pullB.NewCloudOnlyCount);
        Assert.True(pullB.HistoryEventsImported >= 2);

        // Global accent-insensitive search finds CloudOnly version cleanly
        var searchMatches = await dbB.SearchArchiveAsync("tinh tong hai so");
        Assert.Equal(2, searchMatches.Count);
        Assert.All(searchMatches, m => Assert.Equal(VersionAvailability.CloudOnly, m.Version.Availability));
        Assert.All(searchMatches, m => Assert.Equal("Cloud Metadata", m.MatchKind));

        // On-demand download of v2 -> SHA-256 verified -> Local & runnable
        var v2OnB = searchMatches.Select(m => m.Version).Single(v => v.Id == r2.Version!.Id);
        var dl = await driveB.DownloadVersionAsync(v2OnB.Id);
        Assert.True(dl.Success, dl.Error);
        Assert.NotNull(dl.LocalArchivePath);
        Assert.True(File.Exists(dl.LocalArchivePath));

        var v2Updated = await dbB.GetVersionByIdAsync(v2OnB.Id);
        Assert.NotNull(v2Updated);
        Assert.True(v2Updated!.IsLocalAvailable);
        Assert.Equal(VersionAvailability.Synced, v2Updated.Availability);

        var runnerB = new CodeExecutionService(cfgB, dbB, archB, new ToolchainDiscoveryService());
        var runRes = await runnerB.ExecuteArchivedVersionAsync(new CodeExecutionRequest(v2Updated.Id, "4000000000 5000000000\n"));
        Assert.Equal(ExecutionStatus.Finished, runRes.Status);
        Assert.Equal("9000000000", runRes.Stdout.Trim());

        // === Conflict Preservation Test: Local offline v1 collides on same source_path with Remote v1 ===
        var pcCDir = Path.Combine(_root, "PC_C");
        var cfgC = new GhostCodeConfig
        {
            ArchiveDir = Path.Combine(pcCDir, "Archive"),
            ReadyDir = Path.Combine(pcCDir, "Ready"),
            DatabasePath = Path.Combine(pcCDir, "Archive", "ghostcode.db"),
            OAuthClient = new GoogleOAuthClientConfig("test-client.apps.googleusercontent.com", "")
        };
        await using var dbC = new SqliteArchiveStore(cfgC.DatabasePath);
        await dbC.InitializeAsync();
        var archC = new LosslessArchiver(cfgC, dbC);
        var credsC = new DpapiCredentialStore(Path.Combine(pcCDir, "creds.dpapi"));
        await credsC.SaveAsync(new GoogleAuthTokens("tok-c", "ref-c", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(), "duc@example.com", "Duc"));

        // Local device created its own distinct v1 at the identical path srcA before pulling
        await archC.ProcessFileSaveAsync(srcA, Encoding.UTF8.GetBytes("#include <iostream>\nint main(){ std::cout << \"LOCAL_OFFLINE_EDIT\"; return 0; }\n"), ptitCtx);
        await using var driveC = new GoogleDriveService(cfgC, dbC, credsC, http);
        var pullC = await driveC.PullAndReconcileFromDriveAsync();
        Assert.True(pullC.ConflictBranchCount >= 1);

        var cVersions = await dbC.GetVersionsBySourceAsync(srcA);
        Assert.True(cVersions.Count >= 3);
        Assert.Contains(cVersions, v => v.IsConflictBranch);
        var eventsC = await dbC.GetArchiveHistoryEventsAsync();
        Assert.Contains(eventsC, e => e.EventType == "ConflictDetected");
    }
}
