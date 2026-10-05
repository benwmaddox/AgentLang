# Report 011 — Business fixture contract

**Status:** standalone contract milestone implemented and focused validation passed. Runtime integration and cross-language parity remain pending.

## Purpose

Add a versioned JSON-neutral typed contract between the existing immutable F# Small Business Backend reference and future fixture/observation code. The contract remains independent of AgentLang Core so it can be reviewed and validated without making runtime-parity claims.

## Scope and decisions

- Version 1 requires all entity and collection fields. Strict document parsing rejects unknown, missing, duplicate, or incorrectly typed fields.
- GUID input accepts the reference's `Guid.TryParse` variants; canonical output is lower-case `D` format. Explicit-offset instants normalize to UTC.
- Money is signed Int64 minor units only; quantities are bounded positive Int32. Invoice line multiplication and summation are checked exactly.
- Customer `kind` and Subscription `term` remain raw strings; the contract introduces no premium, discount, eligibility, or renewal behavior.
- Provider outcomes are inert fixture data. They do not represent an invocation or comparison of external providers.
- Store projection requires a caller-supplied identity ledger and checks it against public `Store.summary` counts. It does not claim to enumerate private Store maps or reconstruct all historical entities.
- Sparse customer fragments are a separate API with deterministic defaults and per-field provenance. Unsupported `status`/`active` fields are rejected.

## Files

- `experiments/AgentLang.Business.Contracts/`: standalone F# contract library referencing only `AgentLang.Business`.
- `tests/AgentLang.Business.Contracts.Tests/`: executable focused contract checks.
- `docs/BUSINESS-FIXTURE-CONTRACT.md`: schema, normalization, projection limits, and policy scope.

## Validation

`dotnet run --project tests/AgentLang.Business.Contracts.Tests -c Release` passed **7 groups and 69 assertions**. The run built the Contracts project and its Business reference from source and reported no compiler warnings or errors. The assertions cover strict required/unknown/duplicate fields and JSON types; GUID normalization; email policy including null and the 254-character boundary; exact integer money, Int64 overflow, Int32 quantity bounds, and checked invoice totals; equivalent offsets and UTC range handling; full typed document round-trip and provider detail preservation; immutable Store projection with ledger count/ID checks; and deterministic fragment expansion/provenance with unsupported-field rejection.

## Limits

This report does not claim the AgentLang fixture is equivalent, that all private domain state is restorable, that provider calls match, or that the planned 60-task benchmark is executable. Those require separate integration and acceptance evidence.


## Integrated validation and publication

The independent full Release gate passed all 18 checks, including this suite, with zero build warnings/errors. [Report 010](010-runtime-ir-cutover.md) records the shared working-tree evidence and selected IR parity result. This code and report are published with the Runtime cutover; exact committed CI evidence follows publication.
