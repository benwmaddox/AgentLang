# Evidence package 115

This package records the versioned-help adoption replay and its separate
extra-write mutation check. The copied source snapshots and raw traces preserve
the exact bytes captured for scoring. `SHA256SUMS.txt` hashes every other file
in this package using lowercase SHA-256 and POSIX relative paths.

- Root metadata records the versioned-help dispatch freeze, original-condition
  freeze, provenance, pinned runtime hashes, and the copied verifier review.
  Runtime binaries are intentionally omitted.
- `trial/` contains both prompt versions, the prompt-only diff and scope
  summary, the actor's exact final message, and its raw broker trace and parsed
  summary.
- `oracle/` contains the frozen report 111 verifier and the same
  metadata-adjusted verifier used for scoring.
- `scoring/` contains the 144-check independent behavioral result, exact
  committed definition/tests/example, actor inventory proofs, the byte-identical
  disposable mutant input, raw requests/responses/trace, and structured
  mutation and terminal summaries.

The mutation copy remained separate from the actor project. No actor project
files or runtime binaries are included.
