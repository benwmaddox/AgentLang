# AgentLang: Agent-Oriented Extensible Language Prototype

- Status: prototype specification, refined 2026-10-05
- Implementation: F# on .NET
- Primary user: an AI coding agent
- Secondary user: a developer inspecting and controlling that agent

## Purpose and hypothesis

AgentLang tests whether a small, inspectable programming environment can retain project understanding as executable vocabulary. An agent discovers existing operations, creates and tests a reusable word, uses it to complete a task, and leaves it available to a later agent.

The hypothesis is that accumulated, discoverable vocabulary reduces the marginal cost of the next correct software change. Correctness and task success must remain comparable to a conventional F# environment. Implementing a language is an enabling step, not evidence for the hypothesis.

The environment favors agent comprehension, explicit behavior, deterministic introspection, and testability over syntax terseness, compiler sophistication, and throughput. Named words are the primary unit of development. Plain source files remain the durable representation; agents interact through definition-level commands.

AI coding agents are external users and builders of programs, not language entities or runtime internals. The parser, compiler, dictionary, interpreter, tests, and capability enforcement operate deterministically without an LLM or API credential. A developer or ordinary script can use the same interfaces. Task sessions are transactions and observability records, not autonomous agents. Any model integration belongs to a separate optional experiment harness that consumes the public command protocol; the runtime must not depend on that harness or expose built-in AI behavior.

## Scope and release boundaries

The first usable release is a vertical slice of the original milestones. It must let an agent **inspect, define, run, test, commit, reload, and reuse** a word without directly editing project files.

| First usable prototype | Subsequent experiment release | Deferred |
| --- | --- | --- |
| Typed stack, scalars, records, nominal refined types | List/Option/Result | Native compilation, optimization, WASM |
| Approximately 30 trusted primitives | Expand to 50–100 only for measured needs | General .NET calls or reflection |
| Words, local bindings, conditional branches | Higher-order list operations if justified | Generics, compiler macros, OOP |
| Structured diagnostics and JSON protocol | Conversion hints and richer source tooling | Dependency resolution and package system |
| Introspection and dependency graph | Context generation and budget experiments | Async and distributed execution |
| Language tests, library coverage gates, isolated providers | Full small-business benchmark fixture | GUI, web framework, IDE plugin |
| Candidates, temporary words, promotion | Vocabulary maintenance and snapshots | Curator agent and proof contracts |
| Task rollback, logs, file persistence | Model harness and conventional baseline | Production deployment guarantees |

The initial primitive count is a target, not a success criterion. Every primitive needs a typed signature, declared effects, discoverable metadata, and focused validation. Add capabilities in response to observed task requirements, and log missing-capability requests.

The full research domain remains a small-business backend: Customer, Subscription, Invoice, Payment, Email, and Product. The first demo only needs customers and discount calculations. It is not a substitute for the full evaluation suite.

## Language contract

### Values and stack signatures

Use an explicit data-flow authoring model with named typed inputs, ordinary calls, immutable locals and dot chaining. Dot chains are statically resolved first-input calls with pipeline semantics, not OOP or .NET invocation. Words remain the named unit of composition. The verified semantic IR may retain stack operations internally. RPN is the current executable frontend, but an early migration is now required before further controlled agent experiments; see [the migration contract and acceptance plan](FRONTEND-MIGRATION.md).

Evaluation compiles the entire submitted expression before executing it. Type errors prevent every effect. Internal `Int Int -> Int` consumes two integers and produces one; new source supplies named parameters/call arguments without exposing anonymous stack positions.

Preserve stack-inspired discipline through explicit consumed/produced values and minimal hidden state. No mutable language globals are introduced. Immutable constants remain inspectable typed pure definitions; application state is passed explicitly where practical, and external mutable state is accessed only through declared host effects. Definitions precede use lexically; closeness to first use is lint rather than a type/syntax requirement. A stack representation does not by itself guarantee these policies or low memory use.

Initial scalar types are `Int`, `Float`, `Bool`, `String`, and `Unit`. Integers are signed 64-bit values with defined overflow errors. Floats are floating-point values, not financial decimals. The demo must describe its numeric limitations; a real billing fixture needs an explicit decimal or integer-minor-unit Money type.

Record declarations are nominal: two records with the same fields but different names are different types. Field order determines constructor input order. Each record provides a constructor and typed field accessors. Field access must be checked during compilation, not deferred to a string lookup at runtime.

Future collection types are `List<T>`, `Option<T>`, and `Result<T,E>`. These are parameterized built-ins; they do not imply user-defined generic words.

Named semantic types are required in the first release. `Email` is distinct from `String`; `MetersPerSecond` is distinct from `Float` and from other units over Float. A wrapper has one underlying scalar type, an optional pure validation predicate, and generated construction/unwrapping operations. Construction checks the predicate before creating a value and returns a structured refinement error on rejection. No implicit coercion or representation-based interchange is allowed. Nominal distinction is checked statically; value predicates are checked at construction time. A constructed value retains its nominal type throughout stack checking, records, calls, and persistence.

For example, the intended declaration contract is:

```text
type Email : String
    validate email.valid?
end

type MetersPerSecond : Float
end
```

The validator must have the underlying scalar as its sole input and Bool as its sole output, declare no effects, and have no transitive effects. It must be discoverable and retained with the type. `Email.new : String -> Email` validates; `Email.value : Email -> String` explicitly unwraps. An Email passed to a String operation without unwrapping fails compilation. Cross-unit arithmetic also requires explicitly defined typed words.

An email validation example is illustrative, not a claim of complete Internet email-standard conformance. The validator source and tests define its accepted values. Numeric wrappers can impose ranges or other predicates in addition to nominal units. The first release rejects changing existing type declarations or their frozen validation semantics rather than silently invalidating previously constructed values. A later migration design is separate.

Freeze the validator's complete transitive word dependency closure when its type becomes persistent. Reject replacements affecting that closure, even when the replacement keeps the same signature and effects. This includes indirect helper words. Validate the same closure when loading the project; a type cannot silently rebind to newer validator semantics. Changes to these validators require a new type name in this release.

### Source representation

The current legacy syntax is line-oriented RPN. The example below records executable legacy syntax, not the new target. The next authoring milestone implements expression/dot source with explicit syntax versions; the README must distinguish implemented behavior from the target. Strings use quoted literals with documented escaping. Source locations contain file/source identifier, line, and column. The parser distinguishes incomplete interactive input from invalid complete input. Source/history/editing and coverage preserve the authored frontend.

```text
record Customer
    field kind String
    field balance Float
end

word customer.premium? : Customer -> Bool
    effects none
    doc "Whether this customer has premium status."
    customer.kind
    "premium"
    equals
end

word customer.discounted-balance : Customer -> Float
    effects none
    let customer
    $customer customer.balance
    $customer customer.premium?
    if
        0.9 float.multiply
    else
    end
end

test customer.premium?/premium
    "premium" 100.0 customer.new customer.premium?
    => true
end
```

The implementation README is the authoritative executable syntax reference, including generated record-word names and primitive names. A syntax change must update examples and acceptance checks together. This specification's example illustrates the contract rather than fixing all identifiers permanently.

`let name` consumes the top stack value and creates a local binding; `$name` pushes it. Locals are word-scoped and statically typed. Branches consume a Bool, and both paths must produce compatible stack shapes. An omitted else behaves as an empty branch and must still type-check. Initial definitions need not support recursion; reject it explicitly if unsupported. Minimal stack manipulation is limited to dup, drop, and swap, with their types resolved statically.

### Compiler and runtime

Use F# discriminated unions for types, values, effects, AST nodes, diagnostics, and IR. Keep parser, compiler, runtime, and protocol concerns separate without requiring a separate assembly for every module. The initial usable slice historically interpreted a checked expression tree. The required executable boundary is now a verified typed semantic IR shared by the interpreter and any later native backend. Source ASTs remain authoring and diagnostic representations; runtime execution must not fall back to them.

Compilation parses a versioned authoring AST, resolves names/arguments, checks types/effects, and lowers into verified semantic IR. The runtime executes that IR, not the AST. No JIT/native backend is required. Expose source, signatures, named inputs, effects, dependencies and IR through introspection; deeper compiler-stage queries remain planned.

The runtime must bound execution steps and call depth and return structured errors when limits are exceeded. Define division-by-zero, overflow, invalid conversion, and stack-underflow behavior. Runtime errors are not host stack traces.

## Trusted capability boundary

Only host-implemented primitives can perform external effects. User words compose these primitives; they cannot access .NET reflection, arbitrary libraries, or an escape hatch.

Effect names reserve the following vocabulary: fs.read, fs.write, db.read, db.write, network.read, network.write, process.execute, clock.read, random.read, and console.write. The first release may implement only a subset. An unknown effect must be rejected rather than treated as harmless.

Compilation computes the transitive effects of each word and verifies that every effect is declared. A declared effect does not grant permission: runtime policy authorizes each effect operation. Default policy denies external effects. Policy validation must occur before invoking a provider, including when effects are reached through composed words.

Providers are replaceable host implementations. Tests use fresh deterministic virtual filesystem and clock providers with no access to production host state. Separate tests must not share mutable provider state. Future network, database, and random providers follow the same boundary.

Do not claim filesystem glob confinement until implemented and tested. A later real filesystem provider needs canonical path checks, an explicit symlink/reparse-point policy, and tests for traversal. An in-memory provider is sufficient for the first experiment.

Project commits and task management are host control-plane operations. Their file writes are not granted to language programs as fs.write. External effects are generally not reversible; abort restores language state only, and reports effects already performed.

## Dictionary and change lifecycle

A word has a stable identity, name, signature, effects, source, documentation, dependencies, examples, attached tests, revision, provenance, and quality level. Callers are derived from the dependency graph. Metadata output is deterministic and sorted where ordering has no semantic meaning.

Definitions first enter candidate state. Candidate words can be compiled, evaluated, inspected, and tested in the current session. Temporary words are explicitly session-scoped candidates. Promotion changes lifecycle intent; it must not bypass test or dependency validation.

A commit must:

1. Parse and type-check every affected definition.
2. Resolve all dependencies and validate declared effects.
3. Ensure no persistent word depends on an uncommitted or temporary word.
4. Validate affected callers when replacing definitions.
5. Run all attached tests in isolated deterministic providers and satisfy the selected quality gate.
6. Persist the complete accepted change atomically, or leave persistent state unchanged.

The initial API may commit one word at a time. If so, dependencies must be committed first; a multiword task commit must use dependency order and an atomic boundary. A project word requires at least one attached passing test. A library word additionally requires complete instruction and conditional-branch coverage. Temporary downstream logic can be evaluated without meeting the library gate. The benchmark harness supplies independent acceptance tests.

Records and their generated words are one change boundary. Refined types and their constructors, unwrappers, and validation dependencies are another. Schema changes must not silently reinterpret existing values or invalidate callers. Until migrations exist, incompatible changes should be rejected.

Store canonical readable `.agent` sources in a project directory. A single dictionary file is acceptable initially; per-type/word/test files are optional organization. Loading a project must reproduce signatures, definitions, tests, examples, IDs, and revision metadata. Load failures must be reported without partially accepting a corrupt dictionary. Save files through a temporary file and atomic replacement where supported. History must not be advertised as a full VCS.

## Tests, examples, and observability

Tests have an owning word and a unique case name. They execute a body and compare the complete resulting stack with expected values. Tests distinguish compilation errors, runtime errors, and assertion failures. Examples use a similar source form but remain metadata and are not automatically counted as tests.

Two quality levels are required:

| Level | Intended use | Commit requirement |
| --- | --- | --- |
| Project | Downstream application composition | At least one attached test, all attached tests pass |
| Library | Reusable vocabulary relied on across tasks | Project requirements plus 100% executable instruction coverage and both outcomes of every conditional |

Coverage is computed from runtime execution of the current compiled word by its attached tests. Give instructions and branches stable identifiers within a revision and map them to source spans. Report executed/total instructions, observed/total branch outcomes, and uncovered source locations. Synthetic bookkeeping such as Return is excluded from the executable-instruction denominator. Do not count coverage from unrelated prior interactive executions. Reset measurement when source or dependencies change.

The initial library gate applies to the word's own implementation; dependency quality is exposed separately. It must not be presented as whole-system coverage. A library word's use of lower-quality dependencies is visible to the developer and agent. Tests run with virtual providers; successful test coverage must not grant production effect permissions.

Branch coverage requires both true and false outcomes at every conditional, including an omitted else. It does not require every possible combination of branches, prove correctness, or substitute for boundary and invalid-input tests. The first language supports conditionals; any future looping, pattern matching, or short-circuit operator needs an explicit coverage rule before qualifying for library use. A word with no branches still requires at least one passing test and complete executable-instruction coverage.

The commit operation selects the quality level explicitly, for example `commit-word customer.premium? --library` or a protocol library flag; exact transport syntax is documented in README. Preserve the level on reload. Replacements of a library word must satisfy its gate again and cannot silently downgrade it. Introspection reports current coverage and whether the selected commit policy passes.

Required discovery operations are words, describe, source, dependencies, callers, search, effects, ir, tests, test, test-all, examples, and recent-words. Describe includes implementation kind, lifecycle, signature, effect set, dependencies, documentation, revision, and test status. A test status must say whether tests were run on the current revision; stale results must not appear as current passes.

Search initially matches names and documentation deterministically. Type/output/effect/dependency filters, transitive graph queries, compact context generation, duplicate warnings, and vocabulary maintenance can be added after the first slice. Structural duplicate detection warns; it does not prove semantic equivalence.

Every operation supports a stable JSON representation through the agent protocol. Responses identify success or failure and carry typed payloads or diagnostics. Diagnostics include code, message, word/operation where applicable, source span, and expected/actual type or stack state. Human presentation is an adapter over these same results.

The REPL accepts multiline declarations and shows one result per completed submission. The JSON-lines mode accepts one JSON request per line, with multiline source encoded as a JSON string. Standard output contains only response JSON in that mode; prompts and debugging output belong elsewhere.

## Tasks and rollback

Task begin records a goal and snapshots dictionary state, including records, words, candidates, tests, examples, and revision metadata. Nested tasks can be rejected initially. Status exposes created/changed words, inspected/executed words, tests, effects, and errors. Commit accepts validated changes and completes the task. Abort restores the snapshot and clears task-local changes, including disk state changed through interim word commits if those are permitted.

Task logs use ordered event sequence numbers. Given the same fixture, commands, and deterministic providers, semantic events must be reproducible. Task IDs, wall-clock timestamps, and duration are metadata and need not be byte-identical. Failed tasks and aborts retain research logs without retaining their dictionary edits.

Do not describe a task as transactional for external I/O. Task log retention and dictionary rollback are separate requirements.

## Acceptance criteria for the first usable release

- `10 20 add` returns 30; a submitted type mismatch fails before any effect executes.
- User words compose trusted words and expose their source, IR, signature, effects, and dependencies.
- Records construct typed values; incorrect constructor or field use is rejected during compilation.
- Nominal scalar types reject representation-based interchange. Email construction validates input; distinct numeric units cannot be mixed without explicit conversion. Their declarations and predicates survive reload and rollback.
- Local bindings and both conditional paths have checked stack behavior.
- Undeclared transitive effects fail definition validation. Declared but unauthorized effects fail before provider invocation.
- An attached failing test prevents persistence, and each test uses isolated provider state.
- An untested project word cannot commit. A library word with an uncovered branch cannot commit; adding tests for both outcomes permits commit and exposes source-mapped coverage. Temporary downstream evaluation does not require library coverage.
- Persistent words cannot retain candidate/temporary dependencies. Replacement cannot invalidate callers silently.
- A fresh process reloads committed vocabulary and its tests; invalid storage does not load partially.
- A task can define, test, and commit vocabulary, then abort and restore its earlier dictionary state.
- JSON requests and diagnostics can be consumed without scraping human prose.
- A premium-discount demo shows discovery, definition, testing, commit, and reuse in a fresh session.
- Automated acceptance checks exercise these contracts, including negative cases. Build and checks run from documented commands without a model API key.

These criteria demonstrate infrastructure readiness. They do not establish token savings, correctness gains, or research success.

## Evaluation design

Compare three modes with the same model, task intent, acceptance oracle, and effect fixture:

| Mode | Environment | Retention |
| --- | --- | --- |
| A: Flat | AgentLang primitives and common fixed domain fixture | Agent-created words discarded between tasks |
| B: Growing | Same AgentLang starting fixture | Accepted agent-created words retained |
| C: Conventional | Equivalent F# fixture and ordinary file tools | Accepted code retained |

Mode A versus B isolates vocabulary retention. B versus C compares the complete workflow. Keep domain data and host capabilities equivalent. Do not give one mode ready-made business rules absent from the others.

Begin with a small pilot sequence: premium classification, discounts, renewal eligibility, annual-renewal discounts, and reminder rules. Then expand toward 20 simple tasks, 20 medium tasks, 10 debugging tasks, and 10 refactoring tasks. Each has hidden deterministic acceptance tests, a fixed starting snapshot, and a documented task dependency. Vocabulary sequences intentionally retain prior successful changes; independent tasks reset state.

Run multiple trials, rotate mode order, and record model version, prompts, tool descriptions, sampling parameters, token accounting, retries, and harness version. Report failures, timeouts, missing primitives, and raw-source fallbacks. Do not silently remove difficult tasks after seeing results. Use confidence intervals and per-task paired comparisons; a single lucky demo is not sufficient.

The initial context contains language basics, core commands, the task, and a high-level project description. It must not dump the dictionary. Discovery is part of the treatment, so include its calls and token costs. The conventional baseline gets equivalent goals and repository tools. Tests provided to agents must be separated from hidden acceptance oracles.

Context-window experiments at 2k, 4k, 8k, 16k, and 32k require a documented harness budget and truncation/retrieval policy. Account for system prompts, protocol schemas, and tool results. If a provider cannot enforce a window, report an application-enforced budget rather than claiming model-window equivalence.

## Metrics and source of truth

| Metric | Source |
| --- | --- |
| Task success | Independent acceptance oracle |
| Input/output tokens and cached tokens | Model-provider usage, with estimates labeled separately |
| Initial/retrieved context bytes and tokens | Harness request accounting |
| Turns, tool calls, wall-clock duration | Harness |
| Compiles, compile/test failures, errors, effects | Runtime event log |
| Words created/replaced/reused, temporary/promoted words | Dictionary and event log |
| Reverted attempts | Explicit abort/revert events; do not infer from final state |
| Vocabulary size, unused words, callers, revision lifetime | Snapshot graph and history |

Runtime counters cannot measure LLM tokens without harness data. Display unknown values as unavailable, never zero. Keep attempted and successful calls distinct and distinguish tests from independent acceptance tests.

Vocabulary Reuse Ratio is calls to domain words present at task start divided by all domain-word calls during the task. Exclude primitives and test executions from the primary ratio, and report those separately. Define a zero denominator as unavailable.

Primitive Distance is a static expansion count of primitive calls in the dependency graph. Branches and repetition mean it is not executed work; label it as structural compression, define cycle handling, and report actual execution counts separately.

Measure vocabulary quality through subsequent reuse, callers, lifetime, and downstream correctness. Report unused and structurally duplicate words and short-lived replacements as pollution signals. Do not treat the number of new words as an automatic benefit.

Error recovery measures need linked error/attempt events. Turns or tokens per resolved error are harness-derived. Count unresolved errors too. An inferred recovery must be labeled rather than silently classified as observed.

## Research decision

The north-star metric is marginal cost of a correct subsequent change as accepted vocabulary accumulates. Report cost and success together to avoid rewarding cheap failures.

A promising signal is at least 20% fewer total input tokens or 20% fewer turns at comparable task success. Predefine what comparable means for the chosen sample and report uncertainty. Stronger evidence is reduced cost on later tasks without simpler tasks, or comparable success at substantially smaller context budgets.

Investigate contrary evidence: ignored vocabulary, duplicate growth, expensive discovery, repeated stack confusion, language errors dominating time, raw-source fallbacks, missing primitives, or a conventional baseline performing as well with lower cost. These are useful outcomes, not failures to hide.

The experiment must answer whether agents create reusable words, whether later agents find them, whether retention changes cost/latency/correctness, whether metadata helps recovery, whether smaller contexts remain viable, and whether vocabulary quality degrades.

## Late research: syntax and stack locality

The user subsequently requested an early switch away from RPN after finding it difficult to read. Expression/dot source is now the next authoring milestone, retaining words and verified semantic IR; see [the migration plan](FRONTEND-MIGRATION.md). Remaining later research compares presentation/locality instead of postponing that switch. Preserve strong types, effects, inspection and library test/coverage gates.

The stack model has a potentially useful property independent of notation: it encourages a word to operate on recently produced values in a local flow instead of repeatedly reaching into distant state. Compare current RPN with a small alternative using named inputs, local bindings, or expression/pipeline notation that retains this property and lowers to the same semantic IR. Do not assume either RPN or conventional syntax wins.

The user's subsequent discussion favors strong encouragement of data flow rather than compulsory Forth-style source. The leading candidate for research is an expression-oriented frontend with explicit pipelines, named typed inputs, immutable nearby locals, and ordinary calls/branches when needed. Prefer guidance and inspectable diagnostics over hard locality restrictions. Implicit current-value blocks, rebinding, and first-class function composition require separate justification. [The discussion review](../reports/017-data-flow-syntax-review.md) records the recommendation, semantic questions, and experiment boundary; the frontend is not yet a settled or implemented contract.

Use equivalent tasks, domain vocabulary, acceptance oracles, semantic behavior, and backend. Measure success, stack-order/type errors, error recovery, inspection/context cost, generated code, and tool interactions. Review whether source makes value flow easier to follow and whether the alternative causes more distant variable/state references. Separate frontend results from vocabulary-retention results.

Also investigate memory behavior under the later LLVM development/release backend design. LLVM would replace or supplement interpreted execution; F# may remain the host/compiler implementation. Allocation strategy, value representation, ownership/lifetimes, runtime services, and retained metadata determine memory usage. RPN, an operand stack, or LLVM alone does not establish a lower-memory system. Compare footprint, startup, allocation rate, and peak memory on equivalent workloads, changing one relevant factor at a time where practical. Any frontend change must pass shared semantic conformance and preserve source-mapped diagnostics and coverage.

## Delivery sequence

1. Ship and validate the first usable slice and demo.
2. Run a fresh external subagent through the small protocol; record supplied context, interactions, independent results, and available usage.
3. Complete the user-directed expression/dot frontend migration and validate source/persistence/IR conformance before further controlled agent trials. Close-to-first-use is lint; retain explicit values/effects and no mutable language globals.
4. Freeze equivalent new-frontend fixtures and run a pilot against flat and conventional modes before optional language expansion.
5. Add capabilities justified by task requirements and pilot failures, then freeze fixtures and harness versions.
6. Run the controlled suite and publish outcomes, uncertainty, and limitations.

Future curator agents, synthesized context, maturity levels, contracts, and a second game/simulation domain remain hypotheses for later work. They must not delay the first measured agent task.

## Conditional development and release backends

If the initial experiments support the hypothesis, evolve toward separate development and release runtimes. Development retains the interpreter, REPL, introspection, and hot word replacement, and may use LLVM JIT compilation for stable or frequently executed words. Release uses LLVM AOT to emit optimized native code with a minimal runtime and ships neither the interpreter nor compiler.

The typed semantic IR is the authoritative executable representation from the initial architecture. Source and AST are authoring representations; type/effect checking lowers them to this IR before execution. Interpreter, JIT, and AOT must share type identities, numeric/error behavior, control flow, effects, and value semantics. Native performance, memory use, and startup approaching Rust or C are later evaluation goals, not current guarantees.

LLVM implementation remains outside V1 and is conditional on successful experiments. The Runtime now executes the small verified semantic IR through its interpreter, as recorded in report 010. The source frontend migration must preserve that boundary without adding LLVM.
