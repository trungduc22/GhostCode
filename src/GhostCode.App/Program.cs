using GhostCode.Core.Browser;
using GhostCode.Core.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace GhostCode.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (NativeMessagingHost.IsNativeMessagingInvocation(args))
        {
            using var shutdownCts = ProcessShutdownSignal.CreateLinkedCancellationTokenSource(
                CancellationToken.None,
                out var signalReg);
            using (signalReg)
            {
                return NativeMessagingHost.RunStdioHostAsync(args, shutdownCts.Token).GetAwaiter().GetResult();
            }
        }

        ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
