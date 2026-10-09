# Current delivery roadmap

Updated 2026-10-09. This is a delivery order and status map, not a replacement
for the [PRD](PRD.md). Reliable agent edits, discovery and reuse of accumulated
typed vocabulary remain the primary research question. Runtime performance and
development cost are separate measurements.

Product target clarification: approach F# in data/structural correctness with
C-family source familiarity; use F# as the primary throughput/RAM baseline.
Higher throughput may justify a modest, predeclared whole-process memory
increase. Use Erlang/OTP as an isolation reference without requiring the full
OTP feature set. Arenas are the general allocation model for language-managed
dynamic values for now. Request/response processing is the first evaluation
workload, with frequent cleanup and separately retained mailbox/I/O state.
Choose arena lifetimes, placement and reuse policies against those product
goals; minimum RAM alone is not the success criterion.
See the [PRD comparison roles](PRD.md#product-targets-and-comparison-roles) and
[runtime evaluation](ASYNC-ARENA-EVALUATION.md#product-baseline-and-requestresponse-workload).

This remains a prototype: breaking changes are expected. Favor one current
design over compatibility layers and routine version proliferation. Update
affected code, examples and tests together; retain historical experiment
evidence without requiring the current runtime to support old contracts.

Priority update: an efficacy report comes first; efficient LLVM execution with
interpreter/JIT/AOT and a chosen memory design comes second. See
[report 101](../reports/101-approach-efficacy-review.md). Flow/2 is complete (report 100), and the guided repair comparison (report 102)
is complete: all three conditions repaired the defect, with no reliability
superiority established. Native conformance and arena selection should
not wait for every optional feature or proof of a comparative advantage.

## 1. Finish the bounded vocabulary-retention review

[Report 149](../reports/149-validated-window-reuse.md) and
[report 150](../reports/150-shared-rule-maintenance.md) complete the validated
billing composition/maintenance sequence. Both environments reuse the API and
pass the respective 18 and 48 hidden cases; no language reliability lead is
established. The [current efficacy checkpoint](../reports/151-efficacy-checkpoint.md)
closes this microtrial sequence. Next, address the demonstrated empty-occupancy
domain gap using validated nonempty periods and explicit absence, while retaining
independent behavior checks and library gates. Resume native conformance as a
separate secondary track. Keep the mailbox model open: specialized typed
mailboxes/dataflow boundaries are a candidate, not a full actor-framework decision.
Smalltalk-inspired live inspection and scoped dictionary replacement are separate
from adopting an actor object model. Evaluate whether awaiting I/O keeps a mailbox
logically occupied separately from whether its transient arena is retained.
The occupied-period integration and its validation are tracked in
[report 152](../reports/152-occupied-period-domain.md); do not count this
coordinator-designed domain improvement as a new independent agent efficacy trial.

[Report 153](../reports/153-owning-native-enums.md) implements closed,
payload-free enums in `OwningStackAot`, using inline values and the selected
stable arena policy. Focused O0/O2 conformance, malformed-input rejection and
mailbox/real-I/O gates pass, including ignored inputs and nested record fields;
all 37 full local Release checks also pass. Layout ABI 2 adds the explicit enum
case count. The tests exposed and now cover a short-input scanner status defect.
This remains separate from choosing actor versus specialized-mailbox semantics;
Option/Result native payload layouts remain subsequent work.

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

4. The subsequent stateful matched-pair repair is complete
   ([report 125](../reports/125-matched-pair-repair.md)): six fresh agents each
   pass twelve independent scenarios. Both retained Flow agents and both F#
   agents reuse the existing helper, and all six final test suites detect the
   two planned regressions. One Flow agent omits task finalization. This supports
   discoverable reuse and enforced publication gates, but still establishes no
   comparative reliability advantage. Further agent trials should target an
   unresolved failure mode or harder vocabulary discovery, rather than repeat
   this small repair. Native storage conformance is separate evidence and must
   not be reported as improved agent efficacy.

5. The R09-inspired payment-summary comparison is complete
   ([report 135](../reports/135-r09-shared-summary-comparison.md)); its accepted
   agent-created seed is documented in [report 134](../reports/134-r09-discovery-study.md).
   All six participants passed thirteen public behavior scenarios, but only the
   two F# participants and the second retained participant met frozen full
   acceptance. The other outcomes separate workflow completion, a private
   helper-signature constraint omitted from the task, and an incomplete shared
   refactor. This small study establishes no comparative reliability advantage.
   Default help now exposes existing compact inventory, targeted search and
   bounded context ([report 136](../reports/136-discovery-help-exposure.md)).
   A fresh guided composition task now passes 14/14 independent cases and
   171/171 project tests, with inherited definitions preserved and actual helper
   reuse ([report 137](../reports/137-guided-discovery-composition.md)). It has
   no old-help or F# control and establishes no comparative improvement. Do not
   repeat this case; the next comparative study must target an unresolved
   reliability failure rather than collect more feasibility wins. Dependency reachability
   is supporting evidence, not proof that duplicated logic has been removed.
   Native runtime throughput remains separate evidence.

6. Shared payment maintenance is complete ([report 141](../reports/141-paid-invoice-maintenance.md)):
   all four participants passed independent behavior and preserved shared structure.
   The strict new-test criterion was unnecessarily prescriptive; retain its recorded
   failures, but assess resulting regression protection in subsequent work. Library
   dependency qualification is now enforced ([report 142](../reports/142-library-dependency-qualification.md));
   its guided helper-test probe establishes local qualification, not an additional
   project-wide defect detection benefit. The reachable subscription-overlap
   comparison ([report 143](../reports/143-subscription-overlap-comparison.md)) is
   complete: one language submission passes 18/19 cases, the other language
   submission and both F# submissions pass 19/19. All own suites and compiled
   boundary mutation checks pass. Complete library coverage did not expose the
   missing empty-interval guard. No language reliability advantage is established.
   A bounded follow-up can compare discovery/reuse of a validated nonempty-period
   abstraction in both environments; do not add mailbox infrastructure as a prerequisite.
   The existing-core prototype is validated in [report 144](../reports/144-nonempty-interval-prototype.md):
   both implementations pass a shared 125-case bounded grid. Later-agent adoption
   and subscription/Instant integration remain untested.
   [Report 145](../reports/145-rental-vocabulary-reuse.md) completes the four-cell
   rental query comparison: all submissions pass 17/17 independent cases and
   their own suites. Neither retained-start agent calls the interval helper;
   both retained agents inspect helper details but use direct arithmetic. No
   reliability or retained-vocabulary advantage is established by this task.
   Both language queries qualify as libraries; validated Rental-returning
   fixture builders remain project-level because finite-projection qualification
   is unsupported. Keep that limit explicit. Further efficacy work should test
   retained domain rules that fit later tasks and improve conventional broker
   discoverability before comparing tool counts; no new mailbox infrastructure
   is required for this research.
   [Report 146](../reports/146-conventional-project-discovery.md) adds conventional
   `inspect()` discovery with bounded file paths and typed operation schemas.
   Focused suites and host inspection-budget checks pass; use the new bootstrap
   in future frozen trials, without revising historical interaction counts or
   claiming an unmeasured reduction in agent errors.
   [Report 147](../reports/147-agent-authored-batch-reuse.md) completes a
   direct retention test: fresh agents attempted batch billing with or without
   the single-rental queries actually authored by report145's flat participants.
   Correct controls pass 18 independent cases in both languages, and compiling
   wrong-aggregation controls fail six. Both F# submissions pass; neither
   AgentLang participant finds the supported Flow/2 fold spelling, and both
   abort. The F# retained participant duplicates rather than calls its helper.
   The AgentLang participants exchanged failed syntax information, so their runs
   cannot be treated as independent observations. Correct the fold metadata/help
   mismatch and tighten trial isolation before a separately identified discovery
   repeat; do not infer a vocabulary benefit from this outcome.
   The feasibility probe also records a library-coverage tradeoff:
   carrying invalid-window errors through a fold makes every branch testable
   but traverses the input even for invalid windows.
   [Report 148](../reports/148-flow-list-discovery.md) corrects list-callback
   discovery: descriptors retain Stack syntax and separately expose Flow/2
   receiver forms; help includes an executable named-callback fold example.
   Focused runtime/CLI suites and the unchanged billing control pass locally.
   A single fresh participant passes the same retained batch task's 18 oracle
   cases and publishes two qualified library functions. It initially calls the
   retained query, sees an uncovered post-fold error branch, then replaces reuse
   with duplicated arithmetic and qualifies the result. The guidance obstacle
   is overcome; retained production reuse is not demonstrated. Next investigate
   validated-input API shapes that avoid redundant error paths without weakening
   coverage. This is a discovery follow-up, not a replacement for report147 or
   a retention estimate.

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
one reusable scratch owner, using an explicit .NET experiment host.
[Report 126](../reports/126-native-mailbox-dispatch.md) moves bounded dispatch,
pending state and arena ownership into a standalone native controller, with
versioned entry/type metadata; its integrated verifier passes 353 checks.
The tiny fixture reserves 1,400 bytes for controller/arenas/workspace, not total
process memory. This is not general release certification. Real I/O, provider cancellation and a
genuine whole-request lifetime alternative remain prerequisites for the fair
throughput comparison; holding cleared scratch is not a valid substitute.

Memory priority clarification (2026-10-08): implement and test an owning value
stack for most working data, with explicit retained mailbox state. Values own
nested payloads; duplication creates independent values; moves transfer ownership;
logical drops end value lifetimes without surviving aliases. Physical space is
reclaimed only at compiler-proven dead suffixes or operation-wide reset; dead
interior bytes may remain until then. The stack must also
provide actual payload locality: adjacent owning values and
nested fields occupy nearby bytes, not merely nearby handles. Measure offsets,
extents, padding/reservations and access-pattern performance separately.
The current invocation bump arena/shared record DAG does not implement this.
The next bounded experiment must
exercise variable-sized nested values, copy/move/pop, returned results, surviving
outer values, branch joins and capacity failure, with exact storage oracles.
The fixed-size nested-record stage is complete in
[report 128](../reports/128-owning-value-stack.md): 462 cross-backend checks cover
inline ownership, copy/drop/return placement, local scope restoration and capacity
failures. Working-stack extent stays at 288 bytes across one/eight repetitions,
but fixed reservation and diagnostic overhead prevent a total-memory advantage
claim. This stage alone does not
satisfy the variable-size requirement or select the final representation.
The variable-sized slice is complete in
[report 129](../reports/129-variable-owning-values.md): 796 comparison checks and
the 37-check local Release gate pass. Inline Strings inside acyclic records
preserve UTF-16 code units, including isolated surrogates, with dynamic sizes
and field offsets exposed explicitly. Independent bytes, packed locals, dynamic
returns, failure cleanup and capacity boundaries are checked at O0/O2. The ABI3
control still covers fixed records only. Direct concat peaks at 80 bytes;
the wrapped user-function path peaks at 144 because of extra frames/local loads.
These are conformance counters, not a throughput or process-memory win.
The user subsequently clarified that rare movement and bulk pointer reset are
the intended behavior. Replace eager packing with stable arena payloads and
bounded location metadata. Ordinary bindings, read-only loads, same-arena calls
and returns should not copy payloads; function boundaries alone do not require
region resets. Leave interior dead space until a safe suffix or processing-region
reset. Require saved scope marks and compiler-checked early suffix rewind;
escaping results must prevent unsafe rewind. At request completion, reset the
whole arena or return it to the pool. Verify reset safety, unchanged retained output on failure and repeated-turn
capacity reuse. Measure metadata separately from payload traffic, and compare
with report 129's frozen evidence. This implementation now passes focused native
acceptance and the full local Release gate in
[report 130](../reports/130-stable-arena-rewinds.md).
The owning mailbox bridge now passes focused O0/O2 integration in
[report 131](../reports/131-owning-native-mailboxes.md). It keeps one native lifecycle
controller. Generated entries return locations into live working storage;
the controller publishes the complete output set once into inactive retained
storage before resetting scratch. The 180-check verifier and 122 native assertions
per optimization level cover dynamic String request/response lifetimes and
failure isolation. The 37-check full Release gate and 476-assertion LLVM suite
also pass. The focused two-policy mechanism comparison now passes 234 verifier
checks and 97 native assertions at each optimization level in
[report 132](../reports/132-associated-arena-comparison.md): keeping the actual
arena avoids intermediate publication/reimport, preserves failed-resume retry,
and pins slots that returning scratch makes available to other mailboxes. Both
policies use equal configured storage. Broader local validation also passes:
35 owning-stack checks, 353 native-dispatch checks, all 37 Release checks and
476 LLVM assertions. The bounded real-I/O comparison now passes seven cases
under both policies at O0/O2, including terminal cancellation and retry; see
[report 133](../reports/133-real-io-mailbox-correctness.md). Fresh policy and ordinary
mailbox regressions also pass. The optional reset profile now removes full-capacity
scratch payload clears at checkout/release, with matching policy and socket
behavior ([report 138](../reports/138-native-reset-profile.md)). The opt-in
[trusted-generated profile](../reports/139-trusted-generated-native-profile.md)
removes the remaining diagnostic bitmap storage, poison writes and tracking
scans while preserving serialized input checks and callback boundaries. Its
12-run socket matrix preserves both policies' behavior at O0/O2. The first
[matched load comparison](../reports/140-matched-mailbox-load.md) passes 197
correctness-checked trials plus an independent raw-data audit. Native process
memory is smaller in this fixture; a sixteen-slot KEEP pool recovers throughput
lost by four slots. Native RETURN's best short point fails the 30-second check,
so a general throughput advantage remains unproven. The internal arrival driver
and non-monotonic timing require investigation before service-throughput claims.
Keep both arena policies available; return to the primary maintenance-efficacy
comparison below before expanding performance infrastructure. A general actor
framework is not a prerequisite for either experiment.
Native conformance does not
establish service throughput, process RAM or improved agent reliability.
This is not an unresolved preference between packing and bulk reset; see the
[simplicity review](OWNING-STACK-DESIGN-REVIEW.md#stable-arena-payloads-and-bulk-reset-selected-direction).
Rewind placement requires compiler proof; uncertain lifetimes retain storage
until a later proved boundary. Runtime liveness decisions are excluded. Follow
the [stable arena lowering contract](STABLE-ARENA-LOWERING.md).

Deferred research: the user suggested an explicit operation such as
`Allocation.drop(temporary)` that might request compaction. This is an idea,
not selected syntax or part of the current backend work. Distinguish ending a
binding's logical lifetime from relocating surviving payloads. First measure
whether retained dead space is a practical problem. If it is, compare selective
transfer into a fresh arena with in-place compaction. Require compiler proof
that every affected location is controlled and updated, including projected
fields and retained/provider dependencies; pending I/O may make relocation
ineligible. No hidden compaction on ordinary scope exit or drop.

The refined candidate is an explicitly selected region boundary after a deep
function chain, with only its returned values surviving. Move those few results
to the saved region mark and rewind after them when compiler proof permits it.
Evaluate this against retaining the dead space until full reset; measure bytes
recovered, bytes moved and latency. Stack-top placement alone does not prove
safety. A size-based profitability threshold may be dynamic, but lifetime safety
must remain static. The user prefers `let result = compact { ... }` as the
candidate form, with the block's returned value (possibly a record or tuple)
identifying its survivors. Record it as future syntax, not an available feature;
thresholds and detailed semantics remain open. Implementation is deferred until
realistic workloads demonstrate a need. See the
[candidate example and safety rules](STABLE-ARENA-LOWERING.md#allocation-and-lifetime).

Related deferred research: Koka and Perceus, identified by the user on
2026-10-08. Study compiler-inserted last-use drops and reuse analysis that can
implement functional updates in place. Perceus uses precise reference counting;
the compiler's inserted operations can still perform runtime uniqueness checks.
It is not evidence that arbitrary lifetimes or safe mutation are always decided
entirely at compile time. Our adaptation should first investigate statically
proved last-use reuse of existing arena slots without changing source value
semantics, adding reference counting, or relocating unrelated survivors.
If uniqueness or layout suitability cannot be proved, keep the ordinary
allocation path. Measure copy bytes and retained capacity before adopting it.
Sources: [Microsoft Research's Perceus paper](https://www.microsoft.com/en-us/research/publication/perceus-garbage-free-reference-counting-with-reuse/)
and [Koka's reference-counting and reuse examples](https://koka-lang.github.io/koka/doc/book.html).
Final byte encoding remains an experimental choice; shared-graph regions are comparison
controls, not replacements for the owning-value goal. Keep the actual
keep-associated versus return-at-suspension async comparison as the subsequent
throughput decision; mailboxes alone are not the memory differentiator.

The [Midori research note](MIDORI-RESEARCH.md) records relevant primary accounts
and their limits. For later async work, keep suspension explicit, bound queued
bytes/outstanding operations, measure copy costs, and test state changes across
awaits. Midori's linked execution stacks and collected/shared heaps are not
evidence for our owning program-data-stack model.

Include [Goose](GOOSE-RESEARCH.md) as a close memory-layout comparison and a
source of testable alternatives, especially destination construction and
variable-size layout tradeoffs. Review its inferred stack/lifetime rules against
our stricter owning-value contract before adopting them. Repository benchmark
claims require independent replication; no throughput advantage is inferred.

The [owning-stack simplicity review](OWNING-STACK-DESIGN-REVIEW.md) keeps the
source model unchanged while making static placement, dynamic size boundaries,
local cleanup and resource exhaustion explicit. Prefer compiler-derived layout
over adding region/borrow syntax or multiple collection-storage categories.

Research compiler-guided pool sizing from each process-message entry point:
derive fixed layouts and peak call/branch storage, incorporate explicit input
and recursion bounds, and report proven bounds separately from estimates or
unknowns. Combine per-handler sizes with configured executing/suspended owner
counts, retained-state and queue/provider budgets. This follows basic owning-stack
conformance; see [the sizing contract](ASYNC-ARENA-EVALUATION.md#compiler-guided-sizing).

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
User clarification (2026-10-08): keep mailbox identity/static state the same in
both arms. Compare keeping its actual stack/scratch associated throughout async
work against returning scratch to the pool at suspension and reacquiring it on
completion. The keep-associated arm must retain real data and avoid unnecessary
promotion copies, not hold a cleared arena artificially. Record memory,
throughput, tail latency, copying and allocation churn under matched limits;
[the explicit comparison contract](ASYNC-ARENA-EVALUATION.md#explicit-comparison-keep-or-return-the-mailbox-stack)
defines this next experiment. The simulated report104 comparison does not
replace the real-I/O comparison.
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

Runtime organization remains an open design choice. The user is considering
specialized typed mailboxes connected as a data-flow system, with Smalltalk-inspired
live inspection and implementation replacement, rather than committing to a
general actor system. Evaluate functions as the unit of ordinary composition
and mailboxes as state, scheduling and lifetime boundaries; do not require a
mailbox for every function. State retention, async resumption and explicit typed
routing fit this candidate. General spawning, supervision and distribution are
separate decisions. Current native arena/profile work supports the shared
foundation and does not select a general actor framework. AI coding agents
remain external developers, not language-runtime entities.

No current interpreter result proves native footprint, throughput or arena
safety. Require semantic conformance and measured memory, throughput and tail
latency before choosing the model. The proposed long-running Campfire migration
comes after stable language behavior and implemented native memory/runtime
support, as described in [report 088](../reports/088-late-application-migration-research.md).

## 4. Strengthen reusable library contracts

The next approved correctness checkpoint implements the
[structural correctness requirements](STRUCTURAL-CORRECTNESS.md) in bounded
slices: explicit checked ratio/rounding operations, closed state types and
exhaustive decisions, invariant-preserving construction, narrow effectful
execution, and deterministic properties backed by independent expectations.
Inspect existing support before adding mechanisms; a language-native property
framework and general proof system are not prerequisites. Preserve frozen
comparison fixtures and resume efficacy evaluation after the implementation
checkpoint. Track delivered and pending requirements explicitly in its report.

Report 110 delivers the arithmetic slice: exact checked ratios and an authored
total Money scaler using validated unit basis points. [Report 112](../reports/112-closed-domain-states.md)
delivers payload-free Flow/2 enums with verified exhaustive matching and durable
source for project functions. The finite library qualification implementation
is validated locally: it checks direct Bool/enum inputs per
parameter, finite return values with open-composite projections, and actual
target invocation from passing own tests. It rejects unprovable or over-4096
domains and requalifies durable libraries on load and named snapshot restore.
Enum-bearing authored helpers must qualify independently before a library can
reach them. All 37 full local Debug checks pass; see
[the finite coverage contract](FINITE-COVERAGE.md) and [report 116](../reports/116-finite-library-coverage.md).
[Report 117](../reports/117-finite-coverage-adoption.md) tests adoption with one
fresh subagent: it tests both mixed Boolean combinations, qualifies that function,
and honestly keeps an always-populated Option return at project maturity.
Independent behavior and both frozen mutation checks pass. One added test is
redundant; a scripted three-row control passes qualification while missing a
Boolean interaction bug. Evaluate bounded Cartesian coverage separately; no new
policy is adopted from this single trial. For the next structural slice, use
explicit populated-state contracts where presence is guaranteed, then test
legal state transitions without weakening unreachable-alternative diagnostics.
[Report 118](../reports/118-populated-delivery-contract.md) implements one such
narrow slice: an interpreter `list.tail` and a separately loaded Flow/2 delivery
extension use a required `EmailMessage` head inside a populated plan. The focused
Business Transitions runner passed 8 groups / 5,382 assertions, with the original
53 words, 31 types, 154 tests and 44 examples preserved. The extension adds 2
words, 1 type, 7 tests and 3 examples, plus 15 reload checks. All 37 full Debug
checks and 444 separate LLVM assertions pass; no agent-adoption or native List
claim is made. General
cross-field validation and provider-state assertions were pending at that
checkpoint. [Report 119](../reports/119-record-construction-invariants.md)
adds optional whole-record predicates enforced by verified IR, the interpreter
and the supported native record constructor. Focused checks pass: 191 IR,
93 interpreter, 44 formatting, 1,110 Flow, 101 Source, 979 Flow Runtime,
370 Storage, 5,479 business-transition and 455 native assertions. Persistence
and library qualification pass before and after reload. The full Debug run passed 36/37 checks; its sole inspection-script
compatibility failure was repaired and passed 31 assertions separately. Functions returning validated records with finite projections
remain unqualifiable without a proven valid-value domain. This is implementation
evidence, not fresh-agent adoption.

The bounded fresh-agent trial is complete ([report 120](../reports/120-record-validator-adoption.md)):
behavior and reload pass, but the agent misunderstood validator-owned coverage
and left the predicate at project maturity. A separate diagnostic qualified the
same predicate with one correctly owned rejection case.
[Report 121](../reports/121-record-validation-guidance.md) supplies an executable
Flow/2 validator example, exact request selectors and documented caller-view
scopes. A fresh Debug build, 1,023 Flow Runtime assertions and 145 CLI assertions
pass. [Report 122](../reports/122-record-validation-help-adoption.md) completes a
fresh bounds-invariant trial: all ten independent cases and six attached tests
pass after reload, both functions qualify as library, and the durable validator
binding matches. However, all three help requests returned Flow/1, so the new
Flow/2 guidance was never exposed.
[Report 123](../reports/123-versioned-record-help.md) repeats that task with the
same runtime, oracle and scorer, explicitly selecting version-2 help. The fresh
agent saw the example, passed ten independent cases and seven own tests, qualified
both functions and produced no structured errors. This is successful bounded
adoption, not a causal improvement estimate. Pin and verify help versions in
future trials.

[Report 124](../reports/124-efficacy-assessment.md) consolidates efficacy evidence:
reuse and workable edits are observed, but comparative reliability and context
advantages remain unproven. [Report 125](../reports/125-matched-pair-repair.md)
completes the small replicated pair-reminder repair comparison: six fresh agents
(two per retained/reset/conventional condition) each pass 12/12 independent cases.
Both retained Flow and conventional agents reuse their existing helper. One
retained actor omits task.commit despite a correct persisted library revision;
record workflow completion separately. The reset condition is compact, not
reset-rich, and these small samples do not establish a reliability advantage.

Continue the approved native-runtime work in section 3: bounded native dispatch
is validated in report126; next test stack/region lifetime rules and compare the
async arena policies under matched limits.
Do not gate that work on more easy repair variants. Future efficacy trials should
use a held-out multi-function change with collateral-regression opportunities,
independent expected values and unchanged controls. A bounded interface follow-up
should make active-task status visible at close, distinguishing word publication
from completed task logging. Reuse the existing harness; add no comparison
prerequisites without a concrete blocker. Provider-state,
module closure and scoped overrides remain approved work, not prerequisites to
completing all future comparisons. Separately assess
whether unchecked construction inputs need a distinct type or field-based
validation contract; report 120 exposes this reasoning tension but does not
settle the design. Provider-state assertions, explicit module boundaries and
test-local overrides remain pending. Authored library dependency closure is
covered by report 142; owning-backend native enums are covered by report 153.
Use new fixtures and preserve historical research inputs.

After the bounded report 140 runtime comparison, prioritize a small maintenance
comparison using the accepted retained-language and conventional outputs from
report 135. Candidate task: change the existing shared payment-total rule to
include only payments belonging to paid invoices, preserving errors and both
summary callers. Use two fresh agents per language, a frozen independent oracle
covering the helper and both callers, and correct/incorrect preflight controls.
Score behavior, shared structure, collateral changes, tests and finalization
separately. Preserve the helper-provenance distinction: the language helper came
from an earlier agent; the conventional equivalent was supplied. This tests
maintenance of existing vocabulary, not identical prior authorship or a causal
language advantage. Reuse the harness and leave historical submissions untouched.

[Report 141](../reports/141-paid-invoice-maintenance.md) completes this
four-participant maintenance study. All four pass the independent behavioral
oracle and preserve shared structure; both language participants and one F#
participant fail the literal requirement for newly authored overflow/precedence
tests, while inherited coverage remains passing. Strict acceptance is 0/2
language and 1/2 F#; behavioral acceptance is 2/2 in each arm. This small defensive
import-state task does not establish a reliability advantage. Future tasks
should assess the resulting suite rather than require duplicate regressions,
and target normal reachable application behavior where effects, module boundaries
or domain types can catch a concrete mistake. No additional mailbox infrastructure
is required before the next agent comparison.

Report 111's extra-write control passed full library coverage but violated the
IO contract. Make provider-state and effect-count assertions straightforward in
authored tests, and require that control to fail under the stronger tests.
Report 113 implements optional exact effect-count assertions scoped to the target
function. Report 114's fresh-subagent probe repaired the behavior but failed to
adopt the assertion: unversioned help returned Flow/1 guidance while the actor
worked in Flow/2, and its seven tests still accepted the extra-write mutant.
Report 115 repeats the probe with version-2 help requests and an explicit testing
help topic in the primer, keeping runtime, seed and oracle fixed. The fresh actor
used effect assertions; its three relevant tests rejected the restored write and
blocked library replacement. This establishes bounded usability, not comparative
reliability or a causal estimate from two actors. Keep help examples version-matched
in future trials. Explicit provider-state assertions remain pending.

[Report 142](../reports/142-library-dependency-qualification.md) implements the
all-authored library dependency closure, including collection callbacks. Library
publication and affected durable callers require qualified authored dependencies
or trusted/generated operations; each library member retains its own test gate.
A fresh guided subagent qualified a wrapper and helper and added a missing own
test that rejects the controlled false-branch defect. This is bounded adoption
evidence, not a matched reliability result. Local validation is complete after
repairing the legacy callback test setup; original failure and rerun are retained.
Explicit module membership and cross-module access enforcement remain pending.
Do not infer modules from dotted names.
Deterministic injectable effects retain capability and declared-effect
enforcement.
Add test-local typed dictionary overrides as well: nested calls can see a scoped
replacement of an IO or ordinary function, then automatic cleanup restores the
original even on failure or cancellation. Keep persistent bindings intact and
exclude mocked bodies from the original implementation's coverage evidence.

Full Cartesian input coverage and MC/DC remain decisions to evaluate, not
silently adopted requirements. Test coverage establishes observed behavior;
compiler exhaustiveness establishes handled alternatives. Require both where
specified. After implementation, run another bounded external-agent study to
test whether the stronger contracts improve edits and recovery.

For every milestone: work on `main`, save the report, run applicable checks
locally, then commit and push together. The separate `prototype` branch has been
retired at the user's request. Automatic CI remains disabled; remote publication
failures are reported separately from local validation results.
