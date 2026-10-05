# Flow frontend foundation report

Status: focused implementation checks passed, 2026-10-05. This is an opt-in
frontend foundation; it is not the default authoring path or a durable source
format.

The new `FlowParser` and `FlowSyntax` modules parse and render explicit Flow
source without falling back to the legacy stack parser. `FlowLowering` resolves
ordinary calls and first-input dot stages statically, checks named parameter
binding against an explicit catalog, and lowers typed expressions into the
existing checked expression tree and verified semantic IR. It supports
immutable locals, expression-valued `if`, scalar/record/refinement types, and
one output per expression. Named arguments are evaluated once in source order;
the receiver of a dot stage is evaluated once. Branch-local names are restored
at the branch boundary through a scoped semantic operation.

Lowered instructions retain their authored source locations in verified IR.
Private, unique compiler markers are mapped before verification and do not
appear in the exported source map. Missing origin mappings fail closed with a
diagnostic that does not disclose marker coordinates. Only the exact internal
source classifications `synthetic-scope`, `synthetic-store-local`, and
`synthetic-load-local` are exempt from authored coverage obligations, and each
must match its corresponding verified opcode. Formatter output now declares
IR schema version 2 to include the scoped operation.

The parser accepts LF, CRLF, and lone-CR line endings. Its limits are 1,000,000
source code units, 100,000 tokens, 100,000 code units per quoted string, and
128 nested constructs. Multiline spans use absolute source offsets, and
qualified names support multiple `::` segments.

Focused validation completed:

- `dotnet build src/AgentLang.Core/AgentLang.Core.fsproj -c Release` — passed,
  0 warnings and 0 errors.
- `dotnet run --project tests/AgentLang.Flow.Tests/AgentLang.Flow.Tests.fsproj -c Release`
  — passed, 47 assertions. The final run includes nested-`if` call-argument and
  dot-receiver roundtrips/execution plus an outer-local read after a scoped
  branch.
- `dotnet run --project tests/AgentLang.IR.Tests/AgentLang.IR.Tests.fsproj -c Release`
  — passed, 73 assertions, including exact synthetic-tag/opcode checks and
  stack-safe verification.
- `dotnet run --project tests/AgentLang.IR.Formatting.Tests/AgentLang.IR.Formatting.Tests.fsproj -c Release`
  — passed, 39 assertions, including schema version 2 and scoped IR output.
- `dotnet run --project tests/AgentLang.IR.Interpreter.Tests/AgentLang.IR.Interpreter.Tests.fsproj -c Release`
  — passed, 22 assertions. The direct Scope regression overwrites an outer
  local inside Scope, then confirms that the scoped result stack survives,
  the outer local is restored, the source marker maps to its authored span,
  and all seven instructions consume fuel.
- `git diff --check` — passed; Git reported only line-ending normalization
  notices for unrelated shared files.

The Flow tests exercise parser spans and newline forms, namespace resolution,
canonical text rendering, ordinary/dot equivalence, named-argument effect
order, single receiver evaluation, branch-local scope, real source-origin
projection, and growing compiler contexts. These checks establish compiler
and interpreter compatibility for the covered fixtures; they are not a
cross-frontend parity study or an agent productivity result.

The shared CLI/Runtime checks run by the integration owner also passed:
Acceptance passed 34 groups / 583 assertions, and Source passed 84 assertions.
The repository-wide Release validation is being run separately; this report
does not treat those focused checks as its result.

Remaining work is substantial: Flow definitions are not yet committed,
reloaded, renamed, or edited through the Runtime protocol; tests/examples,
containers/cases, complete catalog metadata, and multi-output bindings are not
integrated. The legacy parser and default protocol remain unchanged. No claim is
made that the overall frontend migration or its full acceptance gate is
complete.

## Integrated validation

The subsequent fresh full Release gate passed all 23 required checks, and the
pinned historical CLI comparison passed 314 selected checks. See
[report 021](021-integrated-flow-foundation.md) and its saved evidence. These
results validate the delivered slice; the full Flow migration and controlled
agent evaluation remain incomplete.
