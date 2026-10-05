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

## Exact root addressing proposal

Add an absolute-root spelling before durable source versions are frozen: `::identity(value)` and `items.map(::identity)` select the exact dictionary key `identity`. Existing `identity(value)` and `word identity` keep suffix lookup; `ns::identity(value)` retains namespace qualification. Avoid a pseudo-namespace such as `root::identity`, which could collide with a real word named `root.identity`.

Preserve the qualification kind in the AST instead of stripping the leading marker into an ordinary short name. Extend parser lookahead, rendering, resolver, callback reference validation, and all AST consumers together. With both `identity` and `ns.identity` present, tests must prove exact-root and namespace forms select their own identities while both short forms remain ambiguous. This spelling is proposed, not implemented by the output-vector slice.

## Acceptance

Prove forward calls using declarations in the opposite order; preserve IDs on replacement; reject self/mutual cycles, duplicate names and invalid identity/catalog coverage. Validate multiple Flow words and attachments with disjoint projected markers, reject missing/extra/stale origins, and compare sidecar identities to verified IR. Exercise an unchanged dot-stage collision before publication and require explicit qualification rather than silent rebinding.

Run focused Flow, IR, interpreter, and language acceptance suites, then the full fresh Release validation gate. Durable publication adds the version/hash/history/rollback cases in [FLOW-DURABLE-INTEGRATION.md](FLOW-DURABLE-INTEGRATION.md). Exact root-word addressing remains a prerequisite before that integration.
