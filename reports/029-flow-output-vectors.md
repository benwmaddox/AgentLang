# Flow output vectors and destructuring

Status: ordered output vectors, destructuring, and terminal return vectors are
implemented in the opt-in Flow frontend. The fresh Core build and focused Flow
suite pass; the repository validation gate remains pending.

Flow word signatures now hold ordered, nonempty `Outputs: LangType list` while
retaining the scalar `-> Int` spelling. `let (a, b) = producer()` binds every
output in declared order, with reverse stack stores preserving those names.
`return expression` and `return (a, b)` produce one or more scalar results for
the current lexical block; returns are terminal within that block, but an `if`
or match can bind its result and continue in its enclosing block. Branches and
cases must agree exactly on output count and positional types. Multi-output
calls remain invalid in scalar contexts, and no tuple runtime value or
implicit-spread behavior was added. Call identity and ambiguity are determined
from name and inputs before output arity is checked. Static list callbacks
retain their existing single-output contract.

Lowering evaluates a destructuring producer once and stores its values in
reverse stack order. Generic substitutions, nominal types, effect checks,
source origins for each pattern name and return member, and existing branch
coverage continue through the existing Core/verified IR. Public isolated
expression APIs enforce a one-output boundary.

The shared iterative preflight now streams parameter/output type roots and
statement blocks, and counts every declared type, expression node, return
member, and destructured binding against one 100,000-node budget. It charges all
pattern members before name/duplicate scanning, and checks return terminality
without indexing or copying the block. Render and lower entry points apply the
same checks to host-built ASTs before recursive consumers run.

The focused suite covers scalar compatibility, deterministic vector
parse/render, producer/destructuring/reordering execution, generic output
substitution, `if` continuation, Option and Result cases, exact positional
joins, nominal `Email`, scalar-context rejection, name ambiguity, effects
executed once, authored source origins, malformed parser/host ASTs, and
oversized output lists, binding patterns, return vectors, and statement blocks.

An earlier Core Release build passed with zero warnings and errors before the
wide-vector budget repair. That result is historical and does not validate the
final guard implementation. The fresh Core Release rebuild after the repair
passed with zero warnings and errors. The first Flow focused run then failed to
compile three new branch-coverage assertions: they passed a raw `IrBlock` to
`VerifiedIrBody.inspect`, which accepts only a verified detached body. The
assertions now read function coverage from `VerifiedIrProgram.CoverageByWord`,
matching the existing Flow tests. The retry passed with 261 assertions. The
Flow Lint subsequently passed 66 assertions, and the coordinating agent's
fresh 24-check Release gate passed; see report 030 and its full evidence. No
agent-productivity, benchmark, or runtime-memory improvement is claimed.

| Command | Result |
| --- | --- |
| `dotnet build src/AgentLang.Core/AgentLang.Core.fsproj -c Release --no-restore` | Passed after the shared-budget repair; 0 warnings, 0 errors. |
| `dotnet run --project tests/AgentLang.Flow.Tests/AgentLang.Flow.Tests.fsproj -c Release` | First attempt failed to compile the three coverage assertions described above; after correction, passed with 261 assertions. |
