# Early evaluation preparation and bounded fold work

Status: work in progress, 2026-10-06. No new comparative agent outcome is claimed.
The user prioritized the shortest path to validating agent behavior over many
other prototype deliverables. PRD and requirements now reflect this order;
the full objective and remaining requirements are unchanged.

## Pilot preparation

[The five-task contract](../docs/EARLY-AGENT-EVALUATION.md) compares premium
classification, discounting, annual renewal eligibility, renewal balance and
savings. Flat and Growing both start with schemas only. Growing retains accepted
changes; Conventional retains ordinary accepted F# code. The previous preseeded
Growing control is not reused as the cumulative baseline. Pilot tasks do not
need the in-flight fold feature or the complete strong business fixture.

The [oracle](../experiments/AgentLang.SubagentTrials/early-flow-001/acceptance.json)
has 100 task/vector combinations, using the 20 audited renewal inputs and
task-specific expected values. Language acceptance passed 48 checks for each
of five targets against known-correct fixtures, using the existing published
runtime binaries. Targets 1, 2 and 4 used a copied prior agent result; targets
3 and 5 used root-authored additions in an ignored isolated probe. These are
verifier checks, not external-agent trials or current fold-source validation.

The conventional schema-only seed builds in Release with zero warnings/errors.
The independent external adapter passed 43 checks per target against a separate
root-authored reference and rejects the unimplemented seed at target execution.
These observations validate the acceptance adapter, not agent behavior.
Five wrong conventional targets also built and executed, then failed their
specific independent Bool/Float result checks; arbitrary build/runtime errors
do not count as behavioral rejection. Five wrong language library targets passed their own tests and instruction/
branch coverage, then were rejected by independent behavioral checks. Setup or
coverage failures cannot satisfy those negative controls.

Preparation exposed harness mistakes, all corrected before these passing
checks: Bool eval values are canonical rendered text rather than JSON booleans;
`define` takes `source` while eval takes `code`; library commit uses `library:
true`, not a `maturity` request field; an existing Flow word cannot be redefined
as a new project declaration; an effects declaration needs its line boundary.
Failed probe outputs remain in ignored local evidence. None is presented as an
agent failure. Protocol consistency and syntax discoverability are relevant
observations for the upcoming agent trials.

The language negative controls are now required by the full validation script
and their evidence is configured for CI upload. This adds one check to the
gate; the expanded full gate has not yet run.

Still required before pilot conclusions: freeze complete trial inputs and
prompts, launch actual external
subagents, audit complete state and attached tests, retain starting/ending
states and protocol interactions, repeat with rotated mode order, and report
measurement limits. Exact subagent token counts/model turns remain unavailable.

## Post-publication CI failure

Main run 37456957676 on `f55057b` completed with failure. The language and renewal
checks passed, but the broker verifier required total subprocess duration below
6 seconds for a 500 ms exchange deadline. The observed total was 6,870 ms;
startup, trace work and cleanup were included. This is not a passing main run.

The assertion now measures the exchange trace's elapsed time, requiring
400–2,500 ms around the 500 ms deadline, as well as the existing 10-second outer
process boundary, timeout result, uncertain delivery and no automatic retry.
Oversized trace summaries now retain those metadata fields instead of losing
them behind a long payload prefix. The focused exact broker verifier passed
all 17 checks: the no-reader exchange took 507.3 ms, total process 3,782.1 ms.
Fresh full validation and clean committed CI remain required.

## Bounded fold implementation

Backend, frontend and independent test ownership follow
[the fold contract](../docs/LIST-FOLD-PLAN.md). Core and CLI now build in Release
with zero warnings/errors. Focused verifier/interpreter/format/source/vocabulary
runners pass 114/31/43/89/36 assertions respectively. Direct Flow CLI folding
returns 6 for [1,2,3] with addition and returns seed 7 for empty input; explicit
Stack retains an earlier 99 value. Short `word add` is ambiguous with float.add;
the exact root reference `::add` resolves correctly.

Fresh compilation exposed a source/IR type-conversion mistake in fold lowering;
root converted the checked LangType annotations before comparing with IrType
callback contracts. Test compilation also exposed a list-versus-set coverage
assertion and two offside multiline bindings; those test mistakes were fixed.
The parity fixture initially attempted a two-value literal test expectation,
which legacy tests do not support. It now projects one result for the attached
test and independently checks both preserved stack values through CLI eval.
The current-binary parity contract passes; it is a self-comparison against
explicit fixture oracles, not a pre-fold binary comparison or LLVM conformance.

Runtime/storage/Flow integration tests are still completing and fresh complete
validation/clean CI remain required. No delivered full-domain or comparative
agent outcome is claimed. This adds no mailbox, arena allocator or LLVM backend.

The readiness assessment is saved in
[the project review](PROJECT-READINESS-2026-10-06.md). Comparative agent evidence
is the next major decision milestone; optional language expansion must not
delay it.

## Integration correction and next checkpoint

The fresh focused Flow, Flow-runtime, and storage runners now pass 1,006, 616,
and 314 assertions. Fold test fixtures were corrected to use literal Boolean
expectations for nominal equality, a valid arity-negative callback body, the
IR item type rather than list type, parser-stage diagnostics, optional JSON
fields, and the current persisted callback revision. These corrections do not
relax independent behavior or library coverage checks. The first full validation
run failed these integration fixtures; its evidence is retained. A fresh full
gate and committed CI are still required before publication.

A bounded independent Luna review identified launch configuration and final-state
audits as the remaining pilot preparation. The conventional schema-only fixture
already exists. Continue later tasks from the last independently accepted state
after failures; never seed unaccepted code. Preserve authorization-layer blocks
as blocked outcomes. The initial run is an apparatus trial, not an efficiency
claim.

## Complete local validation

The exact fresh Release gate passed all 32 required checks, with zero build
warnings/errors. The saved local validation records the tested working tree
as dirty on parent f55057b; it is not clean committed CI evidence. Sidecars
include all five adversarial library controls and the corrected 17-check broker
verification. Failed integration validation is retained separately. Publication
will pin source with this report and then audit clean CI before merging.

## Committed source CI and publication

Clean source CI run [37466819793](https://github.com/benwmaddox/AgentLang/actions/runs/37466819793)
passed all 32 required checks on `e87931a1ed22b05e60443c158cf9c2527c0ba6f6`,
with `dirty: false`. Downloaded run identity, full validation, five wrong-library
controls and corrected broker evidence were audited. This report/evidence update
changes no executable source after that run. Publication fast-forwards private
main and prototype together. The first fresh Flat language and Conventional
subagent tasks are underway on that source with pinned copied binaries; their
outcomes belong to the next report. Growing launch hit the subagent thread cap
and was not substituted with an inherited-history agent. Exact usage remains
unavailable, and concurrent apparatus runs cannot compare latency.