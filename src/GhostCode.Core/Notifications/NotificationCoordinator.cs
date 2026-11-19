using System.Collections.Concurrent;
using GhostCode.Core.Models;
using GhostCode.Core.Storage;

namespace GhostCode.Core.Notifications;

public interface INativeNotificationPresenter
{
    void Present(string category, string severity, string title, string message, string actionHint);
}

public sealed record NotificationDispatchResult(
    bool Persisted,
    bool PresentedNatively,
    string SuppressedReason);

/// <summary>
/// Deduplicates persistent failure notifications, enforces user notification level / quiet hours,
/// logs missed alerts to SQLite history, and delegates toast presentation to WinUI AppNotificationManager.
/// </summary>
public sealed class NotificationCoordinator
{
    private readonly GhostCodeConfig _config;
    private readonly SqliteArchiveStore _store;
    private readonly ConcurrentDictionary<string, long> _lastAlertSentMs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _consecutiveFailuresByKey = new(StringComparer.OrdinalIgnoreCase);
    private INativeNotificationPresenter? _presenter;

    public NotificationCoordinator(GhostCodeConfig config, SqliteArchiveStore store, INativeNotificationPresenter? presenter = null)
    {
        _config = config;
        _store = store;
        _presenter = presenter;
    }

    public TimeSpan DedupCooldown { get; set; } = TimeSpan.FromMinutes(10);
    public event Action<NotificationRecord>? NotificationEligible;

    public void AttachPresenter(INativeNotificationPresenter presenter) => _presenter = presenter;

    public async Task<NotificationDispatchResult> NotifyAsync(
        string category,
        string severity,
        string title,
        string message,
        string dedupKey,
        string actionHint = "open",
        CancellationToken ct = default)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var record = new NotificationRecord(
            Guid.NewGuid().ToString("N"),
            category,
            severity,
            title,
            message,
            dedupKey,
            nowMs);

        await _store.RecordNotificationAsync(record, ct);

        if (_config.NotificationLevel == NotificationLevel.Disabled)
            return new NotificationDispatchResult(true, false, "DisabledByUser");

        if (_config.NotificationLevel == NotificationLevel.WarningsOnly
            && !string.Equals(severity, "Warning", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(severity, "Error", StringComparison.OrdinalIgnoreCase))
        {
            return new NotificationDispatchResult(true, false, "WarningsOnlyFilter");
        }

        if (!IsCategoryEnabled(category))
            return new NotificationDispatchResult(true, false, "CategoryDisabled");

        if (IsInQuietHours(DateTime.Now.Hour))
            return new NotificationDispatchResult(true, false, "QuietHoursSuppressed");

        var fullDedupKey = $"{category}:{dedupKey}:{severity}";
        if (_lastAlertSentMs.TryGetValue(fullDedupKey, out var prevMs)
            && nowMs - prevMs < (long)DedupCooldown.TotalMilliseconds)
        {
            return new NotificationDispatchResult(true, false, "DeduplicatedCooldown");
        }

        _lastAlertSentMs[fullDedupKey] = nowMs;
        NotificationEligible?.Invoke(record);
        try
        {
            _presenter?.Present(category, severity, title, message, actionHint);
        }
        catch
        {
            return new NotificationDispatchResult(true, false, "PresenterUnavailable");
        }

        return new NotificationDispatchResult(true, _presenter is not null, string.Empty);
    }

    public async Task<NotificationDispatchResult> NotifyRepeatedFailureOrRecoveryAsync(
        string category,
        string failureKey,
        bool failed,
        string failureTitle,
        string failureMessage,
        string? recoveryTitle = null,
        string? recoveryMessage = null,
        int thresholdBeforeAlert = 2,
        CancellationToken ct = default)
    {
        if (failed)
        {
            var count = _consecutiveFailuresByKey.AddOrUpdate(failureKey, 1, (_, cur) => cur + 1);
            if (count < thresholdBeforeAlert)
                return new NotificationDispatchResult(false, false, $"BelowThreshold({count}/{thresholdBeforeAlert})");

            return await NotifyAsync(category, "Warning", failureTitle, failureMessage, failureKey, "retry_sync", ct);
        }

        if (_consecutiveFailuresByKey.TryRemove(failureKey, out var prevCount) && prevCount >= thresholdBeforeAlert)
        {
            _lastAlertSentMs.TryRemove($"{category}:{failureKey}:Warning", out _);
            if (!string.IsNullOrWhiteSpace(recoveryTitle) && !string.IsNullOrWhiteSpace(recoveryMessage))
                return await NotifyAsync(category, "Success", recoveryTitle, recoveryMessage, $"{failureKey}_recovered", "open", ct);
        }

        return new NotificationDispatchResult(false, false, "NoStateChange");
    }

    private bool IsCategoryEnabled(string category) => category.ToLowerInvariant() switch
    {
        "cloud" => _config.AlertCloudConnection,
        "backup" => _config.AlertBackupFailures,
        "integrity" => _config.AlertBackupFailures,
        "verdict" => _config.AlertSubmissionVerdicts,
        "sync_success" => _config.AlertSuccessfulSync,
        _ => true
    };

    private bool IsInQuietHours(int currentHour)
    {
        if (!_config.QuietHoursEnabled)
            return false;
        int start = Math.Clamp(_config.QuietHoursStartHour, 0, 23);
        int end = Math.Clamp(_config.QuietHoursEndHour, 0, 23);
        if (start == end)
            return false;
        return start < end
            ? currentHour >= start && currentHour < end
            : currentHour >= start || currentHour < end;
    }
}
