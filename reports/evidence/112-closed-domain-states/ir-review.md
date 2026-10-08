# Focused enum IR and runtime-value review (read-only)

Scope: current `Compiler.fs`, `TypedIR.fs`, `IrInterpreter.fs`, and `ValueInspection.fs` enum additions only. Runtime library qualification and persistence are still in progress and are excluded. No production edits or build were performed.

## Result

No concrete verifier or runtime-value blocker found in the reviewed paths. The safeguards are present and connected:

- `TypedIR.fs:434-448` checks nominal key/kind layout, nonnegative enum keys, nonempty unique legal case labels, and type-name collision. `TypedIR.fs:486-492` binds a generated case constructor to its frozen case index, exact generated name, zero inputs, and exact nominal output. `TypedIR.fs:697-702` checks the explicit construction operation against that target and type table. `IrInterpreter.fs:489-494` rechecks case range and arity at execution.
- `Compiler.fs:423-445` rejects missing, duplicate, or unknown authored cases and checks equal stack/local exits. `TypedIR.fs:671-696` requires the exact set of indexes `0..N-1`, exact nominal scrutinee key and enum kind, verifies every arm against the same entry shape, and joins stack and local shape. `TypedIR.fs:748-763` unions arm effects and compares the result to inferred/declared function effects. `IrInterpreter.fs:821-836` checks the runtime nominal key and case range before selecting an arm and recording its case name.
- `IrInterpreter.fs:333-337,870-873` validates runtime enum key/case before output sizing or conversion; its internal `RuntimeEnum(key,index)` equality is structural (`IrInterpreter.fs:534`), so distinct nominal keys/case indexes remain distinct. `ValueInspection.fs:447-467` rejects a host-created `EnumValue` with an invalid case, mismatched name, or wrong nominal kind before structured output.

## Focused test gate still needed

At review time `tests/AgentLang.IR.Tests/Program.fs` has no enum-specific malformed-program tests, and `tests/AgentLang.IR.Interpreter.Tests/Program.fs` has only `Enums = Map.empty` in its lowering context. Add a small table of mutations to one valid verified enum program, rather than a new harness:

1. Match arm index duplicated, omitted, negative, or beyond the frozen case count must fail `IR_ENUM_MATCH_CASE_SET`; a record/scalar key or another enum key must fail `IR_ENUM_MATCH_TYPE`. Give two arms mismatched output stack/local shape and verify `IR_BRANCH_JOIN_MISMATCH`. Give one arm a real effect without updating the function's inferred/declared effects and verify `IR_FUNCTION_EFFECT_MISMATCH` or `IR_UNDECLARED_EFFECT`.
2. Mutate a generated enum target to a wrong case index, nominal kind, target name, nonzero input, or wrong output and verify `IR_GENERATED_SIGNATURE_MISMATCH`; mutate an explicit `MakeEnumCase` operation away from its bound target and verify `IR_GENERATED_OPERATION_MISMATCH` or `IR_ENUM_CONSTRUCTION_TYPE`.
3. Execute two valid cases through distinct arms and assert outcomes and equality/inequality for values of the same enum type. A call to polymorphic `equals` across different enum nominal keys should be rejected by type checking rather than executed. Existing `tests/AgentLang.ValueInspection.Tests/Program.fs:247-250` already covers unknown case and wrong name; add wrong nominal kind and nested enum value only if those paths are not covered elsewhere.

These checks are acceptance coverage for the current IR boundary, not a request to broaden enum syntax or introduce a general malicious-IR test framework.
