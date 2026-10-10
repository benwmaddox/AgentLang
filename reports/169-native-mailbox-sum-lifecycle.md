# Native mailbox lifecycle for nominal Option/Result payloads

2026-10-10. Completed engineering conformance checkpoint. Native integration
and all 37 full local Release checks pass. This is not an independent
agent-efficacy trial.

## Change

The native value-stack backend already supports inline Option/Result payloads.
The previous mailbox integration fixture uses Strings, so it does not exercise
those payloads through publication, suspension, import and resume. The new
[source](../experiments/AgentLang.OwningMailbox/owning-mailbox-sums.flow) keeps
`State.completion: Result<Chunk, String>` and
`Continuation.pending: Option<Chunk>`, with nominal `Chunk.text: String`.

Both RETURN and KEEP_ASSOCIATED run the same compiler-verified semantic IR.
Ordinary helper calls extract Some/None and Ok/Error; a second turn consumes an
actual Error state and returns Ok. Unicode, embedded NUL and empty payloads have
independent literal byte oracles. Layout indexes come from emitted metadata.
The original String source and fixture are unchanged. No product emitter,
runtime implementation or ABI changes are made: layout ABI 3, stack ABI 1 and
module ABI 1 remain current.

## Failure and memory checks

A handler failure after helper matches and an output one byte larger than its
retained bank preserve real before/after logical roots, raw mailbox state and
pending token; the same token is retried. Invalid active tags, active nested
String lengths and inactive Result alternatives must reject at public boundaries
without modifying scanner/controller output sentinels. The checks run in
diagnostic, fast-reset and trusted-generated host profiles at O0/O2.

Expected serialized bytes and counters were derived and reviewed before native
execution. Per policy, the full sequence has 13 dispatched handlers including two
expected failures, 872 logical construction-copy bytes and zero payload moves.
RETURN versus KEEP expects staging 168/184 bytes, imports 464/256 bytes and
publication 296/160 bytes. These count runtime-requested extents, not hardware
memory traffic or throughput. Constructors still copy inline values.

Pre-execution review corrected harness assumptions about Option case rows, KEEP
bank contents and RETURN associated roots; fixed the Unicode completion oracle;
and replaced two post-failure observations with distinct before/after captures.
The malformed nested String mutation now targets its length header at byte 8.
Setup-only Clang link failures are retained separately from execution evidence.

Fresh gate attempts also exposed metadata-display and empty-array binding
mistakes in the PowerShell harness; the first native sums runner exited zero
before its output validation aborted. A subsequent attempt exposed a pre-existing
lifetime bug in the original String C runner: recorded check names pointed to a
loop-local buffer, corrupting later JSON output at O2. The original runner
now owns its bounded check-name storage. Its behavior
fixture and pinned runtime counters remain unchanged. Failed reports remain
evidence, not accepted gate results.

## Validation

The accepted fresh mailbox gate passes all 1,587 checks with unchanged source
hashes: four emitted sums modules and six sums host runs at O0/O2 across
diagnostic/fast-reset/trusted-generated, with both arena policies in each run.
The original String fixture also passes six host runs. Every emitted sums run
matches the independent root-byte and counter fixtures.

The final policy regression passes all 741 checks with the repaired original
harness. The local loopback I/O gate passes its 206,838 repeated matrix checks
with unchanged source hashes; it uses a separate host and is unaffected by the
check-name fix. The fresh unchanged-product native value-stack gate passes all
38 checks, including 50 storage cases/612 checks and 44 lifetime assertions.
A subsequent source-pin audit finds no mismatches in the recorded inputs of
all four accepted native gates after full validation. The full local Release gate
passes all 37 checks; business-policy preflight passes 98 checks across 30
independent outcomes, matching every correct and deliberately wrong control.
Validation used Release, serial build and the existing offline package-audit
skip. CI remains manual-only.

The PRD's main source example now uses Flow/2 rather than legacy RPN. A disposable
virtual-filesystem session defines it, passes both Bool-return tests, commits it
as library vocabulary and reloads it with persistent/library metadata. The PRD
also links the completed efficacy synthesis instead of describing comparisons
as future work.

A completed business-policy preflight control has ten fresh CLI sessions of
roughly 23–25 seconds each for between one and 59 requests. Similar durations
suggest project loading dominates this validation workload; this is an
observation, not a causal profile or a measurement of warm REPL latency. Native
correctness checks do not establish that live development updates are fast.

Read-only inspection identifies duplicate snapshot compilation in the
manifest-backed loader: `validateStoredProject` compiles the snapshot to validate
exported source, returns its state, and `loadProject` compiles that state again.
Activation then requalifies durable libraries. A measured development-load
follow-up should consider returning the already-validated snapshot, preserving
manifest/source checks, legacy loading and library qualification. Static review
does not establish which phase accounts for the observed time; no cache or
runtime optimization is implemented in this milestone.

Three integration attempts failed before acceptance: metadata/empty-array output
validation after one successful sums run, null binding after another successful
sums run, and corrupted original-runner O2 JSON before sums execution. The fourth
attempt passes. Notes of both setup-only link failures, all failed gate reports
and accepted runs are retained; counters and byte fixtures were not adjusted from native
output to obtain a pass.

## Preserved evidence

The verified [archive manifest](evidence/169-native-mailbox-sum-lifecycle/archive.json)
and [evidence ZIP](evidence/169-native-mailbox-sum-lifecycle/evidence.zip) retain
failed attempts, accepted reports, independent fixtures, source pins and full
Release output. The archive contains 2184 entries, 23,435,338 bytes;
SHA-256 `228d058bed7724ba6b2e116c193f3f09f1a0bf836521a3b64b93fb6d75ea2a19`. Every archived
entry was reread and checked against its recorded size/hash and current source,
and ZIP CRC validation passed. Binaries and dependency build trees are excluded.

## Limits and next work

This checks a bounded nominal sum closure through real mailbox lifecycle APIs.
It does not choose a final arena policy, establish service throughput, implement
JIT, prove general release readiness or change the mixed efficacy findings in
[report 168](168-test-source-inspection-agent-probe.md). Further native type
support follows the [nominal/refinement delivery plan](../docs/NATIVE-NOMINALS-IMPLEMENTATION.md);
performance measurements remain separate steps. Validation uses
synthetic local workloads and the established loopback I/O provider.
