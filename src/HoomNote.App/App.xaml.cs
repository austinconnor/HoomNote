using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using HoomNote_App.Services;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace HoomNote_App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    public static Window? MainAppWindow { get; private set; }
    
    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        LocalDataMigration.MovePreviousLibrary();
        DiagnosticsLog.Initialize();
        WindowsShellBranding.RefreshInstalledAppIcon();
        DiagnosticsLog.Info("app.constructing");
        UnhandledException += OnApplicationUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        try
        {
            RequestedTheme = ApplicationTheme.Dark;
            InitializeComponent();
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Critical("app.initialize_failed", exception);
            throw;
        }
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            DiagnosticsLog.Info("app.launching", ("arguments_length", args.Arguments?.Length ?? 0));
            MainAppWindow = new MainWindow();
            MainAppWindow.Activate();
            DiagnosticsLog.Info("app.launched");
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Critical("app.launch_failed", exception);
            throw;
        }
    }

    private static void OnApplicationUnhandledException(object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs args) =>
        DiagnosticsLog.Critical("app.xaml_unhandled_exception", args.Exception);

    private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
            DiagnosticsLog.Critical("app.domain_unhandled_exception", exception,
                ("terminating", args.IsTerminating));
        else
            DiagnosticsLog.Warning("app.domain_unhandled_non_exception", ("terminating", args.IsTerminating));
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args) =>
        DiagnosticsLog.Error("app.unobserved_task_exception", args.Exception);

    private static void OnProcessExit(object? sender, EventArgs args) =>
        DiagnosticsLog.Shutdown();
}
