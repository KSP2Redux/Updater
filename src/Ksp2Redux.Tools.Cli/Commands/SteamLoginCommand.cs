using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Ksp2Redux.Tools.Launcher.Services.Steam;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Signs in to Steam, by QR code or by password, and saves the login for the CLI and the launcher.
/// </summary>
public sealed class SteamLoginCommand : ReduxCommand<SteamLoginSettings>
{
    /// <inheritdoc />
    protected override async Task<int> RunAsync(
        CliContext context,
        SteamLoginSettings settings,
        CancellationToken cancellationToken)
    {
        if (!context.Output.Capabilities.CanPrompt)
        {
            return context.Output.Fail(
                ExitCode.USAGE_ERROR,
                "Signing in to Steam needs a terminal to show the QR code or ask for a password on. " +
                "Sign in once from a terminal and later commands reuse the saved login.");
        }

        try
        {
            if (settings.UsePassword)
            {
                await SignInWithPasswordAsync(context, settings, cancellationToken);
            }
            else
            {
                await SignInWithQrAsync(context, cancellationToken);
            }
        }
        catch (SteamSignInException e)
        {
            return context.Output.Fail(ExitCode.STEAM_FAILED, e.Message);
        }
        catch (TimeoutException)
        {
            return context.Output.Fail(ExitCode.STEAM_FAILED, "Steam could not be reached. Check your connection and try again.");
        }

        var account = context.SteamSession.Account;
        context.Output.Payload(
            new { ok = true, account = account?.AccountName, persona = account?.PersonaName },
            () => context.Output.Result($"Signed in as {account?.DisplayName}"));
        return ExitCode.SUCCESS;
    }

    private static async Task SignInWithPasswordAsync(CliContext context, SteamLoginSettings settings, CancellationToken cancellationToken)
    {
        var console = context.Output.ProgressConsole;
        var username = string.IsNullOrWhiteSpace(settings.Username)
            ? console.Prompt(new TextPrompt<string>("Steam account name:"))
            : settings.Username;
        var password = console.Prompt(new TextPrompt<string>("Password:").Secret());

        // No spinner: Steam Guard may prompt partway, and a prompt cannot draw while a spinner holds the terminal.
        context.Output.Progress("Signing in to Steam...");
        await context.SteamSession.SignInWithCredentialsAsync(username.Trim(), password, new CliSteamGuardPrompt(context.Output), cancellationToken);
    }

    // Steam rotates the QR code while it waits.
    private static async Task SignInWithQrAsync(CliContext context, CancellationToken cancellationToken)
    {
        var output = context.Output;
        const string caption = "Scan this with the Steam Mobile App (Steam Guard, then the QR code icon) to sign in.";

        if (!output.Capabilities.CanAnimate)
        {
            await context.SteamSession.SignInWithQrAsync(url =>
            {
                output.Heading(caption);
                output.ProgressConsole.Write(CliQrCode.Render(url));
            }, cancellationToken);
            return;
        }

        IRenderable Frame(string? url) => url is null
            ? new Markup("Connecting to Steam...")
            : new Rows(new Markup($"[{CliTheme.HEADER_STYLE}]{Markup.Escape(caption)}[/]"), CliQrCode.Render(url));

        await output.ProgressConsole.Live(Frame(null)).StartAsync(async live =>
        {
            await context.SteamSession.SignInWithQrAsync(url =>
            {
                live.UpdateTarget(Frame(url));
                live.Refresh();
            }, cancellationToken);
            live.UpdateTarget(new Markup($"[{CliTheme.ACTIVE_STYLE}]Approved.[/]"));
        });
    }
}
