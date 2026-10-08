# Construction boundary investigation

Read-only investigation at c825e3e by construction_boundary_plan (Luna/max).

Existing refined nominal types only support primitive bases. Parser.fs, FlowParser.fs, FlowSyntax.fs, Compiler.checkScalarValidator and TypedIR reject a record base. Thus a validated wrapper around a complete record is not already available.

Generated scalar constructors run a pure base-value-to-Bool validator and preserve the validator binding through persistence. Generated record constructors currently package typed fields without a complete-record validator. Existing business boundary tests explicitly expose raw record construction paths that can bypass higher-level Customer/Product policy.

A future optional record validator should be enforced by the generated constructor and authoritative IR, including reload and native construction. Reusing primitive scalar wrappers for records is not merely syntax: native record handles carry exact type IDs and the native scalar representation assumes primitive payloads. The existing generic TypeSource.ValidatorTarget persistence field may be reusable, but Runtime currently rejects that target on records.

Affected future areas: Core type definitions, both parsers/formatters, compiler, typed IR, interpreter, persistence/reload and LLVM lowering. Require tests of valid/invalid complete values, pure validator contracts, nominal identity, bypass rejection, fresh reload and supported native conformance. Use isolated Debug builds to preserve frozen research binaries.

This milestone instead guarantees presence structurally in a populated delivery-plan record. It does not establish arbitrary cross-field relationships or replace the pending validator work. The new transition must derive its plan from its own Store rather than trusting an unrelated externally supplied plan.
