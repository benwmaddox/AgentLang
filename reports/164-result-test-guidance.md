# Returned Result values in test guidance

Status: implemented and locally validated. This is an authoring-help improvement,
not a new agent efficacy result.

In [report 163](163-atomic-subscription-handoff.md), the language participant's
initial five-test batch had four incorrect expectations: it used `=> error CODE`
to match returned `Result.error` values. The function body stayed unchanged while
the participant repaired its tests. That is evidence of a discoverability problem
in test authoring, not evidence that the runtime's Result semantics were wrong.

The examples help now explicitly distinguishes returned errors, compared with
`=> value result.error<T, E>(...)`, from structured runtime failures matched by
`=> error CODE`. Flow/2 help includes a complete `tutorial.lookup` function with
negative-input, zero-boundary and positive-input tests. Its requests demonstrate
definition, test execution and library publication. The four existing help topics,
schema version and language semantics remain unchanged.

Runtime checks execute the source and requests returned by help. They verify the
negative result's typed structured payload, absence of runtime-error matching,
all three passing tests, and library maturity with complete supported finite
return coverage. The existing divide-by-zero example supplies the contrasting
structured runtime failure. The zero test checks the chosen `< 0` boundary.

Validation in the canonical checkout:

- `dotnet build tests/AgentLang.Flow.Runtime.Tests/AgentLang.Flow.Runtime.Tests.fsproj -c Release -m:1 -p:NuGetAudit=false`: exit 0, zero warnings/errors.
- `dotnet tests/AgentLang.Flow.Runtime.Tests/bin/Release/net9.0/AgentLang.Flow.Runtime.Tests.dll`: exit 0, 36 groups / 1,355 assertions.
- `git diff --check`: passed.

TEMP and TMP were set to the workspace-local validation directory. Package
auditing was disabled for this local build. Exact commands/results are retained
in [validation evidence](evidence/164-result-test-guidance/validation.txt).
This focused help change does not claim a new full 37-check validation run.

No fresh participant has used the new guidance yet, so reduced authoring errors
remain unproven. The next comparison should test maintenance of an existing
library signature and its callers. Repository investigation found that current
one-function replacement staging checks each proposed edit against unchanged
call shapes, preventing a coordinated arity change. Atomic staging of that final
set is the next engineering prerequisite; type checking, test qualification and
publication must remain all-or-nothing.
