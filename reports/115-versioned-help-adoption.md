# Versioned-help replay: effect assertions adopted and mutation caught

In this replay, one Flow/2 actor repaired the reminder function and attached
exact effect-count assertions to all five target tests. The independent
behavioral verifier passed, and a disposable extra-write mutant failed three
tests and was blocked from replacement. This met the adoption criterion for
this trial. Compared with [report 114](114-effect-assertion-adoption.md), this
run changed the help guidance and launch paths; one actor per condition cannot
establish a causal or comparative advantage.

The replay used one fresh `gpt-6-luna` actor with max reasoning, the same source checkpoint
`a8e7d34b9e8ae50ea3281a4df31fb8b17014028c`, pinned CLI SHA-256
`af2da9bfeaa706a4c67592465b23c48847aadbbd2d6d578342df1412662c3646`, report
111 seed manifest `efafec711482ffd2c7b9bb11015b90b0e8a0bc5ef6be40eb870e955157aff536`,
and the same metadata-adjusted behavioral verifier. The prompt diff is limited
to version-matched help guidance and the actor/trace paths: it selects
`syntaxVersion: 2` for help and explicitly shows the Flow/2 `examples` topic
request. The task specification, seed, runtime, and oracle stayed fixed.

The actor made 25 broker exchanges. All four successful help requests selected
Flow/2, and the `examples` response displayed the exact effect-count syntax and
a source example. The trace contains one exploratory `describe` failure,
`NAME_UNKNOWN_WORD` for `Invoice`. The actor then durably committed revision 4
as a library replacement. It reported 5/5 target tests, 20/20 instructions,
4/4 branch outcomes, 2/2 path-helper tests, and 1/1 example. The captured source
shows exact effect assertions in all five target tests, including two reads and
zero writes for an existing marker and four reads and zero writes across two
repeated calls. The independent behavioral verifier passed 144 checks.

For the mutation check, a new disposable project began byte-identical to the
actor's 39-file project. I staged the exact report 111 word-only extra-write
definition, retaining the five tests. Two tests passed and three failed with
`TEST_EFFECT_ASSERTION_FAILED`: existing-marker cases observed one write versus
zero expected, and the repeated-call case observed two writes versus zero
expected. The candidate still had full 25/25 instruction and 4/4 branch
coverage. `replace-word` returned `COMMIT_TESTS_FAILED`; the disposable
project's persisted library remained at revision 4. The actor project remained
byte-identical through verification and mutation scoring.

Both broker traces record host and runtime exit code 0 with no stderr. The
actor's final message says its later poll returned “Unknown process id”; that
poll result and the trace's terminal status are both preserved. A redundant
second mutant-host launch was refused before startup because the first trace
already existed; it did not overwrite the successful run.

The [evidence package](evidence/115-versioned-help-adoption/README.md) contains
the frozen dispatch and runtime metadata, exact prompt diff and prompts, actor
trace and final message, exact source/test snapshots, verifier output,
disposable mutation inputs/results, inventory comparisons, and a SHA-256
index. It includes no runtime binaries. No source or runtime files were changed
for this scoring pass.
