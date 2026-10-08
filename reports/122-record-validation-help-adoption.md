# 122 — Record-invariant qualification; updated help not reached

A fresh external Luna/max agent implemented the requested `BatchBounds` invariant
and committed both its validator and `batch.span` at library maturity. Independent
reload verification passed all ten frozen cases and all six agent-written tests.
The persisted validator identity matches the reloaded predicate. This is a
successful bounded behavior-and-qualification result.

The intended guidance question was not tested: every help request omitted
`syntaxVersion` and returned Flow/1. The agent never saw report 121's Flow/2
worked example. The setup allowed that mismatch; its primer mentioned versioned
help but did not supply the explicit version-2 request used in earlier trials.
Do not attribute this outcome to the new guidance.

## Fixed task and process

The empty isolated project required `minimum >= 1`, `maximum >= minimum`, equal
endpoints allowed, invalid construction rejected, and span `maximum - minimum`.
Ten oracle cases, the public prompt, broker and 22 runtime files were fixed before
dispatch. The runtime came from the tested report 121 build, published as
`ee97dda`. A fresh agent received no inherited conversation or coordinator hints,
only the existing JSONL broker and a 100-exchange limit. Its session closed normally.

Preflight's correct control passed four tests and all ten cases. An always-true
validator failed both rejection tests and admitted all six invalid oracle values.
The first harness attempt read a coverage flag at the wrong JSON level; corrected
runs and the original failure are retained. A final supplemental assertion also
checked that the faulty predicate's finite coverage was incomplete. This occurred
before dispatch and did not change the public task, oracle or runtime.

The agent initially placed its rejection tests on `batch.span`. Committing span
as library failed with `COMMIT_TEST_REQUIRED` for the untested validator. It added
one validator-owned valid case and one validator-owned rejected construction,
then successfully committed the validator and span as libraries. The rejection
case observed the predicate's false return before constructor rejection.

## Independent result

| Check | Result |
| --- | --- |
| Four valid inputs, including equal endpoints | Correct spans |
| Six invalid inputs | Direct construction rejected with `RECORD_VALIDATION_FAILED` |
| Agent-owned tests after reload | 6/6 passing |
| Validator-owned tests | One valid case and one rejected construction; false and true observed |
| Span-owned passing value tests | Two; plus two rejected-construction tests |
| Authored validator and span maturity | Both library |
| Fields, signatures, validator purity | Match the task |
| Durable constructor validator target | Matches manifest and reloaded predicate WordId |
| Scoring isolation | Original actor project's 17 files unchanged |

The scorer ran in a fresh process over a copied committed project, using the
pinned runtime. Its source-selector and manifest checks were reviewed before
execution; the first scoring run passed. Runtime correctness is independently
supported for these cases, not inferred from the agent's final answer. Its final
answer attributes the initial library-commit failure to missing validator tests;
the trace identifies the candidate as `batch.span`, blocked on
`batch.bounds-valid`.

## Process observations and limits

The broker recorded 47 exchanges, 11 structured error responses, 7,402 request
payload bytes and 59,235 response payload bytes. Full session duration was
391.323416 seconds, including startup idle and close. These bytes are not model
tokens; provider token/turn usage was unavailable.

Errors included two discovery queries, five definition attempts, the dependency
test gate and three source/type inspection requests. All three help calls returned
syntax version 1. The final source inspection still tried a record through the
word selector and did not retrieve its type declaration. Runtime inspection and
authoring ergonomics remain material friction despite the completed implementation.

This is one bounded adoption observation, with small Int values and no overflow
claim. It does not establish a comparative reliability, efficiency or guidance
benefit. The task differs from report 120. Passing coverage does not prove all
condition interactions or business policy correct.

Next run a tightly scoped help-exposure follow-up with an explicit
`{"op":"help","topic":"define","syntaxVersion":2}` request in the primer and
verify the returned version. Keep the runtime, task, oracle and boundaries fixed;
report any outcome as another observation, not a causal estimate from two agents.
Also retain the open construction-input type question and the approved remaining
provider-state, module closure and scoped dictionary-override work.

## Evidence and validation scope

The exact local scorer command was:

```powershell
pwsh -NoProfile -File .agentlang/record-validation-help-adoption-001/scoring/Score-Actor.ps1 -ActorProjectPath .agentlang/record-validation-help-adoption-001/project/actor -RunId actor-01
```

It exited zero. Protocol traces, fixed inputs, controls, persisted project,
scoring copy, review, timing and the unchanged actor final statement are archived
under [the evidence index](evidence/122-record-validation-help-adoption/index.json).
The 22 binary artifacts are identified by hashes rather than duplicated in Git.
The final freeze manifest hash matches `dispatch.json`. Prior frozen runtimes
were unchanged. No product source changed in this reporting milestone; report
121's fresh build and focused 1,168 assertions remain its validation. No new full
suite, native or comparative-performance result is claimed.
