# Native record ownership and retained outputs

Status: bounded native record implementation validated locally, 2026-10-07.

## Decision

Reports 104 and 105 established a bounded native arena comparison and native
refined scalar conformance. This integration executes actual language records
in an invocation arena and exports successful values into an independent native
owner before scratch cleanup. This is not mailbox scheduling or async I/O.

A small freestanding C runtime owns graph validation, record allocation/access,
structural equality and promotion. The existing Clang compiles it into the native
artifact without an OS allocator, model dependency or managed callback. F# owns
backing buffers and emits typed LLVM calls. This division keeps pointer and graph
logic in ordinary C instead of expanding generic safety logic into LLVM strings.
The semantic IR and managed interpreter remain authoritative and unchanged.

## Implementation language policy

The user has explicitly removed any requirement to use a particular implementation
language. Correctness, maintainability, testability and measured runtime behavior
determine the choice. F# remains the current compiler/tooling implementation; the
small C runtime is a bounded ownership and graph-processing component. Neither
choice is a permanent product constraint. LLVM is the compilation backend, not
a replacement source implementation language. A broader rewrite requires concrete
evidence that it improves these goals; this milestone does not undertake one.

## Bounded semantic contract

Supported records recursively contain Int, Bool, Unit, refined Int/Bool values
and other supported records. Empty records, shared children and multiple roots
are included. Recursive record type graphs, Float, String, containers, effects
and suspension remain unsupported and reject before native compilation.

Each entry invocation and its nested calls share scratch. Returning from a
function never resets scratch. Successful output roots are promoted together to
an empty retained owner; aliases across roots remain shared. Promotion validates
and computes required capacity before publishing any root or retained state.
Failure leaves public outputs and the destination owner unchanged. Cancellation
of external I/O is outside this execution slice.

Handles combine a nonzero owner generation and a one-based node-directory index.
Generation, index, expected type, field count and payload bounds are validated
before reading payload. Constructors refer only to older children, establishing
an immutable DAG. Promotion marks from roots, walks nodes in descending order,
checks capacity, then copies in ascending order and rewrites child handles.
Scratch metadata may be changed during failed promotion; retained state may not.

A disposable retained result decodes from retained bytes and frozen metadata
only. It must survive scratch poisoning/reuse and disposal of the compiled DLL.
Separate invocations retain separate owners. Generation exhaustion must fail
rather than wrap and make stale handles valid.

## ABI v2

The existing 32-byte diagnostic prefix remains first, but the ABI version changes.
An ABI-v1 request must reject before any v2 tail field is read. Layout fixtures
must compare actual C, LLVM and managed layouts.

| Object | Contract |
| --- | --- |
| Context | 64 bytes, alignment 8: diagnostic prefix; scratch pointer at 32; retained pointer at 40; call workspace pointer at 48; workspace capacity at 56; reserved u32 at 60 |
| Arena | 48 bytes: data pointer 0; byte capacity 8; used bytes 12; node directory pointer 16; node capacity 24; count 28; generation 32; flags 36; reserved u64 40 |
| Node | 32 bytes: type ID 0; field count 4; payload offset 8; payload bytes 12; scratch mark 16; reserved 20; forwarding handle u64 24 |
| Payload | Declared-order 64-bit fields; scalars retain their existing encodings |
| Handle | Generation in high 32 bits, one-based node index in low 32 bits; zero invalid |

Public output capacity and internal call-workspace capacity are distinct. Entry
validation checks required pointers, alignment, checked spans and non-overlap
before mutation. The embedding caller still promises that pointers designate
real readable/writable allocations; range arithmetic cannot prove arbitrary
pointer validity. Scratch/retained byte and node limits are explicit native
resource budgets, distinct from the language's value limits and whole-process
memory. Data, node-directory and call-workspace backing must be accounted for.

Raw callers initialize the status slot to InvalidRequest before invocation.
Validation failures discovered before safe non-overlap checks leave the status
slot untouched, because writing through it could mutate protected storage. In
particular, ABI-v1 rejection reads only the version prefix and writes neither
status nor outputs. The managed wrapper initializes the status automatically.

Capacity diagnostics report required totals, not shortfalls: scratch construction
reports used payload bytes plus the new payload and current nodes plus one;
promotion reports unique reachable payload bytes and node count. Separate byte
and directory limits allow either to fail independently.

The first runtime implementation validates the existing graph during construction
and access. Repeated construction can therefore cost quadratic time. This is a
known performance limitation to measure and improve after conformance, not an
efficient native release-runtime claim.

## Semantic value limits

The interpreter checks values at block entry, after every executed instruction,
and at final output. It checks stack roots then locals in slot order. Sharing
counts per occurrence for semantic limits, while physical promotion counts
unique nodes. These must remain separate.

For the supported fixed-layout types, depth, expanded nodes and estimated output
bytes depend only on type. Memoized saturated type metrics can generate runtime
checks without materializing root arrays after every instruction. Checks remain
conditional on the executed path and follow successful operation/validator
execution; they cannot turn an untaken runtime failure into a compile failure.

Limits remain depth 256, expanded nodes 100,000 and estimated bytes 8,000,000.
Preserve Core's own-type-description checks, child traversal order, per-value
check order and aggregate-root check order, with identical diagnostics and no
extra language fuel. Include long nominal names and repeated roots: inspection
found that prior native scalar execution did not yet implement these aggregate
value checks.

## Acceptance and validation

- Differential O0/O2 tests against interpreter and independent expected values,
  diagnostics and steps for nested records, refined fields, accessors, generated
  aliases, calls, locals, branches, empty records and structural equality.
- Retained decoding after scratch overwrite/reuse and compiled-program disposal;
  shared child identity preserved across output roots.
- Exact-fit and one-short byte/node budgets, output canaries and failed promotion
  atomicity. Native capacity failures are explicit resource outcomes.
- Direct native negative tests for stale generations, wrong owners/types,
  invalid indices/spans and self/forward edges without invalid memory access.
- Independent ABI-v2 layout fixture; historical v1 fixture retained; version
  rejection before accessing the enlarged context.
- Depth, alias-expanded nodes, stack-plus-locals and estimated-output boundaries,
  including the nominal scalar aggregate-limit gap.
- Existing native scalar tests remain passing. Build runtime objects fresh for
  each native artifact; preserve frozen Release experiment binaries.

Run the exact optional native gate and the direct runtime safety harness locally,
then applicable IR/interpreter regression tests in isolated artifact directories.
Record terminal results and source/runtime/IR/object hashes before publishing.
No native throughput, mailbox or general lifetime-safety claim follows from this
slice alone. Comparative agent-reliability advantage remains unproven.

## Results and evidence

The exact gate `pwsh -NoProfile -File scripts/Verify-NativeConformance.ps1`
passed **342 assertions**, with fresh O0/O2 native artifacts and zero build
warnings/errors. It covers nested/refined records, generated accessors and aliases,
structural equality, branches/locals/calls, depth and aggregate value limits,
untaken over-limit paths, byte/node capacity boundaries, failed promotion guards
and independent retained ownership. First decoding after scratch reuse, peer
result disposal and compiled-DLL disposal verifies actual native ownership rather
than a previously cached managed value.

The direct C harness passed **77 assertions at each of O0, O2 and O1 with
undefined-behavior traps**. All three runtime objects have no undefined symbols.
C size/alignment/offset measurements match the ABI fixture in **43 checks**;
the integrated gate also compares managed and emitted layouts. All **94 generated
DLLs** have no PE imports or CLR header, and every extracted runtime source/header
matches the tested source bytes. These artifact checks do not make the F# host or
current metadata wrapper a compiler-free release application.

Fresh isolated IR tests passed 114 assertions and interpreter tests passed 31.
All ten frozen agent-experiment runtime files remain unchanged. No Core semantics,
verifier, interpreter or frozen research binaries changed. Validation is local;
CI remains manual-only.

[Evidence index](evidence/106-native-record/index.json) links the complete native
run, C harness logs and commands, layout measurements, regression logs, source
and generated-IR archives, artifact hashes and frozen-runtime checks.

## Review and failed attempts

Review fixed shared result metadata being cleared during disposal, an early
scratch-allocation cleanup gap, disposal racing DLL execution, unsafe status
writes before overlap checks, and 32-bit root-offset arithmetic. Type-graph and
metric traversal now use explicit stacks; review checked cycles, diamond sharing
and diagnostic ordering. The context layout export includes all 11 fields.

Three failed gate attempts are preserved in the evidence. The first exposed a
test expecting a status write on early ABI-v1 rejection. The second terminated
with Windows heap-corruption status 0xC0000374: a raw test helper wrote a canary
past its allocation. It now reserves the extra slot while advertising only the
logical public output capacity. The third exposed an incorrect fixture span:
a saved O root (6,094,686 estimated bytes) plus two M roots (1,523,550 each)
exceeds the 8 MB semantic limit at duplication, before the final constructor.
Independent byte/node arithmetic corrected the expected first-failure location.
The fourth full fresh gate passed; none of the failed runs is counted as success.

## What this establishes

Actual language records now execute in a native scratch arena and survive its
reclamation through bounded, alias-preserving promotion into an independent
native owner. Semantic limit checks match the interpreter while physical storage
counts unique nodes. This is a useful ownership boundary for the proposed
per-turn mailbox design.

It does not yet provide mailbox state, scheduling, suspension, cancellation or
real I/O. Graph validation may be quadratic; the current default capacities and
F# embedding are not optimized server measurements. Strings, containers, general
native release packaging and JIT remain future work. This milestone adds no new
agent comparison: [report 101](101-approach-efficacy-review.md) and
[report 102](102-guided-defect-repair-comparison.md) still support feasibility and
reuse, with comparative reliability superiority unproven.
