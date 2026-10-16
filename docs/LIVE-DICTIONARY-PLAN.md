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

1. Pure-add guard on the commit caller scan, plus commit phase timing (done,
   PR #2: 14.9 s → 7.3 s).
2. Fingerprint as a hash of per-entry digests, memoized per word entry (done,
   PR #3).
3. Extend path (PR #4):
   - `IrVerifier.extendCompilerProgram`;
   - `Compiler.extendIrProgramWithSourceOrigins`;
   - `FlowLowering.extendFlowProjectSources`;
   - `compileRuntimeSnapshotCore` with an extension;
   - pure-add detection against recent and loaded snapshots;
   - differential mode and an extension log;
   - the Flow.Runtime group `testIncrementalDefinesMatchFullCompile`.

   Campfire probe: define 6.0 s → 0.3 s, commit 7.1 s → 1.8 s (1.3 s of it is
   publish).
4. Publish cost on a one-word add (done, PR #5).
5. Two-part build (done, PR #6).
6. Replacement through the caller closure (PR #7, below).
7. Status-only commits re-fingerprint the parent snapshot instead of
   recompiling it, quadratic commit scans are removed, and per-case test
   overhead is cut (PR #8). The verified Campfire commit went from 36.5 s to
   about 11 s.

The project was archived on 16 October 2026; see
[the final report](../reports/final-report/index.html).

## Replacing a word (as built)

`dictionaryDelta` also accepts words that changed. The extension then recompiles:

- the changed words and their transitive callers, computed over the parent's IR call graph (validator calls through generated record and scalar constructors count as calls);
- every case owned by those words or by added words;
- every other case whose compiled body calls one of them directly. Callers are closed under calling, so a direct call suffices;
- every test file whose cases can reach one of them through their overlay program.

Everything else, kept functions included, is the parent's. `IrVerifier.extendCompilerProgram` rejects a kept function, or a type validator, that calls a replaced function.

The extension falls back to a full compile when:

- a record or scalar validator would be recompiled;
- a Stack-authored word or case would be;
- the words to compile are more than a third of the Flow words (and more than 16).

Check mode (`AGENTLANG_INCREMENTAL_CHECK=1`) also requires every body bound to an earlier program to reach only functions that are unchanged in the new one. IR equality alone cannot see a stale binding.

The commit gate is wider too. A replacement or type change already ran the tests of every word whose body calls the changed words. It now also runs the tests of every other owner that has a case whose compiled body calls a changed word or one of its callers. Such a case covers that code even though its owner does not call it.

Campfire probe, replacing a word that has one caller:

| Step | Before | After |
| --- | ---: | ---: |
| define | 5.7 s | 0.3 s |
| `replace-word` | 6.1 s | 0.7 s |

Each snapshot is also remembered under its compiled state, so the commit's durable projection of it is a cache hit.

## What a pure add is (as built)

`pureAddedWords parent state` accepts `state` only when all of these hold:

- every parent word, test, example and Flow source row is the same object in
  `state`;
- types, type sources, replacements, deprecations and evolution groups are
  unchanged;
- the new words are Flow-authored and own every new row;
- no new word's short name is a name that a retained call site requested
  without qualification.

The last rule matters because unqualified Flow calls resolve by short name
(`candidatesFor`), so such an add could rebind or make ambiguous an existing
call. That case takes the full compile, which re-checks every binding.

Retained test bodies stay bound to the program they were verified with.
Test-file overlays keep their `Original` program. A snapshot extended by
adding words contains those programs unchanged, so this is the Forth model:
earlier definitions keep the dictionary they were compiled in.

Switches:

- `AGENTLANG_INCREMENTAL=0` disables extension.
- `AGENTLANG_INCREMENTAL_CHECK=1` also compiles from scratch and requires the
  same program, attached bodies and stored bindings.
- `AGENTLANG_INCREMENTAL_LOG=<file>` appends one line per extension.

## Two-part build (Windows exe first, verification second)

`scripts/Build-CampfireFlowServer.ps1 -TwoPart -OutDir <dir>` builds twice.

**Part 1** goes to `<dir>/unverified` with `-Unverified`:
- `emit --unverified` defines the words but does not commit them, so no attached test runs;
- the code is still parsed, type-checked and IR-verified;
- the entry points come from `ExportUnverifiedFlowEntryPoint`;
- it is compiled at O0;
- it writes `UNVERIFIED.txt` next to the exe.

**Part 2** is the normal build into `<dir>`: it commits, which runs every attached test, then compiles at O2. A failing test fails this part.

You can run part 1's exe while part 2 builds. `-Unverified` alone builds only part 1.

Campfire on 2026-10-16:

| Part | Ready at |
| --- | ---: |
| 1, unverified exe | 18.7 s |
| 2, verified exe | 81 s |

The FlowHttp tests build the counter app unverified at O0 and run the same parity and protocol checks against it.
