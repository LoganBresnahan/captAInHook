#!/bin/sh
# release/package.sh — build ONE release archive for ONE target (ADR-0020 d1).
#
#   release/package.sh <version> <rid> [outdir]
#
#   rid     linux-x64 | linux-arm64 | osx-arm64 | osx-x64 — must be THIS host's
#           OS: Native AOT does not cross operating systems, and the shim and
#           the DLLs of one archive must come from one build (the shim's skew
#           guard compares the wire MVID it linked against the DLL beside it).
#   outdir  default ./dist
#
# Produces <outdir>/captainhook-<version>-<os>-<arch>.tar.gz holding one
# top-level directory of the same name: captainShim, captainHook, the four
# loose app DLLs, ui/, bosun (pinned + verified), payloads/, LICENSE, NOTICE,
# VERSION. stdout carries ONLY the archive's path (build noise goes to stderr),
# so `install.sh --archive "$(release/package.sh …)"` works.
#
# Needs: the .NET 10 SDK, the Native AOT toolchain (clang on Linux, Xcode CLT
# on macOS), curl, and sha256sum or shasum.
set -eu

[ $# -ge 2 ] || { echo "usage: $0 <version> <rid> [outdir]" >&2; exit 2; }
VERSION=$1
RID=$2
OUT=${3:-dist}

ROOT=$(cd "$(dirname "$0")/.." && pwd)
mkdir -p "$OUT"
OUT=$(cd "$OUT" && pwd)

case $RID in
  linux-x64)   OS=linux  ARCH=x64   BOSUN_TARGET=x86_64-linux ;;
  linux-arm64) OS=linux  ARCH=arm64 BOSUN_TARGET=aarch64-linux ;;
  osx-arm64)   OS=darwin ARCH=arm64 BOSUN_TARGET=aarch64-macos ;;
  osx-x64)     OS=darwin ARCH=x64   BOSUN_TARGET=x86_64-macos ;;
  *) echo "package.sh: unsupported rid '$RID'" >&2; exit 2 ;;
esac

# SemVer build metadata (+…) names the archive and its folder but is NOT
# stamped into the assemblies: a dev build named 0.1.0-dev+<sha> must compute
# the same identity as the previous commit's when the code did not change, or
# every docs-only deploy would cost a cold hook (ADR-0004 d3).
STAMP=${VERSION%%+*}
NAME=captainhook-$VERSION-$OS-$ARCH
STAGE=$OUT/$NAME
rm -rf "$STAGE" "$OUT/$NAME.tar.gz"

sha256() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1
  else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

# ---- bosun: the pinned tag, verified against the digests in THIS repo --------
PIN=$ROOT/release/bosun.sha256
BOSUN_TAG=$(sed -n 's/^# tag: \(v[0-9.]*\).*/\1/p' "$PIN")
BOSUN_SHA=$(awk -v f="bosun-$BOSUN_TARGET" '$2 == f { print $1 }' "$PIN")
[ -n "$BOSUN_TAG" ] && [ -n "$BOSUN_SHA" ] || { echo "package.sh: no bosun pin for $BOSUN_TARGET in $PIN" >&2; exit 1; }
BOSUN_CACHE=${BOSUN_CACHE:-${XDG_CACHE_HOME:-$HOME/.cache}/captainhook-bosun/$BOSUN_TAG}
BOSUN_BIN=$BOSUN_CACHE/bosun-$BOSUN_TARGET
if [ ! -f "$BOSUN_BIN" ] || [ "$(sha256 "$BOSUN_BIN")" != "$BOSUN_SHA" ]; then
  mkdir -p "$BOSUN_CACHE"
  curl -fsSL -o "$BOSUN_BIN.part" \
    "https://github.com/LoganBresnahan/bosun/releases/download/$BOSUN_TAG/bosun-$BOSUN_TARGET"
  mv "$BOSUN_BIN.part" "$BOSUN_BIN"
fi
GOT=$(sha256 "$BOSUN_BIN")
if [ "$GOT" != "$BOSUN_SHA" ]; then
  echo "package.sh: bosun-$BOSUN_TARGET checksum MISMATCH (got $GOT, pinned $BOSUN_SHA) — refusing to package" >&2
  exit 1
fi

# ---- the two executables, one build -----------------------------------------
# Single-file self-contained engine (ADR-0012 d2); the csproj keeps the four
# app assemblies LOOSE beside it, because identity and the skew guard read them.
dotnet publish "$ROOT/dotnet/captainHook/captainHook.csproj" -c Release -r "$RID" \
  --self-contained -p:PublishSingleFile=true -p:Version="$STAMP" -o "$STAGE" -v q -nologo >&2
SHIM_OUT=$OUT/.shim-$RID
rm -rf "$SHIM_OUT"
dotnet publish "$ROOT/dotnet/captainShim/captainShim.csproj" -c Release -r "$RID" \
  -p:Version="$STAMP" -o "$SHIM_OUT" -v q -nologo >&2
cp "$SHIM_OUT/captainShim" "$STAGE/captainShim"
rm -rf "$SHIM_OUT"

for f in captainShim captainHook captainHook.dll captainHookWire.dll captainHookActors.dll FSharp.Core.dll; do
  [ -f "$STAGE/$f" ] || { echo "package.sh: $f missing from the publish — the loose-DLL target did not run?" >&2; exit 1; }
done

# ---- the rest of the archive ------------------------------------------------
install -m 0755 "$BOSUN_BIN" "$STAGE/bosun"
cp -R "$ROOT/ui" "$STAGE/ui"
[ -f "$STAGE/ui/index.html" ] || { echo "package.sh: ui/index.html missing — is the committed ui/ built?" >&2; exit 1; }

# Shipped payloads stay FLAT (ADR-0020 d6): the installer links each file
# individually, so a payload's $(dirname "$0") sees only linked siblings. The
# mail-bus payloads stay in the repo while the bus is frozen (ADR-0016).
mkdir -p "$STAGE/payloads"
for p in "$ROOT"/examples/payloads/*.sh "$ROOT/examples/payloads/README.md"; do
  case $(basename "$p") in
    starter-mail-*|reaper-deterministic.sh|turn-claude.sh) continue ;;
  esac
  cp "$p" "$STAGE/payloads/"
done
chmod 0755 "$STAGE"/payloads/*.sh

cp "$ROOT/LICENSE" "$ROOT/NOTICE" "$STAGE/"
printf '%s\n' "$VERSION" > "$STAGE/VERSION"

# macOS arm64 kills an unsigned or invalidly signed Mach-O on exec. The AOT
# linker and the SDK's apphost step sign ad hoc; verify, and re-sign ad hoc
# only what fails (anything stripped or patched after linking).
if [ "$OS" = darwin ]; then
  for b in captainShim captainHook bosun; do
    codesign -v "$STAGE/$b" 2>/dev/null || codesign -s - -f "$STAGE/$b"
    codesign -v "$STAGE/$b"
  done
fi

# COPYFILE_DISABLE: bsdtar on macOS would otherwise add AppleDouble ._ files.
COPYFILE_DISABLE=1 tar -C "$OUT" -czf "$OUT/$NAME.tar.gz" "$NAME"
rm -rf "$STAGE"
echo "package.sh: $NAME.tar.gz  sha256 $(sha256 "$OUT/$NAME.tar.gz")" >&2
echo "$OUT/$NAME.tar.gz"
