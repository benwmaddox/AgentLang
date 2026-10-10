# 182 — Efficacy decision checkpoint

Status: independently audited evidence checkpoint, 2026-10-10.

The project demonstrates that external AI coding agents can discover, reuse,
repair and publish strongly typed vocabulary. It has not demonstrated that this
workflow produces more reliable changes than conventional F#. The latest repeated
maintenance sequence supports continuing the prototype, with that distinction
kept explicit. AI agents remain development tools outside the language runtime.

## New evidence since the previous checkpoint

[Checkpoint 177](177-reliability-and-vocabulary-checkpoint.md) identified loss of
inherited assertions and examples despite passing behavioral and library gates.
The next study made preservation a separate endpoint rather than treating a
passing test count as proof that earlier evidence survived.

[Report 179](179-preview-repair-and-record-migration.md) completed two fresh
participants per condition, each repairing a validation defect and then migrating
the result to a typed record. Each stage used frozen independent acceptance and
explicitly permitted fixture changes.

The task specified the exact two-field result type. This study tests repair and
typed API migration, not autonomous abstraction or type design. Discovery/reuse
evidence comes from the earlier bounded studies reviewed in checkpoint 177.

| Endpoint | Retained vocabulary | Reset-rich vocabulary | Conventional F# |
| --- | --- | --- | --- |
| Participants completing both stages | 2/2 | 2/2 | 2/2 |
| Repair, per participant | 37/37 | 37/37 | 37/37 |
| Migration original-state projection, per participant | 37/37 | 37/37 | 37/37 |
| Migration proposed-state projection, per participant | 37/37 | 37/37 | 37/37 |
| Unrelated inherited evidence | Preserved | Preserved | Preserved |
| Library publication / F# local validation | 4/4 published | 4/4 published | 4/4 validated |
| Normal broker finalization | 4/4 | 4/4 | 4/4 |
| Broker exchanges, summed across four stages | 90 | 83 | 50 |

The retained treatment includes code, tests, documentation and history. Reset is
a rich domain environment, not primitives alone. F# uses unequal seed provenance,
suites and editing operations. Both retained first-stage participants shortened
their launch allowlists. The fixed order and two participants per condition do
not support causal attribution or a population reliability estimate.

F# used fewer broker exchanges. Retained had the shortest summed broker duration,
but duration includes participant pauses and validation. These measurements do
not establish provider token savings, smaller effective context, total agent
wall-time savings or native runtime throughput.

The latest sequence avoided the earlier evidence-loss outcomes under an explicit
preservation contract. That does not establish that the language alone caused
preservation: all conditions succeeded. Several participant tests derived expected
results from existing implementation helpers; two used error-to-input fallbacks.
The independent oracle supplies a separate model and remains necessary.

## What the gates do and do not establish

The calibrated incorrect preview scored 29/37 while correct controls scored
37/37. Deliberately wrong migration controls passed their matching library tests
and qualification, yet scored 27/37 on the affected result field. These are useful
negative results: execution coverage cannot certify intended behavior.

Keep exhaustive matching, explicit effects, nominal/refined types and strict
library coverage. Keep independent behavioral scoring and evidence-preservation
review alongside them. Do not weaken the library gate to improve this study's
outcome. Its unreachable nested success branch also exposed pressure to duplicate
an established missing-subscription error; investigate reusable typed construction
or compiler-proven impossibility separately, with reachable paths still covered.

## Engineering progress remains separate

[Report 180](180-native-refined-mailbox.md) adds frozen Int/String refinement
admission at native mailbox boundaries and checks failure preservation and retry.
It is semantic and ownership conformance, not an agent comparison or a selected
service memory policy.

[Report 181](181-validated-snapshot-reuse.md) removes redundant activation
compilation while preserving validation and library requalification. Three fresh
processes per build show no clear startup improvement: median 11.513 seconds
before and 11.615 after. Identical retrieval responses and unchanged project
bytes establish parity for that input, not broad performance superiority.

All 37 local Release checks passed at 181. No new runtime test or participant
result is counted in this documentation checkpoint. Local virtual effects and
bounded brokers suffice for this research; no external capability is added.

## Decision and remaining research

Continue native strong-type and arena conformance toward a useful build target;
do not wait for proof of comparative efficacy to complete that engineering.
Resolve the next unsupported semantic slice before adding optimization caches
or more easy composition trials. Preserve the semantic IR as the common
interpreter/native contract. JIT, general type coverage and service performance
remain incomplete.

The [read-only native investigation](evidence/182-efficacy-decision-checkpoint/native-plan.md)
selects unvalidated nominal String wrappers as
the next bounded slice: refined String already provides the dynamic layout and
exact-name host codecs, but wrappers with no predicate are explicitly rejected.
The [acceptance plan](../docs/NATIVE-NOMINALS-IMPLEMENTATION.md#next-slice-unvalidated-nominal-string-planned-after-checkpoint-182)
requires ordinary and mailbox boundary checks before support is claimed. It adds
no runtime implementation in this checkpoint and does not replace the broader
Float/List, JIT or arena-policy work.

The next efficacy study should address an unresolved question on a different
held-out maintenance workload, using the existing brokers. Predeclare behavior,
unrelated-evidence preservation and recovery separately; rotate order and disclose
remaining access/provenance differences. Do not repeat this sequence to imply
independence. Smaller context budgets, long-lived vocabulary quality, autonomous
abstraction creation and trusted-core adequacy remain unmeasured here. Authoritative
provider token counts are still unavailable.

The current decision is feasibility with observed discovery/reuse and successful
bounded maintenance, without an observed reliability advantage. This is sufficient
to keep investigating, not to claim the central hypothesis is proved.

## Evidence

An independent read-only audit checked all 18 archived behavioral projections,
the validation summary, representative preservation reviews, terminal traces and
negative controls. It verified the complete ZIP digest and selected member hashes;
it did not rehash every member, reread every manual source review or rerun scorers.
Reports 180/181 were checked as engineering descriptions, without a new audit of
their archives.

[Audit record](evidence/182-efficacy-decision-checkpoint/audit.md) and
[local validation receipt](evidence/182-efficacy-decision-checkpoint/validation.json)
record scope, input identities and documentation-link checks. Historical trial
inputs, submissions and failed attempts remain unchanged in their original
archives. No participant, runtime, package-security or service-performance test
was added by this checkpoint.
