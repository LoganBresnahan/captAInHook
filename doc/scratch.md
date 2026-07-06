# Scratch

Running list — jot ideas here, promote to DESIGN.md / real tasks when they firm up.

## Figuring out auto-updates to my development process

- [ ] skills
- [ ] docs
- [ ] tests
- [ ] updates

## Shim latency: the AOT ladder (input for the decision-7 gate, 2026-07-06)

Measured after ADR-0004 landed whole: warm hook ~250ms = 67ms CLR boot +
~170ms shim first-JIT + 13.4ms daemon dispatch. The dispatch is already
fast; the shim tax is the residual, 4-5x what decision 7 estimated.

Ladder (each rung produces the next rung's measurement):
1. **A — status quo**: dogfood; is 250ms *felt*? (hooks run concurrently
   with prompt processing — wall time ≠ perceived delay).
2. **B — ReadyToRun**: `-p:PublishReadyToRun=true -r linux-x64`; kills the
   JIT share → ~110-140ms projected. One flag, reversible, no invariant
   touched, identity behaves (crossgen2 deterministic). Default next step.
3. **C — thin AOT captainShim** (decision 7's artifact): ~30-50ms. Costs:
   extract captainHookWire leaf lib (codec+rendezvous+shim client — moved,
   not duplicated; divergence there is a silent-break hazard so sharing is
   mandatory), JSON source-gen, delegation fallback, clang on build hosts,
   and the PERMANENT two-artifact skew hazard (native shim is invisible to
   content identity — fold shim hash into identity / atomic deploys).
   Trigger: B lands and ~120ms is still felt, or a high-frequency handler
   (PreToolUse gate) raises the stakes.

No new ADR needed at any rung: A/B are within decision 7; C is the decision
already recorded, waiting on its trigger.
