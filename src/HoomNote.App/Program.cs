using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using HoomNote_App.Services;
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
        var initialPackagePath = HoomNoteFileActivation.FindPackagePath(args);
        try
        {
            initialPackagePath ??= HoomNoteFileActivation.FindPackagePath(
                AppInstance.GetCurrent().GetActivatedEventArgs());
        }
        catch
        {
            // Plain command-line activation still works when App Lifecycle activation data is
            // unavailable (for example, a portable executable selected through Open with).
        }
        Application.Start(initialization =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(initialPackagePath);
        });
        return 0;
    }
}
