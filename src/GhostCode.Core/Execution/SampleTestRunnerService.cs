using GhostCode.Core.Analysis;
using GhostCode.Core.Models;
using GhostCode.Core.Storage;

namespace GhostCode.Core.Execution;

public sealed record SampleTestRunResult(
    string SampleId,
    int CaseIndex,
    bool IsCustom,
    bool Passed,
    string StatusLabel,
    ExecutionStatus ExecutionStatus,
    string ExpectedOutput,
    string ActualOutput,
    string CompilerOrStderr,
    string DiffSummary,
    string UnifiedDiffText,
    long ElapsedMs);

public sealed record SampleSuiteRunReport(
    string VersionId,
    string ProblemKey,
    int TotalCases,
    int PassedCases,
    string SummaryLabel,
    IReadOnlyList<SampleTestRunResult> CaseResults);

/// <summary>
/// Compiles and executes an archived version against stored problem samples or user custom test cases.
/// Explicitly distinguishes local "Sample Passed" from an official online judge "Judge Accepted (AC)" verdict.
/// </summary>
public sealed class SampleTestRunnerService
{
    private readonly GhostCodeConfig _config;
    private readonly SqliteArchiveStore _store;
    private readonly CodeExecutionService _runner;

    public SampleTestRunnerService(
        GhostCodeConfig config,
        SqliteArchiveStore store,
        CodeExecutionService runner)
    {
        _config = config;
        _store = store;
        _runner = runner;
    }

    public async Task<SampleTestRunResult> RunSingleSampleAsync(
        string versionId,
        SampleTestCaseRecord sample,
        bool? trimTrailingWhitespaceOverride = null,
        CancellationToken ct = default)
    {
        var exec = await _runner.ExecuteArchivedVersionAsync(
            new CodeExecutionRequest(
                versionId,
                sample.InputText,
                _config.ExecutionTimeoutMs,
                _config.ExecutionMemoryLimitMb),
            onOutputChunk: null,
            ct);

        if (exec.Status != ExecutionStatus.Finished)
        {
            var statusLabel = exec.Status switch
            {
                ExecutionStatus.CompileError => "Compile Error",
                ExecutionStatus.TimeLimitExceeded => "Time Limit Exceeded",
                ExecutionStatus.MemoryLimitExceeded => "Memory Limit Exceeded",
                ExecutionStatus.RuntimeError => "Runtime Error",
                ExecutionStatus.Cancelled => "Cancelled",
                _ => exec.StatusBadgeText
            };

            var errOut = string.IsNullOrWhiteSpace(exec.CompilerOutput) ? exec.Stderr : exec.CompilerOutput;
            return new SampleTestRunResult(
                sample.Id,
                sample.CaseIndex,
                sample.IsCustom,
                Passed: false,
                StatusLabel: statusLabel,
                ExecutionStatus: exec.Status,
                ExpectedOutput: sample.ExpectedOutput,
                ActualOutput: exec.Stdout,
                CompilerOrStderr: errOut,
                DiffSummary: statusLabel,
                UnifiedDiffText: errOut,
                ElapsedMs: exec.ElapsedMs);
        }

        bool trim = trimTrailingWhitespaceOverride ?? _config.TrimTrailingWhitespaceOnCompare;
        var normExpected = NormalizeForCompare(sample.ExpectedOutput, trim);
        var normActual = NormalizeForCompare(exec.Stdout, trim);

        bool matched = string.Equals(normExpected, normActual, StringComparison.Ordinal);
        var diff = SourceDiffEngine.ComputeDiff(normExpected, normActual, "Expected", "Actual");

        return new SampleTestRunResult(
            sample.Id,
            sample.CaseIndex,
            sample.IsCustom,
            Passed: matched,
            StatusLabel: matched ? "Sample Passed" : "Output Mismatch",
            ExecutionStatus: exec.Status,
            ExpectedOutput: sample.ExpectedOutput,
            ActualOutput: exec.Stdout,
            CompilerOrStderr: exec.Stderr,
            DiffSummary: matched
                ? $"Sample Passed ({exec.ElapsedMs} ms)"
                : $"Output Mismatch (+{diff.AddedLines}/-{diff.RemovedLines}/~{diff.ModifiedLines})",
            UnifiedDiffText: matched ? "(Exact output match)" : diff.UnifiedDiffText,
            ElapsedMs: exec.ElapsedMs);
    }

    public async Task<SampleSuiteRunReport> RunAllSamplesAsync(
        string versionId,
        string problemKey,
        bool? trimTrailingWhitespaceOverride = null,
        CancellationToken ct = default)
    {
        var samples = await _store.GetSampleTestsAsync(problemKey, ct);
        if (samples.Count == 0)
        {
            return new SampleSuiteRunReport(
                versionId,
                problemKey,
                0,
                0,
                "No sample tests stored for this problem.",
                Array.Empty<SampleTestRunResult>());
        }

        var results = new List<SampleTestRunResult>(samples.Count);
        int passed = 0;
        foreach (var tc in samples)
        {
            ct.ThrowIfCancellationRequested();
            var res = await RunSingleSampleAsync(versionId, tc, trimTrailingWhitespaceOverride, ct);
            if (res.Passed)
                passed++;
            results.Add(res);
        }

        var summary = passed == samples.Count
            ? $"{passed}/{samples.Count} Sample Passed (Local Only — not Judge Accepted)"
            : $"{passed}/{samples.Count} Sample Passed";

        return new SampleSuiteRunReport(versionId, problemKey, samples.Count, passed, summary, results);
    }

    public async Task<SampleTestCaseRecord> AddCustomSampleAsync(
        string problemKey,
        string inputText,
        string expectedOutput,
        string sourceUrl = "",
        CancellationToken ct = default)
    {
        var existing = await _store.GetSampleTestsAsync(problemKey, ct);
        var nextIdx = existing.Count == 0 ? 1 : existing.Max(s => s.CaseIndex) + 1;
        var rec = new SampleTestCaseRecord(
            Id: $"{problemKey}_custom_{Guid.NewGuid():N}",
            ProblemKey: problemKey,
            CaseIndex: nextIdx,
            InputText: inputText ?? string.Empty,
            ExpectedOutput: expectedOutput ?? string.Empty,
            SourceUrl: sourceUrl ?? string.Empty,
            IsCustom: true,
            CapturedAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await _store.UpsertSampleTestAsync(rec, ct);
        return rec;
    }

    public static string NormalizeForCompare(string text, bool trimTrailingWhitespace)
    {
        var unix = (text ?? string.Empty).Replace("\r\n", "\n");
        if (!trimTrailingWhitespace)
            return unix;

        var lines = unix.Split('\n').Select(l => l.TrimEnd(' ', '\t')).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return string.Join('\n', lines);
    }
}
