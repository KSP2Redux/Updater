#!/usr/bin/env bash
#
# Builds the macOS download of the KSP2 Redux launcher: an Apple Silicon .app with the Wine + DXMT
# runtime inside it, packed into a .dmg, plus the command line tool (inside the app, and on its own as
# redux-cli-macos-arm64). Only the macOS download carries the runtime. The Windows and
# Linux builds are unchanged.
#
#   packaging/macos/build-mac-app.sh --runtime path/to/redux-mac-runtime.tar.xz
#   packaging/macos/build-mac-app.sh --runtime path/to/wine-runtime-folder --no-dmg
#   packaging/macos/build-mac-app.sh                  # no runtime: the app falls back to CrossOver
#
# The runtime archive has a single top-level wine-runtime/ folder holding wine/ (with DXMT already
# installed into lib/wine), prefix-files/, LICENSES/ and runtime.json. See runtime-layout.md.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$REPO_ROOT/src/Ksp2Redux.Tools.Launcher/Ksp2Redux.Tools.Launcher.csproj"
CLI_PROJECT="$REPO_ROOT/src/Ksp2Redux.Tools.Cli/Ksp2Redux.Tools.Cli.csproj"
RID="osx-arm64"
RUNTIME=""
OUTPUT="$REPO_ROOT/artifacts/macos"
MAKE_DMG="true"

while [ $# -gt 0 ]; do
    case "$1" in
        --runtime) RUNTIME="${2:-}"; shift 2 ;;
        --output) OUTPUT="${2:-}"; shift 2 ;;
        --no-dmg) MAKE_DMG="false"; shift ;;
        -h|--help) sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "error: unknown argument $1" >&2; exit 1 ;;
    esac
done

echo "==> Building the launcher .app ($RID)"
dotnet msbuild "$PROJECT" -t:BundleApp -p:RuntimeIdentifier="$RID" -p:Configuration=Release \
    -p:SelfContained=true -p:PublishSingleFile=false -v:minimal -nologo

PUBLISH_DIR="$REPO_ROOT/src/Ksp2Redux.Tools.Launcher/bin/Release/net10.0/$RID/publish"
SOURCE_APP="$PUBLISH_DIR/KSP2 Redux.app"
[ -d "$SOURCE_APP" ] || { echo "error: $SOURCE_APP was not produced" >&2; exit 1; }

mkdir -p "$OUTPUT"
APP="$OUTPUT/KSP2 Redux.app"
rm -rf "$APP"
cp -R "$SOURCE_APP" "$APP"

if [ -n "$RUNTIME" ]; then
    echo "==> Adding the Wine + DXMT runtime from $RUNTIME"
    RESOURCES="$APP/Contents/Resources"
    rm -rf "$RESOURCES/wine-runtime"
    if [ -d "$RUNTIME" ]; then
        cp -R "$RUNTIME" "$RESOURCES/wine-runtime"
    else
        tar -xf "$RUNTIME" -C "$RESOURCES"
    fi
    [ -f "$RESOURCES/wine-runtime/runtime.json" ] || { echo "error: the runtime has no runtime.json at its top level" >&2; exit 1; }
else
    echo "==> No runtime given: this build launches the game through CrossOver when it is installed"
fi

# The CLI rides inside the app so it finds the runtime beside it, the same way the launcher does, and is
# also written out on its own as the release asset. That name must avoid "win" and "linux" as well.
echo "==> Building the command line tool ($RID)"
CLI_PUBLISH="$OUTPUT/cli-publish"
rm -rf "$CLI_PUBLISH"
dotnet publish "$CLI_PROJECT" -c Release -r "$RID" --self-contained true -p:PublishSingleFile=true \
    -o "$CLI_PUBLISH" -v:minimal -nologo
cp "$CLI_PUBLISH/redux-launcher-cli" "$APP/Contents/MacOS/redux-launcher-cli"
# Removed first rather than copied over: macOS caches a binary's signature against the file, and one
# overwritten in place is killed on its next launch.
rm -f "$OUTPUT/redux-cli-macos-arm64"
cp "$CLI_PUBLISH/redux-launcher-cli" "$OUTPUT/redux-cli-macos-arm64"
codesign --force --sign - "$OUTPUT/redux-cli-macos-arm64"
rm -rf "$CLI_PUBLISH"

# Ad-hoc signing lets the app run on Apple Silicon. A release build should be signed with the team's
# Developer ID and notarized instead, or Gatekeeper warns on first open.
echo "==> Signing (ad-hoc)"
codesign --force --deep --sign - "$APP"

if [ "$MAKE_DMG" = "true" ]; then
    # The launcher's self-updater picks release assets by name and matches "win" and "linux" anywhere
    # in it, so the file name must avoid both (which rules out "darwin").
    DMG="$OUTPUT/KSP2-Redux-macOS-arm64.dmg"
    echo "==> Creating $DMG"
    rm -f "$DMG"
    hdiutil create -volname "KSP2 Redux" -srcfolder "$APP" -ov -format UDZO "$DMG" >/dev/null
    echo "Built $DMG ($(du -h "$DMG" | cut -f1))"
fi

echo "Built $APP"
echo "Built $OUTPUT/redux-cli-macos-arm64"
