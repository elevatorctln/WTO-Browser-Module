#!/usr/bin/env bash
# local script to build BrowserServerModule and copy to KSA mods folder.
set -euo pipefail

MODS_ROOT="${KSA_MODS_DIR:-$HOME/Documents/My Games/Kitten Space Agency/mods}"
SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MODULE_ID="BrowserServerModule"
OUT_MODULE="$SRC/bin/Debug/net10.0"

TARGET_MOD_DIRS=("$MODS_ROOT")
for borea_mods in "$HOME/.local/share/Borea/Instances"/*/mods; do
    if [ -d "$borea_mods" ]; then
        TARGET_MOD_DIRS+=("$borea_mods")
    fi
done

echo "=== Building $MODULE_ID ==="
dotnet build "$SRC/$MODULE_ID.csproj" -c Debug "$@"

for dest_root in "${TARGET_MOD_DIRS[@]}"; do
    DEST_MODULE="$dest_root/$MODULE_ID"
    mkdir -p "$DEST_MODULE"
    rm -rf "$DEST_MODULE/bin" "$DEST_MODULE/obj"
    rm -f  "$DEST_MODULE"/*.cs "$DEST_MODULE"/*.csproj

    install -m 644 "$OUT_MODULE/$MODULE_ID.dll"       "$DEST_MODULE/"
    install -m 644 "$OUT_MODULE/$MODULE_ID.deps.json" "$DEST_MODULE/"
    install -m 644 "$SRC/mod.toml"                    "$DEST_MODULE/"
    install -m 644 "$SRC/LICENSE.TXT"                 "$DEST_MODULE/"
    if [ -f "$SRC/../Import/0Harmony.dll" ]; then
        install -m 644 "$SRC/../Import/0Harmony.dll" "$DEST_MODULE/"
    fi

    # Web overlay for OBS browser source
    mkdir -p "$DEST_MODULE/web/css" "$DEST_MODULE/web/js"
    cp -r "$SRC/web/"* "$DEST_MODULE/web/"

    echo "Installed $MODULE_ID to: $DEST_MODULE"
done
echo
echo "Ensure manifest.toml contains:"
echo "  [[mods]]"
echo "  id = \"$MODULE_ID\""
echo "  enabled = true"
