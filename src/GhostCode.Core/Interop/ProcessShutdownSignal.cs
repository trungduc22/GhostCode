using System.Security.Cryptography;
using System.Text;

namespace GhostCode.Core.Interop;

/// <summary>
/// Path-scoped named EventWaitHandle used by installers, upgraders, uninstallers,
/// and packaging scripts to ask a specific GhostCode executable instance to flush
/// watchers, browser bridge, cloud queues, and SQLite WAL and exit cleanly without
/// affecting GhostCode processes running from other paths (such as Portable copies).
/// </summary>
public static class ProcessShutdownSignal
{
    public const string EventNamePrefix = "Local\\GhostCode_Exit_";

    public static string NormalizeExecutablePath(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            throw new ArgumentException("Executable path must not be empty.", nameof(exePath));

        return Path.GetFullPath(exePath.Trim()).ToLowerInvariant();
    }

    public static string GetEventName(string exePath)
    {
        var normalized = NormalizeExecutablePath(exePath);
        var hash = SHA256.HashData(Encoding.Unicode.GetBytes(normalized));
        var shortHex = Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
        return $"{EventNamePrefix}{shortHex}";
    }

    public static string ResolveCurrentExecutablePath() =>
        Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "GhostCode.App.exe");

    public static IDisposable RegisterListener(Action onShutdownRequested, string? exePath = null)
    {
        ArgumentNullException.ThrowIfNull(onShutdownRequested);

        var targetExe = string.IsNullOrWhiteSpace(exePath) ? ResolveCurrentExecutablePath() : exePath;
        var eventName = GetEventName(targetExe);
        var waitHandle = new EventWaitHandle(initialState: false, EventResetMode.ManualReset, eventName);
        int fired = 0;

        var registration = ThreadPool.RegisterWaitForSingleObject(
            waitHandle,
            (_, timedOut) =>
            {
                if (timedOut)
                    return;

                if (Interlocked.Exchange(ref fired, 1) == 0)
                {
                    try { onShutdownRequested(); }
                    catch { }
                }
            },
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: true);

        return new ShutdownSignalRegistration(waitHandle, registration);
    }

    public static CancellationTokenSource CreateLinkedCancellationTokenSource(
        CancellationToken outerCt,
        out IDisposable? registration,
        string? exePath = null)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        registration = RegisterListener(() =>
        {
            try { cts.Cancel(); }
            catch { }
        }, exePath);
        return cts;
    }

    public static bool TrySignalExit(string exePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(exePath))
            return false;

        try
        {
            var eventName = GetEventName(exePath);
            if (!EventWaitHandle.TryOpenExisting(eventName, out var waitHandle))
                return false;

            using (waitHandle)
            {
                return waitHandle.Set();
            }
        }
        catch
        {
            return false;
        }
    }

    private sealed class ShutdownSignalRegistration : IDisposable
    {
        private EventWaitHandle? _waitHandle;
        private RegisteredWaitHandle? _registeredWait;

        public ShutdownSignalRegistration(EventWaitHandle waitHandle, RegisteredWaitHandle registeredWait)
        {
            _waitHandle = waitHandle;
            _registeredWait = registeredWait;
        }

        public void Dispose()
        {
            var reg = Interlocked.Exchange(ref _registeredWait, null);
            var handle = Interlocked.Exchange(ref _waitHandle, null);

            if (reg is not null)
            {
                try { reg.Unregister(null); } catch { }
            }

            handle?.Dispose();
        }
    }
}
