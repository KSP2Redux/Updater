using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Reports which Steam account is signed in, shared with the launcher window.
/// </summary>
public sealed class SteamStatusCommand : ReduxCommand<SteamStatusSettings>
{
    /// <inheritdoc />
    protected override async Task<int> RunAsync(
        CliContext context,
        SteamStatusSettings settings,
        CancellationToken cancellationToken)
    {
        var session = context.SteamSession;
        var account = session.SavedAccountName;
        if (account is null)
        {
            return NotSignedIn(context);
        }

        if (!settings.ShouldCheck)
        {
            context.Output.Payload(
                new { ok = true, signedIn = true, account, @checked = false },
                () => context.Output.Result($"Signed in as {account}"));
            return ExitCode.SUCCESS;
        }

        var connected = await context.Output.StatusAsync("Connecting to Steam", _ => session.TryResumeAsync(cancellationToken));

        // TryResumeAsync forgets a revoked login, which tells a refusal apart from Steam being unreachable.
        if (!connected && !session.HasSavedLogin)
        {
            context.Output.Warn($"Steam no longer accepts the saved login for {account}.");
            return NotSignedIn(context);
        }

        if (!connected)
        {
            return context.Output.Fail(
                ExitCode.STEAM_FAILED,
                $"Signed in as {account}, but Steam could not be reached to check it. Try again when you are online.");
        }

        var persona = session.Account?.PersonaName;
        context.Output.Payload(
            new { ok = true, signedIn = true, account, persona, @checked = true },
            () => context.Output.Result(persona is null || persona == account
                ? $"Signed in as {account} (checked with Steam)"
                : $"Signed in as {persona} ({account}, checked with Steam)"));
        return ExitCode.SUCCESS;
    }

    private static int NotSignedIn(CliContext context) =>
        context.Output.Fail(
            ExitCode.STEAM_NOT_SIGNED_IN,
            "Not signed in to Steam. Run 'redux-launcher-cli steam login' to sign in.");
}
