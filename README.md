# KSP2 Redux Updater
This is the installer, updater and uninstaller application for KSP2 Redux.

## macOS

KSP2 never shipped for the Mac, so the macOS launcher (`KSP2-Redux-macOS-arm64.dmg`, Apple Silicon)
carries a Wine runtime that runs the Windows game, and can download your copy of KSP2 from Steam.

The launcher is not signed by Apple yet, so macOS asks you to approve it the first time:

1. Open the `.dmg` and drag **KSP2 Redux** into **Applications**.
2. Open KSP2 Redux. When macOS says it could not verify the app, click **Done**.
3. Open **System Settings**, go to **Privacy & Security**, and scroll down to **Security**. Next to
   the message about KSP2 Redux, click **Open Anyway**, then confirm with your password or Touch ID.
4. Click **Open Anyway** once more when macOS asks. From then on the launcher opens normally, until
   you download a new version.

If the launcher opens but the game does not start, run this once in Terminal, then try again:

```sh
xattr -dr com.apple.quarantine "/Applications/KSP2 Redux.app"
```

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
on your PATH. On a Mac the same `curl` line installs the Apple Silicon build, and the macOS launcher
app also carries a copy at `KSP2 Redux.app/Contents/MacOS/redux-launcher-cli`. Running it again
upgrades in place. Then:

```sh
redux-launcher-cli --help              # every command, with examples
redux-launcher-cli detect              # find KSP2 the way the launcher does
redux-launcher-cli installs add        # add the install it found to the config
redux-launcher-cli update              # install the newest build in its channel
redux-launcher-cli launch              # start the game
```

No KSP2 on this machine, or on a Mac, where Steam will not download it? The CLI can fetch your own
copy with your Steam account, no Steam client needed:

```sh
redux-launcher-cli steam login         # scan a QR code with the Steam Mobile App, or --password
redux-launcher-cli steam download      # download KSP2 into ~/Games and add it as the active profile
redux-launcher-cli update              # then install Redux into it
```

The sign-in is shared with the launcher window. On macOS, `launch` runs the game through the Wine
runtime inside the launcher app (or CrossOver when that is all there is), in the same prefix the
launcher uses, so saves are shared too. `kill` stops it.

Everything in the launcher's Settings tab is here too. Each install profile carries its own launch
settings: `installs show` lists them, `installs set` changes them (including launching through Steam
on Windows and Linux), and `installs delete` removes a copy of the game from disk along with its
profile. `settings` shows and changes the patch source, concurrent chunks and verbose logging,
`open install|logs|game-data|storage` opens those folders, and `news` lists the latest posts.

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

The launcher also runs on macOS for development (the game itself is not
available there yet). To produce a proper `.app` bundle:

```sh
dotnet msbuild src/Ksp2Redux.Tools.Launcher/Ksp2Redux.Tools.Launcher.csproj \
  -t:BundleApp -p:RuntimeIdentifier=osx-arm64 -p:Configuration=Release \
  -p:SelfContained=true -p:PublishSingleFile=false
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
| `KSP2-Redux-macOS-arm64.dmg` | the macOS launcher, with the Wine runtime and the CLI inside |
| `redux-cli-x64.exe`, `redux-cli-x64`, `redux-cli-macos-arm64` | the command line tool |

The CLI asset names must never contain `win` or `linux`. The launcher's
self-update picks its download out of this same asset list with
`Assets.FirstOrDefault(a => a.Name.Contains("win" or "linux"))`, and launchers
already installed cannot be fixed, so an asset that matched would be handed to
them as an update to themselves. Note `windows` contains `win`, and so does
`darwin`, so the rule is about the substring. A test in `CliReleaseServiceTest` holds the CLI to it.

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
