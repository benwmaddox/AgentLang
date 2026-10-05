# Flow containers and exhaustive cases

Status: focused and integrated Release validation passed. Flow remains an
opt-in frontend; the default parser, protocol, and durable source format have
not changed.

This slice adds exact Flow syntax forms for the closed list, option, and result
constructors: `list::empty<T>()`, `list::singleton<T>(value)`,
`option::none<T>()`, `option::some<T>(value)`, `result::ok<T, E>(value)`, and
`result::error<T, E>(value)`. Type arguments must be explicit and closed, and
are recursively checked against built-in and declared nominal types. Payloads
must exactly match the declared type, including refined scalar and record
identity. The forms lower to existing container constructor expressions and
verified IR operations.

Flow now parses exhaustive `Option` and `Result` matches. Either source order
is accepted and canonical rendering orders `some`/`none` and `ok`/`error`.
Each arm is checked for one result of the same type. Payload bindings are local
to their arm, cannot shadow outer locals, and may share a spelling across
independent Result arms. Both arms contribute dependencies, declared effects,
and branch coverage even when runtime execution selects only one arm. No helper
words, closures, tuple values, new runtime, or allocator changes were added.

Focused Flow tests cover all six constructors, nested container types,
containerized refined values and records, invalid refined values, constructor
and match diagnostics, case-local visibility, same-named Result payloads,
both execution outcomes, both coverage outcomes, conservative effect
preflight, and canonical parse/render roundtrips for nested constructor and
match expressions. A completed-AST iterative depth check also bounds postfix
chains and combinations of receiver, argument, conditional, and match nesting
before recursive lowering/rendering. Incremental Flow lowering chooses new
private source markers after the highest retained marker index and reports a
structured exhaustion error instead of assuming retained markers are dense.

Validation results:

- `dotnet build src/AgentLang.Core/AgentLang.Core.fsproj -c Release` passed
  with zero warnings and errors; see [Core build evidence](evidence/026-core-build.json).
- `dotnet run --project tests/AgentLang.Flow.Tests -c Release` passed with
  122 assertions, including nested constructor/case rendering, a valid
  128-node render/lower/execute boundary, rejection beyond that limit, and
  sparse source-marker allocation; see [focused Flow evidence](evidence/026-flow-focused.json).
- The integrated fresh Release gate passed all 24 checks with zero build
  warnings and errors. It included Flow (122 assertions), IR (102), and Flow
  lint (42), plus persistence projection, parser limits, matched fixtures, and
  subagent-host checks. Full details are in [semantic integration report](026-flow-semantic-integration.md)
  and [integrated validation evidence](evidence/026-flow-semantic-validation.json).

This slice does not integrate Flow definitions with persistence, test/example
storage, rename, reload, or the Runtime protocol. Static callbacks, explicit
multi-output bindings, Flow-native tests and examples, and default authoring
cutover remain later work. Focused language tests do not establish end-to-end
frontend migration or agent-productivity results.
