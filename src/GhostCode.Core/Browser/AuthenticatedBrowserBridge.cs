using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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
/// - Enforces loopback-only connections and `X-GhostCode-Token` authentication.
/// - Ignores background tab events (`tab_active == false`).
/// - Expires stale context after `ContextExpirationSeconds`.
/// - Never controls or modifies user source files.
/// </summary>
public sealed class AuthenticatedBrowserBridge : IAsyncDisposable
{
    private readonly GhostCodeConfig _config;
    private readonly object _ctxLock = new();
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
    }

    public int ListeningPort { get; private set; }
    public string BridgeToken => _config.BridgeAuthToken;

    public event Action<ProblemContext?>? ContextChanged;

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

        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _win32Task = Task.Run(() => Win32ScannerLoopAsync(_cts.Token));
    }

    public void ForceWin32ScanNow()
    {
        var (winCtx, titles) = ScanVisibleBrowserWindows();
        lock (_ctxLock)
        {
            _browserTitles = titles;
            if (winCtx is not null)
            {
                var current = GetActiveContextLocked();
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (current is null || current.Source != "Extension" || now - current.SeenAtUnixMs > 30_000)
                {
                    _activeContext = winCtx;
                    ContextChanged?.Invoke(winCtx);
                }
            }
        }
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
                await WriteHttpResponseAsync(stream, 413, """{"error":"payload too large"}""", ct);
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
            bool isExtensionOrigin = origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
                                     || origin.StartsWith("extension://", StringComparison.OrdinalIgnoreCase);

            if (method == "OPTIONS")
            {
                await WriteHttpResponseAsync(stream, 204, string.Empty, ct);
                return;
            }

            if (method == "GET" && path.StartsWith("/handshake", StringComparison.Ordinal))
            {
                if (!isExtensionOrigin)
                {
                    await WriteHttpResponseAsync(stream, 403, """{"error":"extension origin required"}""", ct);
                    return;
                }
                var tokJson = JsonSerializer.Serialize(new { token = _config.BridgeAuthToken });
                await WriteHttpResponseAsync(stream, 200, tokJson, ct);
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
                await WriteHttpResponseAsync(stream, 200, json, ct);
                return;
            }

            if (method == "POST" && path.StartsWith("/context", StringComparison.Ordinal))
            {
                headers.TryGetValue("X-GhostCode-Token", out var suppliedToken);
                if (!string.Equals(suppliedToken, _config.BridgeAuthToken, StringComparison.Ordinal))
                {
                    await WriteHttpResponseAsync(stream, 401, """{"error":"unauthorized"}""", ct);
                    return;
                }

                BrowserContextPayload? payload;
                try
                {
                    payload = JsonSerializer.Deserialize<BrowserContextPayload>(bodyBytes);
                }
                catch
                {
                    await WriteHttpResponseAsync(stream, 400, """{"error":"invalid json"}""", ct);
                    return;
                }

                if (payload is null)
                {
                    await WriteHttpResponseAsync(stream, 400, """{"error":"empty payload"}""", ct);
                    return;
                }

                // Never allow background tabs to overwrite the currently active problem
                if (!payload.tab_active)
                {
                    await WriteHttpResponseAsync(stream, 202, """{"ok":false,"ignored":"background_tab"}""", ct);
                    return;
                }

                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (payload.timestamp > 0 && nowMs - payload.timestamp > _config.ContextExpirationSeconds * 1000L)
                {
                    await WriteHttpResponseAsync(stream, 202, """{"ok":false,"ignored":"stale_timestamp"}""", ct);
                    return;
                }

                ProblemContext? parsed = null;
                if (!string.IsNullOrWhiteSpace(payload.url))
                    parsed = SourceAnalyzer.ParseContextFromUrl(payload.url, payload.problem_title, nowMs);

                if (parsed is null && !string.IsNullOrWhiteSpace(payload.problem_code))
                {
                    var sanitized = SourceAnalyzer.SanitizeCode(payload.problem_code);
                    if (sanitized is not null)
                    {
                        var plat = Enum.TryParse<OjPlatform>(payload.platform, true, out var p)
                            ? p
                            : SourceAnalyzer.GuessPlatformFromCode(sanitized);
                        parsed = new ProblemContext(
                            plat,
                            sanitized,
                            SourceAnalyzer.CleanTitle(payload.problem_title, sanitized),
                            payload.url ?? string.Empty,
                            "Extension",
                            nowMs);
                    }
                }

                if (parsed is null)
                {
                    await WriteHttpResponseAsync(stream, 400, """{"error":"unrecognized problem"}""", ct);
                    return;
                }

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

                await WriteHttpResponseAsync(stream, 200, """{"ok":true}""", ct);
                return;
            }

            await WriteHttpResponseAsync(stream, 404, """{"error":"not found"}""", ct);
        }
    }

    private static async Task WriteHttpResponseAsync(NetworkStream stream, int statusCode, string body, CancellationToken ct)
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
        var header = $"HTTP/1.1 {statusCode} {statusText}\r\n"
            + "Content-Type: application/json; charset=utf-8\r\n"
            + $"Content-Length: {bodyBytes.Length}\r\n"
            + "Access-Control-Allow-Origin: *\r\n"
            + "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n"
            + "Access-Control-Allow-Headers: Content-Type, X-GhostCode-Token\r\n"
            + "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
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
