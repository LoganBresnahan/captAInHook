# Flow: shell history — what agents ran, recorded and searched

Every shell command an agent runs is recorded as one `tool.shell` line in
`~/.captainHook/logs/shell-history.jsonl`, kept forever, and `captainHook
history` searches those lines: by text, session, cwd, time and harness, one
line per command or the whole multi-line command
([ADR-0009](../adr/0009-trail-rotation.md) d7, v0.1.3; its own file since d8,
v0.1.4).

```
 agent runs a shell command; the host fires PostToolUse
        │  {"tool_name":"Bash"|"run_shell"|…, "tool_input":{"command":"…"}, "cwd":…}
        ▼
 HookRun.CollapsedAsync  or  DaemonHost.DispatchOneAsync   (one per hook, never both)
        │  Harness.ParseEvent → HookEvent
        ▼
 ShellTrail.Record(evt, dispatchId)        BEFORE the policy gate
   PostToolUse? tool_input.command a non-blank string?   (content, not tool name)
        │ yes                                  │ no → nothing
        ▼                                      ▼
 ShellTrail.Sink ── O_APPEND ──► ~/.captainHook/logs/shell-history.jsonl   (0600, 0700 dir)
   tool.shell {command, cwd, harness, tool} + sessionId, dispatchId       KEPT FOREVER
                                                                           (never rotated)
 daemon start (once, background):  trail's 0.1.3-window tool.shell lines
   ─► shell-history.0.jsonl   (ShellTrail.MigrateLegacy: temp + rename)
        │
        │      … later, a person (or `hh -a`) …
        ▼
 captainHook history [PATTERN] [--session P] [--since 2d|3h|ISO] [--cwd DIR]
                     [--harness H] [-n|--limit N] [--full] [--json]
   ShellTrail.HistoryFiles:  shell-history.0.jsonl (or, before a daemon made it,
                             the trail's segments + active file), then shell-history.jsonl
   ShellTrail.Read:  stream line by line; skip lines not naming tool.shell
                     before parsing; skip torn/unparseable lines
   filter (all must hold) ─► ring of N (-n/--limit) or straight out
        ▼
 stdout  2026-10-19 14:02  56544d6f  ~/captAInHook  git status
         (cut to the terminal width with …; whole lines into a pipe)
 exit    0 matches · 1 none (stderr explains an empty history) · 2 usage
```

## Why record, and why here

The trail used to hold no agent commands at all: every `command` field in it
was `exec.spawn`'s payload path. The only record of what agents ran was
agent-shell-history's lines in the maintainer's bash history, flattened (a
multi-line command joined with ` ⏎ `) and without cwd or time. Recording in the
engine gives history the fields a search needs.

**Content, not name.** A shell call is a PostToolUse whose
`tool_input.command` is a string. Claude Code's shell is `Bash`, moby's is
`run_shell`, and a filter on one name reads the other as not-a-shell
(ADR-0019's incident). The host's name rides along as `tool`, for display
only.

**PostToolUse only.** A command denied at PreToolUse, or interrupted, never
ran; one that ran and failed did. The same line agent-shell-history draws.

**Before the policy gate.** The policy decides which handlers see an event. A
command that ran is a fact whatever the policy says about handlers, so even
an event-level deny still records it (pinned by
`TheCollapsedRun_Records_EvenWhenPolicyDeniesTheEvent`).

## Retention: forever, in its own file

- **Kept forever, the owner's explicit choice** (ADR-0009 d8). The trail is
  days-to-weeks telemetry and will be rotated and capped (roadmap item 32);
  `shell-history.jsonl` is not the trail and nothing in its policy touches
  it. Command text is kept whole and indefinitely, so a token an agent puts
  on a command line stays there, as it would in a shell history. 0600 in the
  0700 logs dir (ADR-0016 d13).
- **From 0.1.3 on.** 0.1.3 wrote these lines into the trail itself; a daemon
  copies them once into `shell-history.0.jsonl` at start, and history reads
  that copy (or the trail, before the copy exists) ahead of the active file.
  Nothing recorded since 0.1.3 is lost or doubled. Lines a rolled-back 0.1.3
  writes to the trail after the copy exists are not picked up.
- **Older than 0.1.3:** only agent-shell-history's bash-history lines, if that
  payload was on. A history with nothing in it says so on stderr.
- **Not in the GUI's trace view**, which streams the trail only.
- **One writer seam, swapped in tests.** `ShellTrail.Sink` is the trail's
  O_APPEND primitive (`WireJsonl.Append`) by default; the test suite replaces
  it at module load, as it does the Log sink, so no test writes a user's
  history.

## Reading it back

Streamed, never loaded: `File.ReadLines`, a substring test for
`"evt":"tool.shell"` before any JSON parse, and at most N entries held for
`-n`/`--limit N` (the N most recent matches, printed oldest first; `-n` as in
`hh -n`). Measured: the
maintainer's real 87 MB trail in ~0.2 s; a synthetic 117 MB trail with 133k
commands in ~1.2 s, memory flat at ~60 MB.

- **PATTERN** is a case-insensitive substring of the whole command, never of
  the cut display line. One pattern; quote it to include spaces.
- **`--session`** is a sessionId prefix (case-insensitive); **`--harness`** an
  exact harness name; **`--since`** `30m` / `3h` / `2d` / `1w` back from now,
  or an ISO date/time (local unless it names an offset).
- **`--cwd`** matches by path component after resolving `~` and relative
  paths: `--cwd ~/proj` matches `~/proj/src`, never `~/project`.
- **Default output:** `time  session8  cwd  command`, local time to the
  minute, the first 8 characters of the session id (agent-shell-history's
  tag), home shown as `~`, newlines as ` ⏎ `. Cut to the terminal width with
  `…` only when stdout is a terminal.
- **`--full`** prints a multi-line command as multi-line, continuation lines
  aligned under the first. **`--json`** prints one object per line: `ts`,
  `sessionId`, `cwd`, `harness`, `tool`, `command`, `dispatchId`.

## Ground truth

| what | where |
|---|---|
| recognition + the `tool.shell` line (`ShellTrail.Command`, `Record`, `Sink`, `HistoryPath`) | `dotnet/captainHook/Core/ShellTrail.cs` |
| the 0.1.3 window's one-time copy (`MigrateLegacy`, `LegacyPath`), run at daemon start | `ShellTrail.cs`; `Core/DaemonHost.cs` (`legacyTrail`, production-only via `Program.cs`) |
| called before the policy gate, both sites | `Core/HookRun.cs` (`CollapsedAsync`), `Core/DaemonHost.cs` (`DispatchOneAsync`) |
| what history reads, in order (`ShellTrail.HistoryFiles`; the trail's segment order in `Files`) + streaming read (`Read`, `Parse`) | `dotnet/captainHook/Core/ShellTrail.cs` |
| the verb (args, filters, ring buffer, output shapes, exit codes) | `dotnet/captainHook/Core/HistoryVerb.cs`; `Mode.History` in `dotnet/captainHookWire/Cli.cs`; terminal width in `Program.cs` |
| trail path | `WireJsonl.DefaultLogPath()` (`CAPTAINHOOK_LOG`, else `~/.captainHook/logs/captainHook.jsonl`) |
| events | `tool.shell` (src `tool`, in shell-history.jsonl, not the trail); `daemon.shellHistoryMigrated`, `daemon.shellHistoryMigrateError` (src `daemon`, in the trail) |
| test seam | `ShellTrail.Sink` swapped in `TestInfra` (`CapturedShellHistory` to read it) |
| tests | `ShellHistoryTests` (content recognition across tool names, non-shell calls ignored, multi-line whole, both wire sites incl. under a policy deny, torn lines, segment order, every filter, limit, the three output shapes, exit codes, the empty-history note, the shim refuses the verb, its own file beside the trail, the 0.1.3 lines read from the trail and then from their migrated copy with nothing doubled, the daemon's migration, `-n`); `release/smoke.sh` step 4 (an installed daemon records a `run_shell` PostToolUse into shell-history.jsonl, 0600, nothing in the trail, and `history --full` finds it, on every release target) |
| decision | [ADR-0009](../adr/0009-trail-rotation.md) d7, d8 |
