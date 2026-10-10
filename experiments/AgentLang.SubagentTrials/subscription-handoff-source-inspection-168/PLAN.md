# Fresh Flow source-inspection probe 168

Status: completed. The participant naturally inspected test source before edits,
passed 132 independent checks and 150 attached tests, but omitted two prior
success-test pre-state assertions. See [report 168](../../../reports/168-test-source-inspection-agent-probe.md).

## Objective and scope

Run one fresh `gpt-6-luna` / max-reasoning Flow participant against the existing
subscription handoff maintenance task after milestone 167 is terminal, validated,
and committed and pushed. This is a usability probe for natural discovery and
preservation behavior. It is not a matched comparison, reliability estimate, or
causal claim about any interface.

The participant starts from an exact copy of the frozen report-166 Flow seed at
`.agentlang/subscription-handoff-maintenance-166/seeds/agentlang-project`.
Preparation verifies the seed against the report-166 frozen `initial-actor`
inventory before copying it. The prompt keeps the report-166 public task and
Flow/2 primer byte-for-byte apart from the broker launch paths. Keep the public
100-exchange limit and allowed operations. The prompt does not name
`includeSource` or `caseName`.

Use the complete Release CLI output directory produced by the final gate. Record
the milestone commit, all runtime file hashes, the final gate report, and the
four source hashes from `post-build-source-pin.json`. Preparation must stop if
the gate is pending, any check failed, a source hash changed, the frozen seed
differs, or an output path already exists.

Before any CLI execution, verify the entire pinned runtime directory against
its recorded file inventory and recheck the gate report and source pin. This
includes `AgentLang.Core.dll`, which defines execution semantics, and every
other runtime file as well as the CLI.

## Scoring and review

Reuse the report-166 Flow scorer. Its only source change is `EVIDENCE_ROOT`,
which points at `.agentlang/subscription-handoff-source-inspection-168`; the
independent model, case set, expectations, and scoring code remain unchanged.
The scorer fingerprints its input actor, copies it to a new run directory, and
executes only against that copy. Keep every run and failed attempt under the
168 evidence root.

Before scoring controls, copy the three already prepared project trees from
`.agentlang/subscription-handoff-maintenance-166/control/flow/{correct,
skip-dry-run-creation-validation,return-updated-store-on-dry-run}` into the 168
control directory. Verify each tree byte-for-byte before and after scoring;
do not rebuild the controls from Flow source or rewrite their callers/tests.
Run the copied grader only against these 168 copies. Each control has 132
checks: 66 target cases plus 66 retained-caller cases. The correct control must
pass all 132. The skip-creation-validation fault must show 16 target failures,
with both callers passing all 66; the modified-store fault must show 10 target
failures, with both callers passing all 66. Report target behavior separately
from caller behavior. These inherited report-166 controls are project-maturity
behavioral controls in their original control domain; their results do not
establish library maturity.

After the participant finishes, score the saved Flow project, run its attached
tests and final task/library checks on a copy, and run the existing preservation
auditor with explicit 168 paths. Review all five prior handoff tests manually:

| Case | Required preservation |
| --- | --- |
| `creation-rules-follow-cancellation` | Preserve the duplicate-subscription result after cancellation validation. |
| `cancellation-rules-precede-creation` | Preserve `CANCEL_BEFORE_START` precedence. |
| `success` | Keep explicit pre-call assertions that the old subscription is active and uncancelled, plus the existing post-call and unrelated-store assertions. |
| `blank-term-after-cancellation` | Preserve `INVALID_SUBSCRIPTION_TERM`. |
| `missing-old-precedes-creation` | Preserve the conflicting replacement ID `30000000-0000-0000-0000-000000000001` and `SUBSCRIPTION_NOT_FOUND`. |

The unchanged conventional seed may be passed as both conventional audit paths;
label that audit row as a baseline self-comparison, never as a new conventional
actor. Behavior, participant-authored coverage, library qualification, and
source preservation are separate findings.

Record trace use of `tests`, `includeSource`, and `caseName`, including whether
the vulnerable cases were inspected before edits. Preserve failures and any
protocol recovery. Broker byte counts are not model tokens, so make no fixed
context claim. Stop after this one Flow probe.
