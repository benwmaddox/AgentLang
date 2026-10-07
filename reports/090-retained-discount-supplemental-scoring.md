# R04 supplemental scoring boundary

Status: scoring criteria recorded before supplemental execution. Implementation
and results are pending. This document does not claim independent acceptance.

## Why a separate score is needed

R04 completed its actor task, but the original frozen verifier stopped before
behavioral evaluation on an undefined `$prior` reference in predecessor checking.
The failed canonical acceptance remains unchanged. The trace audit subsequently
rejected that acceptance identity and did not emit a completed audit JSON. The
original blocked outcome and prompt/teardown deviations must remain visible.

The source review identifies `$priorResult`, loaded immediately before the bad
reference, as the intended predecessor object. Editing the frozen verifier or
replacing canonical acceptance would invalidate the original evidence. A second
canonical invocation is also deliberately stopped by collision guards.

## Supplemental criteria

Use a new, explicitly post-hoc scorer with its own source hash and separate
output paths. Its scope is the unchanged R04/S07 output, not retrospective
certification of the original trial protocol.

- Bind the original prelaunch, global freeze, source snapshots, runtime and
  starting-state records. Verify the actual files and their declared hashes.
- Verify the exact same-block retained S01 predecessor, its acceptance bytes,
  identity and accepted output tree, and the R04 starting tree. Reject altered
  flags, identity, hash or tree before any candidate CLI execution.
- Bind the original failed acceptance and the actor's final inventory. Preserve
  them and the complete canonical run. Execute only an owned scratch copy;
  verify the actor inventory again afterward.
- Validate the target's durable signature, purity and relevant retained-project
  metadata before scoring. Report exactly which preservation and test checks
  are performed; omitted checks cannot be implied by a passing behavior result.
- Apply all 54 cases from the unchanged pinned S07 oracle with independent
  BigInteger arithmetic, exact ordinal raw-kind comparison and signed Int64
  values. Keep missing/unexecuted cases distinct from behavioral failures.
- Save checks, observed outcomes, runtime calls, scorer identity and limitations.
  Label the result `supplemental-post-hoc-behavior`, never frozen acceptance.

Focused controls must include a correct implementation, incorrect discount
arithmetic and invalid binding/provenance rejection before execution. Tests must
exercise predecessor binding, not only discount behavior in an unrelated fixture.

## Interpretation

A passing supplemental score would establish only the unchanged output's
behavior on the specified cases and the checks actually recorded. It cannot
repair the failed original verifier, establish a passing trace audit, erase
prompt-delivery deviations or support a standardized retention-effect claim.
The original actor used retained vocabulary, but reuse alone does not prove
reliability gains. No token, turn, native-memory or performance inference follows.

No supplemental result is recorded yet. Add actual commands, results and saved
artifact references after implementation review and local execution.
