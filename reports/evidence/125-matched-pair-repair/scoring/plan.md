# Pair scorer implementation plan

## Scope
Implement one task-scoped PowerShell scorer for the fixed 12-case pair oracle. It supports either a committed Flow actor copy or a committed F# actor copy. It reads the existing oracle and runner inputs; it does not define actor code, dispatch actors, or change product/runtime code.

## Affected files
- `score-pair.ps1`: selects Flow or F#, runs actor-owned checks separately, then evaluates each oracle case on fresh copied/in-memory provider state, and writes bounded evidence plus original-project inventory hashes.
- `plan.md`: this plan.

## Acceptance
- Assert the exact ordered pair of string results, the complete resulting virtual filesystem map, and cumulative `fs.read`/`fs.write` counts for all 12 oracle cases.
- Repeat the two designated calls against the same evolving state.
- For each denied-capability case, verify preflight returns `CAPABILITY_DENIED` with no reads/writes and unchanged full state.
- Run the actor's own test path separately and retain full error records in evidence.
- Build/run only from copied actor files; hash actor input before/after and require it to stay unchanged.
- For F#, build copied source with fresh isolated output/intermediate paths. Use the already pinned Flow CLI/runtime input.

## Validation
Run the scorer once for each ready committed actor copy using the root-provided exact commands; inspect evidence for all 12 cases and unchanged source inventories. Run the scorer once against the root-selected correct/fault control copies; confirm it passes the correct copy and rejects both requested mutants. Do not add general harnesses or product changes.