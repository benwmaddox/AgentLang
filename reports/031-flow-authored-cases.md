# Flow-authored tests and examples

Status: implementation complete; focused Flow validation passed 354 assertions.

## Delivered

Flow now has standalone `test owner/case { ... => expectation }` and `example owner/case { ... => literal }` source objects. Test expectations are closed to literals, validated runtime error codes, or pure value expressions. Parsing stops the outer body at its expectation marker while nested match-arm arrows remain part of the expression. Renderers preserve the canonical dotted owner/case convention, and attachment records retain the exact authored source, syntax version, header/expectation spans, and authored expression spans.

Lowering validates the attachment shape and the existing Core test/example contract. Actual and expected expressions share one marker allocator, with origins merged into the retained Flow context. Compilation uses the exact caller-supplied verified program and the existing origin-aware Core test/example APIs. Tests and expected values become separate executable bodies; examples retain their literal expectation. This slice does not integrate Flow attachments into Runtime storage, manifests, or protocol commands.

The Flow suite adds regressions for round-trip source and spans; literal, runtime-error, expression, and example compilation; nested matches and destructuring; malformed and host-built definitions; expected-value output/type/effect failures; shared actual/expected structural limits; strict `Email` nominal expectations; and attachment markers allocated after sparse retained origins. Coverage tests filter against the target word's verified source owner and exact `(site, outcome)` obligations. They verify that an inline same-label branch earns no library credit, an actual call through the library earns its own branch credit, and an expected-expression call through the opposite branch runs in a separate trace. Unselected expected effects fail purity checking, while unselected actual effects remain in body metadata and preflight before execution.

## Validation

- `dotnet build src/AgentLang.Core/AgentLang.Core.fsproj -c Release` — passed under the root agent's serial build coordination, zero warnings and errors. The test source was subsequently expanded; this build validates the unchanged Core implementation, not the expanded Flow test file.
- `dotnet run --project tests/AgentLang.Flow.Tests -c Release` — passed, 354 assertions.
- Focused validation had three recorded failures before the pass: the first build found one example-expectation assertion type mismatch and two unannotated `ResizeArray` lookups; after those fixes, the next run failed the sparse-fixture precondition because its retained word had only one marker, and the following run found that the selected attachment expressions did not allocate synthetic markers. The earlier unused-origin-key issue was caught in review and repaired before these focused executions. The fixture was corrected to remap markers in retained word bodies and to use actual/expected `if` expressions that allocate their own markers. Root retained the raw failed outputs and repair history in `reports/032-flow-cases-integration.md`.
- Broader integration and full-gate validation are owned by the root agent; raw failure outputs and repairs are in `reports/032-flow-cases-integration.md`.

## Remaining boundary

The frontend exposes standalone Flow test/example APIs only. Runtime attachment dispatch, manifest/source-format association, durable authored-source storage, revision history, rename, reload, rollback, and protocol integration remain follow-on work described in `docs/FLOW-ATTACHMENTS.md`.
