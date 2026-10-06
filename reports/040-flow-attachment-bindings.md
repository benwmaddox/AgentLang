# Flow attachment source bindings

Status: Source implementation, focused Flow acceptance, independent review, and the local Release validation gate passed. Commit and committed-CI publication remain pending.

## Scope

This milestone extends source-backed Flow compilation from words to attached tests and examples. It adds `compileBatchFlowProjectSources`, which accepts the existing exact Flow word inventory/change set plus an independent host-declared attachment inventory and explicit attachment changes. The existing word-only APIs remain available.

An attachment key is `(owner WordId, test-or-example, case name)`. The host supplies an expected key-to-`SourceRef` map and the exact source documents. Inventory validation requires one document per declared key, matching reference, object kind, strict UTF-8 content hash, parsed owner/case, stable owner identity, and base revision. Add requires an absent key. Replace and Remove require a prior `SourceRef` compare-and-swap match. Only an explicit Replace changes retained source. A retained source is returned with its owner revision advanced when that owner changes.

Actual test bodies, expression expectations, and example bodies compile as detached verified bodies against the one final word program. Their transient call bindings include attachment key, body role, structural AST path, authored span/form/name, stable target identity, and target revision. Detached reconciliation requires `SiteOwner=None` and checks call count/order, source kind/span, operation kind, name, identity, and revision. Expected-expression effects and types remain subject to the compiler's test checks. Examples and tests are compiled here; this API does not execute them or claim their behavior has passed.

Unchanged attachments are compiled once against the immutable base program and again against the final word program. The implementation compares body-role/path sets and stable target IDs, permits a same-ID target revision advance, and rejects rebinds or ambiguities. A single marker-allocation seed advances across attachments, while each compiler body receives only final word-context origins plus its own attachment origins.

## Trust boundary and limits

The attachment inventory's expected map is a host assertion. The compiler proves exact supplied-document agreement with that map and the provided `Context`; it cannot authenticate the durable manifest or prove the host's expected map is complete. Base Flow cases must belong to an owner in the exact host-declared Flow word inventory. Candidate Flow cases must belong to a base Flow owner or a word explicitly added/replaced as Flow in the batch. Stack-owned cases remain the host's responsibility, including old cases across a Stack-to-Flow transition; the transition can add new Flow cases. Source-file labels come from the host and are used for diagnostics. The result is a pure compiler proposal: persistence, manifest versioning, test execution, library coverage gates, rollback, Runtime integration, and default-frontend cutover remain out of scope.

`compileTestWithCallBindings` also exposes transient host-AST reconciliation for actual and expected bodies, including same-span fixtures. It proves AST-to-IR correspondence only and does not authenticate source bytes or membership.

Source-backed attachment errors retain their original code, span, and expected/actual detail while gaining the owning word in `Diagnostic.Word` and a message label for attachment owner ID, kind, and case. When an error span maps to an authored call, the label also reports body role and structural path. This applies to immutable-base and final-candidate test/example lowering and reconciliation.

## Acceptance evidence

The final source checkpoint passed a fresh Core build:

```text
dotnet build src/AgentLang.Core/AgentLang.Core.fsproj -c Release --no-incremental
exit code: 0
warnings: 0
errors: 0
elapsed: 16.33s
```

The saved build and source hash are in [the fresh Core build evidence](evidence/039-attachment-diagnostics-fresh-build.json). The final focused run passed **705 Flow assertions** with `dotnet run --project tests/AgentLang.Flow.Tests -c Release`; the output is in [the focused Flow evidence](evidence/039-ninth-focused-flow.json). A separate source-backed attachment smoke passed **19 checks**, covering retained test/example sources and stable-identity revision advancement; see [the smoke evidence](evidence/039-diagnostics-client-smoke.json).

The complete local Release validation gate passed all **25 checks**, including the solution build, application acceptance suites, persistence projection, fixture and subagent-host checks, parser limits, and whitespace validation. Its full command outputs are preserved in [the gate evidence](evidence/039-flow-attachment-validation.json). The independent source review found no material issues.

Source commit `d0782c1` also passed exact clean committed CI with all 25 checks. Saved remote evidence and the reports-only publication are recorded in [report 039](039-flow-attachment-integration.md). The preceding word-only milestone is recorded in [report 038](038-flow-binding-integration.md).
