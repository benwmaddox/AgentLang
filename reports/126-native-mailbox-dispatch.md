# 126 — Standalone native mailbox dispatch

Status: bounded standalone native dispatch validated locally, 2026-10-08.

## Purpose and boundary

Report 108 executed actual compiled Flow/2 handlers but used a .NET experiment
host for mailbox ownership and scheduling. This milestone moves fixed mailbox
admission, pending completion identity, arena storage and dispatch into C.
The F# frontend still parses and compiles source; the standalone executable
loads the generated native module and performs its turns without a CLR or
managed callback. The source fixture is byte-identical to report 108.

This advances the approved lean-runtime work after the matched efficacy
comparison in report 125. It is not a new agent experiment and cannot establish
an agent reliability advantage. Real asynchronous I/O, provider cancellation,
JIT and saturated-server throughput remain outside this checkpoint.

## Contracts

Execution ABI v3 remains unchanged. Separate versioned module ABI1 and mailbox
control ABI1 have independent layout fixtures. A module contains named entries
from the exact same backend-authorized verified program instance, deterministic
entry/type tables, function pointers, workspace requirements and diagnostic
metadata. Validation rejects unsupported or cross-program entries before tool
execution. Existing single-entry compilation remains available.

The module fingerprint identifies semantic metadata and emitted LLVM IR, not
the full binary build. The manifest records optimization and separate generated
source/runtime/header/DLL hashes. Compact native diagnostics expose code,
location and runtime arguments; richer message and expected/actual descriptions
remain in the manifest. Modules and caller-supplied storage must outlive use.
Compilation is not release certification: library eligibility and a pinned
release dependency closure remain separate work.

The native controller uses one shared scratch arena/workspace and two retained
banks per fixed mailbox, all reserved up front in caller-owned storage. A turn
imports retained inputs, executes the compiled handler, stages successful output
in the inactive bank, then swaps ownership and clears the previous bank. A
handler or capacity failure preserves the active graph and pending token.
Scratch is reset and poisoned after each attempted execution. No per-turn
allocation or managed graph conversion is needed; importing and promoting
records still copies data.

Actual OS thread identity confines each runtime. A shared in-handler guard
prevents reentrant turns from reusing scratch. Completion capabilities contain
runtime, mailbox and sequence identity; copying a token aliases the same
completion. Busy admission, wrong ownership, stale/duplicate/cross-runtime
completions and foreign-thread operations reject before handler execution.
The native C caller is trusted; this API is not a sandbox against arbitrary
pointers or a security boundary.

Generation allocation is checked before state mutation. Scratch and staging
receive fresh generations; exhausted counters do not wrap. ABI3's 32-bit owner
generation field bounds a runtime to at most 2,147,483,647 attempted invocations
with the current two-generation scheme. This is a prototype lifetime limit,
not a production longevity solution. Token and runtime-instance counters also
reject exhaustion. Disposing clears live state and is idempotent on the creator
thread; reserved caller storage is released by its caller afterward.

## Validation and evidence

The [evidence index](evidence/126-native-mailbox-dispatch/index.json) records
successful and failed integration attempts, exact source bytes, layout fixtures,
commands, test results and artifact hashes. Final integration run
`f0cea07ec0af461c896fc16057ed25d9` passes all 353 verifier checks. Both O0/O2
executables pass 51 handler/protocol checks, return A=17 and B=103 after overlap,
then A=19 after the later completion, and preserve pending state across division
and seven-byte retained-capacity failures. The direct C suites each pass 69
named checks at O0/O2 and separately under O1 undefined-behavior trap
instrumentation. These counts overlap in scope; they are not independent trials.

Fresh complete native conformance passes 476 assertions. The existing managed
mailbox reference passes 120 checks across interpreter/O0/O2. The full local
Release gate passes all 37 checks, including 98 business-policy preflight checks
across 30 deterministic outcomes. The aggregate build began before the final
manifest encoder change; the later fresh full native suite and integration build
cover that final compiler source. No Core/interpreter code changed during this
run. Compiler command summaries are preserved; that runner did not save a full
raw console transcript. Native integration captures each subprocess output.

Both native runs report these exact bounded storage totals:

| Component | Reserved bytes |
| --- | ---: |
| Shared scratch, including descriptor and node directory | 168 |
| Four retained banks, including descriptors and node directories | 512 |
| Shared call workspace | 24 |
| Controller, mailbox slots, roots and remaining layout overhead | 696 |
| Total caller-supplied storage | 1,400 |

Peak live retained payload is 32 bytes/four nodes; scratch high-water is 24
bytes/three nodes. Each primary runtime records twelve acquisitions and twelve
returns, including two failed handlers. Disposal leaves zero live retained
payload/nodes and zero outstanding scratch leases. The 1,400 bytes excludes
loaded code/type metadata, CRT/OS state, thread stacks and process overhead;
it is not whole-process memory or a representative application footprint.

Repeated O0 builds and O2 produce identical semantic fingerprints and metadata.
Actual C layouts match the independent module/control fixtures. Historical
execution ABI1/2/3 fixtures remain unchanged. Generated module DLLs have no
imports; runners import allowlisted KERNEL32 symbols, including CRT startup,
loader, thread and allocation support. All inspected images have zero CLR
headers. This proves native execution for the tested subset, not an entirely
heap-free process. The controller's turn path has no allocation calls. Scratch
and staging poisoning are deliberate safety instrumentation whose cost is not
a production performance result.

All source inputs match their before/after hashes in the passing integration
run. All 22 copied report125 runtime files still match their pins. Existing
retention004 drafts were untouched. CI remains manual-only.

The independent source/outcome/capacity oracle is
`tests/fixtures/native-conformance/native-dispatch-v1.json`; module and control
layout fixtures are alongside it. Reproduction commands are:

```powershell
pwsh -NoProfile -File scripts/Verify-NativeDispatch.ps1
pwsh -NoProfile -File scripts/Verify-NativeConformance.ps1
pwsh -NoProfile -File scripts/Verify-NativeMailboxSuspension.ps1
pwsh -NoProfile -File scripts/Validate.ps1 -Configuration Release
```

Review found reused arena generations, an overly broad Int-storage-kind message
check and old retained banks left populated after successful swaps. Corrections
assign fresh checked generations, require canonical Int message signatures and
clear obsolete state only after success. Tests also exposed empty manifest
serialization; an explicit JSON encoder now covers all semantic fields. The
evidence distinguishes source review, direct C tests and execution of actual
compiled handlers. Further review added pre-mutation caller-buffer overlap
rejection and acceptance of unused zero-output entries while keeping mailbox
role signatures strict.

Four failed integration attempts remain archived: an F# bootstrap syntax error,
a production C compile during an incomplete alias-guard edit, a runner reading
a diagnostic pointer after unloading its module, and a stale-token test expecting
stale while the token was still the most recently completed one. The runner now
copies diagnostic text before unload and completes a later zero-delta request
before testing the older token. The runtime's duplicate/stale distinction was
preserved; the acceptance test was corrected to exercise its declared condition.

## Interpretation and next step

Zero scratch leases while suspended does not mean zero reserved memory. Both
retained banks, scratch, node directories, arena descriptors, root tables,
workspace and controller overhead count toward reservation. This deliberately
bounded controller is not evidence that the final allocator or pooling policy
is selected. Keep the per-turn candidate, then introduce real bounded I/O and
a genuine whole-request ownership alternative before a matched memory and tail
latency comparison. Holding already-cleared scratch is not that alternative.

The user explicitly reaffirmed the next comparison: keep the same mailbox and
its actual stack/scratch arena associated until async work safely finishes,
versus returning scratch at suspension and reacquiring it for that same mailbox.
The updated [async comparison contract](../docs/ASYNC-ARENA-EVALUATION.md)
requires equivalent workloads, memory ceilings and tail-latency targets, measuring
throughput, live/reserved/process memory, copying and allocation churn. The
logical-delay comparison in report104 does not replace this real-I/O experiment.

The subsequent user clarification prioritizes stack-style memory management for
most working data. This milestone implements invocation-wide scratch and retained
state, not nested stack reclamation. The next bounded experiment must resolve
outer references, returned/nested values and stale handles after rewind before
adopting a stack/region rule. Mailbox identity is an execution boundary, not proof
of the desired memory discipline; the PRD and memory research documents now
state that distinction explicitly.

A further clarification makes the preferred model explicitly **value-based**:
stack entries own their full payload; duplication yields independent values and
moves transfer ownership. Popping must reclaim owned payload without dangling
aliases or orphaned storage. Current ABI3 shared record graphs do not satisfy
that stronger model. They remain tested groundwork and a comparison control;
copy/move/pop and return placement for owning values are the next implementation
question. The PRD and stack research contract were updated before publication.
