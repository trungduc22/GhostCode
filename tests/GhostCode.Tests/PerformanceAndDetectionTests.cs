using System.Diagnostics;
using GhostCode.Core.Archiver;
using GhostCode.Core.Browser;
using GhostCode.Core.Models;
using GhostCode.Core.Monitoring;
using GhostCode.Core.Storage;
using Xunit;

namespace GhostCode.Tests;

public sealed class PerformanceAndDetectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ghostcode_perf_test_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private GhostCodeConfig CreateConfig()
    {
        Directory.CreateDirectory(_root);
        return new GhostCodeConfig
        {
            ArchiveDir = Path.Combine(_root, "GhostCode"),
            ReadyDir = string.Empty,
            DatabasePath = Path.Combine(_root, "ghostcode.db"),
            HttpPort = 0,
            BridgeAuthToken = "test-pairing-secret-0123456789abcdef"
        };
    }

    [Fact]
    public async Task GetLatestBackupSummaryAndCloudOnlyStats_ReturnRealRecordsWithoutLoadingAllVersions()
    {
        var cfg = CreateConfig();
        await using var store = new SqliteArchiveStore(cfg.DatabasePath);
        await store.InitializeAsync();

        Assert.Null(await store.GetLatestBackupSummaryAsync());

        var archiver = new LosslessArchiver(cfg, store);
        var watchDir = Path.Combine(_root, "work");
        Directory.CreateDirectory(watchDir);
        cfg.WatchedFolders.Add(new WatchedFolderConfig(watchDir, true, true, "work"));

        var src = Path.Combine(watchDir, "sol.cpp");
        await File.WriteAllTextAsync(src, "#include <iostream>\nint main(){\n    int n = 42;\n    std::cout << n * 2 << '\\n';\n    return 0;\n}\n");
        var ctx = new ProblemContext(
            OjPlatform.Ptit,
            "C04037",
            "Liệt Kê Phần Tử",
            "https://code.ptit.edu.vn/student/question/C04037",
            "Extension",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            true,
            "Thorium");
        var res = await archiver.ProcessFileSaveAsync(src, await File.ReadAllBytesAsync(src), ctx);
        Assert.Equal(ArchiveOutcome.CreatedVersion, res.Outcome);

        var latest = await store.GetLatestBackupSummaryAsync();
        Assert.NotNull(latest);
        Assert.Equal("C04037_Liet_Ke_Phan_Tu.cpp", latest!.FileName);
        Assert.Equal("Ptit", latest.Platform);
        Assert.Equal("C04037", latest.ProblemCode);
        Assert.True(latest.CreatedAtUnixMs > 0);

        var stats = await store.GetStatsAsync();
        Assert.Equal(1, stats.TotalVersions);
        Assert.Equal(0, stats.CloudOnlyVersions);
    }

    [Fact]
    public async Task MultiDirectoryMonitor_ArchivesPromptlyViaSignalDrivenDebounce()
    {
        var cfg = CreateConfig();
        var watchDir = Path.Combine(_root, "src");
        Directory.CreateDirectory(watchDir);
        cfg.WatchedFolders.Add(new WatchedFolderConfig(watchDir, true, true, "src"));

        await using var store = new SqliteArchiveStore(cfg.DatabasePath);
        await store.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, store);

        var ctx = new ProblemContext(
            OjPlatform.Codeforces,
            "CF_1872A",
            "Two Vessels",
            "https://codeforces.com/problemset/problem/1872/A",
            "Extension",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            true,
            "Thorium");
        await using var monitor = new MultiDirectoryMonitor(cfg, store, archiver, () => ctx);
        var archivedTcs = new TaskCompletionSource<ArchiveVersionRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        archiver.VersionArchived += v => archivedTcs.TrySetResult(v);
        monitor.Start();

        var file = Path.Combine(watchDir, "main.cpp");
        await File.WriteAllTextAsync(file, "#include <iostream>\nint main(){\n    int a = 3, b = 5;\n    std::cout << (a + b) << '\\n';\n    return 0;\n}\n");

        var completed = await Task.WhenAny(archivedTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(archivedTcs.Task, completed);
        var ver = await archivedTcs.Task;
        Assert.Equal("CF_1872A_Two_Vessels.cpp", ver.FileName);
    }

    [Fact]
    public async Task AuthenticatedBrowserBridge_SkipsWin32FallbackScanWhenExtensionHeartbeatIsFresh()
    {
        var cfg = CreateConfig();
        await using var bridge = new AuthenticatedBrowserBridge(cfg, new BrowserIntegrationService(new FakeProbe()));

        Assert.True(bridge.ShouldRunWin32FallbackScan());
        bridge.RecordBrowserHeartbeat(BrowserKind.Thorium, "1.4.0");
        Assert.False(bridge.ShouldRunWin32FallbackScan());
    }

    [Fact]
    public void BrowserIntegrationService_CachesStaticExeDiscoveryAcrossRapidCalls()
    {
        var probe = new CountingProbe();
        var svc = new BrowserIntegrationService(probe);
        var cfg = CreateConfig();

        var first = svc.DiscoverInstalledBrowsers(cfg);
        int fileChecksAfterFirst = probe.FileExistsCalls;
        var second = svc.DiscoverInstalledBrowsers(cfg);

        Assert.Equal(first.Count, second.Count);
        Assert.Equal(fileChecksAfterFirst, probe.FileExistsCalls);

        var forced = svc.DiscoverInstalledBrowsers(cfg, forceRescan: true);
        Assert.Equal(first.Count, forced.Count);
        Assert.True(probe.FileExistsCalls > fileChecksAfterFirst);
    }

    [Fact]
    public async Task AuthenticatedBrowserBridge_25ContextSwitchesMeetSub50MsCoreLatency()
    {
        var cfg = CreateConfig();
        await using var bridge = new AuthenticatedBrowserBridge(cfg, new BrowserIntegrationService(new FakeProbe()));
        var observedLatencies = new List<double>();

        for (int i = 0; i < 25; i++)
        {
            var code = $"P{i:D4}";
            var url = $"https://code.ptit.edu.vn/student/question/{code}";
            var sw = Stopwatch.StartNew();
            var tcs = new TaskCompletionSource<ProblemContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(ProblemContext? c)
            {
                if (c?.Code == code)
                    tcs.TrySetResult(c);
            }
            bridge.ContextChanged += Handler;
            var (status, _) = bridge.ApplyContextPayload(new BrowserContextPayload(
                code,
                $"Problem {i}",
                "PTIT",
                url,
                true,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                "Thorium"));
            Assert.Equal(200, status);
            var ctx = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
            sw.Stop();
            bridge.ContextChanged -= Handler;
            Assert.Equal(code, ctx?.Code);
            observedLatencies.Add(sw.Elapsed.TotalMilliseconds);
        }

        observedLatencies.Sort();
        var p95 = observedLatencies[(int)Math.Ceiling(observedLatencies.Count * 0.95) - 1];
        Assert.True(p95 < 50.0, $"Expected core context switch p95 < 50ms, actual {p95:F2}ms");
    }

    private sealed class FakeProbe : IBrowserEnvironmentProbe
    {
        public bool FileExists(string path) => false;
        public IReadOnlyCollection<string> GetRunningProcessNames() => Array.Empty<string>();
        public string? GetFileVersion(string path) => null;
        public string? ReadRegistryString(string fullKeyPath, string valueName) => null;
    }

    private sealed class CountingProbe : IBrowserEnvironmentProbe
    {
        public int FileExistsCalls { get; private set; }
        public bool FileExists(string path)
        {
            FileExistsCalls++;
            return path.EndsWith("thorium.exe", StringComparison.OrdinalIgnoreCase);
        }
        public IReadOnlyCollection<string> GetRunningProcessNames() => new[] { "thorium" };
        public string? GetFileVersion(string path) => "130.0.0.0";
        public string? ReadRegistryString(string fullKeyPath, string valueName) => null;
    }
}
