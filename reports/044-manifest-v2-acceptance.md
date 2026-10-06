# Manifest v2 Storage acceptance

This acceptance covers the Storage DTO and persistence boundary for manifest v2. Storage records each word revision's source frontend/version and ordered authored call bindings. A binding keeps the source reference, optional test/example case name, body role, structural `FlowAstPath`, call form, requested name, and stable target kind/ID. It does not persist a target revision, compiler ordinal, source span, or runtime IR value.

The Storage tests preserve all six literal v1 golden hashes and the original fixture bytes. They exercise v1 parsing as Stack/1 with no bindings, refusal to write meaningful v2 metadata into a v1 manifest, and v2 typed commit/load round trips across definition, actual, expected-expression, and example bindings. They compare hashes for forward and reversed binding input to verify canonical serialization, inspect serialized targets for the absence of revision fields, and confirm that CURRENT and named-snapshot envelopes remain version 1 while referencing a v2 manifest.

The migration fixture appends a Flow/1 revision to a Stack/1 history entry under the same stable word ID. It checks that the complete v1 revision record, source references, source bytes, and history remain available after the manifest moves to v2. A named v1 snapshot is then restored after a v2 publication, verifying that it selects the original v1 manifest and remains readable.

Malformed raw manifests cover unsupported schema versions before missing schema fields, missing source-format/call-binding metadata, unknown frontends, body roles, path segments, call forms, or targets, missing/foreign source references, duplicate site keys, negative and oversized indexes, excessive path depth, binding count, and aggregate path-segment count. Typed-writer checks verify v1 and v2 refusal paths leave authority and generation unchanged, including the independent rule that Stack revisions cannot carry Flow bindings, incompatible source kinds/roles, omitted case names, and oversized authored metadata. Runtime coverage confirms new Stack publications use v2 with Stack/1 and empty bindings, and that a mixed history with a Flow revision and Stack current revision raises `RUNTIME_UNSUPPORTED_FRONTEND` before parsing either the deliberately invalid project aggregate or the Flow definition as Stack. Storage itself remains a parser/serializer boundary; it does not claim semantic Flow or IR validation.

The focused Storage command is:

```powershell
dotnet run --project tests/AgentLang.Storage.Tests/AgentLang.Storage.Tests.fsproj --configuration Release
```

The fresh Core build passed with zero warnings and errors in 16.77 seconds (`reports/evidence/042-first-core-build.json`). The focused Storage command passed all 14 groups and 229 assertions (`reports/evidence/042-ninth-focused-storage.json`). Eight earlier focused attempts exposed fixture syntax and expectation issues; those were corrected without changing or removing acceptance assertions. Root's report 042 retains the attempt evidence.

The full Release gate passed all 25 checks, including `dotnet build AgentLang.sln --configuration Release`, the Storage, Flow, IR, Runtime, CLI, and source acceptance suites, source-binding probes, fresh-process persistence projection, fixture validators, parser limits, and diff checks. The full command/output ledger is in `reports/evidence/042-durable-manifest-validation.json`. The v1 golden fixture files were not changed.
