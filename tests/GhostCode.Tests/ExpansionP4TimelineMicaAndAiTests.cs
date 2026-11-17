using System.Net;
using System.Text;
using System.Text.Json;
using GhostCode.Core.Ai;
using GhostCode.Core.Archiver;
using GhostCode.Core.Cloud;
using GhostCode.Core.Models;
using GhostCode.Core.Monitoring;
using GhostCode.Core.Storage;

namespace GhostCode.Tests;

public sealed class ExpansionP4TimelineMicaAndAiTests : IDisposable
{
    private readonly string _root;

    public ExpansionP4TimelineMicaAndAiTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GhostCode_ExpP4_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class RecordingAiHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var json = JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new { message = new { content = "Check loop bounds for off-by-one indexing." } }
                }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task ProblemTimeline_ReturnsChronologicalUtcEventsForProblem()
    {
        var watchDir = Path.Combine(_root, "tl_watch");
        var archiveDir = Path.Combine(_root, "tl_archive");
        var readyDir = Path.Combine(_root, "tl_ready");
        Directory.CreateDirectory(watchDir);
        Directory.CreateDirectory(archiveDir);
        Directory.CreateDirectory(readyDir);

        var cfg = new GhostCodeConfig
        {
            ArchiveDir = archiveDir,
            ReadyDir = readyDir,
            DatabasePath = Path.Combine(archiveDir, "ghost.db")
        };

        await using var store = new SqliteArchiveStore(cfg.DatabasePath);
        await store.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, store);

        var src = Path.Combine(watchDir, "sol.cpp");
        var ctx = new ProblemContext(OjPlatform.Ptit, "J02002", "Timeline Test", "https://code.ptit.edu.vn/student/question/J02002", "Extension", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await archiver.ProcessFileSaveAsync(src, Encoding.UTF8.GetBytes("#include <iostream>\nint main(){ int a = 10; std::cout << a; return 0; }\n"), ctx);
        await Task.Delay(15);
        await archiver.ProcessFileSaveAsync(src, Encoding.UTF8.GetBytes("#include <iostream>\nint main(){ int a = 20; std::cout << a * 2; return 0; }\n"), ctx);

        await store.UpsertSubmissionRecordAsync(new SubmissionRecord(
            "sub_tl_1", "Ptit", "J02002", "S10", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "C++17", SubmissionVerdict.Accepted, 10, 512, null, false));

        var timeline = await store.GetTimelineForProblemAsync("J02002", 50);
        Assert.True(timeline.Count >= 3);
        for (int i = 1; i < timeline.Count; i++)
        {
            Assert.True(timeline[i - 1].OccurredAtUnixMs <= timeline[i].OccurredAtUnixMs,
                $"Expected ascending UTC timeline order at index {i}");
        }
        Assert.Contains(timeline, e => e.EventType == "SubmissionVerdict");
    }

    [Fact]
    public async Task CodingTimeTracker_EnforcesOptInAndIdleCutoff_AndRemainsExcludedFromCloudManifest()
    {
        var cfg = new GhostCodeConfig { EstimateActiveCodingTime = false };
        var tracker = new CodingTimeTracker(cfg, idleCutoffMs: 180_000);

        // Opt-out default ignores activity cleanly
        tracker.RecordActivity("J01001", 1_000_000);
        tracker.RecordActivity("J01001", 1_060_000);
        Assert.Equal(0, tracker.GetEstimatedDurationMs("J01001"));
        Assert.Contains("disabled", tracker.FormatEstimatedDuration("J01001"), StringComparison.OrdinalIgnoreCase);

        // Enable opt-in: accumulates 60s + 90s = 150s, ignores 400s idle gap (> 180s)
        cfg.EstimateActiveCodingTime = true;
        tracker.RecordActivity("J01001", 1_000_000);
        tracker.RecordActivity("J01001", 1_060_000); // +60s
        tracker.RecordActivity("J01001", 1_460_000); // +400s idle -> ignored!
        tracker.RecordActivity("J01001", 1_550_000); // +90s

        Assert.Equal(150_000, tracker.GetEstimatedDurationMs("J01001"));
        Assert.StartsWith("Estimated:", tracker.FormatEstimatedDuration("J01001"));

        // Verify cloud manifest never includes coding time telemetry
        var archiveDir = Path.Combine(_root, "manifest_check");
        Directory.CreateDirectory(archiveDir);
        await using var store = new SqliteArchiveStore(Path.Combine(archiveDir, "ghost.db"));
        await store.InitializeAsync();
        var manifestJson = Encoding.UTF8.GetString(await CloudManifestCodec.BuildManifestBytesAsync(store));
        Assert.DoesNotContain("EstimatedDuration", manifestJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CopilotAssistanceService_RequiresExplicitOptInAndToken_NeverScrapes_AndLabelsSuggestions()
    {
        var cfg = new GhostCodeConfig();
        Assert.False(cfg.AiAssistanceOptIn);

        var handler = new RecordingAiHandler();
        var ai = new CopilotAssistanceService(cfg, new HttpClient(handler));

        // 1. Default disabled -> blocks network calls completely even if token configured
        ai.ConfigureUserProvidedToken("user-explicit-token");
        var blocked = await ai.RequestExplanationOrHintAsync("int main(){}", "Explain");
        Assert.False(blocked.Succeeded);
        Assert.Equal(0, handler.Calls);
        Assert.Contains("disabled", blocked.StatusNote, StringComparison.OrdinalIgnoreCase);

        // 2. Opt-in enabled without user-provided token -> still never scrapes VS Code or makes calls
        cfg.AiAssistanceOptIn = true;
        ai.ConfigureUserProvidedToken(null);
        var noTok = await ai.RequestExplanationOrHintAsync("int main(){}", "Explain");
        Assert.False(noTok.Succeeded);
        Assert.Equal(0, handler.Calls);

        // 3. Opt-in + explicit user token -> sends request once and prepends mandatory disclaimer
        ai.ConfigureUserProvidedToken("user-explicit-token", "https://example.invalid/chat/completions");
        var ok = await ai.RequestExplanationOrHintAsync("for(int i=0;i<=n;i++)", "Find bug");
        Assert.True(ok.Succeeded);
        Assert.Equal(1, handler.Calls);
        Assert.StartsWith(CopilotAssistanceService.RequiredDisclaimer, ok.Content);
    }
}
