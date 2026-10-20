# Flow: shell history — what agents ran, recorded and searched

Every shell command an agent runs is recorded in the trail as one `tool.shell`
line, and `captainHook history` searches those lines: by text, session, cwd,
time and harness, one line per command or the whole multi-line command
([ADR-0009](../adr/0009-trail-rotation.md) d7, v0.1.3).

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
 trail ── tool.shell {command, cwd, harness, tool} + sessionId, dispatchId
        │
        │      … later, a person (or `hh -a`) …
        ▼
 captainHook history [PATTERN] [--session P] [--since 2d|3h|ISO] [--cwd DIR]
                     [--harness H] [--limit N] [--full] [--json]
   ShellTrail.Files: captainHook.<seq>.jsonl (ascending) …, then captainHook.jsonl
   ShellTrail.Read:  stream line by line; skip lines not naming tool.shell
                     before parsing; skip torn/unparseable lines
   filter (all must hold) ─► ring of N (--limit) or straight out
        ▼
 stdout  2026-10-19 14:02  56544d6f  ~/captAInHook  git status
         (cut to the terminal width with …; whole lines into a pipe)
 exit    0 matches · 1 none (stderr explains an empty trail) · 2 usage
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

## Retention: history sees what the trail still holds

- Commands from the first build that records them (0.1.3) onward. A trail
  with none says so on stderr instead of looking like an empty search.
- For as long as the trail keeps them. The trail is one unbounded file today;
  when ADR-0009's segmented archive lands, history already reads the numbered
  segments before the active file, oldest first.
- The durable long-term record stays agent-shell-history's file. history is
  the structured window onto the recent past.
- Command text is recorded whole, so a token an agent puts on a command line
  is in the trail, as it would be in a shell history. The trail is 0600 in a
  0700 directory (ADR-0016 d13).

## Reading it back

Streamed, never loaded: `File.ReadLines`, a substring test for
`"evt":"tool.shell"` before any JSON parse, and at most N entries held for
`--limit N` (the N most recent matches, printed oldest first). Measured: the
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
| recognition + the `tool.shell` line (`ShellTrail.Command`, `Record`) | `dotnet/captainHook/Core/ShellTrail.cs` |
| called before the policy gate, both sites | `Core/HookRun.cs` (`CollapsedAsync`), `Core/DaemonHost.cs` (`DispatchOneAsync`) |
| segment order + streaming read (`ShellTrail.Files`, `Read`, `Parse`) | `dotnet/captainHook/Core/ShellTrail.cs` |
| the verb (args, filters, ring buffer, output shapes, exit codes) | `dotnet/captainHook/Core/HistoryVerb.cs`; `Mode.History` in `dotnet/captainHookWire/Cli.cs`; terminal width in `Program.cs` |
| trail path | `WireJsonl.DefaultLogPath()` (`CAPTAINHOOK_LOG`, else `~/.captainHook/logs/captainHook.jsonl`) |
| events | `tool.shell` (src `tool`) |
| tests | `ShellHistoryTests` (content recognition across tool names, non-shell calls ignored, multi-line whole, both wire sites incl. under a policy deny, torn lines, segment order, every filter, limit, the three output shapes, exit codes, the empty-trail note, the shim refuses the verb); `release/smoke.sh` step 4 (an installed daemon records a `run_shell` PostToolUse and `history --full` finds it, on every release target) |
| decision | [ADR-0009](../adr/0009-trail-rotation.md) d7 |
