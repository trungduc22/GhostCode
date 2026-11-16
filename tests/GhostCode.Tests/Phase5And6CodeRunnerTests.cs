using System.Text;
using GhostCode.Core.Archiver;
using GhostCode.Core.Execution;
using GhostCode.Core.Models;
using GhostCode.Core.Storage;
using GhostCode.Core.Toolchains;

namespace GhostCode.Tests;

public sealed class Phase5And6CodeRunnerTests : IDisposable
{
    private readonly string _root;

    public Phase5And6CodeRunnerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GhostCode_P5P6_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private (GhostCodeConfig Cfg, SqliteArchiveStore Store, LosslessArchiver Archiver, CodeExecutionService Runner) CreateEnv()
    {
        var archiveDir = Path.Combine(_root, "Archive");
        var readyDir = Path.Combine(_root, "Ready");
        Directory.CreateDirectory(archiveDir);
        Directory.CreateDirectory(readyDir);
        var cfg = new GhostCodeConfig
        {
            ArchiveDir = archiveDir,
            ReadyDir = readyDir,
            DatabasePath = Path.Combine(archiveDir, "ghostcode.db"),
            ExecutionTimeoutMs = 4000,
            ExecutionMemoryLimitMb = 256
        };
        var store = new SqliteArchiveStore(cfg.DatabasePath);
        store.InitializeAsync().GetAwaiter().GetResult();
        var archiver = new LosslessArchiver(cfg, store);
        var runner = new CodeExecutionService(cfg, store, archiver, new ToolchainDiscoveryService());
        return (cfg, store, archiver, runner);
    }

    [Fact]
    public async Task Runner_ExecutesExactSelectedHistoricalVersion_V1vsV2_WithRealGppAndStdin()
    {
        var (_, store, archiver, runner) = CreateEnv();
        await using var _ = store;

        var srcPath = Path.Combine(_root, "My Workspace", "Baitoan_Tổng_Hai_Số.cpp");
        Directory.CreateDirectory(Path.GetDirectoryName(srcPath)!);

        const string v1Code = """
            #include <iostream>
            int main() {
                long long a, b;
                if (std::cin >> a >> b) std::cout << "V1=" << (a + b) << "\n";
                return 0;
            }
            """;
        const string v2Code = """
            #include <iostream>
            #include <vector>
            #include <algorithm>
            int main() {
                int n;
                if (!(std::cin >> n)) return 0;
                std::vector<long long> a(n);
                for (int i = 0; i < n; ++i) std::cin >> a[i];
                std::sort(a.begin(), a.end());
                for (int i = 0; i < n; ++i) std::cout << a[i] << (i + 1 == n ? '\n' : ' ');
                return 0;
            }
            """;

        var r1 = await archiver.ProcessFileSaveAsync(srcPath, Encoding.UTF8.GetBytes(v1Code), null);
        var r2 = await archiver.ProcessFileSaveAsync(srcPath, Encoding.UTF8.GetBytes(v2Code), null);
        Assert.NotNull(r1.Version);
        Assert.NotNull(r2.Version);

        var resV1 = await runner.ExecuteArchivedVersionAsync(new CodeExecutionRequest(r1.Version!.Id, "100000000000 200000000000\n"));
        Assert.Equal(ExecutionStatus.Finished, resV1.Status);
        Assert.Equal(0, resV1.ExitCode);
        Assert.Contains("V1=300000000000", resV1.Stdout);

        var resV2 = await runner.ExecuteArchivedVersionAsync(new CodeExecutionRequest(r2.Version!.Id, "3\n9000000000000 42 -5\n"));
        Assert.Equal(ExecutionStatus.Finished, resV2.Status);
        Assert.Equal("-5 42 9000000000000", resV2.Stdout.Trim());
    }

    [Fact]
    public async Task Runner_ExecutesPythonEofInputAndHandlesErrorsTimeoutStopAndCloudOnlyGuard()
    {
        var (_, store, archiver, runner) = CreateEnv();
        await using var _ = store;

        // 1. Python EOF multi-line stdin
        var pyPath = Path.Combine(_root, "sum_eof.py");
        const string pyCode = """
            import sys
            vals = [int(x) for line in sys.stdin for x in line.split()]
            print("TOTAL=", sum(vals))
            """;
        var pyArch = await archiver.ProcessFileSaveAsync(pyPath, Encoding.UTF8.GetBytes(pyCode), null);
        var pyRes = await runner.ExecuteArchivedVersionAsync(new CodeExecutionRequest(pyArch.Version!.Id, "10 20\n30 40\n"));
        Assert.Equal(ExecutionStatus.Finished, pyRes.Status);
        Assert.Equal(0, pyRes.ExitCode);
        Assert.Contains("TOTAL= 100", pyRes.Stdout);

        // 2. Compile error diagnostics
        var cePath = Path.Combine(_root, "bad.c");
        var ceArch = await archiver.ProcessFileSaveAsync(cePath, Encoding.UTF8.GetBytes("int main(){ missing_semicolon }"), null);
        var ceRes = await runner.ExecuteArchivedVersionAsync(new CodeExecutionRequest(ceArch.Version!.Id, ""));
        Assert.Equal(ExecutionStatus.CompileError, ceRes.Status);
        Assert.False(string.IsNullOrWhiteSpace(ceRes.CompilerOutput));

        // 3. Non-zero runtime exit + stderr
        var rePath = Path.Combine(_root, "fail_exit.py");
        var reArch = await archiver.ProcessFileSaveAsync(rePath, Encoding.UTF8.GetBytes("import sys\nsys.stderr.write('BOOM_ERR\\n')\nsys.exit(7)\n"), null);
        var reRes = await runner.ExecuteArchivedVersionAsync(new CodeExecutionRequest(reArch.Version!.Id, ""));
        Assert.Equal(ExecutionStatus.RuntimeError, reRes.Status);
        Assert.Equal(7, reRes.ExitCode);
        Assert.Contains("BOOM_ERR", reRes.Stderr);

        // 4. Time limit exceeded (infinite loop)
        var tlePath = Path.Combine(_root, "infinite.cpp");
        var tleArch = await archiver.ProcessFileSaveAsync(tlePath, Encoding.UTF8.GetBytes("int main(){ while(true){} return 0; }"), null);
        var tleRes = await runner.ExecuteArchivedVersionAsync(new CodeExecutionRequest(tleArch.Version!.Id, "", TimeoutMs: 700));
        Assert.Equal(ExecutionStatus.TimeLimitExceeded, tleRes.Status);

        // 5. Explicit Stop cancellation
        var slowPy = Path.Combine(_root, "sleep.py");
        var slowArch = await archiver.ProcessFileSaveAsync(slowPy, Encoding.UTF8.GetBytes("import time\ntime.sleep(10)\n"), null);
        using var stopCts = new CancellationTokenSource(200);
        var stopRes = await runner.ExecuteArchivedVersionAsync(new CodeExecutionRequest(slowArch.Version!.Id, "", TimeoutMs: 10_000), ct: stopCts.Token);
        Assert.Equal(ExecutionStatus.Cancelled, stopRes.Status);

        // 6. Cloud-only version CANNOT be executed before local download & SHA-256 verification
        var cloudOnly = new ArchiveVersionRecord(
            "cloud-only-exec-guard", 1, Path.Combine(_root, "remote_only.cpp"), "cloud://drive_123", null,
            "remote_only.cpp", new string('a', 64), new string('a', 64), 42, "C01001", "Remote", "PTIT",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), IsLocalAvailable: false, DriveFileId: "drive_123");
        await store.UpsertRemoteVersionMetadataAsync(cloudOnly);
        await Assert.ThrowsAnyAsync<Exception>(() => runner.ExecuteArchivedVersionAsync(new CodeExecutionRequest("cloud-only-exec-guard", "")));
    }
}
