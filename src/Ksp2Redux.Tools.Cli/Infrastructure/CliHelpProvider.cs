using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Cli.Help;
using Spectre.Console.Rendering;

namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// The help screen, with the options every command shares repeated on the root page.
/// </summary>
public sealed class CliHelpProvider : HelpProvider
{
    private static readonly (string Option, string Description)[] SHARED_OPTIONS =
    [
        ("--json", "Emit a JSON document on stdout instead of text"),
        ("--verbose", "Print the launcher's info and debug log lines to stderr"),
        ("--color <MODE>", "When to style output: auto, always or never"),
        ("--install <ID_OR_NAME>", "Id or name of the KSP2 install to act on"),
    ];

    /// <summary>
    /// Initializes the provider against the running application's settings.
    /// </summary>
    /// <param name="settings">The settings the command app was configured with.</param>
    public CliHelpProvider(ICommandAppSettings settings)
        : base(settings)
    {
    }

    /// <inheritdoc />
    public override IEnumerable<IRenderable> GetOptions(ICommandModel model, ICommandInfo? command)
    {
        foreach (var renderable in base.GetOptions(model, command))
        {
            yield return renderable;
        }

        if (command is not null)
        {
            yield break;
        }

        Grid grid = new();
        grid.AddColumn(new GridColumn().PadRight(4).NoWrap());
        grid.AddColumn();
        foreach (var (option, description) in SHARED_OPTIONS)
        {
            grid.AddRow($"    {option}", description);
        }

        yield return Text.NewLine;
        yield return new Markup($"[{CliTheme.HEADER_STYLE}]COMMAND OPTIONS:[/]");
        yield return Text.NewLine;
        yield return grid;
    }
}
