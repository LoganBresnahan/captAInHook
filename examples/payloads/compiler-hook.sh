#!/bin/sh
# compiler-hook — a ONESHOT captAInHook payload: the project's own checker
# after the agent's edits. It is arm 2 of the pharos trial (captAInHook#4), and
# follows the contract it shares with arm 3, pharos's hook: geex's
# doc/stack-trial-hooks.md. Where this header and that file disagree, the file
# wins.
#
# Register it on four events with the SAME args:
#
#   UserPromptSubmit  the turn starts: forget the last one, answer at once,
#                     then keep running (reply-then-linger, daemon mode) to take
#                     the TURN-START snapshot, the baseline of every file the
#                     agent doesn't write this turn.
#   PreToolUse        on a writer tool (Write, Edit, MultiEdit, NotebookEdit),
#                     Bash or Task: bump the generation (below), lock-free. On a
#                     writer, the first write to a file this turn then takes
#                     THAT FILE's baseline, its errors just before the write (a
#                     file that doesn't exist yet has an empty one), and resets
#                     its reported counts. Other tools: nothing. Wire PreToolUse
#                     for the writers, Bash and Task (or every tool).
#   PostToolUse       after a writer tool: the new errors not yet reported this
#                     turn, as information (inject).
#   Stop              if new errors remain (written this turn or not): block once with
#                     them as the reason. When stop_hook_active is set, the turn
#                     ends: at most one block per turn.
#
#   args: --root <tsconfig dir> [--budget-ms N] -- <checker argv…>
#   the trial: ["--root", "<proj>", "--budget-ms", "<N>", "--",
#               "<proj>/node_modules/.bin/tsc", "--noEmit", "--incremental", "--pretty", "false",
#               "--tsBuildInfoFile", "<run dir>/hook.tsbuildinfo", "-p", "<proj>/tsconfig.json"]
#
# The checker runs in --root and is read as tsc --pretty false output. Every
# error with a file is in scope: tsc reports only the files in its program,
# which includes new files its include/exclude/files would take; errors with no
# file (TS5xxx, TS6053, TS2318) are skipped. Its KEY is (path,
# code, message): line and column move with edits, so they're shown, never
# compared. An occurrence is new when its key occurs more often in the file
# than in the file's baseline; a key's new occurrences are its last ones in
# sort order (path bytewise, line, column, code, message).
#
# Fresh or reused: each PreToolUse on a writer, Bash or Task call appends to a
# generation file (an atomic append, never under the lock, so a read-only call
# never waits on a check). A check records the generation it started under,
# and a baseline or Stop's check reuses the latest result only while the
# generation hasn't moved (a writer's baseline discounts its own bump). A check
# run at a writer's PreToolUse is never kept for reuse: that write is about to
# change the disk. A Bash call with run_in_background makes every check fresh
# until the turn ends. Reported counts are per key
# (path, code, message): only new occurrences beyond the ones already reported
# this turn are shown, and a file's counts reset at its first-write baseline.
#
# Unsure means silent. A check or baseline that fails, can't be read, or runs
# past --budget-ms (match the entry's budgetMs; the checker is stopped a second
# early so the timeout can be logged) says nothing, and a file whose baseline
# is unknown gets no reports that turn. Each case logs one stderr line (the
# trail's exec.stderr):
#   trial.unsure reason=<failed|unparsed|no-baseline|incomplete> event=<baseline|PostToolUse|Stop>
# (any detail on a `compiler-hook detail=…` line of its own). A check is
# INCOMPLETE when its output shows a blocker of tsc's type pass (a code on the
# version-pinned syntactic list, an error with no file, an error on
# tsconfig.json) and no program-file error off that list to prove the pass
# ran: tsc hides every type error behind those. A tsc version with no list
# makes every check unsure. It still reports its new errors, against files
# with known baselines, but it's never a baseline and never kept for reuse.
# A check that ran logs `compiler-hook checked event=… errors=N new=M …`.
#
# Needs jq (without it: noop, logged). Turn state lives on disk per session
# under $CAPTAINHOOK_COMPILER_HOOK_STATE (default ~/.captainHook/compiler-hook).

ME=compiler-hook
CAP=10
BUDGET_MS=30000
ROOT=""
WRITERS=" Write Edit MultiEdit NotebookEdit "

while [ $# -gt 0 ]; do
  case $1 in
    --budget-ms) BUDGET_MS=$2; shift 2 ;;
    --root)      ROOT=$2; shift 2 ;;
    --)          shift; break ;;
    *)           break ;;
  esac
done

IFS= read -r ENVELOPE || ENVELOPE=$ENVELOPE

if ! command -v jq >/dev/null 2>&1; then
  id=$(printf '%s' "$ENVELOPE" | sed -n 's/.*"dispatchId":"\([^"]*\)".*/\1/p')
  case $(printf '%s' "$ENVELOPE" | sed -n 's/.*"type":"\([^"]*\)".*/\1/p') in
    PostToolUse) ev=PostToolUse ;; Stop) ev=Stop ;; *) ev=baseline ;;
  esac
  printf 'trial.unsure reason=failed event=%s\n%s detail=no-jq\n' "$ev" "$ME" >&2
  printf '{"effect":"noop","dispatchId":"%s"}\n' "$id"
  exit 0
fi

jget() { printf '%s' "$ENVELOPE" | jq -r "$1" 2>/dev/null; }
ID=$(jget '.dispatchId // ""')
EVENT=$(jget '.event.type // ""')
SID=$(jget '.event.sessionId // ""')
CWD=$(jget '.event.cwd // ""')
TOOL=$(jget '.event.payload.tool_name // ""')
TARGET=$(jget '.event.payload.tool_input.file_path // .event.payload.tool_input.notebook_path // ""')
STOP_ACTIVE=$(jget '.event.payload.stop_hook_active // false')

case $EVENT in PostToolUse|Stop) LOG_DEFAULT=$EVENT ;; *) LOG_DEFAULT=baseline ;; esac
LOG_EVENT=$LOG_DEFAULT
log()    { printf '%s %s\n' "$ME" "$*" >&2; }
# The contract's shared line, exactly; any detail goes on a line of its own.
unsure() {
  printf 'trial.unsure reason=%s event=%s\n' "$1" "$LOG_EVENT" >&2
  [ -z "${2:-}" ] || log "detail=$2 reason=$1 event=$LOG_EVENT"
}
answer() { # <effect-json>: the ONE stdout line
  printf '%s' "$1" | jq -c --arg id "$ID" '. + (if $id == "" then {} else {dispatchId: $id} end)'
}
noop()   { answer '{"effect":"noop"}'; }

[ -n "$ROOT" ] || ROOT=$CWD
if [ $# -eq 0 ] || [ -z "$ROOT" ] || [ ! -d "$ROOT" ]; then
  unsure failed "no-checker-or-root"; noop; exit 0
fi
ROOT=$(cd "$ROOT" && pwd -P)

SAFE=$(printf '%s' "${SID:-unknown}" | tr -c 'A-Za-z0-9_-' '_')
DIR=${CAPTAINHOOK_COMPILER_HOOK_STATE:-${HOME:-/tmp}/.captainHook/compiler-hook}/$SAFE
mkdir -p "$DIR" 2>/dev/null || { unsure failed "no-state-dir"; noop; exit 0; }

now() { date +%s; }
BUDGET_S=$(( (BUDGET_MS - 1000) / 1000 )); [ "$BUDGET_S" -ge 1 ] || BUDGET_S=1
DEADLINE=$(( $(now) + BUDGET_S ))
nap() { sleep 0.2 2>/dev/null || sleep 1; }

lock() {
  while ! mkdir "$DIR/lock" 2>/dev/null; do
    holder=$(cat "$DIR/lock/pid" 2>/dev/null)
    if [ -n "$holder" ] && ! kill -0 "$holder" 2>/dev/null; then
      # Steal by rename, then check what we took: another waiter may have
      # stolen the dead lock first and made its own, which goes back.
      if mv "$DIR/lock" "$DIR/lock.stale.$$" 2>/dev/null; then
        if [ "$(cat "$DIR/lock.stale.$$/pid" 2>/dev/null)" = "$holder" ]; then rm -rf "$DIR/lock.stale.$$"
        elif [ ! -e "$DIR/lock" ]; then mv "$DIR/lock.stale.$$" "$DIR/lock" 2>/dev/null || rm -rf "$DIR/lock.stale.$$"
        else rm -rf "$DIR/lock.stale.$$"; fi
      fi
      continue
    fi
    [ "$(now)" -lt "$DEADLINE" ] || return 1
    nap
  done
  echo $$ > "$DIR/lock/pid"
}
unlock() { rm -rf "$DIR/lock"; }

# Atomic write: a budget kill mid-write leaves the old file, never half a new one.
put() { printf '%s\n' "$2" > "$DIR/$1.tmp.$$" && mv -f "$DIR/$1.tmp.$$" "$DIR/$1"; }
get() { if [ -e "$DIR/$1" ]; then cat "$DIR/$1"; else printf '%s\n' "$2"; fi; }

is_writer() { case $WRITERS in *" $TOOL "*) return 0 ;; esac; return 1; }

# The written file's path relative to the root, as tsc prints it (../ when
# it's outside).
rel_target() {
  [ -n "$TARGET" ] || return 0
  d=$(dirname "$TARGET"); b=$(basename "$TARGET")
  if [ -d "$d" ]; then d=$(cd "$d" && pwd -P); fi
  to=$d/$b; from=$ROOT; up=""
  while :; do
    case "$to" in "$from"/*) break ;; esac
    [ "$from" = / ] && break
    from=$(dirname "$from"); up="../$up"
  done
  if [ "$from" = / ]; then printf '%s%s' "$up" "${to#/}"; else printf '%s%s' "$up" "${to#"$from"/}"; fi
}

# tsc --pretty false, read into in-scope error objects. A continuation line
# (indented) joins its error's message with one space.
PARSE='
  split("\n") | map(sub("\r$"; ""))
  | reduce .[] as $l ({out: [], cur: null};
      if .cur != null and ($l | test("^\\s+\\S")) then .cur.msg += " " + ($l | sub("^\\s+"; ""))
      else (if .cur != null then .out += [.cur] else . end)
        | .cur = ($l | (capture("^(?<file>.+)\\((?<line>[0-9]+),(?<col>[0-9]+)\\): error TS(?<code>[0-9]+): (?<msg>.*)$")
                        // capture("^error TS(?<code>[0-9]+): (?<msg>.*)$") // null))
      end)
  | (if .cur != null then .out + [.cur] else .out end)
  | map({ file: ((.file // "") | sub("^\\./"; "")),
          line: ((.line // "0") | tonumber), col: ((.col // "0") | tonumber),
          code: ("TS" + .code), n: (.code | tonumber), msg: (.msg | sub("\\s+$"; "")) })'
SCOPE='map(select(.file != "" and (.file | startswith("/") | not)
                  and (.file | test("(^|/)tsconfig[^/]*\\.json$") | not)))'

# tsc runs its type pass only when the program has no syntactic, options or
# global errors. So a run is INCOMPLETE when it shows a blocker (a code on the
# version's syntactic list, an error with no file, an error on tsconfig.json)
# and no proof the type pass ran (an error in a program file whose code is NOT
# on the list). Neither "TS1xxx" nor "below 2000" works: TS2657 and TS17008
# (JSX), TS6189 (scanner) and TS8010 (checkJs) are syntactic, while TS1484,
# TS1192 and TS1259 come from the type pass.
#
# The syntactic lists, one per TypeScript version (derived from that version's
# scanner, parser and JavaScript-file checks). Another version needs its own
# list before it's trusted: with none, every check is unsure.
syntactic_codes() {
  case $1 in
    6.0.3) echo "1002-1003 1005 1007 1010-1012 1034 1068-1069 1109-1110 1121 1124-1132 1134-1140
                 1142 1144-1146 1160-1161 1177-1181 1198-1199 1206 1209 1223 1228 1260 1351-1353
                 1357 1359 1381-1382 1385-1390 1433-1443 1472 1477-1478 1486-1490 1499-1538 2427
                 2457 2657 2754 2809 2819 6188-6189 8002-8006 8008-8013 8016-8017 8033-8034
                 8037-8039 17002 17006-17008 17014-17015 17021 18009 18016 18026 18029-18030" ;;
    *) return 1 ;;
  esac
}
CONFIG='(.file | test("(^|/)tsconfig[^/]*\\.json$"))'
LISTED='(.n as $n | any($syn[]; $n >= .[0] and $n <= .[1]))'
INCOMPLETE="(any(.[]; $LISTED or .file == \"\" or $CONFIG)
             and (any(.[]; .file != \"\" and ($CONFIG | not) and ($LISTED | not)) | not))"

# tsc_version <checker>: sets TSC_VERSION, read once per session and checker.
tsc_version() {
  cache=$DIR/tsc.version
  if [ "$(sed -n 1p "$cache" 2>/dev/null)" = "$1" ]; then v=$(sed -n 2p "$cache")
  else
    v=$( (cd "$ROOT" && "$1" --version) 2>/dev/null </dev/null | sed -n 's/^Version \([0-9][0-9.]*\).*/\1/p' | head -n 1)
    [ -n "$v" ] && printf '%s\n%s\n' "$1" "$v" > "$cache"
  fi
  TSC_VERSION=${v:-unknown}
}
# syn_json: TSC_VERSION's syntactic list as [[lo, hi], …], or fail when it has none.
syn_json() {
  codes=$(syntactic_codes "$TSC_VERSION") || return 1
  printf '%s' "$codes" | tr -s ' \n' '\n\n' | sed '/^$/d' |
    jq -R -s -c 'split("\n") | map(select(. != "") | split("-") | map(tonumber) | [.[0], (.[1] // .[0])])'
}

# run_checker <out-file>: the in-scope errors as a JSON array in $DIR/<out-file>.
# Returns 0 when it ran and could be read; else logs why and returns 1.
run_checker() {
  out=$1; shift
  tsc_version "$1"
  SYN=$(syn_json) || { unsure failed "no-syntactic-list-for-tsc-$TSC_VERSION"; return 1; }
  raw=$DIR/raw.$$
  rm -f "$raw.timeout"
  ( cd "$ROOT" && exec "$@" ) >"$raw" 2>&1 </dev/null &
  cpid=$!
  left=$(( DEADLINE - $(now) )); [ "$left" -ge 1 ] || left=1
  # The watchdog marks a timeout only while the checker still runs: cancelling
  # it (below) wakes it early, and it must not mistake that for the deadline.
  ( sleep "$left"; kill -0 "$cpid" 2>/dev/null || exit 0
    : > "$raw.timeout"; pkill -TERM -P "$cpid" 2>/dev/null; kill -TERM "$cpid" 2>/dev/null
    sleep 2; pkill -KILL -P "$cpid" 2>/dev/null; kill -KILL "$cpid" 2>/dev/null ) >/dev/null 2>&1 &
  wpid=$!
  wait "$cpid"; rc=$?
  pkill -P "$wpid" 2>/dev/null; kill "$wpid" 2>/dev/null
  if [ -e "$raw.timeout" ]; then rm -f "$raw" "$raw.timeout"; unsure failed timeout; return 1; fi
  all=$(jq -R -s -c "$PARSE" "$raw" 2>/dev/null) || { rm -f "$raw"; unsure unparsed; return 1; }
  said=0; [ -s "$raw" ] && said=1
  rm -f "$raw"
  # A failing exit with no error we can read: output we can't parse is
  # `unparsed`, silence is `failed`. Either way it isn't "no errors".
  if [ "$rc" -ne 0 ] && [ "$(printf '%s' "$all" | jq length)" -eq 0 ]; then
    if [ "$said" -eq 1 ]; then unsure unparsed "exit=$rc"; else unsure failed "exit=$rc"; fi
    return 1
  fi
  put "$out" "$(printf '%s' "$all" | jq -c "$SCOPE")"
  # INCOMPLETE: tsc reports type errors only for a program with no syntax,
  # options or global errors, so one of those anywhere hides the rest.
  if [ "$(printf '%s' "$all" | jq --argjson syn "$SYN" "$INCOMPLETE")" = true ]; then : > "$DIR/$out.incomplete"
  else rm -f "$DIR/$out.incomplete"; fi
}
incomplete() { [ -e "$DIR/$1.incomplete" ]; }

# The generation: one line per writer/Bash/Task call this turn.
gen()  { if [ -e "$DIR/gen" ]; then wc -l < "$DIR/gen" | tr -d ' '; else echo 0; fi; }
bump() { echo . >> "$DIR/gen"; }
# reusable <discount>: the latest check still shows the disk.
reusable() {
  [ -e "$DIR/snap.json" ] && [ ! -e "$DIR/bg" ] &&
    [ "$(jq -r '.gen' "$DIR/snap.json")" = "$(( $(gen) - $1 ))" ]
}
# fresh_check: run the checker into current.json, kept for reuse under the
# generation it started with.
fresh_check() {
  g=$(gen)
  run_checker current.json "$@" || return 1
  if incomplete current.json; then rm -f "$DIR/snap.json"; unsure incomplete; return 0; fi
  put snap.json "$(jq -c --argjson g "$g" '{gen: $g, errors: .}' "$DIR/current.json")"
}

turnstart_state() { # ok | unknown | none
  if [ -e "$DIR/turnstart.late" ] || [ -e "$DIR/turnstart.unknown" ]; then echo unknown
  elif [ -e "$DIR/turnstart.json" ]; then echo ok
  else echo none; fi
}

forget_turn() {
  rm -f "$DIR/turnstart.json" "$DIR/turnstart.unknown" "$DIR/turnstart.late" "$DIR/turnstart.pending" \
        "$DIR/touched.json" "$DIR/reported.json" "$DIR/stop.json" \
        "$DIR/snap.json" "$DIR/gen" "$DIR/bg" "$DIR/writing" "$DIR/current.json"
  rm -f "$DIR"/*.incomplete
}

take_turnstart() { # <keep: yes|no> <checker…>, under the lock
  keep=$1; shift
  LOG_EVENT=baseline
  g=$(gen)
  if run_checker turnstart.next "$@" && { ! incomplete turnstart.next || { unsure incomplete; rm -f "$DIR/turnstart.next"; false; }; }; then
    if [ -e "$DIR/turnstart.late" ]; then rm -f "$DIR/turnstart.next"   # a write went first: untrusted
    else
      mv -f "$DIR/turnstart.next" "$DIR/turnstart.json"
      [ "$keep" = yes ] && put snap.json "$(jq -c --argjson g "$g" '{gen: $g, errors: .}' "$DIR/turnstart.json")"
    fi
  else
    : > "$DIR/turnstart.unknown"
  fi
  rm -f "$DIR/turnstart.pending"
  LOG_EVENT=$LOG_DEFAULT
}

# diff: {new, fresh, skipped} for $DIR/current.json against each file's
# baseline: the touched file's own, else the turn-start snapshot's.
diff_errors() {
  jq -n -c --slurpfile cur "$DIR/current.json" \
        --argjson touched "$(get touched.json '{}')" \
        --argjson ts "$( [ "$(turnstart_state)" = ok ] && jq -c '{known: true, errors: .}' "$DIR/turnstart.json" || echo '{"known": false}')" \
        --argjson rep "$(get reported.json '{}')" '
    def key: [.file, .code, .msg] | tojson;
    def order: sort_by(.file, .line, .col, .n, .msg);
    def baseline($f): if $touched[$f] then $touched[$f]
                      elif $ts.known then {known: true, errors: [$ts.errors[] | select(.file == $f)]}
                      else {known: false} end;
    [ $cur[0] | order | group_by(.file)[] | . as $errs | ($errs[0].file) as $f
      | baseline($f) as $b
      | if ($b.known | not) then {skipped: $f}
        else ($b.errors | group_by(key) | map({key: (.[0] | key), value: length}) | from_entries) as $bc
          | { new: [ $errs | group_by(key)[] | (.[0] | key) as $k | (length - ($bc[$k] // 0)) as $ex
                     | select($ex > 0) | .[-$ex:] | order | (.[] | . + {k: $k}) ] }
        end ] as $per
    | ([ $per[] | .new // empty | .[] ] | order) as $new
    | { new: ($new | map(del(.k))),
        fresh: ([ $new | group_by(.k)[] | order | .[($rep[.[0].k] // 0):][] ] | order | map(del(.k))),
        skipped: [ $per[] | .skipped // empty ] }'
}

# render <header> <errors-json>: the contract's wording, capped at 10 lines.
render() {
  printf '%s' "$2" | jq -r --arg head "$1" --argjson cap "$CAP" '
    ([$head] + (.[0:$cap] | map("\(.file):\(.line):\(.col) \(.code) \(.msg)"))
      + (if length > $cap then ["…and \(length - $cap) more."] else [] end)) | join("\n")'
}

HEAD_EDIT="New errors after your edits (information, not instructions; errors mid-refactor are expected):"
HEAD_STOP="These errors appeared during this turn and are still present:"

case $EVENT in
  UserPromptSubmit)
    forget_turn
    echo $$ > "$DIR/turnstart.pending"
    noop
    exec >/dev/null           # the answer is out; what follows is the linger
    if lock; then take_turnstart yes "$@"; unlock
    else : > "$DIR/turnstart.unknown"; rm -f "$DIR/turnstart.pending"; LOG_EVENT=baseline; unsure failed "timeout-lock"; fi
    exit 0 ;;

  PreToolUse)
    case $TOOL in Bash|Task) ;; *) is_writer || { noop; exit 0; } ;; esac
    bump                      # lock-free: a Bash or Task call never waits on a check
    if [ "$TOOL" = Bash ] && [ "$(jget '.event.payload.tool_input.run_in_background // false')" = true ]; then
      : > "$DIR/bg"
    fi
    is_writer || { noop; exit 0; }
    # The turn-start snapshot must exist before the turn's first write lands.
    while [ "$(turnstart_state)" = none ]; do
      p=$(cat "$DIR/turnstart.pending" 2>/dev/null)
      if [ -z "$p" ] || ! kill -0 "$p" 2>/dev/null; then
        rm -f "$DIR/turnstart.pending"
        if lock; then
          if [ "$(turnstart_state)" = none ] && [ ! -e "$DIR/writing" ]; then
            take_turnstart no "$@"
            [ "$(turnstart_state)" = ok ] && TS_HERE=1     # this very disk, before this write
          fi
          unlock
        fi
        break
      fi
      [ "$(now)" -lt "$DEADLINE" ] || break
      nap
    done
    if [ "$(turnstart_state)" = none ]; then : > "$DIR/turnstart.late"; LOG_EVENT=baseline; unsure no-baseline late; LOG_EVENT=$LOG_DEFAULT; fi

    rel=$(rel_target)
    lock || { LOG_EVENT=baseline; unsure failed "timeout-lock"; noop; exit 0; }
    if [ -n "$rel" ] && [ "$(get touched.json '{}' | jq --arg f "$rel" 'has($f)')" = false ]; then
      LOG_EVENT=baseline
      if [ ! -e "$ROOT/$rel" ]; then b='{"known": true, "errors": []}'      # created this turn
      elif [ "${TS_HERE:-0}" = 1 ]; then b=$(jq -c --arg f "$rel" '{known: true, errors: [.[] | select(.file == $f)]}' "$DIR/turnstart.json")
      elif reusable 1; then b=$(jq -c --arg f "$rel" '{known: true, errors: [.errors[] | select(.file == $f)]}' "$DIR/snap.json")
      elif run_checker baseline.cur "$@"; then
        if incomplete baseline.cur; then b='{"known": false}'; unsure incomplete
        else b=$(jq -c --arg f "$rel" '{known: true, errors: [.[] | select(.file == $f)]}' "$DIR/baseline.cur"); fi
      else b='{"known": false}'; fi
      put touched.json "$(get touched.json '{}' | jq -c --arg f "$rel" --argjson b "$b" '.[$f] = $b')"
      put reported.json "$(get reported.json '{}' | jq -c --arg f "$rel" 'with_entries(select((.key | fromjson | .[0]) != $f))')"
      LOG_EVENT=$LOG_DEFAULT
    fi
    : > "$DIR/writing"
    unlock
    noop; exit 0 ;;

  PostToolUse)
    is_writer || { noop; exit 0; }
    lock || { unsure failed "timeout-lock"; noop; exit 0; }
    fresh_check "$@" || { unlock; noop; exit 0; }
    d=$(diff_errors)
    fresh=$(printf '%s' "$d" | jq -c '.fresh')
    shown=$(printf '%s' "$fresh" | jq -c --argjson cap "$CAP" '.[0:$cap]')
    put reported.json "$(get reported.json '{}' |
      jq -c --argjson s "$shown" 'reduce ($s[] | [.file, .code, .msg] | tojson) as $k (.; .[$k] += 1)')"
    unlock
    [ "$(printf '%s' "$d" | jq '.skipped | length')" -eq 0 ] || unsure no-baseline "files=$(printf '%s' "$d" | jq -r '.skipped | join(",")')"
    log "checked event=PostToolUse errors=$(jq length "$DIR/current.json") new=$(printf '%s' "$d" | jq '.new | length') fresh=$(printf '%s' "$fresh" | jq length)"
    if [ "$(printf '%s' "$fresh" | jq length)" -eq 0 ]; then noop
    else answer "$(jq -cn --arg t "$(render "$HEAD_EDIT" "$fresh")" '{effect: "inject", text: $t}')"; fi
    exit 0 ;;

  Stop)
    # At most one block per turn: an agent already continuing from our block
    # (stop_hook_active) ends the turn, whatever the errors.
    if [ "$STOP_ACTIVE" = true ] || [ -e "$DIR/stop.json" ]; then
      forget_turn; noop; exit 0
    fi
    lock || { unsure failed "timeout-lock"; forget_turn; noop; exit 0; }
    if reusable 0; then jq -c '.errors' "$DIR/snap.json" > "$DIR/current.json"
    else fresh_check "$@" || { forget_turn; unlock; noop; exit 0; }; fi
    d=$(diff_errors)
    new=$(printf '%s' "$d" | jq -c '.new')
    [ "$(printf '%s' "$d" | jq '.skipped | length')" -eq 0 ] || unsure no-baseline "files=$(printf '%s' "$d" | jq -r '.skipped | join(",")')"
    log "checked event=Stop errors=$(jq length "$DIR/current.json") new=$(printf '%s' "$new" | jq length)"
    if [ "$(printf '%s' "$new" | jq length)" -eq 0 ]; then forget_turn; unlock; noop; exit 0; fi
    put stop.json '{"blocked":true}'
    unlock
    answer "$(jq -cn --arg r "$(render "$HEAD_STOP" "$new")" '{effect: "decide", verdict: "deny", reason: $r}')"
    exit 0 ;;

  *) noop; exit 0 ;;
esac
