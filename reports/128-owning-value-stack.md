# 128 — Owning value-stack comparison

Status: validated fixed-layout prototype checkpoint, 2026-10-08.
Tested as a working tree based on `60b4cee`; see the source hashes and commands in
the [evidence index](evidence/128-owning-value-stack/index.json).

## Question

Can the native backend store complete nested values inline, preserve independent
owners across copies and calls, and reclaim working storage during a message
handler rather than accumulating all allocations until invocation exit?

This is a memory-semantics experiment. It does not measure agent reliability,
server throughput, cache performance, or a completed production runtime. The
latest independent agent comparison remains [report 125](125-matched-pair-repair.md).

## Candidate and control

The candidate lowers the existing verified semantic IR into a separate owning
stack backend. The first slice supports Int, Bool, Unit, and acyclic records with
fixed inline layouts. Named locals own storage; loading a local creates an
independent value. Results enter caller-owned storage before callee cleanup.
Source authors do not manage stack positions or borrow older stack values.

The control is the existing ABI3 native arena backend. Both use the same verified
program, with interpreter results and handwritten output/layout oracles as
independent checks. Compare one and eight repeated helper calls at O0 and O2.
Report peak live payload, reserved stack extent, copy traffic, instrumentation,
and host staging separately. Constant peak working storage would support frame
reuse for this workload; it would not prove lower total process memory.

The experiment host remarshal boundary between two turns is explicit. It is not
a standalone native persistent mailbox controller or an async implementation.
Variable-sized values and real I/O remain outside this slice.

## Validation

- Fresh serial Release solution build: passed with zero warnings/errors.
- Existing native conformance: 476 assertions passed from a fresh isolated build.
- Existing native mailbox dispatch: 353 checks passed; its 20 recorded source
  inputs were unchanged during the run.
- Direct C owning storage checks: O0/O2/UBSan-trap runs pass 16 cases and 189
  checks in the source-stable main comparison below, after formatting and the
  freestanding copy fix. Ordinary UBSan runtime linking is unavailable on this
  host; the passing trap variant is recorded separately.
- Full local Release gate: all 37 checks pass, including 98 business-policy
  checks across 30 independent outcomes. The run uses repository-local temporary
  storage, serial build, and the explicit audit skip propagated to child verifiers.
  It took about 29 minutes; the business-policy preflight accounts for about 22.
- Generated owning-stack comparison: source-stable run
  `verification-304f9241c59248f1ab41039878f649a8` passes all 462 semantic/layout
  checks and 26 verifier checks at O0/O2 and N=1/N=8. Physical trace assertions
  now select the duplicate's own drop event and follow relocation into the
  caller's return area. All 39 tested source hashes match the final working files.
  Focused cases cover Unit allocation (payload/extent 8/8), Empty (0/8), and
  restoration of the same LocalSlot after a 16-byte Envelope is shadowed by an
  eight-byte Leaf. A static acyclic chain accepts 65 entered language bodies and
  rejects the next with `RUNTIME_CALL_DEPTH`, matching the interpreter and ABI3;
  failure leaves zero live/cursor bytes and preserves caller output.

Measurements from the main comparison:

| Storage category | One helper repetition | Eight helper repetitions |
| --- | ---: | ---: |
| Owning stack reserved extent | 288 bytes | 288 bytes |
| Peak live operand payload | 32 bytes | 32 bytes |
| Peak live local payload | 104 bytes | 104 bytes |
| Reserved local storage (within stack extent) | 184 bytes | 184 bytes |
| ABI3 control scratch payload allocation | 104 bytes | 272 bytes |

Separate peaks need not occur simultaneously and must not be summed as a measured
joint peak. The control also needs node metadata; the candidate needs bounded
trace/initialization metadata and host staging. These are not total-process
memory comparisons. The candidate exhibits reuse but has greater fixed payload
reservation for both small samples. Its measured deep-copy traffic grows by
952 bytes from one to eight repetitions; reclamation is not free of transfer work.

The correctness probe reserves a 512-byte data buffer plus **327,976 bytes** of
native trace/context/bitmap instrumentation per invocation (8,192 trace slots).
That instrumentation dominates this tiny workload. The report's 288-byte working
extent is not the runtime's total allocation. Managed value encoding/decoding,
input staging, and retained-output staging/commit are additional host work. A
production memory or throughput claim needs a separately measured execution mode
with appropriate observability costs, not this diagnostic allocation profile.

The evidence archive includes final reports, tested sources, generated LLVM,
selected failed runs, and regression logs. Large files are losslessly gzip
compressed with original and stored hashes in the index. Executable/object
artifacts remain local; their hashes and build commands are retained. The full
regression run precedes the last candidate-only accounting/depth fixes; the
subsequent fresh 462-check comparison builds and tests those final changes.

Reproduce the focused comparison with
`pwsh -NoProfile -File scripts/Verify-NativeValueStack.ps1`.
The full gate used `scripts/Validate.ps1 -Configuration Release -SerialBuild
-SkipPackageAudit` with TEMP/TMP directed to a writable repository-local folder.
The existing native control used a fresh isolated Release build of
`tests/AgentLang.Llvm.Tests`; dispatch used
`scripts/Verify-NativeDispatch.ps1 -SerialBuild -SkipPackageAudit`.

## Failures and corrections retained

The new emitter needed corrections to LLVM labels and operand escaping, local
ownership flags, empty-field extents, and final live-byte accounting. Review also
examined offset overflow: a successful base and a static relative are bounded by
Int32.MaxValue, and capacity validation precedes use of an oversized frame end.
Capacity diagnostics use int64 so an oversized required count remains positive.
The native entry also rejects capacities above Int32.MaxValue before emitted
offset calculations; that invariant does not rely solely on the managed caller.
The first candidate link exposed an unavailable `memmove` import in the
freestanding runtime. Replacing it with bounded directional copying allowed the
candidate to reach execution. The first initializer then failed live-byte
accounting because Int/Bool constant stores omitted the corresponding live-byte
increment. Adding those increments enabled the semantic comparison. Review found
the same omission for eight-byte Unit values. Scalar constant emission now shares
one store-and-account path, and compiled Unit tests check both live bytes and
allocation-event payload. The native frame limit also needed one extra allowance
for the wrapper to match language call-depth semantics; both sides are tested.

Unit/Empty and same-slot shadow tests use compiler-minted IR. Flow source still
rejects empty record declarations; this checkpoint does not add that syntax.
The call-chain test is acyclic, not a test of recursive execution. Handwritten
source, IR fixtures, and implementation-worker debugging are not fresh independent
agent trials. No new agent-efficacy advantage is claimed.

The experiment initially used dotted namespace calls. Flow parses those as
property-style calls on a local; qualified calls use `::`. Constructors and
namespaced helper expressions were corrected. This is authoring friction, not a
new language fix or an independent agent-efficacy result.

The initial Flow fixture also used `drop(local);`, which the expression-statement
checker rejects because `drop` has zero outputs. Removing that invalid syntax
does not demonstrate early local destruction: the repeated Flow helper relies on
scope/frame cleanup. A separate verified stack-IR case exercises actual operand
dup/drop and returns the surviving value.

Parallel MSBuild restore failed without a compiler diagnostic on this host.
Serial builds succeeded. NuGet vulnerability lookup was unavailable, and the
shared temporary directory denied atomic file operations. Validation uses an
explicit opt-in audit skip and repository-local temporary storage; failed runs
are preserved. The audit skip does not establish package vulnerability status.

## Design follow-through

[The design review](../docs/OWNING-STACK-DESIGN-REVIEW.md) keeps compiler-controlled
scope cleanup, value ownership, and physical payload locality separate from
allocator mechanics. No older-stack reference feature is planned.

[Compiler-guided mailbox sizing](../docs/ASYNC-ARENA-EVALUATION.md#compiler-guided-sizing)
is planned research: distinguish proven bounds, workload estimates, and unknown
requirements. Handler storage estimates need concurrency/admission limits to
become pool budgets. This checkpoint does not implement a general sizing analyzer.
