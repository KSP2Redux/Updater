using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Prints a shell completion script that calls back into the hidden complete command.
/// </summary>
public sealed class CompletionCommand : Command<CompletionSettings>
{
    private const string PWSH_SCRIPT = """
        Register-ArgumentCompleter -Native -CommandName redux-launcher-cli -ScriptBlock {
            param($wordToComplete, $commandAst, $cursorPosition)
            $words = @($commandAst.CommandElements | Select-Object -Skip 1 | ForEach-Object { $_.ToString() })
            if ($wordToComplete -ne '') {
                $words = if ($words.Count -gt 1) { $words[0..($words.Count - 2)] } else { @() }
            }
            redux-launcher-cli complete -- "word:$wordToComplete" @words | ForEach-Object {
                [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
            }
        }
        """;

    private const string BASH_SCRIPT = """
        _redux_launcher_cli() {
            local IFS=$'\n'
            local words=("${COMP_WORDS[@]:1:COMP_CWORD-1}")
            COMPREPLY=($(redux-launcher-cli complete -- "word:${COMP_WORDS[COMP_CWORD]}" "${words[@]}"))
        }
        complete -o default -F _redux_launcher_cli redux-launcher-cli
        """;

    /// <inheritdoc />
    // The script only knows how to ask the CLI, so a new command shows up in completion as soon as
    // it is in the catalog, without the user regenerating anything.
    protected override int Execute(CommandContext context, CompletionSettings settings, CancellationToken cancellationToken)
    {
        Console.Out.WriteLine(settings.Shell == CompletionShell.Bash ? BASH_SCRIPT : PWSH_SCRIPT);
        return ExitCode.SUCCESS;
    }
}
