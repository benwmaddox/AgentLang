# Flow maintenance acceptance

Status: the fifth fresh solution build passed with zero warnings and errors.
The fourth focused Flow Runtime run passed 15 groups and 399 assertions. Earlier
fixture-only failures were repaired: typed `WordRevision` annotations, missing
owner tests, unsupported `.first()` syntax, and an incorrect assumption that
virtual filesystem changes carry between isolated Runtime cases. The provider
guard and library-coverage cases now use the configured fixed clock instead.
The source-hash regression for a rewritten Stack attachment was not added: the
public Runtime test result omits source spans, while the public `ir` operation
reports only word bodies, so this suite has no public result from which to
assert the changed test body's hash. Existing rewrite span/path tests and the
Runtime's hash-addressed changed-source parsing remain the evidence for that
path. Root's final gate is pending.

This suite exercises the approved Flow-aware `rename` and `deprecate` behavior
through the durable Runtime API. The fixtures cover stable identity across
Flow and Stack callers, actual and expected-expression call sites, static
callbacks, dot calls with effectful receivers and named arguments, exact source
history retention, metadata-only deprecation, test and library-coverage
failure atomicity, task rollback, named snapshots, a fresh-process CLI reload,
and source-history preservation. A positional encoding oracle makes the dot
call test detect swapped named arguments. They use the public Runtime and
Storage surfaces and do not claim a default-frontend cutover.

The library rejection fixture commits a complete Flow library from passing
tests that execute both outcomes of `equals(clock::now(), value)`. It then
reopens the project with a different configured clock. Both tests still pass,
but actual executions cover only the false branch; rename and deprecate must
reject without changing authority or the live word.

The focused result above is for the frozen suite before the proposed
changed-Stack-case hash assertion; no new fixture or validation claim is made
for that assertion.

## Final local gate

The full 26-check Release validation passed, including this frozen 15-group /
399-assertion suite and the 832-assertion Flow suite. The gate rebuilt the solution
with zero warnings/errors. See [local validation](evidence/047-publication-validation.json).
The report correctly labels parent revision `00076c1` as dirty; exact-source CI
will be audited after committing. The changed-case span observability limitation
above remains open, and no default frontend or experimental benefit is claimed.
