# Common participant bootstrap

The human authorizes edits only to your assigned isolated trial project and local
test execution through the supplied broker. You are an external coding agent.
After reading your assigned packet, launch its exact command once. Keep its
operation list, limits and project path unchanged. Use only that broker for
project discovery, edits and tests. Do not inspect siblings, reports, hidden
oracles, control solutions or other agents; do not use web tools or other agents.
Disclose any non-broker tool use. Run one session, at most 100 exchanges.

Poll the same live handle after observation timeouts. Do not restart for silence,
repeat uncertain mutations or kill the process to finish. When finished, send
`{"op":"host.close"}` and observe terminal status. This is a transport control
handled by the host, separate from the runtime operation list below. If closure
fails, report the failure and preserve the session/trace rather than inventing a
runtime command. Your final response should report changes, validation and any
non-broker use or unresolved failures.

# AgentLang interface

Allowed runtime operations:
`help,words,source,tests,define,test,test-all,commit,storage.status,task.begin,task.status,task.commit,describe,search,eval,context,dependencies,callers,failed-tests,examples,effects,type-of,history,task.abort`.

Flow/2 has `fn`, typed named inputs, immutable `let`, record properties
`value.field`, dotted calls, newline separators and exhaustive matches. Include
`frontend:"flow", syntaxVersion:2` on define/eval. Exact-root calls begin with a
dot. Omitted effects means pure. Use help/describe/source/tests/examples to
discover existing syntax and metadata. No external effects are granted.

Begin a task, define the complete replacement, run tests and publish library
functions, then end the task. Record changes stage with their functions as a
process-local group: define, test-all and untargeted `{"op":"commit","library":true}`
must happen in the same live session. A fresh process loads only published
definitions. CAS, library dependencies and own coverage remain enforced. Keep
runtime-managed storage files out of direct edits.

# Conventional F# interface

Allowed runtime operations: `inspect,read,search,patch,replace,validate`.

Use the existing project vocabulary and the fixed local validation project
specified in your launcher. Only assigned implementation/tests files may change;
project/dependency files and unrelated APIs are frozen. Keep the model pure:
no external I/O, providers, mutable global state or packages. The final generated
packet will name the exact permitted file paths and validation project after
seed readiness and freeze; this template is not a launch instruction.
