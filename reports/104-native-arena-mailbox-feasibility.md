# Native arena and mailbox ownership comparison

Status: standalone feasibility comparison complete, 2026-10-07. This is a
standalone native experiment following scalar LLVM conformance (report 103),
not an arena allocator integrated into AgentLang.

## Question and scope

Can a single execution lane suspend a mailbox, retain only the data needed for
resumption and provider access, and safely reuse its scratch backing storage?
Compare that candidate with retaining scratch for the whole request. Measure
real native allocation, copying, cleanup and process memory before changing the
language's memory semantics.

The C11 probe uses explicit bounded structs and Win32 allocation, with no new
package dependencies. C is an experiment implementation choice, not a decision
about the eventual production runtime. The existing F# frontend, verified IR,
interpreter and LLVM backend remain unchanged.

## Fixed comparison contract

Before collecting results, fix the following matrix:

- Clang 19.1.5, fresh builds at O0 and O2.
- Per-turn and whole-request modes, alternating their order by repeat.
- Six workloads: CPU control, delayed provider with small continuation,
  delayed provider with large continuation, slow outgoing consumer, cancellation,
  and a retained-only control with no disposable intermediate working data.
- Three repetitions, 32 offered requests, seed 17: 72 matched runs.
- Sixteen KiB native chunks and a one MiB shared cache cap for both policies.
  The first five workloads allocate and consume 512 KiB of disposable scratch;
  retained-only keeps the large graph but omits that disposable working set.
- Eight MiB application allocation budget, including fixed state/metadata and
  cached arena backing; 64 MiB whole-process committed-memory ceiling.
- One execution thread and fixed mailbox identities. A suspended mailbox queues
  unrelated messages FIFO; completion/cancellation controls resume the same one.
- One pending operation per mailbox. Buffers stay owned until the provider or
  outgoing consumer acknowledges release. A cancel request alone releases none.

Review before measurement added the retained-only control and explicit disposable
scratch: the original graph-only shape would measure copying retained data but
would not test releasing dead intermediates. The shared cache holds a complete
working set instead of forcing nearly all pages to be freed every turn.

The provider uses deterministic logical delays. Logical ticks are not network
latency, and elapsed probe time is not saturated-server throughput. Memory
capacity rejections are observable outcomes, not successful completions.

The native program enforces the process ceiling using a Windows Job Object and
verifies it by rejecting an over-limit allocation. The separate arena budget
counts backing chunks even while cached. `arenaReservedBytes` and its peak count
committed 16 KiB chunk backing,
including cached chunks; they do not measure the virtual-address extent Windows
reserves. Used payload excludes alignment padding, covered by the full-chunk
budget. Volatile writes and reads ensure scratch pages are touched. Private
bytes and working set are sampled after drain; the Job Object supplies the
separate peak process-commit measurement. See Microsoft's documentation for
[VirtualAlloc](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-virtualalloc),
[job memory limits](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_extended_limit_information)
and [process memory counters](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex).

## Acceptance

Safety checks cover generation-checked handles after physical reuse, bounded
nested promotion with aliases and nominal tags, atomic failed promotion,
retained output after scratch reset, queue/pending/output capacity, cancellation
before/after readiness, duplicate/late completion, failure cleanup and repeated
reuse. Draining must release all dynamic owners and backing allocations while
preserving declared mailbox state.

An independent PowerShell checker verifies every offered ID, terminal status,
completed value and resource invariant. The completed-value oracle is unsigned
32-bit `(seed * 1000003 + (id + 1) * 97 + 0x41C64E6D)`; the native handler must
read and validate its retained graph to produce that value. Every non-cancellation
workload must complete all 32 requests; cancellation
must cancel exactly the 11 IDs divisible by three and complete the other 21.
Every mailbox is `id % 8`. Capacity rejection is tested separately and cannot
substitute for success in this non-pressure matrix. Completed values must agree
across modes; identical configurations must preserve outcomes and counters
across optimization and repetition.
Checks reject missing IDs, inconsistent counts, wrong values, unreturned owners,
false process-limit enforcement and budget violations. All native build/run
outputs and hashes accompany the results.

## Results

The fixed matrix passed all 72 runs and 5,154 independent checker assertions.
There were 2,304 offered requests: 2,172 completed with the expected value and
132 were cancelled as specified. No request failed or was rejected. Both fresh
warning-as-error builds passed 248 native safety assertions each. All 13 checker
controls passed, including valid examples and deliberately corrupted outputs.
A separate O1 build with undefined-behavior trap instrumentation also passed the
248 safety assertions. It is outside the memory/time comparison.

Peak arena backing and allocation counts below were identical across O0/O2 and
all three repeats. KiB means 1,024 bytes. Copy bytes are cumulative across 32
requests; allocation calls count actual backing allocations, not arena objects.
Every allocation had a matching free after drain.

| Workload | Per-turn peak KiB | Whole-request peak KiB | Per-turn copy bytes | Allocation calls, turn / request |
| --- | ---: | ---: | ---: | ---: |
| CPU control | 560 | 560 | 0 | 35 / 35 |
| Delayed, small continuation | 912 | 4,480 | 7,680 | 57 / 280 |
| Delayed, large continuation | 912 | 4,480 | 265,728 | 57 / 280 |
| Slow outgoing consumer | 1,040 | 4,608 | 7,680 | 65 / 288 |
| Cancellation | 912 | 4,480 | 19,968 | 57 / 280 |
| Retained-only control | 400 | 384 | 265,728 | 25 / 24 |

Whole-request mode copied zero continuation bytes in every workload. The
33,752 bytes of fixed metadata are charged separately and equally to each
application budget. All dynamic arena/provider/output bytes and owners were
zero after drain; static mailbox state survived. Cancellation left provider
ownership unchanged until acknowledgement, and safety tests separately exercised
outgoing-buffer cancellation and invalid/late completions.

Per-turn peak backing was 79.6% lower in the delayed and cancellation workloads,
and 77.4% lower with slow outgoing consumers. Independent peak process commit
also moved in the expected direction: delayed-small used 1,440–1,456 KiB per-turn
versus 5,040–5,048 KiB whole-request across all builds/repeats. The retained-only
control reversed the result: per-turn used 4.2% more arena backing and copied
265,728 bytes; peak process commit was 932–940 KiB versus 912–924 KiB.

These are results for a deliberately fixed 512 KiB disposable working set,
eight mailboxes and one MiB cache. They do not estimate typical application
memory savings. The result supports the proposed mechanism: releasing dead
working data at suspension can reduce retained backing, while promotion adds
cost when almost everything must survive. Keep per-turn scratch as the leading
candidate; do not yet freeze the language's allocation semantics.

Elapsed QPC measurements are retained in the evidence as descriptive probe
measurements. No throughput or latency winner is declared: logical delays are
simulated, the load is not externally paced, and neither policy is saturated.
The next useful runtime step is a narrow verified-IR lifetime/retained-value
contract and native conformance slice, followed by real I/O under equal memory
and tail-latency budgets. Idle backing release and mailbox pool sizing remain
separate experiments.

## Reproduction and review

Run locally on Windows x64 with Clang and the Windows SDK installed:

```powershell
pwsh -NoProfile -File scripts/Verify-NativeArenaMailbox.ps1
```

The runner builds fresh O0/O2 executables in an isolated ignored directory,
requires safety checks before the matrix, validates every output against a
separate arithmetic oracle, and records exact commands, raw output, source/tool/
executable hashes and failures. See [probe documentation](../experiments/native-arena-mailbox/README.md)
for workload shapes and ownership boundaries.

[Saved evidence](evidence/104-native-arena/index.json) indexes the full comparison,
the contract saved before measurements, exact source/runner archive, sanitizer
attempts, frozen-runtime checks and a compact result summary. The optional
reporting UBSan runtime failed to link with the installed Windows libraries;
Clang's runtime-free `-fsanitize=undefined -fsanitize-trap=undefined` build
succeeded. No missing runtime was replaced with a stub. Trap instrumentation
covers the exercised safety tests and is not a memory-safety proof.

Independent source review found and resolved missing immediate output
acknowledgement, insufficient output-span validation, conflicting cancellation
fixtures and missing real handler-failure cleanup tests. Coordinator execution
also found a PowerShell automatic-variable collision and a nested checker-array
return that hid failed validation controls; both were fixed before accepted
measurement. The final source was normalized to repository LF rules and rebuilt.
The retained-only control and working-set/cache choices were fixed before the
first successful comparison, not selected after observing a result.

Existing language/compiler code and frozen experiment binaries are unchanged.
No existing-language suite was rerun for this isolated C experiment; its fresh
native checks and the focused checker controls are the applicable validation.
CI remains manual-only. F# and C are current implementation tools, not product
requirements; future choices should preserve semantics and reduce maintenance
risk.

## What this cannot establish

Checked handles in a trusted C probe do not establish language-wide escape
analysis, general nominal/container promotion, strict LIFO allocation or an
arena-backed AgentLang data stack. The prototype fixes handler version and does
not test dictionary replacement or suspended-code generation retention.

The comparison is not a web server and has no real asynchronous I/O. A later
throughput decision still needs an external scheduled-arrival load generator,
real provider/slow-client behavior, equal process ceilings, predeclared latency
and error budgets, saturation/overload sweeps and repeated qualifying results.
It also does not alter the efficacy conclusion: agent composition and repair
are feasible, but comparative reliability superiority remains unproven.
