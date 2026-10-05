# PRD implementation and verification ledger

The active goal is completion of the prototype and evaluation requirements in the supplied 82-section PRD, including the refined strong-type and library-test requirements. The first usable slice is a milestone, not a substitute for that goal. This ledger preserves later deliverables until authoritative evidence establishes completion.

Status: **verified** means the listed scoped behavior has observed acceptance evidence; **partial** means some behavior exists but the full section is not proven; **pending** means implementation or evidence is missing; **design** means a principle/advisory requirement; **excluded** means a V1 non-goal or explicitly optional follow-on. A green build is not evidence of research success.

Baseline evidence: [Milestone 001 report](../reports/001-first-usable-prototype.md), Core/Parser/Compiler/Runtime/Protocol modules, CLI, examples, and the 16-group/154-assertion acceptance runner. The detailed acceptance source is authoritative for what the baseline tests actually exercise.

Current milestone evidence: [Milestone 002 report](../reports/002-next-milestone.md), fresh Release build with zero warnings/errors, 18 language groups/256 assertions, 82 offline harness assertions, and 7 business groups/113 assertions. No live model comparison has been run.

Durable-state milestone evidence: [Milestone 003 report](../reports/003-durable-project-state.md), independent fresh Release build with zero warnings/errors, 26 language groups/396 assertions, 98 harness, 113 business, 57 source, 64 storage, and 63 conventional-tool assertions. A separate fresh-process verifier passed 99 checks over 31 protocol exchanges. This validates the listed implementation slice, not a controlled agent performance comparison.

Exploratory external-agent evidence: [the subagent pilot](../reports/004-subagent-vocabulary-pilot.md) records a fresh agent building two tested library words and a different agent discovering/reusing them for a later task. Independent host checks passed 16 and 17 cases respectively. The second agent retained earlier repository context; exact model usage is unavailable. This proves a scoped workflow, not the required controlled performance comparison.

Discovery and IR-foundation evidence: [Milestone 005](../reports/005-discovery-and-typed-ir.md), Release build with zero warnings/errors, 27 language groups/465 assertions, 53 discovery assertions, 8 IR verifier groups/37 assertions, all existing suites and 99 fresh-process persistence checks. Nine discovery commands are integrated. Lowering and vocabulary-analysis evidence is recorded in [milestone 006](../reports/006-ir-lowering-and-execution.md) and [milestone 007](../reports/007-vocabulary-analysis-foundation.md): full Release validation with 67 IR and 32 vocabulary assertions plus all existing suites and 99 persistence checks. At that milestone Runtime still executed the AST. A 314-check baseline self-comparison validates parity infrastructure only. Runtime warnings and measured reuse metrics remain pending.

Standalone backend and baseline-audit evidence: [milestone 008](../reports/008-standalone-ir-backend.md) and [milestone 009](../reports/009-experiment-baselines-and-task-bank.md), fresh full Release validation with zero warnings/errors, 185 harness, 69 IR, 36 formatter, 13 interpreter and 2,114 task-bank assertions, all existing suites and 99 persistence checks. Runtime cutover was pending at that milestone; executable benchmark trials remain pending.

| Original section | Requirement / disposition | Current evidence and outstanding work |
| --- | --- | --- |
| 1 | Partial: small extensible typed environment | Runtime and words exist; complete values/providers/evaluation remain |
| 2 | Partial: cross-agent vocabulary accumulation | Fresh-process Customer reuse verified; later-agent benefits unmeasured |
| 3 | Pending: ten primary agent outcomes | Requires real agent trials and comparable baseline |
| 4 | Excluded: listed V1 non-goals | No native/LLVM/WASM/JIT, generic user definitions, OO, arbitrary reflection, or frameworks |
| 5 | Design: explicit inspectable deterministic behavior | Apply to every feature and review; discovery still incomplete |
| 6 | Partial: complete word metadata | Stable IDs, durable revision provenance/history now verified; full examples/test metadata query remains incomplete |
| 7 | Verified: concatenative execution | Basic eval and user-word acceptance groups |
| 8 | Verified: typed scalar/nominal stack | Type mismatch and no-effects-on-type-error checks; closed container types independently probed |
| 9 | Verified: Int/Float/Bool/String/Unit/List/Option/Result/Record foundation | Closed nested containers, static callbacks/cases, persistence and coverage checks |
| 10 | Verified: nominal refined scalar types | Email/unit distinctions, validators, reload and freeze checks |
| 11 | Partial: explicit effect vocabulary | Declarations and transitive checks; complete providers pending |
| 12 | Partial: effect capability restrictions | Default denial verified; resource/path-scoped policy pending |
| 13 | Partial: 50–100 boring primitives | Baseline 44, current dictionary 49; syntax constructs counted separately. JSON/process/environment/introspection primitives incomplete |
| 14 | Verified: no arbitrary .NET escape | Only host builtins invoke host code; no reflection/call escape surface |
| 15 | Verified: interactive REPL foundation | Multiline CLI smoke and expressions; additional commands tracked below |
| 16 | Partial: required introspection commands | words/describe/source/dependencies/callers/search/effects/tests/test/examples/type-of; recent-words and fuller metadata pending |
| 17 | Verified: machine-readable command transport | JSON-lines protocol and diagnostic acceptance checks |
| 18 | Partial: first-class tests | Tests/test-all/failed-tests and gates; runtime-error expectations and typed pure expected-expression focused checks passed (report 013); complete Flow test integration pending |
| 19 | Partial: examples as metadata | Persisted cases; query currently returns names rather than full example details |
| 20 | Verified: attached documentation | Word doc source, describe, persistence and rollback checks |
| 21 | Verified: incremental word declarations | Parser and user-word acceptance checks |
| 22 | Verified: candidate validation and test-gated commits | Failing/untested commits blocked; dependency and scoped metadata regressions |
| 23 | Verified: temporary words/promote/discard | Session isolation and task-cleanup checks |
| 24 | Verified: readable file dictionary foundation | dictionary.agent reload; richer file layout advisory |
| 25 | Verified: durable word history and diff | Hashed prior definitions/tests/examples/provenance and source diff survive reload; milestone-003 checks |
| 26 | Partial: task sessions and complete logging | begin/status/commit/abort verified; complete ordered inspection/execution/change events pending |
| 27 | Partial: deterministic structured task log | Aggregates exist; event sequence and complete metrics pending |
| 28 | Verified: dictionary rollback | Abort restores definitions/types/tests/docs/policy after interim commits |
| 29 | Partial: replaceable simulated effects | Isolated virtual file/clock tests; provider interfaces and other domains pending |
| 30 | Verified: small agent command interface foundation | Engine.Dispatch and Protocol; live LLM integration pending |
| 31 | Design: suggested F# solution organization | Existing Core/CLI/Acceptance projects; add host/harness/domain boundaries as needed |
| 32 | Partial: strongly typed internal concepts | Value/type/AST unions and closed ten-case IR effect union; source/runtime effect metadata still checked strings |
| 33 | Partial: authoritative typed IR execution and inspection | Runtime cutover now routes eval, calls, tests and state transitions through verified IR; full Release validation passed 18 checks and pinned parity passed 314 selected contracts (report 010). LLVM remains excluded from V1 |
| 34 | Partial: bounded runtime and turnaround | Step/depth bounds exist; timing goals unmeasured |
| 35 | Partial: small parser with spans/incomplete detection | Spans, buffering and source nesting limits verified by 84 Source assertions and bounded fresh-process probe (report 015); parse-stage API/incomplete classification pending |
| 36 | Partial: lowered ir stage inspection | ir renders verified functions/generated operations or canonical primitive contracts; parse/ast/types stage commands remain pending |
| 37 | Verified: stack checking and branch joins | Compiler and negative acceptance cases |
| 38 | Verified: minimal stack gymnastics | dup/drop/swap; no extended stack operation assortment |
| 39 | Verified: typed local bindings | Local/branch joins and demo checks |
| 40 | Verified: deterministic structural search | Exact nested nominal input/output types, declared effects and direct dependency queries; no embeddings |
| 41 | Verified: compact bounded dependency/type context | Complete JSON entries, recursive nominal declarations, concise docs, hard budgets/omissions and exact transported data bytes; not inferred task relevance |
| 42 | Partial: context/token accounting | Harness records complete prepared/sent request bytes and returned provider usage; exact token budget/retrieval breakdown pending |
| 43 | Partial: complete small-business fixture | Customer language demo, conventional reference and standalone typed fixture contract; full language domain, matched adapters, 40–60 words and 50–100 tests pending |
| 44 | Excluded from first domain: optional later simulation | Preserve for later generalization, not a V1 blocker |
| 45 | Partial: equivalent conventional environment | F# business foundation passes 7 groups/113 assertions; language parity and matching task oracles missing |
| 46 | Pending: fresh/growing/debugging/discovery/refactoring experiments | Categories require tasks and runner |
| 47 | Pending: all task metrics | Runtime aggregates are a subset; harness and event sources required |
| 48 | Pending: Vocabulary Reuse Ratio | Define start-of-task words and distinguish domain/test/primitive executions |
| 49 | Partial: static primitive distance analysis | Arbitrary-precision multiplicity and cycle errors verified; runtime/task integration and actual invocation metrics pending |
| 50 | Pending: 2k/4k/8k/16k/32k context trials | Enforced harness budgets and recorded outcomes required |
| 51 | Pending: error recovery metrics | Link attempts/errors/resolutions and provider usage, include unresolved errors |
| 52 | Pending: vocabulary quality metrics | Reuse/callers/lifetime/downstream acceptance evidence required |
| 53 | Pending: pollution metrics | Unused/duplicate/short-lived words and growth traces required |
| 54 | Partial: structural duplicate analysis | Stable-ID structural fingerprints and sorted candidates verified; commit warnings pending |
| 55 | Partial: deprecate/rename/replace | Stable-ID semantic rename/deprecation and caller-gated implementation replacement verified; replace OLD NEW pending; frozen validator rename refused |
| 56 | Verified: committed snapshot save/load | Hash integrity, project/provider restoration and retention of current host capabilities verified |
| 57 | Partial: model-provider harness | Scripted/canned HTTP adapter, traces/usage/state/oracles and audited starting profiles with Growing lineage; snapshot selection, conventional runner and live trials pending |
| 58 | Partial: compact system prompt | Mandatory compact primer and per-run prompt artifact verified; live usage pending |
| 59 | Partial: small initial context strategy | Prompt avoids dictionary dump; full requests include retrieval history and byte caps, exact token accounting pending |
| 60 | Partial: conventional tool baseline | Path-confined read/search/CAS edit/fixed validation tools verified; actual same-model comparison pending; validation is not an OS sandbox |
| 61 | Partial: 60-task artifact bank | 20 simple, 20 medium, 10 debugging, 10 refactoring tasks and 180 proposed hidden cases; all execution adapters, matched fixtures, authenticated evidence and snapshot pins remain pending |
| 62 | Partial: vocabulary-building task sequence | Initial premium demo; sequence and comparable costs pending |
| 63 | Partial: CLI observability | task status/log, words and bounded graph; recent-words/metrics pending; GUI optional |
| 64 | Verified: direct and transitive dependency graph | Stable closures, callbacks/refinement validator edges, cycle/reuse markers and explicit graph limits |
| 65 | Verified: definition-level word editing | define stages source; replace-word commits after current word and transitive caller tests pass |
| 66 | Partial: semantic change boundaries | Scoped word/test metadata checked; dedicated type/test/example/doc operations pending |
| 67 | Verified: stable word IDs | Preserve across temporary promotion, replacement, semantic rename, history and reload; milestone-003 identity checks |
| 68 | Partial: structured failure reporting | Code/word/span/expected/actual; related definitions and complete current-stack context pending |
| 69 | Pending: deterministic conversion hints | Search type graph for suggested conversion words |
| 70 | Partial: all eight milestones | First usable slice only; full types/effects, domain, harness and evaluation remain |
| 71 | Pending: success evidence | Compare correctness and uncertainty alongside >=20% token/turn signal; no claimed gains |
| 72 | Pending: failure evidence | Report bypasses/duplicates/discovery/error/raw-source/conventional outcomes |
| 73 | Pending: answers to RQ1–RQ10 | Controlled results required; missing-primitive events must be recorded |
| 74 | Pending: Flat/Growing/Conventional comparison | Equivalent fixture, model, task, oracle, retention policy and repetitions |
| 75 | Excluded: follow-on curator | Explicitly not initial implementation |
| 76 | Excluded: optional classifier-driven context | Deterministic context in section 41 remains required |
| 77 | Excluded: optional extra maturity levels | Required user addition project/library rigor already implemented |
| 78 | Excluded: proof-like contracts | Explicit follow-on, not V1 |
| 79 | Verified: initial usable slice | Baseline checks; must not redefine full completion as this milestone |
| 80 | Partial: actual agent demo and later-agent reuse | Two actual external subagent tasks passed independent checks and retained-word discovery/reuse; controlled cost comparisons pending |
| 81 | Pending: marginal cost trend | Sequence, cost data, comparable task difficulty and correctness required |
| 82 | Pending: full prototype outcome | Unfamiliar-project agent task plus measured help for later agents required |

Runtime cutover evidence: [report 010](../reports/010-runtime-ir-cutover.md) records the 18-check full Release gate, 29 language groups/494 assertions, 99 persistence checks, and 314 selected pinned AST-versus-IR contracts. [Report 011](../reports/011-business-fixture-contract.md) records 69 standalone fixture-contract assertions; [report 012](../reports/012-value-inspection.md) records 44 structural value-inspection assertions. Neither foundation establishes cross-language business parity or research gains.

## Named-command completion checklist

The earlier late-only syntax research plan is superseded by the user-directed early [expression/dot frontend migration](FRONTEND-MIGRATION.md). Completing that migration is required before further controlled agent experiments. Preserve local interaction with recently produced values and the authoritative typed semantic IR, then evaluate agent success, error recovery, context and tool cost. Later LLVM allocation/layout and memory measurements remain separate research; changing source notation does not establish native memory gains. [Report 016](../reports/016-design-value-assessment.md) records the earlier assessment, not the current implementation order.

Verified foundation: eval, define, words, describe, type-of, source, dependencies, callers, search, search-type, search-output, search-effect, search-dependency, transitive-dependencies, transitive-callers, graph, context, effects, ir (verified IR/primitive contract), tests, test, test-all, failed-tests, examples (names only), commit/commit-word, replace-word, promote, discard, task.begin/status/commit/abort/log, stack, durable history/diff, rename, deprecate, snapshot.save/load and storage.status.

Still required or incomplete: recent-words, parse, ast, types, metrics, replace OLD NEW, full example/test metadata, definition-level documentation/test/example edits, agent-run with model/task/snapshot selection.

## Planned implementation order

Proposed memory follow-on: [scoped arena lifetime contract](MEMORY-REGIONS.md), including escape/promotion, generation/snapshot retention, cleanup, capacity and used/reserved/peak measurements. This is pending design/implementation and must not be confused with existing temporary words, managed GC or the value-size safety bounds. It follows the early authoring migration; native allocator integration remains conditional later work.

User-directed early frontend migration now precedes further controlled agent experiments: [expression/dot source plan](FRONTEND-MIGRATION.md). Preserve words, strong types, effects, library gates and authoritative semantic IR; implement named inputs/locals, static first-input dot calls, explicit versioned source/persistence, complete legacy semantic coverage and default cutover. Close-to-first-use is lint. Do not introduce mutable language globals; retain explicit values/effects. The default Runtime/protocol frontend is still RPN. An opt-in Flow parser and typed lowering foundation now exist (report 019); complete containers/cases, test/example integration, durable authored metadata and default cutover remain required. Focused compiler checks do not establish the migration acceptance criteria.

User addition: the typed semantic IR must become the authoritative executable representation. Runtime now routes through the verified IR interpreter; report 010 records cutover validation. Further source-stage inspection remains pending; LLVM development JIT and release AOT remain conditional follow-ons after successful agent experiments. See [the backend plan](BACKENDS.md).

1. Typed containers, case handling, and coverage rules; establish provider harness contracts in parallel.
2. Stable identity, durable history, snapshots, structured discovery/context/metrics and complete task events.
3. Replaceable effect providers and confined real providers; JSON and necessary primitives within 50–100.
4. Full business fixture and independent acceptance suite, compact prompts, live pilot and conventional baseline.
5. Freeze reproducible fixtures; run and report Flat/Growing/Conventional trials and context-budget variations.

Each validated milestone includes updated saved reports in its Git commit, is pushed to the private repository, and is merged into main. Reports distinguish implemented behavior, directly observed tests, remaining requirements, and research findings. Live-provider usage may require account credentials; continue all independent implementation/validation while identifying that constraint, and never invent usage or benchmark outcomes.

User-requested later research: [no language heap; program data stack only](STACK-ONLY-RESEARCH.md).
The data stack is not the CPU stack and may live within an arena. Investigate
strict LIFO storage separately from arena-only allocation, preserving typed
compound values and reporting retention/copying limits and measured memory.
Research is required; adopting this memory policy in V1 is not.

Integrated foundation evidence: [report 021](../reports/021-integrated-flow-foundation.md) records a fresh 23-check Release gate and 314 selected pinned CLI parity checks. Flow remains opt-in; complete semantic surface, durable integration and default cutover are still required.

Flow semantic extension evidence: [report 026](../reports/026-flow-semantic-integration.md) records focused checks for closed typed constructors, exhaustive Option/Result cases, actual AST-depth bounds, sparse source-marker allocation, attached test/example compiler origins, and advisory binding lint. Flow passed 122 assertions; IR passed 102; lint passed 42. Full integrated validation is tracked in that report. This does not complete static callback syntax, output vectors, Flow-native test/example parsing, durable source integration, default authoring cutover, or controlled agent evaluation.

Flow static-callback evidence: [report 028](../reports/028-flow-callback-integration.md) records 198 Flow assertions, 47 lint assertions and the fresh passing 24-check Release gate. Named callbacks lower directly through existing typed list IR, preserve nominal distinctions and empty-list effects/dependencies, and retain source/coverage metadata. Shared iterative preflight protects host-built expression/word rendering and lowering with depth 128 and a 100,000 expanded expression/type-node budget per word. This does not complete output vectors, Flow-native tests/examples, exact-root addressing, durable authored-source integration, default frontend cutover, or controlled agent-performance evaluation.
