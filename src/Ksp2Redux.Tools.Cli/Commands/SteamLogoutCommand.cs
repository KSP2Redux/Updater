using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

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
        if (account is not null)
        {
            await context.SteamSession.SignOutAsync();
        }

        context.Output.Payload(
            new { ok = true, signedOut = account },
            () => context.Output.Result(account is null ? "Not signed in to Steam." : $"Signed out of {account}."));
        return ExitCode.SUCCESS;
    }
}
