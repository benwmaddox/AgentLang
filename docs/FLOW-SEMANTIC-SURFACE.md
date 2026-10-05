# Flow stage-2 semantic surface

Status: implementation plan only. This document records the remaining Flow frontend work for `FRONTEND-MIGRATION` stage 2; it does not claim that these forms are implemented or executable.

## Current boundary

`FlowSyntax.fs` currently models literals, locals, named calls, dot calls, `if`, single-name `let`, and one output type per word. `FlowParser.fs` parses that slice. `FlowLowering.fs` lowers it to the existing checked `Expr` model and verified IR. It deliberately rejects calls with more than one output (`FLOW_CALL_OUTPUT_ARITY`).

The backend already has the main target operations: `ConstructContainer`, `MapList`, `FilterList`, `EachList`, `MatchOption`, and `MatchResult`. `Compiler.inferBody` validates constructor payloads, callback arity and types, case-local scope, branch output/local joins, dependencies, and inferred effects. A `WordDefinition` and IR function already support multiple outputs. Stage 2 should extend source syntax and lowering to these contracts rather than add another runtime or synthesize helper words.

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

Keep list callbacks statically named and closure-free:

```text
customers.map(customer::normalize)
customers.filter(customer::active?)
customers.each(email::send)
```

Parse the callback as a word reference, not as a value expression. Resolve it against the current dictionary and lower directly to the existing static list operation. `map` requires one callback input and one output; `filter` requires one `Bool` output; `each` requires one `Unit` output. Reject callback expressions, missing or ambiguous targets, and multi-output callbacks.

Represent multiple outputs as a type vector, not a tuple value:

```text
word classify(customer: Customer) -> (Bool, Discount) {
    effects none
    ...
}

let (eligible, discount) = classify(customer)
```

Use `FlowWordDefinition.Outputs: LangType list`, a destructuring `LetMany` statement, and output-vector inference. A call used as a scalar argument, receiver, condition, constructor payload, or ordinary `let` must produce exactly one output. A destructuring binding must name exactly the outputs returned, in signature order. Branches and cases must agree on the full vector. Do not add a tuple runtime value or silently discard multi-output results; effect-only statement calls should declare and produce `Unit`.

Flow tests and examples are also part of stage 2. Add source objects with word identity, name, body, expectation/example result, source text, and spans. Reuse the existing `TestExpectation` meanings: literal value, runtime error code, or an independently evaluated expected expression. Keep documentation on the word as it is now. Test and example grammar must be explicit and round-trippable; their bodies should use Flow expressions, not embedded RPN strings.

## File-level implementation sequence

1. Extend `FlowSyntax.fs` with the constructor, case, callback-reference, destructuring, and test/example nodes. Keep every user-authored expression and pattern span. Make one helper produce a structured single-output diagnostic wherever an expression position cannot accept a vector.
2. Extend `FlowParser.fs` for the constructor spellings, case blocks, static callback references, output vectors, destructuring patterns, tests, and examples. Preserve the existing source/token/depth bounds. Malformed generic arguments, missing/duplicate cases, incomplete matches, and bad destructuring must fail with structured source spans.
3. Extend `FlowSource` rendering and round-trip tests for every new form. Canonical rendering must preserve qualified constructor and callback names, explicit type arguments, case ordering, output-vector ordering, documentation, tests, and examples.
4. Extend `FlowLowering.fs` inference to carry output vectors and enforce single-output contexts. Map Flow constructors and cases directly to the corresponding existing `Expr` operations. Map static callbacks to `MapList`/`FilterList`/`EachList`. Lower destructuring by binding the stack outputs in reverse pop order so source names still correspond to signature order. Preserve written argument evaluation order and the current one-time dot receiver rule.
5. Compile tests and examples against the exact verified program snapshot. Add origin-aware compiler APIs: current `compileIrTestWithExpectationAgainstProgram` and `compileIrExampleAgainstProgram` fingerprint with `Map.empty` and build bodies through `bodyFromInference context Map.empty`. Flow artifacts need APIs that validate the exact combined source-origin map and compile actual, expected, and example bodies with their own spans. Keep the pure expected body separately traced and exclude its sites from the tested word's coverage credit.
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

No implementation or validation result is implied by this plan document.
