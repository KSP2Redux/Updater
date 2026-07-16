# KSP2 Redux Updater
This is the installer, updater and uninstaller application for KSP2 Redux.

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
the new version, tags `updater-v<version>`, publishes self-contained win-x64
and linux-x64 launcher binaries, and creates the GitHub release automatically
(see `.github/workflows/release.yaml`).

It is a cross-platform application with releases currently being made for Windows and Linux (untested). Please report any problems with running the application in
[Issues](https://github.com/KSP2Redux/Updater/issues) or in the [KSP2 Modding Society Discord server](https://discord.gg/8yq8d5VGQR).

## KSP2 Redux
For more information about KSP2 Redux, see [our website](https://ksp2redux.org) or the [Redux GitHub page](https://github.com/KSP2Redux/Redux).

## Contact
You can contact us in the [KSP2 Modding Society Discord server](https://discord.gg/8yq8d5VGQR).
