# Ksp2Redux.Tools.GameCli

Command-line and MCP tooling for the KSP2 Redux CLI integration.

The `ksp2redux-cli` executable talks to the Unity editor bridge and the runtime CLI integration bridge over local JSON-RPC sockets. Run it without arguments for the command list.

It is a developer tool for working on the game and is not part of a launcher release. The game repository builds it into `scripts/Ksp2Redux.Cli.exe` with `scripts/build-cli.ps1`.

This project used to be the separate `ksp2redux.cli` repository. Its history was merged in here.
