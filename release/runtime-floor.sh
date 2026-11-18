#!/bin/sh
# release/runtime-floor.sh — refuse to package a build whose .NET is older
# than release/runtime-floor.
#
#   release/runtime-floor.sh <engine-bundle> <shim-project.assets.json>
#
# The engine is self-contained: it carries the runtime the SDK chose (the
# newest patch that SDK knows), named in the bundle's embedded deps.json as
# Microsoft.NETCore.App.Runtime.<rid>/<version>. The shim is Native AOT: its
# runtime is compiled in by Microsoft.DotNet.ILCompiler, whose version the
# shim's restore records in obj/project.assets.json. Either one below the
# floor, or not found, exits 1. Without this a build host on an old SDK ships
# the old runtime silently (0.1.5 shipped 10.0.13; the dev host was on 10.0.9).
#
# Reads both artifacts; runs neither (package.sh may build a target this host
# can't execute). Everything goes to stderr: package.sh's stdout is the
# archive path alone.
set -eu

[ $# -eq 2 ] || { echo "usage: $0 <engine-bundle> <shim-project.assets.json>" >&2; exit 2; }
ENGINE_BUNDLE=$1
SHIM_ASSETS=$2

ROOT=$(cd "$(dirname "$0")/.." && pwd)
FLOOR=$(sed -n 's/^\([0-9][0-9.]*[0-9]\)[[:space:]]*$/\1/p' "$ROOT/release/runtime-floor" | head -n 1)
[ -n "$FLOOR" ] || { echo "runtime-floor.sh: no version line in release/runtime-floor" >&2; exit 1; }

# $1 < $2 as dotted numbers (10.0.9 < 10.0.15); GNU and BSD sort both take -t/-k/n.
lt() {
  [ "$1" != "$2" ] &&
    [ "$(printf '%s\n%s\n' "$1" "$2" | sort -t. -k1,1n -k2,2n -k3,3n | head -n 1)" = "$1" ]
}

ENGINE=""
[ -f "$ENGINE_BUNDLE" ] &&
  ENGINE=$(grep -ao 'Microsoft\.NETCore\.App\.Runtime\.[A-Za-z0-9-]*/[0-9][0-9.]*[0-9]' "$ENGINE_BUNDLE" | head -n 1 | sed 's,.*/,,')
SHIM=""
[ -f "$SHIM_ASSETS" ] &&
  SHIM=$(grep -o '"Microsoft\.DotNet\.ILCompiler/[0-9][0-9.]*[0-9]"' "$SHIM_ASSETS" | head -n 1 | sed 's,.*/,,; s,",,g')

check() { # <what> <version>
  if [ -z "$2" ]; then
    echo "runtime-floor.sh: can't find the $1's .NET version — refusing to package" >&2
    return 1
  fi
  if lt "$2" "$FLOOR"; then
    echo "runtime-floor.sh: the $1 is .NET $2, below the floor $FLOOR (release/runtime-floor) — update the .NET SDK and rebuild" >&2
    return 1
  fi
  echo "runtime-floor.sh: $1 .NET $2 (floor $FLOOR)" >&2
}

rc=0
check "engine runtime" "$ENGINE" || rc=1
check "shim AOT compiler" "$SHIM" || rc=1
exit $rc
