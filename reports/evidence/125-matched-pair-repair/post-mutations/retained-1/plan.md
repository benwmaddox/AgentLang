# Retained-1 Flow mutation controls

## Scope
Create two isolated copies of the closed retained-1 Flow actor under this directory. Keep the original actor, attached tests, metadata, inherited helper, pinned runtime, scorer, and other arms unchanged.

## Procedure
1. Record the original actor project inventory (relative path, size, SHA-256).
2. Copy the complete original actor into separate directories for the two predeclared target-body mutations.
3. Through the pinned CLI JSONL interface, read each copy's current revision and source, stage the single mutation with `replace:true` and `expectedRevision`, run `test` and `test-all`, then publish with `replace-word` only if the fixture gate accepts it. Preserve inherited tests and helper source exactly.
4. If replacement succeeds, run the frozen pair scorer on that mutated copy with a unique output path. A gate rejection counts as mutation detection; never score an unchanged candidate as mutated.
5. Verify mutation semantics, unchanged metadata/tests/helper, unchanged original inventory, and preserve full CLI requests/responses plus scorer output.

## Mutations
- `second-id`: leave the first helper call unchanged; construct the second Invoice using the first invoice identifier and the second invoice status before calling the existing helper.
- `same-content-write`: leave first processing unchanged; inline only second processing so an existing second reminder marker is read, written back byte-for-byte once, and returned unchanged. Keep the missing-marker and non-open behavior unchanged.

## Validation
Use the pinned runtime copied under `.agentlang/pair-repair-001/runtime/debug-artifacts`, direct CLI JSONL invocation shape used by scoring, exact expected revision from `describe`, and `.agentlang/pair-repair-001/scoring/score-pair.ps1` without `-RequireFullPass`. Compare original input inventory before and after.
