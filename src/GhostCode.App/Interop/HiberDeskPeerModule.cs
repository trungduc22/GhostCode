using GhostCode.Core.Notifications;
using GhostCode.Core.Storage;
using NativeShellKit.Abstractions.DependencyInjection;
using NativeShellKit.Abstractions.Modules;

namespace GhostCode.App.Interop;

internal sealed class HiberDeskPeerModule(
    string? isolatedRoot, Func<IServiceProvider, CancellationToken, Task<bool>> openArchive) : IShellModule
{
    private HiberDeskPeer? _peer;
    public ModuleDescriptor Descriptor { get; } = new("hiberdesk-peer", "HiberDesk archive access", "1");
    public int StartupOrder => 110;
    public void RegisterServices(IServiceRegistry services) => services.AddSingleton<HiberDeskPeer>(sp =>
        new(isolatedRoot, sp.GetRequiredService<SqliteArchiveStore>(), sp.GetRequiredService<NotificationCoordinator>(),
            ct => openArchive(sp, ct)));
    public IReadOnlyList<ModuleNavigationItem> GetNavigationItems() => [];
    public IReadOnlyList<ModuleCommandDescriptor> GetCommands() => [];

    public Task InitializeAsync(IModuleContext context, CancellationToken ct = default)
    {
        _peer = context.Services.GetRequiredService<HiberDeskPeer>();
        // App startup opens the archive before exposing the authorized query endpoint.
        return Task.CompletedTask;
    }

    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        if (_peer is not null)
            await _peer.DisposeAsync().ConfigureAwait(false);
    }
}
