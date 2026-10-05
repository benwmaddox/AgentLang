# Early migration to explicit data-flow source

Status: migration in progress, 2026-10-05. The user requested an early switch away from RPN after finding it difficult to read. Dot chaining is the preferred authoring notation. This supersedes the earlier plan to defer frontend changes until after initial agent experiments. The opt-in Flow parser and verified-IR lowering now support named typed inputs, immutable locals, ordinary/named/dot calls and conditional expressions. Runtime and protocol authoring still default to RPN; the complete semantic surface, durable Flow source and default cutover remain required. See report 021 for validated foundation evidence.

## Architecture and target syntax

Keep words as inspectable operations with stable identities, types, effects, documentation, tests, dependencies and history. Keep the authoritative verified semantic IR and its interpreter. Add an expression authoring AST and lowering pass, not another interpreter. F# remains the host/compiler implementation; LLVM and memory-layout research remain later work.

Use named typed parameters, immutable lexical locals, ordinary calls, expression-valued branches, and dot chains. Encourage nearby producers and consumers without forcing every computation into a linear chain. No OOP, inheritance, dynamic dispatch, implicit current-value blocks, or .NET method invocation is introduced.

Keep the useful stack discipline prominent through explicit consumed/produced types, visible value flow and minimal hidden state. Do not introduce mutable language globals. Immutable constants may be represented by ordinary typed pure definitions with discoverable dependencies. Pass application state explicitly as typed values when practical; stateful external services remain host providers accessed only through declared and authorized effects. Dictionary metadata, task logs and capability configuration are host state, not unrestricted program globals. Stack execution alone does not enforce this boundary.

Preserve the original memory motivation too: clear short-lived working data and reasonable bulk-cleanup boundaries. Named locals/dot chains still lower to analyzable typed value operations. They must not introduce uncontrolled retained roots or make allocation lifetime depend on punctuation. Future region analysis considers all uses/escapes, not just operand-stack pops; see [the arena contract](MEMORY-REGIONS.md).

```text
word customer.discounted-balance(customer: Customer) -> Float {
    effects none
    doc "Applies the existing premium discount policy."
    let balance = customer.balance();
    if customer.premium?() {
        balance.multiply(0.9)
    } else {
        balance
    }
}
```

This example preserves the existing toy Float policy, not an exact-money business policy. Balance and eligibility can reuse the same named input without stack manipulation.

## Calls and resolution

- Ordinary calls name dictionary words. Use `::` for expression namespace qualification, for example `customer::balance(customer)`; displayed dictionary identities remain `customer.balance`. Unique unqualified names such as `add(10, 20)` are also possible. Namespace qualification and receiver chaining must not be syntactically ambiguous.
- `value.operation(args)` is a statically resolved first-input call. Evaluate the receiver once, then explicit argument expressions in written order; invoke the resolved word with the receiver as input one.
- If exposed, `value |> operation(args)` is exactly equivalent: same resolution, evaluation order, types, effects and lowered calls. Dot is the default presentation. Neither spelling introduces closures or implicit partial application.
- Resolve short stages from first-input type, stage name and explicit argument types using strict type compatibility. Zero or multiple candidates yield deterministic diagnostics. Do not select by dictionary order, docs, runtime value shape or expected output. A qualified ordinary call is the author's explicit way to resolve ambiguity; the parser never falls back from a receiver chain into a namespace call.
- Introspection exposes the resolved stable word identity. Dictionary growth/rename must not silently rebind existing dot calls: validate authored source resolution in the complete proposed dictionary before publication. An alias collision that makes retained source ambiguous requires qualification at affected call sites before commit.
- Parameter names are public authored-word metadata. Record constructors use declared field names. Trusted primitives require an audited explicit parameter catalog wherever named arguments are supported. Reject duplicate, missing, unknown and conflicting arguments.
- Named arguments evaluate in source order even when parameter binding needs reordering. Lower through typed temporary locals; never reorder observable effects to match declaration order.
- Nominal/refined constructors and unwrapping remain explicit checked operations. Do not infer constructors through arbitrary dot chains or coerce base types implicitly.

Chains can change carried type. Result/Option do not unwrap automatically: use explicit cases or separately specified map/bind operations. Branches/cases must agree on typed outputs. Multi-output words use explicit destructuring at a binding/return boundary; do not add implicit tuple spreading or a tuple runtime representation simply for source convenience. Prefer records for named results. Stack-gymnastics words remain internal/legacy rather than idiomatic new source.

Require one output for a carried chain value; use an explicit multi-output binding when needed. Ordinary effect-only statement calls should expose Unit as their source contract. Reject local reassignment in the flow frontend rather than relying on the existing stack lowerer's permissive local map. Branch-local bindings do not escape their lexical branch. Synthetic reorder temporaries and loads must not manufacture extra user-facing coverage obligations.

## Binding locality

Definitions must precede use in lexical scope. Close-to-first-use is lint, as confirmed by the user: prefer immutable locals initialized nearby, with source-mapped feedback for unnecessarily distant bindings and unused locals. Keep this guidance distinct from semantic validity and test/coverage gates; it is not a hard maximum line/statement distance in the first frontend.

Locality analysis should follow dependencies and lexical blocks, not physical line count. Branch sharing, deliberate eager validation/resource acquisition, and reuse of expensive results are legitimate reasons for earlier definitions. Moving an effectful initialization can change behavior; never automatically move, duplicate or remove it. Any suggested pure move must preserve dependency order and value meaning. A future strict style profile may make selected warnings fatal only after false positives and exceptions are evaluated.

This retains useful pressure toward recent data without forcing stack positions, recomputation, or artificial blocks. Short source lifetimes do not establish lower runtime memory; allocation/liveness is a separate backend measurement.

## Source, persistence and compatibility

Preserve authored source, spans, parameter names, syntax version and coverage obligations. `source`, history, diff, rename, tests and examples must show the actual frontend rather than unexplained generated RPN.

Use explicit frontend versions: legacy stack source and new flow source have distinct parser entry points. Do not choose a frontend by parsing and falling back after errors. Switch default protocol authoring/eval only after the full flow acceptance gate passes; explicit legacy mode remains for historical projects and backend fixtures.

Keep historical revision objects/hashes unchanged. Audit manifest/object/source versions and deterministic fixtures before persistence changes. New objects carry syntax and parameter metadata; exports may use explicitly versioned sections/documents, with an exact encoding frozen and tested before publication. Reload must reconstruct identical authoring objects and resolved semantics.

Preserve word IDs, ownership, dependencies, maturity, revisions, frozen validators, rollback and snapshots. Rewritten source/coverage locations require relevant tests to rerun. Historical pilot artifacts remain RPN evidence; do not rewrite them as if new-frontend agent trials occurred.

## Delivery plan

1. **Contract and typed authoring model:** Core/Parser/Compiler/Source implement versioned flow AST, named inputs, calls, locals, dot resolution and lowering. Start scalar, record/refined and conditional programs. Check complete types/effects before executing. Continue interpreter value-safety repairs independently.
2. **Complete semantic surface:** follow the [stage-2 implementation map](FLOW-SEMANTIC-SURFACE.md) for closed containers, static callbacks, Option/Result cases, test expectations/examples and multi-output bindings. Preserve all branch coverage/errors. Adapt the in-progress structured expectations and eval work instead of discarding it.
3. **Durable integration:** Runtime/Storage/Source preserve versions/parameters, canonical source, reload/history/diff, rename/replacement, library gates, frozen validators, snapshots and abort. Audit related format versions/fixtures. Keep explicit legacy loading.
4. **Default cutover and trial reset:** CLI/Protocol/README/examples/primers/fixtures adopt flow/dot after conformance. Freeze equivalent snapshots and run external subagent pilots. Retain the conventional baseline and update matching language fixtures. No new trials on an intermediate mixed frontend.

Use canonical checkout and bounded non-overlapping ownership; freeze shared Core for builds. LLVM, widespread infix operators, rebinding, closures, user generics and implicit current-value blocks are outside this migration.

## Acceptance and validation

- Ordinary/dot calls agree: `add(10, 20)` returns 30 and has the same resolved IR call as its dot equivalent. Type errors prevent all effects.
- Named arguments/chains preserve nominal distinctions, constructor validation, written effect order and single receiver evaluation. Ambiguities fail deterministically; qualified calls work; vocabulary growth cannot silently change meaning.
- Conditionals/containers/cases retain verified semantics and source coverage. Pure expected expressions do not provide tested-word coverage. Multi-output bindings and malformed/incomplete interactive input have focused positive/negative checks.
- Define/test/library commit/reload/replace/rename/snapshot/abort preserve authored syntax, parameters and identities. Historical stack revisions load through explicit legacy mode.
- Fresh full Release validation passes, including source/storage/runtime/IR suites and persistence projection. Add cross-frontend semantic conformance over equivalent fixtures; build success alone is not parity. After source freeze run `./scripts/Validate.ps1 -Configuration Release -ReportPath .agentlang/reports/flow-cutover-validation.json`.
- Update executable examples, compact primers, PRD and reports with code. Publish together and distinguish implementation checks from actual agent evidence.

Make the new frontend default before further controlled vocabulary experiments. Later research can still compare dot/pipe/RPN presentations and native memory, but it no longer delays the requested authoring improvement.

## Exact root identity addressing

The current opt-in name grammar cannot explicitly select an unqualified authored dictionary key when another namespaced word has the same suffix: short lookup becomes ambiguous, while qualified source requires namespace segments. This fails closed, but leaves a naming gap. Before durable/default cutover, provide and test an explicit exact-root identity spelling or a complete namespace migration that preserves existing stable IDs and historical source. Do not resolve the gap through expected output, dictionary order, or silent rebinding. Include ordinary calls and static callbacks in the conformance fixtures.
