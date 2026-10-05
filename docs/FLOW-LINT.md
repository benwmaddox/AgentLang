# Flow binding lint

`AgentLang.FlowLint` provides an advisory, deterministic analysis over a parsed
`FlowWordDefinition`. It reports unused local bindings and locals whose first
read comes after too many intervening statements. It does not rewrite the
definition, remove initializers, or participate in compilation, tests, library
gates, or durable commits. An unused binding can have an effectful initializer;
the lint reports it without assuming the initializer is safe to move or drop.

The API is `FlowLint.analyze options definition`, returning either a list of
`FlowLintWarning` records or a structured `FlowLintError`. The default is
`FlowLint.defaultOptions`, which warns when more than eight lexical statements
intervene. `FlowLint.noDistanceLimit` disables distance warnings but keeps
unused-binding warnings. A negative threshold is rejected with
`FLOW_LINT_INVALID_OPTIONS`; callers can also use `tryCreateOptions` to validate
configuration before analysis.

Distance is counted within the statement block that owns a binding. If a local
is declared at statement index `d` and first read at index `u`, its gap is
`u - d - 1`. Eight intervening statements are therefore within the default
limit, while nine trigger `FLOW_LOCAL_FIRST_USE_TOO_DISTANT`. An unused binding
emits `FLOW_LOCAL_UNUSED`, with no first-use span or gap. Option/Result payload
locals are treated like locals introduced immediately before the first case
statement. Formal parameters are deliberately excluded.

An initializer is walked before its `let` name becomes visible, so self-reads
do not count as uses of that binding. A vector binding walks its initializer
before exposing any of its names, then makes all names visible together. Each
binding keeps its own authored name span, and diagnostics retain pattern order.
Reads of an outer local anywhere inside an `if` or `match` expression are
attributed to the statement containing that expression. Locals declared inside
a branch or case use that branch or case's own statement positions. Every member
of a terminal `return (...)` vector is visited in order at the return statement's
position; reads of outer locals in nested returns are attributed to the enclosing
`if` or `match` statement. Calls, dot-call receivers and arguments, constructors,
conditions, scrutinees, and payload expressions are all visited. Static callback
word references are dictionary targets rather than local reads; their receivers
are still visited. Match cases are traversed in source-span order; warnings
otherwise follow syntax traversal order, so repeated analysis of the same tree
returns the same results. Output signatures do not change lint behavior.

The linter is syntax-only: it does not resolve call targets, infer effects, or
judge whether a local is semantically used through runtime behavior. Directly
constructed trees with more than 256 nested expressions/blocks return
`FLOW_LINT_NESTING_LIMIT` instead of recursing without a bound. This first slice
is a library API and test project; runtime, CLI, protocol, and editor exposure
are not part of it.
