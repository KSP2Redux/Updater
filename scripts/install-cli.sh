#!/usr/bin/env bash
#
# Installs, updates or removes the KSP2 Redux launcher CLI.
#
# Downloads the newest redux-launcher-cli release from GitHub, checks it against the SHA256 the
# releases API publishes for it, and puts it in a per-user folder. Nothing here needs root and
# nothing is written outside that folder.
#
# Re-running upgrades in place. The launcher config and logs are shared with the launcher window
# and are never touched, including by --uninstall.
#
#   curl -fsSL https://raw.githubusercontent.com/KSP2Redux/Updater/main/scripts/install-cli.sh | bash
#   ./install-cli.sh --version 0.4.2.3
#   ./install-cli.sh --uninstall

set -euo pipefail

REPOSITORY="KSP2Redux/Updater"
ASSET_NAME="redux-launcher-cli-linux-x64"
EXECUTABLE_NAME="redux-launcher-cli"
TAG_PREFIX="cli-v"
INSTALL_DIRECTORY="${REDUX_CLI_HOME:-$HOME/.local/bin}"
WANTED_VERSION=""
UNINSTALL="false"

while [ $# -gt 0 ]; do
    case "$1" in
        --version) WANTED_VERSION="${2:-}"; shift 2 ;;
        --install-dir) INSTALL_DIRECTORY="${2:-}"; shift 2 ;;
        --uninstall) UNINSTALL="true"; shift ;;
        -h|--help) sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "error: unknown argument $1" >&2; exit 1 ;;
    esac
done

target="$INSTALL_DIRECTORY/$EXECUTABLE_NAME"

if [ "$UNINSTALL" = "true" ]; then
    if [ -f "$target" ]; then
        rm -f "$target"
        echo "Removed $target"
    else
        echo "Nothing to remove at $target"
    fi
    echo "Your launcher config and logs were left alone."
    exit 0
fi

need() {
    command -v "$1" >/dev/null 2>&1 || { echo "error: $1 is required but not installed." >&2; exit 1; }
}

need curl

# python is only used to read the releases JSON. Every distro that can run the game has it, and it
# beats asking for jq or parsing JSON with a regex.
PYTHON=""
for candidate in python3 python; do
    # Actually run it rather than trusting that it resolves. Windows ships a python shim that exists
    # on PATH purely to tell you to install python.
    if command -v "$candidate" >/dev/null 2>&1 && "$candidate" -c "pass" >/dev/null 2>&1; then
        PYTHON="$candidate"
        break
    fi
done

if [ -z "$PYTHON" ]; then
    echo "error: python3 is required to read the GitHub releases API." >&2
    exit 1
fi

echo "Looking for the newest $EXECUTABLE_NAME release..."

# Only the CLI's own tags. The launcher ships from updater-v tags in the same repository.
releases_json="$(mktemp)"
trap 'rm -f "$releases_json"' EXIT

# Fetched to a file rather than piped straight into python, because curl reports a write failure
# whenever the reader on the other end of the pipe stops early.
curl -fsSL \
    -H 'Accept: application/vnd.github+json' \
    -H 'User-Agent: install-cli.sh' \
    "https://api.github.com/repos/$REPOSITORY/releases" -o "$releases_json"

release_line="$("$PYTHON" -c '
import json, sys

tag_prefix, asset_name, wanted = sys.argv[1], sys.argv[2], sys.argv[3]

def parsed(tag):
    try:
        return tuple(int(part) for part in tag[len(tag_prefix):].split("."))
    except ValueError:
        return None

candidates = []
for release in json.load(sys.stdin):
    if release.get("draft") or release.get("prerelease"):
        continue
    tag = release.get("tag_name", "")
    if not tag.startswith(tag_prefix):
        continue
    version = parsed(tag)
    if version is None:
        continue
    if wanted and tag[len(tag_prefix):] != wanted:
        continue
    for asset in release.get("assets", []):
        if asset.get("name") == asset_name:
            candidates.append((version, tag, asset.get("browser_download_url", ""), asset.get("digest") or ""))

if not candidates:
    sys.exit(1)

candidates.sort(reverse=True)
version, tag, url, digest = candidates[0]
print(f"{tag}\t{url}\t{digest}")
' "$TAG_PREFIX" "$ASSET_NAME" "$WANTED_VERSION" <"$releases_json")" || {
    echo "error: no $TAG_PREFIX release with a $ASSET_NAME asset was found in $REPOSITORY." >&2
    exit 1
}

tag="$(printf '%s' "$release_line" | cut -f1)"
url="$(printf '%s' "$release_line" | cut -f2)"
digest="$(printf '%s' "$release_line" | cut -f3)"

echo "Downloading $ASSET_NAME from $tag..."

temporary="$(mktemp)"
trap 'rm -f "$temporary" "$releases_json"' EXIT
curl -fsSL "$url" -o "$temporary"

# The releases API publishes the digest it computed on upload, so a truncated or tampered download
# is caught here rather than the first time someone runs the binary.
case "$digest" in
    sha256:*)
        expected="${digest#sha256:}"
        if command -v sha256sum >/dev/null 2>&1; then
            actual="$(sha256sum "$temporary" | cut -d' ' -f1)"
        elif command -v shasum >/dev/null 2>&1; then
            actual="$(shasum -a 256 "$temporary" | cut -d' ' -f1)"
        else
            actual=""
            echo "warning: no sha256sum or shasum available, skipping checksum verification." >&2
        fi

        if [ -n "$actual" ] && [ "$actual" != "$expected" ]; then
            echo "error: checksum mismatch for $ASSET_NAME. Expected $expected, got $actual." >&2
            exit 1
        fi
        [ -n "$actual" ] && echo "Checksum verified."
        ;;
    *)
        echo "warning: release $tag published no checksum for $ASSET_NAME, skipping verification." >&2
        ;;
esac

mkdir -p "$INSTALL_DIRECTORY"

# Replacing the file rather than writing into it, so a running copy keeps its own inode and an
# upgrade from a shell that still has the CLI open does not fail.
install -m 755 "$temporary" "$target.new"
mv -f "$target.new" "$target"

echo ""
echo "Installed $EXECUTABLE_NAME $tag to $target"

case ":$PATH:" in
    *":$INSTALL_DIRECTORY:"*) ;;
    *)
        echo ""
        echo "$INSTALL_DIRECTORY is not on your PATH. Add this to your shell profile:"
        echo "  export PATH=\"\$PATH:$INSTALL_DIRECTORY\""
        ;;
esac

echo "Try: $EXECUTABLE_NAME --help"
