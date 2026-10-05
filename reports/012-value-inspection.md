# Report 012 — Structured runtime value inspection

**Status:** standalone observation groundwork implemented; focused Release validation passed. Runtime and protocol integration remain pending.

## Purpose

Provide a bounded, deterministic JSON view of already-produced public `Value` values using one compiler-authorized verified IR snapshot as the source of nominal schema. This gives adapters and first-class tests a typed observation format without making execution or value construction part of the inspection API.

## Scope and decisions

- Output is an explicit versioned JSON DTO; F# discriminated unions are never serialized directly.
- Primitive and container tags are closed. Lists, options, results, records, and scalar wrappers retain their type and case information.
- Record fields follow declared field order. Snapshot-local nominal keys distinguish same-shaped scalar types.
- Int64 values use invariant decimal strings. Finite Float values use round-trip text plus an explicit negative-zero flag.
- The API requires the exact compiler-authorized primitive registry and a verified program handle. A model-only verifier result is rejected.
- Host-created malformed values are rejected with structured diagnostics. Declared list/option/result metadata is depth-checked before structural equality; null top-level value collections and null list payloads are rejected.
- Value/type depth, value nodes, JSON nodes/depth, nominal indexing, and estimated UTF-8 output are bounded. The output budget starts with a 256-byte reserve for the embedding envelope and returns no partial JSON.
- The inspector checks scalar payload shape against the declared base type. It does not execute refinement validators or prove that a public `Value` originated from the supplied snapshot; callers must pair the value with its exact active snapshot.

## Files

- `src/AgentLang.Core/ValueInspection.fs`: pure bounded DTO construction and compact JSON convenience API.
- `tests/AgentLang.ValueInspection.Tests/`: focused executable checks.
- `docs/VALUE-INSPECTION.md`: JSON schema, adapters, limits, and provenance caveat.

## Validation

- `dotnet build src/AgentLang.Core/AgentLang.Core.fsproj -c Release` succeeded with 0 warnings and 0 errors.
- `dotnet run --project tests/AgentLang.ValueInspection.Tests/AgentLang.ValueInspection.Tests.fsproj -c Release` passed **5 groups and 44 assertions**.

The tests cover nested typed containers, both Option and Result arms, nominal record/scalar metadata, declaration-order fields, Int64 boundaries, Float round trips including negative zero, malformed shapes/types, untrusted verifier-only handles, deep container metadata, null host values, deterministic limits, and deterministic JSON output.

## Limits

No Runtime or Protocol payload has been switched to this format. No refinement predicate or scalar validator is run, and the result is not a provenance proof. Byte accounting is a conservative bounded estimate with an envelope reserve; adapters remain responsible for lossless Int64/Float normalization.


## Integrated validation and publication

The independent full Release gate passed all 18 checks, including this suite, with zero build warnings/errors. [Report 010](010-runtime-ir-cutover.md) records the shared working-tree evidence and selected IR parity result. This code and report are published with the Runtime cutover; exact committed CI evidence follows publication.

## Committed CI evidence

Implementation/report revision `f1836d1f3a097b6bfa9dcde41d047a0a5da00187` passed [CI run 37306481981](https://github.com/benwmaddox/AgentLang/actions/runs/37306481981). The downloaded report identifies that exact revision, `dirty: false`, all 18 checks passing, and 99 passing fresh-process persistence checks. Saved evidence: [clean committed validation](evidence/010-committed-ci-validation.json) and [clean persistence projection](evidence/010-committed-ci-projection.json). This publication update accompanies the implementation when merged into main. The pinned 314-check AST-versus-IR comparison remains the separately recorded working-tree run; CI does not repeat that historical-binary comparison.