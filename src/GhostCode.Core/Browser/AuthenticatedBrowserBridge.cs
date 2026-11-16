using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GhostCode.Core.Analysis;
using GhostCode.Core.Models;

namespace GhostCode.Core.Browser;

public sealed record BrowserContextPayload(
    string? problem_code,
    string? problem_title,
    string? platform,
    string? url,
    bool tab_active = true,
    long timestamp = 0);

/// <summary>
/// Authenticated loopback HTTP bridge + Win32 browser title scanner.
/// - Requires HMAC-SHA256 pairing proof via POST /handshake (rejects unauthenticated GET or spoofed Origin).
/// - Issues short-lived rotatable session tokens via X-GhostCode-Token.
/// - Strictly validates supported problem URLs and active-tab status.
/// - Never emits Access-Control-Allow-Origin: * and never logs auth secrets.
/// </summary>
public sealed class AuthenticatedBrowserBridge : IAsyncDisposable
{
    public const string DefaultPairingId = "ghostcode-local-ext";

    private readonly GhostCodeConfig _config;
    private readonly object _ctxLock = new();
    private readonly object _authLock = new();
    private readonly Dictionary<string, long> _seenNonces = new(StringComparer.Ordinal);

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

    public AuthenticatedBrowserBridge(GhostCodeConfig config)
    {
        _config = config;
        if (string.IsNullOrWhiteSpace(_config.BridgeAuthToken))
            _config.BridgeAuthToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        RotateSessionToken();
    }

    public int ListeningPort { get; private set; }
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

    public string RotateSessionToken()
    {
        lock (_authLock)
        {
            _sessionToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            int ttl = _config.SessionTokenTtlSeconds > 0 ? _config.SessionTokenTtlSeconds : 300;
            _sessionExpiresAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ttl * 1000L;
            return _sessionToken;
        }
    }

    public static string ComputeHandshakeProof(string pairingSecret, string nonce, long timestampMs)
    {
        var keyBytes = Encoding.UTF8.GetBytes(pairingSecret);
        var msgBytes = Encoding.UTF8.GetBytes($"{nonce}:{timestampMs}");
        var mac = HMACSHA256.HashData(keyBytes, msgBytes);
        return Convert.ToHexStringLower(mac);
    }

    public bool TryPairAndIssueSessionToken(
        string? pairingId,
        string? nonce,
        long timestampMs,
        string? suppliedProof,
        out string sessionToken,
        out int expiresInSeconds)
    {
        sessionToken = string.Empty;
        expiresInSeconds = _config.SessionTokenTtlSeconds > 0 ? _config.SessionTokenTtlSeconds : 300;

        if (!string.Equals(pairingId, DefaultPairingId, StringComparison.Ordinal))
            return false;
        if (string.IsNullOrWhiteSpace(nonce) || nonce.Length > 128 || string.IsNullOrWhiteSpace(suppliedProof))
            return false;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (Math.Abs(nowMs - timestampMs) > 60_000)
            return false;

        var expectedHex = ComputeHandshakeProof(_config.BridgeAuthToken, nonce, timestampMs);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedHex);
        var actualBytes = Encoding.UTF8.GetBytes(suppliedProof.Trim().ToLowerInvariant());
        if (expectedBytes.Length != actualBytes.Length || !CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
            return false;

        lock (_authLock)
        {
            // Prune stale nonces older than 90 seconds
            var cutoff = nowMs - 90_000;
            foreach (var oldKey in _seenNonces.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                _seenNonces.Remove(oldKey);

            if (_seenNonces.ContainsKey(nonce))
                return false;

            _seenNonces[nonce] = nowMs;

            if (string.IsNullOrEmpty(_sessionToken) || nowMs >= _sessionExpiresAtMs)
            {
                _sessionToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                _sessionExpiresAtMs = nowMs + expiresInSeconds * 1000L;
            }
            sessionToken = _sessionToken;
            return true;
        }
    }

    public bool ValidateSessionToken(string? suppliedToken)
    {
        if (string.IsNullOrWhiteSpace(suppliedToken))
            return false;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_authLock)
        {
            if (string.IsNullOrEmpty(_sessionToken) || nowMs >= _sessionExpiresAtMs)
                return false;
            var expected = Encoding.UTF8.GetBytes(_sessionToken);
            var actual = Encoding.UTF8.GetBytes(suppliedToken.Trim());
            return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
        }
    }

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

        _listener = new TcpListener(IPAddress.Loopback, _config.HttpPort);
        _listener.Start();
        ListeningPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        RotateSessionToken();
        WritePairingFileIfConfigured();

        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _win32Task = Task.Run(() => Win32ScannerLoopAsync(_cts.Token));
    }

    private void WritePairingFileIfConfigured()
    {
        if (string.IsNullOrWhiteSpace(_config.PairingFilePath))
            return;
        try
        {
            var dir = Path.GetDirectoryName(_config.PairingFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(new
            {
                pairing_id = DefaultPairingId,
                pairing_secret = _config.BridgeAuthToken,
                port = ListeningPort
            });
            var tmp = $"{_config.PairingFilePath}.tmp.{Guid.NewGuid():N}";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, _config.PairingFilePath, overwrite: true);
        }
        catch { }
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

    private async Task Win32ScannerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                ForceWin32ScanNow();
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch { }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch { }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            if (client.Client.RemoteEndPoint is not IPEndPoint ep || !IPAddress.IsLoopback(ep.Address))
                return;

            await using var stream = client.GetStream();
            var rawHeaders = new MemoryStream();
            var buf = new byte[4096];
            int headerEnd = -1;

            while (rawHeaders.Length < 16_384)
            {
                int n = await stream.ReadAsync(buf, ct);
                if (n == 0)
                    return;
                rawHeaders.Write(buf, 0, n);
                var span = rawHeaders.GetBuffer().AsSpan(0, (int)rawHeaders.Length);
                headerEnd = span.IndexOf("\r\n\r\n"u8);
                if (headerEnd >= 0)
                    break;
            }
            if (headerEnd < 0)
                return;

            var allBytes = rawHeaders.ToArray();
            var headerText = Encoding.ASCII.GetString(allBytes, 0, headerEnd);
            var lines = headerText.Split("\r\n");
            if (lines.Length == 0)
                return;

            var reqParts = lines[0].Split(' ');
            if (reqParts.Length < 2)
                return;
            var method = reqParts[0].ToUpperInvariant();
            var path = reqParts[1];

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon > 0)
                    headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
            }

            int contentLength = 0;
            if (headers.TryGetValue("Content-Length", out var clStr))
                int.TryParse(clStr, out contentLength);
            if (contentLength < 0 || contentLength > 65_536)
            {
                await WriteHttpResponseAsync(stream, 413, """{"error":"payload too large"}""", null, ct);
                return;
            }

            int bodyStart = headerEnd + 4;
            var bodyBytes = new byte[contentLength];
            int already = Math.Min(contentLength, allBytes.Length - bodyStart);
            if (already > 0)
                Buffer.BlockCopy(allBytes, bodyStart, bodyBytes, 0, already);
            int remaining = contentLength - already;
            int offset = already;
            while (remaining > 0)
            {
                int n = await stream.ReadAsync(bodyBytes.AsMemory(offset, remaining), ct);
                if (n == 0)
                    break;
                offset += n;
                remaining -= n;
            }

            var origin = headers.TryGetValue("Origin", out var org) ? org : string.Empty;
            string? allowedOrigin = TryGetAllowedExtensionOrigin(origin);
            bool isWebOrigin = origin.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                            || origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

            if (method == "OPTIONS")
            {
                if (isWebOrigin)
                {
                    await WriteHttpResponseAsync(stream, 403, """{"error":"origin forbidden"}""", null, ct);
                    return;
                }
                await WriteHttpResponseAsync(stream, 204, string.Empty, allowedOrigin, ct);
                return;
            }

            // Never expose pairing or session tokens via unauthenticated GET /handshake
            if (method == "GET" && path.StartsWith("/handshake", StringComparison.Ordinal))
            {
                await WriteHttpResponseAsync(stream, 403, """{"error":"authenticated POST /handshake required"}""", allowedOrigin, ct);
                return;
            }

            if (method == "POST" && path.StartsWith("/handshake", StringComparison.Ordinal))
            {
                if (isWebOrigin)
                {
                    await WriteHttpResponseAsync(stream, 403, """{"error":"web origin forbidden"}""", null, ct);
                    return;
                }

                headers.TryGetValue("X-GhostCode-Pairing-Id", out var pairingId);
                headers.TryGetValue("X-GhostCode-Nonce", out var nonce);
                headers.TryGetValue("X-GhostCode-Timestamp", out var tsStr);
                headers.TryGetValue("X-GhostCode-Proof", out var proof);

                if (!long.TryParse(tsStr, out var tsMs)
                    || !TryPairAndIssueSessionToken(pairingId, nonce, tsMs, proof, out var issuedToken, out var expiresIn))
                {
                    await WriteHttpResponseAsync(stream, 403, """{"error":"invalid pairing proof"}""", allowedOrigin, ct);
                    return;
                }

                var tokJson = JsonSerializer.Serialize(new { session_token = issuedToken, expires_in = expiresIn });
                await WriteHttpResponseAsync(stream, 200, tokJson, allowedOrigin, ct);
                return;
            }

            if (method == "GET" && path.StartsWith("/status", StringComparison.Ordinal))
            {
                var ctx = GetActiveContext();
                var json = JsonSerializer.Serialize(new
                {
                    ok = true,
                    context = ctx,
                    browser_titles = GetDetectedBrowserTitles()
                });
                await WriteHttpResponseAsync(stream, 200, json, allowedOrigin, ct);
                return;
            }

            if (method == "POST" && path.StartsWith("/context/clear", StringComparison.Ordinal))
            {
                headers.TryGetValue("X-GhostCode-Token", out var suppliedToken);
                if (!ValidateSessionToken(suppliedToken))
                {
                    await WriteHttpResponseAsync(stream, 401, """{"error":"unauthorized"}""", allowedOrigin, ct);
                    return;
                }

                ClearActiveContext();
                await WriteHttpResponseAsync(stream, 200, """{"ok":true,"cleared":true}""", allowedOrigin, ct);
                return;
            }

            if (method == "POST" && path.StartsWith("/context", StringComparison.Ordinal))
            {
                if (isWebOrigin)
                {
                    await WriteHttpResponseAsync(stream, 403, """{"error":"web origin forbidden"}""", null, ct);
                    return;
                }

                headers.TryGetValue("X-GhostCode-Token", out var suppliedToken);
                if (!ValidateSessionToken(suppliedToken))
                {
                    await WriteHttpResponseAsync(stream, 401, """{"error":"unauthorized"}""", allowedOrigin, ct);
                    return;
                }

                BrowserContextPayload? payload;
                try
                {
                    payload = JsonSerializer.Deserialize<BrowserContextPayload>(bodyBytes);
                }
                catch
                {
                    await WriteHttpResponseAsync(stream, 400, """{"error":"invalid json"}""", allowedOrigin, ct);
                    return;
                }

                var (status, responseBody) = ApplyContextPayload(payload);
                await WriteHttpResponseAsync(stream, status, responseBody, allowedOrigin, ct);
                return;
            }

            await WriteHttpResponseAsync(stream, 404, """{"error":"not found"}""", allowedOrigin, ct);
        }
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

        bool updated;
        lock (_ctxLock)
        {
            var key = $"{parsed.Platform}|{parsed.Code}|{parsed.Title}";
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

    private static bool ContainsControlChars(string? s) =>
        !string.IsNullOrEmpty(s) && s.Any(ch => char.IsControl(ch) && ch != '\t');

    private static string? TryGetAllowedExtensionOrigin(string origin)
    {
        if (string.IsNullOrWhiteSpace(origin) || ContainsControlChars(origin))
            return null;
        if (origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
            || origin.StartsWith("extension://", StringComparison.OrdinalIgnoreCase))
        {
            return origin;
        }
        return null;
    }

    private static async Task WriteHttpResponseAsync(NetworkStream stream, int statusCode, string body, string? allowedOrigin, CancellationToken ct)
    {
        var statusText = statusCode switch
        {
            200 => "OK",
            202 => "Accepted",
            204 => "No Content",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            413 => "Payload Too Large",
            _ => "OK"
        };
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {statusCode} {statusText}\r\n");
        sb.Append("Content-Type: application/json; charset=utf-8\r\n");
        sb.Append($"Content-Length: {bodyBytes.Length}\r\n");
        if (!string.IsNullOrEmpty(allowedOrigin))
        {
            sb.Append($"Access-Control-Allow-Origin: {allowedOrigin}\r\n");
            sb.Append("Vary: Origin\r\n");
        }
        sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
        sb.Append("Access-Control-Allow-Headers: Content-Type, X-GhostCode-Token, X-GhostCode-Pairing-Id, X-GhostCode-Nonce, X-GhostCode-Timestamp, X-GhostCode-Proof\r\n");
        sb.Append("Connection: close\r\n\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
        if (bodyBytes.Length > 0)
            await stream.WriteAsync(bodyBytes, ct);
        await stream.FlushAsync(ct);
    }

    #region Win32 Window Title Scanner Fallback

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private static (ProblemContext? Context, List<string> Titles) ScanVisibleBrowserWindows()
    {
        var titles = new List<string>();
        ProblemContext? found = null;
        if (!OperatingSystem.IsWindows())
            return (found, titles);

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd))
                    return true;
                int len = GetWindowTextLength(hWnd);
                if (len <= 0 || len > 512)
                    return true;

                var sb = new StringBuilder(len + 1);
                if (GetWindowText(hWnd, sb, sb.Capacity) <= 0)
                    return true;
                var title = sb.ToString();

                GetWindowThreadProcessId(hWnd, out uint pid);
                string procName = string.Empty;
                try
                {
                    using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                    procName = proc.ProcessName.ToLowerInvariant();
                }
                catch { }

                var lowerTitle = title.ToLowerInvariant();
                bool isBrowser = procName is "msedge" or "thorium"
                    || lowerTitle.Contains("microsoft\u200b edge")
                    || lowerTitle.Contains("microsoft edge")
                    || lowerTitle.Contains("- thorium");
                if (!isBrowser)
                    return true;

                var clean = SourceAnalyzer.StripBrowserSuffix(title);
                if (!string.IsNullOrWhiteSpace(clean) && !titles.Contains(clean))
                    titles.Add(clean);

                found ??= SourceAnalyzer.ParseContextFromWindowTitle(title, now);
                return true;
            }, IntPtr.Zero);
        }
        catch { }

        return (found, titles);
    }

    #endregion

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
