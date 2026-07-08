#!/bin/bash
# Refresh the distributable bundles with the current EightPlayers build.
# The zips are full drop-in BepInEx installs; this replaces just the mod DLL.
set -euo pipefail
cd "$(dirname "$0")/.."

DLL=EightPlayers/bin/Release/net472/EightPlayers.dll
[ -f "$DLL" ] || DLL=EightPlayers/bin/Debug/net472/EightPlayers.dll
[ -f "$DLL" ] || { echo "build EightPlayers first"; exit 1; }
echo "packaging $DLL"

STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$STAGE/BepInEx/plugins"
cp "$DLL" "$STAGE/BepInEx/plugins/EightPlayers.dll"

for z in dist/SoR-EightPlayers-Linux.zip dist/SoR-EightPlayers-Windows.zip; do
  (cd "$STAGE" && zip -q "$OLDPWD/$z" BepInEx/plugins/EightPlayers.dll)
  echo "updated $z"
done
cp "$DLL" dist/EightPlayers.dll
echo "updated dist/EightPlayers.dll"
