# Validated lookup domain extension

Revision used: `db481e26d1bf5016aba6ff708d099b52a6d3e6ae`.

The separate Flow/2 extension adds `lookup.valid?` and the `ValidatedCustomerLookup` record. Its attached tests cover a missing result, a matching customer, and a mismatching customer. The mismatching generated constructor is expected to fail with `RECORD_VALIDATION_FAILED`; the test evidence confirms the validator's actual `false` return was recorded. The predicate was committed at library maturity, and the record's validator target remained bound to the same stable `WordId` after a fresh engine reload.

The focused run preserved the baseline inventory at 53 words, 31 types, 154 tests, and 44 examples per run. The separate populated-delivery extension remained 2 words, 1 type, 7 tests, and 3 examples. The new lookup extension adds 1 word, 1 type, 3 tests, and 2 examples. The full focused run passed 9 groups and 5,479 assertions.

Command:

```text
dotnet run --project tests/AgentLang.Business.Transitions.Tests/AgentLang.Business.Transitions.Tests.fsproj --configuration Debug -- --evidence .agentlang/record-validator-001/domain/focused-business-transitions.json
```

The command and machine-readable counts are in `focused-business-transitions.json`; full stdout is in `business-transitions-run-02.log`. The existing project-only `email.delivery-fold-step` finite-coverage rejection also appears in the log and its enclosing test group passes.

`git diff --check` and trailing-whitespace checks passed. `dotnet format ... --verify-no-changes` could not run because `dotnet format` does not support F# projects; no Fantomas executable was available in the local PATH.

Source SHA-256:

| File | SHA-256 |
| --- | --- |
| `tests/AgentLang.Business.Transitions.Tests/Program.fs` | `8DC1E478CD1A20D18D2E3C7E057BB4777E6967E967DCDD2B3F3DCFFD484F18EC` |
| `examples/business-validated-lookup.agent` | `374CD3C2B9E8EE6FA2DC6BD946CAFAE8B368ACDC295B9154781A4843BF28E6E8` |

This is focused local validation only; no full repository gate or commit was run.
