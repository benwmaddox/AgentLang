# 198 — Native Bool wrappers and bounded admission

Status: accepted on 2026-10-11 after fresh local validation. Failed preparation
attempts remain separate from accepted results.

This implements the bounded [plan in report 194](194-native-bool-implementation-plan.md):
Bool-backed nominal types and optional pure frozen Bool-to-Bool predicates in
the owning native backend. Host values retain exact nominal names and distinct
TypeIds. Wrapping and unwrapping retag the existing descriptor without moving
its payload. The representation remains eight-byte little-endian 0 or 1.

The native descriptor scanner now checks the complete eight-byte value after
the existing owner-bounded readability check. Noncanonical values fail before
execution or publication. Active record, Option and Result payloads use the same
recursive check; inactive alternatives remain unexamined. This adds no host
capability, dependency, allocator mechanism or ABI revision. Layout schema 3,
stack ABI 1, mailbox ABI 1 and module ABI 1 remain unchanged.

## Accepted validation

The independent direct C checks pass at O0 and O2: one focused case with 55
checks, and the full storage suite with 51 cases and 667 checks. The fixed
16-case/189-check baseline is unchanged; the dynamic suite grows from 34/423
to 35/478. The deterministic fixture counts were updated from an independent
control-count audit, rather than suppressing the new checks.

These controls cover canonical false/true, noncanonical high-byte values,
external and stack owners shorter than eight bytes, aligned nonzero offsets,
active nested leaves, inactive alternatives, descriptor mutations, and unchanged
measurement canaries and guard bytes on rejection.

The final fresh F# build passes with zero warnings/errors. Focused O0/O2 tests
pass 22 nominal Bool and 58 refined Bool assertions. The full LLVM suite passes
771 assertions. Existing mailbox integration
passes 2,255 checks, memory-policy regression passes 741, and the focused refined
mailbox regression passes five assertions. The fresh native script verifier also
passes. The complete workspace-temporary Release gate passes all 37 checks,
including the policy preflight's 98 checks across 30 independent outcomes.

| Local validation | Current result |
| --- | --- |
| Focused nominal Bool, O0/O2 | 22 assertions passed |
| Focused refined Bool, O0/O2 | 58 assertions passed |
| Full LLVM suite | 771 assertions passed |
| Direct C Bool scanner, each O0/O2 | 1 case, 55 checks passed |
| Direct C storage suite, each O0/O2 | 51 cases, 667 checks passed |
| Independent native runner | 1,450 checks passed |
| Fresh native script verifier | 50 checks passed; 47 source inputs unchanged |
| Existing mailbox integration | 2,255 checks passed |
| Existing mailbox policy regression | 741 checks passed |
| Focused refined mailbox | 5 assertions passed |
| Workspace-temporary full Release gate | All 37 checks passed |

Counts include repeated observations and regressions, not distinct new scenarios.

## Boundary and preparation findings

The existing mailbox lifecycle requires String request/completion inputs and
nominal record State/Continuation roots. A top-level Bool mailbox request is
therefore outside this contract and remains rejected before code generation.
Bool admission is exercised directly in ordinary native entry and recursively
inside mailbox State records; this change does not broaden the role contract.

Interpreter execution of already-retained roots does not replay their predicate.
Ordinary native external admission does. Tests account separately for body and
frozen predicate steps, including RETURN reimport and KEEP reuse.

Test preparation exposed incorrect expected nominal ordering, a misplaced nested
sum fixture, inadequate retained capacity, and incorrect callback cursor
expectations. A failed raw RETURN predicate can leave imported inputs and
predicate scratch in its disposable context; it must publish no valid output or
committed state. This differs from host cleanup and from malformed KEEP roots,
which must fail before appending completion data. A fabricated parked context
also failed the runtime's stack-layout checks; the retry fixture now uses the
actual successful begin invocation and owner ranges. A misplaced `Marshal.Copy`
length argument prevented the test from repairing its malformed field; correcting
the destination pointer and length makes that same parked mailbox retry succeed.
The successful retry appends completion data after the parked cursor, without
replaying the frozen predicate. Independent interpreter-derived instruction
counts cover ordinary typed-root admission, constructors, BEGIN, RETURN and KEEP.

The first native script run failed four historical BoolTag negative controls that
still expected the newly supported type to be rejected. Those expectations were
removed from the nominal-Int and refined-String companion matrices; Float
rejection controls remain. The corrected runner passes all 1,450 behavioral
checks. Its next verifier attempt caught a stale returned summary count (28
instead of 27 refined-String cases per optimization); that reporting field was
corrected for the final verifier rerun. These failed verifier attempts remain
separate from a passing native gate.

The first full Release run built successfully, then encountered access-denied
failures while committing temporary test projects. Repeating the language
acceptance command with TEMP/TMP inside the writable workspace passes all 35
groups and 642 assertions. The original run finished with nine failing checks out of 37 and is retained
as a failed environment/preparation attempt. Its independent policy preflight
passed all 98 checks across 30 outcomes. The separate workspace-temporary
rerun finished successfully with all 37 checks passing.

Preparation failures are retained separately from accepted passing results.
Final receipts, source pins, ABI audit, source review and relevant logs are
archived with this report. Some intermediate build attempts have no standalone command/exit
receipt; their preparation history will be labelled as a receipt gap, not
reconstructed as test evidence. The final accepted build has its own complete
log and receipt.

Independent source review finds no concrete blocker in the frozen core and
focused tests. No Bool-specific same-name dictionary-replacement executable has
been added: this slice relies on shared frozen-target revision checks and the
existing Int/String replacement regressions for that invariant. The retry harness
remaps the captured context and checks owner ranges and observable behavior;
it does not assert equality of every cumulative context counter or bitmap.

This engineering slice adds no agent efficacy result or service throughput
measurement. [Report 199](199-efficacy-decision-after-reference-maintenance.md)
remains the current efficacy assessment. Float/List native support, general JIT
and service memory-policy selection remain separate work.


## Reproduction and evidence

The full gate used `pwsh -NoProfile -File scripts/Validate.ps1 -Configuration
Release -SerialBuild -SkipPackageAudit`, with TEMP/TMP in the writable workspace.
The package-audit skip is explicit; no dependency was added or changed. Native
verification additionally used `Verify-NativeValueStack.ps1`,
`Verify-OwningMailbox.ps1 -SerialBuild` and `Verify-OwningMailboxPolicy.ps1`.
The full LLVM suite ran from the fresh isolated Release build, not a stale DLL.
CI remains manual-only.

[Evidence archive](evidence/198-native-bool-admission/evidence.zip) and
[member/hash index](evidence/198-native-bool-admission/evidence.zip.index.json)
retain accepted gates and known failed/superseded preparation. These are bounded
functional and admission checks, not a general security certification.
