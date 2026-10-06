# Durable Flow source integration

Status: architecture plan plus an implemented and locally verified manifest-v2 storage slice. Storage preserves Flow format and authored call-binding metadata, while Runtime still rejects Flow revisions before parsing the Stack export; authoritative Flow loading, writing, and compilation remain unimplemented.

## Current repository evidence

- `Storage.SourceRef` contains an object kind and lowercase SHA-256 hash. `Storage.SourceObject.Content` is hashed as its exact UTF-8 bytes. Existing kinds already include project, word, type, test, and example source.
- `CURRENT` and named snapshots remain format 1. Manifest readers accept formats 1 and 2. Version-1 revisions default to Stack syntax version 1 with no call bindings; version-2 revisions store `sourceFormat` and canonical authored call bindings. The exact v1 golden manifest format remains available for compatibility.
- `Storage.commit` checks that the supplied export bytes equal the manifest's project-source object, writes immutable objects and the manifest, then atomically replaces `CURRENT`. Export refresh is post-commit and can report a warning. If `CURRENT` is absent, `Storage.load` exposes `dictionary.agent` as read-only `LegacyAuthority`; the first successful manifest commit migrates the live project while recording baseline revision objects.
- Runtime builds `dictionary.agent` with `Source.renderWord`, `Source.renderTest`, and `Source.renderExample`; the current renderer emits stack/RPN bodies and embeds host maturity/revision metadata. New Runtime commits write manifest format 2 and mark their revisions Stack/1 with no call bindings. `Runtime.validateStoredProject` rejects any Flow revision, including historical revisions, before parsing the aggregate with `Parser.parse`; Flow loading is not wired yet.
- `DictionaryState.WordIds` maps current names to stable IDs. IDs are generated when no manifest head supplies one; rename moves the existing ID. Runtime's rename path walks `Expr` bodies with the RPN-only `Source.renameReferences`, rechecks callers/tests, then persists.
- Tests/examples already have content-addressed revision references. `Runtime.TaskSession` snapshots in-memory dictionary state, the executable snapshot, storage authority/export bytes, manifest identity, and virtual provider/clock state. Abort uses `Storage.restore`, which advances the storage generation without manufacturing revisions. Named snapshots retain the manifest hash plus virtual files and clock.
- The Flow foundation is opt-in and not used by Runtime. `FlowWordDefinition` retains named parameters, authored Flow text, and a syntax-version integer. `FlowLowering.Context.ParameterNames` is needed because compiler `WordDefinition` inputs do not retain names. Runtime state has neither a Flow-source map nor Flow-aware project parsing. `Source` rename/render/history operations understand only the stack AST. Origin-aware compiler test/example overloads now pass the focused IR and integrated gates (report 026). Runtime attachment parsing and durable Flow authoring remain unwired; the existing legacy wrappers retain their empty-origin behavior.
- `Protocol` is a JSON-lines parser and dispatcher into `Runtime.Engine.Dispatch`; it does not select a frontend. The current `eval` and `define` routes parse stack syntax.

## Recommended durable model

Make immutable authored source objects the authority and treat `WordDefinition` and verified IR as derived executable projections. Do not persist Flow by rendering its lowered `Expr` list as if that RPN were the authored source. `dictionary.agent` remains a deterministic, human-readable export, but the manifest-linked Flow word/test/example objects must contain the actual authored Flow text.

Add explicit `sourceFormat: { frontend, version }` metadata to each word revision, where `frontend` is `stack` or `flow`. Tests and examples inherit the format and grammar version of their owning revision. Add an explicit version for type source objects if their grammar later evolves independently. The current word head continues to select a stable ID and current revision; historical revisions retain their own source format. This allows a Flow revision to supersede an RPN head under the same stable ID while keeping the original RPN source object and hash untouched. It also permits an explicit, temporary mixed project during migration without any parse-error fallback. Flow remains opt-in until the complete conformance gate passes; only then should the default frontend change.

Use manifest schema version 2 for these fields. Keep the `CURRENT` pointer and named snapshot formats at version 1. Version-1 pointer, snapshot, manifest, and source-object bytes and hashes must remain byte-for-byte compatible; the manifest reader accepts v1 and v2, while new writes use v2. A v2 manifest may continue referencing old stack source objects; add frontend/version metadata around those references instead of rewriting their object bytes. Tests and examples inherit their revision's grammar; give them separate format metadata only if they later need an independently evolving grammar. Unsupported versions return structured storage errors.

Freeze Flow source encoding before the first durable commit: canonical UTF-8 without BOM, LF newlines, deterministic section/object ordering, and a documented final-newline rule. Hash the exact rendered bytes. The export must include an explicit per-definition frontend marker where it aggregates source from different revisions, but Runtime must select parsers from manifest metadata, never by trying Flow and falling back to stack. The storage object remains the source of truth; the export is validated against a deterministic assembly of those objects.

Keep Flow authorship separate from the legacy-compatible compiler projection. Add an authored-definition union keyed by stable `WordId`, for example `StackV1 of WordDefinition | FlowV1 of FlowWordDefinition`, with name-to-ID indexing from manifest heads. Keep authored Flow tests/examples similarly identity-bound. `RuntimeSnapshot` can continue holding compiled `WordEntry` values, compiler context, verified program, and executable test/example bodies, but it must also retain the exact authored objects, parameter catalog, and source-origin maps used to produce that snapshot. The lowerer's `WordDefinition.SourceText` is never used as the persisted source for a Flow head.

At load, dispatch directly from `WordRevision` source metadata to the matching parser. Parse and validate all objects into a proposed authored state, build the complete compiler context and Flow parameter catalog, resolve calls against the complete proposed dictionary, lower every Flow definition, compile the verified program, and compile source-aware tests/examples. Validate heads, revision metadata, type declarations, dependencies, effects, and source projections before activating the new runtime snapshot. A failed parse or compile leaves the active state untouched. A missing `CURRENT` remains the explicit read-only legacy import path; a malformed Flow object never triggers legacy parsing.

The whole-project compilation path must first predeclare every name, complete signature, preserved word ID, and parameter name, then resolve and lower every body against that closed proposed vocabulary. The implemented `compileBatchWords` prerequisite supports forward references and checked additions/replacements through a body-free signature catalog and one final real-body compile (report 036). The incremental `compileWord` path still refuses existing names. Neither API alone is complete durable project editing: authored binding stability, unchanged-source re-resolution and complete attachment assembly remain required. Do not verify empty placeholder bodies or allocate new IDs to sidestep replacement checks. Allocate globally disjoint origin markers across all lowered words and rebuild the projection when edits or deletions change the source set; do not infer the next marker from `Map.Count` when marker IDs may have gaps.

At publication, derive a complete proposed source state first. Run the existing candidate, transitive durable-caller, validator-closure, test, and library-coverage gates against that exact verified program. Persist Flow definition/test/example bytes and a v2 manifest only after those checks pass. The current manifest's content-addressed revision refs preserve provenance and attached cases. Failed gates must leave the old `CURRENT` pointer authoritative; orphaned immutable objects are acceptable because they are unreachable.

### Affected files and APIs

- `Storage.fs`: versioned v1/v2 manifest serialization/parsing; per-revision frontend/version metadata; v1 hash compatibility; retain current pointer/restore/CAS semantics. Keep the current source-object kinds unless a separate syntax version cannot be represented safely in revision metadata.
- `FlowSyntax.fs` / `FlowParser.fs` / `FlowSource`: parse a complete project/source object set, render canonical Flow definitions/tests/examples, and expose syntax version in source metadata. Add tree-based rename operations. Keep type declarations and validators explicit.
- `FlowLowering.fs` / `Compiler.fs`: extend the implemented word-batch prerequisite with authored bindings and complete project attachment assembly. The origin-aware `compileIrTestWithExpectationAgainstProgramWithSourceOrigins` and `compileIrExampleAgainstProgramWithSourceOrigins` APIs are implemented and covered by report 026; Flow-authored attachment parsing/lowering is implemented in reports 031/032. Use them with the full retained context origins plus disjoint attachment markers against the exact verified snapshot; the legacy wrappers intentionally retain empty-origin behavior. Runtime/durable integration remains separate. Compile pure expected expressions as separate isolated bodies and never merge their traces into tested-word coverage.
- `Runtime.fs`: extend `DictionaryState` with authored source by stable ID; retain compiled words only as derived/staged views. Update `sourceFor`, `currentManifestFor`, `parsedState`, `validateStoredProject`, `loadProject`, `commitCandidates`, replace, rename, history/diff, snapshot load, and task rollback. `describe` must read Flow parameter names; `source` and history must return actual authored Flow, not translated RPN.
- `Source.fs`: retain explicit legacy render/rename APIs and add Flow-aware render/rename/history helpers. Preserve old revision bytes. Shared name rewrites must visit calls, static callback references, test/example bodies, scalar validators, and ownership references.
- `Protocol.fs` / CLI: add explicit opt-in Flow operations or a strictly validated frontend field for `eval`/`define` during stage 3. Keep stack parsing on a named legacy route. Change the default only in the later cutover stage after the complete Flow acceptance gate.
- `Runtime` and persistence tests: cover versioned load, compile-before-activate, state rollback, object/hash equality, caller re-resolution, and host-controlled library gates. README, primers, and prompt changes belong to the later cutover stage.

### Resolution and mutation rules

Flow call resolution must remain stable as the vocabulary grows. Retain a per-authored-call binding from source site to target stable `WordId` in the compiled/source metadata. Before committing any new definition, replacement, or rename, re-resolve all retained Flow source against the complete proposed dictionary and compare those bindings. If a short dot stage changes target or becomes ambiguous, abort publication with affected source locations; require the author to qualify/rewrite those call sites. Do not select a replacement by dictionary order or silently bind an old call to a new word.

Rename keeps the definition's stable ID, increments its revision, and updates exact ordinary calls, callback word references, attached test/example owners and bodies, and validator references. Dot stages are not plain name strings: use the stored resolved-call binding to identify affected sites. Rename may AST-rewrite a resolved dot call to an explicit qualified ordinary call with the receiver as input one, provided it preserves written argument order, evaluates the receiver exactly once, and passes verification. Reject the rename if that safe transformation cannot be established. Recompile all changed definitions/tests and affected durable callers before publication. Replacement similarly preserves the target ID and runs the current transitive-caller tests and library gates against the proposed Flow snapshot.

`WordRevision` parameter names remain part of the canonical Flow definition bytes. On load, compare parsed parameter name/type pairs to the compiled input signature and the manifest head/revision. Do not infer or synthesize names from stack positions. Host-owned maturity, revision, actor, task, and timestamp stay in manifest metadata rather than being inserted into user Flow source. Tests/examples retain exact Flow source references in the revision; expected-expression bodies are pure and independently source-mapped.

## Lifecycle acceptance

1. **Version and identity compatibility:** load a frozen v1 fixture and verify its `CURRENT`, manifest, object hashes, history, and RPN bodies remain unchanged. Load it only through the explicit stack parser. Unknown versions and malformed Flow source return structured errors without parser fallback. Create a Flow revision under the same word ID and verify its source hash covers exact Flow bytes while the older RPN revision hash remains identical.
2. **Authoritative Flow reload:** define and commit a named-parameter Flow word with a test and example. Verify `dictionary.agent`, manifest project source, word revision object, test/example refs, parameter names, syntax version, maturity, and revision agree. Start a fresh `Engine`; verify `source`, `describe`, dependencies/callers, tests, and examples expose Flow objects and the recompiled verified behavior. Confirm no persisted definition object contains a translated RPN body for a Flow revision.
3. **Resolution safety:** add a vocabulary change that collides with a retained dot stage and prove commit rejects it with exact caller/span data until the source is explicitly qualified. Rename a word and confirm the same ID moves to the new name, direct calls/callbacks and attached test/example refs update, and changed callers receive new revisions only after passing tests. Exercise ambiguous and changed dot targets.
4. **Commit and library gates:** test failed own tests, failed durable-caller tests, transitive scalar-validator changes, undeclared effects, missing attached Flow tests, and incomplete raw branch coverage. In each failure case, compare pre/post `CURRENT` authority and semantic state; only accepted source objects become reachable. Verify pure expected-expression code is isolated and contributes no tested-word coverage.
5. **Task and snapshot rollback:** begin a task, stage Flow definitions/tests, then abort. Verify the authoritative manifest/hash and exact export/source bytes return to the captured state, the live generation only advances, and no Flow revisions become current. Save/load a committed snapshot in a fresh engine and verify Flow source, IDs, tests/examples, virtual files, and clock restore together. Preserve task logs as provenance outside the restored project authority.
6. **Storage failure and deterministic encoding:** inject failure before pointer replacement and verify the old authority remains live; inject export refresh failure and verify success plus warning while fresh load still compiles the manifest. Verify same logical Flow sources render identical UTF-8 bytes and object hashes on Windows/Linux, independent of platform newline settings.

7. **Temporary and maturity lifecycle:** keep temporary Flow definitions outside the manifest; verify discard and task abort leave no durable references. Promotion preserves the temporary word's stable ID and creates its first durable revision. Confirm project/library maturity applies the existing host-controlled test and branch-coverage gates to the proposed complete Flow program.

## Validation after integration

```powershell
dotnet run --project tests/AgentLang.Storage.Tests -c Release
dotnet run --project tests/AgentLang.Source.Tests -c Release
dotnet run --project tests/AgentLang.Flow.Tests -c Release
dotnet run --project tests/AgentLang.IR.Tests -c Release
dotnet run --project tests/AgentLang.IR.Interpreter.Tests -c Release
dotnet run --project tests/AgentLang.Acceptance -c Release
dotnet run --project tests/AgentLang.Harness.Tests -c Release
./scripts/Validate.ps1 -Configuration Release -ReportPath .agentlang/reports/flow-durable-validation.json
```

The commands above describe broader Flow integration work; current schema-slice
validation is recorded at the end of this document.

## Resolved recommendations and remaining audit

The migration uses explicit per-revision frontend metadata. Stack-authored and Flow-authored current heads may coexist during gradual conversion; each historical revision keeps its own format. Flow remains opt-in until the full conformance gate passes, after which the default may move to Flow.

Manifest v2 adds `sourceFormat: { frontend, version }` to each word revision. Tests and examples inherit the owning revision's grammar. Add independent test/example format metadata only if their grammar later evolves separately. Keep v1 pointer, snapshot, manifest, and source-object bytes and hashes exactly compatible; the migration must not rewrite old RPN objects.

Rename may rewrite affected resolved dot calls to qualified ordinary calls with the receiver first, while preserving the authored argument order and evaluating the receiver once. It must reject a rename when that transformation cannot be proven safe, using stable `WordId` bindings and source spans to identify affected sites.

The first schema slice preserves frozen v1 bytes and adds version-aware v2 serialization, storage-neutral per-revision format/binding DTOs, membership and bound validation, and an early Runtime rejection for unsupported Flow loading. Its focused Storage acceptance passed 14 groups and 229 assertions. The remaining implementation work is manifest-selected authored-source parsing, exact binding regeneration/verification against the compiler, Flow-aware project export and edits, and fresh-process coverage for rename, rollback, snapshot restore, branch coverage, and library gates. These remain future acceptance gates; this schema milestone alone does not make Flow a durable Runtime frontend.

## Concrete schema and compatibility prerequisites

Manifest, CURRENT, and snapshot versions are now independent contracts: manifest readers accept 1/2, while CURRENT and named snapshots remain at 1. Revision parsing receives its enclosing manifest version. Version 1 synthesizes Stack/1 metadata and an empty binding list; a v1 writer omits the added fields only for those defaults and rejects meaning-bearing metadata. New Runtime commits use manifest version 2, while an explicit Storage v1 commit remains available for compatibility and byte-preserving round trips. The nested v2 shape is `sourceFormat: { frontend, version }`; unsupported manifest versions are rejected before parsing project references or revision metadata.

The literal v1 prerequisite is implemented in `tests/AgentLang.Storage.Tests/fixtures/v1-golden`: CURRENT, manifest and referenced word/test/example/project objects have six pinned hashes independent of production serialization. Load, history, capture/restore and named snapshot restore preserve exact bytes and manifest identity; reports 032/036 record passing storage checks (9 groups / 105 assertions). This proves current v1 behavior, not v2 compatibility. Later v2 tests must add a revision under the same stable word ID while every historical v1 reference and object hash remains unchanged.

### Audited version boundaries

The storage implementation has three independent serialized boundaries with
separate version contracts. Manifest versioning is now independent from the
version-1 `CURRENT` pointer and named snapshot envelopes:

| Boundary | Current implementation | Required migration |
| --- | --- | --- |
| Manifest | `Storage.manifestNode`, `parseManifest`, `parseWordRevision`, `validateManifest` | Implemented: parse/write 1/2 by manifest version; v1 defaults to Stack/1 and no bindings; v2 includes revision format and binding metadata |
| CURRENT authority pointer | `Storage.pointerNode` and pointer parsing | Keep version 1 and existing authority/generation encoding |
| Named snapshot | `Storage.snapshotFileNode`, `parseSnapshotFile` | Keep version 1; its referenced manifest may be 1 or 2 |
| Runtime publication | `Runtime.currentManifestFor` | Implemented for current Stack-only Runtime writes: manifest v2, Stack/1, empty bindings, and unchanged historical source references |
| Runtime source export/load | `Runtime.sourceFor`, `serializeWord`, `parseProjectSource` | Flow guard implemented before aggregate parsing, including historical revisions; manifest-selected Flow parsing and authored-source export remain future work |

F# compilation order is also a boundary: `Storage.fs` precedes `TypedIR.fs`,
`Compiler.fs` and `FlowLowering.fs`. Persisted binding DTOs must therefore not
depend on `FlowLowering.FlowCallBinding` or verified IR types. Use storage-neutral
identity/source/path representations and an explicit validated conversion at
the Runtime/compiler boundary. `FlowSyntax.fs` already precedes Storage, but
sharing structural path types does not make compiler-owned target resolution a
storage responsibility. Storage validates schema, ownership and exact object
references; Runtime validates the authored binding against verified semantics.

The focused Storage acceptance now verifies a version-1 snapshot referring to a
version-2 manifest, frozen version-1 compatibility, history migration under the
same stable word ID, and rejection of unsupported manifest versions before
version-specific revision fields. It passed 14 groups and 229 assertions; see
[the acceptance report](../reports/044-manifest-v2-acceptance.md). The literal
v1 object hashes remain unchanged.

The storage-neutral target representation distinguishes user, primitive and
generated targets with a stable string identity; it does not persist the target
revision as a rebinding key. Runtime still checks each newly compiled IR call's
exact revision. Bound binding counts, structural path depth/indexes and identity
text before recursive consumers. Validate source membership and duplicate
`(source reference, structural site)` keys in Storage, and account for binding
metadata in capacity limits. Missing or wrong-shaped required wire members use
the existing `STORAGE_INVALID_JSON` classification; semantic tag, version,
membership and compatibility violations return their structured storage error.
Only parsed/verified semantics can establish exact site completeness. A
version-1 serializer must reject Flow metadata or nonempty bindings rather than
omit them and publish a different meaning.

Persist resolved call bindings explicitly in v2 revision metadata (an ordered `callBindings` list), rather than assuming they can be recovered from a newly expanded vocabulary. Each binding contains its authored source reference (kind and hash), body role and structural AST path, call form/requested name, and closed target kind/stable identity; the containing word ID/revision plus exact source reference scopes the key. Cover definition, test actual/expected-expression, and example actual bodies, and require each source reference to belong to that revision. Site keys identify calls within the exact authored object, not regenerated IR ordinals or source line numbers. Storage validates unique keys, source/role/case compatibility, bounded path segments, and closed tags; Runtime/compiler verification of exact site coverage and target identity remains future work. Stack revisions use no Flow binding entries. A binding list is written deterministically by source reference then site key. Changed source must receive newly verified bindings; unchanged retained source must match its persisted list before publication. Historical revision bindings remain immutable.

For v2 loading, treat aggregate ProjectSource as a deterministic human-readable export projection of manifest-referenced objects, not a second parser authority. Explicit frontend markers make the mixed export readable; parser dispatch uses each revision's sourceFormat. Rebuild and byte-compare the aggregate after parsing authoritative type/word/case objects and validating metadata. The manifest-selected project object still participates in the existing exact export check at commit. Missing CURRENT retains the existing explicit legacy import path.

### Attachment binding schema handoff

Attachment keys include stable owner ID, test/example kind and case name. Within
the exact source object, distinguish actual and pure expected-expression bodies
before the structural AST path. This distinction is part of the site key even
when both expressions happen to have the same span. Definition bodies use their
own role. Storage checks compatible source kinds, revision source membership
and unique `(source, case, body role, path)` keys; the compiler/Runtime proves
exact parsed site coverage and semantic correspondence.

Compiler events retain emitted IR order for reconciliation. The durable list's
canonical source-reference/site-key ordering is a serialization contract, not
execution order: execution remains defined by semantic IR. Runtime converts
between keyed metadata and compiler traversal without interpreting list position
as a call identity. Binding strings, tags, indexes, spans and path depth need
closed validation and explicit bounds; a static DTO must not introduce unchecked
arbitrary target kinds or parser fallback.

The manifest-v2 storage slice updates Storage's version-aware parser and the
Runtime manifest builder; the Storage test fixture and migration cases passed
the focused acceptance run (14 groups, 229 assertions; see report 044). V1
parsing synthesizes Stack/1 plus an empty binding
list, and v1 serialization omits the added fields only when those defaults
hold. Runtime writes v2 for new commits while preserving historical references.
The compiler attachment work supplies transient bindings but does not yet make
Runtime load or commit Flow-authored definitions.

## Current implementation validation

The Core project rebuilt in Release with zero warnings or errors in 16.77
seconds; the focused Storage suite passed all 14 groups and 229 assertions. The
complete local Release gate passed all 25 checks, with zero build warnings or
errors. Independent source review found no material issue. See [the Core build
evidence](../reports/evidence/042-first-core-build.json), [focused Storage
evidence](../reports/evidence/042-ninth-focused-storage.json), [full gate
evidence](../reports/evidence/042-durable-manifest-validation.json), and
[report 042's attempt chronology](../reports/042-durable-manifest-integration.md).
Committed CI remains pending.
