# Flow output vectors and destructuring

This document records the next opt-in Flow syntax slice. It is a design and acceptance contract; it does not wire Flow into Runtime, the protocol, or durable project storage.

## Compatibility and syntax

Extend `FlowWordDefinition` from one `Output` type to an ordered, nonempty `Outputs: LangType list`. Keep existing scalar declarations such as `-> Int` valid and render them the same way. Write multi-output signatures explicitly as `-> (Int, String)`. The parentheses mark a signature vector; they do not introduce tuple types or values. Empty output vectors are invalid. `Unit` remains an ordinary one-element output vector, so an effectful Unit-returning word still declares `-> Unit` and produces `unit` when a value is needed.

Add explicit vector binding and return forms:

```text
word split(value: Int) -> (Int, String) {
    effects none
    return (value, "label")
}

word use(value: Int) -> String {
    effects none
    let (number, label) = split(value)
    return label
}
```

`let (a, b) = expression` binds every output, in signature order. Its right side must produce exactly as many values as there are names. Each name must be a real, unique binding; `_` and partial destructuring are rejected. A one-output binding continues to use the existing `let name = expression` form.

`return (a, b)` is a terminal result statement for the current lexical word, branch, or match-case block. It yields that block's result vector; it is not an early or nonlocal function exit. Thus, in `let (a, b) = if condition { return (1, 2) } else { return (3, 4) }`, the branch return supplies the `if` value, the `let` binds it, and execution continues with the next statement in the enclosing block. The return members are ordinary scalar expressions evaluated left to right; the parentheses do not create an expression-level tuple. The existing final scalar expression remains valid for one-output code. A multi-output call never spreads implicitly: bind all outputs with `let (...)` and then return the desired scalar expressions explicitly. Return vectors are nonempty, and a return followed by another statement in the same lexical block is invalid.

Flow is still pre-durable, so this additive grammar remains syntax version 1. Before stage 3 persists the first Flow source revision, freeze the grammar and version-selection contract and test it as a durable source-format contract.

## Typing and lowering

Keep separate scalar and vector inference paths. Scalar contexts require exactly one output: ordinary `let`, call arguments, dot receivers, container payloads, and isolated expression compilation. They reject a multi-output call with a Flow arity diagnostic. A vector-aware resolver returns the complete, substituted output list after resolving the word from its name and input arguments; output count must not silently change name resolution or disambiguate overloads.

`let (names...) = expression` is a vector context. It accepts a call or dot call with an exact output count, and an `if` or `match` expression whose branches or cases explicitly return equal vectors. Branch and case vectors must match in both length and type at every position. Existing scalar branches and cases remain one-element vectors. Empty blocks do not synthesize Unit; `Unit` remains an explicit value.

A terminal return vector lowers its scalar expressions in written order and leaves those values on the existing stack. For a call with outputs `[out1; out2]`, Core places `out1` below `out2`; lowering the matching `let (first, second)` must therefore emit stores for `second` then `first`, while preserving the source-order name-to-type mapping. Every output must be bound. No Core or verified-IR stack changes are needed: `WordDefinition.Outputs`, `Expr.Call`, and the existing exact branch/match exit-stack checks already support ordered output lists.

Retain source provenance for each destructured name. Store an authored name span with every `LetMany` binding so each generated local store has a useful authored source site. Return members keep their expression spans. Synthetic lowering markers must still be projected before verified IR is created; no private marker coordinates may escape in diagnostics or source maps.

## Flow AST consumers

The implementation must update all exhaustive matches over Flow syntax:

- `FlowSyntax.fs`: render output lists, parenthesized destructuring, and terminal return vectors, including nested branch and case blocks.
- `FlowParser.fs`: parse scalar and vector signatures; parse binding and return vectors in both block readers; reject empty vectors, duplicate names, arity-incomplete patterns, and nonterminal returns; include every initializer and return member in `expressionsInStatements` and the iterative nesting traversal.
- `FlowLowering.fs`: retain the scalar arity wrapper, add vector-aware call/output inference, infer terminal block vectors, require exact branch/case vector equality, validate the complete word output list, and lower stores in reverse stack order.
- `FlowLint.fs`: visit every `LetMany` initializer before making its names visible, then register all bindings at that statement index; visit each return member at the return statement index. Lint remains advisory and does not rewrite the AST or source.
- Flow and Flow-lint tests: update any hand-built AST fixtures to cover the new variants and source spans.

## Acceptance coverage

The focused suite should establish that:

- Existing one-output parse, render, lowering, and execution behavior stays unchanged; vector rendering round-trips deterministically.
- A multi-output producer can be destructured, reordered by explicit return, verified, and executed with the exact declared output order. Generic output substitution is preserved.
- A terminal return in each arm of an `if` used as a `let (...)` initializer supplies that branch's vector value; a following enclosing statement still executes and can use the destructured locals.
- `if`, Option match, and Result match accept equal output vectors and reject differing arity, differing position types, or order mismatches.
- A wrong-count `let (...)`, a multi-output call in any scalar context, an empty output/return vector, duplicate destructuring names, a nonterminal return, partial `_` binding, and empty branch/case results fail closed.
- Effectful multi-output calls execute once and require every result to be bound. Unit remains a single output, with no empty-vector fallback.
- Nesting checks traverse every destructuring initializer and return member. Lint observes initializer-before-binding visibility and visits all vector members without changing source.
- Source-map entries for calls and generated local stores resolve to authored spans; no synthetic marker coordinate reaches a user-facing diagnostic or verified source map.

Runtime/protocol attachment, durable storage, source migration, and frontend cutover are outside this slice.

## Validation after the shared source freeze

Run the focused source suites, interpreter checks, and the repository validation gate:

```powershell
dotnet build AgentLang.sln -c Release
dotnet run --project tests/AgentLang.Flow.Tests/AgentLang.Flow.Tests.fsproj -c Release
dotnet run --project tests/AgentLang.Flow.Lint.Tests/AgentLang.Flow.Lint.Tests.fsproj -c Release
dotnet run --project tests/AgentLang.IR.Tests/AgentLang.IR.Tests.fsproj -c Release
dotnet run --project tests/AgentLang.IR.Interpreter.Tests/AgentLang.IR.Interpreter.Tests.fsproj -c Release
dotnet run --project tests/AgentLang.Acceptance/AgentLang.Acceptance.fsproj -c Release
pwsh -NoProfile -File scripts/Validate.ps1 -Configuration Release -ReportPath .agentlang/reports/flow-outputs-validation.json
```
