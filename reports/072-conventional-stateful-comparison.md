# Conventional stateful reminder comparison

2026-10-06. A fresh external Luna/max agent implemented the reminder task in a
conventional F# project. All 34 independent acceptance checks pass. Paired with
[the language trial](071-stateful-agent-reminder.md), this shows both approaches
can reuse a retained helper and implement the required stateful behavior. It
does not establish a language efficiency advantage.

## Preparation and boundaries

The [preparation script](evidence/072-prepare-conventional-stateful.ps1) creates
nominal `InvoiceId` and `InvoiceStatus`, an `Invoice` record, the retained
`Invoice.reminderPath` helper, an in-memory counted file provider, two helper
tests, and an unimplemented operation. The actor and
[archived seed](../experiments/AgentLang.SubagentTrials/stateful-conventional-001/runs/conventional-001/starting-project/Domain.fs)
are byte-identical before launch. The wrappers distinguish types without
validating IDs or statuses, matching the scoped language fixture.

The agent owns only `Operations.fs` and `SelfTests.fs` through the existing
hash-guarded repository broker. `Domain.fs`, the project file, seed assertions
and test entry point must remain unchanged. The broker is freshly built from
`20388a3`, with zero warnings/errors. CLI SHA-256 is
`53dda8d8e3eb152bfc79d266d3cb22b5267ea98fd646fc1e3c0642bce542d8d3`;
dispatcher SHA-256 is
`1564ebf9c79cc4e23239490d08d6ba2cd65134ca8cc83599715c918e92a18eae`.

The [frozen prelaunch record](../experiments/AgentLang.SubagentTrials/stateful-conventional-001/runs/conventional-001/prelaunch.json)
pins seed inventory, prompt, broker, host and independent oracle. The actor has
no inherited history, 100 allowed exchanges, a 120-second exchange deadline,
and no cumulative inspection-response cap. Only inspect/read/search/patch/
replace/validate are allowed. This is a protocol/instruction boundary, without
an operating-system filesystem sandbox. No coordinator repair hints are given.

A separate [known-correct control](../experiments/AgentLang.SubagentTrials/stateful-conventional-001/runs/conventional-001/acceptance-preflight.json)
passes the oracle before freeze. The
[untouched seed](../experiments/AgentLang.SubagentTrials/stateful-conventional-001/runs/conventional-001/no-op-acceptance.json)
fails the documentation gate. A
[wrong empty-marker control](../experiments/AgentLang.SubagentTrials/stateful-conventional-001/runs/conventional-001/wrong-empty-acceptance.json)
passes its four own tests and example, but independent acceptance rejects its
replacement of an existing empty marker with `queued`. The oracle's indentation
regex and additional state checks were corrected during review before launch.

## Verified result

Source review of [the final operation](../experiments/AgentLang.SubagentTrials/stateful-conventional-001/runs/conventional-001/final-project/Operations.fs)
confirms that its returned helper path feeds all provider operations. Exact
`open` queues a missing marker, reads an existing marker unchanged, and returns
`not-open` for other statuses without provider calls. The actor adds an XML
documentation comment, four assertion-based tests and a runnable example.
Review of [the tests](../experiments/AgentLang.SubagentTrials/stateful-conventional-001/runs/conventional-001/final-project/SelfTests.fs)
confirms actual result, state and operation-count assertions; printed markers
alone are not treated as proof of test quality.

The [independent oracle](evidence/072-verify-conventional-stateful.ps1) runs only
disposable copies. Its [result](../experiments/AgentLang.SubagentTrials/stateful-conventional-001/runs/conventional-001/acceptance.json)
verifies fresh tests/example, unchanged source inventories and seeded files,
the typed API, and these behaviors:

- New open and second independent IDs write `queued`, with one read and one write.
- Repeated, arbitrary and empty markers return exactly, with two reads and no
  writes. An unrelated marker is preserved too.
- Paid, cancelled and uppercase `OPEN` make no provider calls or state changes.
- A JSON state snapshot written in one adapter process is restored in another;
  a repeated call reads the retained marker with no write.
- The fixture's execution wrapper denies either missing permission, both
  missing permissions and a non-open invocation before provider operations.
- Separate compile probes reject plain String for Invoice and swapped nominal
  ID/status fields with `FS0001`, after a valid typed adapter builds.

There is no F# instruction/branch coverage measurement. Snapshot restoration is
fixture JSON serialization and provider reconstruction; AgentLang uses its
named snapshot commands. F# takes an explicit provider argument and its wrapper
can be bypassed by ordinary F# code; this is not equivalent to AgentLang's
enforced runtime effect boundary or a security comparison.

## Paired observations and feedback

Both actors use Luna/max with no inherited turns, nominal invoice values, the
same relative path rule, a retained helper, four own test categories, documentation
and an example. Each is independently accepted. Oracle check totals have
different granularity and must not be ranked against one another.

| Observed protocol measure | AgentLang (071) | Conventional F# |
| --- | ---: | ---: |
| Exchanges | 22 | 12 |
| Request payload bytes | 2,981 | 4,613 |
| Selected response payload bytes | 16,257 | 6,505 |
| Recorded diagnostics | 1 | 0 |

The [trace audit](../experiments/AgentLang.SubagentTrials/stateful-conventional-001/runs/conventional-001/trace-audit.json)
verifies wire framing/hashes, configuration, allowed edits, unchanged frozen
inputs and clean host/runtime exit. Actor-owned session `34185` is closed with
Ctrl+C after completion, without an extra JSONL request. Schema 1 records
selected responses before stdout flush, not model consumption. Neither trial
measures exact model tokens, turns, context windows or controlled latency.
The prompts and tool interfaces differ. AgentLang additionally performs its
library coverage and durable-publication workflow; conventional publication is
source-file persistence. This is one paired feasibility task, not a controlled
performance result.

My assessment: inspectability, checked composition and tested publication work
for this small stateful change. Conventional F# also supports discovery, reuse
and correct implementation, with fewer exchanges here. The pair therefore
supplies no positive efficiency signal for the custom language. Mandatory
coverage does not remove the need for independent state/lifecycle oracles, as
the negative control and 071's path mismatch demonstrate.

Next priority is executable parity for a broader strongly typed business task
sequence, followed by repeated paired trials. Additional tiny success demos
would be weaker evidence than medium tasks with accumulating vocabulary. The
full domain, 60-task suite, controlled model-cost/context measurements and full
PRD verification remain incomplete. LLVM and memory-model research stay deferred.

Clean [main CI](evidence/071-main-ci.json) passed all 32 checks at `20388a3`;
its [artifact](evidence/071-main-validation.json) records `dirty: false`.
This milestone changes only experiment assets, evidence and reports, with no
compiler, runtime or broker behavior change.

Publication follow-up: clean [main CI](evidence/072-main-ci.json) passed all 32
checks at `8066875`; its [artifact](evidence/072-main-validation.json) records
the exact revision and `dirty: false`, separately from the frozen broker pin.
