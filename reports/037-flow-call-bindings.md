# Flow authored call bindings

Status: source-backed batch lowering and authored-call reconciliation are implemented. The final focused Flow suite passes 564 assertions; root is rerunning the full 25-check Release gate after the additional semantic-proof negatives. This remains a compiler prerequisite, not durable publication.

## Implemented surface

`FlowLowering.compileWordWithCallBindings` provides a transient AST-to-verified-IR proof for one new word. `FlowLowering.compileBatchFlowSources` accepts a host-declared inventory of current Flow word sources plus source-backed additions/replacements. It validates strict UTF-8 references and base ownership metadata, re-lowers unchanged Flow bodies under the proposed signature catalog, rejects changed stable target identities or newly ambiguous calls, assembles all real bodies, performs one final complete-program compile, and returns bindings reconciled with verified call-like IR operations.

A call binding contains the owner name, stable ID and source revision, the exact `SourceRef`, and a structural AST path plus span, call form, requested name, stable target identity and current target revision. Paths distinguish identical spans in host-built ASTs. Fragment composition follows emitted execution order through call receivers/arguments, branch conditions, option/result scrutinees and their cases. Generated constructors/accessors are represented; the compiler-inserted scalar validator is not an authored call site.

The source inventory is complete only relative to `ExpectedFlowOwnerIds`, an explicit host assertion. The current compiler `Context` does not retain the frontend tag needed to independently discover every Flow-authored word, and this API does not establish manifest membership. Diagnostic source labels must match retained base span labels for exact IR span reconciliation. A Stack-authored word may transition to Flow under its existing stable ID without pretending its old Stack body is Flow.

## Focused acceptance added

The new Flow fixtures exercise calls with duplicate host AST spans and distinct structural paths; event order through nested conditions, option/result scrutinees and arms, argument lists, destructuring and multi-output returns; named dot receiver/argument/stage ordering; namespace and absolute-root resolution; callbacks with their exact stable IDs and spans; generated scalar/record construction and accessors; implicit validator exclusion; retained same-ID revision advancement; source inventory/reference failures; dot ambiguity and ordinary-call redirection to a different stable ID; root-key stability as suffix candidates grow; and Stack-to-Flow migration while re-verifying a retained Stack caller. They also mutate a base `Definition.Body` from `add` to type-correct `subtract` while retaining its valid source bytes, spans, and origin map, prove the altered dictionary still compiles, then expect `FLOW_SOURCE_BODY_MISMATCH`. Valid same-arity renamed parameter metadata and missing parameter metadata independently expect their dedicated source-proof errors.

The focused Flow suite initially passed 555 assertions; the added semantic-proof negatives then passed in a final 564-assertion run, exit code 0; [final evidence](evidence/038-focused-flow-final-success.json). Earlier fixture attempts were preserved rather than hidden:

- Initial compile rejected the reserved local `base` (`FS0010`): [failure](evidence/038-first-focused-flow-build-failure.json).
- The next compile exposed unqualified `FlowLowering` types and ambiguous change-record inference: [failure](evidence/038-second-focused-flow-build-failure.json).
- Execution then found that `answer()` was already ambiguous because the caller name also ended in `.answer`; the fixture owner was renamed: [failure](evidence/038-first-focused-flow-execution-failure.json).
- The next redirect fixture still produced ambiguity when both zero-argument suffix and exact candidates applied. It now changes the old target signature in the same batch, leaving only the new exact target applicable and exercising stable-ID rebinding: [failure](evidence/038-second-focused-flow-execution-failure.json).
- A root-call path assertion lacked a nested call inside the argument; the fixture now includes one: [failure](evidence/038-focused-flow-root-path-fixture-failure.json).
- A dot-argument selector found an earlier list callback; it now matches the exact child path under the selected dot call: [failure](evidence/038-focused-flow-dot-order-fixture-failure.json).

These were fixture defects, not changes to resolver behavior. The final suite verifies the intended diagnostics and site relationships, including semantic-body mismatch despite a type-correct altered base body and parameter metadata mismatch/missing cases.

## Earlier checkpoints

Before adding these acceptance cases, the new source-backed Core passed a fresh Release build with zero warnings/errors, and the existing Flow suite passed 473 assertions. These are compile and compatibility checkpoints, not proof of the new API's acceptance. Evidence: [Core build](evidence/038-source-backed-core-build-success.json) and [existing Flow regression](evidence/038-source-backed-existing-flow-pass.json).

A separate pinned probe passed 31 assertions for same-span calls, condition/branch ordering, Stack-to-Flow replacement with a retained Stack caller, and a bad source hash. It complements the focused suite but does not replace the broader fixture set: [probe evidence](evidence/038-stack-flow-migration-probe-pass.json).

## Limits

The batch source API covers word definitions only. Test/example source references and bindings, durable binding DTOs, manifest/source membership, publication, rollback, Runtime integration, and default frontend selection remain outside this slice. The `FlowCallBinding` records are compiler evidence, not persisted authority. Those boundaries and subsequent storage work are tracked in [the durable integration plan](../docs/FLOW-DURABLE-INTEGRATION.md).

The final focused Flow binding suite is complete at 564 assertions. The final full Release gate passed all 25 checks, including the 31-assertion independent probe, with zero build warnings/errors: [local evidence](evidence/038-flow-binding-final-validation.json). Committed CI/publication and earlier source-backed Core build attempts are tracked in [integration report 038](038-flow-binding-integration.md).
