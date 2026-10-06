# Expected runtime errors in tests

Tests can assert that execution raises a specific language diagnostic:

```agentlang
word number.divide(numerator: Int, denominator: Int) -> Int {
    effects none
    ::divide(numerator, denominator)
}
test number.divide/basic {
    number::divide(6, 2)
    => 3
}
test number.divide/zero {
    number::divide(1, 0)
    => error RUNTIME_DIVIDE_BY_ZERO
}
```

The final line `=> error CODE` is available only in `test` blocks. `CODE` must
be a stable uppercase identifier beginning with an ASCII letter and containing
only uppercase letters, digits, or underscores. Matching is exact and uses the
diagnostic code, not its message. Value expectations such as `=> 42` continue
to work unchanged. Examples remain value-only.

An expected-error test must contain at least one expression. The compiler still
resolves and type-checks the complete test body and its dependencies; the
expectation does not turn compile-time errors into passing tests. Since the
test is expected to throw, it has no final-value stack expectation. At runtime,
only the matching language diagnostic passes. Normal completion, a different
language diagnostic, or an unexpected host failure fails the test.

Expected errors are useful for checking rejected inputs, refinements, division
by zero, and bounded numeric operations. Tests should keep their effect setup
deterministic, and library-word branch coverage requirements continue to apply
to each relevant outcome.


These are Flow declarations. Explicit Stack tests retain the same `=> error CODE`
expectation in their legacy `test ... end` source. Case-only Flow documents can
add a test to one existing Flow user-word owner; replacing an existing case needs
explicit owner revision CAS. Expected expressions and examples do not substitute
for the tested word's actual library coverage.
