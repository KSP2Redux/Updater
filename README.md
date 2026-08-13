# KSP2 Redux Updater
This is the installer, updater and uninstaller application for KSP2 Redux.

## Command line

`redux-launcher-cli` drives the same install path as the launcher window, from a terminal. It reads
and writes the same configuration, so the two stay in step.

Install it with one line. Nothing needs administrator rights.

```powershell
irm https://raw.githubusercontent.com/KSP2Redux/Updater/main/scripts/install-cli.ps1 | iex
```

```sh
curl -fsSL https://raw.githubusercontent.com/KSP2Redux/Updater/main/scripts/install-cli.sh | bash
```

That drops the binary in `%LOCALAPPDATA%\Programs\redux-launcher-cli` or `~/.local/bin` and puts it
on your PATH. Running it again upgrades in place. Then:

```sh
redux-launcher-cli --help              # every command, with examples
redux-launcher-cli detect              # find KSP2 the way the launcher does
redux-launcher-cli installs add        # add the install it found to the config
redux-launcher-cli update              # install the newest build in its channel
redux-launcher-cli launch              # start the game
```

It keeps itself current: `redux-launcher-cli self-update` installs the newest build, `version
--check` reports whether one is published, and the CLI mentions a new release on its own once a day
when you are running it in a terminal. `--no-update-check` turns that off, `self-uninstall` removes
the binary and leaves your launcher config alone.

For scripting, `--json` puts a document on stdout and everything else on stderr, exit codes are
stable, and `completion pwsh|bash` prints a shell completion script.

## Development

Requires the .NET 10 SDK (pinned in `global.json`).

```sh
dotnet build          # build everything
dotnet test           # run the test suite
```

The solution (`Ksp2Redux.Tools.slnx`) is laid out as:

| Path | What it is |
|---|---|
| `src/Ksp2Redux.Tools.Launcher` | The Avalonia launcher/updater app (the main deliverable) |
| `src/Ksp2Redux.Tools.Cli` | Headless CLI over the launcher's install path (`redux-launcher-cli`) |
| `src/Ksp2Redux.Tools.Common` | Shared patch engine (`Patching/`) and release-feed schema (`Models/`) |
| `src/Ksp2Redux.Tools.Installer` | Windows WPF installer |
| `src/Ksp2Redux.Tools.PatchApplier` / `PatchGenerator` | CLI tools to apply/create patch files |
| `src/Ksp2Redux.Tools.Uploader` | CLI tool that publishes releases and the manifest |
| `tests/` | NUnit test suite (incl. Avalonia headless UI tests) and the MockGame fixture |
| `design/` | The `@ksp2redux/design` web design system, previews, and conventions |

Package versions are managed centrally in `Directory.Packages.props`; shared
MSBuild settings (including the version) live in `Directory.Build.props`.
Warnings are errors.

## Releasing

Bump `<Version>` in `Directory.Build.props` and merge to `main`. CI detects
the new version, tags `updater-v<version>`, and publishes one release holding
both products (see `.github/workflows/release.yaml`):

| Asset | What it is |
|---|---|
| `Ksp2Redux-win-x64.exe`, `Ksp2Redux-linux-x64` | the launcher |
| `redux-cli-x64.exe`, `redux-cli-x64` | the command line tool |

The CLI asset names must never contain `win` or `linux`. The launcher's
self-update picks its download out of this same asset list with
`Assets.FirstOrDefault(a => a.Name.Contains("win" or "linux"))`, and launchers
already installed cannot be fixed, so an asset that matched would be handed to
them as an update to themselves. Note `windows` contains `win`, so the rule is
about the substring. A test in `CliReleaseServiceTest` holds the CLI to it.

The release notes lead with a table saying which file is which, because a
single page with four binaries on it is how people end up downloading the
command line tool when they wanted the launcher.

Publishing to winget is a separate manual step, see `packaging/winget`.

It is a cross-platform application with releases currently being made for Windows and Linux (untested). Please report any problems with running the application in
[Issues](https://github.com/KSP2Redux/Updater/issues) or in the [KSP2 Redux Discord server](https://discord.gg/ksp2redux).

## KSP2 Redux
For more information about KSP2 Redux, see [our website](https://ksp2redux.org) or the [Redux GitHub page](https://github.com/KSP2Redux/Redux).

## Contact
You can contact us in the [KSP2 Redux Discord server](https://discord.gg/ksp2redux).
