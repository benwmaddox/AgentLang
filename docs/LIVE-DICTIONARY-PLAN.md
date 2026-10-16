# Live dictionary plan

Goal: adding a word to a loaded project feels close to Forth. A `define` plus
`commit` of one new word and its tests should finish in under a second on
Campfire (about 444 Flow words and 503 attached cases). Every test still runs
against the code it covers.

## Where the time goes today (Campfire, 2026-10-16)

| Step | Time |
| --- | ---: |
| probe define (one word + one test) | 6.1–6.7 s |
| probe commit, before the pure-add guard | 14.9 s |
| probe commit, after the pure-add guard | 7.8 s |
| of which: proposed snapshot | 6.3 s |
| of which: publish | 1.4 s |

Every runtime snapshot compile is from scratch:

- `compileRuntimeSnapshotWith` passes every Flow word as a `Rehydrate` change
  against an empty base inventory;
- `compileBatchWordsCore` re-lowers any retained sources it is given;
- `Compiler.compileProgram` re-checks and re-lowers every word;
- `IrVerifier.verifyProgram` re-verifies every function;
- every attachment and test file is recompiled against the new program.

The snapshot fingerprint covers each word's Status and Maturity, so a commit
(which only changes those) cannot reuse define's program by patching. Reuse has
to come from an incremental compile whose result is the same verified program
a from-scratch compile would produce.

## Design: extend a verified dictionary

A pure add is a batch whose new words replace nothing, evolve no type and add
no host operation. The parent snapshot is the dictionary already running.

1. **Set-hash fingerprint.** Make the snapshot fingerprint a hash over the
   sorted per-entry digests (word, host operation, record, scalar, enum,
   origin), not one hash over one long string. The from-scratch value and the
   incremental value (parent's entry digests plus the new entries) are then
   equal by construction. `IR_STALE_COMPILER_SNAPSHOT` keeps its meaning: same
   entry set, same fingerprint.
2. **`IrVerifier.extendCompilerProgram parent delta`.** It requires the
   following:
   - same catalogs and nominal types;
   - new ids absent from the parent;
   - new source sites owned only by new functions.

   It verifies only the new functions against the merged program. The
   acyclicity check covers only new nodes: parent bodies cannot call a word
   that did not exist when they were verified.
3. **`Compiler.extendIrProgram`.** Check and lower only the new words against
   the extended context; reuse the parent's per-word coverage and source map.
4. **Flow lowering.** Lower only the new sources. Retained words keep their
   lowered definitions and call bindings, which are already required to be
   stable (`checkRetainedBindingStability`).
5. **Attachments.** Retained test and example bodies are rebound to the
   extended program after one containment check: the parent's functions
   appear unchanged in the child. This is the check the interpreter already
   runs for test-file overlays. Only new cases are compiled.
6. **Runtime.** `define` and `commit` of a pure add extend the active
   snapshot. Anything else (replace, remove, type change) takes the
   from-scratch path, until reverse-dependency replacement lands.

## Proof obligations

- **Differential mode.** `AGENTLANG_INCREMENTAL_CHECK=1` also compiles from
  scratch and compares the program, fingerprint, test bodies and results.
- **Flow.Runtime test.** Define N words one at a time, then compare the
  result to loading all N at once.
- **Every-test rule.** The probe requires a passing result for its own test.
  Commit still runs every attached test of the selected words. A pure add
  has no callers to re-run.

## Order of PRs

1. Pure-add guard on the commit caller scan, plus commit phase timing (done:
   14.9 s → 7.8 s).
2. Set-hash fingerprint (no behaviour change; differential test).
3. `extendCompilerProgram` and `extendIrProgram`, with a direct test against
   a from-scratch compile.
4. Flow lowering and attachment reuse; Runtime pure-add path; differential
   mode.
5. Publish cost on a one-word add.
6. Replacement through the reverse-dependency closure.
