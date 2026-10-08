# Conventional broker preflight

Use the existing V2 host with the conventional CLI and
`--validation-project tests/AgentLang.Business.Tests.fsproj`. The assigned project
root contains `business/` and `tests/`; the test project references only the copied
library. Actor edits must preserve nominal types and project wiring, and remain
within the assigned copy.

The fresh Release broker build is under
`.agentlang/r09-discovery-001/conventional-broker-artifacts/`. Its local build
completed with zero warnings/errors; the command and output are retained under
`conventional-broker-build/`.

The first actual V2 `validate` probe failed because vulnerability auditing could
not reach NuGet and the copied project treats warnings as errors. It was not a
source/test failure. A second probe with `$env:NuGetAudit = 'false'` passed the
existing eight groups and 134 assertions. Both trace/output captures remain in
`conventional-broker-probe/`. The host itself returned zero for both sessions;
acceptance must inspect the nested validation exit code and result.

Set that environment property in the participant's exact launch command, before
starting the broker. This intentionally excludes package vulnerability auditing
from local validation; it does not disable compiler warnings or language tests.
Do not modify the runtime or weaken source checks to work around network access.
Revalidate the final helper-containing F# start through the same broker before
the main comparison freeze. The successful probe used the earlier duplicated
scaffold, not the final retained-helper start.
