# Current delivery roadmap

Updated 2026-10-07. This is a delivery order and status map, not a replacement
for the [PRD](PRD.md). Reliable agent edits, discovery and reuse of accumulated
typed vocabulary remain the primary research question. Runtime performance and
development cost are separate measurements.

Priority update: an efficacy report comes first; efficient LLVM execution with
interpreter/JIT/AOT and a chosen memory design comes second. See
[report 101](../reports/101-approach-efficacy-review.md). Flow/2 is complete (report 100), and the guided repair comparison (report 102)
is complete: all three conditions repaired the defect, with no reliability
superiority established. Native conformance and arena selection should
not wait for every optional feature or proof of a comparative advantage.

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

1. The quick preliminary comparison is complete: all four fresh actors passed
   54 independent S07 cases, starting definitions were preserved, and retained
   helper reuse was observed. Retained made more runtime calls than the language
   controls; F# was equally correct. See [report 099](../reports/099-quick-agent-comparison.md)
   for metadata omissions, fixture friction and limits. This is feasibility
   evidence, not a demonstrated retention advantage.
2. The guided repair comparison is complete (report 102). Proceed with the
   accepted native architecture (report 103) while research continues. Preserve original 003
   on hold and the unfinished 004 runners as unvalidated drafts. The twelve-cell
   repaired study is deferred by the user's instruction to compare sooner;
   its prior freeze requirements still apply if that study is resumed.

3. The location-unhinted repair comparison is complete ([report 109](../reports/109-location-unhinted-repair.md)):
   all three fresh actors pass 54/54 target cases. Both language conditions
   preserve vocabulary; F# also fills two unfinished pricing helpers. This
   supports discovery/repair feasibility, not comparative superiority. Next
   bounded research should use an unfamiliar rule/refactor, current Flow/2,
   equivalent implemented helper topology and independent expected values.
   Avoid repeatedly retesting this one pricing defect or expanding the harness.

Acceptance is an evidence-backed review of the declared conditions, including
their limitations. Completing a language feature or passing actor-written tests
alone is not acceptance of the research hypothesis.

## 2. Completed: make the approved examples executable

The [revised examples](EXAMPLE-SYNTAX-MIGRATION.md) now use executable Flow/2 `fn`, plain
record properties, typed `==`, omitted pure effects and metadata spacing.
The explicitly versioned Flow/2 authoring path uses the same verified
semantic IR, with durable source-version selection and an explicit formatter (report 100).
Retain Flow/1 and Stack/1 historical bytes and interpretation.

Validated acceptance includes property/method disambiguation, nominal-type equality
rejection, effect checking before execution, formatter idempotence and
define/test/commit/reload round trips. Migrate executable examples and runtime
help together after these checks pass. Run focused Flow, Source and Storage
suites and applicable full local validation with fresh build outputs that do
not overwrite the frozen experiment binaries. See
[the implementation plan](../reports/085-frontend-library-revision-plan.md).

## 3. Build a lean native execution target and choose memory semantics

The first scalar LLVM AOT implementation is locally validated; see
[report 103](../reports/103-llvm-architecture-and-native-slice.md). The user
authorized this step based on demonstrated capability; comparative reliability
research continues. Implementation language remains open to maintenance needs.

The first standalone native arena/mailbox comparison is complete; see
[report 104](../reports/104-native-arena-mailbox-feasibility.md). All 72 runs
passed. Per-turn backing fell 77–80% for suspended workloads with disposable data,
but the retained-only control used more backing and copying. This supports
continuing the candidate, not a production throughput claim or language-wide
lifetime guarantee. Next, specify a narrow retained-value/lifetime contract in
the verified IR and add native conformance before broad runtime integration.
The standalone probe does not implement AgentLang mailboxes or idle trimming.
Native Int/Bool refinement conformance is now validated (252 native assertions);
see [report 105](../reports/105-native-refined-scalars.md). Native record execution
now passes 342 conformance assertions with invocation scratch, atomic retained
outputs and decoding after scratch/DLL disposal; see [report 106](../reports/106-native-record-ownership.md).
[Report 107](../reports/107-native-state-reentry.md) now validates typed state
re-entry with 441 native assertions: retained results feed later invocations
without managed graph decoding, preserving old state on failure. This currently
copies the input graph and live outputs, and requires one verified program
instance. [Report 108](../reports/108-native-mailbox-suspension.md) now passes 120
checks for readable Flow/2 handlers suspending/resuming fixed mailboxes through
one reusable scratch owner, using an explicit .NET experiment host. Next move
bounded dispatch/pending state into the native runtime and define reproducible
entry/type metadata for release execution. Real I/O, provider cancellation and a
genuine whole-request lifetime alternative remain prerequisites for the fair
throughput comparison; holding cleared scratch is not a valid substitute.

Keep the semantic IR authoritative. Development backends add LLVM
JIT while release builds use LLVM AOT plus a minimal runtime. Research arenas,
an arena-backed program-data stack, bounded mailboxes and suspended-I/O state
with explicit lifetime and buffer rules. Compare whole-request arenas against
per-turn scratch arenas with retained continuations under equal resource limits.
The first native slice establishes bounded interpreter/native agreement for
checked arithmetic, typed values, calls, branches and structured failures, with
versioned layout/ABI fixtures. Use an actual arena/pool prototype to measure
used/reserved/process memory, escaping outputs and cancellation cleanup.
Then expand conformance and implement development JIT generation invalidation
and compiler-free AOT release builds. A small slice does not imply general
native support; the same semantic IR remains authoritative throughout.
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

## 4. Strengthen reusable library contracts

Implement the approved module boundaries and stricter qualification rules:
cross-module authored calls require library qualification; library functions
call only qualified authored library functions or trusted/generated operations.
Add exhaustive matching for every function, finite return-value coverage and
per-parameter Bool/enum coverage alongside own-function branch coverage.
Qualification must survive reload correctly and become invalid when its bound
dependency contracts change. Deterministic injectable effects retain capability
and declared-effect enforcement.
Add test-local typed dictionary overrides as well: nested calls can see a scoped
replacement of an IO or ordinary function, then automatic cleanup restores the
original even on failure or cancellation. Keep persistent bindings intact and
exclude mocked bodies from the original implementation's coverage evidence.

Full Cartesian input coverage and MC/DC remain decisions to evaluate, not
silently adopted requirements. Test coverage establishes observed behavior;
compiler exhaustiveness establishes handled alternatives. Require both where
specified. After implementation, run another bounded external-agent study to
test whether the stronger contracts improve edits and recovery.

For every milestone: save the report, run applicable checks locally, commit,
merge and push together. Automatic CI remains disabled; remote publication
failures are reported separately from local validation results.
