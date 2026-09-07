#!/usr/bin/env bash
# Build a Release configuration and package the mod as a ModDB-ready zip.
set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_DIR"

VERSION=$(grep -oP '"version":\s*"\K[^"]+' DistantVistas/modinfo.json)
OUT="$REPO_DIR/dist"
MOD_DIR="DistantVistas/bin/Release/net10.0/Mods/distantvistas"

dotnet build DistantVistas -c Release

mkdir -p "$OUT"
ZIP="$OUT/distantvistas_${VERSION}.zip"
rm -f "$ZIP"

# ModDB zips contain the mod files at the archive root (no wrapping folder),
# and never the game's own DLLs (all references are Private=false).
# Always use forward-slash zip entry names (Linux / unzip / VS asset loader).
python3 - "$MOD_DIR" "$ZIP" <<'EOF'
import os, sys, zipfile
mod_dir, zip_path = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk(mod_dir):
        for f in files:
            if f.endswith(".pdb"):
                continue
            full = os.path.join(root, f)
            # Windows os.path.relpath uses '\'; zip entries must use '/'.
            arcname = os.path.relpath(full, mod_dir).replace("\\", "/")
            z.write(full, arcname)
    print("packaged:", zip_path)
    for info in z.infolist():
        print(f"  {info.file_size:>9}  {info.filename}")
EOF
