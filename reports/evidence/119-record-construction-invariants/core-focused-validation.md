# Record validator Core validation

Validation ran in the canonical `prototype` checkout with Debug configuration only.

| Command | Result |
| --- | --- |
| `dotnet build src/AgentLang.Core/AgentLang.Core.fsproj -c Debug` | Passed, 0 warnings, 0 errors. |
| `dotnet run -c Debug --project tests/AgentLang.IR.Tests/AgentLang.IR.Tests.fsproj` | Passed, 191 assertions after the zero-field refinement guard correction and validator-only snapshot invalidation test. |
| `dotnet run -c Debug --project tests/AgentLang.IR.Interpreter.Tests/AgentLang.IR.Interpreter.Tests.fsproj` | Passed, 93 assertions. |
| `dotnet run -c Debug --project tests/AgentLang.IR.Formatting.Tests/AgentLang.IR.Formatting.Tests.fsproj` | Passed, 44 assertions. |
| `dotnet run -c Debug --project tests/AgentLang.Flow.Tests/AgentLang.Flow.Tests.fsproj` | Passed, 1,110 assertions. |
| `dotnet run -c Debug --project tests/AgentLang.Source.Tests/AgentLang.Source.Tests.fsproj` | Passed, 101 assertions. |

The focused tests cover record validator source parsing and rendering, exact `Record -> Bool` and purity checks, interpreter validation before exposing a record, `RECORD_VALIDATION_FAILED` call-site diagnostics, direct and indirect constructor cycles, tampered type-table/MakeRecord metadata, record validator IR formatting, and finite-coverage rejection when a validated record has finite projections without a proven valid-value domain. The additional zero-field test ensures a validated empty record (whose unrefined domain is a closed singleton) is unsupported rather than treated as open; a validated `Range` with only `Int` fields remains supported with no finite obligations. A compiler snapshot test confirms that changing only a record's validator target invalidates the snapshot when both targets have the same exact pure signature and record fields are unchanged.

The `FiniteCoverage.fs` correction was rechecked with the Core Debug build and `AgentLang.IR.Tests`; the latter was rerun after adding the validator-only snapshot test and passed with 191 assertions. This file records the exact commands and results without replacing the existing `build-01`/`build-02` evidence directories or logs.
