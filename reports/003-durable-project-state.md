# Milestone 003: durable project state and editing foundations

Date: 2026-10-04
Status: in progress; no milestone pass claimed.

## Planned scope

Stable word identities, durable source/test/example revisions, named snapshots, semantic vocabulary maintenance, and exact task rollback across interim commits. A canonical AST source renderer will support reference rewrites. A separate conventional repository tool foundation will support later controlled comparisons.

The storage module owns hash/path/schema checks and atomic publication. Runtime retains full parsing, type/effect checks, validator freezing, test gates, and scoped durable projection. One authoritative pointer is the transaction boundary; readable dictionary export is regenerable. Export failure after publication must report a committed state with a warning. Corrupt authoritative storage must fail loading, rather than silently use an older export.

## Acceptance criteria

- Reload multiple revisions with stable IDs and inspect/diff their sources and metadata.
- Rename calls, static callbacks and attached cases semantically while retaining identity, behavior, and caller safety.
- Preserve unrelated staged replacements and failing metadata during scoped commits.
- Abort after interim commits and restore the initial authority/history without adding rollback word revisions.
- Restore named snapshots with virtual filesystem/clock state while retaining current host capabilities.
- Reject tampered, missing, escaping or incompatible storage without publishing partial state.
- Exercise failures before publication and export failures after publication.
- Validate conventional path confinement, stale-edit refusal, deterministic search and fixed validation commands independently.

## Evidence and feedback

Planning review identified a critical transaction hazard: reporting export failure as a failed commit after publishing the manifest leaves disk and memory inconsistent. The design makes publication explicit and separates export warnings from failed commits. Generation checks must run under an exclusive writer lock, and legacy/empty restoration must preserve concurrency fencing without fabricating historical word revisions.

The existing external harness captures only the dictionary export and task log files. It must migrate to authoritative storage snapshots before the new persistence behavior can be accepted; restoring the export alone would no longer roll back vocabulary.

Validation and implementation results will be added when observed. Live model comparisons remain pending; scripted infrastructure checks are not research results.

First independent checkpoint: `dotnet run --project tests/AgentLang.Source.Tests` passed **47 assertions**. Tests cover canonical parser round trips, targeted semantic renames, and unchanged direct-call/list-callback execution. The renderer preserves Float literal type, quoted values, and nominal/container signatures; it rewrites executable calls and static callbacks while keeping literal text unchanged. This checkpoint validates the renderer, not the full storage/maintenance milestone.

Architecture steering: the owner requested fresh external subagents for AI experiments and a conditional later split between live development (interpreter/REPL/introspection/hot replacement plus LLVM JIT) and native release (LLVM AOT plus minimal runtime). The PRD now requires authoritative typed semantic IR; checked-AST execution remains an explicit gap. LLVM remains outside V1. This documentation change does not claim backend implementation or native performance.

Second independent source checkpoint: the source test runner passed **56 assertions** after introducing `ExpectedValue`/`ExpectedRuntimeError` test expectations. These checks cover canonical expected-error syntax and rejection of malformed codes, empty error-test bodies, error expectations in examples, and unresolved/static-invalid test bodies. Runtime matching and negative coverage gates are being validated separately.

The final scoped source check passed **57 assertions**. Independent conventional tool validation passed **59 assertions**, including stale-edit refusal, path/Windows alias checks, deterministic bounded search, and fixed validation command outcomes. A Windows overwrite failure found during implementation was fixed by closing the temporary file before atomic replacement. Conventional validation executes F# with host permissions; it is explicitly not an OS sandbox. Neither check constitutes a complete milestone pass or an agent comparison.

The documentation milestone was committed and pushed to private `main` as **85fa534148c0dbb1c9e41bf2c5cd1b57fcffe7d6**. [CI passed](https://github.com/benwmaddox/AgentLang/actions/runs/37250058205). This commit includes the backend plan, subagent evaluation policy, and prior CI evidence; in-progress milestone-003 implementation is not in that commit.

An exploratory subagent attempt exposed two host issues before any language work: sandbox shell startup fails on this workstation, and unified-exec process IDs cannot be shared across agents. The initial attempt made no vocabulary changes. A fresh retry starts its own pinned runtime process and will keep the task open while the host checks persisted words independently. These are infrastructure findings, not language or task-correctness failures. Trial artifacts are retained under `.agentlang/subagent-pilot-6d9e341/`.

Current checkpoint: the storage-only project passed **8 groups / 64 assertions** in an independent full-project run after Core compilation resumed. The harness worker reported **86 assertions** and conventional tooling **63 assertions**; coordinating review then found a linked-history path gap in harness capture/restore. The fix rejects linked ancestors/files and bounds history reads; the worker's focused suite passed **98 assertions**, including external-history preservation regressions. The coordinating rerun was blocked by a subsequent in-progress Runtime type-inference error, so final integrated acceptance remains unproven. Initial Runtime load/commit integration compiled at an earlier checkpoint; named snapshots, durable history commands, semantic maintenance, and their full acceptance checks remain in progress.

The separate [two-agent pilot report](004-subagent-vocabulary-pilot.md) now records both independently accepted tasks, with 16 and 17 host checks. It uses the pinned milestone-002 runtime; it does not validate the new durable-runtime work. The reviewed IR migration proposal uses resolved snapshot-local nominal identities and preserves Storage v1 because type/field names are immutable; a new type-ID schema migration is deferred until a concrete need exists.

The coordinating rerun now passes **98 harness assertions**, and independent Release conventional validation passes **63 assertions**. Runtime maintenance regression testing found that replacing a word could succeed even when a dependent caller's attached test failed against the replacement. The replacement guard is being corrected before integrated acceptance and publication. These focused passes do not yet establish a complete milestone pass.
