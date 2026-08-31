#!/usr/bin/env bash
set -euo pipefail

# Cloud Agent environment bootstrap for Distant Vistas.
#
# Building this repo needs two things that are not in the tree:
#   1. the .NET 10 SDK, and
#   2. a Vintage Story game install, for the non-redistributable game assemblies
#      the mod and its check harness reference (VintagestoryAPI.dll, etc.).
#
# The game binaries themselves are a free public download from the official CDN;
# only *playing* (a client joining a server) needs a paid account. So this script
# can fetch them, which is enough to build the mod and run the game-less `fast`
# check tier and the headless dedicated server. See README.md "Building".
#
# Idempotent: safe to re-run. Each step is skipped when its output already exists.

DOTNET_VERSION_CHANNEL="10.0"
DOTNET_DIR="/usr/share/dotnet"       # matches scripts/*.sh DOTNET_ROOT default
VS_VERSION="1.22.5"                  # modinfo.json minimum; README-supported
GAME_DIR="$HOME/Games/vintagestory${VS_VERSION}"  # matches *.csproj GamePath default

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# sudo only if we are not already root and it exists.
SUDO=""
if [[ "$(id -u)" -ne 0 ]]; then
    command -v sudo >/dev/null 2>&1 && SUDO="sudo"
fi

# --- 0. Headless graphics stack --------------------------------------------
# The `smoke`/`matrix` check tiers launch the real graphical client, which needs
# a virtual display (Xvfb) and Mesa's software GL driver (no GPU on the VM).
# x11-utils provides xdpyinfo, which .cursor/run-smoke.sh probes for a display.
GL_PKGS=(xvfb libgl1-mesa-dri x11-utils)
missing=()
for pkg in "${GL_PKGS[@]}"; do
    dpkg -s "$pkg" >/dev/null 2>&1 || missing+=("$pkg")
done
if [[ ${#missing[@]} -gt 0 ]]; then
    echo "install: installing headless-GL packages: ${missing[*]}"
    $SUDO apt-get update -qq
    $SUDO DEBIAN_FRONTEND=noninteractive apt-get install -y -qq "${missing[@]}"
else
    echo "install: headless-GL packages already present"
fi

# --- 1. .NET 10 SDK ---------------------------------------------------------
if "$DOTNET_DIR/dotnet" --list-sdks 2>/dev/null | grep -q '^10\.'; then
    echo "install: .NET 10 SDK already present at $DOTNET_DIR"
else
    echo "install: installing .NET $DOTNET_VERSION_CHANNEL SDK to $DOTNET_DIR"
    tmp_script="$(mktemp)"
    curl -fSL https://dot.net/v1/dotnet-install.sh -o "$tmp_script"
    chmod +x "$tmp_script"
    $SUDO "$tmp_script" --channel "$DOTNET_VERSION_CHANNEL" --install-dir "$DOTNET_DIR"
    rm -f "$tmp_script"
fi
# Put dotnet on PATH for interactive shells and the game's own launch scripts.
$SUDO ln -sf "$DOTNET_DIR/dotnet" /usr/local/bin/dotnet
export DOTNET_ROOT="$DOTNET_DIR"
export PATH="$DOTNET_DIR:$PATH"

# --- 2. Vintage Story game files -------------------------------------------
if [[ -f "$GAME_DIR/VintagestoryAPI.dll" ]]; then
    echo "install: Vintage Story $VS_VERSION already present at $GAME_DIR"
else
    echo "install: downloading Vintage Story $VS_VERSION game files"
    mkdir -p "$GAME_DIR"
    tarball="$(mktemp --suffix=.tar.gz)"
    # The client tarball bundles the dedicated server too (VintagestoryServer.dll),
    # so one download covers building, the fast checks, and headless server runs.
    curl -fSL \
        "https://cdn.vintagestory.at/gamefiles/stable/vs_client_linux-x64_${VS_VERSION}.tar.gz" \
        -o "$tarball"
    # The archive unpacks into a top-level vintagestory/ dir; flatten it so the
    # DLLs sit directly in $GAME_DIR (the csproj GamePath default).
    tmp_extract="$(mktemp -d)"
    tar -xzf "$tarball" -C "$tmp_extract"
    shopt -s dotglob
    mv "$tmp_extract/vintagestory/"* "$GAME_DIR/"
    shopt -u dotglob
    rm -rf "$tmp_extract" "$tarball"
    echo "install: extracted Vintage Story to $GAME_DIR"
fi

# --- 3. Verify the toolchain by building the mod ----------------------------
echo "install: building DistantVistas to verify the toolchain"
cd "$REPO_ROOT"
dotnet build DistantVistas -v quiet --nologo

echo "install: done"
