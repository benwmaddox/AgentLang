# Complete Flow project lowering

Status: opt-in Flow batch lowering is implemented as a compiler prerequisite. It does not change the default frontend or provide durable project editing/publication. Authored call-binding metadata remains a required follow-on before batch changes can be treated as a complete project update.

## Existing compiler authority

`Compiler.compileIrProgramWithSourceOrigins` already compiles all complete definitions in one `IrLoweringContext` and verifies the entire program. Every definition is checked against the same complete word dictionary. Forward calls therefore work when all real bodies and IDs are supplied. The verifier rejects self/mutual cycles with `IR_RECURSIVE_CALL_GRAPH`; preserve that current policy rather than implicitly adding recursion.

`FlowLowering.compileWord` lowers one candidate using the existing context, rejects existing names/IDs, inserts it, and recompiles. It cannot resolve forward Flow declarations or represent replacement. Detached body APIs compile eval/test bodies against a fingerprint-identical verified program; they do not add functions. There is no `compileIrWordAgainstProgram` API.

## Implemented batch path

`FlowLowering.compileBatchWords` accepts an immutable `Context` and an ordered, nonempty list of `FlowWordChange` values. Each change carries a `FlowWordDefinition` and explicit host revision intent: `Add(WordId, revision)` or `Replace(WordId, expectedRevision, revision)`. It returns lowered projections, the complete final context, one verified program, and source-site origins. Additions and replacements are the only supported changes in this slice.

Before it constructs the overlay, the API checks that base `Words` and `WordIds` have the same keys, IDs are globally unique and nonempty, revisions are synchronized, parameter metadata is well-formed, and base source-origin keys and values are exact and valid. It rejects duplicate proposed names, reused add IDs, missing or protected replacement targets, wrong IDs, stale revisions, non-advancing revisions, malformed host-built Flow names, open proposed signatures, unknown nominal types, and undeclared effect names. Replacements preserve the existing stable ID, status, and maturity, while keeping entry and definition revisions synchronized. New words begin as candidate project words.

Resolution uses a body-free signature catalog with ordered inputs and outputs, effects, primitive-versus-generated-versus-user kind, named parameters, stable identity, revision, and declaration span. It includes all base dictionary entries and every proposed Flow declaration before any body is lowered. Trusted `BuiltinOp` signatures keep their existing generic variable relationships; generated constructors and accessors remain closed nominal signatures, with record constructor parameter names derived from record fields. No placeholder `WordEntry` or executable body is created for a proposed word.

Every actual proposed definition is lowered against that same catalog. A shared source-marker allocator spans the batch. Final assembly contains only retained real base definitions and real lowered bodies. Origin keys are derived from zero-width markers in those final bodies; old replacement markers are pruned only after validating the entire input origin snapshot. The complete real context is then passed once to `Compiler.compileIrProgramWithSourceOrigins`. That compiler remains authoritative for final body types, effects, scalar validators, call cycles, IR verification, and exact final origins.

This supports same-batch forward ordinary calls, dot-stage selection, static callbacks, generated constructors/accessors, scalar and vector signatures, and existing named-argument behavior. It rechecks untouched word bodies under the final signature set, so an incompatible replacement fails atomically before a result context is returned.

The current result is an opt-in compilation artifact. It does not persist changes, assign project storage IDs, edit a manifest, publish a word, compile attachments, or provide the authored call-binding sidecar described below. A passing batch compile therefore does not establish safe durable publication.

Flow named arguments need the complete external parameter-name catalog because `WordDefinition` has no parameter names. Validate names and arity rather than relying on `Map.ofList` to silently replace duplicates. Existing record constructor parameter names continue to derive from fields.

## Required follow-on: authored call bindings

The batch lowerer currently emits string targets; verified IR later resolves those into primitive/generated/user identities. A later project-editing slice must also capture each authored call's owner stable ID and revision, structural AST site, source span, call form, requested name, and selected target ID and revision. It must cover ordinary calls, selected dot stages, static list callbacks, and generated conversions/constructors, then verify the sidecar against final IR targets and projected source maps.

Persist call bindings with their authoritative source revision. Before vocabulary changes, re-resolve unchanged retained source against the complete proposed dictionary and compare target identities. Report affected caller/spans if a target changes or becomes ambiguous. Recompilation alone is insufficient: an old dot call can still type-check while silently selecting a different word. This rebinding/collision check is intentionally not claimed by the current API.

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

## Signature-only lowering boundary

The existing Flow resolver makes its candidate decisions from word names,
ordered input/output types, declared effects, builtin/generated kind, named
parameter metadata, source spans, and stable target IDs. It does not inspect
callee bodies to infer signatures. `compileBatchWords` uses a closed,
body-free signature catalog, lowers all real Flow bodies, and defers final
definition/scalar-validator checks to one
`Compiler.compileIrProgramWithSourceOrigins` call over the complete real
dictionary. The catalog contains no executable placeholder definitions.

The current API validates the supplied base dictionary, closed proposed Flow
signatures, stable-ID coverage/uniqueness, parameter names and arity, and the
declared effect vocabulary. There is no evidence for adding a generic compiler
inference API; the existing Flow resolver handles the tested forward-reference
cases through its signature catalog.

## Acceptance and limits

The implemented prerequisite is exercised with reverse-order forward ordinary/dot/callback calls, primitive generics, generated targets, named-argument ordering, positional output-vector destructuring, replacement identity/revision preservation, incompatible retained callers, aggregate effects, validators, cycles, malformed catalogs, and exact origin pruning/rejection. It does not yet exercise attachments in the batch API.

The focused Flow suite passes with 473 assertions. The fresh Release validation gate also passed all 24 checks locally: solution build with zero warnings/errors, Flow (473 assertions), Flow lint (68), IR (102), interpreter (22), language acceptance (34 groups / 583 assertions), storage (9 groups / 105 assertions), and the other acceptance, fixture, projection, parser-limit, and whitespace checks. The captured run is [the local validation evidence](../reports/evidence/036-flow-batch-validation.json). That evidence records revision `84611d5` with a dirty working tree, so this is local validation of the milestone state, not a clean committed-revision CI result.

Before durable project editing, add the authored binding sidecar and unchanged-call rebinding test, then compile test/example attachments only against the returned final verified snapshot with exact, disjoint source origins. Rebinding must detect unchanged dot-stage collisions before publication. Durable work must also add version/hash/history/rollback behavior and enforce the host-assigned identity boundary.

Durable publication adds the storage and history cases in [FLOW-DURABLE-INTEGRATION.md](FLOW-DURABLE-INTEGRATION.md). Exact root-word addressing remains a prerequisite before that integration.
