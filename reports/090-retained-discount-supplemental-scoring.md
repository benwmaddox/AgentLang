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

The second production attempt also failed before execution: the corrected tree
serializer called `Get-CanonicalInventoryRows`, while the owned helper is named
`ConvertTo-CanonicalInventoryRows`. Saved evidence records zero runtime calls
and zero of 54 behavior cases, with scorer SHA-256
`13c4174cb31a74be881fe67d419117eafc37828713637dede738e6e84b07e39c`.
The failure is archived byte-for-byte under `phase-02`, with its size and SHA-256
in `phase-02-index.json`. This is another harness failure, not an agent behavior
result. The corrected replay and final source review remain pending.

The third attempt confirmed the frozen actor tree hash and carried-forward R03
starting tree hash, but stopped on a nonexistent `previousAcceptance` property
in the global-freeze schema. It also recorded zero runtime calls and zero
behavior cases. Its reported scorer SHA-256 is
`a0c0f007918e9e85bfec7a3e97995ee388ff03dc0ce058de6f5a2c0830489058`.
Exact evidence is retained in `phase-03` and its index. A separate read-only
review now checks all scorer schema reads against the actual saved artifacts.
Passing synthetic controls have not yet established production integration.

Root repeated `pwsh -NoLogo -NoProfile -File
scripts/Test-RetentionOutputSupplement.ps1` after the schema correction: exit 0,
20 focused controls, 17 rejects before runtime and zero CLI calls. Before/after
source SHA-256 was identical at
`8e5c3f6debb9f2cc34dabfef7e51a6fac517f4a11371df822ef167793f88a313`;
runner before/after remained `b5d1594f...e7deb`. Exact command, full hashes and
console result are archived in phase-04. This check establishes focused-control
behavior only; production integration and independent schema review remain open.

The independent saved-schema review of scorer `8e5c3f6d...a313` found two more
pre-execution blockers despite the passing focused controls. R04's starting
state has no `retentionFallback` property (the prelaunch pin does), and the
global freeze's `tasks` value is an array of path/hash rows, not an object with
an `S07` member. The correction must use the actual pin plus explicit starting
outcome and select exactly one task row by its pinned path. Neither mismatch
is evidence about the agent's authored solution. This review demonstrates why
actual immutable-input integration is required in addition to synthetic controls.

Root also rechecked the original R04 prelaunch inventory during this review:
all 23 source/snapshot pairs matched their saved SHA-256 and byte lengths, and
all 26 pinned runtime files matched. The original canonical acceptance SHA-256
remains `0e6638878f89fe58c0859787f051652a6a6227e804192628cce75d80c71a5888`.
This is an unchanged-input check, not behavior acceptance.

The bounded independent schema review is complete at scorer SHA-256
`4dd2de3279fefdcb28b64e7f269518bbadf6b3ed9e203ba40128e2e8e338a7e8`.
It confirmed both schema blockers above still exist in that exact source, so
that hash must not be replayed. Other reviewed predecessor, normalized-path,
runtime-field, flat-inventory and early-failure initialization checks align with
the saved records. Root's focused runner again passed 20 controls on that hash,
with matching before/after source hashes; this reinforces the controls' limited
scope. Fix both blockers and rerun controls before production execution.
