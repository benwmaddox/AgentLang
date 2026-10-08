# Finite-coverage adoption by a fresh agent

One fresh external Luna/max subagent completed the bounded task: it added tests for both
mixed Boolean input combinations, published `flags.agree` as a library function, and left
`selection.accept` at project maturity with an accurate explanation of its
unreachable declared return alternative. Independent checks passed all 14
behavior cases. Its tests rejected both predeclared incorrect implementations.
This supports usability of the coverage feedback, not comparative superiority.

## Design fixed before dispatch

The runtime was freshly built from `a81a7aad74119f48d52f851e629d96f702ffda5d`
into an isolated Debug directory. All nine runtime files, the broker, seed,
actor prompt, acceptance plan, verifier and two mutants were hashed before
launch. The actor received no conversation history, hidden oracle, mutation
body or answer hints. It used the existing JSONL broker with versioned Flow/2
help; only its isolated dictionary was editable. The limit was 100 exchanges.

The seed contained two correct project functions and four passing tests.
`flags.agree` implements Boolean equality, initially tested only on equal pairs.
`selection.accept` preserves an existing first item or fills an empty selection;
its return record declares `first: Option<Int>` although it always returns Some.
The task required preservation of signatures and behavior, useful tests, honest
library qualification and a committed task session. Mutation resistance was a
separate measurement, not an additional post-hoc success criterion.

Before dispatch, 27 control checks passed. A complete four-row Boolean suite
rejected both mutants. A three-row suite with only the true/false off-diagonal
qualified as library, but accepted the mutant wrong on false/true. An independent
truth-table check caught that error. The complete-control scoring smoke also
passed all four Boolean rows and ten Selection cases. Coordinator review corrected
harness issues before freezing, including a self-comparison in the attachment
preservation check and an acceptance rule that had conflated literal source
patterns with execution evidence. The review notes are retained.

## Observed outcome

| Function | Agent action | Verified result |
| --- | --- | --- |
| `flags.agree` | Added false/true and true/false tests | Four tests pass; full finite input/return evidence; persistent library |
| `selection.accept` | Added one test, attempted library publication, then committed project maturity | Three tests pass; both branches covered; None remains correctly missing |

Function IDs, exact body hashes, type definitions and original test hashes were
preserved. The final suite passed 7/7 tests. Independent scoring passed four
Boolean combinations and ten Selection cases, including zero, negative values
and Int64 boundaries. Scoring used disposable copies and left the actor project
unchanged. The actor explained that producing None would violate the current
behavior; it did not weaken the signature or fabricate qualifying evidence.

There is also a test-quality limitation: the added Selection test repeats the
existing Some(5), incoming 11, expected Some(5) case. It adds no new behavioral
case despite its more descriptive name. Passing gates do not ensure that every
additional test earns its maintenance cost.

Both frozen word-only mutants were rejected with `COMMIT_TESTS_FAILED` while
retaining the actor's tests. One is wrong only for true/false, the other only for
false/true. Both refusals preserved durable state and attached test hashes.
The actor chose the full four-row suite even though the current per-parameter
and finite-return gate could be satisfied with three rows. This is an observed
choice by one actor, not a guarantee supplied by the language.

## Interaction and friction

The broker recorded 42 exchanges over 254.6 seconds: 2,038 request payload bytes
and 58,522 response payload bytes. These are transport bytes, not model tokens;
provider token usage and model turn counts were unavailable. Both broker and
runtime exited with code zero and no runtime stderr.

The actor recovered from two discovery errors: a describe request using the
wrong field and an unsupported describe help topic. The third error was the
expected finite-coverage refusal for Selection. Initial sandbox file/process
operations failed while applying deny-read ACLs. A recorded coordinator message
permitted the already-authorized host fallback for the exact prompt read and
broker launch; it supplied no task guidance. Accordingly this was not entirely
unassisted transport, even though the task solution was independent.

## Interpretation and next step

This result supports a specific capability: an agent can inspect finite evidence,
add meaningful missing cases and accept an honest qualification boundary. It
also exposes two limits. Per-parameter coverage does not cover all interactions,
and duplicate tests remain possible. The scripted escaped mutant provides a
concrete reason to evaluate bounded Cartesian input coverage; it does not by
itself settle that policy or establish MC/DC.

For the next structural correctness slice, prefer explicit populated-state return
types where behavior guarantees presence, rather than weakening the gate for an
unreachable Option alternative. Keep that separate from stronger tests of legal
state transitions. Module visibility and complete library dependency closure,
provider-state assertions, native enum support and production mailbox behavior
remain pending. None is implied by this adoption result.

This single guided task has no F# comparator and establishes no general
reliability advantage, vocabulary-retention benefit, causal coverage effect,
context saving or native-runtime performance claim.

## Local validation and evidence

Commands from the canonical checkout:

```powershell
& .agentlang/finite-adoption-001/verifiers/Verify-FiniteCoverageAdoption.ps1 -Mode preflight
& .agentlang/finite-adoption-001/verifiers/Verify-FiniteCoverageAdoption.ps1 -Mode score -ActorProject .agentlang/finite-adoption-001/project/actor-project -OutputDirectory .agentlang/finite-adoption-001/scoring
& .agentlang/finite-adoption-001/Summarize-Trace.ps1
```

The source revision already passed the 37-check full local gate in
[report 116](116-finite-library-coverage.md). This evidence-only milestone changes
no product code and does not rerun that entire gate. Fresh runtime build,
preflight controls, independent scoring, frozen-file audit and publication diff
checks ran locally. CI remains manual-only.

[Evidence index](evidence/117-finite-coverage-adoption/index.json) identifies the
exact prompt, dispatch, raw trace, actor final response, frozen oracle/mutants,
preflight controls, scoring copies and integrity review. Archived project state
and raw logs retain their bytes; runtime binaries are excluded. Replaying the
recorded commands requires restoring artifacts to their original trial layout
and supplying the pinned runtime or documenting a new runtime build.
