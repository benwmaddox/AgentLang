# 059 — Preparing the repeated external-agent comparison

Status: verified interface and repeat-study preparation, 2026-10-06.

Report 057's exploratory comparison used whole-file replacement in the
conventional arm. Later requests included growing test/source payloads, so
request-byte differences cannot establish a language cost advantage. This
milestone adds a small conventional text patch before another matched sequence.

## Interface and study preparation

The [patch plan](../docs/CONVENTIONAL-PATCH-PLAN.md) specifies exact ordinal
unique-anchor editing, mandatory expected file hash, atomic confined writes,
strict UTF-8 and size checks, unchanged rejection state and metadata-only logs.
It changes an external experiment tool, not language semantics. Full-file
replacement remains available.

The [repeat package](../experiments/AgentLang.SubagentTrials/early-flow-002/README.md)
archives common instructions and separate language/conventional primers. The
language primer uses compact inventory and described Flow references; the F#
primer exposes the patch contract. Exact per-trial prompts, binary/source pins,
starting state and host configuration must be saved before launching. The
same five public tasks and independent acceptance rules remain in use. Trials
will run serially in rotated arm order, with a fresh zero-history Luna/max agent
per task. Coordinator teardown is identified separately from agent work.

No repeated trial has started. The archived pilot-001 assets and results stay
frozen. Interface preparation is not comparative evidence.

## Validation and publication evidence

Fresh `pwsh -NoProfile -File scripts/Validate.ps1` passed all **32 required
Release checks** with zero build warnings/errors. The saved
[local validation](evidence/059-local-validation.json) records dirty parent
`8dc9a0c`; clean committed-source CI is separate. The direct conventional suite
passes **167 assertions**, and its CLI suite **7 groups / 322 assertions**.
These cover actual edits and reads, exact BOM/CRLF/Unicode preservation, stale
hashes before anchor lookup, empty/missing/overlapping ambiguous anchors,
malformed arguments, confinement, encoding/size rejection without writes or
temporary files, metadata-only ordered logs, top-level/nested JSONL recovery
and request limits. Existing full replacement tests remain intact. The complete
gate also passes the unchanged language, IR, persistence and experiment fixtures.
No language IR/storage version changes occur. The patch retains the existing
writer's documented external filesystem race and operating-system sandbox limits.

An initial run caught a test-fixture problem: F# emits the `"\uD800"` literal as
valid U+FFFD, so that input cannot test malformed UTF-16 rejection. The
[pure probe](evidence/059-surrogate-fixture-probe.json) distinguishes it from a
runtime-constructed U+D800. The corrected test asserts the actual code unit
and its JsonValue round-trip before requiring strict encoder rejection; the
subsequent complete gate passes. This required a test repair, not a patch
implementation change. An [independent review](evidence/059-independent-review.json)
found no confirmed source/contract defect; fresh validation is authoritative.

The preceding milestone has separate clean committed-source evidence:
[main CI identity](evidence/058-main-ci.json) and
[full validation](evidence/058-main-validation.json). Run 37490952224 passed all
32 required checks on clean `8dc9a0c5678d7acabd2809088546a5a09c45826f`.

## Research limits

Even with these fixes, protocol bytes and exchanges are proxies, not exact model
tokens or turns. Library full own instruction/branch coverage and conventional
self-tests impose different publication policies. Both arms require meaningful
tests and pass the same behavioral oracle; the structural language checks are
additional. The small String/Float domain does not cover all strong-type or
full-business requirements. Rotated repetition is the next research checkpoint;
full PRD success, context-budget studies, memory and native backends remain open.
