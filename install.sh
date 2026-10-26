#!/bin/sh
# Installs Corvid from a GitHub release: corvid, corvid-vault and corvid-sandbox, side by side; with --app, the windowed
# app (corvid-app) too.
#
#   curl -fsSL https://raw.githubusercontent.com/starlinghq/corvid/main/install.sh | sh
#   curl -fsSL https://raw.githubusercontent.com/starlinghq/corvid/main/install.sh | sh -s -- --cuda --version v0.1.0
#   curl -fsSL https://raw.githubusercontent.com/starlinghq/corvid/main/install.sh | sh -s -- --app
#
# Each version goes in <prefix>/lib/corvid/<version>/, and <prefix>/bin/corvid and corvid-vault link to it. corvid
# finds corvid-sandbox next to its real path, so the sandbox is not linked. With --app the app goes in that version's app/
# directory with its own copy of corvid-sandbox, <prefix>/bin/corvid-app links to it, and a desktop entry and the icon go
# in <prefix>/share/applications and <prefix>/share/icons. Nothing outside <prefix> is touched, and no models are
# downloaded: run `corvid doctor` afterwards.
set -eu

repo="starlinghq/corvid"
version="latest"
variant=""
prefix="${CORVID_PREFIX:-$HOME/.local}"
from=""
app=""
from_app=""

usage() {
    cat <<'EOF'
Usage: install.sh [options]

  --version <tag>   a release tag such as v0.1.0 (default: the latest release)
  --cuda            the CUDA variant, for Kev on an NVIDIA GPU (needs the CUDA 13 runtime)
  --prefix <dir>    install under <dir>/lib/corvid and link into <dir>/bin (default: ~/.local, or $CORVID_PREFIX)
  --from <tarball>  install a local corvid-linux-x64[-cuda].tar.gz instead of downloading; no checksum is checked
  --app             the windowed app too (corvid-app, U-7): a desktop entry and an icon under <dir>/share
  --from-app <tarball>
                    with --app, a local corvid-app-linux-x64.tar.gz instead of downloading; no checksum is checked
  -h, --help        this text
EOF
}

die() { printf 'install.sh: %s\n' "$*" >&2; exit 1; }

while [ $# -gt 0 ]; do
    case "$1" in
        --version) [ $# -ge 2 ] || die "--version needs a tag"; version="$2"; shift 2 ;;
        --cuda) variant="-cuda"; shift ;;
        --prefix) [ $# -ge 2 ] || die "--prefix needs a directory"; prefix="$2"; shift 2 ;;
        --from) [ $# -ge 2 ] || die "--from needs a file"; from="$2"; shift 2 ;;
        --app) app=1; shift ;;
        --from-app) [ $# -ge 2 ] || die "--from-app needs a file"; from_app="$2"; app=1; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) usage >&2; die "unknown option: $1" ;;
    esac
done

# Corvid is Linux-first: the sandbox is bubblewrap, Landlock and seccomp, and Vault's master key is systemd-creds.
[ "$(uname -s)" = "Linux" ] || die "Corvid releases are Linux only for now; build from source elsewhere."
case "$(uname -m)" in
    x86_64|amd64) ;;
    *) die "Corvid releases are x86_64 only for now (this machine is $(uname -m)); build from source." ;;
esac

for tool in tar mktemp; do
    command -v "$tool" >/dev/null 2>&1 || die "$tool is required"
done

if command -v curl >/dev/null 2>&1; then
    fetch() { curl -fsSL --retry 3 -o "$2" "$1"; }
elif command -v wget >/dev/null 2>&1; then
    fetch() { wget -q -O "$2" "$1"; }
else
    fetch() { die "curl or wget is required"; }
fi

if command -v sha256sum >/dev/null 2>&1; then
    sha256() { sha256sum "$1" | cut -d' ' -f1; }
elif command -v shasum >/dev/null 2>&1; then
    sha256() { shasum -a 256 "$1" | cut -d' ' -f1; }
else
    sha256() { die "sha256sum or shasum is required to verify the download"; }
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT INT TERM

# CORVID_RELEASE_URL points at a mirror, or a local directory (file://) for testing, holding the release's assets.
if [ -n "${CORVID_RELEASE_URL:-}" ]; then
    base="$CORVID_RELEASE_URL"
elif [ "$version" = "latest" ]; then
    base="https://github.com/$repo/releases/latest/download"
else
    base="https://github.com/$repo/releases/download/$version"
fi

# Puts the release asset $1 at $work/$1: copied from the local file $2 when one is given, else downloaded and checked
# against the release's SHA256SUMS (fetched once).
obtain() {
    if [ -n "$2" ]; then
        [ -f "$2" ] || die "no such file: $2"
        cp "$2" "$work/$1"
        return
    fi
    printf 'Downloading %s (%s)\n' "$1" "$version"
    fetch "$base/$1" "$work/$1" || die "could not download $base/$1"
    if [ ! -f "$work/SHA256SUMS" ]; then
        fetch "$base/SHA256SUMS" "$work/SHA256SUMS" || die "could not download $base/SHA256SUMS"
    fi
    expected="$(awk -v a="$1" '$2 == a || $2 == "*" a { print $1 }' "$work/SHA256SUMS")"
    [ -n "$expected" ] || die "SHA256SUMS has no entry for $1"
    actual="$(sha256 "$work/$1")"
    [ "$expected" = "$actual" ] || die "checksum mismatch for $1 (expected $expected, got $actual)"
}

asset="corvid-linux-x64${variant}.tar.gz"
obtain "$asset" "$from"
# The app runs its models on the CPU: one asset serves both variants.
app_asset="corvid-app-linux-x64.tar.gz"
[ -z "$app" ] || obtain "$app_asset" "$from_app"

mkdir "$work/x"
tar -xzf "$work/$asset" -C "$work/x"
for bin in corvid corvid-vault corvid-sandbox; do
    [ -f "$work/x/$bin" ] || die "$asset has no $bin"
done
installed="$(cat "$work/x/VERSION" 2>/dev/null || true)"
[ -n "$installed" ] || die "$asset has no VERSION"
case "$installed" in */*|.*) die "$asset has an unusable VERSION: $installed" ;; esac

# U-7, U-9: the app, unpacked inside the version before anything is replaced, so a bad tarball changes nothing. It is the
# same release as the CLI or it is refused: they share the data directory and the event log's format.
if [ -n "$app" ]; then
    mkdir "$work/x/app"
    tar -xzf "$work/$app_asset" -C "$work/x/app"
    [ -f "$work/x/app/corvid-app" ] || die "$app_asset has no corvid-app"
    [ -f "$work/x/app/Assets/Icons/Svg/corvid.svg" ] || die "$app_asset has no icon (Assets/Icons/Svg/corvid.svg)"
    app_version="$(cat "$work/x/app/VERSION" 2>/dev/null || true)"
    [ "$app_version" = "$installed" ] || die "$app_asset is version ${app_version:-unknown}, not $installed; install the same release of both"
    # The app finds corvid-sandbox next to its real path, as corvid does: its own copy of the release's native one.
    cp "$work/x/corvid-sandbox" "$work/x/app/corvid-sandbox"
fi

dest="$prefix/lib/corvid/$installed$variant"
bindir="$prefix/bin"
# A corvid that is not one of our links (a hand-copied build, say) is left alone, before anything is installed.
linked="corvid corvid-vault"
[ -z "$app" ] || linked="$linked corvid-app"
for bin in $linked; do
    if [ -e "$bindir/$bin" ] && [ ! -L "$bindir/$bin" ]; then
        die "$bindir/$bin exists and is not a link; move it aside and run again"
    fi
done
mkdir -p "$prefix/lib/corvid" "$bindir"
# Replace a previous install of the same version whole, so no stale file survives.
rm -rf "$dest.new"
mv "$work/x" "$dest.new"
rm -rf "$dest"
mv "$dest.new" "$dest"
chmod 0755 "$dest/corvid" "$dest/corvid-vault" "$dest/corvid-sandbox"

for bin in corvid corvid-vault; do
    ln -sfn "$dest/$bin" "$bindir/$bin"
done

if [ -n "$app" ]; then
    chmod 0755 "$dest/app/corvid-app" "$dest/app/corvid-sandbox"
    ln -sfn "$dest/app/corvid-app" "$bindir/corvid-app"
    # The desktop entry and the icon, where the desktop looks under this prefix (XDG: ~/.local/share by default). The
    # entry names the link, so it survives the next version's install.
    share="$prefix/share"
    mkdir -p "$share/applications" "$share/icons/hicolor/scalable/apps"
    cp "$dest/app/Assets/Icons/Svg/corvid.svg" "$share/icons/hicolor/scalable/apps/corvid-app.svg"
    if [ -f "$dest/app/Assets/Icons/corvid.png" ]; then
        mkdir -p "$share/icons/hicolor/256x256/apps"
        cp "$dest/app/Assets/Icons/corvid.png" "$share/icons/hicolor/256x256/apps/corvid-app.png"
    fi
    desktop="$share/applications/corvid-app.desktop"
    cat > "$desktop.new" <<EOF
[Desktop Entry]
Type=Application
Version=1.5
Name=Corvid
GenericName=Coding agent
Comment=The windowed Corvid: conversations, approvals, review and the rest of the agent's evidence
Exec="$bindir/corvid-app"
TryExec=$bindir/corvid-app
Icon=$share/icons/hicolor/scalable/apps/corvid-app.svg
Terminal=false
Categories=Development;
StartupWMClass=com.starlinghq.corvid
EOF
    chmod 0644 "$desktop.new"
    mv "$desktop.new" "$desktop"
fi

# v0.1.0's installer put versions in <prefix>/share/corvid/<version>/, which is also Corvid's data directory. Remove those
# old installs, now that nothing links to them. A directory goes only if it holds exactly the four files a release
# tarball unpacks to, so Corvid's own data (checkpoints, the event log) is never touched.
for old in "$prefix/share/corvid"/*/; do
    [ -d "$old" ] && [ ! -L "${old%/}" ] || continue
    # shellcheck disable=SC2012 # compared against four fixed names, so ls is exact enough here.
    [ "$(ls -A "$old" | LC_ALL=C sort | tr '\n' ' ')" = "VERSION corvid corvid-sandbox corvid-vault " ] || continue
    if ! rm -rf "$old"; then printf 'Could not remove the old install in %s; remove it by hand\n' "${old%/}" >&2; continue; fi
    printf 'Removed the old install in %s\n' "${old%/}"
done

printf 'Installed Corvid %s%s in %s\n' "$installed" "$variant" "$dest"
# The first run extracts the bundled ONNX Runtime natives, so it takes a moment.
"$bindir/corvid" --version
if [ -n "$app" ]; then
    printf 'Installed the windowed app as %s, with its desktop entry in %s\n' "$bindir/corvid-app" "$desktop"
    [ -z "$variant" ] || printf 'The app runs its models on the CPU; corvid itself uses the CUDA variant.\n'
fi

# shellcheck disable=SC2016 # $PATH is printed for the user to paste, not expanded here.
case ":$PATH:" in
    *":$bindir:"*) ;;
    *) printf '\n%s is not on PATH. Add it, for example:\n  export PATH="%s:$PATH"\n' "$bindir" "$bindir" ;;
esac

command -v bwrap >/dev/null 2>&1 || printf '\nbubblewrap (bwrap) is not installed; shell tools refuse to run without it.\n'

cat <<'EOF'

Next:
  corvid doctor                     # what is missing, and how to fix it
  corvid model download qwen3.5-0.8b gliner2-base-v1 embeddinggemma-2 kev-0.8b
  corvid chat
EOF
if [ -n "$app" ]; then
    printf '  corvid-app                        # the windowed app, or Corvid in the applications menu\n'
else
    printf '\nThe windowed app installs with: install.sh --app\n'
fi
