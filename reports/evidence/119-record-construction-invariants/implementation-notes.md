# Native record validator implementation notes

## Scope

The LLVM backend now treats a record's frozen `ValidatorCall` as part of the
generated constructor contract. It allocates the record in the scratch arena,
calls the pure predicate, and exposes the record value to the caller only after
the predicate returns canonical `Bool true`.

The validator dependency is included in native function reachability for both
explicit `MakeRecord` IR and direct generated-constructor calls. Predicate
failures report `RECORD_VALIDATION_FAILED` at the record-construction source
site. Malformed native Bool encodings report `RUNTIME_VALIDATOR_RESULT` with the
raw `i64` value in the existing error-argument context fields.

## Trust and ABI boundary

The native allocator remains an internal allocation helper. This change does
not claim that arbitrary external C callers of that helper are protected by the
language predicate. F# retained-root reentry remains restricted to opaque
results owned by the exact verified program; no public host-created record
injection path was added.

No ABI, storage version, record layout, or C allocator signature changes were
made. Existing records without validators retain their previous behavior.

## Validation

The new native conformance case uses `Range { minimum: Int; maximum: Int }` and
a nested pure validator (`range.valid?` -> `range.bounds-valid?` -> generated
field accessors). It covers valid and invalid construction under O0 and O2,
construction through a user function, diagnostic parity with the interpreter,
and re-entry from an opaque retained record root through a constructor that
re-runs validation.

Fresh Debug validation passed. The build had 0 warnings and 0 errors, and the
native conformance executable passed all 455 assertions, including the new
record-validator stage. The exact command, exit codes, SHA-256 source inventories
before/after build and test, and logs are recorded in `build-02/run-summary.json`
and the files listed there. Source inventories matched throughout the build and
test. Native object/IR artifacts are under
`.agentlang/native-validation/tests-47783ccc155149358eec33a0f3d24f6e/`.

The .NET SDK's `dotnet format whitespace` refused both F# projects because it
supports only C# and Visual Basic. `git diff --check` passed. No separate
F# formatter is configured in this checkout.
