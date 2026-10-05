# Complete Flow project lowering

Status: read-only compiler investigation and implementation contract. No batch Flow API is implemented by this document. Extend the frontend through this path before durable storage/default cutover; do not treat per-word compilation as a replacement for a complete proposed snapshot.

## Existing compiler authority

`Compiler.compileIrProgramWithSourceOrigins` already compiles all complete definitions in one `IrLoweringContext` and verifies the entire program. Every definition is checked against the same complete word dictionary. Forward calls therefore work when all real bodies and IDs are supplied. The verifier rejects self/mutual cycles with `IR_RECURSIVE_CALL_GRAPH`; preserve that current policy rather than implicitly adding recursion.

`FlowLowering.compileWord` lowers one candidate using the existing context, rejects existing names/IDs, inserts it, and recompiles. It cannot resolve forward Flow declarations or represent replacement. Detached body APIs compile eval/test bodies against a fingerprint-identical verified program; they do not add functions. There is no `compileIrWordAgainstProgram` API.

## Required batch path

1. Validate the complete proposed source set, rejecting duplicate names and invalid named-parameter catalogs before building maps. Retain ordered input/output signatures, declared effects, and stable target identities.
2. Build a type-distinct, signature-only resolution catalog for trusted/generated/stack words plus every proposed Flow word. It contains no executable placeholder body. Refactor Flow resolution to consume that catalog while preserving name/input resolution and scalar/vector output checks.
3. Lower every actual Flow body against the complete catalog. Capture resolved authored calls and thread one source-marker allocator through the whole batch. Replacement removes the old body before final assembly; preserve existing stable IDs and host-assigned revision metadata.
4. Assemble the final context from real lowered definitions and retained real base definitions. `WordIds` must cover the exact word-name set with globally unique, nonempty IDs. Generated and primitive effective names remain included. The host/storage boundary assigns new IDs once.
5. Rebuild the exact origin map for zero-width markers in the final bodies, dropping markers of deleted/replaced bodies. Invoke `compileIrProgramWithSourceOrigins` once. Signature-only catalog entries must never become executable compiler authority.
6. Compile each test/example only against that final verified snapshot, with full context origins plus disjoint actual/expected case markers. Preserve pure expected-expression and coverage isolation.

Flow named arguments need the complete external parameter-name catalog because `WordDefinition` has no parameter names. Validate names and arity rather than relying on `Map.ofList` to silently replace duplicates. Existing record constructor parameter names continue to derive from fields.

## Authored call bindings

The current lowerer records string targets; verified IR resolves them into primitive/generated/user identities. A batch result must also capture each authored call's owner stable ID, syntax-site identity/span, and selected target identity. Cover ordinary calls, selected dot stages, static list callbacks, and generated conversions/constructors. Verify the sidecar against the final IR's resolved targets and projected source map.

Persist call bindings with their authoritative source revision. Before vocabulary changes, re-resolve unchanged retained source against the complete proposed dictionary and compare target identities. Report affected caller/spans if a target changes or becomes ambiguous. Recompilation alone is insufficient: an old dot call can still type-check while silently selecting a different word.

IR `SourceSiteId(owner, ordinal)` joins final source maps and operations, but its regenerated ordinal alone is not a stable authored-site identity across revisions. Rename preserves target identity and uses bindings to rewrite the appropriate source sites, followed by complete re-verification and existing caller/test/library gates.

## Exact root addressing

The Flow frontend now preserves an absolute-root target as a distinct AST node
and callback-reference qualification. `::identity(value)` and
`items.map(::identity)` select only the exact dictionary key `identity`;
`identity(value)` and `word identity` retain suffix lookup, and
`ns::identity(value)` retains namespace qualification. Absolute-root targets
contain one simple identifier. No pseudo-namespace is introduced, and exact
root calls do not consider generated aliases or suffix candidates.

The parser, source renderer, lowering resolver, callback validation, and lint
traversal retain this qualification through their respective stages. Manual
ASTs receive the same shape validation before rendering or lowering. Focused
acceptance checks cover same-suffix identities, callback signatures/effects,
root calls with named arguments, receiver chaining, attachments, and spans;
their validation results are recorded in report 033.

## Future signature-only lowering boundary

The existing Flow resolver makes its candidate decisions from word names,
ordered input/output types, declared effects, builtin/generated kind, named
parameter metadata, source spans, and stable target IDs. It does not inspect
callee bodies to infer signatures. A future complete-snapshot lowerer can use a
closed signature-only resolution catalog for these decisions, lower all real
Flow bodies, and then defer final definition/scalar-validator checks to one
`Compiler.compileIrProgramWithSourceOrigins` call over the complete real
dictionary. The catalog must not contain executable placeholder definitions.

Before lowering, that future boundary should validate the closed type catalog,
stable-ID coverage and uniqueness, parameter names and arity, and the declared
effect vocabulary. There is no evidence yet for adding a generic compiler
inference API; add one only if a focused batch-lowering case cannot be handled
through the existing Flow resolver. This is a design contract for durable
batch lowering, not a claim that a complete-batch API exists today.

## Acceptance

Prove forward calls using declarations in the opposite order; preserve IDs on replacement; reject self/mutual cycles, duplicate names and invalid identity/catalog coverage. Validate multiple Flow words and attachments with disjoint projected markers, reject missing/extra/stale origins, and compare sidecar identities to verified IR. Exercise an unchanged dot-stage collision before publication and require explicit qualification rather than silent rebinding.

Run focused Flow, IR, interpreter, and language acceptance suites, then the full fresh Release validation gate. Durable publication adds the version/hash/history/rollback cases in [FLOW-DURABLE-INTEGRATION.md](FLOW-DURABLE-INTEGRATION.md). Exact root-word addressing remains a prerequisite before that integration.
