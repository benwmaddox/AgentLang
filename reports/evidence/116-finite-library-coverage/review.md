# Read-only finite-coverage implementation review

The Core evaluator and runtime hook compiled at the worker's first checkpoint. This review records concrete observations while lifecycle wiring remains in progress; it is not a release verdict.

## Domain and observation boundary

- Fully finite records form a bounded Cartesian product. For mixed/open records, the agreed policy is independent finite field projections and nested tag obligations; a nested fully finite record field still retains its whole domain. The current `OpenRecord` recursion follows this policy. My earlier concern about `Record<Bool,Bool,Int>` cross-pairs was withdrawn after root clarified the approved contract.
- `Option`/`Result` observations now check embedded generic type metadata against the declared IR type before crediting a tag or value. This closed an earlier direct-analyzer mismatch concern.
- The interpreter passes lazy entry and normal-return decoders, and runtime matches `(WordId, revision)` for each own test independently of effect assertions. The qualification fold uses only `Passed` own results. Expected-expression evaluation uses a separate isolated trace, so it cannot supply target observations.

## Concrete regression found and corrected in progress

The first `coverageJson` draft called `finiteCoverageEvidence` for generated accessor test targets, but generated IDs are in `GeneratedTargetsById`, disjoint from user `FunctionsById`. Existing `test receipt.amount` cases would have returned `LIBRARY_COVERAGE_TARGET_MISSING`. The worker added a user-function guard before serializing `finiteCoverage`; verify with existing generated accessor test/reload cases in `tests/AgentLang.Flow.Runtime.Tests/Program.fs` near 1104, 1126, and 1207.

## Lifecycle re-review

The worker connected `loadProject` and requalification before activation (`Runtime.fs` around 2365–2387). `snapshot.load` validates a proposed executable and requalifies its libraries before `Storage.restoreSnapshot` (around 5131–5137), restoring the prior clock in `finally`. Commit validates selected library words and affected library callers on the durable proposed snapshot (around 3112–3116). Rename/rewrite and deprecation now call the same central gate on their exact proposed snapshots (around 4763 and 4888). These resolve the lifecycle omissions at the earlier checkpoint. I found no further concrete bypass in those paths during source review; focused runtime acceptance and reload tests remain the validation gate.

## Final evidence finding (reported to worker)

A passing `=> error CODE` test may make one successful target call before its final expected fault. The first final-audit revision retained that call's `TargetReturns` and credited it because the test passed. The worker corrected the matching-error branch to clear `TargetReturns` while preserving actual inputs/invocation count (`Runtime.fs` around 2270–2272). The focused Flow runtime regression calls a Bool target normally, faults afterward, and verifies that library publication still reports the missing `false` return (`Program.fs` around 2139–2152). The worker reports the Flow runtime suite passing; this source review found no other concrete evidence or publication blocker.
