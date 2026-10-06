# Exact patches for the repeated agent comparison

Report 057 identifies whole-file replacement as a conventional request-byte
confound. Before repeating matched tasks, provide a normal small-edit operation.
This changes the external baseline tool, not language semantics or the archived
pilot-001 apparatus.

## Contract

`patch` requires exactly `path`, `expectedSha256`, `oldText`, and `newText`, all
strings. `oldText` must be nonempty and occur exactly once using ordinal matching
in the current valid UTF-8 file. Count overlapping matches too. Empty `newText`
permits deletion; insertion uses an existing unique context anchor. Missing and
ambiguous anchors return distinct structured errors. No fuzzy matching, newline
normalization, shell execution or arbitrary filesystem access is introduced.

Validate the file hash before finding the anchor. Preserve surrounding contents,
including BOM, CRLF/LF, whitespace and Unicode. Reject invalid UTF-8 output and
oversized output before writing. Reuse the existing confined atomic replacement
and final compare-and-swap check; output reports normalized path, new hash and
file bytes without echoing the entire source. Log one patch operation with the
existing metadata-only ordered event schema. Full replacement remains available
and keeps its existing contract.

## Scope and acceptance

One Luna/max worker owns Conventional Dispatcher and its direct/CLI tests.
Root owns protocol documentation, experiment preparation, reports and serial
validation. No frozen pilot primer, binary, fixture or trace is modified.

Test actual edits and subsequent reads, exact preservation of unrelated content,
stale hashes, missing/empty/overlapping-ambiguous anchors, invalid argument types
and extras, path confinement, output limits, ordered metadata-only logs, and
top-level/nested JSONL transport. Rejected patches must leave bytes and hash
unchanged and leave no temporary files. Existing replacement, validation and
CLI request-limit behavior must still pass.

Run the fresh full `pwsh -NoProfile -File scripts/Validate.ps1` Release gate.
Review focused results, versions/contracts and diffs before committing report
059 with evidence and publishing to private main/prototype. The earlier clean
milestone-058 CI evidence is retained separately. Once this tool is verified,
freeze a new primer and standardized launch prompts for a rotated repeated
Flat/Growing/Conventional sequence. No comparative saving is claimed from this
tool change alone.
