# Flow static list callbacks

Status: the focused static callback slice is implemented; output vectors,
Flow-native tests/examples, and durable Flow source integration remain follow-on
work.

Flow now accepts qualified callback references such as
`customers.map(customer::normalize)` and explicitly marked short references
such as `customers.filter(word active?)`. A reference is valid only as the
sole positional argument to `map`, `filter`, or `each`. Ordinary arguments
remain ordinary expressions, so `.map(localValue)` is never reinterpreted as
a word name. Qualified ordinary calls such as `list::map(1)` retain their
normal call meaning.

Resolution uses actual dictionary entries, not generated constructor aliases.
It determines identity before checking callback arity and types, so output
constraints cannot silently disambiguate short names. `map` requires one input
and one output, `filter` requires one `Bool` output, and `each` requires one
`Unit` output. The frontend emits existing `MapList`, `FilterList`, and
`EachList` expressions; it adds no closures or helper words. Static callback
effects and dependencies remain part of checked bodies for empty lists. The
interpreter's effect preflight runs before iteration or provider calls.

The focused tests cover callback parse/render round trips, value-argument and
qualified-call disambiguation, short-name and signature errors, nominal
`Email` mapping, empty and populated list behavior, filter keep/drop outcomes,
`each` effects and Unit output, callback source spans, resolved target
identity, empty-list capability denial, and malformed direct-AST references.
An incomplete callback reference at EOF reports `FLOW_INCOMPLETE_INPUT` rather
than being parsed as another expression form.

The parser's iterative depth check was moved into shared `FlowStructure`
validation. The same preflight now checks host-built ASTs before public
rendering or lowering. Expression and type depth are limited to 128; each
expression or word has a shared 100,000 expanded-node budget across expression
and type nodes. Word validation includes parameter and output types alongside
body expressions. Callback references remain leaves. Expanded counting follows
each reference in a shared AST DAG, so repeated subtrees cannot cause
unbounded work in recursive consumers.

A manually constructed 128-node expression rendered, lowered, compiled, and
executed. Depth-129 expressions and types, including word parameter/output
types and constructor type arguments, were rejected with
`FLOW_NESTING_LIMIT` and the relevant source span. Shared `TResult<T, T>` type
and repeated-expression DAGs were rejected with `FLOW_STRUCTURE_LIMIT`; a
word's input type and body also shared one aggregate budget. The diagnostics
do not format the offending type or expand the DAG.

Validation completed on the shared checkout:

| Command | Result |
| --- | --- |
| `dotnet build src/AgentLang.Core/AgentLang.Core.fsproj -c Release --no-restore` | Passed after the shared type/expanded-node validator change; 0 warnings, 0 errors. |
| `dotnet run --project tests/AgentLang.Flow.Tests/AgentLang.Flow.Tests.fsproj -c Release` | Passed after the shared type/expanded-node validator change; 198 assertions. |
| `dotnet run --project tests/AgentLang.Flow.Lint.Tests/AgentLang.Flow.Lint.Tests.fsproj -c Release` | Passed; 47 assertions. |
| `dotnet run --project tests/AgentLang.IR.Tests/AgentLang.IR.Tests.fsproj -c Release` | Passed; 102 assertions. |
| `dotnet run --project tests/AgentLang.IR.Interpreter.Tests/AgentLang.IR.Interpreter.Tests.fsproj -c Release` | Passed; 22 assertions. |
| `dotnet run --project tests/AgentLang.Acceptance/AgentLang.Acceptance.fsproj -c Release` | Passed; 34 groups, 583 assertions. |

Flow Lint, IR, IR Interpreter, and Acceptance were repeated in the final fresh
24-check Release gate after the type/expanded-node follow-up; all checks passed
with Flow at 198 assertions. See report 028 and its final gate evidence.
No benchmark or
agent-productivity result is claimed. The default Runtime and protocol still
use the existing durable source model; complete Flow migration remains
necessary.
