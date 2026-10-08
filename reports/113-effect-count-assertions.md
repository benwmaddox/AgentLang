# Authored effect-count assertions

Implementation validated locally: all 37 full Debug gate checks pass. This is not
an efficacy result. The subsequent [adoption probe](114-effect-assertion-adoption.md)
reports behavior repair but unsuccessful discovery of the assertion feature.

## Behavior

Flow/2 tests can add an optional exact provider-count assertion after their value
or runtime-error expectation:

```flow
=> "held" effects { fs.read: 2; fs.write: 0; }
```

Counts cover actual invocations of the attached authored Flow/2 function and its
nested helpers/callbacks, identified by verified function ID and revision.
Repeated invocations aggregate. Setup provider calls outside the target and pure
expectation evaluation are excluded; calling the target during setup still
counts. The target must run at least once. Provider attempts that fault count,
and the interpreter closes the scope in `finally`.

The supported virtual categories are fs.read, fs.write, clock.read and
console.write. Omitted categories mean zero. Other categories, Flow/1 owners,
examples, generated/primitive owners and malformed counts are explicitly rejected.
No new capability is granted, and expectation expressions remain statically pure.

Value/error and effect verdicts are combined without losing either diagnostic.
A mismatched map reports `TEST_EFFECT_ASSERTION_FAILED` with expected/actual maps,
invocation count and source span. It cannot be swallowed as an expected runtime
error. Tests without the suffix retain their existing response shape and behavior.
Existing persistence stores the authored suffix; no manifest or native ABI change
is required. Rename/reload retains its enforcement. Runtime help advertises the
feature for Flow/2.

## Focused validation

- Flow parser/lowering: 1,104 assertions, including canonical rendering, bounds,
  unsupported categories and owner-version refresh.
- IR interpreter: 71 assertions, including exact ID/revision, nested/list callback
  scopes, provider/runtime/fuel faults and denied preflight before entry.
- IR verifier: 132 assertions.
- Flow Runtime: 28 groups / 920 assertions, including simultaneous wrong-value and
  wrong-effect failures, repeated/nested counts, fault attempts, zero invocations,
  pure expectations, legacy JSON, reload/rename and rejected library replacement.
- Active Flow call-binding script: 31 assertions.
- Native-mailbox adapter builds with fresh isolated dependencies, zero warnings
  and errors. This is host API compatibility, not native effect-assertion execution.
- The documentation example was executed through JSONL and reported one read,
  zero target writes and one target invocation despite its setup write. An initial
  launch used the wrong isolated artifact path and failed before runtime startup;
  the corrected path is the one recorded in the build log.

The scripted extra-write case is covered by the runtime tests: return-only tests
can pass, while an opted-in zero-write assertion rejects the write and blocks
library replacement. The full Debug gate passed all 37 checks, including business-policy preflight: 98 checks across 30 independent outcomes.

## Research boundary and next check

This makes one observed test weakness expressible through the language. It does
not establish that agents will discover or use the feature, or that edits are
more reliable than F#. The next bounded check uses a fresh subagent against a
separate copy of report 111's extra-write control. It asks for a repair and useful
regression tests, without teaching this suffix in the prompt, and independently
checks whether the resulting tests detect the reintroduced defect.

Finite input/return coverage, complete-record construction invariants and scoped
dictionary overrides remain separate pending requirements. Count assertions do
not establish final state, event order or correctness for every input.

## Full gate provenance

Command: `./scripts/Validate.ps1 -Configuration Debug -ReportPath .agentlang/effect-assertions-001/full-validation.json`.

The run began on dirty revision `502011e6` and tested the source subsequently
committed as `a8e7d34`. It exited zero after 37 passing checks. No source changed
after that checkpoint; subsequent edits update documentation and evidence only.
The solution build reported zero warnings and errors. All ten frozen Release
artifacts from report 108 remain unchanged. Evidence includes the full gate
summary/log, archived auxiliary JSON outputs, focused logs and a SHA-256 index.
