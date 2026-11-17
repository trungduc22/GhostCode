using System.IO.Pipes;
using System.Text;
using GhostCode.Core.Archiver;
using GhostCode.Core.Browser;
using GhostCode.Core.Interop;
using GhostCode.Core.Models;
using GhostCode.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GhostCode.Tests;

public sealed class GracefulShutdownTests
{
    [Fact]
    public void EventNames_AreDeterministicPerCanonicalExecutablePath_AndIsolatedAcrossPaths()
    {
        var eventA = ProcessShutdownSignal.GetEventName(@"C:\Program Files\GhostCode\GhostCode.App.exe");
        var eventASame = ProcessShutdownSignal.GetEventName(@"  c:\program files\ghostcode\..\GhostCode\ghostcode.app.exe ");
        var eventB = ProcessShutdownSignal.GetEventName(@"D:\Portable\GhostCode\GhostCode.App.exe");

        Assert.StartsWith("Local\\GhostCode_Exit_", eventA, StringComparison.Ordinal);
        Assert.Equal(eventA, eventASame);
        Assert.NotEqual(eventA, eventB);
    }

    [Fact]
    public async Task SignalingOnePath_TriggersOnlyMatchingListener_LeavingOtherPathsUntouched()
    {
        var unique = Guid.NewGuid().ToString("N");
        var pathInstalled = $@"C:\FakeInstalled_{unique}\GhostCode.App.exe";
        var pathPortable = $@"D:\FakePortable_{unique}\GhostCode.App.exe";

        var installedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var portableTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var regInstalled = ProcessShutdownSignal.RegisterListener(() => installedTcs.TrySetResult(true), pathInstalled);
        using var regPortable = ProcessShutdownSignal.RegisterListener(() => portableTcs.TrySetResult(true), pathPortable);

        Assert.True(ProcessShutdownSignal.TrySignalExit(pathInstalled));
        var fired = await installedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fired);

        await Task.Delay(150);
        Assert.False(portableTcs.Task.IsCompleted);
    }

    [Fact]
    public async Task CheckpointWalAsync_FlushesPendingWalFramesIntoMainDatabaseFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ghostcode_wal_ckpt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var dbPath = Path.Combine(root, "ghostcode.db");
            var srcDir = Path.Combine(root, "src");
            var archDir = Path.Combine(root, "arch");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(archDir);

            var srcFile = Path.Combine(srcDir, "main.cpp");
            byte[] bytes = Encoding.UTF8.GetBytes("#include <iostream>\nint main(){ std::cout << 42 << '\\n'; return 0; }\n");
            await File.WriteAllBytesAsync(srcFile, bytes);

            var cfg = GhostCodeConfig.CreateDefault(root, srcDir);
            cfg.DatabasePath = dbPath;
            cfg.ArchiveDir = archDir;
            cfg.ReadyDir = string.Empty;

            await using var store = new SqliteArchiveStore(dbPath);
            await store.InitializeAsync();
            var archiver = new LosslessArchiver(cfg, store);
            var res = await archiver.ProcessFileSaveAsync(srcFile, bytes, null);
            Assert.NotNull(res.Version);

            await store.CheckpointWalAsync();

            var walPath = dbPath + "-wal";
            Assert.True(!File.Exists(walPath) || new FileInfo(walPath).Length == 0);

            var health = await store.CheckDatabaseHealthAsync();
            Assert.True(health.Healthy);

            var versions = await store.QueryVersionsAsync(limit: 50);
            Assert.Contains(versions, v => v.Id == res.Version.Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task NativeMessagingBridgeLoop_ExitsZeroPromptlyWhenPathSignalFires()
    {
        var fakeExe = $@"C:\FakeNm_{Guid.NewGuid():N}\GhostCode.App.exe";
        using var shutdownCts = ProcessShutdownSignal.CreateLinkedCancellationTokenSource(
            CancellationToken.None,
            out var registration,
            fakeExe);
        using (registration)
        using (var pipeServer = new AnonymousPipeServerStream(PipeDirection.Out))
        using (var pipeClient = new AnonymousPipeClientStream(PipeDirection.In, pipeServer.ClientSafePipeHandle))
        using (var outStream = new MemoryStream())
        using (var http = new HttpClient())
        {
            var cfg = new GhostCodeConfig { HttpPort = 0 };
            var runTask = NativeMessagingHost.RunStdioBridgeStreamsAsync(
                pipeClient,
                outStream,
                cfg,
                "Thorium",
                http,
                shutdownCts.Token);

            await Task.Delay(120);
            Assert.False(runTask.IsCompleted);

            Assert.True(ProcessShutdownSignal.TrySignalExit(fakeExe));
            var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, exitCode);
        }
    }
}
