# 118 — Populated delivery contracts

The focused Business Transitions runner passed 8 groups and 5,382 assertions.
The full local Debug gate passed all 37 checks, and the separate LLVM suite
passed 444 assertions. This is an implementation checkpoint, not
an agent-efficacy or native-performance result.

## Delivered behavior

The interpreter now exposes `list.tail : List<a> -> List<a>`. It returns a
typed empty list for an empty or singleton input and otherwise returns the
positional suffix. The element type, order, duplicates and immutable input are
preserved. Focused tests cover empty and singleton lists, multiple values,
duplicate nominal values, non-list rejection and Flow/2 discovery metadata.
The LLVM test keeps `List<T>` outside the supported native slice and expects an
explicit diagnostic; this change adds no native List support.

The separately loaded Flow/2 extension in
[`business-populated-delivery.agent`](../examples/business-populated-delivery.agent)
adds two functions and one record:

- `email.populated-delivery-plan` returns `Option<PopulatedEmailDeliveryPlan>`.
  The plan exists only for a nonempty outbox and requires a concrete
  `EmailMessage` in its `first` field; `remaining` is the typed FIFO suffix.
- `email.apply-delivery-result-populated` derives its plan from the supplied
  Store. An empty outbox takes precedence over the supplied provider result. A
  provider failure preserves its raw detail and leaves the input unchanged. A
  success transfers exactly the FIFO head to sent emails while preserving
  duplicate positions and unrelated Store fields.

The extension models provider outcomes as explicit pure values; it does not
call an external mail provider. Existing Flow/1 business definitions and the
baseline inventory remain unchanged: 53 authored words, 31 types, 154 attached
tests and 44 examples. The extension adds 2 words, 1 type, 7 tests and 3
examples. Its focused reload checks total 15.

The focused runner checks each word's attached tests and immediately queries
coverage for that test batch. It requires current coverage, no uncovered
instructions or branch outcomes, and complete finite coverage with no missing
or unsupported cases. This timing matters because later test runs replace the
runtime's latest-batch coverage evidence. The extension is committed as library
vocabulary; a fresh Engine then checks word identities, exact source and type
source, attached test/example names, maturity and coverage, and reruns all seven
tests and three examples.

## Independent comparison and limits

The test harness builds expected queue states with the F# business domain and
uses `Domain.EmailOutbox.deliverNext` for the expected transition. Successful
results are compared by full canonical Store projection; error codes are
compared with the domain oracle. The F# `DomainError` renderer prefixes provider
failures while Flow preserves the provider's raw message, so the test checks
that language-side message against an explicit expected value instead of
comparing unlike renderings. A temporary Flow/2 probe binds one Store value,
passes that same binding to the transition and returns it for projection. The
probe is discarded before library commit and checked absent both before commit
and after a fresh Engine reload.

This demonstrates a narrow presence guarantee for a populated FIFO plan. It
does not provide general cross-field record validators or protect every raw
record constructor boundary. Provider-state assertions, external I/O and
cancellation behavior remain separate work. The extension uses a hand-written
test runner; it is not evidence that an AI agent would discover or prefer this
design. No conventional comparator, reliability advantage, vocabulary-retention
effect or native execution result is claimed.

## Validation and evidence

Focused command:

```powershell
dotnet run --project tests/AgentLang.Business.Transitions.Tests/AgentLang.Business.Transitions.Tests.fsproj --configuration Debug -- --evidence .agentlang/populated-delivery-001/business-transitions-evidence.json
```

Recorded result: 8 groups and 5,382 assertions passed, including the preserved
baseline and populated-delivery extension inventories above. The test ran on a
dirty tree based at `c825e3e3ec01a7e47674fcea6af854ff0ab30d5b`; the saved
`tested-source.json` records exact source hashes. Focused evidence and review
notes are retained in `.agentlang/populated-delivery-001/` and published under [`evidence/118-populated-delivery-contract`](evidence/118-populated-delivery-contract/).

The separate LLVM regression command, `dotnet run --no-build --configuration Debug --project tests/AgentLang.Llvm.Tests`, passed all 444 assertions against the fresh Debug build. Its output is saved as `llvm-debug.log` in the evidence directory. It includes explicit rejection of native `List<Int>` for `list.tail`.

The full command was:

```powershell
./scripts/Validate.ps1 -Configuration Debug -ReportPath .agentlang/populated-delivery-001/full-validation.json
```

It exited 0: 37/37 checks passed, fresh build with zero warnings/errors. The
business-policy regression passed 98 checks across 30 independent outcomes.
The full gate took 21 minutes 52 seconds; 15 minutes 54 seconds were in the
existing business-policy preflight. These are validation durations, not function
execution benchmarks. CI remains manual-only.

[Evidence index](evidence/118-populated-delivery-contract/index.json) identifies
source hashes, focused and full logs, review notes, and failed development runs.
The auxiliary process reports are preserved in `full-validation-auxiliary.zip`;
runtime binaries are excluded. The frozen report-108 Release runtime remained
unchanged. There is no syntax, IR operation, storage schema, or native ABI change.

The next construction slice is outlined in the [record-validator plan](evidence/118-populated-delivery-contract/next-record-validator-plan.md): a pure predicate over a complete record, enforced through verified construction and reload. It is a plan, not delivered functionality. The trusted native allocator boundary must remain explicit.
