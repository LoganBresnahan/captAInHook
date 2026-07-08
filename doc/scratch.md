# Scratch

Running list — jot ideas here, promote to DESIGN.md / real tasks when they firm up.

## Figuring out auto-updates to my development process

- [ ] skills
- [ ] docs
- [ ] tests
- [ ] updates

## Trail integrity follow-up

- [ ] **Emitters don't actually O_APPEND** (surfaced by ADR-0007 sse-trail-tail's
      adversarial verify, 2026-07-08; probed via strace). `File.AppendAllText`
      on .NET 10/Linux opens WITHOUT O_APPEND and pwrites at offsets cached at
      open — so the shim and daemon appending the trail in the same sub-ms
      window can overwrite each other's lines, contradicting WireJsonl.Append's
      comment and ADR-0004 N3's convergence story. Fix is emitter-side (both
      `WireJsonl.Append` AND `Logging.fs`'s file sink need a true O_APPEND
      open); wire lib is the AOT leaf + golden-pinned, so it rides its own
      slice, likely an ADR-0004 amendment. See doc/platform.md § File locking.

## Security follow-ups

- [ ] **Trail-at-rest hardening** (surfaced by ADR-0007 auth-token-origin's
      adversarial verify, 2026-07-07). The JSONL trail holds prompts + tool
      calls but is created 0644, and `WireJsonl.Append` makes `~/.captainHook`
      at the umask default (0755) on the first hook — so a co-located user can
      read the trail in the window before a daemon tightens the dir to 0700, or
      in pure-collapsed/no-daemon runs. The API TOKEN is already safe (0600 at
      birth) and the daemon now tightens the dir, but closing the trail window
      means the shim/`WireJsonl` creating the dir 0700 + the trail file 0600 —
      a wire-lib (AOT leaf) change, cross-platform-guarded, likely an ADR-0004
      amendment. See doc/platform.md § Runtime directories (the residual note).
