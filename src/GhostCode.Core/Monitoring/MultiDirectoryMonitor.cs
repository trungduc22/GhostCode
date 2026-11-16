using System.Collections.Concurrent;
using System.Text;
using GhostCode.Core.Analysis;
using GhostCode.Core.Archiver;
using GhostCode.Core.Models;
using GhostCode.Core.Storage;

namespace GhostCode.Core.Monitoring;

public sealed record ActiveWatchInfo(
    string FolderPath,
    bool Recursive,
    bool IsWatching,
    string? Error);

public sealed record MonitoringDiagnosticResult(
    bool Success,
    string ProbeFilePath,
    string? ArchivedVersionId,
    string Summary);

/// <summary>
/// Multi-directory FileSystemWatcher engine with atomic-save Rename support,
/// event debouncing, locked-file retries, exclusion guards, and bounded periodic reconciliation.
/// </summary>
public sealed class MultiDirectoryMonitor : IAsyncDisposable
{
    private readonly GhostCodeConfig _config;
    private readonly SqliteArchiveStore _store;
    private readonly LosslessArchiver _archiver;
    private readonly Func<ProblemContext?> _contextProvider;

    private readonly object _watcherLock = new();
    private readonly List<(WatchedFolderConfig Config, FileSystemWatcher? Watcher, string? Error)> _watchers = new();
    private readonly ConcurrentDictionary<string, (long DueMs, string EventType, string FolderPath)> _pending =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private Task? _workerTask;
    private Task? _reconcileTask;

    public MultiDirectoryMonitor(
        GhostCodeConfig config,
        SqliteArchiveStore store,
        LosslessArchiver archiver,
        Func<ProblemContext?> contextProvider)
    {
        _config = config;
        _store = store;
        _archiver = archiver;
        _contextProvider = contextProvider;
    }

    public event Action<MonitorEventRecord>? EventRecorded;

    public IReadOnlyList<ActiveWatchInfo> GetActiveWatches()
    {
        lock (_watcherLock)
        {
            return _watchers
                .Select(w => new ActiveWatchInfo(w.Config.Path, w.Config.Recursive, w.Watcher is not null, w.Error))
                .ToList();
        }
    }

    public void Start()
    {
        if (_cts is not null)
            return;

        _cts = new CancellationTokenSource();
        ReloadWatches();
        _workerTask = Task.Run(() => DebounceWorkerAsync(_cts.Token));
        _reconcileTask = Task.Run(() => ReconcileLoopAsync(_cts.Token));
    }

    public void ReloadWatches()
    {
        lock (_watcherLock)
        {
            ClearWatchersLocked();

            foreach (var folder in _config.WatchedFolders)
            {
                if (!folder.Enabled)
                    continue;
                if (KnownFoldersResolver.IsDriveRoot(folder.Path))
                {
                    _watchers.Add((folder, null, "Skipped drive root for safety"));
                    continue;
                }
                if (!Directory.Exists(folder.Path))
                {
                    _watchers.Add((folder, null, "Directory not found"));
                    continue;
                }

                try
                {
                    var fullRoot = Path.GetFullPath(folder.Path);
                    var fsw = new FileSystemWatcher(fullRoot)
                    {
                        IncludeSubdirectories = folder.Recursive,
                        InternalBufferSize = 32 * 1024,
                        NotifyFilter = NotifyFilters.FileName
                                     | NotifyFilters.LastWrite
                                     | NotifyFilters.CreationTime
                                     | NotifyFilters.Size
                    };

                    fsw.Created += (_, e) => OnRawFsEvent(fullRoot, e.FullPath, "Created");
                    fsw.Changed += (_, e) => OnRawFsEvent(fullRoot, e.FullPath, "Changed");
                    fsw.Renamed += (_, e) => OnRawFsEvent(fullRoot, e.FullPath, "Renamed");
                    fsw.Error += (_, e) =>
                    {
                        var msg = e.GetException()?.Message ?? "Watcher buffer overflow";
                        _ = RecordEventSafeAsync(fullRoot, fullRoot, "WatcherError", msg);
                    };

                    fsw.EnableRaisingEvents = true;
                    _watchers.Add((folder, fsw, null));
                }
                catch (Exception ex)
                {
                    _watchers.Add((folder, null, ex.Message));
                }
            }
        }
    }

    private void ClearWatchersLocked()
    {
        foreach (var (_, w, _) in _watchers)
        {
            if (w is not null)
            {
                try { w.EnableRaisingEvents = false; } catch { }
                w.Dispose();
            }
        }
        _watchers.Clear();
    }

    private string[] ExcludedDirectories() =>
        new[] { _config.ArchiveDir, _config.ReadyDir };

    private void OnRawFsEvent(string watchedRoot, string fullPath, string eventType)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return;

        // If Dev-C++ compiled an .exe, queue its matching .c/.cpp source file
        if (Path.GetExtension(fullPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetDirectoryName(fullPath);
            var stem = Path.GetFileNameWithoutExtension(fullPath);
            if (!string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(stem))
            {
                foreach (var ext in new[] { ".c", ".cpp", ".cc", ".cxx" })
                {
                    var srcCandidate = Path.Combine(dir, stem + ext);
                    if (File.Exists(srcCandidate) && SourceAnalyzer.IsEligibleSourceFile(srcCandidate, ExcludedDirectories()))
                    {
                        EnqueueDebounced(watchedRoot, srcCandidate, "Compiled");
                        break;
                    }
                }
            }
            return;
        }

        if (!SourceAnalyzer.IsEligibleSourceFile(fullPath, ExcludedDirectories()))
            return;

        EnqueueDebounced(watchedRoot, fullPath, eventType);
    }

    private void EnqueueDebounced(string watchedRoot, string filePath, string eventType)
    {
        var full = Path.GetFullPath(filePath);
        var dueMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 250;
        _pending.AddOrUpdate(full, (dueMs, eventType, watchedRoot), (_, _) => (dueMs, eventType, watchedRoot));
    }

    private async Task DebounceWorkerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(120, ct);
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                foreach (var kv in _pending.ToArray())
                {
                    if (kv.Value.DueMs <= now && _pending.TryRemove(kv.Key, out var item))
                    {
                        await ProcessDetectedFileAsync(item.FolderPath, kv.Key, item.EventType, ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Continue monitoring even on transient handler failure
            }
        }
    }

    private async Task ProcessDetectedFileAsync(string watchedRoot, string filePath, string eventType, CancellationToken ct)
    {
        if (!File.Exists(filePath))
            return;

        var bytes = await ReadFileWithRetryAsync(filePath, ct);
        if (bytes is null)
        {
            await RecordEventSafeAsync(watchedRoot, filePath, "ReadFailed", "File remained locked after retries");
            return;
        }

        var ctx = _contextProvider();
        var res = await _archiver.ProcessFileSaveAsync(filePath, bytes, ctx, ct);
        await RecordEventSafeAsync(watchedRoot, filePath, eventType, res.Message);
    }

    private static async Task<byte[]?> ReadFileWithRetryAsync(string path, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, useAsync: true);
                var ms = new MemoryStream();
                await fs.CopyToAsync(ms, ct);
                return ms.ToArray();
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(60 * (attempt + 1), ct);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                await Task.Delay(60 * (attempt + 1), ct);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    private async Task ReconcileLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                await ReconcileOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch { }
        }
    }

    /// <summary>
    /// Bounded reconciliation pass (max depth 3, max 500 eligible files per folder)
    /// to pick up files saved while watcher events were dropped or before startup.
    /// </summary>
    public async Task<int> ReconcileOnceAsync(CancellationToken ct = default)
    {
        const int maxDepth = 3;
        const int maxFilesPerFolder = 500;
        int archivedCount = 0;
        var excluded = ExcludedDirectories();

        foreach (var folder in _config.WatchedFolders.ToArray())
        {
            if (!folder.Enabled || KnownFoldersResolver.IsDriveRoot(folder.Path) || !Directory.Exists(folder.Path))
                continue;

            int seenInFolder = 0;
            var stack = new Stack<(string Dir, int Depth)>();
            stack.Push((Path.GetFullPath(folder.Path), 0));

            while (stack.Count > 0 && seenInFolder < maxFilesPerFolder && !ct.IsCancellationRequested)
            {
                var (dir, depth) = stack.Pop();
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(dir);
                }
                catch
                {
                    continue;
                }

                foreach (var file in files)
                {
                    if (seenInFolder >= maxFilesPerFolder || ct.IsCancellationRequested)
                        break;
                    if (!SourceAnalyzer.IsEligibleSourceFile(file, excluded))
                        continue;

                    seenInFolder++;
                    var bytes = await ReadFileWithRetryAsync(file, ct);
                    if (bytes is null)
                        continue;

                    var sha = SourceAnalyzer.ComputeSha256Hex(bytes);
                    var existing = await _store.GetSnapshotAsync(Path.GetFullPath(file), ct);
                    if (existing is not null && existing.Sha256 == sha)
                        continue;

                    var res = await _archiver.ProcessFileSaveAsync(file, bytes, _contextProvider(), ct);
                    if (res.Outcome == ArchiveOutcome.CreatedVersion)
                    {
                        archivedCount++;
                        await RecordEventSafeAsync(folder.Path, file, "Reconciled", res.Message);
                    }
                }

                if (folder.Recursive && depth < maxDepth)
                {
                    try
                    {
                        foreach (var sub in Directory.EnumerateDirectories(dir))
                        {
                            var info = new DirectoryInfo(sub);
                            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                                continue;
                            var subName = info.Name;
                            if (subName.StartsWith('.') || subName.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                                || subName.Equals("bin", StringComparison.OrdinalIgnoreCase)
                                || subName.Equals("obj", StringComparison.OrdinalIgnoreCase)
                                || subName.Equals("target", StringComparison.OrdinalIgnoreCase))
                                continue;

                            var fullSub = Path.GetFullPath(sub).TrimEnd(Path.DirectorySeparatorChar);
                            bool isExcluded = excluded.Any(ex => !string.IsNullOrWhiteSpace(ex) &&
                                fullSub.Equals(Path.GetFullPath(ex).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
                            if (!isExcluded)
                                stack.Push((sub, depth + 1));
                        }
                    }
                    catch { }
                }
            }
        }

        return archivedCount;
    }

    public async Task<MonitoringDiagnosticResult> RunDiagnosticTestAsync(string? targetFolder = null, CancellationToken ct = default)
    {
        var folder = targetFolder ?? _config.WatchedFolders.FirstOrDefault(w => w.Enabled && Directory.Exists(w.Path))?.Path;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return new MonitoringDiagnosticResult(false, string.Empty, null, "No enabled watched folder exists.");

        bool startedHere = false;
        if (_cts is null)
        {
            Start();
            startedHere = true;
        }

        var probePath = Path.Combine(folder, $"GhostCode_DiagProbe_{Guid.NewGuid():N}.cpp");
        var tcs = new TaskCompletionSource<ArchiveVersionRecord>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(ArchiveVersionRecord ver)
        {
            if (string.Equals(Path.GetFullPath(ver.SourcePath), Path.GetFullPath(probePath), StringComparison.OrdinalIgnoreCase))
                tcs.TrySetResult(ver);
        }

        _archiver.VersionArchived += Handler;
        try
        {
            var probeCode = $"#include <stdio.h>\nint main(){{ printf(\"diag_{Guid.NewGuid():N}\"); return 0; }}\n";
            await File.WriteAllTextAsync(probePath, probeCode, Encoding.UTF8, ct);

            var winner = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5), ct));
            if (winner == tcs.Task)
            {
                var ver = await tcs.Task;
                return new MonitoringDiagnosticResult(
                    true,
                    probePath,
                    ver.Id,
                    $"Verified watcher detected and archived probe inside '{folder}' -> {ver.FileName}");
            }

            return new MonitoringDiagnosticResult(
                false,
                probePath,
                null,
                $"Timed out waiting for FileSystemWatcher event in '{folder}'.");
        }
        finally
        {
            _archiver.VersionArchived -= Handler;
            try { if (File.Exists(probePath)) File.Delete(probePath); } catch { }
            if (startedHere)
                await StopAsync();
        }
    }

    private async Task RecordEventSafeAsync(string folder, string file, string eventType, string message)
    {
        var ev = new MonitorEventRecord(0, folder, file, eventType, message, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        EventRecorded?.Invoke(ev);
        try { await _store.RecordMonitorEventAsync(ev); } catch { }
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        if (cts is null)
            return;
        _cts = null;

        cts.Cancel();
        lock (_watcherLock)
        {
            ClearWatchersLocked();
        }

        var tasks = new[] { _workerTask, _reconcileTask }.Where(t => t is not null).Cast<Task>().ToArray();
        if (tasks.Length > 0)
        {
            try { await Task.WhenAll(tasks); } catch { }
        }
        cts.Dispose();
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
