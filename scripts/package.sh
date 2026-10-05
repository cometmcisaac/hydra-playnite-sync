#!/usr/bin/env bash
# Builds HydraSync and produces dist/HydraSync-<version>.pext
# .pext = plain zip; extension.yaml MUST be at archive root (Playnite ExtensionInstaller).
# Never include Playnite*.dll (rejected by VerifyExtensionPackage) or *.pdb.
set -euo pipefail

export DOTNET_ROOT="/opt/homebrew/opt/dotnet/libexec"
export PATH="$DOTNET_ROOT/bin:$PATH"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJ="$ROOT/src/HydraSync/HydraSync.csproj"
OUT="$ROOT/src/HydraSync/bin/Debug"
DIST="$ROOT/dist"

dotnet build "$PROJ" -v q

VERSION="$(sed -n 's/^Version: *//p' "$ROOT/src/HydraSync/extension.yaml" | tr -d '[:space:]')"
PACKAGE="$DIST/HydraSync-$VERSION.pext"

mkdir -p "$DIST"
rm -f "$PACKAGE"

cd "$OUT"
zip -9 -X "$PACKAGE" \
  extension.yaml \
  icon.png \
  HydraSync.dll \
  LevelDb.Managed.dll \
  Snappier.dll \
  Newtonsoft.Json.dll \
  System.Buffers.dll \
  System.Memory.dll \
  System.Numerics.Vectors.dll \
  System.Runtime.CompilerServices.Unsafe.dll

echo "Built $PACKAGE"
unzip -l "$PACKAGE"
