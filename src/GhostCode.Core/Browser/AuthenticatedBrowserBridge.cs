using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GhostCode.Core.Analysis;
using GhostCode.Core.Models;
using GhostCode.Core.Notifications;
using GhostCode.Core.Storage;

namespace GhostCode.Core.Browser;

public sealed record BrowserContextPayload(
    string? problem_code,
    string? problem_title,
    string? platform,
    string? url,
    bool tab_active = true,
    long timestamp = 0,
    string? browser_name = null);

public sealed record BrowserSamplePairPayload(
    string? input,
    string? output);

public sealed record BrowserProblemPayload(
    string? problem_code,
    string? problem_title,
    string? platform,
    string? url,
    string? description = null,
    string? input_spec = null,
    string? output_spec = null,
    string? constraints = null,
    string? time_limit = null,
    string? memory_limit = null,
    bool interactive_or_custom = false,
    List<BrowserSamplePairPayload>? samples = null,
    bool tab_active = true,
    long timestamp = 0,
    string? browser_name = null);

public sealed record BrowserSubmissionPayload(
    string? platform,
    string? problem_code,
    string? url,
    string? submission_id = null,
    long submitted_at = 0,
    string? language = null,
    string? verdict = null,
    int? runtime_ms = null,
    int? memory_kb = null,
    bool is_version_certain = false,
    bool tab_active = true,
    string? browser_name = null);

internal sealed record ActiveBrowserSession(
    BrowserKind Kind,
    string ExtensionVersion,
    long LastHeartbeatUnixMs);

/// <summary>
/// Authenticated loopback HTTP bridge + Win32 browser title scanner.
/// - Requires HMAC-SHA256 pairing proof via POST /handshake (rejects unauthenticated GET or spoofed Origin).
/// - Issues short-lived rotatable session tokens via X-GhostCode-Token.
/// - Tracks per-browser extension heartbeats accurately (Connected ONLY when heartbeat is fresh).
/// - Strictly validates supported problem URLs and active-tab status.
/// - Never emits Access-Control-Allow-Origin: * and never logs auth secrets.
/// </summary>
public sealed partial class AuthenticatedBrowserBridge : IAsyncDisposable
{
    public const string DefaultPairingId = "ghostcode-local-ext";

    private readonly GhostCodeConfig _config;
    private readonly BrowserIntegrationService _browserIntegration;
    private readonly SqliteArchiveStore? _store;
    private readonly NotificationCoordinator? _notifications;
    private readonly object _ctxLock = new();
    private readonly object _authLock = new();
    private readonly Dictionary<string, long> _seenNonces = new(StringComparer.Ordinal);
    private readonly Dictionary<BrowserKind, ActiveBrowserSession> _browserSessions = new();

    private string _sessionToken = string.Empty;
    private long _sessionExpiresAtMs;
    private ProblemContext? _activeContext;
    private List<string> _browserTitles = new();
    private string _lastDedupKey = string.Empty;
    private long _lastDedupAtMs;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private Task? _win32Task;

    public AuthenticatedBrowserBridge(
        GhostCodeConfig config,
        BrowserIntegrationService? browserIntegration = null,
        SqliteArchiveStore? store = null,
        NotificationCoordinator? notifications = null)
    {
        _config = config;
        _browserIntegration = browserIntegration ?? new BrowserIntegrationService();
        _store = store;
        _notifications = notifications;
        if (string.IsNullOrWhiteSpace(_config.BridgeAuthToken))
            _config.BridgeAuthToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        RotateSessionToken();
    }

    public int HeartbeatTimeoutMs { get; set; } = 15_000;
    public int ListeningPort { get; private set; }
    public string? ListenerStartError { get; private set; }
    public string PairingSecret => _config.BridgeAuthToken;
    public string BridgeToken => _config.BridgeAuthToken;

    public string CurrentSessionToken
    {
        get
        {
            lock (_authLock)
                return _sessionToken;
        }
    }

    public event Action<ProblemContext?>? ContextChanged;
    public event Action? BrowserConnectionsChanged;
    public event Action<ProblemStatementRecord, IReadOnlyList<SampleTestCaseRecord>>? ProblemStatementReceived;
    public event Action<SubmissionRecord>? SubmissionVerdictReceived;

    public void ClearActiveContext()
    {
        bool hadContext;
        lock (_ctxLock)
        {
            hadContext = _activeContext is not null;
            _activeContext = null;
            _lastDedupKey = string.Empty;
        }
        if (hadContext)
            ContextChanged?.Invoke(null);
    }

    public ProblemContext? GetActiveContext()
    {
        lock (_ctxLock)
        {
            if (_activeContext is null)
                return null;
            var ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _activeContext.SeenAtUnixMs;
            if (ageMs > _config.ContextExpirationSeconds * 1000L)
            {
                _activeContext = null;
                return null;
            }
            return _activeContext;
        }
    }

    public IReadOnlyList<string> GetDetectedBrowserTitles()
    {
        lock (_ctxLock)
        {
            return _browserTitles.ToList();
        }
    }

    public void Start()
    {
        if (_cts is not null)
            return;
        _cts = new CancellationTokenSource();
        ListenerStartError = null;

        try
        {
            _listener = new TcpListener(IPAddress.Loopback, _config.HttpPort);
            _listener.Start();
        }
        catch (SocketException ex) when (_config.HttpPort != 0)
        {
            ListenerStartError = $"Port {_config.HttpPort} unavailable ({ex.Message}); using fallback port.";
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
        }

        ListeningPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        RotateSessionToken();
        WritePairingFileIfConfigured();

        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _win32Task = Task.Run(() => Win32ScannerLoopAsync(_cts.Token));
    }

    public void ForceWin32ScanNow()
    {
        var (winCtx, titles) = ScanVisibleBrowserWindows();
        bool notifyExpired = false;
        ProblemContext? notifyUpdated = null;
        lock (_ctxLock)
        {
            _browserTitles = titles;
            var prev = _activeContext;
            var current = GetActiveContextLocked();
            if (prev is not null && current is null)
                notifyExpired = true;

            if (winCtx is not null)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (current is null || current.Source != "Extension" || now - current.SeenAtUnixMs > 30_000)
                {
                    _activeContext = winCtx;
                    notifyUpdated = winCtx;
                    notifyExpired = false;
                }
            }
        }
        if (notifyUpdated is not null)
            ContextChanged?.Invoke(notifyUpdated);
        else if (notifyExpired)
            ContextChanged?.Invoke(null);
    }

    private ProblemContext? GetActiveContextLocked()
    {
        if (_activeContext is null)
            return null;
        var ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _activeContext.SeenAtUnixMs;
        if (ageMs > _config.ContextExpirationSeconds * 1000L)
        {
            _activeContext = null;
            return null;
        }
        return _activeContext;
    }

    /// <summary>
    /// Validates and applies a browser context payload shared by HTTP /context and Chromium Native Messaging.
    /// </summary>
    public (int StatusCode, string JsonBody) ApplyContextPayload(BrowserContextPayload? payload)
    {
        if (payload is null)
            return (400, """{"error":"empty payload"}""");

        // Never allow background tabs to overwrite the currently active problem
        if (!payload.tab_active)
            return (202, """{"ok":false,"ignored":"background_tab"}""");

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (payload.timestamp > 0 && (nowMs - payload.timestamp > _config.ContextExpirationSeconds * 1000L || payload.timestamp - nowMs > 60_000))
            return (202, """{"ok":false,"ignored":"stale_timestamp"}""");

        if (string.IsNullOrWhiteSpace(payload.url)
            || payload.url.Length > 2048
            || ContainsControlChars(payload.url)
            || ContainsControlChars(payload.problem_title)
            || ContainsControlChars(payload.problem_code)
            || !Uri.TryCreate(payload.url, UriKind.Absolute, out var parsedUri)
            || (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
        {
            return (400, """{"error":"invalid or unsupported problem url"}""");
        }

        var titleHint = payload.problem_title is { Length: > 300 } ? payload.problem_title[..300] : payload.problem_title;
        var parsed = SourceAnalyzer.ParseContextFromUrl(payload.url, titleHint, nowMs);
        if (parsed is null)
            return (400, """{"error":"unrecognized or unsupported problem url"}""");

        if (!string.IsNullOrWhiteSpace(payload.browser_name))
            parsed = parsed with { BrowserName = payload.browser_name.Trim() };

        bool updated;
        lock (_ctxLock)
        {
            var key = $"{parsed.Platform}|{parsed.Code}|{parsed.Title}|{parsed.Url}|{parsed.BrowserName}";
            if (key == _lastDedupKey && nowMs - _lastDedupAtMs < 5_000)
            {
                _activeContext = parsed with { SeenAtUnixMs = nowMs };
                updated = false;
            }
            else
            {
                _lastDedupKey = key;
                _lastDedupAtMs = nowMs;
                _activeContext = parsed;
                updated = true;
            }
        }

        if (updated)
            ContextChanged?.Invoke(parsed);

        return (200, """{"ok":true}""");
    }

    private void RecordHeartbeatFromPayloadOrHeaders(string? payloadBrowser, string? browserHdr, string? extVerHdr)
    {
        var effectiveBrowser = !string.IsNullOrWhiteSpace(payloadBrowser) ? payloadBrowser : browserHdr;
        if (TryParseBrowserKind(effectiveBrowser, out var kind))
            RecordBrowserHeartbeat(kind, extVerHdr);
    }

    private bool IsWebsiteCaptureAllowed(string host, string platformName, string? rawPlatform)
    {
        if (_config.WebsiteStatementCaptureEnabled is null || _config.WebsiteStatementCaptureEnabled.Count == 0)
            return true;

        string?[] keys = { host, platformName, rawPlatform };
        foreach (var k in keys)
        {
            if (string.IsNullOrWhiteSpace(k))
                continue;
            if (_config.WebsiteStatementCaptureEnabled.TryGetValue(k.Trim(), out var allowed) && !allowed)
                return false;
        }
        return true;
    }

    private static bool IsSupportedHost(string host)
    {
        var h = host.ToLowerInvariant();
        if (h.StartsWith("www.", StringComparison.Ordinal))
            h = h[4..];
        return h is "code.ptit.edu.vn" or "codeforces.com" or "oj.vnoi.info" or "leetcode.com" or "leetcode.cn" or "hackerrank.com"
            || h.EndsWith(".code.ptit.edu.vn", StringComparison.Ordinal)
            || h.EndsWith(".codeforces.com", StringComparison.Ordinal)
            || h.EndsWith(".oj.vnoi.info", StringComparison.Ordinal)
            || h.EndsWith(".leetcode.com", StringComparison.Ordinal)
            || h.EndsWith(".hackerrank.com", StringComparison.Ordinal);
    }

    public async Task<(int StatusCode, string JsonBody)> ApplyProblemPayloadAsync(
        BrowserProblemPayload? payload,
        CancellationToken ct = default)
    {
        if (payload is null)
            return (400, """{"error":"empty payload"}""");
        if (!payload.tab_active)
            return (202, """{"ok":false,"ignored":"background_tab"}""");
        if (!_config.CaptureProblemStatements)
            return (202, """{"ok":false,"ignored":"capture_disabled"}""");

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (string.IsNullOrWhiteSpace(payload.url)
            || payload.url.Length > 2048
            || ContainsControlChars(payload.url)
            || !Uri.TryCreate(payload.url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return (400, """{"error":"invalid or unsupported problem url"}""");
        }

        var parsed = SourceAnalyzer.ParseContextFromUrl(payload.url, payload.problem_title, nowMs);
        if (parsed is null)
            return (400, """{"error":"unrecognized or unsupported problem url"}""");

        if (!IsWebsiteCaptureAllowed(uri.Host, parsed.Platform.ToString(), payload.platform))
            return (202, """{"ok":false,"ignored":"site_capture_disabled"}""");

        var problemKey = ProblemStatementRecord.BuildKey(parsed.Platform.ToString(), parsed.Code);
        var stmt = new ProblemStatementRecord(
            ProblemKey: problemKey,
            Platform: parsed.Platform.ToString(),
            ProblemCode: parsed.Code,
            Title: string.IsNullOrWhiteSpace(payload.problem_title) ? parsed.Title : payload.problem_title.Trim(),
            Url: payload.url.Trim(),
            Description: (payload.description ?? string.Empty).Trim(),
            InputSpec: (payload.input_spec ?? string.Empty).Trim(),
            OutputSpec: (payload.output_spec ?? string.Empty).Trim(),
            Constraints: (payload.constraints ?? string.Empty).Trim(),
            TimeLimit: (payload.time_limit ?? string.Empty).Trim(),
            MemoryLimit: (payload.memory_limit ?? string.Empty).Trim(),
            InteractiveOrCustomJudge: payload.interactive_or_custom,
            CapturedAtUnixMs: payload.timestamp > 0 ? payload.timestamp : nowMs);

        var samples = new List<SampleTestCaseRecord>();
        if (payload.samples is not null)
        {
            for (int i = 0; i < payload.samples.Count && i < 20; i++)
            {
                var s = payload.samples[i];
                if (s is null) continue;
                var tc = new SampleTestCaseRecord(
                    Id: $"{problemKey}_sample_{i + 1}",
                    ProblemKey: problemKey,
                    CaseIndex: i + 1,
                    InputText: s.input ?? string.Empty,
                    ExpectedOutput: s.output ?? string.Empty,
                    SourceUrl: payload.url.Trim(),
                    IsCustom: false,
                    CapturedAtUnixMs: stmt.CapturedAtUnixMs);
                samples.Add(tc);
            }
        }

        if (_store is not null)
        {
            await _store.UpsertProblemStatementAsync(stmt, ct);
            foreach (var tc in samples)
                await _store.UpsertSampleTestAsync(tc, ct);
        }

        ProblemStatementReceived?.Invoke(stmt, samples);
        return (200, JsonSerializer.Serialize(new { ok = true, problem_key = problemKey, samples_count = samples.Count }));
    }

    public async Task<(int StatusCode, string JsonBody)> ApplySubmissionPayloadAsync(
        BrowserSubmissionPayload? payload,
        CancellationToken ct = default)
    {
        if (payload is null)
            return (400, """{"error":"empty payload"}""");
        if (!payload.tab_active)
            return (202, """{"ok":false,"ignored":"background_tab"}""");
        if (!_config.TrackSubmissionVerdicts)
            return (202, """{"ok":false,"ignored":"verdict_tracking_disabled"}""");

        if (string.IsNullOrWhiteSpace(payload.url)
            || !Uri.TryCreate(payload.url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !IsSupportedHost(uri.Host))
        {
            return (400, """{"error":"invalid or unsupported submission url"}""");
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var parsedCtx = SourceAnalyzer.ParseContextFromUrl(payload.url, null, nowMs);
        var platformName = parsedCtx?.Platform.ToString() ?? (payload.platform ?? "Unknown").Trim();
        var problemCode = !string.IsNullOrWhiteSpace(parsedCtx?.Code) ? parsedCtx!.Code : (payload.problem_code ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(problemCode))
            return (400, """{"error":"missing problem_code"}""");

        if (!IsWebsiteCaptureAllowed(uri.Host, platformName, payload.platform))
            return (202, """{"ok":false,"ignored":"site_capture_disabled"}""");

        var verdict = ParseSubmissionVerdict(payload.verdict);
        string? matchedVersionId = null;
        if (_store is not null)
        {
            var versions = await _store.QueryVersionsAsync(search: problemCode, limit: 50, ct: ct);
            var latest = versions
                .Where(v => string.Equals(v.ProblemCode, problemCode, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(v => v.CreatedAtUnixMs)
                .ThenByDescending(v => v.VersionNum)
                .FirstOrDefault();
            matchedVersionId = latest?.Id;
        }

        // Honest attribution: only claim certain version match when the caller provably matches exact bytes/commit.
        bool versionCertain = payload.is_version_certain && !string.IsNullOrEmpty(matchedVersionId);
        var stableId = !string.IsNullOrWhiteSpace(payload.submission_id)
            ? $"sub_{platformName}_{problemCode}_{payload.submission_id.Trim()}"
            : $"sub_{platformName}_{problemCode}_{Guid.NewGuid():N}";

        var record = new SubmissionRecord(
            Id: stableId,
            Platform: platformName,
            ProblemCode: problemCode,
            SubmissionId: payload.submission_id?.Trim(),
            SubmittedAtUnixMs: payload.submitted_at > 0 ? payload.submitted_at : nowMs,
            Language: (payload.language ?? string.Empty).Trim(),
            Verdict: verdict,
            RuntimeMs: payload.runtime_ms,
            MemoryKb: payload.memory_kb,
            VersionId: matchedVersionId,
            IsVersionCertain: versionCertain);

        if (_store is not null)
            await _store.UpsertSubmissionRecordAsync(record, ct);

        if (_notifications is not null && _config.AlertSubmissionVerdicts && verdict is not SubmissionVerdict.Pending and not SubmissionVerdict.Judging)
        {
            var severity = verdict == SubmissionVerdict.Accepted ? "Info" : "Warning";
            var uncertaintyNote = versionCertain ? string.Empty : " (version link unverified)";
            await _notifications.NotifyAsync(
                "verdict",
                severity,
                $"GhostCode — {problemCode}: {verdict}",
                $"[{platformName}] {problemCode} verdict: {verdict}{uncertaintyNote}",
                $"verdict_{record.Id}_{verdict}",
                "open",
                ct);
        }

        SubmissionVerdictReceived?.Invoke(record);
        return (200, JsonSerializer.Serialize(new { ok = true, id = record.Id, verdict = verdict.ToString(), is_version_certain = versionCertain }));
    }

    public static SubmissionVerdict ParseSubmissionVerdict(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return SubmissionVerdict.Unknown;
        var norm = raw.Trim().ToLowerInvariant();
        if (norm is "ac" || norm.Contains("accepted")) return SubmissionVerdict.Accepted;
        if (norm is "wa" || norm.Contains("wrong answer")) return SubmissionVerdict.WrongAnswer;
        if (norm is "tle" || norm.Contains("time limit")) return SubmissionVerdict.TimeLimitExceeded;
        if (norm is "mle" || norm.Contains("memory limit")) return SubmissionVerdict.MemoryLimitExceeded;
        if (norm is "re" or "rte" || norm.Contains("runtime error")) return SubmissionVerdict.RuntimeError;
        if (norm is "ce" || norm.Contains("compil")) return SubmissionVerdict.CompilationError;
        if (norm.Contains("judg") || norm.Contains("running")) return SubmissionVerdict.Judging;
        if (norm.Contains("pending") || norm.Contains("queue") || norm.Contains("waiting")) return SubmissionVerdict.Pending;
        return SubmissionVerdict.Other;
    }

    public static bool TryParseBrowserKind(string? raw, out BrowserKind kind)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            kind = default;
            return false;
        }
        var norm = raw.Trim().ToLowerInvariant();
        if (norm.Contains("thorium")) { kind = BrowserKind.Thorium; return true; }
        if (norm.Contains("edge") || norm.Contains("msedge")) { kind = BrowserKind.Edge; return true; }
        if (norm.Contains("firefox")) { kind = BrowserKind.Firefox; return true; }
        if (norm.Contains("chrome")) { kind = BrowserKind.Chrome; return true; }
        kind = default;
        return false;
    }

    public void RecordBrowserHeartbeat(BrowserKind kind, string? extensionVersion)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool changed;
        lock (_authLock)
        {
            changed = !_browserSessions.TryGetValue(kind, out var existing)
                || nowMs - existing.LastHeartbeatUnixMs > HeartbeatTimeoutMs;
            _browserSessions[kind] = new ActiveBrowserSession(kind, extensionVersion ?? existing?.ExtensionVersion ?? "1.4.0", nowMs);
            if (!_config.KnownConnectedBrowsers.Contains(kind.ToString(), StringComparer.OrdinalIgnoreCase))
                _config.KnownConnectedBrowsers.Add(kind.ToString());
        }
        _browserIntegration.ClearGuidedInstalling(kind);
        if (changed)
            BrowserConnectionsChanged?.Invoke();
    }

    public List<BrowserStatusInfo> GetBrowserConnectionStates()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var discovered = _browserIntegration.DiscoverInstalledBrowsers(_config);
        var list = new List<BrowserStatusInfo>();

        foreach (var (kind, name, installed, isRunning, exe, ver) in discovered)
        {
            ActiveBrowserSession? sess;
            lock (_authLock)
            {
                _browserSessions.TryGetValue(kind, out sess);
            }

            bool heartbeatFresh = sess is not null && nowMs - sess.LastHeartbeatUnixMs <= HeartbeatTimeoutMs;
            bool isGuidedInstalling = _browserIntegration.IsGuidedInstalling(kind);
            bool inProfile = installed && _browserIntegration.IsExtensionDetectedInBrowserProfile(kind);
            bool knownConnected = _config.KnownConnectedBrowsers.Contains(kind.ToString(), StringComparer.OrdinalIgnoreCase);

            BrowserConnectionState state;
            string detail;
            if (heartbeatFresh)
            {
                state = BrowserConnectionState.ExtensionConnected;
                detail = $"Authenticated extension active (v{sess!.ExtensionVersion})";
            }
            else if (isGuidedInstalling)
            {
                state = BrowserConnectionState.GuidedInstalling;
                detail = "Guided setup open — waiting for extension handshake";
            }
            else if (sess is not null)
            {
                state = BrowserConnectionState.ExtensionDisconnected;
                detail = "Extension heartbeat expired / browser closed";
            }
            else if (installed && (inProfile || knownConnected))
            {
                bool hostHealthy = _browserIntegration.IsNativeHostHealthy(kind);
                state = hostHealthy ? BrowserConnectionState.PendingHandshake : BrowserConnectionState.RepairNeeded;
                detail = hostHealthy
                    ? "Extension installed — waiting for browser tab wakeup"
                    : "Native Messaging bridge needs repair";
            }
            else if (isRunning)
            {
                state = BrowserConnectionState.Running;
                detail = "Browser running without active GhostCode extension";
            }
            else if (installed)
            {
                state = BrowserConnectionState.Installed;
                detail = "Extension not installed yet";
            }
            else
            {
                state = BrowserConnectionState.NotInstalled;
                detail = "Not detected on this PC";
            }

            list.Add(new BrowserStatusInfo(
                kind, name, installed, isRunning, exe, ver,
                state, sess?.ExtensionVersion, sess?.LastHeartbeatUnixMs, detail));
        }

        return list;
    }

    private static bool ContainsControlChars(string? s) =>
        !string.IsNullOrEmpty(s) && s.Any(ch => char.IsControl(ch) && ch != '\t');

    public async Task StopAsync()
    {
        var cts = _cts;
        if (cts is null)
            return;
        _cts = null;
        cts.Cancel();
        try { _listener?.Stop(); } catch { }
        var tasks = new[] { _acceptTask, _win32Task }.Where(t => t is not null).Cast<Task>().ToArray();
        if (tasks.Length > 0)
        {
            try { await Task.WhenAll(tasks); } catch { }
        }
        cts.Dispose();
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
