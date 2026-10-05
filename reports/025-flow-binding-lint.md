# Flow binding lint

## Scope

This implementation adds a pure lint over the stable Flow AST. It reports
unused `let` and match-payload locals, plus locals whose first use exceeds a
configurable lexical statement gap. It does not modify source or change
compiler, test, or library-gate behavior.

## Implementation

- `FlowLint.analyze` returns stable warning DTOs with code, binding name,
  declaration span, optional first-use span, and optional gap.
- The default maximum is eight intervening statements. `None` disables only
  distance warnings; unused warnings remain active.
- Each block tracks visible names in a persistent map. Initializers are scanned
  before a new `let` becomes visible, and an ordered binding collector preserves
  deterministic warning order without repeatedly copying prior bindings.
- Reads of parent locals in branch expressions use the containing expression's
  statement index. Branch and case locals are analyzed in their own lexical
  blocks. Formal parameters are excluded.
- Container payloads, call arguments and receivers, conditions, and
  Option/Result match scrutinees and cases are visited. The analyzer never
  moves or removes an initializer, including one with effects.
- Invalid negative thresholds and directly constructed trees beyond the
  256-level syntax traversal bound return structured errors.

The new dependency-free project is `tests/AgentLang.Flow.Lint.Tests`. It covers
threshold boundaries, disabled distance diagnostics, line-ending independence,
branch-local and multi-level outer-binding positions, initializer self-read
visibility (using syntax-only parsed input that is intentionally not
typechecked), used and unused Option/Result payloads, constructor/call/match
traversal, unused initializer preservation, deterministic ordering, a
4,096-binding flat block, and success/failure at the nesting boundary.

## Validation status

The coordinating agent registered the module and acceptance project in the
Core project, solution, and validation script. The focused Release command
`dotnet run --project tests/AgentLang.Flow.Lint.Tests -c Release` passed with 42
assertions after rebuilding Core without compiler warnings. The integrated
validation gate is still pending. This report makes no end-to-end runtime or
CLI integration claim.
