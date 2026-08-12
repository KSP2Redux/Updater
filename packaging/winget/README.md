# winget packaging

The CLI ships as a single portable exe, so winget installs it as a `portable` package: winget puts
the binary in its own links folder and manages the PATH entry, which means `install-cli.ps1` and
winget will not fight over the same folder.

The manifests here are templates. `{VERSION}` and `{SHA256}` are filled in per release.

## Publishing a version

Nothing in this repository can push to winget on its own, because the package index lives in
[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) and submitting to it needs a
GitHub token that belongs to a person rather than to Actions.

First release, done once by hand:

1. Install the helper: `winget install wingetcreate`
2. Run it against the release asset:

   ```powershell
   wingetcreate new https://github.com/KSP2Redux/Updater/releases/download/cli-v{VERSION}/redux-launcher-cli-win-x64.exe
   ```

3. Answer the prompts using the values in `KSP2Redux.ReduxLauncherCli.yaml`, then let it open the
   pull request against winget-pkgs. Expect a review turnaround measured in days.

Afterwards, each release is an update rather than a new package:

```powershell
wingetcreate update KSP2Redux.ReduxLauncherCli `
  --version {VERSION} `
  --urls https://github.com/KSP2Redux/Updater/releases/download/cli-v{VERSION}/redux-launcher-cli-win-x64.exe `
  --submit --token $env:WINGET_TOKEN
```

To automate that from the release workflow, create a classic PAT with `public_repo` on an account
that has forked winget-pkgs, save it as the `WINGET_TOKEN` repository secret, and add the step in
`release.yaml` marked with the winget comment. It is left out until the secret exists, because a
release job that fails on a missing token every time is worse than not having the step.

## Identifiers

| Field | Value |
|---|---|
| PackageIdentifier | `KSP2Redux.ReduxLauncherCli` |
| Command | `redux-launcher-cli` |
| InstallerType | `portable` |
| Architecture | `x64` |
