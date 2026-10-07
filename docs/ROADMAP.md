# Current delivery roadmap

Updated 2026-10-07. This is a delivery order and status map, not a replacement
for the [PRD](PRD.md). Reliable agent edits, discovery and reuse of accumulated
typed vocabulary remain the primary research question. Runtime performance and
development cost are separate measurements.

## 1. Finish the bounded vocabulary-retention review

The interpreter, typed semantic IR, dictionary, task transactions, introspection,
Flow/1 authoring and existing library coverage gates are implemented. Earlier
external-agent comparisons observed reuse; they did not establish a general
reliability or efficiency advantage.

The matched retention study has four of twelve actor attempts recorded. R01
and R03 passed independent acceptance, R02 failed independent behavior, and
R04 was blocked before independent behavior by a verifier bug. See
[the trial plan](VOCABULARY-RETENTION-TRIAL-PLAN.md) and
[the original R04 report](../reports/089-retained-discount-harness-blocked.md).

Next actions:

1. Review and locally test the separate R04 supplemental scorer. Bind the
   original records before execution, score all 54 pinned cases on scratch
   copies, and preserve the failed canonical outcome. Its result cannot repair
   the original controlled trial or establish a retention advantage.
2. Continue the predeclared fresh-agent order, starting with R05/reset-rich/S01,
   only from a clean committed checkout with valid source, runtime and launch
   pins. Save original failures as well as successes.
3. Address the known retained-predecessor verifier defect through an explicit
   study amendment or separately versioned harness before treating later
   affected cells as valid controlled evidence. Preserve original artifacts;
   do not silently replace frozen source or acceptance records.
4. Publish a comparative review with independent outcomes, actual reuse,
   incorrect edits and recovery observations. Separate missing harness results
   from program failures. Report unavailable model usage as unavailable.

Acceptance is an evidence-backed review of the declared conditions, including
their limitations. Completing a language feature or passing actor-written tests
alone is not acceptance of the research hypothesis.

## 2. Make the approved examples executable

The [revised examples](EXAMPLE-SYNTAX-MIGRATION.md) currently preview `fn`, plain
record properties, typed `==`, omitted pure effects and metadata spacing.
Implement an explicitly versioned Flow/2 authoring path over the same verified
semantic IR, with durable source-version selection and an explicit formatter.
Retain Flow/1 and Stack/1 historical bytes and interpretation.

Acceptance includes property/method disambiguation, nominal-type equality
rejection, effect checking before execution, formatter idempotence and
define/test/commit/reload round trips. Migrate executable examples and runtime
help together after these checks pass. Run focused Flow, Source and Storage
suites and applicable full local validation with fresh build outputs that do
not overwrite the frozen experiment binaries. See
[the implementation plan](../reports/085-frontend-library-revision-plan.md).

## 3. Strengthen reusable library contracts

Implement the approved module boundaries and stricter qualification rules:
cross-module authored calls require library qualification; library functions
call only qualified authored library functions or trusted/generated operations.
Add exhaustive matching for every function, finite return-value coverage and
per-parameter Bool/enum coverage alongside own-function branch coverage.
Qualification must survive reload correctly and become invalid when its bound
dependency contracts change. Deterministic injectable effects retain capability
and declared-effect enforcement.

Full Cartesian input coverage and MC/DC remain decisions to evaluate, not
silently adopted requirements. Test coverage establishes observed behavior;
compiler exhaustiveness establishes handled alternatives. Require both where
specified. After implementation, run another bounded external-agent study to
test whether the stronger contracts improve edits and recovery.

## 4. Research a lean native runtime

Keep the semantic IR authoritative. Later development backends may add LLVM
JIT while release builds use LLVM AOT plus a minimal runtime. Research arenas,
an arena-backed program-data stack, bounded mailboxes and suspended-I/O state
with explicit lifetime and buffer rules. Compare whole-request arenas against
per-turn scratch arenas with retained continuations under equal resource limits.
The approved [async arena experiment](ASYNC-ARENA-EVALUATION.md) makes per-turn
scratch the main candidate and whole-request arenas the comparison. Measure
sustained successful requests per second at the same enforced memory ceiling
and acceptable tail latency, including slow clients, cancellation and bounded
pending I/O. Record copying, backpressure and whole-process memory as well as
arena accounting before adopting either design.
Also evaluate bounded backing-storage reuse with high/low watermarks and delayed
trimming: reset safe turn-local state promptly, retain reusable capacity within
the budget, and measure burst/idle/outlier behavior separately from the arena
lifetime comparison. Long-lived state and outstanding I/O retain their own
cleanup boundaries.

No current interpreter result proves native footprint, throughput or arena
safety. Require semantic conformance and measured memory, throughput and tail
latency before choosing the model. The proposed long-running Campfire migration
comes after stable language behavior and implemented native memory/runtime
support, as described in [report 088](../reports/088-late-application-migration-research.md).

For every milestone: save the report, run applicable checks locally, commit,
merge and push together. Automatic CI remains disabled; remote publication
failures are reported separately from local validation results.
