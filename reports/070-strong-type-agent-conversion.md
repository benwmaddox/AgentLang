# Fresh-agent use of refined and nominal types

2026-10-06. A fresh external Luna/max agent discovered typed constructors and
accessors, added `delivery.speed-kph : Delivery -> KilometersPerHour`, and
published it as a tested pure library word. Independent acceptance passes
118 checks, including six conversion vectors and four compile-time mismatch
probes. This is a strong-type usability result for one task; it does not
establish an agent cost advantage or complete the business-domain evaluation.

## Fixture and prelaunch controls

The [preparation script](evidence/070-prepare-strong-type-study.ps1) creates the
seed through ordinary Flow define, test and commit operations. Its
[transcript](../experiments/AgentLang.SubagentTrials/strong-type-flow-001/runs/units-001/fixture-setup.json)
records six passing Email-validator tests, 40/40 own instructions and no
branches before library publication. Four persisted types are provided:

- `Email`, a String refinement bound to the persisted pure validator.
- `MetersPerSecond` and `KilometersPerHour`, distinct Float-backed nominal types.
- `Delivery`, with `contact: Email` and `speed: MetersPerSecond` fields.

The email policy requires an at sign and dot, rejects spaces, and rejects
leading/trailing at signs. It is illustrative, not the Internet email standard.
The speed wrappers provide no range refinement, unit algebra or implicit
conversion. Signed and fractional values are part of the public contract.

Fresh source `8d52195` builds with zero warnings/errors. CLI SHA-256 is
`c1a3b1dce20858d51c66a5bec16bbe910694ba1c8c24e8a5869b31e1bd5785c2`;
Core is `3ea76705892258b1009ace0ddcf1b8042c1662a37812c1bb0652e88658930fc1`.
The [prelaunch record](../experiments/AgentLang.SubagentTrials/strong-type-flow-001/runs/units-001/prelaunch.json)
pins the prompt, runtime, host, seed and independent acceptance script before
actor launch. The actor has no inherited turns and is instructed to use only
its isolated project protocol, with no raw repository/oracle access. This is an
instruction boundary, not a claim of hard filesystem isolation. The host has
no effect capabilities, a fixed clock, 100 exchanges and a 120-second exchange
deadline. No cumulative inspection-response cap is enabled.

The independent verifier is checked before launch using a coordinator-created
disposable [correct control](../experiments/AgentLang.SubagentTrials/strong-type-flow-001/runs/units-001/acceptance-preflight.json),
which passes all 118 checks. The
[untouched-seed control](../experiments/AgentLang.SubagentTrials/strong-type-flow-001/runs/units-001/no-op-acceptance.json)
fails the missing-persisted-target gate as intended. An initial preflight
[failure](../experiments/AgentLang.SubagentTrials/strong-type-flow-001/runs/units-001/acceptance-preflight-01.json)
is retained: the coordinator's one-line negative probe put its expression on
the `effects none` line, causing `FLOW_EFFECTS_TRAILING`. The probe was corrected
and rerun before freezing the oracle. This is not an agent error or a language
type-checking failure.

## Verified agent outcome

The durable implementation is:

```text
word delivery.speed-kph(shipment: Delivery) -> KilometersPerHour {
    effects none
    KilometersPerHour::new(float::multiply(MetersPerSecond::value(delivery::speed(shipment)), 3.6))
}
```

Compiled dependencies confirm the record field read, explicit source-unit
unwrap, Float multiplication and destination-unit construction. The new word
has four passing own tests for zero, positive, fractional and negative inputs,
with 6/6 instructions and no branches. All ten project tests pass after reload.
Exactly one new persistent user word is added. Seeded types, validator binding,
word identity/revisions, tests/examples and immutable history remain unchanged.
The agent authored no documentation or examples for its new word; this trial
therefore does not demonstrate their role in later discovery.

The frozen [acceptance script](evidence/070-verify-strong-type-trial.ps1) and
[result](../experiments/AgentLang.SubagentTrials/strong-type-flow-001/runs/units-001/acceptance.json)
verify six independent speeds: 0, 1, 2.5, -3, 0.125 and 100 m/s. Each result must
have nominal type `KilometersPerHour`; explicit unwrapping must yield the
expected Float within the declared numerical tolerance. Valid Email construction
and unwrapping succeed; invalid construction reports `REFINEMENT_FAILED`.

Four independently authored Flow documents are rejected with structured type
errors: cross-unit construction, a wrong-unit Delivery speed, plain String for
its Email field, and a typed Int for the Float-backed constructor. Each document
contains valid sentinel declarations before the error. Word inventory,
task-created names, type-source availability and durable bytes prove that none
of those declarations partially stage. All acceptance runs use disposable copies
or read-only queries; seed and actor-project inventories remain unchanged.

The [trace audit](../experiments/AgentLang.SubagentTrials/strong-type-flow-001/runs/units-001/trace-audit.json)
verifies 17 exchanges, 1,941 request payload bytes, 16,111 selected response
payload bytes and zero diagnostic responses. The
[audit wrapper](evidence/070-audit-strong-type-trace.ps1) also verifies that the
independent oracle and starting inventory have not changed since freeze. Actor
host session 41941 is stopped with Ctrl+C after completion; host and runtime
exit zero without an added JSONL request. No coordinator repair hints are sent.

Schema 1 proves response selection before stdout flush, not delivery or model
consumption. Exact model tokens, turns, context windows and controlled latency
are unavailable. There is no matched conventional trial for this task.

## Implications and remaining work

The agent used explicit nominal boundaries without a compile/test failure or
needing raw source access. The independently rejected programs show that unit
and validation distinctions enforce real constraints rather than serving only
as documentation. Full own coverage is meaningful for the authored six
instructions, but does not prove all behavior or replace independent oracles.
This straight-line task adds no new evidence about branch-heavy library logic.

Clean [main CI](evidence/069-main-ci.json) for source `8d52195` passed all
32 checks; its [artifact](evidence/069-main-validation.json) records
`dirty: false`. This milestone adds experiment artifacts and documentation,
with no runtime, compiler or host behavior change.

The next priority is stateful agent evaluation and expansion of the strong
business vocabulary using existing providers. The full domain, remaining
60-task categories, controlled costs and small-context comparisons remain open.
LLVM, allocator and mailbox research stay deferred. The full PRD goal remains
active; this task does not establish the overall hypothesis.
