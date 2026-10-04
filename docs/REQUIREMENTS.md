# PRD implementation and verification ledger

The active goal is completion of the prototype and evaluation requirements in the supplied 82-section PRD, including the refined strong-type and library-test requirements. The first usable slice is a milestone, not a substitute for that goal. This ledger preserves later deliverables until authoritative evidence establishes completion.

Status: **verified** means the listed scoped behavior has observed acceptance evidence; **partial** means some behavior exists but the full section is not proven; **pending** means implementation or evidence is missing; **design** means a principle/advisory requirement; **excluded** means a V1 non-goal or explicitly optional follow-on. A green build is not evidence of research success.

Baseline evidence: [Milestone 001 report](../reports/001-first-usable-prototype.md), Core/Parser/Compiler/Runtime/Protocol modules, CLI, examples, and the 16-group/154-assertion acceptance runner. The detailed acceptance source is authoritative for what the baseline tests actually exercise.

| Original section | Requirement / disposition | Current evidence and outstanding work |
| --- | --- | --- |
| 1 | Partial: small extensible typed environment | Runtime and words exist; complete values/providers/evaluation remain |
| 2 | Partial: cross-agent vocabulary accumulation | Fresh-process Customer reuse verified; later-agent benefits unmeasured |
| 3 | Pending: ten primary agent outcomes | Requires real agent trials and comparable baseline |
| 4 | Excluded: listed V1 non-goals | No native/LLVM/WASM/JIT, generic user definitions, OO, arbitrary reflection, or frameworks |
| 5 | Design: explicit inspectable deterministic behavior | Apply to every feature and review; discovery still incomplete |
| 6 | Partial: complete word metadata | Signatures/source/effects/tests/dependencies present; IDs/provenance/history incomplete |
| 7 | Verified: concatenative execution | Basic eval and user-word acceptance groups |
| 8 | Verified: typed scalar/nominal stack | Type mismatch and no-effects-on-type-error checks; containers pending |
| 9 | Partial: Int/Float/Bool/String/Unit/List/Option/Result/Record | Scalars and records verified; container surface pending |
| 10 | Verified: nominal refined scalar types | Email/unit distinctions, validators, reload and freeze checks |
| 11 | Partial: explicit effect vocabulary | Declarations and transitive checks; complete providers pending |
| 12 | Partial: effect capability restrictions | Default denial verified; resource/path-scoped policy pending |
| 13 | Partial: 50–100 boring primitives | Initial 45; lists/results/JSON/process/environment/introspection primitives incomplete |
| 14 | Verified: no arbitrary .NET escape | Only host builtins invoke host code; no reflection/call escape surface |
| 15 | Verified: interactive REPL foundation | Multiline CLI smoke and expressions; additional commands tracked below |
| 16 | Partial: required introspection commands | words/describe/source/dependencies/callers/search/effects/tests/test/examples; type-of/recent-words pending |
| 17 | Verified: machine-readable command transport | JSON-lines protocol and diagnostic acceptance checks |
| 18 | Partial: first-class tests | Tests/test-all/failed-tests and gates; structured-value/error expectations incomplete |
| 19 | Partial: examples as metadata | Persisted cases; query currently returns names rather than full example details |
| 20 | Verified: attached documentation | Word doc source, describe, persistence and rollback checks |
| 21 | Verified: incremental word declarations | Parser and user-word acceptance checks |
| 22 | Verified: candidate validation and test-gated commits | Failing/untested commits blocked; dependency and scoped metadata regressions |
| 23 | Verified: temporary words/promote/discard | Session isolation and task-cleanup checks |
| 24 | Verified: readable file dictionary foundation | dictionary.agent reload; richer file layout advisory |
| 25 | Partial: revision history and diff | Revision numbers persist; durable prior source/provenance pending |
| 26 | Partial: task sessions and complete logging | begin/status/commit/abort verified; complete ordered inspection/execution/change events pending |
| 27 | Partial: deterministic structured task log | Aggregates exist; event sequence and complete metrics pending |
| 28 | Verified: dictionary rollback | Abort restores definitions/types/tests/docs/policy after interim commits |
| 29 | Partial: replaceable simulated effects | Isolated virtual file/clock tests; provider interfaces and other domains pending |
| 30 | Verified: small agent command interface foundation | Engine.Dispatch and Protocol; live LLM integration pending |
| 31 | Design: suggested F# solution organization | Existing Core/CLI/Acceptance projects; add host/harness/domain boundaries as needed |
| 32 | Partial: strongly typed internal concepts | Value/type/AST unions; effect identifiers currently checked strings |
| 33 | Pending: small lowered printable IR | Checked-tree interpreter is explicitly an initial implementation step |
| 34 | Partial: bounded runtime and turnaround | Step/depth bounds exist; timing goals unmeasured |
| 35 | Partial: small parser with spans/incomplete detection | Spans and interactive buffering; parse-stage API/incomplete classification pending |
| 36 | Pending: parse/ast/types/ir stage debugging | Current ir accurately labels checked tree; richer stage representations pending |
| 37 | Verified: stack checking and branch joins | Compiler and negative acceptance cases |
| 38 | Verified: minimal stack gymnastics | dup/drop/swap; no extended stack operation assortment |
| 39 | Verified: typed local bindings | Local/branch joins and demo checks |
| 40 | Partial: structural search | Text search exists; search-type/output/effect/dependency pending |
| 41 | Pending: compact dependency/type context | context command and bounded deterministic representation missing |
| 42 | Pending: context/token accounting | Needs provider usage plus harness request/retrieval accounting |
| 43 | Partial: complete small-business fixture | Customer demo only; ten types, stateful domain, 40–60 words and 50–100 tests pending |
| 44 | Excluded from first domain: optional later simulation | Preserve for later generalization, not a V1 blocker |
| 45 | Pending: equivalent conventional environment | F# baseline and matching acceptance oracles missing |
| 46 | Pending: fresh/growing/debugging/discovery/refactoring experiments | Categories require tasks and runner |
| 47 | Pending: all task metrics | Runtime aggregates are a subset; harness and event sources required |
| 48 | Pending: Vocabulary Reuse Ratio | Define start-of-task words and distinguish domain/test/primitive executions |
| 49 | Pending: Primitive Distance | Static dependency expansion, cycle definition, and actual calls separately |
| 50 | Pending: 2k/4k/8k/16k/32k context trials | Enforced harness budgets and recorded outcomes required |
| 51 | Pending: error recovery metrics | Link attempts/errors/resolutions and provider usage, include unresolved errors |
| 52 | Pending: vocabulary quality metrics | Reuse/callers/lifetime/downstream acceptance evidence required |
| 53 | Pending: pollution metrics | Unused/duplicate/short-lived words and growth traces required |
| 54 | Pending: structural duplicate warnings | Commit warning and deterministic fingerprints missing |
| 55 | Pending: deprecate/rename/replace | Stable identity and caller-safe durable edits required |
| 56 | Pending: reproducible snapshot save/load | Snapshot integrity and restoration tests missing |
| 57 | Pending: model-provider harness | API adapter, traces/usage/final state/oracles; no live runs yet |
| 58 | Pending: compact system prompt | Harness prompt artifact and actual usage required |
| 59 | Pending: small initial context strategy | Enforce without dumping dictionary, track retrieval costs |
| 60 | Pending: conventional tool baseline | Confined read/search/edit/test tools and same provider/model |
| 61 | Pending: 60 deterministic benchmark tasks | 20 simple, 20 medium, 10 debugging, 10 refactoring and independent acceptance tests |
| 62 | Partial: vocabulary-building task sequence | Initial premium demo; sequence and comparable costs pending |
| 63 | Partial: CLI observability | task status/log and words; recent-words/graph/metrics pending; GUI optional |
| 64 | Partial: runtime dependency graph | Direct dependencies/callers exist; complete transitive graph APIs pending |
| 65 | Partial: definition-level editing | define replaces words safely; explicit replace-word interface pending |
| 66 | Partial: semantic change boundaries | Scoped word/test metadata checked; dedicated type/test/example/doc operations pending |
| 67 | Pending: stable definition IDs | Preserve across rename/history/reload; currently name identity only |
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
| 80 | Partial: actual agent demo and later-agent reuse | Runtime demo verified; real first/later agents and reuse discovery unmeasured |
| 81 | Pending: marginal cost trend | Sequence, cost data, comparable task difficulty and correctness required |
| 82 | Pending: full prototype outcome | Unfamiliar-project agent task plus measured help for later agents required |

## Named-command completion checklist

Verified foundation: eval, define, words, describe, source, dependencies, callers, search, effects, ir (checked-tree representation only), tests, test, test-all, failed-tests, examples (names only), commit/commit-word, promote, discard, task.begin/status/commit/abort/log, stack. History/diff are process-local and do not satisfy durable history.

Still required or incomplete: type-of, recent-words, search-type, search-output, search-effect, search-dependency, context, parse, ast, types, lowered ir, graph, metrics, transitive-dependencies, transitive-callers, deprecate, rename, replace, replace-word, snapshot save/load, durable history/diff, full example/test metadata, definition-level documentation/test/example edits, agent-run with model/task/snapshot selection.

## Planned implementation order

1. Typed containers, case handling, and coverage rules; establish provider harness contracts in parallel.
2. Stable identity, durable history, snapshots, structured discovery/context/metrics and complete task events.
3. Replaceable effect providers and confined real providers; JSON and necessary primitives within 50–100.
4. Full business fixture and independent acceptance suite, compact prompts, live pilot and conventional baseline.
5. Freeze reproducible fixtures; run and report Flat/Growing/Conventional trials and context-budget variations.

Each validated milestone gets a saved report and Git commit pushed to the private repository. Reports distinguish implemented behavior, directly observed tests, remaining requirements, and research findings. Live-provider usage may require account credentials; continue all independent implementation/validation while identifying that constraint, and never invent usage or benchmark outcomes.
