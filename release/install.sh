#!/bin/sh
# captAInHook installer (ADR-0020 d3) — POSIX sh, no sudo, fails closed.
#
#   curl -fsSL https://github.com/LoganBresnahan/captAInHook/releases/latest/download/install.sh | sh
#   curl -fsSL …/install.sh | sh -s -- --version 0.1.0
#
# Options:
#   --version <v>    install this release (default: the latest)
#   --archive <tgz>  install a local archive built by release/package.sh
#                    (verified against <tgz>.sha256 or a SHA256SUMS beside it, when present)
#   --force          reinstall even if this version is already current
#   --rollback       switch back to the previous version (again to return)
#   --prune          remove every version but the current and the previous one
#                    (an install does this too); downloads nothing
#   --uninstall      remove the installed versions and links (config is kept)
#   -h | --help
#
# Environment:
#   CAPTAINHOOK_INSTALL_DIR  default ${XDG_DATA_HOME:-$HOME/.local/share}/captainhook
#   CAPTAINHOOK_BIN_DIR      default $HOME/.local/bin
#   CAPTAINHOOK_RELEASE_URL  default https://github.com/LoganBresnahan/captAInHook/releases
#
# Layout (ADR-0020 d2):
#   $CAPTAINHOOK_INSTALL_DIR/versions/<v>/   one unpacked archive per version,
#                                            with this script as install.sh
#   $CAPTAINHOOK_INSTALL_DIR/payloads/<f>    file symlinks into the current version
#   $CAPTAINHOOK_INSTALL_DIR/previous        the version before the last switch
#   $CAPTAINHOOK_BIN_DIR/captainShim, captainHook   file symlinks into the current version
#
# This script never edits shell rc files, ~/.claude/settings.json, or
# ~/.captainHook. Wiring the hooks is a separate, explicit step:
#   captainHook install-hooks          (prints the diff it would make)
#   captainHook install-hooks --apply  (writes it, after a backup)
#
# Once installed, `captainHook self update|rollback|prune|uninstall` runs this
# script for you (ADR-0020 d8).
set -eu

APP=captainhook
RELEASES=${CAPTAINHOOK_RELEASE_URL:-https://github.com/LoganBresnahan/captAInHook/releases}
INSTALL_DIR=${CAPTAINHOOK_INSTALL_DIR:-${XDG_DATA_HOME:-$HOME/.local/share}/captainhook}
BIN_DIR=${CAPTAINHOOK_BIN_DIR:-$HOME/.local/bin}
ENTRIES="captainShim captainHook"

say() { printf 'captainhook: %s\n' "$*"; }
# Inline, not read back from "$0": under `curl … | sh` there is no script file.
usage() {
  cat <<'USAGE'
captAInHook installer
  install.sh [--version <v>] [--archive <tgz>] [--force] | --rollback | --prune | --uninstall
    --version <v>    install this release (default: the latest)
    --archive <tgz>  install a local archive built by release/package.sh
    --force          reinstall even if this version is already current
    --rollback       switch back to the previous version (and again to return)
    --prune          remove every version but the current and the previous one
                     (an install does this too); downloads nothing
    --uninstall      remove the installed versions and links (config is kept)
  env: CAPTAINHOOK_INSTALL_DIR (default ${XDG_DATA_HOME:-~/.local/share}/captainhook)
       CAPTAINHOOK_BIN_DIR     (default ~/.local/bin)
USAGE
}
die() { printf 'captainhook: error: %s\n' "$*" >&2; exit 1; }

WANT_VERSION=""
ARCHIVE=""
FORCE=0
UNINSTALL=0
ROLLBACK=0
PRUNE=0
while [ $# -gt 0 ]; do
  case $1 in
    --version) [ $# -ge 2 ] || die "--version needs a value"; WANT_VERSION=${2#v}; shift 2 ;;
    --archive) [ $# -ge 2 ] || die "--archive needs a path"; ARCHIVE=$2; shift 2 ;;
    --force) FORCE=1; shift ;;
    --uninstall) UNINSTALL=1; shift ;;
    --rollback) ROLLBACK=1; shift ;;
    --prune) PRUNE=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) die "unknown option '$1' (try --help)" ;;
  esac
done

# ---- platform ----------------------------------------------------------------
case $(uname -s) in
  Linux) OS=linux ;;
  Darwin) OS=darwin ;;
  *) die "unsupported OS '$(uname -s)' — captAInHook targets Linux and macOS (WSL2 on Windows)" ;;
esac
case $(uname -m) in
  x86_64|amd64) ARCH=x64 ;;
  aarch64|arm64) ARCH=arm64 ;;
  *) die "unsupported architecture '$(uname -m)'" ;;
esac
# A shell under Rosetta reports x86_64 on an Apple Silicon Mac: install native.
if [ "$OS" = darwin ] && [ "$ARCH" = x64 ] && [ "$(sysctl -n sysctl.proc_translated 2>/dev/null || echo 0)" = 1 ]; then
  ARCH=arm64
fi

sha256() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1
  elif command -v shasum >/dev/null 2>&1; then shasum -a 256 "$1" | cut -d' ' -f1
  else die "need sha256sum or shasum to verify the download"; fi
}

fetch() {  # fetch <url> <dest>
  if command -v curl >/dev/null 2>&1; then curl -fsSL --retry 3 --connect-timeout 20 -o "$2" "$1"
  elif command -v wget >/dev/null 2>&1; then wget -q -O "$2" "$1"
  else die "need curl or wget"; fi
}

# The version a bin link currently points into, or empty.
current_version() {
  t=$(readlink "$BIN_DIR/captainHook" 2>/dev/null) || { echo ""; return; }
  case $t in
    "$INSTALL_DIR"/versions/*/captainHook) t=${t#"$INSTALL_DIR"/versions/}; echo "${t%/captainHook}" ;;
    *) echo "" ;;
  esac
}

# ln -s + rename(2): a FILE symlink switched atomically under GNU and BSD mv
# alike. Never a directory symlink — there is no portable atomic replace.
switch_link() {  # switch_link <target> <link>
  tmp="$(dirname "$2")/.$(basename "$2").$$.tmp"
  rm -f "$tmp"
  ln -s "$1" "$tmp"
  mv -f "$tmp" "$2"
}

HANDLERS=${CAPTAINHOOK_HANDLERS_FILE:-$HOME/.captainHook/handlers.json}
SETTINGS=$HOME/.claude/settings.json

# ---- uninstall ----------------------------------------------------------------
if [ "$UNINSTALL" = 1 ]; then
  if [ -f "$SETTINGS" ] && grep -q "$BIN_DIR/captainShim hook" "$SETTINGS" 2>/dev/null && [ "$FORCE" = 0 ]; then
    die "$SETTINGS still runs $BIN_DIR/captainShim — remove the hooks first:
    captainHook install-hooks --remove --apply
  then re-run --uninstall (or pass --force to leave them dangling)"
  fi
  for e in $ENTRIES; do
    t=$(readlink "$BIN_DIR/$e" 2>/dev/null) || continue
    case $t in "$INSTALL_DIR"/versions/*) rm -f "$BIN_DIR/$e"; say "removed $BIN_DIR/$e" ;; esac
  done
  if [ -d "$INSTALL_DIR/payloads" ]; then
    for l in "$INSTALL_DIR"/payloads/*; do
      [ -L "$l" ] || continue
      case $(readlink "$l") in "$INSTALL_DIR"/versions/*) rm -f "$l" ;; esac
    done
    rmdir "$INSTALL_DIR/payloads" 2>/dev/null || say "kept $INSTALL_DIR/payloads (it holds files of yours)"
  fi
  rm -rf "$INSTALL_DIR/versions" "$INSTALL_DIR/previous"
  rmdir "$INSTALL_DIR" 2>/dev/null || true
  say "uninstalled. Kept your config and logs in ~/.captainHook (delete it by hand if you want it gone)."
  say "a daemon still running exits by itself after its idle window."
  exit 0
fi

# Point every link at one installed version: the two bin entries, then one
# FILE symlink per shipped payload (flat — ADR-0020 d6), dropping payload links
# the version no longer ships. Refuses to clobber anything that is not ours.
activate() {  # activate <version-dir> <version>
  mkdir -p "$BIN_DIR"
  for e in $ENTRIES; do
    if [ -e "$BIN_DIR/$e" ] || [ -L "$BIN_DIR/$e" ]; then
      if [ ! -L "$BIN_DIR/$e" ]; then die "$BIN_DIR/$e exists and is not a symlink — not overwriting it"; fi
      case $(readlink "$BIN_DIR/$e") in
        "$INSTALL_DIR"/versions/*) ;;
        *) [ "$FORCE" = 1 ] || die "$BIN_DIR/$e points elsewhere ($(readlink "$BIN_DIR/$e")) — pass --force to take it over" ;;
      esac
    fi
  done
  for e in $ENTRIES; do switch_link "$1/$e" "$BIN_DIR/$e"; done

  mkdir -p "$INSTALL_DIR/payloads"
  for f in "$1"/payloads/*; do
    [ -f "$f" ] || continue
    l=$INSTALL_DIR/payloads/$(basename "$f")
    if [ -e "$l" ] && [ ! -L "$l" ]; then say "kept your own file $l (not replacing it with the shipped one)"; continue; fi
    switch_link "$f" "$l"
  done
  for l in "$INSTALL_DIR"/payloads/*; do
    [ -L "$l" ] || continue
    case $(readlink "$l") in "$INSTALL_DIR"/versions/*) ;; *) continue ;; esac
    [ -f "$1/payloads/$(basename "$l")" ] && continue
    if [ -f "$HANDLERS" ] && grep -q "$l" "$HANDLERS" 2>/dev/null; then
      say "warning: $HANDLERS still names $l, which $2 no longer ships — that handler will fail to start"
    fi
    rm -f "$l"
  done
}

# Retention (ADR-0020 d3): keep the current and the previous version; remove
# the rest, and leftovers of an install that stopped half-way. A daemon running
# from a removed folder keeps its unlinked inodes until doctor drains it. The
# same lines as geex's and pharos's install.sh.
prune() {  # prune <current>
  keep_previous=$(cat "$INSTALL_DIR/previous" 2>/dev/null || echo "")
  for d in "$INSTALL_DIR"/versions/* "$INSTALL_DIR"/versions/.tmp-*; do
    [ -d "$d" ] || continue
    v=$(basename "$d")
    if [ "$v" = "$1" ]; then say "kept $v (current)"; continue; fi
    if [ -n "$keep_previous" ] && [ "$v" = "$keep_previous" ]; then say "kept $v (previous)"; continue; fi
    case $v in .tmp-*-$$) continue ;; esac
    rm -rf "$d"
    case $v in .*) ;; *) say "removed $v" ;; esac
  done
}

# ---- prune ---------------------------------------------------------------------
# No current version (no link, or one into another layout) is nothing to prune:
# the current one is what decides what stays.
if [ "$PRUNE" = 1 ]; then
  CUR=$(current_version)
  if [ -z "$CUR" ]; then say "no captAInHook installed at $BIN_DIR/captainHook; nothing to prune"; exit 0; fi
  prune "$CUR"
  "$INSTALL_DIR/versions/$CUR/captainHook" doctor </dev/null 2>/dev/null | grep -v 'nothing to reap' | sed 's/^/  /' || true
  exit 0
fi

# ---- rollback ------------------------------------------------------------------
if [ "$ROLLBACK" = 1 ]; then
  OLD=$(current_version)
  PREV=$(cat "$INSTALL_DIR/previous" 2>/dev/null || echo "")
  if [ -z "$PREV" ] || [ ! -f "$INSTALL_DIR/versions/$PREV/VERSION" ]; then die "no previous version is installed to roll back to"; fi
  "$INSTALL_DIR/versions/$PREV/captainHook" version </dev/null >/dev/null 2>&1 \
    || die "$INSTALL_DIR/versions/$PREV/captainHook does not run here — links left unchanged"
  activate "$INSTALL_DIR/versions/$PREV" "$PREV"
  [ -n "$OLD" ] && printf '%s\n' "$OLD" > "$INSTALL_DIR/previous"
  say "rolled back to $PREV; $OLD is kept as the previous version (--rollback again returns to it)"
  "$INSTALL_DIR/versions/$PREV/captainHook" doctor </dev/null 2>/dev/null | grep -v 'nothing to reap' | sed 's/^/  /' || true
  exit 0
fi

# ---- which archive -----------------------------------------------------------
TMP=$(mktemp -d 2>/dev/null || mktemp -d -t captainhook)
trap 'rm -rf "$TMP"' EXIT INT TERM

if [ -n "$ARCHIVE" ]; then
  [ -f "$ARCHIVE" ] || die "no such archive: $ARCHIVE"
  base=$(basename "$ARCHIVE")
  case $base in
    "$APP"-*-"$OS"-"$ARCH".tar.gz) ;;
    "$APP"-*.tar.gz) die "$base is not for this machine ($OS-$ARCH)" ;;
    *) die "$base does not look like a captAInHook release archive ($APP-<version>-$OS-$ARCH.tar.gz)" ;;
  esac
  VERSION=${base#"$APP"-}
  VERSION=${VERSION%-"$OS"-"$ARCH".tar.gz}
  expected=""
  if [ -f "$ARCHIVE.sha256" ]; then expected=$(cut -d' ' -f1 < "$ARCHIVE.sha256")
  elif [ -f "$(dirname "$ARCHIVE")/SHA256SUMS" ]; then
    expected=$(awk -v f="$base" '$2 == f || $2 == "*"f { print $1 }' "$(dirname "$ARCHIVE")/SHA256SUMS")
  fi
  if [ -n "$expected" ]; then
    [ "$(sha256 "$ARCHIVE")" = "$expected" ] || die "checksum MISMATCH for $base — not installing"
    say "verified $base"
  else
    say "local archive $base has no checksum beside it — installing as given"
  fi
  SRC=$ARCHIVE
else
  if [ -n "$WANT_VERSION" ]; then sums_url=$RELEASES/download/v$WANT_VERSION/SHA256SUMS
  else sums_url=$RELEASES/latest/download/SHA256SUMS; fi
  fetch "$sums_url" "$TMP/SHA256SUMS" || die "could not download $sums_url"
  # The asset name carries the version: captainhook-<v>-<os>-<arch>.tar.gz
  line=$(grep " \*\{0,1\}$APP-.*-$OS-$ARCH\.tar\.gz\$" "$TMP/SHA256SUMS" | head -1 || true)
  [ -n "$line" ] || die "the release has no archive for $OS-$ARCH"
  expected=${line%% *}
  base=${line##* }
  base=${base#\*}
  VERSION=${base#"$APP"-}
  VERSION=${VERSION%-"$OS"-"$ARCH".tar.gz}
  [ -z "$WANT_VERSION" ] || [ "$VERSION" = "$WANT_VERSION" ] || die "SHA256SUMS for v$WANT_VERSION names $base"
fi

VDIR=$INSTALL_DIR/versions/$VERSION
OLD=$(current_version)

if [ "$OLD" = "$VERSION" ] && [ -f "$VDIR/VERSION" ] && [ "$FORCE" = 0 ]; then
  say "captAInHook $VERSION is already installed and current — nothing to do (--force reinstalls)"
  exit 0
fi

# ---- unpack (verified bytes only) ---------------------------------------------
mkdir -p "$INSTALL_DIR/versions"
if [ "$FORCE" = 1 ] || [ ! -f "$VDIR/VERSION" ]; then
  if [ -z "$ARCHIVE" ]; then
    say "downloading $base"
    fetch "$RELEASES/download/v$VERSION/$base" "$TMP/$base" || die "could not download $base"
    got=$(sha256 "$TMP/$base")
    [ "$got" = "$expected" ] || die "checksum MISMATCH for $base (got $got, expected $expected) — not installing"
    say "verified $base"
    SRC=$TMP/$base
  fi
  UNPACK=$INSTALL_DIR/versions/.tmp-$VERSION-$$
  rm -rf "$UNPACK"
  mkdir -p "$UNPACK"
  tar -xzf "$SRC" -C "$UNPACK"
  [ -f "$UNPACK/$APP-$VERSION-$OS-$ARCH/VERSION" ] || { rm -rf "$UNPACK"; die "archive layout unexpected (no $APP-$VERSION-$OS-$ARCH/VERSION)"; }
  # Replacing a folder a daemon runs from is safe: it keeps the unlinked inodes.
  rm -rf "$VDIR"
  mv "$UNPACK/$APP-$VERSION-$OS-$ARCH" "$VDIR"
  rm -rf "$UNPACK"
fi

# Prove the new engine runs HERE (architecture, signature) before switching.
"$VDIR/captainHook" version </dev/null >/dev/null 2>&1 || die "$VDIR/captainHook does not run on this machine — links left unchanged"

# ---- switch -------------------------------------------------------------------
activate "$VDIR" "$VERSION"
if [ -n "$OLD" ] && [ "$OLD" != "$VERSION" ]; then printf '%s\n' "$OLD" > "$INSTALL_DIR/previous"; fi

say "installed captAInHook $VERSION → $VDIR"
[ -n "$OLD" ] && [ "$OLD" != "$VERSION" ] && say "previous version $OLD kept for rollback"

# ---- retention: current + previous ----------------------------------------------
prune "$VERSION"

# A pruned version's daemon is drained here; the previous version's is left to
# idle out — no hook reaches it after the switch (ADR-0020 d3).
"$VDIR/captainHook" doctor </dev/null 2>/dev/null | grep -v 'nothing to reap' | sed 's/^/  /' || true

# ---- next steps ---------------------------------------------------------------------
case ":$PATH:" in
  *":$BIN_DIR:"*) ;;
  *) say "note: $BIN_DIR is not on your PATH — add it in your shell profile, e.g.
    export PATH=\"$BIN_DIR:\$PATH\"" ;;
esac
say "next: wire the hooks (nothing is changed until you pass --apply):
    $BIN_DIR/captainHook install-hooks
    $BIN_DIR/captainHook install-hooks --apply"
say "later: $BIN_DIR/captainHook self update | self rollback | self prune
    uninstall: $BIN_DIR/captainHook install-hooks --remove --apply, then $BIN_DIR/captainHook self uninstall"
