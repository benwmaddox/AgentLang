# 141 — Maintenance of shared payment vocabulary

Status: preflight passed; four fresh participants dispatched, results pending.
Starting revision: `aff6a84`.

This study returns to the primary question of reliable later edits to discovered,
reusable vocabulary. It follows the runtime comparison in report 140 without
treating native performance as evidence about agent editing quality.

The planned comparison uses two fresh agents per environment, starting from the
accepted retained-language and F# second submissions in report 135. Both account
and dashboard summaries already share a customer payment-total operation. The
maintenance task adds a paid-invoice status requirement while preserving customer
ownership, missing-invoice handling, checked arithmetic, error precedence and
unrelated behavior. Independently score behavior, shared structure, collateral
changes, authored tests and task finalization.

The language's underlying helper was authored by an earlier agent; the original
F# equivalent was supplied by the coordinator. Both summary integrations were
agent edits. These histories and the different discovery/editing interfaces
prevent an identical-provenance or causal language-advantage claim. Four agents
on one change provide bounded observations, not a general superiority estimate.

The new rule affects defensive imported states. It does not repair a defect
reachable through the conventional application's normal payment API, which
already couples a successful payment with a paid invoice. The supplied builders
deliberately bypass those construction invariants for both arms. Keep that
limitation visible when interpreting the result; this is a small shared-rule
maintenance task, not a broad application-correctness benchmark.

## Preparation evidence

Fresh Release builds of AgentLang.Cli and AgentLang.Conventional.Cli pass with
zero warnings and errors. Independent copies of the accepted starting projects
pass 190/190 language tests and 18 F# groups / 157 assertions. Historical
submissions are unchanged. Local build and baseline logs are under
`.agentlang/maintenance-141/`.

All 388 archived non-build files in the two selected original projects match
the corresponding entries in report 135's published evidence ZIP. This verifies
the retained starts against the historical artifact, rather than trusting only
their current directory names or passing tests. The per-file check is saved as
`accepted-source-verification.json`; fresh build source and artifact hashes are
recorded in `build-provenance.json` in the same local evidence directory.

Before dispatch, we resolved a fixture-access mismatch: language records permit
defensive imported states, whereas the F# store/invoice/payment constructors are
private and its normal payment operation marks the invoice paid atomically.
Equivalent frozen test-only builders give both arms a way to author regression
tests for the new rule. Their purpose is public, and participants must preserve
their source and exclude them from production call paths. The language builder
has one attached registration test, increasing that starting suite from 190 to
191. Existing production construction rules remain unchanged.

The public task, independent oracle, starts, scorers and controls were checked
and frozen before participant dispatch. The freeze includes all four project
inventories, exact prompts/wrappers, tooling source and runtime artifact hashes.
Two fresh Luna/max agents per arm receive no prior conversation, a 100-exchange
broker limit and no access to private acceptance material. Dispatch is recorded
in the study's `dispatch/events.json`. No participant efficacy result is claimed
until the unchanged acceptance procedure has been executed.

The F# preflight now distinguishes the intended cases: the unchanged baseline
fails the two changed-rule cases and passes the two preserved-error cases; the
correct status guard passes all four; a plausible guard that also drops negative
payments fails the signed-payment case. Each invocation evaluates the direct
helper and both summaries. The correct control also passes the unchanged 18
groups / 157 assertions. Frozen inputs match before and after these checks.
This validates that arm's scorer, not agent efficacy. Evidence is in
`.agentlang/maintenance-141/fsharp-scoring/preflight-controls.json`.

Root independently reran the final language scorer: baseline 6/12 checks,
correct control 12/12, incorrect positive-only control 9/12, with zero runtime
errors. These are four scenarios evaluated at three call sites, not twelve
independent business scenarios. Both language control copies pass all 191
attached tests. The incorrect control therefore demonstrates again that passing
the existing tests is insufficient: it returns 100 and 113 where the independent
signed-payment case requires 70 and 83. This is coordinator control evidence,
not a participant outcome or a new comparative advantage claim.

Preparation encountered protocol and fixture errors before the freeze: appending
the dictionary text did not register a durable definition; registration needed
the correct syntax selector and an attached test; one self-test expected an Int
from a Bool body; and PowerShell's default JSON timestamp conversion corrupted
fixture literals. The final setup uses definition-level publication and preserves
timestamp strings. Some initial attempts survive only as tool-transcript
observations, not complete raw response files; do not represent them as fully
archived trials. No such attempt is counted as a participant result.

## Retained preflight evidence

The [preflight archive](evidence/141-paid-invoice-maintenance/preflight.zip)
contains the exact generated starts, control projects/results, raw retained
responses, build logs and 30 build-source snapshots. All 1,308 entries were
verified by size and SHA-256 against the
[index](evidence/141-paid-invoice-maintenance/preflight-index.json).
The archive is 6,433,611 bytes, SHA-256
`3cc65fd3cede92742656ad769049429be48ea18c6371252a03e0fc874c9cc18c`.
Participant projects and live traces are deliberately excluded from this
preflight package; their outcomes will be retained after completion.
