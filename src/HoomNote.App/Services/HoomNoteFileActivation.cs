using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Windows.Storage;

namespace HoomNote_App.Services;

internal static class HoomNoteFileActivation
{
    private const string PackageExtension = ".hoomnote";

    internal static string? FindPackagePath(IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
        {
            var path = NormalizePackagePath(argument);
            if (path is not null) return path;
        }

        return null;
    }

    internal static string? FindPackagePath(AppActivationArguments? activation)
    {
        if (activation?.Data is IFileActivatedEventArgs fileActivation)
        {
            foreach (var item in fileActivation.Files)
            {
                if (item is StorageFile file && NormalizePackagePath(file.Path) is { } path)
                    return path;
            }
        }

        if (activation?.Data is ILaunchActivatedEventArgs launchActivation)
            return FindPackagePath([launchActivation.Arguments]);

        return null;
    }

    internal static string? NormalizePackagePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim().Trim('"');
        if (!Path.GetExtension(candidate).Equals(PackageExtension, StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            return Path.GetFullPath(candidate);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
