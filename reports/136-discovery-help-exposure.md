# 136 — Expose existing discovery in default help

Status: implemented; focused local validation passes. Starting revision:
`1908bdb`. This is an interface change motivated by report 135, not a new
agent-efficacy result.

The six-agent shared-summary study found that both first-round language agents
requested the full dictionary listing. The runtime already supported a compact
names-only inventory, but the frozen primer did not surface that option. The
comparison also found a behaviorally correct submission that failed to complete
the requested shared-aggregation refactor. These observations motivate clearer
discovery guidance and separate structural acceptance; they do not establish
that either change will improve agent outcomes.

The implementation exposes compact inventory, targeted search and bounded
context requests in the existing default authoring help. It keeps the full
`words` default response, help topics and schema unchanged. Discovery requests
are independent of source syntax and must execute under both default and
Flow/2 help. The authoring guide shows the same requests. No new language
construct, dependency service or model tool is needed.

The next held-out experiment must separate three questions:

- Did the agent discover and reuse an existing relevant abstraction?
- Did the final behavior satisfy independent acceptance cases?
- Did every required entry point actually delegate the requested computation,
  without retaining the duplicate implementation?

Existing `transitive-dependencies` queries can witness helper reachability,
including static callbacks. Reachability alone is insufficient: a redundant or
unreachable helper call can coexist with duplicated logic. Acceptance must also
review the active source/IR paths and retain behavioral tests. Any helper
signature restriction must be explicit in the participant task; report 135's
undisclosed signature requirement must not recur.

Historical prompts, trials and scores remain frozen. The next trial should use
a different task with the revised guidance and declared structural criteria,
not repair or rescore the six published submissions. No claim of better agent
reliability, lower token usage or native performance follows from this help
change alone.

## Validation

Fresh Release builds and local execution pass:

| Suite | Result |
| --- | --- |
| Flow runtime | 30 groups, 1,064 assertions |
| Language acceptance | 35 groups, 632 assertions |
| Discovery | 106 assertions |

The new tests dispatch the actual help-provided JSON requests for both syntax
versions. They check compact inventory shape, a real search result and bounded
context containing its requested root. Existing acceptance tests preserve the
default full inventory contract. Discovery tests exercise dependency closure,
static callbacks and context limits. `git diff --check` passes.

Initial nonserial builds exited before compilation without a useful final error.
Serial builds succeeded; workload-locator diagnostics in verbose logs did not
establish a missing-SDK root cause. The first fresh test execution then failed
with `STORAGE_ACCESS_DENIED` while publishing a test definition in the sandbox
temporary directory. Using a writable workspace temporary directory resolved
that failure. These are validation-environment failures, not passing test runs.

For each listed project, the successful command was:

```powershell
$env:TEMP = 'D:\code\AgentLang\.agentlang\discovery-help-136\temp'
$env:TMP = $env:TEMP
dotnet run --configuration Release --project tests/<project> -p:NuGetAudit=false -p:BuildInParallel=false -m:1
```

The directory must exist first. Projects are `AgentLang.Flow.Runtime.Tests`,
`AgentLang.Acceptance` and `AgentLang.Discovery.Tests`. Package vulnerability
auditing was disabled for these local checks; CI was not used. This focused
validation is not a new full Release or LLVM regression run. Commands, outputs
and executed-source hashes are in the
[validation record](evidence/136-discovery-help/validation.json).
