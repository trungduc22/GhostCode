using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using GhostCode.Core.Models;

namespace GhostCode.Core.Browser;

/// <summary>
/// Chromium / Firefox Native Messaging host protocol (4-byte little-endian length prefix + UTF-8 JSON)
/// for direct authenticated extension-to-app pairing and context delivery.
/// </summary>
public sealed class NativeMessagingHost
{
    public const string HostName = "com.ghostcode.bridge";
    private readonly AuthenticatedBrowserBridge _bridge;

    public NativeMessagingHost(AuthenticatedBrowserBridge bridge)
    {
        _bridge = bridge;
    }

    public static bool IsNativeMessagingInvocation(string[]? args)
    {
        if (args is null || args.Length == 0)
            return false;
        foreach (var a in args)
        {
            if (string.IsNullOrWhiteSpace(a))
                continue;
            if (a.Equals("--native-messaging-host", StringComparison.OrdinalIgnoreCase)
                || a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
                || a.Contains(BrowserIntegrationService.FirefoxExtensionId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public static (string BrowserHint, string? ConfigPath) ParseHostArgs(string[] args)
    {
        string browserHint = string.Empty;
        string? configPath = Environment.GetEnvironmentVariable("GHOSTCODE_CONFIG_PATH");
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--browser", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                browserHint = args[i + 1].Trim();
            else if (args[i].Equals("--config", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                configPath = args[i + 1].Trim();
        }
        return (browserHint, string.IsNullOrWhiteSpace(configPath) ? null : configPath);
    }

    public static async Task<int> RunStdioHostAsync(string[] args, CancellationToken ct = default)
    {
        var (browserHint, configPath) = ParseHostArgs(args);
        var cfg = GhostCodeConfig.LoadOrDefault(configPath ?? GhostCodeConfig.DefaultConfigPath());
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        return await RunStdioBridgeStreamsAsync(input, output, cfg, browserHint, http, ct);
    }

    public static async Task<int> RunStdioBridgeStreamsAsync(
        Stream input,
        Stream output,
        GhostCodeConfig config,
        string browserHint,
        HttpClient http,
        CancellationToken ct = default)
    {
        string sessionToken = string.Empty;
        string effectiveBrowser = string.IsNullOrWhiteSpace(browserHint) ? "Chrome" : browserHint.Trim();

        while (!ct.IsCancellationRequested)
        {
            var lenBuf = new byte[4];
            int headerRead;
            try
            {
                headerRead = await ReadExactAsync(input, lenBuf, ct).WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return 0;
            }
            if (headerRead == 0)
                return 0;
            if (headerRead < 4)
                return 1;

            int len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (len <= 0 || len > 1024 * 1024)
                return 1;

            var msgBytes = new byte[len];
            if (await ReadExactAsync(input, msgBytes, ct) < len)
                return 1;

            string responseJson = await ForwardMessageToBridgeAsync(msgBytes, config, effectiveBrowser, sessionToken, http, tok => sessionToken = tok, ct);
            await WriteMessageAsync(output, responseJson, ct);
        }
        return 0;
    }

    private static async Task<string> ForwardMessageToBridgeAsync(
        byte[] utf8Json,
        GhostCodeConfig config,
        string browserHint,
        string currentSessionToken,
        HttpClient http,
        Action<string> updateSessionToken,
        CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(utf8Json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var tEl) ? (tEl.GetString() ?? string.Empty) : string.Empty;
            var baseUrl = $"http://127.0.0.1:{config.HttpPort}";

            async Task<(bool Ok, string Token, string? Error)> EnsureSessionAsync()
            {
                if (!string.IsNullOrEmpty(currentSessionToken))
                    return (true, currentSessionToken, null);

                var nonce = Guid.NewGuid().ToString("N");
                var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var proof = AuthenticatedBrowserBridge.ComputeHandshakeProof(config.BridgeAuthToken, nonce, ts);
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/handshake")
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
                req.Headers.Add("X-GhostCode-Browser", browserHint);
                req.Headers.Add("X-GhostCode-Ext-Version", "1.4.0");
                req.Headers.Add("X-GhostCode-Pairing-Id", AuthenticatedBrowserBridge.DefaultPairingId);
                req.Headers.Add("X-GhostCode-Nonce", nonce);
                req.Headers.Add("X-GhostCode-Timestamp", ts.ToString());
                req.Headers.Add("X-GhostCode-Proof", proof);

                try
                {
                    using var resp = await http.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode)
                        return (false, string.Empty, $"handshake_http_{(int)resp.StatusCode}");
                    using var rDoc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                    var tok = rDoc.RootElement.TryGetProperty("session_token", out var st) ? (st.GetString() ?? string.Empty) : string.Empty;
                    if (!string.IsNullOrEmpty(tok))
                        updateSessionToken(tok);
                    return (!string.IsNullOrEmpty(tok), tok, string.IsNullOrEmpty(tok) ? "missing_session_token" : null);
                }
                catch (Exception ex)
                {
                    return (false, string.Empty, ex.Message);
                }
            }

            switch (type.ToLowerInvariant())
            {
                case "handshake":
                {
                    var (ok, tok, err) = await EnsureSessionAsync();
                    return JsonSerializer.Serialize(new
                    {
                        ok,
                        port = config.HttpPort,
                        pairing_id = AuthenticatedBrowserBridge.DefaultPairingId,
                        session_token = tok,
                        browser_name = browserHint,
                        error = err
                    });
                }

                case "context":
                {
                    var (ok, tok, err) = await EnsureSessionAsync();
                    if (!ok)
                        return JsonSerializer.Serialize(new { ok = false, port = config.HttpPort, browser_name = browserHint, error = err });

                    string payloadJson = root.TryGetProperty("payload", out var pEl)
                        ? pEl.GetRawText()
                        : Encoding.UTF8.GetString(utf8Json);
                    using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/context")
                    {
                        Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
                    };
                    req.Headers.Add("X-GhostCode-Token", tok);
                    req.Headers.Add("X-GhostCode-Browser", browserHint);
                    using var resp = await http.SendAsync(req, ct);
                    return JsonSerializer.Serialize(new
                    {
                        ok = resp.IsSuccessStatusCode,
                        status = (int)resp.StatusCode,
                        browser_name = browserHint
                    });
                }

                case "clear_context":
                {
                    var (ok, tok, err) = await EnsureSessionAsync();
                    if (!ok)
                        return JsonSerializer.Serialize(new { ok = false, error = err });
                    using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/context/clear")
                    {
                        Content = new StringContent("{}", Encoding.UTF8, "application/json")
                    };
                    req.Headers.Add("X-GhostCode-Token", tok);
                    using var resp = await http.SendAsync(req, ct);
                    return JsonSerializer.Serialize(new { ok = resp.IsSuccessStatusCode, cleared = true });
                }

                default:
                    return JsonSerializer.Serialize(new { ok = false, error = "unknown_message_type", browser_name = browserHint });
            }
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }
    }

    public async Task<bool> ProcessNextMessageAsync(Stream input, Stream output, CancellationToken ct = default)
    {
        var lenBuf = new byte[4];
        int headerRead = await ReadExactAsync(input, lenBuf, ct);
        if (headerRead == 0)
            return false;
        if (headerRead < 4)
            throw new EndOfStreamException("Truncated Native Messaging length prefix.");

        int len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
        if (len <= 0 || len > 1024 * 1024)
            throw new InvalidDataException($"Invalid Native Messaging payload length: {len}.");

        var msgBytes = new byte[len];
        if (await ReadExactAsync(input, msgBytes, ct) < len)
            throw new EndOfStreamException("Truncated Native Messaging payload.");

        string responseJson = HandleRequestJson(msgBytes);
        await WriteMessageAsync(output, responseJson, ct);
        return true;
    }

    public string HandleRequestJson(byte[] utf8Json)
    {
        try
        {
            using var doc = JsonDocument.Parse(utf8Json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var tEl) ? (tEl.GetString() ?? string.Empty) : string.Empty;

            switch (type.ToLowerInvariant())
            {
                case "handshake":
                    return JsonSerializer.Serialize(new
                    {
                        ok = true,
                        port = _bridge.ListeningPort,
                        pairing_id = AuthenticatedBrowserBridge.DefaultPairingId,
                        session_token = _bridge.CurrentSessionToken
                    });

                case "context":
                    var payload = root.TryGetProperty("payload", out var pEl)
                        ? JsonSerializer.Deserialize<BrowserContextPayload>(pEl.GetRawText())
                        : JsonSerializer.Deserialize<BrowserContextPayload>(utf8Json);
                    var (status, body) = _bridge.ApplyContextPayload(payload);
                    return JsonSerializer.Serialize(new
                    {
                        ok = status == 200,
                        status,
                        detail = JsonDocument.Parse(body).RootElement.Clone()
                    });

                case "clear_context":
                    _bridge.ClearActiveContext();
                    return """{"ok":true,"cleared":true}""";

                case "status":
                    return JsonSerializer.Serialize(new
                    {
                        ok = true,
                        port = _bridge.ListeningPort,
                        context = _bridge.GetActiveContext()
                    });

                default:
                    return """{"ok":false,"error":"unknown_message_type"}""";
            }
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }
    }

    public static async Task WriteMessageAsync(Stream output, string json, CancellationToken ct = default)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var lenPrefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lenPrefix, payload.Length);
        await output.WriteAsync(lenPrefix, ct);
        await output.WriteAsync(payload, ct);
        await output.FlushAsync(ct);
    }

    public static void WriteNativeMessagingManifest(string manifestPath, string hostExecutablePath, string extensionOrigin)
    {
        var dir = Path.GetDirectoryName(manifestPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var manifest = JsonSerializer.Serialize(new
        {
            name = HostName,
            description = "GhostCode Native Messaging Bridge",
            path = hostExecutablePath,
            type = "stdio",
            allowed_origins = new[] { extensionOrigin }
        }, new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(manifestPath, manifest, Encoding.UTF8);
    }

    private static async Task<int> ReadExactAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await s.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (n == 0)
                return total;
            total += n;
        }
        return total;
    }
}
