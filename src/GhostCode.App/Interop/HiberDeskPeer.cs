using System.Text.Json;
using GhostCode.Core.Notifications;
using GhostCode.Core.Storage;
using NativeShellKit.Abstractions.Identity;
using NativeShellKit.Abstractions.Notifications;
using NativeShellKit.Communication.Ipc;
using GhostNotification = GhostCode.Core.Models.NotificationRecord;

namespace GhostCode.App.Interop;

internal sealed class HiberDeskPeer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> SharedCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "backup", "integrity", "cloud", "sync_success"
    };

    private readonly LocalPeerGrant _grant;
    private readonly SqliteArchiveStore _store;
    private readonly NotificationCoordinator _source;
    private readonly Func<CancellationToken, Task<bool>> _openArchive;
    private readonly SemaphoreSlim _control = new(1);
    private readonly Queue<NotificationRecord> _recent = new();
    private NamedPipeIpcServer? _server;
    private volatile bool _enabled;

    public HiberDeskPeer(string? isolatedDataRoot, SqliteArchiveStore store, NotificationCoordinator source,
        Func<CancellationToken, Task<bool>> openArchive)
    {
        _grant = new(new AppIdentity("ghostcode-hiberdesk-v1", "GhostCode peer", "trungduc22", baseDataRoot: isolatedDataRoot));
        _store = store;
        _source = source;
        _openArchive = openArchive;
        _source.NotificationEligible += Capture;
    }

    public bool Enabled => _enabled;

    public async Task InitializeAsync()
    {
        await _control.WaitAsync().ConfigureAwait(false);
        try { await StartAsync().ConfigureAwait(false); }
        finally { _control.Release(); }
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        await _control.WaitAsync().ConfigureAwait(false);
        try
        {
            if (enabled == Enabled)
                return;
            await StopAsync().ConfigureAwait(false);
            if (enabled)
            {
                await _grant.GrantAsync().ConfigureAwait(false);
                await StartAsync().ConfigureAwait(false);
            }
            else
                await _grant.RevokeAsync().ConfigureAwait(false);
        }
        finally { _control.Release(); }
    }

    private async Task StartAsync()
    {
        if (_server is not null)
            return;
        var server = await _grant.OpenServerAsync().ConfigureAwait(false);
        if (server is null)
            return;
        server.RegisterAllowedHandler("ghostcode.status", async (request, ct) =>
        {
            var stats = await _store.GetStatsAsync(ct).ConfigureAwait(false);
            NotificationRecord[] recent;
            lock (_recent)
                recent = _recent.Where(item => item.ExpiresUtc > DateTimeOffset.UtcNow).ToArray();
            return request with { Operation = "ok", PayloadJson = JsonSerializer.Serialize(new
            {
                version = 1, stats.TotalVersions, stats.PendingUploads, stats.FailedUploads,
                stats.LastSuccessfulSyncUnixMs, notifications = recent
            }, Json) };
        });
        server.RegisterAllowedHandler("ghostcode.open.archive", async (request, ct) => request with
        {
            Operation = await _openArchive(ct).ConfigureAwait(false) ? "ok" : "unavailable",
            PayloadJson = "{}"
        });
        await server.StartAsync().ConfigureAwait(false);
        _server = server;
        _enabled = true;
    }

    private void Capture(GhostNotification source)
    {
        if (!Enabled || !SharedCategories.Contains(source.Category))
            return;
        var created = DateTimeOffset.FromUnixTimeMilliseconds(source.OccurredAtUnixMs);
        var severity = Enum.TryParse<NotificationSeverity>(source.Severity, true, out var parsed)
            ? parsed : NotificationSeverity.Info;
        var notification = new NotificationRecord(source.Id, "ghostcode", "archive", source.Category,
            severity, "GhostCode", "", null, null, created, created.AddMinutes(3), false);
        lock (_recent)
        {
            if (!Enabled)
                return;
            while (_recent.Count >= 24)
                _recent.Dequeue();
            _recent.Enqueue(notification);
        }
    }

    private async Task StopAsync()
    {
        _enabled = false;
        lock (_recent) _recent.Clear();
        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _source.NotificationEligible -= Capture;
        await _control.WaitAsync().ConfigureAwait(false);
        try { await StopAsync().ConfigureAwait(false); }
        finally { _control.Release(); }
    }
}
