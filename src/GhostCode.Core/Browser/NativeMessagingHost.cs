using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace GhostCode.Core.Browser;

/// <summary>
/// Chromium Native Messaging host protocol (4-byte little-endian length prefix + UTF-8 JSON)
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
