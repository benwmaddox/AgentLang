# Closed enums in Flow/2

Closed enums represent a finite set of nominal alternatives. This slice
supports cases without payloads:

```flow
enum RenewalState {
    case pending
    case renewed
    case cancelled
}

fn renewal.label(state: RenewalState) -> String {
    match state {
        pending => "Pending"
        renewed => {
            let label = "Renewed"
            label
        }
        cancelled => "Cancelled"
    }
}

test renewal.label/pending {
    renewal.label(RenewalState.pending())
    => "Pending"
}

test renewal.label/renewed {
    renewal.label(RenewalState.renewed())
    => "Renewed"
}

test renewal.label/cancelled {
    renewal.label(RenewalState.cancelled())
    => "Cancelled"
}
```

Construct cases with `RenewalState.pending()`. Cases with the same label in
different enum types remain distinct, and equality does not convert strings or
other nominal types. A match must name each case exactly once. One-expression
arms can be written directly after `=>`; an arm with multiple statements keeps
a braced block. Missing, unknown, and duplicate cases fail before execution.

An enum must contain at least one case, and its labels must be unique. The
reserved labels `some`, `none`, `ok`, and `error` belong to Option and Result
matches. Payload cases, wildcard arms, and case-set migration are not
supported. Committed type definitions remain immutable. Flow/1 does not accept
enum declarations or enum-specific call bindings.

Enum identity and ordered case tables are retained in the verified IR. Match
effects contribute to the function's checked effects, and interpreter
coverage records the case actually reached. Library qualification checks each
direct enum parameter position for every declared case and also checks
supported finite return domains. Enum-bearing authored helpers must be
independently qualified; a wrapper cannot inherit their evidence. Unsupported
or oversized domains fail closed with `LIBRARY_FINITE_DOMAIN_UNSUPPORTED`,
and missing cases fail with `LIBRARY_FINITE_COVERAGE_INCOMPLETE`. See the
[finite coverage contract](FINITE-COVERAGE.md).

The owning-stack LLVM backend supports payload-free enums as inline eight-byte
ordinals with nominal type identity and layout ABI 2 case counts. Construction,
exhaustive matching, equality, and enum-bearing records pass
interpreter/native O0/O2 conformance; malformed external tags and extents are
rejected. The older graph-backed `LlvmAot` backend still rejects enum
operations with `IR_LLVM_ENUM_UNSUPPORTED`. Neither interpreter support nor
semantic conformance establishes a performance advantage. See
[report 153](../reports/153-owning-native-enums.md) for backend scope.

The [persistence regression fixture](../examples/closed-renewal-state.agent)
retains identity calls in its scrutinee and arms, plus attached cases for every
label outcome.
