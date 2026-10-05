# Flow-authored tests and examples

Status: implementation plan. This describes the next Flow syntax/compiler slice and its later Runtime integration. It does not claim that Flow tests or examples are currently parsed, compiled, or stored by Runtime.

## Current boundary

The Flow frontend currently parses and renders expressions and words. It has no Flow test/example AST, parser, renderer, or lowering API. Core `TestDefinition` and `ExampleDefinition` carry legacy `Expr list` bodies; tests support literal, stable runtime-error-code, and pure value-expression expectations, while examples support literal expectations only. Compiler origin-aware test/example functions exist, but they consume those legacy executable definitions. Runtime still parses source with `Parser.parse`, serializes cases through `Source.renderTest` / `Source.renderExample`, and compiles them without Flow source origins.

## Proposed source model

Add a closed `FlowTestExpectation` union:

```fsharp
type FlowTestExpectation =
    | Literal of Literal * SourceSpan
    | RuntimeError of string * SourceSpan
    | Expression of FlowExpression
```

Add `FlowTestDefinition` and `FlowExampleDefinition` records with a case `Name`, owning `Word`, `Body: FlowStatement list`, expectation, complete `SourceText`, full `Span`, and `SyntaxVersion`. Examples keep a literal expectation. Retain spans for the header, body statements, expectation marker and value, and the example literal so diagnostics and IR source maps point back to authored text.

Parse and render each test/example as a standalone source object with `FlowParser.parseTest`, `FlowParser.parseExample`, `FlowSource.renderTest`, and `FlowSource.renderExample`. A later project parser can combine these objects with words after dispatching each object using its owning revision's frontend metadata.

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

Add `FlowLowering.lowerTest` and `lowerExample`. Each returns the existing executable `TestDefinition` or `ExampleDefinition` together with the authored Flow source/version and its source projection. The executable body is derived from the Flow statements; the authored Flow source remains the source representation.

For a test with `Expression` expectation, lower the tested body and expected expression with one shared source-marker allocator. Their marker sets must be disjoint from each other and from the compiler context's retained markers. Calling `lowerExpression` twice with the same context is insufficient because both lowerings can choose the same next marker. Pass the combined origin map to `Compiler.compileIrTestWithExpectationAgainstProgramWithSourceOrigins`. Use `Compiler.compileIrExampleAgainstProgramWithSourceOrigins` for examples. These APIs already compile the expected expression separately and require it to be pure.

The tested body must produce one value for literal and expression assertions. An error assertion must execute a nonempty body. An expected expression must produce one value with the same closed type as the tested body and have no effects. Runtime currently runs a value expectation with an isolated trace, so its instructions, branches, and effects cannot satisfy the tested word's library coverage. Keep that separation when wiring Flow-origin-aware bodies. Coverage belongs to the library function's own authored IR sites reached by calls from the actual test body. The compiler emits detached test-body sites with `SiteOwner = None`; Runtime sets `CoverageTarget` to the tested word and records a hit only while that word is the active call. Results are then intersected with that word's coverage obligations. Inline `if`/`match` branches in test fixtures therefore cannot satisfy missing branches in the tested word, even when they produce the same outcome labels. Only execution of the target word's own branch sites counts.

`FlowSource.renderTest` and `renderExample` should be deterministic and parseable. Decide the durable byte policy in stage 3. If storage keeps exact authored formatting, the source text and all spans must refer to those stored bytes; do not also promise that the stored bytes equal canonical rendering. If storage canonicalizes before commit, rebuild or reparse the source and spans from the exact canonical bytes that are hashed and stored. In either policy, persist the Flow source object as authority and never persist generated RPN as a Flow test/example definition.

## Core acceptance

- Parse/render/reparse literal, runtime-error, and value-expression tests; literal-only examples; nested Flow expressions; qualified calls; and word documentation. Check case names, owner names, syntax version, source text policy, and spans.
- Reject missing or duplicate expectation markers, empty bodies where disallowed, malformed runtime error codes, extra content after expectations, multiple values, type mismatches, and effectful expected expressions. Reject error or expression expectations on examples.
- Lower actual and expected bodies with nonoverlapping source markers, including a context that already contains Flow words. Check that diagnostics map back to authored expectation/body spans and do not expose private markers.
- Execute each success/error assertion kind. Give a library word an uncovered Option/Result branch, then execute an inline fixture `match` that takes the same label; verify the library word remains undercovered. Separately call the library word with inputs that take both of its own branches and verify those calls satisfy its branch obligations. Confirm branches and effects in the expected expression do not count; an effect in an unselected branch of the actual test remains part of conservative effect validation.
- Check that Flow examples execute and retain their literal expected value and authored source through parse/render/lower operations.

Focused commands after the Core implementation:

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

No code, build, or runtime integration is claimed by this plan.
