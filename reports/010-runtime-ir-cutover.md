# Runtime IR cutover

Status: implemented and independently validated through the full Release gate and the selected pinned AST-versus-IR parity contracts. No performance improvement or exhaustive semantic equivalence is claimed.

## Runtime boundary

`Runtime.Engine` now keeps each active `DictionaryState` paired with one
immutable `VerifiedIrProgram`, the exact lowering context, and detached
verified bodies for attached tests and examples. `eval` compiles its expression
against the active snapshot and executes that body. User words, primitives,
generated record/scalar operations, callbacks, and attached tests execute only
through `IrInterpreter`. Examples are compiled and verified but remain
non-executable metadata.

The former Runtime `invoke`/`runBody` AST evaluator and its AST-derived
coverage collector have been removed. Compiler/Parser AST processing remains
for source parsing, static diagnostics, graph/dependency analysis, and
canonical persistence; it is not a Runtime execution fallback. The `ir`
command uses `IrFormatting.toData` for user and generated targets, and labels
primitive output as a canonical primitive contract.

## Snapshot publication

Staging, temporary promotion/cleanup, discard, commit, rename, and deprecation
compile their proposed state before activating it. Candidate commit separately
compiles the exact durable projection and runs selected tests, caller tests, and
library coverage against that projection. The final active and durable
projections are both compiled before storage publication. The active snapshot
is switched only after successful persistence.

Project load validates source, manifest, identities, revisions, and referenced
objects before compiling and activating the loaded snapshot. Named snapshot
load compiles its validated state before restoring storage. Task begin captures
the exact executable snapshot with the dictionary and storage snapshots;
abort restores storage first, then reactivates that captured program without
creating new word revisions.

Task completion and abort remain successful when the durable task-log write
fails after the dictionary transition. The response and retained in-memory
task status include a structured `logWarning` with `saved: false`; this avoids
reporting a failed commit or rollback after that state transition has already
completed. The Acceptance regression uses an occupied task-log path to cover
both commit and abort behavior.

Coverage remains owned by each word body. IR `SourceSiteId` values are resolved
through the verified program/body source maps to the existing
`file:line:column` coverage keys. Branch outcome labels remain `true`/`false`,
`some`/`none`, `ok`/`error`, and the map/filter/each iteration outcomes.
Instruction hooks preserve the existing trace accounting and source-aware
10,000-step diagnostic; the interpreter independently enforces the same local
fuel limit, call-depth bound, and collection cap. Host effects remain behind
capability preflight and the existing virtual file, clock, and console
providers.

## Validation recorded so far

- Before the final generated-owner and task-log-write regressions were added, the focused Core Release build passed with 0 warnings and 0 errors and Acceptance passed 28 groups / 477 assertions. Those results do not cover the latest source edits.
- The final Acceptance source also checks generated type-owner tests during commit and task-log write failures after both commit and abort; final independent results are below.
- The Acceptance suite covers runtime errors, capability denial, empty and nonempty callback coverage, nominal/generated operations, test-gated commit, replacements, task rollback, named snapshot restore, and reload. It also checks formatter output and the candidate → commit → replacement → discard → reload lifecycle.

Final whole-solution and selected parity results are recorded below. No LLVM backend is included or required here.

## Final independent validation

`./scripts/Validate.ps1 -Configuration Release -ReportPath .agentlang/reports/010-runtime-ir-validation.json` passed all **18 checks**, with zero build warnings/errors. Language Acceptance passed **29 groups / 494 assertions**; the interpreter passed 13, IR verifier/lowering 69, formatting 36, vocabulary 32, harness 185, business reference 113, business contract 69, value inspection 44, source 57, storage 64, conventional 63, discovery 53, and proposed task-bank validation 2,114 assertions. Fresh-process persistence passed 99 checks. Both diff checks passed.

The pinned comparison command was `pwsh -NoProfile -File scripts/Verify-IRParity.ps1 -ReferenceCliDll .agentlang/ir-parity/host-019d754/AgentLang.Cli.dll -CandidateCliDll src/AgentLang.Cli/bin/Release/net9.0/AgentLang.Cli.dll -EvidencePath .agentlang/reports/010-ir-parity.json`. It passed **314 selected contract checks across 24 fresh processes**. The reference is the retained AST host from revision `019d754`; candidate execution is IR-only. The comparison covers the fixture-defined observable fields, not every primitive input, diagnostic prose, or every possible program.

Saved evidence: [full working-tree gate](evidence/010-working-tree-validation.json), [persistence projection](evidence/010-working-tree-projection.json), and [pinned parity requests/responses and binary hashes](evidence/010-working-tree-ir-parity.json). These runs identify base revision `f0a2b31963d19517238a9556b50fe9019de7181d` with a dirty working tree, not a clean committed revision.

Independent read-only review found no AST fallback or dictionary/program pairing defect. It exposed the post-transition task-log failure edge; commit and abort now return truthful success plus a structured `logWarning` when the log cannot be saved, retaining that warning in in-memory task status. Log persistence is best-effort and not atomic with dictionary publication. Generated type-owner tests now run before type commit. Root validation also corrected new test-only F# indexing syntax and an assertion that confused commit's array response with test-run's object response.

## Publication

Code, reports 010–012, architecture/PRD updates, and the working-tree evidence are published together. Exact committed CI evidence will be recorded after publication. Controlled experiments and native backends remain outside this milestone.
## Committed CI evidence

Implementation/report revision `f1836d1f3a097b6bfa9dcde41d047a0a5da00187` passed [CI run 37306481981](https://github.com/benwmaddox/AgentLang/actions/runs/37306481981). The downloaded report identifies that exact revision, `dirty: false`, all 18 checks passing, and 99 passing fresh-process persistence checks. Saved evidence: [clean committed validation](evidence/010-committed-ci-validation.json) and [clean persistence projection](evidence/010-committed-ci-projection.json). This publication update accompanies the implementation when merged into main. The pinned 314-check AST-versus-IR comparison remains the separately recorded working-tree run; CI does not repeat that historical-binary comparison.