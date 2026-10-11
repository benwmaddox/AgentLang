# Native Unit canonicality source review

Reviewed the planned change in `reports/202-native-unit-canonicality-plan.md` and the four scoped files:

- `src/AgentLang.Llvm/native/owning_stack_runtime.c`
- `src/AgentLang.Llvm/native/owning_stack_runtime_test.c`
- `tests/AgentLang.Llvm.Tests/Program.fs`
- `tests/fixtures/native-conformance/native-value-stack.json`

## Findings

The production check is in the shared value scanner. The Unit branch reads its eight-byte token only after `al_owning_scan_readable_in_owner` succeeds. For external inputs that checks the supplied source range; for stack values it also checks capacity, cursor, and initialization. On a nonzero token the scanner takes its existing failure path. The measured size remains local until the complete scan succeeds, so rejected values do not publish payload or extent outputs.

The Option fixture describes field 0 as `Some Unit` and field 1 as `None`, matching the runtime’s tag mapping. Its child and sentinel case fields both have offset 8 and zero flags/reserved values. I initially found `field_count=0`, which violated the runtime’s Option descriptor requirement; the current source has `first_field=0, field_count=2`, so this blocker is resolved. The scanner recursively checks only tag 0’s active Unit child. The tests cover zero Some, nonzero Some rejection at external and stack ingress, and nonzero bytes in None’s inactive trailing storage while expecting an 8-byte None value.

The C test adds one case and 37 `AL_CHECK`s: 2 zero Unit checks, 6 checks for three nonzero tokens, 16 checks for source/owner lengths 0 through 7, 2 initial Some checks, 6 malformed Some checks, 2 inactive-None checks, 2 exact-size None checks, and 1 guard check. This matches the fixture change from 51/667 to 52/704 and from 35/478 to 36/515. The fixed 16/189 baseline and ABI fields remain unchanged.

The F# case has an independent interpreter value oracle, checks the expected 32 output bytes for Unit, Some Unit, and None, and runs owning O0/O2 while requiring schema 3. It expects four charged instructions. `OwningStackExecutionResult` does not expose `StepsConsumed`; the current source reads the native counter through the existing `invokeRawOwningEntryForMetrics` helper and checks status plus `NativeOwningContext.StepsConsumed`, so the capacity arguments are not being treated as fuel.

The managed codec writes Unit as eight zero bytes and rejects nonzero Unit during decode. The interpreter represents Unit as the single `RuntimeUnit` case and uses structural equality. Requiring canonical Unit bytes in the shared scanner closes the direct-ingress mismatch without changing ABI or language values.

The shortened source/owner tests use valid backing arrays with smaller declared bounds; they do not use faulting guard pages. Source inspection confirms the owner and source bounds checks precede the Unit load. No remaining source correctness blocker was found in the assigned scope.

## Review boundary

This is a source-only review. I did not edit implementation files or run builds/tests; the implementation owner retains the validation gates. This review is bounded to the listed change and is not a general security certification.
