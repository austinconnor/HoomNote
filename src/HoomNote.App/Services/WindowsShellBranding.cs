using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HoomNote_App.Services;

internal static class WindowsShellBranding
{
    private const string UninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\HoomNote";
    private const uint ShellEventAssociationChanged = 0x08000000;
    private const uint ShellNotifyIdList = 0x0000;

    internal static string GetIconPath() =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");

    internal static void RefreshInstalledAppIcon()
    {
        var iconPath = GetIconPath();
        if (!File.Exists(iconPath))
        {
            DiagnosticsLog.Warning("shell.install_icon_missing", ("path", iconPath));
            return;
        }

        try
        {
            using var uninstallKey = Registry.CurrentUser.OpenSubKey(UninstallKeyPath, writable: true);
            if (uninstallKey is null)
                return;

            var installLocation = uninstallKey.GetValue("InstallLocation") as string;
            if (string.IsNullOrWhiteSpace(installLocation))
                return;

            var installedRoot = Path.GetFullPath(installLocation)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!iconPath.StartsWith(installedRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                return;

            var currentIcon = uninstallKey.GetValue("DisplayIcon") as string;
            if (string.Equals(currentIcon, iconPath, StringComparison.OrdinalIgnoreCase))
                return;

            uninstallKey.SetValue("DisplayIcon", iconPath, RegistryValueKind.String);
            SHChangeNotify(ShellEventAssociationChanged, ShellNotifyIdList, nint.Zero, nint.Zero);
            DiagnosticsLog.Info("shell.install_icon_refreshed");
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Warning("shell.install_icon_refresh_failed",
                ("exception", exception.GetType().Name));
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, nint item1, nint item2);
}
