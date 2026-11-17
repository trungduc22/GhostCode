using GhostCode.Core.Browser;
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
            return NativeMessagingHost.RunStdioHostAsync(args).GetAwaiter().GetResult();
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
