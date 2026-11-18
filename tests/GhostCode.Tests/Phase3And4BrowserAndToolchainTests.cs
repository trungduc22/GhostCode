using System.Net;
using System.Text;
using System.Text.Json;
using GhostCode.Core.Browser;
using GhostCode.Core.Models;
using GhostCode.Core.Toolchains;

namespace GhostCode.Tests;

public sealed class Phase3And4BrowserAndToolchainTests : IDisposable
{
    private readonly string _root;

    public Phase3And4BrowserAndToolchainTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GhostCode_P3P4_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class MockBrowserProbe : IBrowserEnvironmentProbe
    {
        public HashSet<string> ExistingPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RunningProcesses { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> RegAppPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool FileExists(string path) => ExistingPaths.Contains(path);
        public IReadOnlyCollection<string> GetRunningProcessNames() => RunningProcesses;
        public string? GetFileVersion(string path) => Versions.GetValueOrDefault(path);
        public string? ReadRegistryString(string fullKeyPath, string valueName) => RegAppPaths.GetValueOrDefault(fullKeyPath);
    }

    private sealed class MockToolchainProbe : IToolchainEnvironmentProbe
    {
        public HashSet<string> ExistingPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RunningProcesses { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> Dirs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> UninstallEntries { get; } = new();
        public Func<string, IReadOnlyList<string>, (int, string, string)>? ProcessRunner { get; set; }

        public bool FileExists(string path) => ExistingPaths.Contains(path);
        public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, SearchOption searchOption) => Array.Empty<string>();
        public IEnumerable<string> EnumerateDirectories(string directory) => Dirs.GetValueOrDefault(directory) ?? new List<string>();
        public IReadOnlyCollection<string> GetRunningProcessNames() => RunningProcesses;
        public string? GetFileVersion(string path) => Versions.GetValueOrDefault(path);
        public string? ReadRegistryString(string fullKeyPath, string valueName) => null;
        public IEnumerable<string> ReadRegistryUninstallDisplayNames() => UninstallEntries;

        public (int ExitCode, string Stdout, string Stderr) RunProcess(string executable, IReadOnlyList<string> args, string? stdinText = null, int timeoutMs = 4000)
            => ProcessRunner?.Invoke(executable, args) ?? (0, string.Empty, string.Empty);
    }

    [Fact]
    public void BrowserDiscovery_DetectsHostAndMockedChromeEdgeFirefoxThorium_AndStagesExtensions()
    {
        // Real host probe (checks actual Edge/Thorium presence cleanly without throwing)
        var realService = new BrowserIntegrationService();
        var realBrowsers = realService.DiscoverInstalledBrowsers(new GhostCodeConfig());
        Assert.Equal(4, realBrowsers.Count);
        Assert.Contains(realBrowsers, b => b.Kind == BrowserKind.Edge && b.IsInstalled);

        // Mocked full 4-browser matrix including Chrome & Firefox
        var probe = new MockBrowserProbe();
        const string chromeExe = @"C:\Mock\Chrome\chrome.exe";
        const string edgeExe = @"C:\Mock\Edge\msedge.exe";
        const string ffExe = @"C:\Mock\Firefox\firefox.exe";
        const string thExe = @"C:\Mock\Thorium\thorium.exe";
        probe.ExistingPaths.Add(chromeExe);
        probe.ExistingPaths.Add(edgeExe);
        probe.ExistingPaths.Add(ffExe);
        probe.ExistingPaths.Add(thExe);
        probe.Versions[chromeExe] = "131.0.0.1";
        probe.Versions[ffExe] = "133.0";
        probe.RunningProcesses.Add("firefox");

        var cfg = new GhostCodeConfig
        {
            BridgeAuthToken = "pair-secret-abc",
            CustomBrowserPaths =
            {
                ["Chrome"] = chromeExe,
                ["Edge"] = edgeExe,
                ["Firefox"] = ffExe,
                ["Thorium"] = thExe
            }
        };

        var svc = new BrowserIntegrationService(probe);
        var found = svc.DiscoverInstalledBrowsers(cfg);
        Assert.All(found, b => Assert.True(b.IsInstalled));
        Assert.True(found.Single(b => b.Kind == BrowserKind.Firefox).IsRunning);
        Assert.False(found.Single(b => b.Kind == BrowserKind.Chrome).IsRunning);

        // Stage Chromium and Firefox extension assets & native messaging manifests
        if (!OperatingSystem.IsWindows())
            return;

        var appDataRoot = Path.Combine(_root, "AppData");
        var crStage = svc.PrepareIntegration(BrowserKind.Chrome, cfg, 48123, @"C:\App\GhostCode.App.exe", appDataRoot);
        var ffStage = svc.PrepareIntegration(BrowserKind.Firefox, cfg, 48123, @"C:\App\GhostCode.App.exe", appDataRoot);

        Assert.True(crStage.Success);
        Assert.True(File.Exists(Path.Combine(crStage.ExtensionDirectory, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(crStage.ExtensionDirectory, "pairing.generated.json")));
        Assert.True(File.Exists(crStage.NativeManifestPath));
        Assert.Contains("chrome-extension://", File.ReadAllText(crStage.NativeManifestPath));

        Assert.True(ffStage.Success);
        Assert.True(File.Exists(Path.Combine(ffStage.ExtensionDirectory, "manifest.json")));
        Assert.True(File.Exists(ffStage.NativeManifestPath));
        Assert.Contains(BrowserIntegrationService.FirefoxExtensionId, File.ReadAllText(ffStage.NativeManifestPath));
    }

    [Fact]
    public async Task AuthenticatedBrowserBridge_PerBrowserHeartbeat_ConnectedOnlyOnFreshHeartbeat_SupportsFirefoxAndFilenamePreview()
    {
        var probe = new MockBrowserProbe();
        probe.ExistingPaths.Add(@"C:\Mock\chrome.exe");
        probe.ExistingPaths.Add(@"C:\Mock\firefox.exe");
        probe.RunningProcesses.Add("firefox"); // Running, NOT yet connected by extension heartbeat

        var cfg = new GhostCodeConfig
        {
            HttpPort = 0,
            BridgeAuthToken = "heartbeat-secret",
            ContextExpirationSeconds = 30,
            CustomBrowserPaths =
            {
                ["Chrome"] = @"C:\Mock\chrome.exe",
                ["Firefox"] = @"C:\Mock\firefox.exe"
            }
        };

        await using var bridge = new AuthenticatedBrowserBridge(cfg, new BrowserIntegrationService(probe));
        bridge.HeartbeatTimeoutMs = 900;
        bridge.Start();

        // Running Firefox without extension heartbeat MUST NOT report Connected
        var initial = bridge.GetBrowserConnectionStates();
        var ffInit = initial.Single(b => b.Kind == BrowserKind.Firefox);
        Assert.Equal(BrowserConnectionState.Running, ffInit.ConnectionState);

        // Authenticate Firefox WebExtension (moz-extension origin) via HMAC handshake + heartbeat + context
        using var http = new HttpClient();
        var baseUrl = $"http://127.0.0.1:{bridge.ListeningPort}";
        var nonce = Guid.NewGuid().ToString("N");
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var hsReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/handshake");
        hsReq.Headers.Add("Origin", "moz-extension://ghostcode-test-uuid");
        hsReq.Headers.Add("X-GhostCode-Browser", "Firefox");
        hsReq.Headers.Add("X-GhostCode-Ext-Version", "1.4.0");
        hsReq.Headers.Add("X-GhostCode-Pairing-Id", AuthenticatedBrowserBridge.DefaultPairingId);
        hsReq.Headers.Add("X-GhostCode-Nonce", nonce);
        hsReq.Headers.Add("X-GhostCode-Timestamp", ts.ToString());
        hsReq.Headers.Add("X-GhostCode-Proof", AuthenticatedBrowserBridge.ComputeHandshakeProof(cfg.BridgeAuthToken, nonce, ts));

        var hsResp = await http.SendAsync(hsReq);
        Assert.Equal(HttpStatusCode.OK, hsResp.StatusCode);
        Assert.True(hsResp.Headers.TryGetValues("Access-Control-Allow-Origin", out var cors));
        Assert.Equal("moz-extension://ghostcode-test-uuid", cors.Single());

        using var doc = JsonDocument.Parse(await hsResp.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("session_token").GetString()!;

        // Verify Firefox is now ExtensionConnected while Chrome stays Installed
        var states1 = bridge.GetBrowserConnectionStates();
        Assert.Equal(BrowserConnectionState.ExtensionConnected, states1.Single(b => b.Kind == BrowserKind.Firefox).ConnectionState);
        Assert.Equal(BrowserConnectionState.Installed, states1.Single(b => b.Kind == BrowserKind.Chrome).ConnectionState);

        // Unauthenticated heartbeat rejected
        var unauthHb = await http.PostAsync($"{baseUrl}/heartbeat", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthHb.StatusCode);

        // Post active PTIT problem from Firefox and verify FilenamePreview + BrowserName
        var ctxReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/context");
        ctxReq.Headers.Add("X-GhostCode-Token", token);
        ctxReq.Headers.Add("X-GhostCode-Browser", "Firefox");
        ctxReq.Content = new StringContent(
            """{"problem_code":"CPP0101","problem_title":"Tính Tổng Hai Số","platform":"PTIT","url":"https://code.ptit.edu.vn/student/question/CPP0101","tab_active":true,"browser_name":"Firefox"}""",
            Encoding.UTF8,
            "application/json");
        var ctxResp = await http.SendAsync(ctxReq);
        Assert.Equal(HttpStatusCode.OK, ctxResp.StatusCode);

        var ctx = bridge.GetActiveContext();
        Assert.NotNull(ctx);
        Assert.Equal("Firefox", ctx!.BrowserName);
        Assert.Equal("CPP0101_Tinh_Tong_Hai_So.cpp", ctx.FilenamePreview);

        // After heartbeat timeout expires without new heartbeat, Firefox drops from ExtensionConnected -> ExtensionDisconnected
        await Task.Delay(1100);
        var states2 = bridge.GetBrowserConnectionStates();
        Assert.Equal(BrowserConnectionState.ExtensionDisconnected, states2.Single(b => b.Kind == BrowserKind.Firefox).ConnectionState);
    }

    [Fact]
    public void ToolchainDiscovery_FindsRealGccGppPythonDevCppVsCode_AndNegotiatesCppStandard()
    {
        // Real host probe on this Windows machine
        var service = new ToolchainDiscoveryService();
        var (ides, toolchains) = service.DiscoverAll(new GhostCodeConfig());

        var gcc = toolchains.Single(t => t.Kind == "C");
        var gpp = toolchains.Single(t => t.Kind == "C++");
        var py = toolchains.Single(t => t.Kind == "Python");

        Assert.True(gcc.IsAvailable, gcc.StatusDetail);
        Assert.True(gpp.IsAvailable, gpp.StatusDetail);
        Assert.True(py.IsAvailable, py.StatusDetail);
        Assert.Contains(new[] { "-std=c++17", "-std=c++14", "-std=c++11" }, flag => gpp.DefaultFlags.Contains(flag, StringComparison.Ordinal));

        // Mocked Dev-C++, VS Code, PyCharm & Visual Studio discovery (deterministic on clean CI runners without GUI IDEs)
        var mock = new MockToolchainProbe();
        const string devCppExe = @"C:\Program Files (x86)\Embarcadero\Dev-Cpp\devcpp.exe";
        const string vscodeExe = @"C:\Users\Test\AppData\Local\Programs\Microsoft VS Code\Code.exe";
        const string pyCharmExe = @"C:\JetBrains\PyCharm 2024.3\bin\pycharm64.exe";
        const string devenvExe = @"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe";
        const string mockPy = @"C:\Python312\python.exe";
        mock.ExistingPaths.Add(devCppExe);
        mock.ExistingPaths.Add(vscodeExe);
        mock.ExistingPaths.Add(pyCharmExe);
        mock.ExistingPaths.Add(devenvExe);
        mock.ExistingPaths.Add(mockPy);
        mock.UninstallEntries.Add(@"PyCharm 2024.3|C:\JetBrains\PyCharm 2024.3|" + pyCharmExe);
        mock.Dirs[@"C:\Program Files\Microsoft Visual Studio"] = new() { @"C:\Program Files\Microsoft Visual Studio\2022" };
        mock.Dirs[@"C:\Program Files\Microsoft Visual Studio\2022"] = new() { @"C:\Program Files\Microsoft Visual Studio\2022\Community" };
        mock.ProcessRunner = (exe, _) => exe.Contains("python", StringComparison.OrdinalIgnoreCase)
            ? (0, mockPy + "\n3.12.4\n", string.Empty)
            : (0, "g++ 13.2.0", string.Empty);

        var mockSvc = new ToolchainDiscoveryService(mock);
        var cfg = new GhostCodeConfig
        {
            PythonInterpreterPath = mockPy,
            CustomIdePaths =
            {
                ["Dev-C++"] = devCppExe,
                ["VS Code"] = vscodeExe,
                ["PyCharm"] = pyCharmExe,
                ["Visual Studio"] = devenvExe
            }
        };
        var (mockIdes, _) = mockSvc.DiscoverAll(cfg);
        Assert.True(mockIdes.Single(i => i.Name == "Dev-C++").IsInstalled);
        Assert.True(mockIdes.Single(i => i.Name == "Visual Studio Code").IsInstalled);
        Assert.True(mockIdes.Single(i => i.Name == "PyCharm").IsInstalled);
        Assert.True(mockIdes.Single(i => i.Name == "Visual Studio").IsInstalled);
    }
}
