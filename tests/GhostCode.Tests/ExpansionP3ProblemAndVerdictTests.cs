using System.Net;
using System.Text;
using System.Text.Json;
using GhostCode.Core.Archiver;
using GhostCode.Core.Browser;
using GhostCode.Core.Execution;
using GhostCode.Core.Models;
using GhostCode.Core.Storage;
using GhostCode.Core.Toolchains;

namespace GhostCode.Tests;

public sealed class ExpansionP3ProblemAndVerdictTests : IDisposable
{
    private readonly string _root;

    public ExpansionP3ProblemAndVerdictTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GhostCode_ExpP3_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static async Task<string> AcquireSessionTokenAsync(HttpClient http, string baseUrl, string bridgeAuthToken)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/handshake");
        req.Headers.Add("Origin", "chrome-extension://bifmfiolmohcdfknnicfikhhpipkokbc");
        req.Headers.Add("X-GhostCode-Pairing-Id", AuthenticatedBrowserBridge.DefaultPairingId);
        req.Headers.Add("X-GhostCode-Nonce", nonce);
        req.Headers.Add("X-GhostCode-Timestamp", ts.ToString());
        req.Headers.Add("X-GhostCode-Proof", AuthenticatedBrowserBridge.ComputeHandshakeProof(bridgeAuthToken, nonce, ts));
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("session_token").GetString()!;
    }

    [Fact]
    public async Task AuthenticatedBrowserBridge_ProblemAndSubmission_RequireAuth_HonorSiteOptOut_AndPreserveVersionHonesty()
    {
        var watchDir = Path.Combine(_root, "watch");
        var archiveDir = Path.Combine(_root, "archive");
        var readyDir = Path.Combine(_root, "ready");
        Directory.CreateDirectory(watchDir);
        Directory.CreateDirectory(archiveDir);
        Directory.CreateDirectory(readyDir);

        var cfg = new GhostCodeConfig
        {
            HttpPort = 0,
            ArchiveDir = archiveDir,
            ReadyDir = readyDir,
            DatabasePath = Path.Combine(archiveDir, "ghost.db"),
            BridgeAuthToken = "p3-secret-bridge-token"
        };

        await using var store = new SqliteArchiveStore(cfg.DatabasePath);
        await store.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, store);

        // Archive two local versions for CF_1900A so verdict attribution can inspect latest without falsely marking v1 AC
        var src = Path.Combine(watchDir, "cover.cpp");
        var ctx = new ProblemContext(OjPlatform.Codeforces, "CF_1900A", "Cover in Water", "https://codeforces.com/problemset/problem/1900/A", "Extension", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var v1 = await archiver.ProcessFileSaveAsync(src, Encoding.UTF8.GetBytes("#include <iostream>\nint main(){ int a = 1; std::cout << a; return 0; }\n"), ctx);
        var v2 = await archiver.ProcessFileSaveAsync(src, Encoding.UTF8.GetBytes("#include <iostream>\nint main(){ int a = 2; std::cout << a * 2; return 0; }\n"), ctx);
        Assert.Equal(ArchiveOutcome.CreatedVersion, v1.Outcome);
        Assert.Equal(ArchiveOutcome.CreatedVersion, v2.Outcome);

        await using var bridge = new AuthenticatedBrowserBridge(cfg, store: store);
        bridge.Start();
        using var http = new HttpClient();
        var baseUrl = $"http://127.0.0.1:{bridge.ListeningPort}";

        // 1. Unauthenticated /problem must fail with 401
        var unauthProb = await http.PostAsync($"{baseUrl}/problem",
            new StringContent("""{"problem_code":"CF_1900A","url":"https://codeforces.com/problemset/problem/1900/A","tab_active":true}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthProb.StatusCode);

        var token = await AcquireSessionTokenAsync(http, baseUrl, cfg.BridgeAuthToken);

        // 2. Spoofed domain must be rejected with 400
        var spoofReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/problem");
        spoofReq.Headers.Add("X-GhostCode-Token", token);
        spoofReq.Content = new StringContent("""{"problem_code":"CF_1900A","url":"https://evil-codeforces.example/problem/1900/A","tab_active":true}""", Encoding.UTF8, "application/json");
        var spoofResp = await http.SendAsync(spoofReq);
        Assert.Equal(HttpStatusCode.BadRequest, spoofResp.StatusCode);

        // 3. Valid /problem stores problem statement and 2 samples in SQLite
        var probBody = JsonSerializer.Serialize(new
        {
            problem_code = "CF_1900A",
            problem_title = "Cover in Water",
            platform = "Codeforces",
            url = "https://codeforces.com/problemset/problem/1900/A",
            description = "Filip has a row of cells.",
            time_limit = "1 second",
            memory_limit = "256 megabytes",
            interactive_or_custom = false,
            samples = new[]
            {
                new { input = "2\n3\n...\n", output = "2\n" },
                new { input = "1\n1\n#\n", output = "0\n" }
            },
            tab_active = true
        });
        var probReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/problem");
        probReq.Headers.Add("X-GhostCode-Token", token);
        probReq.Content = new StringContent(probBody, Encoding.UTF8, "application/json");
        var probResp = await http.SendAsync(probReq);
        Assert.Equal(HttpStatusCode.OK, probResp.StatusCode);

        var pKey = ProblemStatementRecord.BuildKey("Codeforces", "CF_1900A");
        var storedStmt = await store.GetProblemStatementAsync(pKey);
        var storedSamples = await store.GetSampleTestsAsync(pKey);
        Assert.NotNull(storedStmt);
        Assert.Equal("1 second", storedStmt!.TimeLimit);
        Assert.Equal(2, storedSamples.Count);

        // 4. Valid /submission persists verdict with IsVersionCertain == false unless provably verified,
        // and NEVER retroactively marks v1 Accepted!
        var subBody = JsonSerializer.Serialize(new
        {
            platform = "Codeforces",
            problem_code = "CF_1900A",
            url = "https://codeforces.com/problemset/problem/1900/A",
            submission_id = "284001122",
            language = "GNU C++20",
            verdict = "Accepted",
            is_version_certain = false,
            tab_active = true
        });
        var subReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/submission");
        subReq.Headers.Add("X-GhostCode-Token", token);
        subReq.Content = new StringContent(subBody, Encoding.UTF8, "application/json");
        var subResp = await http.SendAsync(subReq);
        Assert.Equal(HttpStatusCode.OK, subResp.StatusCode);

        var subs = await store.GetSubmissionsForProblemAsync("CF_1900A", 10);
        Assert.Single(subs);
        Assert.Equal(SubmissionVerdict.Accepted, subs[0].Verdict);
        Assert.False(subs[0].IsVersionCertain);
        Assert.Equal(v2.Version!.Id, subs[0].VersionId);
        Assert.Null(await store.GetLatestVerdictForVersionAsync(v1.Version!.Id));
        Assert.Null(await store.GetLatestVerdictForVersionAsync(v2.Version!.Id));

        // 5. Per-site opt-out prevents storing problem statements from disabled domains
        cfg.WebsiteStatementCaptureEnabled["codeforces.com"] = false;
        var optOutReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/problem");
        optOutReq.Headers.Add("X-GhostCode-Token", token);
        optOutReq.Content = new StringContent("""{"problem_code":"CF_1900B","url":"https://codeforces.com/problemset/problem/1900/B","description":"Ignored","tab_active":true}""", Encoding.UTF8, "application/json");
        var optOutResp = await http.SendAsync(optOutReq);
        Assert.Equal(HttpStatusCode.Accepted, optOutResp.StatusCode);
        Assert.Null(await store.GetProblemStatementAsync(ProblemStatementRecord.BuildKey("Codeforces", "CF_1900B")));
    }

    [Fact]
    public async Task SampleTestRunnerService_RunsSamplesAndCustomTests_DistinguishesSamplePassedFromAc_AndShowsMismatchDiff()
    {
        var watchDir = Path.Combine(_root, "run_watch");
        var archiveDir = Path.Combine(_root, "run_archive");
        var readyDir = Path.Combine(_root, "run_ready");
        Directory.CreateDirectory(watchDir);
        Directory.CreateDirectory(archiveDir);
        Directory.CreateDirectory(readyDir);

        var cfg = new GhostCodeConfig
        {
            ArchiveDir = archiveDir,
            ReadyDir = readyDir,
            DatabasePath = Path.Combine(archiveDir, "ghost.db"),
            TrimTrailingWhitespaceOnCompare = true,
            ExecutionTimeoutMs = 5000
        };

        await using var store = new SqliteArchiveStore(cfg.DatabasePath);
        await store.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, store);
        var discovery = new ToolchainDiscoveryService();
        var runner = new CodeExecutionService(cfg, store, archiver, discovery);
        var sampleRunner = new SampleTestRunnerService(cfg, store, runner);

        // Ensure Python or C++ is available on host before picking language
        var tcs = discovery.DiscoverToolchains(cfg);
        bool hasPython = tcs.Any(t => t.Kind.Contains("Python", StringComparison.OrdinalIgnoreCase) && t.IsAvailable);
        bool hasCpp = tcs.Any(t => t.Kind.Contains("C++", StringComparison.OrdinalIgnoreCase) && t.IsAvailable);
        Assert.True(hasPython || hasCpp, "Expected Python or C++ compiler/interpreter on host for sample runner test.");

        var ctx = new ProblemContext(OjPlatform.Ptit, "J01001", "Double Input", "https://code.ptit.edu.vn/student/question/J01001", "Extension", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        string srcPath = Path.Combine(watchDir, hasPython ? "double.py" : "double.cpp");
        string code = hasPython
            ? "n = int(input().strip())\nprint(f\"{n * 2}   \")\n" // intentional trailing spaces to test TrimTrailingWhitespaceOnCompare
            : "#include <iostream>\nint main() { long long n; if (std::cin >> n) std::cout << (n * 2) << \"   \\n\"; return 0; }\n";

        var saved = await archiver.ProcessFileSaveAsync(srcPath, Encoding.UTF8.GetBytes(code), ctx);
        Assert.Equal(ArchiveOutcome.CreatedVersion, saved.Outcome);

        var pKey = ProblemStatementRecord.BuildKey("Ptit", "J01001");
        await store.UpsertSampleTestAsync(new SampleTestCaseRecord("s1", pKey, 1, "5\n", "10\n", ctx.Url, false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        await store.UpsertSampleTestAsync(new SampleTestCaseRecord("s2", pKey, 2, "7\n", "14\n", ctx.Url, false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        // Run single sample with trailing whitespace trim enabled -> Sample Passed (NOT Accepted AC)
        var samples = await store.GetSampleTestsAsync(pKey);
        var single = await sampleRunner.RunSingleSampleAsync(saved.Version!.Id, samples[0]);
        Assert.True(single.Passed);
        Assert.Equal("Sample Passed", single.StatusLabel);
        Assert.DoesNotContain("Accepted", single.StatusLabel, StringComparison.OrdinalIgnoreCase);

        // Strict whitespace mode fails due to trailing spaces!
        var strict = await sampleRunner.RunSingleSampleAsync(saved.Version.Id, samples[0], trimTrailingWhitespaceOverride: false);
        Assert.False(strict.Passed);
        Assert.Equal("Output Mismatch", strict.StatusLabel);

        // Add custom test with deliberately different expected output -> RunAllSamples reports 2/3 Sample Passed + diff
        var custom = await sampleRunner.AddCustomSampleAsync(pKey, "12\n", "999\n");
        Assert.True(custom.IsCustom);

        var suite = await sampleRunner.RunAllSamplesAsync(saved.Version.Id, pKey);
        Assert.Equal(3, suite.TotalCases);
        Assert.Equal(2, suite.PassedCases);
        Assert.Contains("Sample Passed", suite.SummaryLabel);
        Assert.DoesNotContain("Judge Accepted", suite.SummaryLabel);
        Assert.Contains("- 999", suite.CaseResults[2].UnifiedDiffText);
        Assert.Contains("+ 24", suite.CaseResults[2].UnifiedDiffText);
    }
}
