# Matched vocabulary retention trial

Status: implementation in progress; no actors launched or results claimed.

The next priority is external-agent behavior, before backend or memory research.
The new package is `business-policy-retention-003`; completed 001/002 studies and
their frozen source snapshots remain unchanged. The model is an external coding
agent, not a language feature.

## Question and conditions

Does passing an independently accepted classifier to a later fresh agent change
how that agent solves the discounted-balance task? Use S01 then S07 because the
earlier Growing S07 actually reused S01's classifier, whereas it did not reuse
S06's discount-basis-points word.

| Arm | S01 start | S07 start |
| --- | --- | --- |
| Flat | Six-type schema-only seed | Same schema-only seed |
| Retained | Shared rich seed | Independently accepted S01 output |
| Reset-rich | Same rich seed | Same rich seed, discarding S01 output |

The rich seed has 53 words, 31 types and 151 tests. The two rich arms use exactly
the same baseline bytes. Preparation must validate its input/runtime/project
inventories and inspect the live dictionary to prove that S01/S06/S07 targets
are absent. Flat has six types and zero authored words/tests.

Two predeclared blocks use rotated arm order: B1 Flat, Retained, Reset-rich; B2
Reset-rich, Flat, Retained. Each arm runs S01 then S07: 12 fresh Luna/max actors,
without inherited turns. Public run identifiers R01–R12 do not teach the arm's
research hypothesis. Execute sequentially and stop after the declared blocks.

## Fixed inputs and transfer

All arms share the exact 002 primer, public task text, runtime, help interface,
model request, capabilities, clock and host limits. The new oracle contains only
the unchanged 001 S01 and S07 defaults/cases, retaining the predecessor hash.
S01 has 10 Bool cases; S07 has 54 signed exact Int64 Money cases independently
checked with BigInteger arithmetic and truncation toward zero.

Freeze the global design, baseline seeds, prompts, oracle, runtime and source
artifacts before the first actor. Each later launch gets a separate pin binding
its actual starting tree and predecessor. Retained S07's starting project cannot
be frozen before its S01 actor creates that result.

Only same-block accepted Retained S01 output may be copied to Retained S07.
Acceptance evidence and complete project hashes must match before transfer.
If S01 fails, an explicit baseline-fallback preparation requires its retained
failure evidence, copies the rich baseline and records that no output was carried
forward. It must never copy failed work or silently substitute a missing result.
Flat and Reset-rich S07 always reset to their frozen baselines.

## Acceptance and termination

Independent verification uses disposable copies, preserving the actor's project.
Check nominal signatures, pure effects, durable library maturity, documentation,
meaningful attached tests, current own instruction/branch coverage, examples and
reload. Preserve baseline type/word identities and source. Keep metadata and
behavior results separate; run every behavior case when the target is safe even
if documentation or another metadata requirement fails.

All actual launches require clean committed-source pins checked before runtime
execution. Controls must prove valid baseline acceptance before mutation, reject
tampered pins/predecessors before any runtime process, and match intended failure
diagnostics rather than treating an arbitrary failure as success.

Use the v2 trial host. After independent verification, the actor closes its own
session with the exact transport control `{"op":"host.close"}`. Require a passing
study-integrity audit and explicit successful termination audit with host/runtime
exit 0/0. Ordinary EOF or Ctrl+C cannot stand in for that evidence. Preserve and
withhold further launches on preflight or audit failure.

## Measurements and limits

Record independent acceptance, own tests/coverage, diagnostics, help queries,
source/dependency evidence of reuse, mutations, exchanges and exact payload bytes.
Do not count dictionary presence alone as reuse. Model tokens, turns, effective
context and model latency remain unavailable unless the provider exposes them;
payload bytes and host lifetime are not substitutes.

This is a task-specific exploratory comparison with two blocks. All arms receive
help, so it cannot isolate help's effect. It does not include a new conventional
arm, prove broad efficiency or context benefits, or replace the remaining full
60-task evaluation requirements.

## Implementation and gates

New preparation, freeze, independent verification, preflight and trace-audit
scripts own this versioned study. Reuse established oracle and inventory patterns
without altering old study identities or synthesizing S06 predecessor evidence.
Focused controls cover baseline equality/absence, accepted versus failed transfer,
correct/no-op/wrong classifier and Money formulas, malformed provenance, pin
tampering, and explicit termination. Fresh source builds, focused checks, review
and applicable release validation precede source publication and actor launches.

## Clean launch workflow

Generated preparation evidence, actor projects, starting copies, prompts, pins and
live traces belong under the ignored `.agentlang/business-policy-retention-003`
directory while a run is prepared and frozen. Writing operational reports into
tracked `reports/evidence` before freezing would dirty the checkout; committing
them would advance HEAD beyond the prepared source revision. Archive immutable
evidence into tracked reports only after its pin is established. Preserve every
failed attempt as well as passing evidence.

Commit the implementation first, run the exact release validation gate with a
fresh build, and bootstrap the canonical seeds once from that clean source.
Archive those exact seeds and committed source snapshots, then commit the
archives and their report. Create and commit the global manifest separately.
Keep the runtime bundle from the original clean build fixed across all cells.
Normal per-cell preparation and freezing must both succeed before any actor is
launched. Later report-only commits are allowed only where frozen artifact and
language-source checks still pass.

A retained predecessor must pass both independent task acceptance and the
post-close study/termination audit. The integrity audit binds the exact acceptance
file hash and accepted final project tree, preventing edits after verification
from being transferred. Failed task behavior may still have a passing integrity
audit; only then may the explicit baseline fallback reference that failed result.
The coordinator also saves its ordered verification-completed and close-requested
events. Host traces alone do not prove those coordinator actions occurred in order.

## Reference control and coordinator evidence

Before model trials, a deterministic reference host uses a genuine clean R01 pin.
Its `runs/R01/preflight-reference-control.json` contains `schemaVersion: 1`, the
study and run IDs, `controlKind: "deterministic-reference"`, and
`modelActorLaunched: false`. This control is not a measured agent outcome. Frozen
preflight takes a separate path from candidate controls, so it never repeats the
candidate requirement that R01 be absent over an existing reference session.

`runs/R01/coordinator.json` has the same identity and control fields plus two
ordered events. `verification-completed` records its actual UTC time, canonical
acceptance path, acceptance SHA-256, `acceptancePassed: true`, and accepted project
tree SHA-256. `close-requested` records its actual UTC time and the same acceptance
SHA-256. The verifier's `finishedUtc` must precede or equal the first event, which
must precede or equal the second event and the host trace's `host-close.atUtc`.
Hashes must match the canonical acceptance and audited project. These records are
written during live coordination; missing events are not inferred from traces.

The bounded frozen validator consumes exact committed manifests, pin, acceptance,
trace and coordinator evidence. It may rerun the auditor to a unique ignored
output, but must not overwrite the bound acceptance file. Tamper controls operate
only on this explicitly marked reference control with preserved original bytes.
They never mutate a model trial. Archive the complete reference evidence before
cleaning its owned slots and starting the actual fresh-agent sequence.
