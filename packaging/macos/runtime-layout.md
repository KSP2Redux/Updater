# macOS runtime

KSP2 has no macOS build, so the macOS launcher runs the Windows build through Wine, with
[DXMT](https://github.com/3Shain/dxmt) translating Direct3D 11 to Metal. Both are LGPL-2.1, so the
runtime can ship inside the launcher as long as its source is published alongside each release.

Only the macOS download carries it. Build it, then bundle it into the app:

```sh
packaging/macos/build-runtime.sh --install-deps --test-game ~/path/to/stock-or-redux/KSP2
packaging/macos/build-mac-app.sh --runtime artifacts/macos/redux-mac-runtime.tar.xz
```

`build-runtime.sh` compiles Wine from pinned, checksum-verified sources, installs DXMT, checks that
DXMT can attach, and with `--test-game` starts KSP2 and waits for the main menu. `build-mac-app.sh`
copies the result into `KSP2 Redux.app/Contents/Resources/wine-runtime`.

## In CI

- `.github/workflows/macos-runtime.yml` builds the runtime on a macOS runner and caches it, keyed on
  `build-runtime.sh`, so it is only recompiled when the script changes. It runs on its own for pull
  requests that touch the script, and can be started by hand.
- `.github/workflows/dotnet.yml` has a `macos` job that runs the tests on macOS and builds the `.app`.
- `.github/workflows/release.yaml` builds the runtime through the workflow above, packs
  `KSP2-Redux-macOS-arm64.dmg`, attaches it to the new release, and adds the Wine and DXMT source
  links to the release notes.

CI cannot run `--test-game`, since there is no KSP2 install on a runner. Run it locally whenever the
pinned versions change.

## What the runtime leaves out

The build skips Wine features that each need an x86_64 copy of a library: fonts (freetype), HTTPS
inside Wine (gnutls), media playback (gstreamer), and 32-bit support. KSP2 needs none of them. Wine's
own dialogs may show blank text without freetype, which is the first thing to add if that matters.

## Layout

```
wine-runtime/
  runtime.json            {"wine": "<name and version>", "dxmt": "v0.80", "wineBinary": "wine/bin/wine"}
  wine/                   bin/, lib/, share/ of an x86_64 macOS Wine build
    lib/wine/x86_64-unix/winemetal.so          DXMT, installed as builtin DLLs
    lib/wine/x86_64-windows/{d3d11,dxgi,d3d10core,winemetal}.dll
  prefix-files/
    system32/winemetal.dll                     copied into the prefix on first launch, as DXMT requires
  LICENSES/               Wine COPYING.LIB, DXMT LICENSE
```

The launcher creates its Wine prefix in `~/Library/Application Support/Ksp2Redux/wine-prefix` the
first time the game is launched, and saves live inside it. A runtime can also be tried without
rebuilding the app by extracting it to `~/Library/Application Support/Ksp2Redux/wine-runtime`.

When no runtime is bundled, the launcher uses CrossOver if it is installed, in a bottle named
`KSP2Redux` set to the DXMT backend.

## Which Wine

DXMT reaches into Wine's macOS driver (`winemac.so`) for five functions and reads the start of its
per-window data. That works with:

- Wine built from CodeWeavers' open-source CrossOver sources, which export a `macdrv_functions`
  table for exactly this. **CrossOver 26.3.0 (Wine 11.0) with DXMT v0.80 is the verified pair**
  that `build-runtime.sh` pins: KSP2 Redux renders, reaches the main menu and takes mouse and
  keyboard input on an M4 Pro. CrossOver 25.1.1 (Wine 10.0) also renders, but the game gets no input
  at all, and its log shows `EnableMouseInPointer failed ... Call not implemented`. A runtime that
  only reaches the main menu is not proven, so always try clicking and typing too.
- Upstream Wine 10.0 to 10.10 should work once those five functions are given default visibility. The
  struct layout matches, but this has not been built and tested yet.

It does **not** work with upstream Wine 10.18 or newer: the window data was restructured
(`client_view` replaced `cocoa_view` and `client_cocoa_view`), and DXMT fails with
"your Wine has no exported symbols needed by DXMT". Prebuilt WineHQ packages for macOS are only
available for 11.x, so the runtime has to be built from source.

Apple's D3DMetal (from the Game Porting Toolkit) and CrossOver's own binaries must never be bundled.
Their licences do not allow redistribution.
