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

Separate scratch scoring now passed all 54 independent R04/S07 cases and
confirmed retained-helper reuse and preservation of prior definitions. See
[the supplemental result](../reports/090-retained-discount-supplemental-scoring.md).
This does not repair the original blocked trial or establish a retention benefit.

Next actions:

1. Preserve 003 on hold after its audit failure. Prepare a separately versioned
   004 study with all twelve fresh cells under one repaired harness version,
   keeping the tasks, baseline bytes and runtime fixed. Do not combine old
   outcomes into its result table; see [the repair plan](../reports/097-uniform-retention-study-repair.md).
2. Validate accepted-predecessor, explicit-fallback and reject-before-execution
   controls before freezing 004. Launch only after clean-source/global/run pins
   and frozen controls pass. Preserve original artifacts and failed controls;
   never silently replace 003 source or acceptance records.
3. Publish a comparative review with independent outcomes, actual reuse,
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
The latest memory candidate preserves mailbox static state while releasing
unused stack/scratch backing storage after inactivity. Reset safe turn-local
state promptly, retain reusable
capacity within the budget, and measure burst/idle/outlier behavior separately from the arena
lifetime comparison. Long-lived state and outstanding I/O retain their own
cleanup boundaries.
An optional alternative is per-type min/max mailbox pools with fast growth and
slow retirement. Research state identity/routing and safe draining separately;
counts alone do not bound memory or add CPU parallelism to a single thread.
For mailboxes with distinct state, the preferred candidate is `min = max` with
stable identities; stack capacity can still be released independently on idle.
At async suspension, the proposed refinement stores surviving data and resume
state in that same mailbox, returns its stack arena to the pool, and reacquires
stack storage for resumption. No additional processing mailbox is required;
ordering during suspension remains a policy to specify and test.

No current interpreter result proves native footprint, throughput or arena
safety. Require semantic conformance and measured memory, throughput and tail
latency before choosing the model. The proposed long-running Campfire migration
comes after stable language behavior and implemented native memory/runtime
support, as described in [report 088](../reports/088-late-application-migration-research.md).

For every milestone: save the report, run applicable checks locally, commit,
merge and push together. Automatic CI remains disabled; remote publication
failures are reported separately from local validation results.
