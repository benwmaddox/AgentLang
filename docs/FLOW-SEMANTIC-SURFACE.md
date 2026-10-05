# Flow stage-2 semantic surface

Status: stage 2 remains partial. Static list callbacks and output vectors lower through existing verified IR operations. Shared iterative `FlowStructure` preflight limits expression and type nesting to 128 and expanded syntax nodes to 100,000 across each word or expression, including host-built ASTs; that budget includes every declared output type, return member, and destructuring binding. The vector slice carries ordered word outputs, vector-aware call inference, `let (...)`, and terminal lexical `return` values. Standalone Flow test/example source objects, parsing, rendering, lowering, and compilation against verified programs are implemented; the focused Flow suite passed with 354 assertions. Report 030 retains earlier callback/vector validation; report 031 records authored-case validation. Durable Flow storage and frontend cutover remain follow-on work.

## Current boundary

`FlowSyntax.fs` now models literals, locals, named calls, dot calls, `if`, typed container constructors, exhaustive Option/Result cases, static callback word references, scalar and destructuring `let`, terminal Return vectors, ordered nonempty word outputs, and standalone Flow test/example objects. `FlowParser.fs` parses scalar and parenthesized output signatures, test/example expectation forms, and bounds completed expression-tree depth, including postfix chains. Shared `FlowStructure` validation also checks host-built expressions, words, and attachments before public rendering or lowering, including combined actual/expected trees, Return members in nested lexical blocks, and source-name metadata. `FlowLowering.fs` lowers into the existing checked `Expr` model and verified IR, using collision-free private markers for sparse retained origins. Multi-output calls are allowed only in vector contexts; scalar positions require exactly one output (`FLOW_CALL_OUTPUT_ARITY`).

The backend already has the main target operations: `ConstructContainer`, `MapList`, `FilterList`, `EachList`, `MatchOption`, and `MatchResult`. `Compiler.inferBody` validates constructor payloads, callback arity and types, case-local scope, branch output/local joins, dependencies, and inferred effects. A `WordDefinition` and IR function already support multiple outputs. Flow test/example compilation uses the existing origin-aware Core APIs and validates against the exact supplied verified program; this adds no new runtime or generated helper words.

## Proposed source forms

Use `::` for qualified intrinsic names, as for ordinary qualified calls. Container constructors have explicit closed type arguments:

```text
list::empty<Customer>()
list::singleton<Customer>(customer)
option::none<Customer>()
option::some<Customer>(customer)
result::ok<Invoice, BillingError>(invoice)
result::error<Invoice, BillingError>(error)
```

Recognize these exact qualified constructor identities as Flow syntax before ordinary call resolution. Do not accept `list.empty<T>()` as an alternate spelling and do not fall back to an ordinary dictionary word if a constructor spelling is malformed. Require the existing constructor arity and payload checks; `none` and empty-list constructors still require type arguments.

Use exhaustive expression cases with explicit lexical blocks:

```text
match customer.lookup() {
    some customer => { customer.email() }
    none => { "missing" }
}

match invoice.create(customer) {
    ok invoice => { invoice.id() }
    error problem => { problem.message() }
}
```

The two cases must be present exactly once. Payload names are immutable and local to their own case. Both cases must return the same output-type vector and the same outer locals.

Static list callbacks are implemented as statically named, closure-free references:

```text
customers.map(customer::normalize)
customers.filter(customer::active?)
customers.each(email::send)
```

The exact forms parse a callback as a word reference, not a value expression. A qualified dictionary name is explicit; short names use `word name`. Ordinary value arguments such as `.map(localValue)` remain ordinary dot calls. Callback identity is resolved against dictionary entries before validating its signature, and generated constructor aliases do not participate. Lowering emits the existing static list operations. `map` requires one callback input and one output; `filter` requires one `Bool` output; `each` requires one `Unit` output. Missing, ambiguous, malformed, and multi-output callbacks are rejected. Callback dependencies and effects remain visible for empty lists, and the existing runtime preflights effects before invoking any provider.

Represent multiple outputs as a type vector, not a tuple value:

```text
word classify(customer: Customer) -> (Bool, Discount) {
    effects none
    ...
}

let (eligible, discount) = classify(customer)
```

Use `FlowWordDefinition.Outputs: LangType list`, a destructuring `LetMany` statement, and output-vector inference. A call used as a scalar argument, receiver, condition, constructor payload, ordinary `let`, or isolated expression must produce exactly one output. A destructuring binding must name exactly the outputs returned, in signature order. Branches and cases must agree on the full vector. Do not add a tuple runtime value or silently discard multi-output results; effect-only statement calls should declare and produce `Unit`.

Flow tests and examples are part of stage 2. Standalone source objects retain owner identity, case name, body, literal/runtime-error/value-expression expectation, exact authored source text, syntax version, and source spans. Tests reuse the existing Core expectation semantics: literal value, stable runtime error code, or independently evaluated pure value expression. Examples retain literal expectations only. Word documentation remains on the word, and attachment bodies use Flow expressions rather than embedded RPN.

## File-level implementation sequence

1. The syntax model contains typed constructors/cases, callbacks, output vectors, destructuring, test/example objects, and authored spans. A common single-output diagnostic rejects vector values in scalar positions.
2. The parser handles constructors, match cases, static callbacks, output vectors, destructuring patterns, tests, and examples under the source/token/depth limits. Invalid generic syntax, cases, expectations, and destructuring receive structured diagnostics.
3. `FlowSource` renders the supported forms deterministically, including constructors, callback names, output/case ordering, docs, tests, and examples.
4. `FlowLowering` infers output vectors and enforces scalar contexts. It maps constructors and cases to existing `Expr` operations, callbacks to `MapList`/`FilterList`/`EachList`, and destructuring to reverse-pop binding stores. Argument evaluation order and one-time dot receiver behavior are preserved.
5. Test/example lowering shares one marker allocator for actual and expected expressions, checks Core type/effect rules, and compiles against the exact verified program with origin-aware APIs. Expected expressions have separate executable bodies and cannot satisfy library coverage.
6. Reuse the compiler's existing type/effect/coverage semantics. Change `Compiler.fs` or `Core.fs` only if a focused parity test demonstrates an actual backend gap. Never generate new named helper words for containers, matches, callbacks, or destructuring.

Source-origin projections must give each synthetic scope, temporary, and load a distinct private marker mapped to the authored origin. Markers must never escape into displayed spans. Inspect the verified program's raw source map and coverage obligations; do not infer coverage from source substrings or generated instruction counts. Inferred effects from every possible case and from static callbacks must be included before capability checks, including for an empty runtime list.

## Focused acceptance cases

- **Containers and nominal types:** construct empty and singleton `List<Customer>`, `None`/`Some<Customer>`, and both `Result<Invoice, BillingError>` variants. Check runtime values and exact IR operations. Reject omitted/wrong type arguments, unknown nominal types, and a `String` payload for `Email` or another refined type.
- **Exhaustive cases:** execute Option `Some` and `None`, and Result `Ok` and `Error`; verify each payload is visible only in its own branch. Check exact missing-case, duplicate-case, wrong-scrutinee-type, payload-shadowing, outer-local, and branch-output diagnostics. Each construct contributes exactly two raw obligations: `some`/`none` or `ok`/`error`; each `if` contributes `true`/`false`.
- **Static list callbacks:** exercise map with empty and non-empty lists, filter with both predicate outcomes, and each with zero and multiple elements. Verify callback resolution/dependencies and all three callback signatures. A callback with a denied effect must be rejected before the effect provider is called even when the input list is empty. Reject inline closures and multi-output callbacks.
- **Multiple outputs:** verify a word declared `-> (A, B)` returns `[A; B]` in that order; destructure it into names in source order; use a two-output producer in both branches and reject unequal vectors. Reject too few/many binding names and multi-output use in scalar contexts. Confirm that no tuple value or generated helper word appears in the verified program.
- **Origins, effects, and coverage:** assert authored source spans for constructors, callback references, bindings, and every case body. Assert all synthetic instructions point back to authored spans and create no extra authored coverage. A library fixture remains failing until each `if` outcome, both Option/Result cases, and callback-owned branch outcome has a real test path. A pure expected expression can pass or fail its expectation but never satisfy tested-word coverage. Effects from unchosen branches and callbacks remain statically visible.
- **Tests/examples and parity:** round-trip tests and examples; compile and run value/error/expression expectations and examples through the same Flow frontend. Compare equivalent legacy compiler fixtures for values, structured diagnostic codes, effects, dependencies, resolved word identities, and raw branch obligations. Test rejected and ambiguous short names as well as qualified calls.

## Follow-on durable integration

Stage 2 produces and verifies Flow objects; it does not make Flow the default or claim durable source persistence. The next migration stage must store the exact Flow version, parameters, source spans/text, documentation, tests, examples, effects, maturity, stable IDs, revisions, and ownership as authoritative source. Dictionary exports and IR are derived data. Commit/library gates must run against the full proposed dictionary before persistence. Rename/replacement must preserve IDs and re-resolve retained source; reload must reproduce the same source objects and verified semantics; task abort and snapshots must restore the authoritative source and metadata. Keep historical stack revisions on the explicit legacy path.

Stage-2 validation after implementation:

```powershell
dotnet run --project tests/AgentLang.Flow.Tests -c Release
dotnet run --project tests/AgentLang.IR.Tests -c Release
dotnet run --project tests/AgentLang.IR.Interpreter.Tests -c Release
dotnet run --project tests/AgentLang.Acceptance -c Release
./scripts/Validate.ps1 -Configuration Release -ReportPath .agentlang/reports/flow-cutover-validation.json
```

The remaining plan is not implementation evidence. Use the scoped results and limitations in report 026; complete frontend migration is still required.
