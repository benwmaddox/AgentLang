# Milestone 001: first usable prototype

Date: 2026-10-04

## Delivered behavior

The F#/.NET prototype supports typed scalar execution, nominal records, validated nominal scalar types, local bindings, checked conditionals, vocabulary discovery, a multiline REPL, and a JSON-lines protocol. Definitions remain staged until a test-gated commit. Project words require passing attached tests; library words additionally require complete instruction coverage and both conditional outcomes. Task abort restores dictionary state, including interim commits. Unpromoted task words are removed on successful task completion.

`Email` is distinct from String and validates construction. `MetersPerSecond` and `KilometersPerHour` remain distinct even though both wrap Float. No implicit conversion or arbitrary .NET invocation exists.

## Validation and observed results

- `dotnet build AgentLang.sln`: zero warnings and errors.
- `dotnet run --project tests/AgentLang.Acceptance`: 16 groups, 154 assertions passed.
- `git diff --check`: clean.
- Independent Customer demo: four language tests passed, both customer words committed as library vocabulary, and a fresh process calculated 90 from a premium balance of 100.
- Independent negative probes: invalid Email construction returned REFINEMENT_FAILED; Email passed to a String operation and incompatible speed units returned TYPE_STACK_MISMATCH.
- Independent library probe: a true-only conditional test blocked commit with uncovered source locations; adding the false case allowed commit.

Acceptance groups exercise numeric boundaries, capability denial, record/local/branch behavior, commit tests, dependency persistence, temporary isolation, scoped replacement and discard, scoped test metadata, nominal refinements, invalid/frozen validators, type-only commits, library coverage, rollback, and task cleanup. These checks validate the named contracts; they do not prove all language behavior or the research hypothesis.

## Feedback

The inspect/define/test/commit/reload path is usable. Strong semantic types distinguish domain meaning without hiding the underlying representation. Source-mapped coverage provides actionable feedback when reusable words lack branch tests.

Most defects found during independent review involved persistence and replacement boundaries rather than arithmetic: temporary definitions leaking into storage, lost library policy after reload, changed validator semantics, and unrelated staged tests being saved. Regression checks now cover those cases. Future features must preserve these boundaries.

The next missing language capability is typed collections and explicit success/absence/error handling. The next experimental capability is a model harness with real usage accounting. Neither vocabulary reuse savings nor smaller-context performance has been measured.

## Limits and remaining work

The interpreter executes a checked expression tree rather than a separate IR. List, Option, and Result are not yet usable language values. File, clock, and console providers are virtual; no real filesystem confinement or network/database/process provider is implemented. Prior revision contents are retained only in the current process. Stable IDs, renames, full event logs, snapshots, structural search filters, context generation, vocabulary metrics, the complete business fixture, the 60-task suite, conventional baseline, provider harness, and controlled results remain open.

The full requirement ledger is [docs/REQUIREMENTS.md](../docs/REQUIREMENTS.md). This milestone is a baseline, not completion of the PRD.
