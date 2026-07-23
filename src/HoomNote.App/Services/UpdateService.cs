using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Velopack;
using Velopack.Sources;

namespace HoomNote_App.Services;

/// <summary>
/// Handles installed-build update checks without ever blocking application startup.
/// The feed URL is injected into update-feed.json by build-release.ps1.
/// </summary>
public static class UpdateService
{
    private const string ConfigurationFileName = "update-feed.json";
    private static int _checkInProgress;

    public static async Task CheckForUpdatesAsync(XamlRoot xamlRoot, bool manual,
        Func<Task>? prepareForRestart = null)
    {
        if (Interlocked.CompareExchange(ref _checkInProgress, 1, 0) != 0) return;
        try
        {
            var configuration = ResolveFeedConfiguration();
            if (configuration is null)
            {
                if (manual)
                    await ShowMessageAsync(xamlRoot, "Updates are not configured",
                        "This build does not contain an update feed URL. Build it with -UpdateUrl to enable OTA updates.");
                return;
            }

            var manager = string.Equals(configuration.Value.Source, "github", StringComparison.OrdinalIgnoreCase)
                ? new UpdateManager(new GithubSource(configuration.Value.Url, null, false))
                : new UpdateManager(configuration.Value.Url);
            if (!manager.IsInstalled)
            {
                if (manual)
                    await ShowMessageAsync(xamlRoot, "Installer required",
                        "OTA updates are available after HoomNote is installed with HoomNote-Setup.exe.");
                return;
            }

            DiagnosticsLog.Info("update.check_started", ("manual", manual));
            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                DiagnosticsLog.Info("update.none_available", ("manual", manual));
                if (manual)
                    await ShowMessageAsync(xamlRoot, "HoomNote is up to date",
                        $"You are running the latest version ({manager.CurrentVersion}).");
                return;
            }

            var targetVersion = update.TargetFullRelease.Version.ToString();
            DiagnosticsLog.Info("update.available", ("version", targetVersion),
                ("size_bytes", update.TargetFullRelease.Size));
            var prompt = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = $"HoomNote {targetVersion} is available",
                Content = "Download the update and restart HoomNote? Your current notes will be saved first.",
                PrimaryButtonText = "Update and restart",
                CloseButtonText = "Later",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await prompt.ShowAsync() != ContentDialogResult.Primary) return;

            var progressDialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "Downloading HoomNote update",
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new ProgressBar { IsIndeterminate = true, Width = 320 },
                        new TextBlock
                        {
                            Text = $"Preparing version {targetVersion}. HoomNote will restart automatically.",
                            TextWrapping = TextWrapping.Wrap
                        }
                    }
                }
            };
            var progressOperation = progressDialog.ShowAsync();
            try
            {
                await manager.DownloadUpdatesAsync(update);
            }
            finally
            {
                progressDialog.Hide();
            }
            await progressOperation;

            if (prepareForRestart is not null) await prepareForRestart();
            DiagnosticsLog.Info("update.applying", ("version", targetVersion));
            manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("update.failed", exception, ("manual", manual));
            if (manual)
                await ShowMessageAsync(xamlRoot, "Update check failed",
                    "HoomNote could not contact the update service. Your notes and current installation were not changed.");
        }
        finally
        {
            Volatile.Write(ref _checkInProgress, 0);
        }
    }

    private static (string Url, string Source)? ResolveFeedConfiguration()
    {
        var environmentUrl = Environment.GetEnvironmentVariable("HOOMNOTE_UPDATE_URL");
        if (!string.IsNullOrWhiteSpace(environmentUrl))
            return (environmentUrl.Trim(), InferSource(environmentUrl));

        var configurationPath = Path.Combine(AppContext.BaseDirectory, ConfigurationFileName);
        if (!File.Exists(configurationPath)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configurationPath));
            if (!document.RootElement.TryGetProperty("url", out var value) ||
                string.IsNullOrWhiteSpace(value.GetString())) return null;
            var url = value.GetString()!.Trim();
            var source = document.RootElement.TryGetProperty("source", out var sourceValue)
                ? sourceValue.GetString()
                : null;
            return (url, string.IsNullOrWhiteSpace(source) ? InferSource(url) : source.Trim());
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("update.configuration_invalid", exception);
            return null;
        }
    }

    private static string InferSource(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            ? "github"
            : "http";

    private static async Task ShowMessageAsync(XamlRoot xamlRoot, string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }
}
