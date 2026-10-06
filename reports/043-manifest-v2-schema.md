# Manifest v2 storage schema

Status: schema implementation and focused Storage acceptance are complete. The fresh Core build and full 25-check Release validation gate passed; independent source review found no material issue. Committed CI remains pending.

## Scope

This milestone adds manifest format 2 metadata to immutable word revisions while keeping the version-1 `CURRENT` pointer and named snapshot formats unchanged. It does not implement Flow source parsing, Runtime compilation, editing, or publication.

Each revision now carries a `SourceFormat` (`Stack` or `Flow`, syntax version 1) and a storage-neutral `CallBindings` list. The wire fields are `sourceFormat: { frontend, version }` and `callBindings`. A binding records the exact `SourceRef`, optional case name, body role (`definition`, `actual`, or `expectedExpression`), structural `FlowAstPath`, closed call form, requested name, and closed target kind/stable identity. Target revision, source span, and regenerated IR ordinal are not identity fields.

Binding uniqueness is scoped by `(SourceRef, CaseName, BodyRole, Path)`. Storage requires definition bindings to name the revision's word-definition source; actual bindings to name one of its test or example sources; expected-expression bindings to name one of its test sources. Stack revisions cannot contain Flow bindings. Storage validates these schema and membership rules but does not claim the binding list exactly covers parsed calls or verifies target resolution against IR.

## Compatibility and bounds

Version-1 manifests parse with Stack syntax version 1 and no bindings. Their writer omits the v2 fields only when those defaults hold, preserving existing v1 bytes and hashes. Explicit Storage v1 commits remain available for compatibility. New Runtime commits write manifest v2 with Stack/1 and empty bindings while retaining historical source references. The `CURRENT` pointer and named snapshots continue to use format 1; snapshots can reference a v2 manifest.

Manifest parsing rejects unsupported manifest versions before reading project-source references or revision-specific metadata. V2 requires both new revision fields and accepts only syntax version 1. Missing or wrong-shaped required JSON members use the existing `STORAGE_INVALID_JSON` classification; unknown frontends, unsupported syntax versions, malformed closed tags, incompatible body roles/source kinds, unowned source references, and duplicate binding keys return their structured frontend, version, or manifest errors.

Validation limits one manifest to 20,000 call bindings and 100,000 aggregate path segments. A path may have at most 128 segments; indexed segments must be nonnegative and no index may exceed 100,000. Call names and target identities have explicit text bounds. The existing 8 MiB maximum applies to the encoded manifest itself; the schema does not approximate this byte check from character counts.

Runtime rejects any manifest containing a Flow revision, including historical revisions, with `RUNTIME_UNSUPPORTED_FRONTEND` before parsing `dictionary.agent`. Storage can load and round-trip Flow metadata, but Flow Runtime loading remains unsupported until authored-source parsing and compiler binding verification are integrated.

## Validation evidence

The fresh Core build passed:

```text
dotnet build src/AgentLang.Core/AgentLang.Core.fsproj -c Release --no-incremental
exit code: 0
warnings: 0
errors: 0
elapsed: 16.77s
```

The exact output and source hashes are recorded in [the build evidence](evidence/042-first-core-build.json). The focused Storage suite passed 14 groups and 229 assertions. It verifies the frozen v1 hashes/defaults and writer refusal rules; v2 call-binding round trips and canonical order; v1 history migration and v1 snapshot restore after v2 publication; malformed metadata and limits; v2 Runtime writes; and the early Flow-load guard. See [the focused acceptance report](044-manifest-v2-acceptance.md) and [test evidence](evidence/042-ninth-focused-storage.json).

The complete Release validation gate passed all 25 checks with zero build warnings or errors; its results are recorded in [the validation evidence](evidence/042-durable-manifest-validation.json) and companion evidence files. Independent source review found no material issues. Eight earlier focused attempts exposed fixture compilation and expectation issues; the repair chronology is recorded in [report 042](042-durable-manifest-integration.md). Committed CI has not yet completed.
