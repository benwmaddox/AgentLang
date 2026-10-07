# Uniform retention study repair

Status: implementation and candidate controls in progress; no 004 freeze or
actor launch is claimed.

The original 003 study's stop rule withholds launches after preflight or audit
failure. R04 hit the undefined `$prior` in the retained-predecessor verifier
branch, then its trace audit failed. Its completed actor, canonical failure and
documented prompt/teardown deviations remain unchanged. Supplemental scoring
in report 090 is separate and cannot lift that original outcome into acceptance.

Choose a fresh `business-policy-retention-004` rather than mixing repaired and
original verifier versions in one primary result table. Run all twelve fresh
actors in the same two-block Flat/Retained/Reset-rich order, with identical task
texts, primer, oracle, baseline bytes and pinned Release runtime. Do not import
003 actor output or count old outcomes as 004 cells. Keep the original 003
report and archived evidence available alongside the new study.

Implementation owns five new V4 runners and a sibling study root. Shared host,
termination auditor and bootstrapper remain unchanged and pinned. Fix the
undefined predecessor reference in the new verifier only. The new freeze binds
its own scripts, study artifacts, baseline/source archives and full runtime
inventory; changing `LocalRoot` on the old scripts is not a versioned repair.

Required candidate controls cover actual accepted predecessor transfer,
explicit failed-S01 baseline fallback, wrong identity/hash/tree, missing audit,
altered passed/frozen flags and tampered pins rejected before candidate CLI
execution. Require intended diagnostics, not an arbitrary thrown exception.
Use a dedicated control slot and preserve failed attempts.

Delivery gate: candidate controls and independent review; commit source;
archive and commit source/baselines; create a global freeze from a clean tree;
commit it; run frozen controls; then prepare and freeze each actor from the
same clean HEAD. Record independent verification before coordinator-authorized
close and require trace/termination audit for each completed cell. Stop on
failed preflight or audit. Actual commands, counts, source hashes and evidence
will be appended after execution.

Runtime source remains pinned to build `8bff657bc4a4a0c2406a1ed3b5448f4b2ae4902c`.
The current freeze checks source changes under `src`, Business and build
props/targets as well as binaries; Debug-only builds do not bypass this boundary.
The approved Flow/2 implementation follows the bounded review or requires an
explicit new runtime/fixture study version. No interpreter memory result is
treated as native arena/mailbox evidence.

The original research hypothesis and full PRD scope remain unchanged. A uniform
rerun may still fail or show no benefit; this repair improves the evidence path
and is not evidence for vocabulary retention by itself.

## Independent draft review

Root's read-only audit found that the first V4 frozen-reference draft still
used canonical `runs/R01`, a normal launchable actor pin, and
`frozen-actor-acceptance` for a deterministic reference. Requiring later cleanup
would not satisfy the dedicated-control-slot requirement above. This draft is
not launch-ready and no control result is counted as an actor outcome.

The repair will give `M00` an explicit non-actor identity and separate control
paths across Prepare, Freeze, Verify and Audit, preserving the real R01
actor destination. The predecessor reject controls must also validate malformed
inputs before the preparer's live baseline CLI gate, so zero-runtime rejection
is demonstrated rather than inferred from a later failure. Both changes still
require actual local controls and review before the source-freeze lifecycle.
