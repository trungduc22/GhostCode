using System.Collections.Concurrent;
using GhostCode.Core.Models;

namespace GhostCode.Core.Monitoring;

/// <summary>
/// Strictly local, opt-in active coding duration estimator with a bounded idle cutoff (default 180 seconds).
/// Never uploaded to cloud storage manifests; all outputs are explicitly labeled "Estimated".
/// </summary>
public sealed class CodingTimeTracker
{
    public const long DefaultIdleCutoffMs = 180_000;

    private sealed class TrackState
    {
        public long LastActivityUnixMs;
        public long AccumulatedMs;
    }

    private readonly GhostCodeConfig _config;
    private readonly long _idleCutoffMs;
    private readonly ConcurrentDictionary<string, TrackState> _tracks = new(StringComparer.OrdinalIgnoreCase);

    public CodingTimeTracker(GhostCodeConfig config, long idleCutoffMs = DefaultIdleCutoffMs)
    {
        _config = config;
        _idleCutoffMs = idleCutoffMs > 0 ? idleCutoffMs : DefaultIdleCutoffMs;
    }

    public void RecordActivity(string problemOrFileKey, long? timestampUnixMs = null)
    {
        if (!_config.EstimateActiveCodingTime || string.IsNullOrWhiteSpace(problemOrFileKey))
            return;

        var now = timestampUnixMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var state = _tracks.GetOrAdd(problemOrFileKey.Trim(), _ => new TrackState { LastActivityUnixMs = now });
        lock (state)
        {
            if (state.LastActivityUnixMs == 0)
            {
                state.LastActivityUnixMs = now;
                return;
            }

            var delta = now - state.LastActivityUnixMs;
            if (delta > 0 && delta <= _idleCutoffMs)
                state.AccumulatedMs += delta;

            if (now > state.LastActivityUnixMs)
                state.LastActivityUnixMs = now;
        }
    }

    public long GetEstimatedDurationMs(string problemOrFileKey)
    {
        if (!_config.EstimateActiveCodingTime || string.IsNullOrWhiteSpace(problemOrFileKey))
            return 0;

        if (!_tracks.TryGetValue(problemOrFileKey.Trim(), out var state))
            return 0;

        lock (state)
            return state.AccumulatedMs;
    }

    public string FormatEstimatedDuration(string problemOrFileKey, UiLanguage language = UiLanguage.En)
    {
        if (!_config.EstimateActiveCodingTime)
            return language == UiLanguage.Vi
                ? "Ước lượng thời gian code đang tắt (Bật trong Cài đặt)"
                : "Coding time estimation disabled (Opt-in in Settings)";

        var prefix = language == UiLanguage.Vi ? "Ước lượng: " : "Estimated: ";
        var ms = GetEstimatedDurationMs(problemOrFileKey);
        var ts = TimeSpan.FromMilliseconds(ms);
        if (ts.TotalHours >= 1)
            return $"{prefix}{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1)
            return $"{prefix}{ts.Minutes}m {ts.Seconds}s";
        return $"{prefix}{ts.Seconds}s";
    }
}
