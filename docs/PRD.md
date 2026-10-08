# AgentLang: Agent-Oriented Extensible Language Prototype

- Status: prototype specification, refined 2026-10-08
- Current prototype implementation: F# on .NET; implementation language is not a product constraint
- Primary user: an AI coding agent
- Secondary user: a developer inspecting and controlling that agent

Implementation-language decision: prioritize correctness, maintainability and
measured runtime behavior over retaining any particular host language. Keep the
existing F# compiler/frontends while they serve those goals; choose the native
runtime implementation independently when allocation, mailboxes and platform
integration need it. Keep the interpreter and native backends consistent with
the current semantic IR across any implementation-language change. A rewrite requires a
concrete benefit; it is not a prerequisite for LLVM-generated native output.

Prototype change policy: breaking changes are expected while testing the design.
Do not maintain old syntax, APIs, layouts, fixtures or compatibility adapters
solely for backwards compatibility. Prefer one current design and update its
compiler, runtime, examples and tests together. Version markers are optional
diagnostic aids, not release commitments. Preserve frozen experiment evidence
and historical reports so prior conclusions remain auditable; that does not
require keeping old implementations in the current runtime.
## Purpose and hypothesis

AgentLang tests whether a small, inspectable programming environment can retain project understanding as executable vocabulary. An agent discovers existing operations, creates and tests a reusable word, uses it to complete a task, and leaves it available to a later agent.

The primary hypothesis is that accumulated, discoverable, strongly typed vocabulary helps fresh AI coding agents make reliable changes, find established behavior, catch violations before committing, and recover from errors. Forth/Lisp-style vocabulary growth is valuable when later builders can discover and safely compose project concepts. Lower token, turn and interaction costs are secondary benefits to measure. Conventional F# is a strong correctness baseline; the experiment tests definition-level change boundaries, discoverability and reusable behavioral contracts as well as static checking. Implementing a language is an enabling step, not evidence for the hypothesis.

The environment favors agent comprehension, explicit behavior, deterministic introspection, and testability over syntax terseness, compiler sophistication, and throughput. Named words are the primary unit of development. Plain source files remain the durable representation; agents interact through definition-level commands.

### Product targets and comparison roles

User clarification, 2026-10-08: aim for data modeling and structural correctness
close to F#, with source syntax more familiar to developers of C-family
languages. Strong nominal/refined types, typed records, explicit alternatives,
exhaustive matching and validated construction should make invalid states hard
to express. Track implemented versus planned checks; test coverage complements
static guarantees and does not replace them. Do not claim general F# parity from
a small benchmark or from the compiler being implemented in F#.

F# is also the primary application-performance baseline. The intended outcome
is higher sustained successful throughput with at most a modest increase in
whole-process RAM, at comparable tail latency and correctness. Lower RAM is
welcome but is not required to win; minimum memory at the expense of useful
throughput is not the objective. Define the acceptable memory increase for a
workload before measuring; no percentage is selected yet. Compare throughput
and memory together, including equal-memory controls and a predeclared modest
extra-memory allowance. Native C/Rust comparisons remain useful diagnostics,
not a replacement for this F# product baseline.

Erlang/OTP is the isolation reference: aim toward independently owned mailbox
state, explicit message transfer, bounded queues and work, and contained
message/handler failures. Full OTP equivalence, distributed infrastructure and
its entire supervision ecosystem are not prerequisites. State precisely what
each implemented boundary contains; mailbox ownership alone does not establish
protection against native-runtime faults or rollback of external effects.

For now, arenas are the general allocation model for language-managed dynamic
values, not an optimization limited to request handlers. The owning data stack
can live in an arena; longer-lived values need appropriate retained storage,
rather than an implicit fallback to independently allocated heap objects or GC.
This does not require every value to share one arena or lifetime. Fixed/static
mailbox state and host/provider allocations remain explicit boundaries. The
current managed interpreter is not evidence that this native allocation model
has been fully implemented.

The first evaluation workload is request/response processing, such as a web
application, with frequent bulk cleanup at compiler/runtime-controlled
boundaries. Retained mailbox state, responses still being sent and pending-I/O
data have separate explicit lifetimes. Compare keeping request working storage
through an await against retaining necessary values and returning scratch at
suspension. Resetting an arena permits reuse of its backing; it need not free
that backing to the OS. Do not reset while live responses or providers still
need its data. This workload focus does not require adding a web framework now.

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

A solid, compact core that can be built upon is a product requirement. Keep trusted host mechanisms separate from authored domain vocabulary: values, strong types, semantic IR, execution, explicit effects and deterministic inspection form the foundation; ordinary business behavior should be composed above it. Measure missing host capabilities, primitive growth, core dependency surface and whether useful abstractions can be built without adding escape hatches. A small primitive count alone does not prove a small implementation or low memory use.

The initial primitive count is a target, not a success criterion. Every primitive needs a typed signature, declared effects, discoverable metadata, and focused validation. Add capabilities in response to observed task requirements, and log missing-capability requests.

The full research domain remains a small-business backend: Customer, Subscription, Invoice, Payment, Email, and Product. The first demo only needs customers and discount calculations. It is not a substitute for the full evaluation suite.

## Language contract

### Values and stack signatures

Use an explicit data-flow authoring model with named typed inputs, ordinary calls, immutable locals and dot chaining. Dot chains are statically resolved first-input calls with pipeline semantics, not OOP or .NET invocation. Words remain the named unit of composition. The verified semantic IR may retain stack operations internally. Flow expression/dot source is implemented through typed semantic IR with durable frontend-tagged sources. The default Flow authoring cutover passed the complete 27-check Release gate; explicit Stack/RPN compatibility remains. Controlled external-agent comparisons are the next research step; see [the migration contract and acceptance plan](FRONTEND-MIGRATION.md).

Evaluation compiles the entire submitted expression before executing it. Type errors prevent every effect. Internal `Int Int -> Int` consumes two integers and produces one; new source supplies named parameters/call arguments without exposing anonymous stack positions.

Preserve stack-inspired discipline through explicit consumed/produced values and minimal hidden state. No mutable language globals are introduced. Immutable constants remain inspectable typed pure definitions; application state is passed explicitly where practical, and external mutable state is accessed only through declared host effects. Definitions precede use lexically; closeness to first use is lint rather than a type/syntax requirement. A stack representation does not by itself guarantee these policies or low memory use.

Initial scalar types are `Int`, `Float`, `Bool`, `String`, and `Unit`. Integers are signed 64-bit values with defined overflow errors. Floats are floating-point values, not financial decimals. The demo must describe its numeric limitations; a real billing fixture needs an explicit decimal or integer-minor-unit Money type.

Record declarations are nominal: two records with the same fields but different names are different types. Field order determines constructor input order. Each record provides a constructor and typed field accessors. Field access must be checked during compilation, not deferred to a string lookup at runtime.

Future collection types are `List<T>`, `Option<T>`, and `Result<T,E>`. These are parameterized built-ins; they do not imply user-defined generic words.

Named semantic types are required in the first release. `Email` is distinct from `String`; `MetersPerSecond` is distinct from `Float` and from other units over Float. A wrapper has one underlying scalar type, an optional pure validation predicate, and generated construction/unwrapping operations. Construction checks the predicate before creating a value and returns a structured refinement error on rejection. No implicit coercion or representation-based interchange is allowed. Nominal distinction is checked statically; value predicates are checked at construction time. A constructed value retains its nominal type throughout stack checking, records, calls, and persistence.

For example, the target Flow declaration contract is:

```text
type Email : String { validate email::valid?; }
type MetersPerSecond : Float {}
```

The explicit validator reference addresses the dictionary word `email.valid?`.
Flow project documents can declare records, scalar types, words, tests and
examples together. The existing definition operation must validate the complete
proposed dictionary before exposing any member: a failed declaration must not
leave partially staged types, generated constructors or words. Commit remains
an explicit dependency-closure operation with the existing test and library
coverage gates. Preserve each authored type declaration's exact source bytes,
frontend version and validator identity across persistence, reload, snapshots
and task abort. See [the migration contract](FRONTEND-MIGRATION.md) for the
implementation and cutover gates; this specification does not claim those gates
have already passed.

The validator must have the underlying scalar as its sole input and Bool as its sole output, declare no effects, and have no transitive effects. It must be discoverable and retained with the type. `Email.new : String -> Email` validates; `Email.value : Email -> String` explicitly unwraps. An Email passed to a String operation without unwrapping fails compilation. Cross-unit arithmetic also requires explicitly defined typed words.

An email validation example is illustrative, not a claim of complete Internet email-standard conformance. The validator source and tests define its accepted values. Numeric wrappers can impose ranges or other predicates in addition to nominal units. The first release rejects changing existing type declarations or their frozen validation semantics rather than silently invalidating previously constructed values. A later migration design is separate.

Freeze the validator's complete transitive word dependency closure when its type becomes persistent. Reject replacements affecting that closure, even when the replacement keeps the same signature and effects. This includes indirect helper words. Validate the same closure when loading the project; a type cannot silently rebind to newer validator semantics. Changes to these validators require a new type name in this release.

### Next Flow syntax revision

The requested next frontend revision uses `fn` for function declarations while
retaining the inspectable dictionary and stable definition identities. Plain
record fields use property syntax (`customer.kind`); ordinary function calls
retain parentheses. Property access is restricted to declared record fields and
lowers to the existing pure, statically typed accessor semantics, without hidden
computation or effects. Add typed infix equality (`==`) with the same equality
rules and nominal-type checks as the existing equality operation; it introduces
no implicit conversions. Define precedence and reject incompatible operands
before execution. Function-local `doc` metadata and compiler-checked effect
contracts remain parts of the design. An omitted `effects` declaration means
`effects none`; calling effectful operations then fails validation. Nonempty
effects require an explicit declaration. Introspection reports the effective
contract, including `none` when omitted, without silently granting inferred
effects.

```text
fn customer.premium?(customer: Customer) -> Bool {
    doc "Whether this customer has premium status."

    customer.kind == "premium"
}
```

The formatter groups any effect declaration and documentation at the beginning
of the function (effects before documentation when both exist), with exactly
one blank line between that metadata and executable body code. It need not emit
`effects none` for an omitted pure contract. This layout does not alter semantic
meaning or source provenance; formatting is an explicit source edit.

Library qualification must be expressible in source. The proposed declaration
is `library fn`; ordinary `fn` retains the lighter project-function gate.
Library intent is not qualification: publication must validate and run the
strict gate before persisting library maturity, and introspection must expose
the distinction. Do not infer library status merely from a namespace or file.

Modules form enforced function-call boundaries. Ordinary `fn` definitions are
callable within their declaring module; another module may call an authored
function only after that function has passed library qualification. Introduce
explicit, durable module membership and compiler/verified-program checks rather
than deriving access from dotted names or file locations. Renames must preserve
membership and identity. Keep this a small module model without dependency
resolution or sophisticated package management. Introspection may still inspect
internal functions; visibility for inspection is separate from call access.

Host-launched entry functions may remain ordinary functions through an explicit
entry-point mechanism; this does not permit ordinary cross-module calls to them.
Define availability rules for trusted primitives, generated constructors and
field accessors separately. Library functions may call only qualified library
functions, trusted primitives and generated type operations; this applies to
same-module private helpers and callbacks supplied to collection operations as
well as direct and cross-module calls. Ordinary functions may call ordinary or
library functions subject to module visibility. Library qualification is separate
from public export visibility: an internal helper may qualify without becoming
an exported API. Qualification must bind the complete dependency closure and
be invalidated by relevant helper revisions, with requalification and
affected-caller tests before publication. This provides a reusable tested
interface while development and host entry functions retain the lighter gate.

The required library gate includes 100% own-function branch coverage and 100%
coverage of finite declared return values. A Bool-returning function must
exercise both true and false, even if its body has no syntactic branch. Cover
every declared enum case when enum types are supported. For Option/Result,
cover each inhabitable alternative; fully finite payloads also require their
finite values. Infinite payload domains do not require exhaustive values, but
their alternatives and meaningful boundary tests remain obligations. A broad
finite return contract whose values cannot all be produced cannot qualify by
silently exempting unreachable values; it needs an appropriately narrower
contract or remains a project function. These are additional requirements,
not a claim that the current coverage implementation already enforces them.

Here exhaustive value coverage means explicitly enumerated domains: Bool,
enum cases, Unit, and supported closed composites of enumerated payloads.
Large scalar domains such as Int, Float, String and numeric Money are not
enumerated exhaustively merely because a runtime representation is bounded.
For composites containing those domains, cover each applicable variant/tag
and use boundary and behavioral assertions for the payload.

Library functions also require finite input coverage: for each Bool or enum
parameter, their own attached tests must exercise every valid declared value.
Record the actual parameter values at tested function invocation; constants in
expectation expressions or unrelated callers cannot satisfy this obligation.
Track coverage independently for each parameter, alongside finite return-value
and branch coverage. Add meaningful interaction tests when parameters combine;
per-parameter coverage does not prove every combination or the business rule.
Do not silently substitute a passing structural coverage result for these
input/output obligations. Enum coverage becomes applicable when enum types are
implemented; the initial Bool cases must work even for branchless functions.

Exhaustive pattern matching is required by the compiler for every function,
independently of library maturity or observed test coverage. For each supported
closed variant type (including Bool, Option, Result and future enums), reject a
match that leaves any possible case unhandled before execution or persistence.
A guarded case cannot establish exhaustiveness merely because its tests passed;
require statically sufficient pattern coverage or an unconditional fallback.
Diagnostics should identify missing cases and their source spans. Library tests
must still exercise their own branches and assert behavior: a statically handled
case is not proof that its implementation is correct. The current Flow/2
[closed-enum slice](CLOSED-ENUMS.md) supports payload-free enums and exhaustive
matching for project functions. Enum library qualification remains blocked until
finite input/output coverage is enforced; native enum execution is not yet supported.

Tests must support injected deterministic effect providers for IO and other
declared effects. Injection must preserve the function's effect contract and
capability enforcement rather than granting an escape hatch. Record coverage
from the tested function's execution, excluding expectation expressions and
unrelated callers, and bind successful qualification to the tested revision
and dependency identities. The R02 result shows why complete structural
coverage and self-authored passing expectations alone cannot prove behavior.

Tests must also support scoped dictionary function redefinition, including for
functions that perform IO. A test can install a temporary replacement under the
same function identity, execute its target so nested calls see the replacement,
and automatically discard it when the test ends. This is a typed dictionary
overlay for one test, not a persistent replacement or only a host-provider mock.
Require the same input/output signature and an explicit compatible effect
contract; retain the original caller's declared effects and capability checks.
Never modify the persistent dictionary, history or production bindings.
Remove the overlay and restore original dispatch on success, assertion failure, runtime error,
cancellation and test abort, and prevent leakage between tests or sessions.
The overridden body supplies no coverage evidence for the original body;
mocking the tested function cannot satisfy its own library qualification.
Record active overrides separately in test diagnostics and logs. Define concrete
test syntax and verify nested-call routing, cleanup and isolation before claiming
this mechanism is implemented. Provider injection remains independently useful.
Compiled callers must see the scoped replacement too. Invalidate all affected
transitive JIT callers, inlined bodies and callback specializations; the safe
initial policy is to run that closure interpreted against the test overlay.
Discard test-specific code and restore original-generation dispatch afterward.
This is controlled late binding through the dictionary with statically checked
identities/signatures/effects, not arbitrary runtime method discovery. AOT release
builds may pin the dictionary graph and optimize calls without a live interpreter.

Flow/2 implements `fn`, record properties, typed `==`, omitted pure effects and
explicit formatting (report 100). Module/library qualification and scoped test
overrides below remain planned. Keep
the authoritative semantic IR unchanged where these forms can lower to existing
operations. Preserve existing authored sources and frontend versions across
reload, introspection and history; define compatibility and update executable
examples, diagnostics and validation together. Frozen agent comparisons retain
their pinned source syntax and runtime rather than mixing frontend revisions.

### Source representation

The current legacy syntax is line-oriented RPN. The example below records executable legacy syntax, not the new target. Expression/dot source now supports typed declarations, words, tests and examples with explicit syntax versions. Default Flow authoring, interactive input and harness selection passed the complete 27-check Release gate (report 052); exact committed-source CI is recorded separately from local dirty-tree evidence. Strings use quoted literals with documented escaping. Source locations contain file/source identifier, line, and column. The parser distinguishes incomplete interactive input from invalid complete input. Source/history/editing and coverage preserve the authored frontend.

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

The initial API may commit one word at a time. If so, dependencies must be committed first; a multiword task commit must use dependency order and an atomic boundary. A project word requires at least one attached passing test. A library word additionally requires complete instruction and branch coverage, the finite input/return coverage specified above, and qualified authored dependencies. Temporary downstream logic can be evaluated without meeting the library gate. The benchmark harness supplies independent acceptance tests.

Records and their generated words are one change boundary. Refined types and their constructors, unwrappers, and validation dependencies are another. Schema changes must not silently reinterpret existing values or invalidate callers. Until migrations exist, incompatible changes should be rejected.

Store canonical readable `.agent` sources in a project directory. A single dictionary file is acceptable initially; per-type/word/test files are optional organization. Loading a project must reproduce signatures, definitions, tests, examples, IDs, and revision metadata. Load failures must be reported without partially accepting a corrupt dictionary. Save files through a temporary file and atomic replacement where supported. History must not be advertised as a full VCS.

## Tests, examples, and observability

Tests have an owning word and a unique case name. They execute a body and compare the complete resulting stack with expected values. Tests distinguish compilation errors, runtime errors, and assertion failures. Examples use a similar source form but remain metadata and are not automatically counted as tests.

Two quality levels are required:

| Level | Intended use | Commit requirement |
| --- | --- | --- |
| Project | Downstream application composition | At least one attached test, all attached tests pass |
| Library | Reusable vocabulary relied on across tasks | Project requirements plus complete executable instruction/branch coverage, finite input/return coverage, and qualified authored dependencies |

Coverage is computed from runtime execution of the current compiled word by its attached tests. Give instructions and branches stable identifiers within a revision and map them to source spans. Report executed/total instructions, observed/total branch outcomes, and uncovered source locations. Synthetic bookkeeping such as Return is excluded from the executable-instruction denominator. Do not count coverage from unrelated prior interactive executions. Reset measurement when source or dependencies change.

Coverage applies to the function's own implementation and its own attached tests; it must not be presented as whole-system coverage. The required dependency rule separately permits library functions to call only qualified authored library functions or trusted/generated operations. Earlier implementation milestones exposed lower-quality dependencies without enforcing this rule; that behavior is not the target contract. Tests run with virtual providers; successful test coverage must not grant production effect permissions.

Branch coverage requires both true and false outcomes at every conditional, including an omitted else. It does not require every possible combination of branches, prove correctness, or substitute for boundary and invalid-input tests. The first language supports conditionals; any future looping, pattern matching, or short-circuit operator needs an explicit coverage rule before qualifying for library use. A word with no branches still requires at least one passing test and complete executable-instruction coverage.

The commit operation selects the quality level explicitly, for example `commit-word customer.premium? --library` or a protocol library flag; exact transport syntax is documented in README. Preserve the level on reload. Replacements of a library word must satisfy its gate again and cannot silently downgrade it. Introspection reports current coverage and whether the selected commit policy passes.

Required discovery operations are words, describe, source, dependencies, callers, search, effects, ir, tests, test, test-all, examples, and recent-words. Describe includes implementation kind, lifecycle, signature, effect set, dependencies, documentation, revision, and test status. A test status must say whether tests were run on the current revision; stale results must not appear as current passes.

Discovery should be progressive: a names-only inventory is available through
`words` with `compact:true`, followed by full metadata for selected words.
Descriptions provide parser-verified exact Flow target spellings, or an explicit
reason a legacy identity cannot be expressed. Smaller payloads and fewer naming
errors are interface improvements; external-agent trials must establish whether
they reduce the cost of correct changes.

Search initially matches names and documentation deterministically. Type/output/effect/dependency filters, transitive graph queries, compact context generation, duplicate warnings, and vocabulary maintenance can be added after the first slice. Structural duplicate detection warns; it does not prove semantic equivalence.

Every operation supports a stable JSON representation through the agent protocol. Responses identify success or failure and carry typed payloads or diagnostics. Diagnostics include code, message, word/operation where applicable, source span, and expected/actual type or stack state. Human presentation is an adapter over these same results.

Authoring conventions must themselves be discoverable through the runtime:
documentation syntax, attached tests/examples, accepted request fields, and the
separate staging and persistence steps for replacements. Reject unsupported
Flow definition fields before editing state; do not silently ignore attempted
metadata. Keep help bounded and deterministic, and expose it through the small
agent interface. Evaluate whether it resolves observed authoring failures with
fresh agents; executable help examples alone are not evidence of an agent benefit.

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

Treat the vocabulary/discoverability benefit as an open hypothesis. Preserve
negative results and compare retained vocabulary against an identical reset-rich
library, not only a primitive-only environment. Equal results, harder discovery,
incorrect reuse, defect propagation and gate friction are legitimate outcomes.
Do not change frozen acceptance criteria after observing a trial or repair actor
outputs before scoring them. New reliability-focused studies need predeclared
defects, regression checks and outcome definitions; existing studies retain
their original design and evidence limitations.


The primary outcome is reliability of subsequent changes as accepted vocabulary accumulates: independent behavioral acceptance, preservation of unrelated behavior, issues caught before commit, and recovery from diagnosed failures. Count incorrect accepted edits, unresolved errors and regressions as well as successful reuse. Distinguish compile-time/type/effect rejection, library-gate rejection and independent acceptance failure; rejection of a valid change is friction, not a caught defect. Marginal cost remains a secondary outcome. Report cost and success together to avoid rewarding cheap failures.

A promising primary signal is more independently correct changes, fewer regressions or better issue detection/recovery under equivalent tasks and acceptance criteria. Include debugging and refactoring tasks with intentional defects and unrelated-behavior checks; reuse counts alone cannot establish reliability. A secondary efficiency signal is at least 20% fewer total input tokens or 20% fewer turns at comparable task success. Predefine what comparable means for the chosen sample and report uncertainty. Stronger evidence is reduced cost on later tasks without simpler tasks, or comparable success at substantially smaller context budgets.

Investigate contrary evidence: ignored vocabulary, duplicate growth, expensive discovery, repeated stack confusion, language errors dominating time, raw-source fallbacks, missing primitives, or a conventional baseline performing as well with lower cost. These are useful outcomes, not failures to hide.

The experiment must answer whether agents create reusable words, whether later agents find them, whether retention changes cost/latency/correctness, whether metadata helps recovery, whether smaller contexts remain viable, and whether vocabulary quality degrades.

## Late research: syntax and stack locality

Required research addition: evaluate **no language heap; program data stack
only**, distinct from the CPU stack. The data stack may be arena-backed.
Compare strict LIFO compound-value storage with processing-arena variants,
including cleanup, escaping outputs, retained state, memory growth and agent
comprehension. Originally a research alternative, the later user clarification
below makes owning values and physical payload locality the target native model;
the representation and performance still require validation. See
[the scope and acceptance criteria](STACK-ONLY-RESEARCH.md).

The user subsequently requested an early switch away from RPN after finding it difficult to read. Expression/dot source is implemented, with the default authoring cutover validated by the complete 27-check Release gate, retaining words and verified semantic IR; see [the migration plan](FRONTEND-MIGRATION.md). Remaining later research compares presentation/locality instead of postponing that switch. Preserve strong types, effects, inspection and library test/coverage gates.

The stack model has a potentially useful property independent of notation: it encourages a word to operate on recently produced values in a local flow instead of repeatedly reaching into distant state. Compare current RPN with a small alternative using named inputs, local bindings, or expression/pipeline notation that retains this property and lowers to the same semantic IR. Do not assume either RPN or conventional syntax wins.

The user's subsequent discussion favors strong encouragement of data flow rather than compulsory Forth-style source. The leading candidate for research is an expression-oriented frontend with explicit pipelines, named typed inputs, immutable nearby locals, and ordinary calls/branches when needed. Prefer guidance and inspectable diagnostics over hard locality restrictions. Implicit current-value blocks, rebinding, and first-class function composition require separate justification. [The discussion review](../reports/017-data-flow-syntax-review.md) records the recommendation, semantic questions, and experiment boundary; that report records the original proposal; current implemented syntax and remaining cutover acceptance are specified in [the frontend migration plan](FRONTEND-MIGRATION.md).

Use equivalent tasks, domain vocabulary, acceptance oracles, semantic behavior, and backend. Measure success, stack-order/type errors, error recovery, inspection/context cost, generated code, and tool interactions. Review whether source makes value flow easier to follow and whether the alternative causes more distant variable/state references. Separate frontend results from vocabulary-retention results.

Also investigate memory behavior under the later LLVM development/release backend design. LLVM would replace or supplement interpreted execution; F# may remain the host/compiler implementation. Allocation strategy, value representation, ownership/lifetimes, runtime services, and retained metadata determine memory usage. RPN, an operand stack, or LLVM alone does not establish a lower-memory system. Compare footprint, startup, allocation rate, and peak memory on equivalent workloads, changing one relevant factor at a time where practical. Any frontend change must pass shared semantic conformance and preserve source-mapped diagnostics and coverage.

## Delivery sequence

Priority clarification, 2026-10-06: validating external AI-agent behavior is the
next major milestone. Take the shortest path using the existing small domain
and a matched Flat/Growing/Conventional sequence. Do not wait for the full
40–60-word domain, 60-task execution suite, optional command completeness,
mailboxes, arenas or LLVM before the first comparative review. Finish bounded
work already underway and add capabilities only when trial requirements or
observed failures justify them. This changes delivery order, not the remaining
full-PRD requirements or the standard of evidence.

The first five-task sequence is now independently accepted in all three modes
([purpose review](../reports/057-early-agent-purpose-review.md)). Vocabulary reuse
and smaller new definitions are observed; an overall agent-cost benefit remains
unproven. Prioritize concise discovery/callable metadata and protocol recovery,
then repeat matched tasks with rotated order and standardized prompts before
expanding the domain or changing memory/backend architecture.

The bounded interface follow-ups are verified in reports
[058](../reports/058-compact-discovery.md) and
[059](../reports/059-repeat-comparison-preparation.md): compact discovery, exact
Flow references and conventional small patches. The versioned early-flow-002
repeat protocol preserves prior artifacts and uses archived prompts, serial
rotated trials and unchanged behavioral acceptance. These are preparation and
interface results. The rotated repeat is now complete in
[purpose review 064](../reports/064-repeat-agent-purpose-review.md): all fifteen
trials passed independent acceptance, with retained-word reuse and definition
compression. Growing used 70 protocol exchanges, Flat 75 and Conventional 67;
these do not establish model-token/turn savings. Prioritize selective context
and honest application retrieval accounting before expanding the domain or
memory/backend design. The [next bounded plan](SELECTIVE-CONTEXT-PLAN.md)
reuses existing compact context and preserves the frozen trial evidence.

1. Ship and validate the first usable slice and demo.
2. Run a fresh external subagent through the small protocol; record supplied context, interactions, independent results, and available usage.
3. Complete the user-directed expression/dot frontend migration and validate source/persistence/IR conformance before further controlled agent trials. Close-to-first-use is lint; retain explicit values/effects and no mutable language globals.
4. Freeze equivalent new-frontend fixtures and run a pilot against flat and conventional modes before optional language expansion.
5. Add capabilities justified by task requirements and pilot failures, then freeze fixtures and harness versions.
6. Run the controlled suite and publish outcomes, uncertainty, and limitations.

Future curator agents, synthesized context, maturity levels, contracts, and a second game/simulation domain remain hypotheses for later work. They must not delay the first measured agent task.

## Development and release backend target

Priority clarified 2026-10-07: first produce an evidence-backed efficacy report
on reliable edits, discovery and vocabulary reuse. Secondary is efficient LLVM
execution through development interpreter/JIT and release AOT, with a chosen
memory design (arenas are the main candidate). Do not require all optional
language features or a proven comparative reliability advantage before a small
native conformance and memory-design prototype. Safety and measured behavior
remain prerequisites for adopting a memory model or claiming performance.

Product direction clarified 2026-10-07: the eventual release runtime should be lean
and organized around typed mailboxes and bounded processing lifetimes. The current
F#/.NET interpreter validates language semantics and editing workflows; it is not
evidence for release memory usage or mailbox execution. Keep the near-term fresh
agent evaluation ahead of allocator/backend implementation. Specify single-thread
handler execution, queue/state capacity, checked message and response transfers,
effect/resource cleanup and arena escape rules before adopting the concrete
mailbox allocation model. Compare native footprint, peak/reserved memory and
startup on equivalent workloads separately from agent-edit reliability.


Memory intent clarified 2026-10-08: use an **owning value-based program-data
stack backed by arenas**, with rare physical copying or movement. Working
payloads normally remain where constructed throughout a processing lifetime.
Logical consumption or scope exit does not require immediate byte reclamation:
leave interior dead space until a safe suffix or processing-region reset.
Reclaim by rewinding the bump pointer when the mailbox no longer needs that
region's current data. Retain backing capacity for reuse according to pool policy.
Eager compaction on local removal is not the intended allocation policy.

Cleanup has two levels. At lexical scope exit, use a saved arena mark and
compiler-checked liveness to rewind a dead suffix without moving survivors.
An escaping result prevents rewinding through its bytes; retain the necessary
prefix, including dead temporary bytes beneath that result, until the enclosing
region or full arena can reset. Do not relocate the result to recover that space.
At request or
processing completion, reset to the arena's initial position or return its
backing to a pool after dependent results and pending I/O have been handled.
Scope exit is an opportunity for safe rewind, not an instruction to compact.
Rewind safety must be proved by the compiler and the pointer update injected at
the proved boundary. If analysis is uncertain, retain the allocations and allow
the stack to grow within its capacity until a later proved cleanup boundary.
Do not decide rewind safety through runtime liveness checks or root scans.
Runtime-sized offsets and normal bounds/capacity checks remain permitted; they
do not decide whether a value is still needed.

Ordinary binding, read-only local access, same-arena calls and returns should
transfer compiler-managed locations or ownership without copying full payloads.
Internal location descriptors are allowed; they do not introduce source-level
borrowed references, an independently allocated object graph, or reference
counting. Immutable value semantics and reset safety remain mandatory. A region
cannot reset while a surviving binding, result, pending I/O operation or retained
state depends on its bytes. Independent duplication and transfer across retained
lifetime boundaries may copy; account for these explicitly. Function boundaries
alone must not force payload relocation. No RPN syntax or manual popping is
required. Evaluate nested values, branch joins, lifetime escape, failure isolation
and bounded capacity through the authoritative semantic IR before rollout.

Physical locality is part of this requirement, informed by the user's Stasislang
experience: neighboring data-stack values must have nearby actual payloads, and
compound values must keep their nested data together. A contiguous table of
handles to separately scattered objects does not satisfy it. Prefer contiguous
storage within each owning stack region; document alignment, frame reservations,
chunk boundaries and any unavoidable gaps. Verify field offsets, owner extents,
copy independence and live payload address spans, then measure representative
traversal/transformation workloads. Locality is a layout requirement, not proof
of a speedup; byte packing and array-of-record versus field-wise layouts remain
measured implementation choices.

Keep this memory model mostly below the source language: use existing immutable
values, functions and locals, with compiler-derived stack positions and layouts.
Fixed-size offsets can be static; variable-size byte distances may use checked
runtime lengths/offsets without scattered language objects. Do not introduce
region/borrow annotations or storage-specific collection syntax simply to make
the allocator implementable. See the [simplicity review](OWNING-STACK-DESIGN-REVIEW.md)
for local cleanup, optimization and capacity-error boundaries.

The earlier native backend uses handles and shared record DAGs in invocation-wide
arenas. The additional owning-stack backend now implements independent inline
fixed-size records, variable-sized Strings/nested records and compiler-controlled
cleanup; see [report 129](../reports/129-variable-owning-values.md). Owning mailbox
integration remains in progress; conformance does not establish the complete
memory model or a footprint/throughput advantage. Its packed policy performs
substantial copying and does not satisfy the clarified rare-movement objective.
Replace that placement policy with stable arena payloads and bulk reset; retain
the measured packed implementation as historical evidence, not a required second
production policy. See [owning value-stack semantics](STACK-ONLY-RESEARCH.md#preferred-semantics-owning-values).

Compare keeping the same mailbox's actual stack/scratch associated across async
work until safe completion against returning it to a bounded pool at suspension
and reacquiring it on resume. Keep behavior, scheduling, memory ceiling and tail
latency criteria matched; measure throughput, live/reserved/process memory,
copying and allocation churn. See [the explicit async comparison](ASYNC-ARENA-EVALUATION.md#explicit-comparison-keep-or-return-the-mailbox-stack).

Optional mailbox memory candidate: declare a bounded typed retained-data layout
outside processing arenas, and allow arena-backed program data stack allocation
for transient values without an independent language object heap. Research
checked transfers, capacity failure, queued data, retained state and reload/
snapshot semantics; do not adopt this restriction or expose mutable globals
implicitly. See [candidate 4](STACK-ONLY-RESEARCH.md#candidates-to-compare).

Here, "static" means a declared, capacity-bounded persistent data layout, not
necessarily immutable data. A handler may update that state through explicit
typed access, but cannot retain references into its processing arena. The
program-data stack may itself occupy that arena. Queue entries and responses
must use separately bounded storage or checked copies before scratch reset.

Proposed allocation direction: scoped arenas with bulk reclamation for suitable phases, plus a separate lifetime strategy for retained data. Specify escape/promotion, nested value ownership, old executable generations, rollback/snapshot retention, capacity accounting and resource cleanup before native allocation. Arena reset is distinct from effect rollback and may retain backing capacity; measure allocation/cleanup time and used/reserved/peak memory. The current managed runtime does not implement arenas. See [the region contract](MEMORY-REGIONS.md) and [assessment](../reports/020-arena-allocation-assessment.md). Keep the early frontend migration ahead of general allocator implementation.

Evolve toward separate development and release runtimes as the secondary
execution target. Development retains the interpreter, REPL, introspection,
and hot word replacement, with LLVM JIT compilation for stable or frequently
executed words. Release uses LLVM AOT to emit optimized native code with a
minimal runtime and ships neither the interpreter nor compiler.

The typed semantic IR is the authoritative executable representation from the initial architecture. Source and AST are authoring representations; type/effect checking lowers them to this IR before execution. Interpreter, JIT, and AOT must share type identities, numeric/error behavior, control flow, effects, and value semantics. Native performance, memory use, and startup approaching Rust or C are later evaluation goals, not current guarantees.

LLVM remains outside the initial V1 interpreter milestone, but is the secondary
project target after efficacy assessment. The Runtime now executes the small
verified semantic IR through its interpreter, as recorded in report 010. The
source frontend migration preserves that boundary without adding LLVM.

## Final-stage research: sustained application migration

User-directed addition, 2026-10-07: after the language is stable and native
arena-backed program-data-stack behavior is implemented and independently
validated, undertake a long-running migration of a substantial existing
application. This is a late, potentially final evaluation stage; it must not
delay current agent-behavior experiments or expand V1 into a web framework.
Interpreter execution alone does not satisfy the memory/runtime prerequisite.

The initial candidate is [Basecamp's ONCE Campfire Rust implementation](https://github.com/basecamp/once-campfire-rust).
Its documented Rails compatibility and parity harness provide potential
external behavioral references. Pin repository revisions, inspect licensing,
and assess harness reuse and host dependencies before selecting the final
scope. The associated X discussion is motivation supplied by the user, not
verified evidence of migration quality or performance. See the
[migration research plan](../reports/088-late-application-migration-research.md).

The user supplied the original [DHH X conversation](https://x.com/dhh/status/2104633108092092880)
for historical context. Retain it and later review accessible replies and quote
posts; record attribution and access limits in the [discussion history](CAMPFIRE-DISCUSSION-HISTORY.md).
A public response is a future option once quality and competitive performance
are demonstrated with reproducible evidence and stated limitations.

Migrate bounded vertical slices incrementally using external AI subagents,
then exercise the migrated system over prolonged operation and subsequent
changes by fresh agents. Preserve the same explicit type/effect, dictionary,
library qualification and semantic IR boundaries. Record missing capabilities
and trusted-core growth; do not hide application logic in a broad host escape
hatch. Separate compatibility gaps and justified language additions from
behavioral regressions. Allow the study to conclude that the approach is
unsuitable for this application.

Predeclare independent compatibility and regression oracles, representative
workloads, fault scenarios, resource budgets and stopping criteria. Compare
the pinned reference and native candidate on equivalent environments. Measure
correct edits, regressions, discoverability, vocabulary reuse/pollution,
long-running memory stability, arena reclamation, queue/state capacity,
startup, latency and throughput. Report used, reserved and peak memory as
separate quantities, along with host-library allocations and runtime failures.
Retain raw artifacts and periodic reports, including failed migration attempts
and semantic mismatches. A successful initial port is not sufficient: later
maintenance and sustained execution are part of the acceptance evidence.

## Structural correctness update

The approved [structural correctness requirements](STRUCTURAL-CORRECTNESS.md)
add closed internal domain states, arithmetic operations with explicit rounding,
pure typed decisions with narrow effectful execution, construction invariants
including relationships between fields, and deterministic property tests with
independent expected values. These are implementation requirements, not claims
that every feature is already delivered. Preserve existing domain contracts
until explicitly migrated, and record backend support separately. Full coverage
and valid types remain necessary constraints rather than proof of business policy.
