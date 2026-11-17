using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using GhostCode.Core.Browser;
using GhostCode.Core.Models;
using GhostCode.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GhostCode.Tests;

[SupportedOSPlatform("windows")]
public sealed class NativeMessagingEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ghostcode_nm_e2e_{Guid.NewGuid():N}");

    public NativeMessagingEndToEndTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void PortablePrepareIntegration_PreservesInstalledHostRegistration_WhenInstalledCopyExists()
    {
        var installedDir = Path.Combine(_root, "Installed");
        var portableDir = Path.Combine(_root, "Portable");
        var appDataDir = Path.Combine(_root, "AppData");
        Directory.CreateDirectory(installedDir);
        Directory.CreateDirectory(portableDir);
        Directory.CreateDirectory(appDataDir);

        var installedExe = Path.Combine(installedDir, "GhostCode.App.exe");
        var portableExe = Path.Combine(portableDir, "GhostCode.App.exe");
        File.WriteAllText(installedExe, "installed");
        File.WriteAllText(portableExe, "portable");

        var cfg = GhostCodeConfig.CreateDefault(_root, _root);
        var svc = new BrowserIntegrationService();

        // Installed instance writes its registration
        var installedRes = svc.PrepareIntegration(
            BrowserKind.Thorium,
            cfg,
            listeningPort: 50001,
            hostExecutablePath: installedExe,
            appDataOverride: appDataDir,
            installedHostExeOverride: installedExe);
        Assert.True(installedRes.Success);

        var cmdPath = Path.Combine(appDataDir, "NativeMessaging", "com.ghostcode.bridge.thorium.cmd");
        Assert.Contains(installedExe, File.ReadAllText(cmdPath));

        // Portable instance runs alongside installed copy: must preserve existing installed host wrapper!
        var portableRes = svc.PrepareIntegration(
            BrowserKind.Thorium,
            cfg,
            listeningPort: 50002,
            hostExecutablePath: portableExe,
            appDataOverride: appDataDir,
            installedHostExeOverride: installedExe);
        Assert.True(portableRes.Success);
        Assert.Contains("Preserved installed GhostCode Native Messaging host", portableRes.Message);
        Assert.Contains(installedExe, File.ReadAllText(cmdPath));
        Assert.DoesNotContain(portableExe, File.ReadAllText(cmdPath));
        Assert.True(File.Exists(Path.Combine(portableRes.ExtensionDirectory, "pairing.generated.json")));
    }

    [Fact]
    public async Task PackagedExecutable_NativeMessagingCmdWrapper_HandshakesAndDeliversContextOverRealStdio()
    {
        var exePath = LocateCompiledAppExe();
        if (exePath is null)
            return;

        var dbPath = Path.Combine(_root, "ghostcode.db");
        var cfgPath = Path.Combine(_root, "config.json");
        var cfg = GhostCodeConfig.CreateDefault(_root, _root);
        cfg.DatabasePath = dbPath;
        cfg.HttpPort = 0;

        await using var store = new SqliteArchiveStore(dbPath);
        await store.InitializeAsync();
        var browserSvc = new BrowserIntegrationService();
        await using var bridge = new AuthenticatedBrowserBridge(cfg, browserSvc, store);
        bridge.Start();
        Assert.True(bridge.ListeningPort > 0);
        cfg.HttpPort = bridge.ListeningPort;
        cfg.Save(cfgPath);

        var nmDir = Path.Combine(_root, "NativeMessaging");
        Directory.CreateDirectory(nmDir);
        var cmdPath = Path.Combine(nmDir, "com.ghostcode.bridge.thorium.cmd");
        File.WriteAllText(cmdPath, $"@echo off\r\n\"{exePath}\" --native-messaging-host --browser Thorium %*\r\n", Encoding.ASCII);

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/d /s /c \"\"{cmdPath}\" chrome-extension://bifmfiolmohcdfknnicfikhhpipkokbc/\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.EnvironmentVariables["GHOSTCODE_CONFIG_PATH"] = cfgPath;

        using var proc = Process.Start(psi);
        Assert.NotNull(proc);

        try
        {
            var stdin = proc.StandardInput.BaseStream;
            var stdout = proc.StandardOutput.BaseStream;

            await WriteNativeMessageAsync(stdin, new { type = "handshake" });
            using var hsDoc = await ReadNativeMessageAsync(stdout, TimeSpan.FromSeconds(15));
            Assert.True(hsDoc.RootElement.GetProperty("ok").GetBoolean(), hsDoc.RootElement.GetRawText());
            Assert.Equal("Thorium", hsDoc.RootElement.GetProperty("browser_name").GetString());

            await WriteNativeMessageAsync(stdin, new
            {
                type = "context",
                payload = new
                {
                    platform = "Ptit",
                    problem_code = "C04032",
                    problem_title = "Liệt kê các số xuất hiện",
                    problem_url = "https://code.ptit.edu.vn/student/question/C04032",
                    tab_title = "C04032 - CodePTIT",
                    browser_name = "Thorium",
                    detected_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }
            });
            using var ctxDoc = await ReadNativeMessageAsync(stdout, TimeSpan.FromSeconds(10));
            Assert.True(ctxDoc.RootElement.GetProperty("ok").GetBoolean(), ctxDoc.RootElement.GetRawText());
            Assert.Equal("C04032", bridge.GetActiveContext()?.Code);

            await WriteNativeMessageAsync(stdin, new { type = "clear_context" });
            using var clrDoc = await ReadNativeMessageAsync(stdout, TimeSpan.FromSeconds(10));
            Assert.True(clrDoc.RootElement.GetProperty("ok").GetBoolean(), clrDoc.RootElement.GetRawText());
            Assert.Null(bridge.GetActiveContext());

            // Closing stdin simulates Chromium closing the native messaging port cleanly
            stdin.Close();
            await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, proc.ExitCode);
        }
        finally
        {
            if (!proc.HasExited)
                try { proc.Kill(entireProcessTree: true); } catch { }
            await bridge.StopAsync();
        }
    }

    private static async Task WriteNativeMessageAsync(Stream stream, object payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        var lenPrefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lenPrefix, json.Length);
        await stream.WriteAsync(lenPrefix);
        await stream.WriteAsync(json);
        await stream.FlushAsync();
    }

    private static async Task<JsonDocument> ReadNativeMessageAsync(Stream stream, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var lenBuf = new byte[4];
        await ReadExactAsync(stream, lenBuf, cts.Token);
        int len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
        Assert.InRange(len, 1, 1024 * 1024);
        var payload = new byte[len];
        await ReadExactAsync(stream, payload, cts.Token);
        return JsonDocument.Parse(payload);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0)
                throw new EndOfStreamException($"Unexpected EOF after {offset}/{buffer.Length} bytes.");
            offset += read;
        }
    }

    private static string? LocateCompiledAppExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var packaged = Path.Combine(dir.FullName, "artifacts", "Release", "GhostCode-win-x64", "GhostCode.App.exe");
            if (File.Exists(packaged))
                return packaged;
            var releaseBin = Path.Combine(dir.FullName, "src", "GhostCode.App", "bin", "x64", "Release", "net10.0-windows10.0.19041.0", "win-x64", "GhostCode.App.exe");
            if (File.Exists(releaseBin))
                return releaseBin;
            dir = dir.Parent;
        }
        return null;
    }
}
