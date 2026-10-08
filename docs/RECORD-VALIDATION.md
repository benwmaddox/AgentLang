# Whole-record validation

Status: implemented and locally validated. The full Debug run passed 36/37
checks; the remaining inspection-script compatibility repair passed its exact
31-assertion probe separately. See [report 119](../reports/119-record-construction-invariants.md).

A record may name one pure validator. The predicate receives the complete
nominal record and returns Bool. Its generated constructor returns the record
only after the predicate returns true; false raises RECORD_VALIDATION_FAILED.
Records without a validator retain ordinary field-type-checked construction.

```agentlang
record Interval {
    field start: Int;
    field finish: Int;
    validate interval::valid?;
}

fn interval.valid?(value: Interval) -> Bool {
    doc "An interval finishes at or after its start."

    int::less-or-equal(value.start, value.finish)
}
```

The complete value is available internally to the predicate. There is no
language-level unchecked constructor. During predicate evaluation, the record
and values passed to pure helpers are unchecked candidates: that code must not
assume the invariant already holds. Predicates may read fields and call pure
helpers, but direct or indirect constructor/validator cycles are rejected.
The compiler does not use these predicates as proven optimization assumptions.
Predicate execution errors propagate; only a normal false return becomes
`RECORD_VALIDATION_FAILED`.
Persistent validator bindings and their dependency closures are frozen so
subsequent edits cannot silently change an existing type's meaning.

Tests for a rejecting predicate use its own attached expected-error test:

```agentlang
test interval.valid?/reversed {
    interval::new(start = 2, finish = 1)
    => error RECORD_VALIDATION_FAILED
}
```

The predicate actually returns false before the constructor rejects the value.
That completed invocation can contribute finite-return and branch evidence.
A target that throws does not gain a fictitious return observation. Ordinary
successful tests must cover the predicate's true return too.

The focused Flow/2 business extension verifies this path with a
`ValidatedCustomerLookup { target: CustomerId; found: Option<Customer> }`.
Its `lookup.valid?` predicate has three attached cases: no match, matching
customer, and a mismatching constructor expected to fail. The predicate itself
qualifies as a library function after tests cover both branches and both Bool
returns. Its record validator target and test metadata survive a fresh Engine
reload. This is implementation validation, not a fresh-agent adoption trial.

Validation can rule out otherwise possible field combinations. Finite coverage
must not enumerate those combinations as though all remained valid. A refined
record with unproven finite projections is explicitly unsupported for exhaustive
return qualification; fully open shapes with no such obligations need no
finite enumeration. This does not prove the predicate's business policy.
Independent behavioral tests remain necessary.

The typed semantic IR records the exact validator target. Interpreter and
supported LLVM construction enforce the same predicate. Low-level native
allocation helpers are trusted implementation machinery, not a supported
unchecked language API. Native Option/List lowering remains unsupported; this
change does not add those backend operations. The Flow runtime business example
does use Option fields and is exercised by the interpreter.

Focused local results include Core IR 191, interpreter 93, formatter 44, Flow
1,110 and Source 101 assertions; Runtime 30 groups / 979 assertions; Storage
16 groups / 370 assertions; Business.Transitions 9 groups / 5,479 assertions;
and LLVM 455 assertions. The aggregate and repaired probe are reported
separately above. No fresh-agent efficacy result is claimed.
