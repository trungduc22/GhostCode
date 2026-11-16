using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GhostCode.Core.Archiver;
using GhostCode.Core.Browser;
using GhostCode.Core.Cloud;
using GhostCode.Core.Models;
using GhostCode.Core.Storage;

namespace GhostCode.Tests;

public class PhaseDCloudAndBrowserTests : IDisposable
{
    private readonly string _root;

    public PhaseDCloudAndBrowserTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GhostCode_PhaseD_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class MockGoogleHandler : HttpMessageHandler
    {
        public bool FailUploads { get; set; }
        public int UploadCalls { get; private set; }
        public int RevokeCalls { get; private set; }
        public List<string> UploadedNames { get; } = new();
        public HashSet<string> KnownSha256s { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            if (url.StartsWith("https://oauth2.googleapis.com/token", StringComparison.Ordinal))
            {
                var json = """{"access_token":"mock_access_1","refresh_token":"mock_refresh_1","expires_in":3600,"token_type":"Bearer"}""";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }

            if (url.StartsWith("https://oauth2.googleapis.com/revoke", StringComparison.Ordinal))
            {
                RevokeCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (url.StartsWith("https://www.googleapis.com/oauth2/v2/userinfo", StringComparison.Ordinal))
            {
                var json = """{"email":"student@ptit.edu.vn","name":"Duc Student"}""";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }

            if (url.StartsWith("https://www.googleapis.com/drive/v3/files", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
            {
                if (url.Contains("mimeType", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"files":[{"id":"folder_codearchive_1","name":"CodeArchive"}]}""", Encoding.UTF8, "application/json")
                    };
                }
                // Duplicate SHA256 lookup
                foreach (var sha in KnownSha256s)
                {
                    if (url.Contains(sha, StringComparison.Ordinal))
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent($$"""{"files":[{"id":"existing_{{sha[..6]}}","name":"dup.cpp"}]}""", Encoding.UTF8, "application/json")
                        };
                    }
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"files":[]}""", Encoding.UTF8, "application/json") };
            }

            if (url.StartsWith("https://www.googleapis.com/upload/drive/v3/files", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                if (FailUploads)
                    throw new HttpRequestException("Simulated KTX Wi-Fi outage");

                UploadCalls++;
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                UploadedNames.Add(body);
                var id = $"drive_file_{UploadCalls}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""{"id":"{{id}}","name":"uploaded.cpp"}""", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task DpapiStore_EncryptsAndDecryptsTokensForCurrentUser()
    {
        var credPath = Path.Combine(_root, "credentials.dpapi");
        var store = new DpapiCredentialStore(credPath);
        var tok = new GoogleAuthTokens("acc_secret_xyz", "ref_secret_abc", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(), "duc@ptit.edu.vn", "Duc");

        await store.SaveAsync(tok);
        var rawCipher = await File.ReadAllBytesAsync(credPath);
        // Plaintext secret must NEVER appear unencrypted on disk
        Assert.DoesNotContain("acc_secret_xyz", Encoding.UTF8.GetString(rawCipher));

        var loaded = await store.LoadAsync();
        Assert.NotNull(loaded);
        Assert.Equal("acc_secret_xyz", loaded!.AccessToken);
        Assert.Equal("duc@ptit.edu.vn", loaded.Email);

        await store.ClearAsync();
        Assert.False(File.Exists(credPath));
    }

    [Fact]
    public async Task GoogleDriveService_HandlesNetworkFailureBackoffRecoveryDeduplicationAndDisconnect()
    {
        var cfg = new GhostCodeConfig
        {
            ArchiveDir = Path.Combine(_root, "CodeArchive"),
            ReadyDir = Path.Combine(_root, "Ready"),
            DatabasePath = Path.Combine(_root, "CodeArchive", "ghostcode.db"),
            OAuthClient = new GoogleOAuthClientConfig("test-client-id.apps.googleusercontent.com", "test-secret")
        };

        await using var db = new SqliteArchiveStore(cfg.DatabasePath);
        await db.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, db);
        var credStore = new DpapiCredentialStore(Path.Combine(_root, "credentials.dpapi"));
        await credStore.SaveAsync(new GoogleAuthTokens("tok1", "ref1", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(), "student@ptit.edu.vn", "Duc Student"));

        var handler = new MockGoogleHandler { FailUploads = true };
        var drive = new GoogleDriveService(cfg, db, credStore, new HttpClient(handler));

        var src = Path.Combine(_root, "Desktop", "Untitled1.cpp");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        var bytes = Encoding.UTF8.GetBytes("#include <stdio.h>\nint main(){ printf(\"ok\"); return 0; }\n");
        var archRes = await archiver.ProcessFileSaveAsync(src, bytes, null);
        Assert.Equal(ArchiveOutcome.CreatedVersion, archRes.Outcome);

        // 1. Upload fails during network outage -> status Failed, AttemptCount = 1, NextRetryAt > now
        var reportFail = await drive.SyncPendingAsync();
        Assert.Equal(0, reportFail.UploadedCount);
        Assert.Equal(1, reportFail.FailedCount);
        var q1 = (await db.GetAllUploadQueueAsync()).Single();
        Assert.Equal(UploadStatus.Failed, q1.Queue.Status);
        Assert.Equal(1, q1.Queue.AttemptCount);
        Assert.True(q1.Queue.NextRetryAtUnixMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        // 2. Network recovers + user triggers manual retry -> uploads & confirms server ID
        handler.FailUploads = false;
        await db.ResetFailedUploadsForRetryAsync();
        var reportOk = await drive.SyncPendingAsync();
        Assert.Equal(1, reportOk.UploadedCount);
        Assert.Equal(0, reportOk.FailedCount);
        Assert.Equal(1, handler.UploadCalls);

        var q2 = (await db.GetAllUploadQueueAsync()).Single();
        Assert.Equal(UploadStatus.Synced, q2.Queue.Status);
        Assert.Equal("drive_file_1", q2.Queue.DriveFileId);

        // 3. Disconnect revokes token & wipes DPAPI file
        await drive.DisconnectAsync();
        Assert.Equal(1, handler.RevokeCalls);
        Assert.Null(await credStore.LoadAsync());
    }

    [Fact]
    public async Task AuthenticatedBrowserBridge_RejectsUnauthenticatedAndBackgroundTabsAndExpiresStaleContext()
    {
        var cfg = new GhostCodeConfig
        {
            HttpPort = 0, // Let OS assign free ephemeral port
            ContextExpirationSeconds = 2,
            BridgeAuthToken = "secret-bridge-token-123"
        };

        await using var bridge = new AuthenticatedBrowserBridge(cfg);
        bridge.Start();
        Assert.True(bridge.ListeningPort > 0);

        using var http = new HttpClient();
        var baseUrl = $"http://127.0.0.1:{bridge.ListeningPort}";

        // 1. Unauthenticated POST must be rejected (401)
        var unauthResp = await http.PostAsync($"{baseUrl}/context",
            new StringContent("""{"problem_code":"C04015","problem_title":"Demo","platform":"PTIT","url":"https://code.ptit.edu.vn/student/question/C04015","tab_active":true}""",
            Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthResp.StatusCode);

        // 2. Inactive background tab must NOT overwrite active problem
        var reqBg = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/context");
        reqBg.Headers.Add("X-GhostCode-Token", cfg.BridgeAuthToken);
        reqBg.Content = new StringContent("""{"problem_code":"C01001","problem_title":"BgTab","platform":"PTIT","url":"https://code.ptit.edu.vn/student/question/C01001","tab_active":false}""",
            Encoding.UTF8, "application/json");
        var bgResp = await http.SendAsync(reqBg);
        Assert.Equal(HttpStatusCode.Accepted, bgResp.StatusCode);
        Assert.Null(bridge.GetActiveContext());

        // 3. Valid active tab updates context, then expires after TTL
        var reqOk = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/context");
        reqOk.Headers.Add("X-GhostCode-Token", cfg.BridgeAuthToken);
        reqOk.Content = new StringContent("""{"problem_code":"CF_1900A","problem_title":"Cover in Water","platform":"Codeforces","url":"https://codeforces.com/problemset/problem/1900/A","tab_active":true}""",
            Encoding.UTF8, "application/json");
        var okResp = await http.SendAsync(reqOk);
        Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
        var active = bridge.GetActiveContext();
        Assert.NotNull(active);
        Assert.Equal("CF_1900A", active!.Code);

        // Wait past 2s TTL -> stale context must expire to null
        await Task.Delay(2300);
        Assert.Null(bridge.GetActiveContext());
    }
}
