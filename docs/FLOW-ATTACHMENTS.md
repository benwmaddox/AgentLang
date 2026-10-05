# Flow-authored tests and examples

Status: Flow-native test/example parsing, rendering, validation, lowering, and compilation are implemented in Core. Durable Runtime storage and protocol integration remain a later stage.

## Current boundary

The Flow frontend parses and renders expressions, words, tests, and examples. Standalone Flow attachments lower to the existing Core `TestDefinition` and `ExampleDefinition` executable forms, then compile against an exact verified program with source-origin-aware compiler APIs. Runtime still loads and stores legacy attachments through `Parser.parse`, `Source.renderTest`, and `Source.renderExample`; this slice does not change Runtime persistence or its protocol.

## Flow source model

`FlowTestExpectation` is a closed union:

```fsharp
type FlowTestExpectation =
    | Literal of Literal * SourceSpan
    | RuntimeError of string * SourceSpan
    | Expression of FlowExpression
```

`FlowTestDefinition` and `FlowExampleDefinition` carry a case `CaseName`, owning `Word`, `Body: FlowStatement list`, expectation, exact authored `SourceText`, full `Span`, and `SyntaxVersion`. Both also retain header and expectation spans; cases and literal values retain their own source spans. Examples keep a literal expectation. Shared Flow structural validation applies the same nesting/node limits to the body and value-expression expectation.

Parse and render each test/example as a standalone source object with `FlowParser.parseTest`, `FlowParser.parseExample`, `FlowSource.renderTest`, and `FlowSource.renderExample`. The owner is metadata for attachment association; lowering deliberately does not require the owner to exist in the supplied word dictionary. A later project parser can combine these objects with words after dispatching each object using its owning revision's frontend metadata.

Use the existing test/example header naming convention: the owner name is the canonical internal word name with dot separators, followed by `/` and the case name. Keep `::` for qualified word calls and references in expressions; parsing maps those names to the same internal dotted form.

```flow
test customer.renew/basic-success {
    customer::renew(customer::example-eligible())
    => true
}

test customer.renew/value-expression {
    customer::balance(customer::example-premium())
    => value money::from-cents(9000)
}

test customer.renew/denied {
    customer::renew(customer::example-ineligible())
    => error RENEWAL_NOT_ALLOWED
}

example invoice.total/basic {
    invoice::total(invoice::example-small())
    => 42.50
}
```

The test body ends before `=>`. `=> literal` produces a literal assertion; `=> error CODE` produces an expected runtime error; and `=> value expression` is parsed as a separate pure expression. Reuse `TestExpectation.isValidRuntimeErrorCode` for error names. Examples accept only `=> literal`. Preserve the current word `doc "..."` field and make word documentation round-trip with Flow word source; this proposal adds no separate documentation declaration.

## Lowering and execution

`FlowLowering.lowerTest` and `lowerExample` return the existing executable `TestDefinition` or `ExampleDefinition` together with authored Flow source/version and a source projection. The executable body is derived from the Flow statements; the authored Flow source remains the source representation. Lowering applies Core test/example validation, including closed expected-value type equality, single-value constraints, runtime-error code validation, and expected-expression purity.

For a test with `Expression` expectation, lower the tested body and expected expression with one shared source-marker allocator. Their marker sets are disjoint from each other and from the compiler context's retained markers. Calling `lowerExpression` twice with the same context is insufficient because both lowerings can choose the same next marker. `FlowLowering.compileTest` passes the combined origin map to `Compiler.compileIrTestWithExpectationAgainstProgramWithSourceOrigins`; `compileExample` uses `Compiler.compileIrExampleAgainstProgramWithSourceOrigins`. The compiler produces separate executable bodies for actual and expected expressions.

The tested body must produce one value for literal and expression assertions. An error assertion must execute a nonempty body. An expected expression must produce one value with the same closed type as the tested body and have no effects. Runtime currently runs a value expectation with an isolated trace, so its instructions, branches, and effects cannot satisfy the tested word's library coverage. Keep that separation when wiring Flow-origin-aware bodies. Coverage belongs to the library function's own authored IR sites reached by calls from the actual test body. The compiler emits detached test-body sites with `SiteOwner = None`; Runtime sets `CoverageTarget` to the tested word and records a hit only while that word is the active call. Results are then intersected with that word's coverage obligations. Inline `if`/`match` branches in test fixtures therefore cannot satisfy missing branches in the tested word, even when they produce the same outcome labels. Only execution of the target word's own branch sites counts.

`FlowSource.renderTest` and `renderExample` should be deterministic and parseable. Decide the durable byte policy in stage 3. If storage keeps exact authored formatting, the source text and all spans must refer to those stored bytes; do not also promise that the stored bytes equal canonical rendering. If storage canonicalizes before commit, rebuild or reparse the source and spans from the exact canonical bytes that are hashed and stored. In either policy, persist the Flow source object as authority and never persist generated RPN as a Flow test/example definition.

## Core acceptance status

- Focused Flow regressions cover parse/render/reparse of literal, runtime-error, and value-expression tests; literal-only examples; nested Flow expressions; qualified calls; and word documentation. They check owner/case names, syntax version, exact source text, and spans.
- Parser, renderer, and lowerer regressions reject missing or duplicate expectation markers, empty bodies where disallowed, malformed runtime error codes, malformed owner/case names, extra content after expectations, multiple values, type mismatches, and effectful expected expressions. Examples reject error or expression expectations.
- Attachment actual and expected bodies share one lowering state. Regressions check sparse retained marker IDs, disjoint actual/expected source markers, authored source projection, and diagnostics without private marker coordinates.
- Execution regressions cover value, error, and example cases. Coverage uses the tested library's own source sites: an inline fixture branch cannot satisfy the library's obligation, calls through the library can cover both paths, and an expected-expression call through the library is traced separately. An effect in an unselected actual branch remains in preflight metadata, and an unselected expected-expression effect still fails purity validation.
- Refined `Email` expectations succeed when both actual and expected sides retain the nominal type; an underlying `String` literal or expression does not match `Email`.

Focused validation command (result is recorded in `reports/031-flow-authored-cases.md`):

```powershell
dotnet run --project tests/AgentLang.Flow.Tests -c Release
dotnet run --project tests/AgentLang.IR.Tests -c Release
dotnet run --project tests/AgentLang.IR.Interpreter.Tests -c Release
```

## Runtime and durable-storage acceptance

This is a separate stage-3 integration. The manifest already attaches test/example source references to word revisions, so parse each attached object using the `sourceFormat` of its owning revision. Validate that the source's owner matches the manifest revision, lower all attached cases against the complete verified program, and retain authored Flow cases in Runtime state. Update source projection, revision history, rename/replace, task rollback, snapshots, and fresh-engine load so callers always see Flow source instead of translated RPN. Preserve v1 stack source behavior and hashes.

Acceptance must prove both in-memory execution and fresh-process persistence: commit a Flow word with an attached test and example; compare stored source bytes/hashes and revision references; restart Runtime; verify `source`, `describe`, `tests`, and `examples`; run library gates; rename a referenced word and update affected test/example bodies safely; then verify abort and snapshot restore return to the exact prior authority. Failed compilation or gates must leave the prior manifest pointer authoritative.

After Runtime integration, run:

```powershell
dotnet run --project tests/AgentLang.Acceptance -c Release
dotnet run --project tests/AgentLang.Storage.Tests -c Release
./scripts/Validate.ps1 -Configuration Release -ReportPath .agentlang/reports/flow-attachments-validation.json
```

This document's implemented scope is the standalone Core/Flow attachment frontend; it does not claim durable Flow storage, Runtime attachment dispatch, or protocol migration.
