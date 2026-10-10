# 173 - Shared-policy maintenance by external AI coding agents

All four fresh participants completed the same maintenance task correctly:
two in the language and two in conventional F#. Each passes 144 independent
behavioral observations. Both language submissions preserve all inherited tests
byte-for-byte; both F# submissions retain their assertions and disclose one
fixture-time adjustment required by the new policy.

This establishes bounded maintenance feasibility and discoverable reuse of an
existing shared function. It does not establish a comparative reliability,
context, token, latency or cost advantage. No participant created a new domain
helper; this task tests maintenance and reuse, rather than vocabulary growth.

## Results

| Participant, in dispatch order | Independent checks | Local validation | Broker exchanges | Rejected requests |
| --- | --- | --- | --- | --- |
| Language-1 | 144/144 | Saved full suite 155/155 | 23 | 1 attachment-owner request |
| F#-1 | 144/144 | Final self-tests passed | 15 | 2 patch requests; 1 validation |
| F#-2 | 144/144 | Final self-tests passed | 26 | 2 patch requests; 1 validation |
| Language-2 | 144/144 | Saved full suite 155/155 | 23 | 0 |

All four brokers closed with independently audited host/runtime exit codes 0/0.
The language submissions each pass 12 handoff tests and three tests per caller,
with 45/45 own instructions, 10/10 branch outcomes and complete required Bool
input and Result-alternative coverage. They publish at library maturity through
the ordinary task protocol. Coverage is implementation evidence; the separate
acceptance cases establish the reported behavioral result.

## Task and frozen design

Maintain the existing seven-input subscription handoff and both five-input
annual/monthly callers. An active old subscription may be handed off only within
its own half-open term window: startedAt <= handoffAt < expiresAt. Missing-old,
already-cancelled and before-start errors retain precedence. At/after expiry,
HANDOFF_WINDOW_CLOSED with the specified message precedes replacement validation,
for both normal and dry-run calls. Preserve complete Store contents and input.

Four fresh GPT-6 Luna/max participants run sequentially in ABBA order, with 100
broker exchanges permitted each. Each receives only its matched public task,
small language/tool primer and isolated broker. No hidden-oracle guidance or
inter-participant coaching is supplied. The prompt asks for meaningful tests,
preservation of unrelated code/assertions, and identification of necessary
fixture changes. It does not prescribe a new abstraction or helper name.

Accepted report-166 projects are the initial seeds. The language runtime is the
independently accepted, hash-pinned report-171 runtime. The conventional seed
receives a disclosed coordinator-only appended error union case and code/message
mapping, because its closed error type otherwise cannot represent the new error.
Existing policy is unchanged; pristine and prepared self-tests pass. Its rebuilt
foundation DLL is frozen before dispatch and is not an agent achievement.

The independent integer-cell model has 36 cases: 29 valid-reference and seven
adversarial orphan-reference fixtures. Both dry-run modes and both callers yield
144 observations per participant. Checks include combined error precedence,
expiry equality, the last cell before expiry, exact new error message, full state
and input preservation. Correct, baseline and three boundary/precedence mutants
execute in both actual environments and produce the expected failure sets.
Control-only maturity tests never qualify participant submissions.

Inputs, prompts, runtime and control evidence were frozen before dispatch.
Later model changes only add export assertions: root reconstructs the calibrated
source to its recorded SHA, verifies identical expected-function AST and case
bytes, and executes the final invariants. Interrupted partial rechecks are kept
as incomplete evidence and are not accepted calibration runs.

## Preservation and recovery

Both language participants change only the shared handoff definition/doc and add
four own tests plus one test to each caller. Root compares persistent manifests
and objects: all existing identities, types, examples, inherited test bytes,
unrelated revisions and caller implementations remain intact. Each saved full
suite passes 155 tests. Language-1 recovers from FLOW_ATTACHMENT_OWNER_MISMATCH
by submitting tests with their correct owning functions; Language-2 has no
rejected requests.

Each F# participant changes only Operations.fs and SelfTests.fs; all six frozen
infrastructure files and both caller implementations remain intact. Their first
validation detects an inherited overlap fixture that attempts handoff exactly
at old expiry. Coordinator preparation had incorrectly assumed neither seed contained such
a fixture; the F# seed does. F#-1 moves its time to the existing
in-window handoffAt; F#-2 moves it from February 1 to January 31. Both retain the
SubscriptionOverlap assertion, identify the adjustment, add boundary/caller
regressions, and pass their next validation. Root reviews the complete diffs:
no inherited assertion is deleted.

The F# broker also rejects four patch requests across the pair: missing anchor,
invalid hash, stale-content guard and ambiguous anchor. These are recoverable
text-editing/protocol failures, not accepted incorrect changes. The broker's
mandatory SHA/anchor checks are part of this experiment's tool design; their
frequency cannot be attributed solely to F# or ordinary repository editing.

## Interpretation and limits

The existing shared vocabulary is discoverable and maintainable on this task,
and both environments support correct caller reuse. Neither side duplicates
the policy or creates an unnecessary helper. Both language submissions meet the
library gate while preserving prior evidence. This improves on the preservation
outcomes documented in reports 166/168, but does not establish that the tooling
change caused the difference across different tasks and fresh agents.

The seed test suites are not identical. The language seed inherits previously
documented test-evidence loss; preservation here is relative to report 166 and
does not restore missing older assertions. The F# expiry-overlap failure is
required by this particular seed fixture, so it is not evidence of a language
reliability disadvantage. All four submissions pass independent acceptance.

Language participants used 46 total broker exchanges; F# participants used 41.
Do not treat exchanges as model turns, bytes as tokens, or these four runs as a
reliability rate. No fixed effective context limit or authoritative model-token
measurement was applied. Concurrent local validation excludes causal latency
claims. This study does not test native throughput, memory or mailbox behavior.
Prompt isolation is a workflow constraint, not an OS security boundary; there
is no security probing in this study.

Close this bounded comparison. The next efficacy task should exercise a genuinely
reused abstraction under a materially different change, rather than repeat this
same easy policy boundary or infer a general advantage from passing tests.

## Durable evidence

[Archive index](evidence/173-handoff-window-maintenance/archive.json) records
13,380 entries. [Evidence ZIP](evidence/173-handoff-window-maintenance/evidence.zip):
28,867,970 bytes; SHA-256
`b55b14967f6babf598bbaf45a0ed14194d7369375996ec8c1396982118940deb`.
CRC, every entry hash and all pinned inputs/results were verified. Initial actor
bytes are preserved separately from final result bytes, including changed
CURRENT manifests; replay binaries appear only as pinned inputs. Source, prompts,
traces, accepted control/participant outputs, failed attempts, interrupted partial
runs and preservation audits are retained. Build and temporary trees are excluded.
