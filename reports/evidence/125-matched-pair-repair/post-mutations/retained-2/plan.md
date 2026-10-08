# Retained-2 Flow mutation controls

## Scope
After the retained-2 actor is closed, inspect only its persisted target revision/source through the pinned CLI. Proceed only if the persisted target is already the corrected pair implementation. Keep the original actor, tests, metadata, inherited helper, pinned runtime, scorer, and all other arms unchanged.

## Procedure
1. Read and record the target `describe.revision`, target source, helper source, and attached tests through the pinned CLI. If persisted state is not corrected, stop and report it without manufacturing a baseline.
2. Record the original actor inventory, then copy the complete actor into separate owned directories for the two predeclared target-body mutations.
3. Use pinned CLI JSONL with `replace:true` and the exact current `expectedRevision`; run `test`, `test-all`, then `replace-word`, preserving all attached tests and helper source.
4. Treat replacement gate rejection as detection. If replacement succeeds, run the frozen pair scorer on that mutated copy with a unique output path and without `-RequireFullPass`.
5. Verify mutation semantics, unchanged metadata/helper/original inventory, and preserve full requests/responses and any scorer output.

## Mutations
- `second-id`: leave first processing unchanged; build the second Invoice with the first invoice identifier and second status for the existing helper call.
- `same-content-write`: leave first processing unchanged; inline second processing so an existing marker is read, written back unchanged exactly once, and returned unchanged; preserve missing-marker and non-open behavior.

## Validation
Use only `.agentlang/pair-repair-001/runtime/debug-artifacts/AgentLang.Cli.dll` and the frozen `.agentlang/pair-repair-001/scoring/score-pair.ps1`. Do not alter tests to force publication.
