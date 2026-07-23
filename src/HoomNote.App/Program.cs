using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace HoomNote_App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // This must be the first application code executed. Update/install hooks may
        // complete and exit here without initializing WinUI or opening a window.
        VelopackApp.Build().Run();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(initialization =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
