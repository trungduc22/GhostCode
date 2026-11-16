using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GhostCode.Core.Analysis;
using GhostCode.Core.Archiver;
using GhostCode.Core.Models;
using GhostCode.Core.Storage;
using GhostCode.Core.Toolchains;

namespace GhostCode.Core.Execution;

public enum ExecutionStatus
{
    Idle,
    Compiling,
    Running,
    Finished,
    CompileError,
    RuntimeError,
    TimeLimitExceeded,
    MemoryLimitExceeded,
    Cancelled
}

public sealed record CodeExecutionRequest(
    string VersionId,
    string StdinText,
    int TimeoutMs = 5000,
    int MemoryLimitMb = 256);

public sealed record CodeExecutionResult(
    ExecutionStatus Status,
    string CommandLineDisplay,
    string CompilerOutput,
    string Stdout,
    string Stderr,
    int? ExitCode,
    long ElapsedMs,
    double? PeakMemoryMb,
    string StatusBadgeText);

/// <summary>
/// Compiles and executes an exact verified historical version inside an isolated temporary folder
/// bounded by Windows Job Objects (kill-on-close &amp; memory limit) and per-run timeouts.
/// NOTE: Timeout + Windows Job Objects bound runaway CPU/memory/child processes,
/// but are not a full OS hypervisor/AppContainer security sandbox against arbitrary syscalls.
/// </summary>
public sealed class CodeExecutionService
{
    private const int MaxCaptureBytes = 256 * 1024;
    private readonly GhostCodeConfig _config;
    private readonly SqliteArchiveStore _store;
    private readonly LosslessArchiver _archiver;
    private readonly ToolchainDiscoveryService _toolchains;

    public CodeExecutionService(
        GhostCodeConfig config,
        SqliteArchiveStore store,
        LosslessArchiver archiver,
        ToolchainDiscoveryService? toolchains = null)
    {
        _config = config;
        _store = store;
        _archiver = archiver;
        _toolchains = toolchains ?? new ToolchainDiscoveryService();
    }

    public async Task<CodeExecutionResult> ExecuteArchivedVersionAsync(
        CodeExecutionRequest request,
        Action<string>? onOutputChunk = null,
        CancellationToken ct = default)
    {
        var version = await _store.GetVersionByIdAsync(request.VersionId, ct)
            ?? throw new InvalidOperationException($"Archive version '{request.VersionId}' not found.");

        // 1. Read & verify SHA-256 of the exact historical archive file (blocks un-downloaded CloudOnly too)
        var exactBytes = await _archiver.ReadAndVerifyVersionBytesAsync(request.VersionId, ct);

        var lang = SourceAnalyzer.LanguageFromPath(version.FileName)
            ?? SourceAnalyzer.LanguageFromPath(version.SourcePath)
            ?? SourceLanguage.Cpp;

        var tempRunDir = Path.Combine(Path.GetTempPath(), "GhostCode_Exec_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRunDir);

        try
        {
            ct.ThrowIfCancellationRequested();
            if (lang == SourceLanguage.Python)
                return await RunPythonAsync(version, exactBytes, tempRunDir, request, onOutputChunk, ct);

            if (lang == SourceLanguage.C)
                return await CompileAndRunNativeAsync(isCpp: false, version, exactBytes, tempRunDir, request, onOutputChunk, ct);

            if (lang == SourceLanguage.Cpp)
                return await CompileAndRunNativeAsync(isCpp: true, version, exactBytes, tempRunDir, request, onOutputChunk, ct);

            return new CodeExecutionResult(
                ExecutionStatus.CompileError,
                string.Empty,
                $"Header or unsupported file type '{Path.GetExtension(version.FileName)}' cannot be executed directly.",
                string.Empty,
                string.Empty,
                null,
                0,
                null,
                "Failed | Unsupported language");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new CodeExecutionResult(
                ExecutionStatus.Cancelled,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                null,
                0,
                null,
                "Stopped | Cancelled");
        }
        finally
        {
            try { Directory.Delete(tempRunDir, recursive: true); } catch { }
        }
    }

    private async Task<CodeExecutionResult> CompileAndRunNativeAsync(
        bool isCpp,
        ArchiveVersionRecord version,
        byte[] exactBytes,
        string workDir,
        CodeExecutionRequest request,
        Action<string>? onOutputChunk,
        CancellationToken ct)
    {
        var compiler = isCpp
            ? _toolchains.DiscoverCppCompiler(_config)
            : _toolchains.DiscoverCCompiler(_config);

        if (!compiler.IsAvailable || string.IsNullOrWhiteSpace(compiler.ExecutablePath))
        {
            return new CodeExecutionResult(
                ExecutionStatus.CompileError,
                string.Empty,
                compiler.StatusDetail,
                string.Empty,
                string.Empty,
                null,
                0,
                null,
                "Failed | Compiler missing");
        }

        // Write temp copy only (never touch archived file); strip UTF-8 BOM on temp copy for GCC 4.x compatibility
        var writeBytes = exactBytes.Length >= 3 && exactBytes[0] == 0xEF && exactBytes[1] == 0xBB && exactBytes[2] == 0xBF
            ? exactBytes[3..]
            : exactBytes;

        var ext = isCpp ? ".cpp" : ".c";
        var tempSource = Path.Combine(workDir, "main" + ext);
        var tempExe = Path.Combine(workDir, "main.exe");
        await File.WriteAllBytesAsync(tempSource, writeBytes, ct);

        var flags = compiler.DefaultFlags.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var compileArgs = new List<string>(flags) { tempSource, "-o", tempExe };
        var cmdDisplay = $"\"{compiler.ExecutablePath}\" {string.Join(' ', flags)} \"{Path.GetFileName(tempSource)}\" -o main.exe";

        var compileRun = await RunBoundedProcessAsync(
            compiler.ExecutablePath,
            compileArgs,
            workDir,
            stdinText: string.Empty,
            timeoutMs: 25_000,
            memoryLimitMb: 0, // Do not restrict compiler linking memory tightly
            onOutputChunk: null,
            ct: ct);

        var compilerLog = string.IsNullOrWhiteSpace(compileRun.Stderr)
            ? compileRun.Stdout
            : string.IsNullOrWhiteSpace(compileRun.Stdout)
                ? compileRun.Stderr
                : compileRun.Stdout + "\n" + compileRun.Stderr;

        if (compileRun.Cancelled)
        {
            return new CodeExecutionResult(
                ExecutionStatus.Cancelled, cmdDisplay, compilerLog, string.Empty, string.Empty,
                null, compileRun.ElapsedMs, null, $"Cancelled | {compileRun.ElapsedMs} ms");
        }

        if (compileRun.ExitCode != 0 || !File.Exists(tempExe))
        {
            return new CodeExecutionResult(
                ExecutionStatus.CompileError,
                cmdDisplay,
                string.IsNullOrWhiteSpace(compilerLog) ? $"Compiler exited with code {compileRun.ExitCode}" : compilerLog,
                string.Empty,
                string.Empty,
                compileRun.ExitCode,
                compileRun.ElapsedMs,
                null,
                $"Compile Error | Exit {compileRun.ExitCode}");
        }

        var execRun = await RunBoundedProcessAsync(
            tempExe,
            Array.Empty<string>(),
            workDir,
            request.StdinText,
            request.TimeoutMs,
            request.MemoryLimitMb,
            onOutputChunk,
            ct);

        return MapExecutionResult(cmdDisplay, compilerLog, execRun);
    }

    private async Task<CodeExecutionResult> RunPythonAsync(
        ArchiveVersionRecord version,
        byte[] exactBytes,
        string workDir,
        CodeExecutionRequest request,
        Action<string>? onOutputChunk,
        CancellationToken ct)
    {
        var python = _toolchains.DiscoverPython(_config);
        if (!python.IsAvailable || string.IsNullOrWhiteSpace(python.ExecutablePath))
        {
            return new CodeExecutionResult(
                ExecutionStatus.CompileError,
                string.Empty,
                python.StatusDetail,
                string.Empty,
                string.Empty,
                null,
                0,
                null,
                "Failed | Python missing");
        }

        var tempPy = Path.Combine(workDir, "solution.py");
        await File.WriteAllBytesAsync(tempPy, exactBytes, ct);

        var args = new[] { "-u", tempPy };
        var cmdDisplay = $"\"{python.ExecutablePath}\" -u solution.py";

        var execRun = await RunBoundedProcessAsync(
            python.ExecutablePath,
            args,
            workDir,
            request.StdinText,
            request.TimeoutMs,
            request.MemoryLimitMb,
            onOutputChunk,
            ct);

        return MapExecutionResult(cmdDisplay, string.Empty, execRun);
    }

    private static CodeExecutionResult MapExecutionResult(
        string cmdDisplay,
        string compilerOutput,
        (ExecutionStatus Outcome, string Stdout, string Stderr, int? ExitCode, long ElapsedMs, double? PeakMemMb, bool Cancelled) run)
    {
        var memText = run.PeakMemMb is double m ? $"{m:F1} MB" : "N/A";
        var badge = run.Outcome switch
        {
            ExecutionStatus.Finished => $"Finished | {run.ElapsedMs} ms | {memText} | Exit {run.ExitCode ?? 0}",
            ExecutionStatus.TimeLimitExceeded => $"Time Limit Exceeded | >={run.ElapsedMs} ms | {memText}",
            ExecutionStatus.MemoryLimitExceeded => $"Memory Limit Exceeded | {run.ElapsedMs} ms | {memText}",
            ExecutionStatus.Cancelled => $"Stopped | {run.ElapsedMs} ms",
            _ => $"Failed | {run.ElapsedMs} ms | {memText} | Exit {run.ExitCode ?? -1}"
        };

        return new CodeExecutionResult(
            run.Outcome,
            cmdDisplay,
            compilerOutput,
            run.Stdout,
            run.Stderr,
            run.ExitCode,
            run.ElapsedMs,
            run.PeakMemMb,
            badge);
    }

    private static async Task<(ExecutionStatus Outcome, string Stdout, string Stderr, int? ExitCode, long ElapsedMs, double? PeakMemMb, bool Cancelled)> RunBoundedProcessAsync(
        string executable,
        IReadOnlyList<string> args,
        string workingDirectory,
        string stdinText,
        int timeoutMs,
        int memoryLimitMb,
        Action<string>? onOutputChunk,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        IntPtr hJob = OperatingSystem.IsWindows() ? CreateConfiguredJobObject(memoryLimitMb) : IntPtr.Zero;
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start '{executable}'.");

        if (hJob != IntPtr.Zero)
        {
            try { AssignProcessToJobObject(hJob, proc.Handle); } catch { }
        }

        var sw = Stopwatch.StartNew();
        long peakBytes = 0;

        try
        {
            var stdoutTask = ReadStreamBoundedAsync(proc.StandardOutput, onOutputChunk, CancellationToken.None);
            var stderrTask = ReadStreamBoundedAsync(proc.StandardError, null, CancellationToken.None);

            // Feed exact stdin with explicit EOF so programs reading until EOF finish cleanly
            try
            {
                if (!string.IsNullOrEmpty(stdinText))
                    await proc.StandardInput.WriteAsync(stdinText.AsMemory(), ct);
                proc.StandardInput.Close();
            }
            catch
            {
                // Child may have exited before reading all stdin
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(Math.Max(100, timeoutMs));

            bool timedOut = false;
            bool cancelled = false;
            try
            {
                while (!proc.HasExited)
                {
                    try
                    {
                        proc.Refresh();
                        if (proc.PeakWorkingSet64 > peakBytes)
                            peakBytes = proc.PeakWorkingSet64;
                    }
                    catch { }

                    await Task.WhenAny(proc.WaitForExitAsync(timeoutCts.Token), Task.Delay(25, timeoutCts.Token));
                    if (timeoutCts.IsCancellationRequested)
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) cancelled = true;
                else timedOut = true;
            }

            if (timeoutCts.IsCancellationRequested && !proc.HasExited)
            {
                if (ct.IsCancellationRequested) cancelled = true;
                else timedOut = true;
            }

            if ((timedOut || cancelled) && !proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                try { await proc.WaitForExitAsync(CancellationToken.None); } catch { }
            }

            sw.Stop();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            int? exitCode = null;
            try { exitCode = proc.ExitCode; } catch { }

            double? peakMb = peakBytes > 0 ? Math.Round(peakBytes / (1024.0 * 1024.0), 1) : null;

            ExecutionStatus status;
            if (cancelled)
                status = ExecutionStatus.Cancelled;
            else if (timedOut)
                status = ExecutionStatus.TimeLimitExceeded;
            else if (exitCode == 0)
                status = ExecutionStatus.Finished;
            else if (memoryLimitMb > 0 && peakBytes >= (long)memoryLimitMb * 1024 * 1024 * 9 / 10)
                status = ExecutionStatus.MemoryLimitExceeded;
            else
                status = ExecutionStatus.RuntimeError;

            return (status, stdout, stderr, exitCode, Math.Max(1, sw.ElapsedMilliseconds), peakMb, cancelled);
        }
        finally
        {
            if (hJob != IntPtr.Zero)
                CloseHandle(hJob);
        }
    }

    private static async Task<string> ReadStreamBoundedAsync(
        StreamReader reader,
        Action<string>? onChunk,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new char[4096];
        bool truncated = false;
        while (true)
        {
            int n = await reader.ReadAsync(buf.AsMemory(0, buf.Length), ct);
            if (n == 0)
                break;

            if (sb.Length < MaxCaptureBytes)
            {
                int take = Math.Min(n, MaxCaptureBytes - sb.Length);
                var slice = new string(buf, 0, take);
                sb.Append(slice);
                onChunk?.Invoke(slice);
                if (take < n) truncated = true;
            }
            else
            {
                truncated = true;
            }
        }
        if (truncated)
            sb.Append("\n...[output truncated at 256 KB cap]...");
        return sb.ToString();
    }

    private static IntPtr CreateConfiguredJobObject(int memoryLimitMb)
    {
        try
        {
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
                return IntPtr.Zero;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if (memoryLimitMb > 0)
            {
                info.BasicLimitInformation.LimitFlags |= JOB_OBJECT_LIMIT_PROCESS_MEMORY;
                info.ProcessMemoryLimit = (UIntPtr)((ulong)memoryLimitMb * 1024UL * 1024UL);
            }

            int sz = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr ptr = Marshal.AllocHGlobal(sz);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ptr, (uint)sz);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
            return job;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
