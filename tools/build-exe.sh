#!/usr/bin/env bash
# Entwickler-Skript: baut das Programm als eine einzige, eigenstaendige Datei (self-contained, single file).
#
#   tools/build-exe.sh              -> dist/win-x64/Berichtsheft.exe      (Standard: Windows-.exe)
#   tools/build-exe.sh all          -> alle Plattformen (win-x64 win-arm64)
#   tools/build-exe.sh win-x64 --zip  -> zusaetzlich dist/Berichtsheft-win-x64.zip
#
# Die Oberflaeche ist eine Windows-Forms-Anwendung (nur Windows). Sie kann aber auch unter Linux/macOS gebaut werden.
# Benoetigt das .NET 8 SDK und beim ersten Mal Internet (Laufzeit-Pakete).
set -euo pipefail
cd "$(dirname "$0")/.."

ALL="win-x64 win-arm64"
TARGET="${1:-win-x64}"
ZIP=0
for a in "$@"; do [ "$a" = "--zip" ] && ZIP=1; done
[ "$TARGET" = "--zip" ] && TARGET="win-x64"
[ "$TARGET" = "all" ] && TARGET="$ALL"

command -v dotnet >/dev/null 2>&1 || { echo "Das .NET 8 SDK wurde nicht gefunden: https://dotnet.microsoft.com/download/dotnet/8.0" >&2; exit 1; }

for RID in $TARGET; do
  OUT="dist/$RID"
  echo
  echo "=== Baue Berichtsheft fuer $RID ==="
  rm -rf "$OUT"
  dotnet publish src/Berichtsheft.Desktop/Berichtsheft.Desktop.csproj -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none -p:DebugSymbols=false -o "$OUT"
  cp README.md "$OUT/"
  EXE="Berichtsheft.exe"
  echo "  -> $OUT/$EXE ($(du -h "$OUT/$EXE" | cut -f1))"
  if [ "$ZIP" = 1 ]; then
    ( cd dist && rm -f "Berichtsheft-$RID.zip" && \
      if command -v zip >/dev/null 2>&1; then zip -qr "Berichtsheft-$RID.zip" "$RID"; else python3 -m zipfile -c "Berichtsheft-$RID.zip" "$RID"; fi )
    echo "  -> dist/Berichtsheft-$RID.zip"
  fi
done
