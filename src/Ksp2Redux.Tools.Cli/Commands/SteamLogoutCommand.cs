using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Ksp2Redux.Tools.Launcher.Services.Steam;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Signs out of Steam and forgets the saved login, for the launcher window as well.
/// </summary>
public sealed class SteamLogoutCommand : ReduxCommand<SteamLogoutSettings>
{
    /// <inheritdoc />
    protected override async Task<int> RunAsync(
        CliContext context,
        SteamLogoutSettings settings,
        CancellationToken cancellationToken)
    {
        var account = context.SteamSession.SavedAccountName;
        var revoked = account is null || await context.SteamSession.SignOutAsync();
        if (!revoked)
        {
            context.Output.Warn(SteamSessionService.REVOKE_FAILED_MESSAGE);
        }

        context.Output.Payload(
            new { ok = true, signedOut = account, revoked },
            () => context.Output.Result(account is null ? "Not signed in to Steam." : $"Signed out of {account}."));
        return ExitCode.SUCCESS;
    }
}
