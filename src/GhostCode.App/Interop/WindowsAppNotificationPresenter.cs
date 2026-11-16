using GhostCode.Core.Notifications;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace GhostCode.App.Interop;

/// <summary>
/// Presents native Windows App SDK toast notifications via AppNotificationManager.
/// Gracefully degrades on restricted test sessions without crashing background operations.
/// </summary>
public sealed class WindowsAppNotificationPresenter : INativeNotificationPresenter, IDisposable
{
    private bool _registered;

    public WindowsAppNotificationPresenter()
    {
        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch
        {
            _registered = false;
        }
    }

    public void Present(string category, string severity, string title, string message, string actionHint)
    {
        if (!_registered)
            return;

        try
        {
            var builder = new AppNotificationBuilder()
                .AddText(title)
                .AddText(message);

            if (string.Equals(actionHint, "retry_sync", StringComparison.OrdinalIgnoreCase))
            {
                builder.AddButton(new AppNotificationButton("Retry Sync").AddArgument("action", "retry_sync"));
                builder.AddButton(new AppNotificationButton("Open GhostCode").AddArgument("action", "open"));
            }
            else
            {
                builder.AddButton(new AppNotificationButton("Open GhostCode").AddArgument("action", "open"));
            }

            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch
        {
            // Native notification service unavailable on isolated session
        }
    }

    public void Dispose()
    {
        if (!_registered)
            return;
        _registered = false;
        try { AppNotificationManager.Default.Unregister(); } catch { }
    }
}
