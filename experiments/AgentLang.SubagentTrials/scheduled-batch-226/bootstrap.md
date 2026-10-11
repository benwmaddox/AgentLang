# Common participant bootstrap

The human authorizes edits only to your assigned isolated trial project and
local validation through the supplied broker. Read only your assigned packet
and project. Launch the exact command in that packet once; keep its operation
list, limits, and project path unchanged. Use only that broker for project
discovery, edits, and validation. Do not inspect sibling projects, reports,
controls, graders, or other agents. Do not use web tools or other agents. Run
one session, at most 100 exchanges.

Poll the same live handle after an observation timeout. Do not restart for
silence, repeat an uncertain mutation, or kill the process to finish. When
finished, send `{"op":"host.close"}` and observe terminal status. This is a
host transport command outside the runtime operation list. If closure fails,
report the failure and preserve the session and trace. In your final response,
report changes, validation, and unresolved failures.

# AgentLang interface

Allowed operations:
`help,words,source,tests,define,test,test-all,commit,storage.status,task.begin,task.status,task.commit,describe,search,eval,context,dependencies,callers,failed-tests,examples,effects,type-of,history,task.abort`.

Use Flow/2 and include `frontend:"flow"` and `syntaxVersion:2` on `define` and
`eval`. Keep new definitions library-qualified. Record changes must be staged
and published with their tests in the same live session. Do not edit runtime
storage files directly. No external effects are granted.

# Conventional F# interface

Allowed operations: `inspect,read,search,patch,replace,validate`.

The fixed validation project is `SubscriptionBatch.Conventional.fsproj` inside
your assigned project. Only `Operations.fs` and `SelfTests.fs` may be edited;
all other project files are read-only. Validation uses that local project.
Keep the implementation pure and do not add packages.
