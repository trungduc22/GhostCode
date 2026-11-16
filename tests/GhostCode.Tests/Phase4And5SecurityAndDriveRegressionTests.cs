using System.Net;
using System.Text;
using System.Text.Json;
using GhostCode.Core.Archiver;
using GhostCode.Core.Browser;
using GhostCode.Core.Cloud;
using GhostCode.Core.Models;
using GhostCode.Core.Storage;
using Microsoft.Data.Sqlite;

namespace GhostCode.Tests;

public sealed class Phase4And5SecurityAndDriveRegressionTests : IDisposable
{
    private readonly string _root;

    public Phase4And5SecurityAndDriveRegressionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GhostCode_P45_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static async Task<(HttpStatusCode Status, string Body, string? SessionToken)> DoHmacHandshakeAsync(
        HttpClient http,
        string baseUrl,
        string pairingSecret,
        string nonce,
        long timestampMs,
        string origin = "chrome-extension://bifmfiolmohcdfknnicfikhhpipkokbc")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/handshake");
        if (!string.IsNullOrEmpty(origin))
            req.Headers.Add("Origin", origin);
        req.Headers.Add("X-GhostCode-Pairing-Id", AuthenticatedBrowserBridge.DefaultPairingId);
        req.Headers.Add("X-GhostCode-Nonce", nonce);
        req.Headers.Add("X-GhostCode-Timestamp", timestampMs.ToString());
        req.Headers.Add("X-GhostCode-Proof", AuthenticatedBrowserBridge.ComputeHandshakeProof(pairingSecret, nonce, timestampMs));
        var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        string? tok = null;
        if (resp.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(body);
            tok = doc.RootElement.GetProperty("session_token").GetString();
        }
        return (resp.StatusCode, body, tok);
    }

    [Fact]
    public async Task Sec1_GetHandshakeWithSpoofedExtensionOrigin_MustRejectAndNeverExposeSecret()
    {
        var cfg = new GhostCodeConfig { HttpPort = 0, BridgeAuthToken = "top-secret-pairing-key" };
        await using var bridge = new AuthenticatedBrowserBridge(cfg);
        bridge.Start();

        using var http = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{bridge.ListeningPort}/handshake");
        req.Headers.Add("Origin", "chrome-extension://maliciousattackerextensionid");
        var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.DoesNotContain(cfg.BridgeAuthToken, body);
    }

    [Fact]
    public async Task Sec1_StatusFromArbitraryWebOrigin_MustNotEmitWildcardCors()
    {
        var cfg = new GhostCodeConfig { HttpPort = 0, BridgeAuthToken = "pairing-key" };
        await using var bridge = new AuthenticatedBrowserBridge(cfg);
        bridge.Start();

        using var http = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{bridge.ListeningPort}/status");
        req.Headers.Add("Origin", "https://evil-website.example");
        var resp = await http.SendAsync(req);

        Assert.False(resp.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Sec2_PostContext_RejectsUnsupportedSpoofedDomainAndMalformedPayloads()
    {
        var cfg = new GhostCodeConfig { HttpPort = 0, BridgeAuthToken = "pairing-key-sec2" };
        await using var bridge = new AuthenticatedBrowserBridge(cfg);
        bridge.Start();
        var baseUrl = $"http://127.0.0.1:{bridge.ListeningPort}";

        using var http = new HttpClient();
        var (_, _, sessionToken) = await DoHmacHandshakeAsync(http, baseUrl, cfg.BridgeAuthToken, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.NotNull(sessionToken);

        foreach (var badJson in new[]
        {
            """{"problem_code":"CF_1A","url":"https://evil-codeforces.com/problemset/problem/1/A","tab_active":true}""",
            """{"problem_code":"C04015","url":"","tab_active":true}""",
            """{"problem_code":"C04015","url":"https://example.com/random","tab_active":true}""",
            "{ malformed json"
        })
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/context");
            req.Headers.Add("X-GhostCode-Token", sessionToken);
            req.Content = new StringContent(badJson, Encoding.UTF8, "application/json");
            var resp = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        Assert.Null(bridge.GetActiveContext());
    }

    [Fact]
    public void Sec3_ConfigJsonAndExport_MustNotLeakClientSecretOrBridgeSecret()
    {
        var cfgPath = Path.Combine(_root, "config.json");
        var cfg = new GhostCodeConfig
        {
            ArchiveDir = Path.Combine(_root, "Archive"),
            ReadyDir = Path.Combine(_root, "Ready"),
            DatabasePath = Path.Combine(_root, "Archive", "ghost.db"),
            BridgeAuthToken = "super-secret-bridge-hex",
            OAuthClient = new GoogleOAuthClientConfig("pub-client.apps.googleusercontent.com", "super-secret-oauth-client")
        };
        cfg.Save(cfgPath);

        var diskJson = File.ReadAllText(cfgPath);
        var exportJson = cfg.ExportToJson();

        Assert.DoesNotContain("super-secret-oauth-client", diskJson);
        Assert.DoesNotContain("super-secret-oauth-client", exportJson);
        Assert.DoesNotContain("super-secret-bridge-hex", exportJson);
    }

    [Fact]
    public async Task Sec4_HmacHandshake_IssuesSessionToken_RejectsNonceReplay_AndEnforcesTokenRotation()
    {
        var cfg = new GhostCodeConfig { HttpPort = 0, BridgeAuthToken = "hmac-rotate-secret" };
        await using var bridge = new AuthenticatedBrowserBridge(cfg);
        bridge.Start();
        var baseUrl = $"http://127.0.0.1:{bridge.ListeningPort}";

        using var http = new HttpClient();
        var nonce = "unique-nonce-" + Guid.NewGuid().ToString("N");
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Web origin must be forbidden even with valid headers
        var webRes = await DoHmacHandshakeAsync(http, baseUrl, cfg.BridgeAuthToken, "web-nonce", ts, "https://attacker.example");
        Assert.Equal(HttpStatusCode.Forbidden, webRes.Status);

        // Valid extension handshake succeeds
        var okRes = await DoHmacHandshakeAsync(http, baseUrl, cfg.BridgeAuthToken, nonce, ts);
        Assert.Equal(HttpStatusCode.OK, okRes.Status);
        Assert.NotNull(okRes.SessionToken);
        Assert.NotEqual(cfg.BridgeAuthToken, okRes.SessionToken);

        // Replay exact same nonce -> rejected 403
        var replayRes = await DoHmacHandshakeAsync(http, baseUrl, cfg.BridgeAuthToken, nonce, ts);
        Assert.Equal(HttpStatusCode.Forbidden, replayRes.Status);

        // Rotate token -> previous session_token must immediately fail with 401
        var oldToken = okRes.SessionToken!;
        var newToken = bridge.RotateSessionToken();
        Assert.NotEqual(oldToken, newToken);

        var oldReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/context");
        oldReq.Headers.Add("X-GhostCode-Token", oldToken);
        oldReq.Content = new StringContent("""{"url":"https://code.ptit.edu.vn/student/question/C04015","problem_title":"Test","tab_active":true}""", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(oldReq)).StatusCode);

        var newReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/context");
        newReq.Headers.Add("X-GhostCode-Token", newToken);
        newReq.Content = new StringContent("""{"url":"https://code.ptit.edu.vn/student/question/C04015","problem_title":"Test","tab_active":true}""", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(newReq)).StatusCode);
    }

    [Fact]
    public async Task Sec5_ContextClearEndpoint_PreventsStaleContextFromAttachingToNewSaves()
    {
        var cfg = new GhostCodeConfig { HttpPort = 0, BridgeAuthToken = "clear-secret" };
        await using var bridge = new AuthenticatedBrowserBridge(cfg);
        bridge.Start();
        var baseUrl = $"http://127.0.0.1:{bridge.ListeningPort}";

        using var http = new HttpClient();
        var (_, _, sessionToken) = await DoHmacHandshakeAsync(http, baseUrl, cfg.BridgeAuthToken, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var setReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/context");
        setReq.Headers.Add("X-GhostCode-Token", sessionToken);
        setReq.Content = new StringContent("""{"url":"https://codeforces.com/problemset/problem/1900/A","problem_title":"Cover in Water","tab_active":true}""", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(setReq)).StatusCode);
        Assert.NotNull(bridge.GetActiveContext());

        // Switching away from OJ page invokes POST /context/clear
        var clrReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/context/clear");
        clrReq.Headers.Add("X-GhostCode-Token", sessionToken);
        clrReq.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(clrReq)).StatusCode);
        Assert.Null(bridge.GetActiveContext());
    }

    [Fact]
    public async Task Sec6_NativeMessagingHost_HandlesLengthPrefixedProtocolEndToEnd()
    {
        var cfg = new GhostCodeConfig { HttpPort = 0, BridgeAuthToken = "nm-secret" };
        await using var bridge = new AuthenticatedBrowserBridge(cfg);
        bridge.Start();
        var host = new NativeMessagingHost(bridge);

        await using var inStream = new MemoryStream();
        await using var outStream = new MemoryStream();

        await NativeMessagingHost.WriteMessageAsync(inStream, """{"type":"handshake"}""");
        await NativeMessagingHost.WriteMessageAsync(inStream, """{"type":"context","payload":{"url":"https://code.ptit.edu.vn/student/question/CPP0101","problem_title":"Tinh Tong","tab_active":true}}""");
        await NativeMessagingHost.WriteMessageAsync(inStream, """{"type":"status"}""");
        await NativeMessagingHost.WriteMessageAsync(inStream, """{"type":"clear_context"}""");
        inStream.Position = 0;

        while (await host.ProcessNextMessageAsync(inStream, outStream)) { }

        Assert.Null(bridge.GetActiveContext());
        Assert.True(outStream.Length > 0);
    }

    private sealed class MockPhase5Handler : HttpMessageHandler
    {
        public bool FailTokenExchange { get; set; }
        public bool ReturnBadSha256OnUpload { get; set; }
        public bool FailRevoke { get; set; }
        public string? LastTokenExchangeBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url.StartsWith("https://oauth2.googleapis.com/token", StringComparison.Ordinal))
            {
                LastTokenExchangeBody = request.Content is not null ? await request.Content.ReadAsStringAsync(cancellationToken) : null;
                if (FailTokenExchange)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("""{"error":"invalid_grant","error_description":"Code expired"}""", Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"acc_p5","refresh_token":"ref_p5","expires_in":3600}""", Encoding.UTF8, "application/json")
                };
            }

            if (url.StartsWith("https://www.googleapis.com/oauth2/v2/userinfo", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"email":"student@ptit.edu.vn","name":"Duc"}""", Encoding.UTF8, "application/json")
                };
            }

            if (url.StartsWith("https://oauth2.googleapis.com/revoke", StringComparison.Ordinal))
            {
                if (FailRevoke)
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (url.StartsWith("https://www.googleapis.com/drive/v3/files", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
            {
                if (url.Contains("mimeType", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"files":[{"id":"folder_1","name":"CodeArchive"}]}""", Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"files":[]}""", Encoding.UTF8, "application/json") };
            }

            if (url.StartsWith("https://www.googleapis.com/upload/drive/v3/files", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                var returnedSha = ReturnBadSha256OnUpload ? "0000000000000000000000000000000000000000000000000000000000000000" : "";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""{"id":"drive_up_1","name":"file.cpp","sha256Checksum":"{{returnedSha}}"}""", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Cloud1_OAuthBrowserCallback_MustNotSayConnectedWhenTokenExchangeFails()
    {
        var cfg = new GhostCodeConfig
        {
            ArchiveDir = Path.Combine(_root, "Archive"),
            ReadyDir = Path.Combine(_root, "Ready"),
            DatabasePath = Path.Combine(_root, "Archive", "ghost.db"),
            OAuthClient = new GoogleOAuthClientConfig("client.apps.googleusercontent.com", "")
        };
        await using var db = new SqliteArchiveStore(cfg.DatabasePath);
        await db.InitializeAsync();
        var creds = new DpapiCredentialStore(Path.Combine(_root, "creds.dpapi"));
        var handler = new MockPhase5Handler { FailTokenExchange = true };
        await using var drive = new GoogleDriveService(cfg, db, creds, new HttpClient(handler));

        string browserHtml = string.Empty;
        HttpStatusCode browserStatus = 0;

        await Assert.ThrowsAnyAsync<Exception>(() => drive.ConnectWithBrowserAsync(authUrl =>
        {
            _ = Task.Run(async () =>
            {
                var uri = new Uri(authUrl);
                var query = uri.Query.TrimStart('?').Split('&')
                    .Select(p => p.Split('='))
                    .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
                using var http = new HttpClient();
                var resp = await http.GetAsync($"{query["redirect_uri"]}?code=bad_code&state={Uri.EscapeDataString(query["state"])}");
                browserStatus = resp.StatusCode;
                browserHtml = await resp.Content.ReadAsStringAsync();
            });
        }));

        Assert.NotEqual(HttpStatusCode.OK, browserStatus);
        Assert.DoesNotContain("GhostCode connected to Google Drive!", browserHtml);
        Assert.Null(await creds.LoadAsync());
    }

    [Fact]
    public async Task Cloud2_UploadMultipart_VerifiesRemoteSha256AndFailsOnChecksumMismatch()
    {
        var cfg = new GhostCodeConfig
        {
            ArchiveDir = Path.Combine(_root, "Archive2"),
            ReadyDir = Path.Combine(_root, "Ready2"),
            DatabasePath = Path.Combine(_root, "Archive2", "ghost.db"),
            OAuthClient = new GoogleOAuthClientConfig("client.apps.googleusercontent.com", "")
        };
        await using var db = new SqliteArchiveStore(cfg.DatabasePath);
        await db.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, db);
        var creds = new DpapiCredentialStore(Path.Combine(_root, "creds2.dpapi"));
        await creds.SaveAsync(new GoogleAuthTokens("acc", "ref", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(), "u@e.com", "User"));
        var handler = new MockPhase5Handler { ReturnBadSha256OnUpload = true };
        await using var drive = new GoogleDriveService(cfg, db, creds, new HttpClient(handler));

        await archiver.ProcessFileSaveAsync(
            Path.Combine(_root, "sha_check.cpp"),
            Encoding.UTF8.GetBytes("#include <iostream>\nint main(){ std::cout << 55; }\n"),
            null);

        var rep = await drive.SyncPendingAsync();
        Assert.Equal(0, rep.UploadedCount);
        Assert.Equal(1, rep.FailedCount);
        var q = (await db.GetAllUploadQueueAsync()).Single();
        Assert.Equal(UploadStatus.Failed, q.Queue.Status);
        Assert.Contains("SHA-256 mismatch", q.Queue.LastError);
    }

    [Fact]
    public async Task Cloud3And4_DpapiClientSecretAndDisconnectRemoteFailureReporting()
    {
        var cfg = new GhostCodeConfig
        {
            ArchiveDir = Path.Combine(_root, "Archive3"),
            ReadyDir = Path.Combine(_root, "Ready3"),
            DatabasePath = Path.Combine(_root, "Archive3", "ghost.db"),
            OAuthClient = new GoogleOAuthClientConfig("client.apps.googleusercontent.com", "")
        };
        await using var db = new SqliteArchiveStore(cfg.DatabasePath);
        await db.InitializeAsync();
        var creds = new DpapiCredentialStore(Path.Combine(_root, "creds3.dpapi"));
        await creds.SaveClientSecretAsync("dpapi-client-secret-xyz");
        var rawSecretCipher = await File.ReadAllBytesAsync(creds.ClientSecretFilePath);
        Assert.DoesNotContain("dpapi-client-secret-xyz", Encoding.UTF8.GetString(rawSecretCipher));

        var handler = new MockPhase5Handler { FailRevoke = true };
        await using var drive = new GoogleDriveService(cfg, db, creds, new HttpClient(handler));

        var tokens = await drive.ConnectWithBrowserAsync(authUrl =>
        {
            _ = Task.Run(async () =>
            {
                var uri = new Uri(authUrl);
                var query = uri.Query.TrimStart('?').Split('&')
                    .Select(p => p.Split('='))
                    .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
                using var http = new HttpClient();
                await http.GetAsync($"{query["redirect_uri"]}?code=ok_code&state={Uri.EscapeDataString(query["state"])}");
            });
        });

        Assert.Equal("student@ptit.edu.vn", tokens.Email);
        Assert.Contains("dpapi-client-secret-xyz", handler.LastTokenExchangeBody ?? string.Empty);

        var disc = await drive.DisconnectAsync();
        Assert.True(disc.LocalCredentialsCleared);
        Assert.False(disc.RemoteRevocationSucceeded);
        Assert.Contains("503", disc.RemoteError);
        Assert.Null(await creds.LoadAsync());
    }
}
