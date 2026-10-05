# Next persistence milestone plan

Status: implementation plan, not delivered behavior. Implement after the typed-container milestone has passed review. Current `dictionary.agent` storage, task rollback, and scoped commits must continue to work during migration.

## Architecture

Introduce a small F# storage module for immutable source objects and a versioned project manifest. Keep word/type/test/example source as readable `.agent` text. A manifest records stable word IDs, current names, maturity, revisions, provenance, and source-object hashes. One atomic manifest replacement is the durable change boundary; independently updating a dictionary and a history file must not create conflicting authoritative state.

Write and validate immutable source objects before replacing the manifest. On reload, validate manifest version, object paths/hashes, unique IDs/names, full parsing, dependency/type/effect compatibility, and validator freeze constraints before exposing any dictionary state. Do not follow a manifest reference outside its object directory. A crash before manifest replacement can leave unreferenced objects, but must not expose half a revision.

Continue exporting canonical `dictionary.agent` for human inspection. After migration, treat this as an export of the authoritative manifest rather than an independent competing write path. If only a legacy dictionary exists, validate it completely and assign initial IDs/history at the first successful durable operation. Do not invent missing older revision contents.

A revision records its stable ID, name at that revision, revision number, canonical source, test/example source, maturity, task ID where present, actor, and timestamp. Timestamps and IDs are metadata; semantic logs remain ordered and deterministic for deterministic commands. A rename retains the ID and adds a revision rather than creating a new word identity.

## Integration boundaries

Runtime persistence uses its existing durable projection: unrelated candidate replacements and test edits cannot leak into an unrelated commit. Candidate inspection/evaluation remain in-memory. A task snapshot retains the starting authoritative manifest and in-memory state; abort restores both even after interim commits, while retaining task logs. Unreferenced revision objects can remain after abort but are not committed history.

Named snapshots store a validated manifest reference and source-object hashes. Save snapshots from committed state only, report excluded candidates, and reject restoring during an active task rather than silently losing its edits. Loading a snapshot restores types, words, tests, examples, maturity, stable IDs, revision history, and provenance; task research logs remain separate. Simulated provider state requires an explicit snapshot policy before claiming effect-state reproducibility.

Vocabulary maintenance is semantic and atomic. Rename rewrites AST calls and static list callbacks, attached test/example owners and bodies, and source generated from those objects; it revalidates the complete graph before persisting. Name collisions, primitive/generated-word changes, and frozen validator closure changes are rejected. Deprecation preserves callers and adds metadata visible in introspection. Replacement requires compatible signatures/effects or a validated caller rewrite; do not silently bind existing callers to an incompatible word.

## Acceptance and validation

- Reload two revisions and inspect/diff both, including names, tests, maturity and provenance.
- Rename a committed word with callers, container callbacks, tests and examples; preserve ID and behavior across a fresh process.
- Reject collisions, invalid caller rewrites and frozen-validator modifications without disk or in-memory changes.
- Commit one word while another replacement and failing metadata remain staged; reload only the intended durable changes.
- Abort after interim commits and maintenance edits; reload the exact earlier manifest state and retain the aborted task log.
- Save/load a named snapshot with nominal/container types, multiple words, tests/examples and library maturity; reject tampered, missing and escaping object references.
- Migrate a legacy dictionary without changing behavior; report prior unavailable revision contents honestly.
- Inject failure before manifest replacement; a fresh process sees the last complete state.
- `dotnet build AgentLang.sln`, `dotnet run --project tests/AgentLang.Acceptance`, the harness/domain checks then available, and `git diff --check`.

Update the requirement ledger and save a milestone report only after these behaviors are independently observed. Extend CLI/JSON commands without giving agents arbitrary storage-file editing.
