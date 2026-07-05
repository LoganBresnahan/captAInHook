// adr-plan — turn an accepted ADR into an effort-ranked, dependency-ordered
// build checklist. Decompose the ADR into buildable slices, rank each slice
// (in parallel) against THIS project's effort philosophy, then order them into
// phases. Reusable: Workflow({ name: 'adr-plan', args: '<adr-path>' }); defaults
// to ADR-0004. This is a *planning/triage* workflow — it decides WHERE effort,
// verification, and ultracode belong; it does not do the implementation.

export const meta = {
  name: 'adr-plan',
  description: 'Decompose an ADR into implementation slices and rank each by effort, verification need, applicable skills, and dependencies — then order them into build phases',
  whenToUse: 'Before implementing an accepted ADR: turn its decisions into an effort-ranked, dependency-ordered build checklist',
  phases: [
    { title: 'Decompose', detail: 'extract concrete implementation slices from the ADR' },
    { title: 'Assess', detail: 'rank each slice: effort, hardness, verify/ultracode, skills, deps, status' },
    { title: 'Plan', detail: 'order slices into dependency-respecting build phases' },
  ],
}

const adrPath = (typeof args === 'string' ? args : args && args.adr) || 'doc/adr/0004-daemon-topology.md'

const SLICE_SCHEMA = {
  type: 'object', additionalProperties: false, required: ['slices'],
  properties: {
    slices: {
      type: 'array',
      items: {
        type: 'object', additionalProperties: false, required: ['id', 'title', 'entails', 'source'],
        properties: {
          id: { type: 'string', description: 'short kebab-case id, e.g. uds-rendezvous' },
          title: { type: 'string' },
          entails: { type: 'string', description: 'what building it actually involves' },
          source: { type: 'string', description: 'which ADR decision / consequence / mitigation it comes from' },
        },
      },
    },
  },
}

const ASSESSMENT_SCHEMA = {
  type: 'object', additionalProperties: false,
  required: ['id', 'status', 'effort', 'hardness', 'needs_verification', 'needs_ultracode', 'skills', 'risk', 'depends_on', 'rationale'],
  properties: {
    id: { type: 'string' },
    status: { type: 'string', enum: ['todo', 'partial', 'done'] },
    effort: { type: 'string', enum: ['low', 'medium', 'high', 'max'] },
    hardness: { type: 'string', enum: ['mechanical', 'moderate', 'hard-reasoning'] },
    needs_verification: { type: 'boolean', description: 'an adversarial verify pass is warranted' },
    needs_ultracode: { type: 'boolean', description: 'decomposes into parallel work AND verification-worthy AND single-pass-would-miss' },
    skills: { type: 'array', items: { type: 'string' }, description: 'captAInHook skills that apply: verify, shipshape, orient' },
    risk: { type: 'string', enum: ['low', 'medium', 'high'] },
    depends_on: { type: 'array', items: { type: 'string' }, description: 'slice ids that must land first' },
    rationale: { type: 'string', description: 'WHY this effort/hardness/verification — tied to reasoning difficulty' },
  },
}

const PLAN_SCHEMA = {
  type: 'object', additionalProperties: false, required: ['ordered_phases', 'critical_path', 'notes'],
  properties: {
    ordered_phases: {
      type: 'array',
      items: {
        type: 'object', additionalProperties: false, required: ['phase', 'slice_ids', 'why'],
        properties: {
          phase: { type: 'string', description: 'e.g. "0. already landed" or "1. rendezvous + minimal daemon"' },
          slice_ids: { type: 'array', items: { type: 'string' } },
          why: { type: 'string' },
        },
      },
    },
    critical_path: { type: 'array', items: { type: 'string' }, description: 'slice ids on the critical path, in order' },
    notes: { type: 'string', description: 'what to batch; which slices genuinely warrant ultracode/verify AND which explicitly do not; sequencing risks' },
  },
}

phase('Decompose')
log(`Decomposing ${adrPath} into implementation slices`)
const decomposed = await agent(
  `Read ${adrPath} (a Nygard-style ADR in the captAInHook repo) fully, and skim the code/docs it references. Extract the concrete IMPLEMENTATION slices it implies — the discrete units a developer would actually build, not the prose sections. For each: a short kebab-case id, a title, what building it entails, and which ADR Decision / Consequence / Mitigation it comes from. Aim for 6-14 buildable-sized slices grounded in the ADR's numbered decisions, its Consequences, and its Mitigations. Do not invent work the ADR does not imply.`,
  { label: 'decompose', phase: 'Decompose', schema: SLICE_SCHEMA }
)
const slices = (decomposed && decomposed.slices) || []
log(`${slices.length} slices found`)

phase('Assess')
// The full slice roster, so each assessor's depends_on references CANONICAL ids
// rather than inventing aliases (the flaw the first run's synthesis flagged —
// each assessor ran in isolation and never saw the sibling ids).
const roster = slices.map(s => `${s.id} — ${s.title}`).join('\n')
// Parallel (barrier): the Plan phase needs ALL assessments at once to order by dependency.
const assessed = (await parallel(slices.map(slice => () =>
  agent(
    `You are ranking ONE implementation slice from captAInHook's ${adrPath} for HOW to build it. Read the ADR section it comes from and the relevant code to judge real difficulty — do not guess.

SLICE: ${JSON.stringify(slice)}

ALL sibling slices (id — title). In depends_on you MUST use only these exact ids, verbatim — never invent, paraphrase, or reword an id:
${roster}

Assess it using THIS project's effort philosophy — be calibrated, do NOT mark everything high:
- effort tracks REASONING DIFFICULTY, not size. Mechanical wiring / boilerplate / tests = low or medium. Subtle concurrency, races, protocol or lifecycle logic, or a decision with sharp edges = high or max.
- hardness: mechanical | moderate | hard-reasoning.
- needs_verification (an adversarial verify pass) = true ONLY when a subtle bug would be costly and a single pass could plausibly ship it wrong (e.g. the lock/bind race, the timeout-vs-fault classification, at-most-once delivery).
- needs_ultracode = true ONLY when the slice decomposes into parallel work AND is verification-worthy AND a single pass would miss things. Reserve it; most slices are false — over-marking is the exact failure this project warns about.
- skills: which captAInHook skills apply — verify (drive the change end-to-end), shipshape (pre-commit gate: tests green twice, docs, logging), orient (rarely).
- status: todo | partial | done — check the code (e.g. the cold-start probe already exists in Core/ColdStartProbe.cs).
- depends_on: the exact ids FROM THE ROSTER ABOVE of slices that must land first — [] if none. Do not use any id not in the roster.
Return the structured assessment with an honest rationale.`,
    { label: `assess:${slice.id}`, phase: 'Assess', schema: ASSESSMENT_SCHEMA }
  )
))).filter(Boolean)

phase('Plan')
log(`Ordering ${assessed.length} assessed slices into build phases`)
const plan = await agent(
  `Ranked implementation slices for captAInHook's ${adrPath} (assessments JSON):
${JSON.stringify(assessed, null, 2)}

Their titles / what-they-entail:
${JSON.stringify(slices, null, 2)}

Produce a dependency-respecting build plan: group slices into ordered phases (respect depends_on; put any already-'done' slices in a phase 0), identify the critical path, and in 'notes' call out what to batch, which slices genuinely warrant ultracode/adversarial-verify AND which explicitly do NOT, and any sequencing risks. Keep it tight and actionable — this is the checklist a developer follows to build the ADR.`,
  { label: 'synthesize', phase: 'Plan', schema: PLAN_SCHEMA }
)

return { adr: adrPath, sliceCount: slices.length, assessed, plan }
