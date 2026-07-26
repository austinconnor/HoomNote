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
using HoomNote.Infrastructure.Storage;
using System.Runtime.InteropServices;
using Windows.Graphics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace HoomNote_App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    public static Window? MainAppWindow { get; private set; }
    private readonly HashSet<MainWindow> _windows = [];
    private readonly SemaphoreSlim _userPreferencesGate = new(1, 1);
    private UserPreferences? _sharedUserPreferences;
    
    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        LocalDataMigration.MovePreviousLibrary();
        DiagnosticsLog.Initialize();
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
            var mainWindow = new MainWindow(isPrimary: true);
            MainAppWindow = mainWindow;
            RegisterWindow(mainWindow);
            mainWindow.Activate();
            DiagnosticsLog.Info("app.launched");
            _ = Task.Run(WindowsShellBranding.RefreshInstalledAppIcon);
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

    private void RegisterWindow(MainWindow window)
    {
        _windows.Add(window);
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            DiagnosticsLog.Info("window.closed", ("remaining_windows", _windows.Count));
        };
    }

    internal static MainWindow? PrimaryWindow => MainAppWindow as MainWindow;

    internal static async Task<UserPreferences> LoadSharedUserPreferencesAsync(
        LocalUserSettingsStore store)
    {
        if (Current is not App app) return await store.LoadAsync();
        await app._userPreferencesGate.WaitAsync();
        try
        {
            app._sharedUserPreferences ??= await store.LoadAsync();
            return app._sharedUserPreferences;
        }
        finally
        {
            app._userPreferencesGate.Release();
        }
    }

    internal static async Task SaveSharedUserPreferencesAsync(
        LocalUserSettingsStore store,
        UserPreferences preferences)
    {
        if (Current is not App app)
        {
            await store.SaveAsync(preferences);
            return;
        }

        await app._userPreferencesGate.WaitAsync();
        try
        {
            // All MainPage instances receive the same folder lists and mappings. Shallow
            // record updates may differ per window, but the hierarchy remains one process-
            // wide source of truth instead of being overwritten by a stale detached window.
            app._sharedUserPreferences = preferences;
            await store.SaveAsync(preferences);
        }
        finally
        {
            app._userPreferencesGate.Release();
        }
    }

    internal static MainPage? FindPageHostingNotebook(Guid documentId, MainPage? except = null)
    {
        if (Current is not App app) return null;
        return app._windows
            .Select(window => window.MainPage)
            .FirstOrDefault(page => page is not null && !ReferenceEquals(page, except) &&
                                    page.ContainsNotebookTab(documentId));
    }

    internal static MainWindow OpenDetachedNotebookWindow(Guid documentId)
    {
        if (Current is not App app) throw new InvalidOperationException("The HoomNote application is not available.");
        var window = new MainWindow(documentId);
        app.RegisterWindow(window);
        window.Activate();
        try
        {
            if (GetCursorPos(out var pointer))
                window.AppWindow.Move(new PointInt32(pointer.X - 120, pointer.Y - 18));
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Warning("window.detach_position_failed",
                ("exception", exception.GetType().Name));
        }
        DiagnosticsLog.Info("window.notebook_detached", ("document_id", documentId));
        return window;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
