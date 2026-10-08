# Closed enums in Flow/2

Closed enums describe a finite set of nominal alternatives. They are suitable
for internal domain states whose possibilities are known at definition time.
This first slice supports cases without payloads:

```flow
enum RenewalState {
    case pending;
    case renewed;
    case cancelled;
}

fn renewal.label(state: RenewalState) -> String {
    doc "Returns the display label for a renewal state."

    match state {
        pending => { "Pending" }
        renewed => { "Renewed" }
        cancelled => { "Cancelled" }
    }
}
```

Create a value with `RenewalState::pending()`. Cases of different enum types
remain different types even when they share a label. Equality compares values
of the same nominal type. There is no implicit conversion from a String.
External strings need an explicit decoder with an invalid-value result.

Every match must name each declared case exactly once. Missing, unknown and
duplicate cases are rejected before execution. All arms must produce compatible
output vectors, and their effects contribute to the function's checked effects.
Every function containing an enum match receives these compiler checks.

The case set must be nonempty and labels unique. This slice reserves `some`,
`none`, `ok` and `error` for the existing Option/Result match forms. There are no
payload cases, wildcard arms or case-set migration operations. Committed type
definitions remain immutable.

The authoritative IR retains enum identity, ordered case tables, construction
and exhaustive matching. Its verifier checks these independently of parsing.
Interpreter coverage records the actual named match outcomes. Durable Flow/2
source and call bindings retain constructors and calls nested inside match arms.
Flow/1 does not accept the new declarations or enum-specific binding paths.

Library qualification now checks each direct enum input parameter for every
declared case and checks supported finite return domains, in addition to the
function's own instruction and branch coverage. An enum-bearing authored helper
reached by a library function must itself be independently qualified as a
library function; a wrapper cannot inherit that evidence. Unprovable or
oversized finite domains fail closed with `LIBRARY_FINITE_DOMAIN_UNSUPPORTED`,
and missing cases fail with `LIBRARY_FINITE_COVERAGE_INCOMPLETE`. See the
[finite coverage contract](FINITE-COVERAGE.md). All 37 local validation checks pass; passing every match arm alone does not establish the complete
library contract.

LLVM execution of enums is not implemented in this slice and fails with
`IR_LLVM_ENUM_UNSUPPORTED`.
Interpreter support is not evidence of native enum performance or memory layout.

The [persistence regression fixture](../examples/closed-renewal-state.agent)
includes identity calls in its scrutinee and arms to exercise durable call paths,
plus attached tests for every label outcome.
