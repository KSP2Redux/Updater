#!/usr/bin/env bash
#
# Builds the Wine + DXMT runtime that ships inside the macOS launcher, from source.
#
#   packaging/macos/build-runtime.sh
#   packaging/macos/build-runtime.sh --install-deps --jobs 12
#   packaging/macos/build-runtime.sh --test-game ~/Games/KSP2-Redux
#   packaging/macos/build-runtime.sh --skip-build       # re-stage and re-test an existing build
#
# Output: artifacts/macos/redux-mac-runtime.tar.xz, ready for build-mac-app.sh --runtime.
#
# Wine comes from CodeWeavers' open-source CrossOver sources (LGPL-2.1): unlike upstream Wine 10.18+,
# its winemac exports the macdrv_functions table DXMT needs. DXMT (LGPL-2.1) comes from its official
# release. CrossOver's binaries and Apple's D3DMetal are not redistributable, so neither is used.
#
# Needs: macOS with the Xcode Command Line Tools, Rosetta on Apple Silicon, and Homebrew's bison,
# flex and mingw-w64 (--install-deps installs them). A build takes 10-20 minutes and about 5 GB.

set -euo pipefail

# Pinned for a reproducible runtime. After a bump, run with --test-game before shipping.
CROSSOVER_VERSION="26.3.0"
CROSSOVER_SHA256="ac99c8ca4b3848f3e81784135f023df266b61c2345726ea55a50b3e030dd6872"
DXMT_VERSION="v0.80"
DXMT_SHA256="8f260e36b5739e68f3bad613381441385c4dc7b85b78ba8de653d5a6a264529d"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WORK="$REPO_ROOT/artifacts/macos/runtime-build"
OUTPUT="$REPO_ROOT/artifacts/macos/redux-mac-runtime.tar.xz"
JOBS="$(sysctl -n hw.ncpu)"
INSTALL_DEPS="false"
SKIP_BUILD="false"
TEST_GAME=""

while [ $# -gt 0 ]; do
    case "$1" in
        --work) WORK="${2:?}"; shift 2 ;;
        --output) OUTPUT="${2:?}"; shift 2 ;;
        --jobs) JOBS="${2:?}"; shift 2 ;;
        --install-deps) INSTALL_DEPS="true"; shift ;;
        --skip-build) SKIP_BUILD="true"; shift ;;
        --test-game) TEST_GAME="${2:?}"; shift 2 ;;
        -h|--help) sed -n '2,17p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "error: unknown argument $1" >&2; exit 1 ;;
    esac
done

step() { echo; echo "==> $*"; }
fail() { echo "error: $*" >&2; exit 1; }

CROSSOVER_URL="https://media.codeweavers.com/pub/crossover/source/crossover-sources-$CROSSOVER_VERSION.tar.gz"
DXMT_URL="https://github.com/3Shain/dxmt/releases/download/$DXMT_VERSION/dxmt-$DXMT_VERSION-builtin.tar.gz"
DXMT_LICENSE_URL="https://raw.githubusercontent.com/3Shain/dxmt/$DXMT_VERSION/LICENSE"

DOWNLOADS="$WORK/downloads"
SOURCE="$WORK/src"
BUILD="$WORK/build"
INSTALL="$WORK/install"
STAGE="$WORK/stage"
RUNTIME="$STAGE/wine-runtime"

step "Checking the toolchain"
[ "$(uname -s)" = "Darwin" ] || fail "this script builds a macOS runtime and must run on macOS"
xcode-select -p >/dev/null 2>&1 || fail "the Xcode Command Line Tools are missing: run xcode-select --install"
arch -x86_64 /usr/bin/true 2>/dev/null || fail "Rosetta is missing: run softwareupdate --install-rosetta"
command -v brew >/dev/null || fail "Homebrew is required for bison, flex and mingw-w64"

for formula in bison flex mingw-w64; do
    if ! brew list --formula "$formula" >/dev/null 2>&1; then
        [ "$INSTALL_DEPS" = "true" ] || fail "missing Homebrew formula $formula (rerun with --install-deps, or brew install $formula)"
        brew install "$formula"
    fi
done

BREW_PREFIX="$(brew --prefix)"
# macOS bison is too old for Wine and Homebrew's is keg-only, so it goes first on PATH.
TOOL_PATH="$(brew --prefix bison)/bin:$(brew --prefix flex)/bin:$BREW_PREFIX/bin:/usr/bin:/bin:/usr/sbin:/sbin"
command -v "$BREW_PREFIX/bin/x86_64-w64-mingw32-gcc" >/dev/null || fail "x86_64-w64-mingw32-gcc not found after installing mingw-w64"

download() {
    local url="$1" file="$2" sha256="$3"
    if [ -f "$file" ] && [ "$(shasum -a 256 "$file" | cut -d' ' -f1)" = "$sha256" ]; then
        echo "  using cached $(basename "$file")"
        return
    fi
    echo "  downloading $url"
    curl -fL --retry 3 -o "$file.part" "$url"
    [ "$(shasum -a 256 "$file.part" | cut -d' ' -f1)" = "$sha256" ] || fail "checksum mismatch for $url"
    mv "$file.part" "$file"
}

step "Fetching sources"
mkdir -p "$DOWNLOADS"
download "$CROSSOVER_URL" "$DOWNLOADS/crossover-sources-$CROSSOVER_VERSION.tar.gz" "$CROSSOVER_SHA256"
download "$DXMT_URL" "$DOWNLOADS/dxmt-$DXMT_VERSION-builtin.tar.gz" "$DXMT_SHA256"
curl -fsSL -o "$DOWNLOADS/dxmt-LICENSE" "$DXMT_LICENSE_URL"

WINE_SRC="$SOURCE/sources/wine"

build_wine() {
    step "Unpacking the Wine source"
    rm -rf "$SOURCE"
    mkdir -p "$SOURCE"
    tar -xzf "$DOWNLOADS/crossover-sources-$CROSSOVER_VERSION.tar.gz" -C "$SOURCE" sources/wine
    [ -x "$WINE_SRC/configure" ] || fail "the CrossOver archive has no sources/wine/configure"

    # CodeWeavers' sources omit this generated header. Its strings only appear in the crash dialog.
    if [ ! -f "$WINE_SRC/programs/winedbg/distversion.h" ]; then
        cat > "$WINE_SRC/programs/winedbg/distversion.h" <<'EOF'
#define WINDEBUG_WHAT_HAPPENED_MESSAGE "This can be caused by a problem in the program or a deficiency in Wine."
#define WINDEBUG_USER_SUGGESTION_MESSAGE "If this problem is not present under Windows, you can save the detailed information to a file using the \"Save As\" button and attach it to a bug report."
EOF
    fi

    # CrossOver 26 needs SONAME_LIBVULKAN, which configure defines only when MoltenVK is found. A name
    # that fails to load is fine: Wine carries on without Vulkan, which DXMT does not use.
    local vulkan_c="$WINE_SRC/dlls/win32u/vulkan.c"
    if grep -q 'libvulkan = SONAME_LIBVULKAN;' "$vulkan_c" && ! grep -q 'REDUX: no MoltenVK' "$vulkan_c"; then
        perl -0pi -e 's/(static void vulkan_init_once\(void\))/#ifndef SONAME_LIBVULKAN \/* REDUX: no MoltenVK in this build *\/\n#define SONAME_LIBVULKAN "libMoltenVK.dylib"\n#endif\n\n$1/' "$vulkan_c"
    fi

    # KSP2 is x86_64, so Wine builds in an x86_64 shell under Rosetta. PKG_CONFIG_LIBDIR keeps the arm64
    # Homebrew libraries out, and each disabled feature would need an x86_64 library KSP2 does not use.
    BUILD_ENV="export PATH='$TOOL_PATH' PKG_CONFIG_LIBDIR=/nonexistent MACOSX_DEPLOYMENT_TARGET=11.0
    export CC='clang -arch x86_64' CXX='clang++ -arch x86_64' OBJC='clang -arch x86_64'"

    step "Configuring Wine $CROSSOVER_VERSION (x86_64)"
    rm -rf "$BUILD" "$INSTALL"
    mkdir -p "$BUILD"
    arch -x86_64 /bin/bash -c "$BUILD_ENV
    cd '$BUILD'
    '$WINE_SRC/configure' --prefix='$INSTALL' --enable-archs=x86_64 --disable-tests \
        --without-x --without-vulkan --without-gstreamer --without-gnutls --without-freetype --without-sdl \
        --without-krb5 --without-cups --without-usb --without-pcap --without-pcsclite --without-dbus \
        --without-capi --without-gphoto --without-sane --without-netapi --without-v4l2 --without-inotify \
        --without-unwind" > "$WORK/configure.log" 2>&1 || { tail -30 "$WORK/configure.log"; fail "configure failed, see $WORK/configure.log"; }

    step "Building Wine with $JOBS jobs (this is the long part)"
    arch -x86_64 /bin/bash -c "$BUILD_ENV
    cd '$BUILD'
    make -j$JOBS && make install" > "$WORK/make.log" 2>&1 || { grep -E 'error' "$WORK/make.log" | tail -20; fail "the build failed, see $WORK/make.log"; }
}

if [ "$SKIP_BUILD" = "true" ]; then
    [ -x "$INSTALL/bin/wineserver" ] || fail "--skip-build needs a finished build in $INSTALL"
    step "Reusing the Wine build in $INSTALL"
else
    build_wine
fi

step "Staging the runtime"
rm -rf "$STAGE"
mkdir -p "$RUNTIME/wine/bin" "$RUNTIME/wine/lib/wine" "$RUNTIME/wine/share/wine" \
    "$RUNTIME/prefix-files/system32" "$RUNTIME/LICENSES"

if [ -x "$INSTALL/lib/wine/x86_64-unix/wine" ]; then
    # Wine 11 (CrossOver 26) installs the real loader beside ntdll.so. bin/wine is only a launcher.
    cp "$INSTALL/bin/wine" "$INSTALL/bin/wineserver" "$RUNTIME/wine/bin/"
    ln -s wine "$RUNTIME/wine/bin/wine64"
else
    # Wine 10 (CrossOver 25) only finds itself under the name "wineloader". A copy named wine or
    # wine64 fails with "could not load kernel32.dll", so those must be symlinks.
    cp "$INSTALL/bin/wine64" "$RUNTIME/wine/bin/wineloader"
    cp "$INSTALL/bin/wineserver" "$RUNTIME/wine/bin/"
    ln -s wineloader "$RUNTIME/wine/bin/wine"
    ln -s wineloader "$RUNTIME/wine/bin/wine64"
fi
rsync -a --exclude '*.a' "$INSTALL/lib/wine/x86_64-unix" "$INSTALL/lib/wine/x86_64-windows" "$RUNTIME/wine/lib/wine/"
# All of share/wine: leaving out a file that wine.inf installs makes wineboot retry the copy forever.
cp -R "$INSTALL/share/wine/." "$RUNTIME/wine/share/wine/"

# Local symbols only, so exported ones such as _macdrv_functions survive.
find "$RUNTIME/wine/bin" "$RUNTIME/wine/lib/wine/x86_64-unix" -type f \( -name '*.so' -o -perm -u+x \) -exec strip -x {} +
# DWARF info more than doubles the size. Some files here are not PE images, so failures are ignored.
find "$RUNTIME/wine/lib/wine/x86_64-windows" -type f -print0 |
    xargs -0 -n 64 "$BREW_PREFIX/bin/x86_64-w64-mingw32-strip" --strip-debug 2>/dev/null || true

step "Installing DXMT $DXMT_VERSION"
DXMT_DIR="$WORK/dxmt"
rm -rf "$DXMT_DIR"
mkdir -p "$DXMT_DIR"
tar -xzf "$DOWNLOADS/dxmt-$DXMT_VERSION-builtin.tar.gz" -C "$DXMT_DIR"
DXMT_ROOT="$DXMT_DIR/$DXMT_VERSION"
# The builtin build replaces Wine's d3d11 and dxgi, so no DLL overrides are needed. DXMT also wants
# winemetal.dll in every prefix, which the launcher copies from prefix-files on first run.
cp "$DXMT_ROOT/x86_64-unix/winemetal.so" "$RUNTIME/wine/lib/wine/x86_64-unix/"
for dll in d3d11 dxgi d3d10core winemetal; do
    cp "$DXMT_ROOT/x86_64-windows/$dll.dll" "$RUNTIME/wine/lib/wine/x86_64-windows/"
done
cp "$DXMT_ROOT/x86_64-windows/winemetal.dll" "$RUNTIME/prefix-files/system32/"

cp "$WINE_SRC/COPYING.LIB" "$RUNTIME/LICENSES/wine-COPYING.LIB"
cp "$WINE_SRC/LICENSE" "$RUNTIME/LICENSES/wine-LICENSE"
cp "$DOWNLOADS/dxmt-LICENSE" "$RUNTIME/LICENSES/dxmt-LICENSE"

# prefixEnv disables the Mono and Gecko installers, whose dialogs would block a new prefix's first run.
cat > "$RUNTIME/runtime.json" <<EOF
{
  "wine": "CrossOver $CROSSOVER_VERSION FOSS, x86_64",
  "dxmt": "$DXMT_VERSION",
  "wineBinary": "wine/bin/wine",
  "source": "$CROSSOVER_URL",
  "sourceSha256": "$CROSSOVER_SHA256",
  "dxmtSource": "$DXMT_URL",
  "prefixEnv": { "WINEDLLOVERRIDES": "mscoree,mshtml=" }
}
EOF

step "Verifying"
nm -gU "$RUNTIME/wine/lib/wine/x86_64-unix/winemac.so" | grep -q ' _macdrv_functions$' \
    || fail "winemac.so does not export _macdrv_functions, so DXMT will not work with this build"
echo "  winemac.so exports _macdrv_functions"

TEST_PREFIX="$WORK/test-prefix"
rm -rf "$TEST_PREFIX"
WINEPREFIX="$TEST_PREFIX" WINEDEBUG=-all WINEDLLOVERRIDES="mscoree,mshtml=" \
    "$RUNTIME/wine/bin/wine" wineboot --init >/dev/null 2>&1 || true
WINEPREFIX="$TEST_PREFIX" "$RUNTIME/wine/bin/wineserver" --wait || true
[ -d "$TEST_PREFIX/drive_c/windows/system32" ] || fail "wineboot could not create a prefix"
echo "  wineboot created a prefix"

if [ -n "$TEST_GAME" ]; then
    [ -f "$TEST_GAME/KSP2_x64.exe" ] || fail "no KSP2_x64.exe in $TEST_GAME"
    if pgrep -f KSP2_x64.exe >/dev/null; then fail "KSP2 is already running, close it before --test-game"; fi
    cp "$RUNTIME/prefix-files/system32/winemetal.dll" "$TEST_PREFIX/drive_c/windows/system32/"

    echo "  starting KSP2 (a game window opens, and closes again once the main menu loads)"
    (cd "$TEST_GAME" && WINEPREFIX="$TEST_PREFIX" WINEDEBUG=-all WINEMSYNC=1 WINEDLLOVERRIDES="mscoree,mshtml=" \
        exec "$RUNTIME/wine/bin/wine" KSP2_x64.exe > "$WORK/game.log" 2>&1) &
    # Disowned so killing it below prints no job notice.
    disown $!

    reached_menu="false"
    for _ in $(seq 1 60); do
        sleep 3
        player_log="$(find "$TEST_PREFIX/drive_c/users" -name Player.log 2>/dev/null | head -1)"
        if [ -n "$player_log" ] && grep -q 'new: \[MainMenu\]' "$player_log"; then reached_menu="true"; break; fi
        pgrep -f KSP2_x64.exe >/dev/null || break
    done
    pkill -f KSP2_x64.exe || true
    sleep 2
    WINEPREFIX="$TEST_PREFIX" "$RUNTIME/wine/bin/wineserver" -k || true

    if grep -q 'no exported symbols needed by DXMT' "$WORK/game.log"; then fail "DXMT could not attach to Wine"; fi
    [ "$reached_menu" = "true" ] || fail "KSP2 did not reach the main menu, see $WORK/game.log and $player_log"
    grep -m1 'Renderer:' "$player_log" | sed 's/^ */  /'
    echo "  KSP2 reached the main menu"
fi

step "Packing $OUTPUT"
mkdir -p "$(dirname "$OUTPUT")"
rm -f "$OUTPUT"
tar -cJf "$OUTPUT" -C "$STAGE" wine-runtime
echo "Built $OUTPUT ($(du -h "$OUTPUT" | cut -f1), $(du -sh "$RUNTIME" | cut -f1) unpacked)"
echo "Next: packaging/macos/build-mac-app.sh --runtime \"$OUTPUT\""
