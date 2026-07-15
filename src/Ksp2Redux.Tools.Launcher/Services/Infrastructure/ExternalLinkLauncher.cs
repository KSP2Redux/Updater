using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using MsBox.Avalonia.Enums;
using Ksp2Redux.Tools.Launcher.Services.Install;

namespace Ksp2Redux.Tools.Launcher.Services.Infrastructure;

/// <summary>
/// Shared behavior for opening a link in the default browser from a view model - validates the URL,
/// awaits the launch, and reports a failure instead of letting a malformed feed link or a missing
/// default browser throw from a click handler with nothing shown to the user.
/// </summary>
internal static class ExternalLinkLauncher
{
    public static async Task LaunchAsync(TopLevel? topLevel, string? url, IMessageBoxService messageBoxService, ILogService log)
    {
        if (topLevel is null) return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            log.Warn($"Ignored invalid link: {url}");
            await messageBoxService.ShowMessageBoxAsOwnedAsync("Couldn't Open Link",
                "That link doesn't look valid.", windowStartupLocation: WindowStartupLocation.CenterOwner);
            return;
        }

        try
        {
            await topLevel.Launcher.LaunchUriAsync(uri);
        }
        catch (Exception ex)
        {
            log.Error($"Failed to open link: {url}", ex);
            await messageBoxService.ShowMessageBoxAsOwnedAsync("Couldn't Open Link",
                $"Couldn't open the link: {ex.Message}", windowStartupLocation: WindowStartupLocation.CenterOwner);
        }
    }
}
