using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GhostCode.Core.Analysis;
using GhostCode.Core.Models;

namespace GhostCode.Core.Browser;

public sealed partial class AuthenticatedBrowserBridge
{
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

    private void WritePairingFileIfConfigured()
    {
        if (!string.IsNullOrWhiteSpace(_config.PairingFilePath))
            WriteAtomicPairingFile(_config.PairingFilePath, browserName: null);

        // Keep any already-staged per-browser extension folders synced when running against real AppData
        try
        {
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localApp) && !string.IsNullOrWhiteSpace(_config.DatabasePath)
                && Path.GetFullPath(_config.DatabasePath).StartsWith(Path.GetFullPath(localApp), StringComparison.OrdinalIgnoreCase))
            {
                foreach (var kind in new[] { BrowserKind.Chrome, BrowserKind.Edge, BrowserKind.Firefox, BrowserKind.Thorium })
                {
                    var stagedDir = BrowserIntegrationService.GetStagedDirectory(kind);
                    var stagedPairing = Path.Combine(stagedDir, "pairing.generated.json");
                    if (Directory.Exists(stagedDir) && File.Exists(stagedPairing))
                    {
                        var srcDir = BrowserIntegrationService.LocateBundledExtensionFolder(
                            kind == BrowserKind.Firefox ? "browser-extension-firefox" : "browser-extension");
                        if (srcDir is not null)
                        {
                            foreach (var file in Directory.GetFiles(srcDir))
                            {
                                var name = Path.GetFileName(file);
                                if (name.Equals("pairing.generated.json", StringComparison.OrdinalIgnoreCase))
                                    continue;
                                var destFile = Path.Combine(stagedDir, name);
                                if (!File.Exists(destFile) || File.GetLastWriteTimeUtc(file) > File.GetLastWriteTimeUtc(destFile))
                                    File.Copy(file, destFile, overwrite: true);
                            }
                        }
                        WriteAtomicPairingFile(stagedPairing, kind.ToString());
                    }
                }
            }
        }
        catch { }
    }

    private void WriteAtomicPairingFile(string path, string? browserName)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = browserName is null
                ? JsonSerializer.Serialize(new
                {
                    pairing_id = DefaultPairingId,
                    pairing_secret = _config.BridgeAuthToken,
                    port = ListeningPort
                })
                : JsonSerializer.Serialize(new
                {
                    pairing_id = DefaultPairingId,
                    pairing_secret = _config.BridgeAuthToken,
                    port = ListeningPort,
                    browser_name = browserName
                });
            var tmp = $"{path}.tmp.{Guid.NewGuid():N}";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    public bool ShouldRunWin32FallbackScan()
    {
        lock (_authLock)
        {
            return !HasFreshExtensionSessionLocked();
        }
    }

    private bool HasFreshExtensionSessionLocked()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var kv in _browserSessions)
        {
            if (now - kv.Value.LastHeartbeatUnixMs <= 30_000)
                return true;
        }
        return false;
    }

    private async Task Win32ScannerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool shouldScan = ShouldRunWin32FallbackScan();
                if (shouldScan)
                {
                    ForceWin32ScanNow();
                }
                else
                {
                    bool notifyExpired = false;
                    lock (_ctxLock)
                    {
                        var had = _activeContext is not null;
                        if (had && GetActiveContextLocked() is null)
                            notifyExpired = true;
                    }
                    if (notifyExpired)
                        ContextChanged?.Invoke(null);
                }

                await Task.Delay(TimeSpan.FromSeconds(shouldScan ? 6 : 10), ct);
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
                headers.TryGetValue("X-GhostCode-Browser", out var browserHdr);
                headers.TryGetValue("X-GhostCode-Ext-Version", out var extVerHdr);

                if (!long.TryParse(tsStr, out var tsMs)
                    || !TryPairAndIssueSessionToken(pairingId, nonce, tsMs, proof, out var issuedToken, out var expiresIn))
                {
                    await WriteHttpResponseAsync(stream, 403, """{"error":"invalid pairing proof"}""", allowedOrigin, ct);
                    return;
                }

                if (TryParseBrowserKind(browserHdr, out var hsBrowser))
                    RecordBrowserHeartbeat(hsBrowser, extVerHdr);

                var tokJson = JsonSerializer.Serialize(new { session_token = issuedToken, expires_in = expiresIn });
                await WriteHttpResponseAsync(stream, 200, tokJson, allowedOrigin, ct);
                return;
            }

            if (method == "POST" && path.StartsWith("/heartbeat", StringComparison.Ordinal))
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

                headers.TryGetValue("X-GhostCode-Browser", out var browserHdr);
                headers.TryGetValue("X-GhostCode-Ext-Version", out var extVerHdr);
                if (TryParseBrowserKind(browserHdr, out var hbBrowser))
                    RecordBrowserHeartbeat(hbBrowser, extVerHdr);

                await WriteHttpResponseAsync(stream, 200, """{"ok":true}""", allowedOrigin, ct);
                return;
            }

            if (method == "GET" && path.StartsWith("/status", StringComparison.Ordinal))
            {
                var ctx = GetActiveContext();
                var json = JsonSerializer.Serialize(new
                {
                    ok = true,
                    context = ctx,
                    browser_titles = GetDetectedBrowserTitles(),
                    browsers = GetBrowserConnectionStates()
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

                headers.TryGetValue("X-GhostCode-Browser", out var browserHdr);
                if (TryParseBrowserKind(browserHdr, out var clrBrowser))
                    RecordBrowserHeartbeat(clrBrowser, null);

                ClearActiveContext();
                await WriteHttpResponseAsync(stream, 200, """{"ok":true,"cleared":true}""", allowedOrigin, ct);
                return;
            }

            if (method == "POST" && (path.StartsWith("/context", StringComparison.Ordinal)
                                       || path.StartsWith("/problem", StringComparison.Ordinal)
                                       || path.StartsWith("/submission", StringComparison.Ordinal)))
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

                headers.TryGetValue("X-GhostCode-Browser", out var browserHdr);
                headers.TryGetValue("X-GhostCode-Ext-Version", out var extVerHdr);

                try
                {
                    if (path.StartsWith("/problem", StringComparison.Ordinal))
                    {
                        var probPayload = JsonSerializer.Deserialize<BrowserProblemPayload>(bodyBytes);
                        RecordHeartbeatFromPayloadOrHeaders(probPayload?.browser_name, browserHdr, extVerHdr);
                        var (status, responseBody) = await ApplyProblemPayloadAsync(probPayload, ct);
                        await WriteHttpResponseAsync(stream, status, responseBody, allowedOrigin, ct);
                        return;
                    }

                    if (path.StartsWith("/submission", StringComparison.Ordinal))
                    {
                        var subPayload = JsonSerializer.Deserialize<BrowserSubmissionPayload>(bodyBytes);
                        RecordHeartbeatFromPayloadOrHeaders(subPayload?.browser_name, browserHdr, extVerHdr);
                        var (status, responseBody) = await ApplySubmissionPayloadAsync(subPayload, ct);
                        await WriteHttpResponseAsync(stream, status, responseBody, allowedOrigin, ct);
                        return;
                    }

                    var payload = JsonSerializer.Deserialize<BrowserContextPayload>(bodyBytes);
                    RecordHeartbeatFromPayloadOrHeaders(payload?.browser_name, browserHdr, extVerHdr);
                    if (payload is not null && string.IsNullOrWhiteSpace(payload.browser_name) && !string.IsNullOrWhiteSpace(browserHdr))
                        payload = payload with { browser_name = browserHdr };

                    var (ctxStatus, ctxResponseBody) = ApplyContextPayload(payload);
                    await WriteHttpResponseAsync(stream, ctxStatus, ctxResponseBody, allowedOrigin, ct);
                    return;
                }
                catch (JsonException)
                {
                    await WriteHttpResponseAsync(stream, 400, """{"error":"invalid json"}""", allowedOrigin, ct);
                    return;
                }
            }

            await WriteHttpResponseAsync(stream, 404, """{"error":"not found"}""", allowedOrigin, ct);
        }
    }

    private static string? TryGetAllowedExtensionOrigin(string origin)
    {
        if (string.IsNullOrWhiteSpace(origin) || ContainsControlChars(origin))
            return null;
        if (origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
            || origin.StartsWith("extension://", StringComparison.OrdinalIgnoreCase)
            || origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase))
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
        sb.Append("Access-Control-Allow-Headers: Content-Type, X-GhostCode-Token, X-GhostCode-Pairing-Id, X-GhostCode-Nonce, X-GhostCode-Timestamp, X-GhostCode-Proof, X-GhostCode-Browser, X-GhostCode-Ext-Version\r\n");
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

                var lowerTitle = title.ToLowerInvariant();
                bool titleLooksLikeBrowser = lowerTitle.Contains("microsoft\u200b edge")
                    || lowerTitle.Contains("microsoft edge")
                    || lowerTitle.Contains("- thorium")
                    || lowerTitle.Contains("google chrome")
                    || lowerTitle.Contains("mozilla firefox")
                    || lowerTitle.Contains("ptit")
                    || lowerTitle.Contains("codeforces")
                    || lowerTitle.Contains("leetcode")
                    || lowerTitle.Contains("vnoj")
                    || lowerTitle.Contains("hackerrank");
                if (!titleLooksLikeBrowser)
                    return true;

                GetWindowThreadProcessId(hWnd, out uint pid);
                string procName = GhostCode.Core.Monitoring.WindowsProcessSnapshot.TryGetProcessNameByPid((int)pid)?.ToLowerInvariant() ?? string.Empty;

                bool isBrowser = procName is "msedge" or "thorium" or "chrome" or "firefox"
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
}
