# Flow attachment acceptance

Status: the comprehensive Flow fixtures are implemented and pass the focused Flow suite: 705 assertions, exit code 0. A fresh nonincremental Core Release build passed with zero warnings/errors, and the independent source-backed smoke passed 19 checks. Root owns the complete local Release gate, which is still running; no full-gate result is claimed here.

## Scope and fixture coverage

The suite exercises `compileBatchFlowProjectSources` with one exact Flow word inventory, explicit word changes, a host-declared attachment key/reference inventory, and independent attachment changes. The fixture includes a branch-bearing Flow word, a generated `Email` scalar constructor, and attached source-backed tests and examples.

It covers literal, runtime-error, expression-expected, and example execution; no-call cases; actual versus expected-expression role keys, including identical host-AST spans and structural paths; callback and branch call ordering; generated scalar constructor bindings without an authored binding for the implicit validator; detached `SiteOwner=None` reconciliation; and actual, expected, and inline-detached branch traces proving only library-owned sites count toward library coverage.

Source proof cases cover an incomplete base Flow word inventory during an attachment-only update, altered base source bytes, incomplete/extra/duplicate attachment documents, wrong expected references, malformed owner/case/kind/revision/hash/file/content/UTF-16 metadata, and expected-expression type/effect checks. CAS cases cover Add, Replace, Remove, stale prior references, duplicate intents, and missing or existing keys. Retained cases prove same-owner revision carry with unchanged source reference/content, re-resolve actual and expected calls at a new target revision, reject true actual and expected stable-ID redirects independently, and preserve both candidate names and owner/case/body-role/path details for actual, expected-expression, and example ambiguity diagnostics. A valid source attachment on a Stack-owned word is rejected until a same-batch Stack-to-Flow replacement proves the owner.

These are compiler proposal checks. They do not add storage v2, Runtime integration, durable attachment membership, execution gates for publication, or a default-frontend cutover. The host-supplied expected map remains the attachment membership boundary.

## Validation

Focused command:

```powershell
dotnet run --project tests/AgentLang.Flow.Tests -c Release
```

The successful focused command was:

```powershell
dotnet run --project tests/AgentLang.Flow.Tests -c Release
```

It printed `Flow tests passed: 705 assertions`; [saved focused evidence](evidence/039-ninth-focused-flow.json) identifies the dirty `prototype` checkout at `469d2f5`. The latest [fresh Core build](evidence/039-attachment-final-core-build-2.json) passed with zero warnings/errors, and the independent [19-check source-backed smoke](evidence/039-source-attachment-smoke-pass.json) passed. The eight earlier focused failures and repairs are recorded in [integration report 039](039-flow-attachment-integration.md). The complete local Release gate remains root-owned; it reruns the established 25-check sequence from [report 038 evidence](evidence/038-flow-binding-final-validation.json), including a fresh solution build, Flow and sibling acceptance projects, the independent Flow binding probe, fresh-process persistence checks, parser limits, and whitespace checks. That earlier gate is historical evidence and does not validate the new fixtures.
