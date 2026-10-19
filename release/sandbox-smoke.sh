#!/bin/sh
# release/sandbox-smoke.sh — install one archive into a THROWAWAY home and
# smoke-test it there, then uninstall (ADR-0020 d7). For a machine with no
# toolchain (a bare Mac): it needs only this release/ directory and an archive
# built by CI. Nothing outside the run directory is touched — HOME,
# XDG_RUNTIME_DIR, the install dir and the bin dir all live inside it — and no
# daemon outlives it (the smoke sets a short idle window).
#
#   sh release/sandbox-smoke.sh <captainhook-…-<os>-<arch>.tar.gz> [run-dir]
#
#   run-dir  default ~/oof-smoke/runs/captainhook-<timestamp> (created; kept,
#            so the trail can be read afterwards)
#
# Exit 0 = every check passed; the last line is the summary.
set -eu

[ $# -ge 1 ] || { echo "usage: $0 <archive.tar.gz> [run-dir]" >&2; exit 2; }
HERE=$(cd "$(dirname "$0")" && pwd)
[ -f "$1" ] || { echo "sandbox-smoke: no such archive: $1" >&2; exit 2; }
ARCHIVE=$(cd "$(dirname "$1")" && pwd)/$(basename "$1")
RUN=${2:-$HOME/oof-smoke/runs/captainhook-$(date +%Y%m%d-%H%M%S)}
mkdir -p "$RUN"
RUN=$(cd "$RUN" && pwd -P)      # macOS: /tmp is /private/tmp — compare real paths

# Everything below happens inside $RUN.
export HOME="$RUN/home"
export XDG_RUNTIME_DIR="$RUN/x"   # short: the daemon's socket path caps at ~104 bytes
unset XDG_DATA_HOME XDG_CACHE_HOME CAPTAINHOOK_INSTALL_DIR CAPTAINHOOK_BIN_DIR CAPTAINHOOK_HANDLERS_FILE
mkdir -p "$HOME"
mkdir -p "$XDG_RUNTIME_DIR" && chmod 700 "$XDG_RUNTIME_DIR"
BIN=$HOME/.local/bin
INSTALL=$HOME/.local/share/captainhook

echo "sandbox-smoke: run dir $RUN"
echo "sandbox-smoke: host    $(uname -srm)"
if command -v sw_vers >/dev/null 2>&1; then echo "sandbox-smoke: macOS   $(sw_vers -productVersion) ($(sw_vers -buildVersion))"; fi

sh "$HERE/install.sh" --archive "$ARCHIVE" | grep -v '^    \|next:\|uninstall:' || true
[ -L "$BIN/captainShim" ] && [ -L "$BIN/captainHook" ] || { echo "sandbox-smoke: FAIL  the bin links were not created" >&2; exit 1; }

# The symlink → AppContext.BaseDirectory question, asked directly: the links
# are FILE symlinks into versions/<v>/, and the engine must report that folder.
for e in captainShim captainHook; do
  echo "sandbox-smoke: link    $e -> $(readlink "$BIN/$e")"
done
"$BIN/captainHook" version | sed 's/^/sandbox-smoke: /'

if [ "$(uname -s)" = Darwin ]; then
  v=$(readlink "$BIN/captainHook"); v=${v%/captainHook}
  for b in captainShim captainHook bosun; do
    sig=$(codesign -dv "$v/$b" 2>&1 | sed -n 's/^Signature=//p')
    codesign -v "$v/$b" 2>/dev/null && ok=valid || ok=INVALID
    q=$(xattr -p com.apple.quarantine "$v/$b" 2>/dev/null || echo none)
    echo "sandbox-smoke: sign    $b: signature=${sig:-?} ($ok), quarantine=$q"
  done
fi

sh "$HERE/smoke.sh"

# Leave nothing installed: hooks out of the temp settings, then the binaries.
"$BIN/captainHook" install-hooks --remove --apply >/dev/null
sh "$HERE/install.sh" --uninstall >/dev/null
[ ! -e "$BIN/captainShim" ] && [ ! -e "$INSTALL/versions" ] || { echo "sandbox-smoke: FAIL  uninstall left files behind" >&2; exit 1; }
echo "sandbox-smoke: ok    uninstalled; the trail is kept at $HOME/.captainHook/logs/captainHook.jsonl"
echo "sandbox-smoke: PASS  $(basename "$ARCHIVE") on $(uname -sm)"
