# R04 supplemental scoring boundary

Status: scoring criteria recorded before supplemental execution. Implementation
is under review and focused validation; production results remain pending.
This document does not claim independent acceptance.

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

## Development validation before production scoring

A read-only review of draft SHA-256
`239b3bc3dc1d415779e3e1e474421b22447aa9a80e5beb122f82fe66d0cd3dfd`
found a directory passed to a leaf-file hash helper, an undefined evidence-field
variable, and missing actual-comparator/predecessor-flag controls. These were
sent to the implementation owner for correction. This is a draft review, not
certification of a later script revision.

Root ran `pwsh -NoProfile -File scripts/Score-RetentionOutputSupplement.ps1 -SelfTest`:

| Draft SHA-256 (unchanged during invocation) | Exit | Observed failure |
| --- | --- | --- |
| `68e70fab99c2f256fb488c29470967bd79f3b13087fab8635cc4ed603f9be57d` | 1 | Max-Int64 fixture expected value disagreed with independent BigInteger arithmetic |
| `f099caddf3eba17df758b9d582d247caaf38b3f0500137dbfac738582c336639` | 1 | Rounding-discriminator control failed |

For the latter failure, a diagnostic invocation of the actual comparator with a
structured Money value of 8 and an expected value of 8 returned `passed=true`.
The fixture incorrectly treated nearest rounding of 90% of 9 as 9; it is 8,
the same as truncation. A proper discriminator such as 90% of 1 gives nearest
1 versus truncation 0. These failures concern scorer test fixtures, not R04
business behavior. No production scratch replay or canonical acceptance repair
is claimed by these checks. Passing focused controls and a reviewed result
writer are still required before using any supplemental outcome.

The revised focused runner subsequently passed locally:
`pwsh -NoProfile -File scripts/Test-RetentionOutputSupplement.ps1` exited 0,
reporting 20 controls, 17 rejection cases before the guarded action, and zero
actual CLI calls. Tested scorer SHA-256:
`710ccd4f21a8850ddd988947c63699b26b25b5ef7e24a2b2be75e944dfe90fef`;
runner SHA-256:
`b5d1594f1675d6121a3dd3d07eb9f41ec8089b5cd483bbac2ff1f6b39aee7deb`.
Console: `.agentlang/business-policy-retention-003/evidence/090-root-focused-controls-710ccd4f21a8.console.txt`.
These are synthetic focused controls, not the 54-case R04 behavior evaluation.
Final source review and an immutable scratch replay remain required before a
production supplemental result can be recorded.

The first production scratch replay exited 1 before any CLI calls or behavior
cases. Its saved failure is the starting-state path comparison (relative pin
path versus absolute saved path). Development also identified that the scorer's
local tree-hash encoding did not match the frozen verifier's encoding; correct
compatibility is required rather than bypassing that check. The failure evidence
reports scorer SHA-256 `9df5db29aa7e3c4610ad8d446ec80259c629f448de86551d61d3c3ff4cc274bc`;
an external before/after source hash was not captured for that invocation.

Root independently compared all 312 actor files against the committed original
R04 final-project archive, including count, length and SHA-256: all still match.
The failed evidence also reports unchanged canonical acceptance bytes. Archived
the first failure, control consoles, original criteria pin and exact original
criteria from commit b902027 under
`reports/evidence/090-supplemental/phase-01/`, with six file hashes in
`reports/evidence/090-supplemental/phase-01-index.json`. The criteria bytes match
the pre-execution pin. This archive preserves a failed attempt, not behavior
acceptance; corrected replay and final source review remain pending.
