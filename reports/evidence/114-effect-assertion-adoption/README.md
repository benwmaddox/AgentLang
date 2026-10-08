# Evidence package 114

This package records one effect-assertion adoption probe. Files were copied
verbatim from the frozen trial artifacts unless their names identify a
summary. `SHA256SUMS.txt` hashes every other file in this package using
lowercase SHA-256 and POSIX relative paths.

- `dispatch-freeze.json`, `provenance.json`, and `runtime-pin.json` record the
  dispatch inputs, starting project, source checkpoint, and pinned runtime
  file hashes. Runtime binaries are intentionally not included.
- `trial/` contains the frozen prompt, the actor's exact final message, the raw
  27-exchange broker trace, its summary, and the independent help-discovery
  review.
- `oracle/` contains the frozen report 111 verifier, the metadata-adjusted
  copied verifier, and the read-only reviews of the adaptation.
- `preflight/` contains the 52-check preflight, positive and negative controls,
  the 143-check positive and 96-check negative verifier results, protocol
  captures, and logs.
- `actor/` contains the independent 146-check behavioral result, exact final
  source/test evidence, before/after inventory proof, and the separate
  return-only mutant control with its raw requests, responses, and recovery
  summary.

The actor's final project and the runtime output directory are not copied here.
The mutant evidence comes from a separate disposable project and does not
modify the actor's final project.
