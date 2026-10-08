# Goose and AgentLang memory design

Status: read-only comparison for the 2026-10-08 owning-value-stack clarification.
This note suggests tests; it does not adopt Goose's architecture or change the
current native fixed-record slice.

## Sources and limits

GitHub's public REST endpoint `GET /repos/aardappel/goose/commits/master` returned
[`35368105e3d50341b79a4cb873cc0261e82dfa42`](https://github.com/aardappel/goose/commit/35368105e3d50341b79a4cb873cc0261e82dfa42)
on 2026-10-08 (commit time 05:39:17 UTC). The shell `git ls-remote` attempt
failed because GitHub did not resolve in this environment. Goose's specification
calls itself a v4 draft. I read its README, specification, tutorial, benchmark
design and summary, plus the implementation/test witnesses linked below. The
source and tests were inspected, not executed; Goose was not built and its
benchmarks were not replicated.

AgentLang's target remains the owning-value contract in
[`STACK-ONLY-RESEARCH.md`](STACK-ONLY-RESEARCH.md): compound values own nested
payloads, duplicates are independent, moves invalidate the source, and popping
reclaims the payload without leaving surviving aliases. The goal also requires
physical locality: similar values should sit close together, not form a logical
stack over scattered object allocations. Exact encoding and source syntax remain
open; keeping related payloads close is a requirement.

## Comparison

| Topic | Goose at the pinned revision | AgentLang implication |
| --- | --- | --- |
| Physical locality | Goose nests dynamic values inline and assigns multiple bump-pointer stacks ([README](https://github.com/aardappel/goose/blob/35368105e3d50341b79a4cb873cc0261e82dfa42/README.md)). | Test packed nested records against SoA and flat scans. Physical contiguity is a hypothesis, not a universal win. |
| Lifetimes | Goose bulk-restores scope watermarks; resizables top separate stacks ([spec §1.2–1.3](https://github.com/aardappel/goose/blob/35368105e3d50341b79a4cb873cc0261e82dfa42/docs/goose_spec.md), [codegen_frames.h](https://github.com/aardappel/goose/blob/35368105e3d50341b79a4cb873cc0261e82dfa42/src/codegen_frames.h)). | Use as a bulk-rewind control; test exact per-value pop and non-topmost returns separately. |
| Copy and alias | Fixed values copy; non-fixed lvalues can alias. `copy` is type-limited, nested refs keep roots, and aliasing is allowed ([spec §4.1, §9](https://github.com/aardappel/goose/blob/35368105e3d50341b79a4cb873cc0261e82dfa42/docs/goose_spec.md), [`ImplicitCopy`](https://github.com/aardappel/goose/blob/35368105e3d50341b79a4cb873cc0261e82dfa42/src/typecheck_exprs.h)). | AgentLang duplicates must own independent nested payloads; root inference alone does not provide this. |
| Reuse and returns | `reusable` can leave stale refs observing a reused same-type slot ([spec §5.4](https://github.com/aardappel/goose/blob/35368105e3d50341b79a4cb873cc0261e82dfa42/docs/goose_spec.md), [`inpool_lru.goose`](https://github.com/aardappel/goose/blob/35368105e3d50341b79a4cb873cc0261e82dfa42/test/storage/inpool_lru.goose)). Goose also constructs results at caller destinations ([tutorial](https://github.com/aardappel/goose/blob/35368105e3d50341b79a4cb873cc0261e82dfa42/docs/tutorial.md), [spec §4.3](https://github.com/aardappel/goose/blob/35368105e3d50341b79a4cb873cc0261e82dfa42/docs/goose_spec.md)). | Pool reuse is a control. Measure destination construction only if copy/move/pop semantics remain intact. |
| Queues and evidence | Queues copy flat messages and may allocate at runtime ([spec §11.2](https://github.com/aardappel/goose/blob/35368105e3d50341b79a4cb873cc0261e82dfa42/docs/goose_spec.md), [runtime_threads.h](https://github.com/aardappel/goose/blob/35368105e3d50341b79a4cb873cc0261e82dfa42/src/runtime/runtime_threads.h)). Benchmarks report mixed workload/backend results and a small-run startup floor ([design](https://github.com/aardappel/goose/blob/35368105e3d50341b79a4cb873cc0261e82dfa42/bench/design.md), [summary](https://github.com/aardappel/goose/blob/35368105e3d50341b79a4cb873cc0261e82dfa42/bench/summary.md)). | Treat flat messages as a narrow control. They prove neither bounded nested retention nor AgentLang performance. |

Goose compiles whole programs to C, with optional in-process TinyCC ([README](https://github.com/aardappel/goose/blob/35368105e3d50341b79a4cb873cc0261e82dfa42/README.md)); AgentLang keeps its verified IR, LLVM JIT/AOT, live dictionary, effect checks and library gates ([PRD](PRD.md), [ROADMAP](ROADMAP.md)). This isolates memory design without shifting AgentLang's agent-focused purpose.

“No heap” here means no general-purpose heap for dynamic language values; Goose
explicitly permits runtime queue allocation. AgentLang should likewise report
host/runtime backing separately from language-owned values.

## Prioritized hypotheses and tests

1. **Ownership costs and correctness first.** At the native-buffer layer, copy
   a nested record/list/string, poison or mutate one copy, pop it and prove
   surviving storage remains valid. At the language layer, use pure transforms
   and compare outputs; values stay immutable. Test moves, non-topmost returns,
   branch joins, early errors, repeated work and exact live/reserved-byte
   oracles. Capacity failure must not partially move or publish a result.
2. **Measure locality with representative access patterns.** Hold the data
   constant while comparing packed nested AoS, SoA and flat scalar scans. Include
   small and cache-exceeding inputs; record bytes touched, copy bytes, slack,
   throughput and tail latency. Goose's own summary notes losses in a flat
   float kernel and an image stencil whose bounds-check outcome varied by
   backend ([Goose summary](https://github.com/aardappel/goose/blob/35368105e3d50341b79a4cb873cc0261e82dfa42/bench/summary.md)),
   so physical contiguity is not a universal performance claim.
3. **Test retained-value boundaries explicitly.** Copy or transfer a nested
   value into a bounded mailbox, clear scratch, then read it back. Exceed each
   bound by one and inject failure before publication; verify the chosen state
   contract. If retained handles are explored, delete and reuse a slot and
   reject the old generation. Keep unbounded runtime queues and whole-arena
   retention as separate controls.
4. **Use fair, row-level benchmarks.** Borrow the controls in Goose's
   [benchmark design](https://github.com/aardappel/goose/blob/35368105e3d50341b79a4cb873cc0261e82dfa42/bench/design.md):
   checksum-equivalent workloads, win and loss controls, teardown in timing,
   comparable backends, allocator tiers, peak process memory plus payload, and
   distributions across sizes. Compare AgentLang's managed reference, strict
   owning stack and shared/scoped arenas. Do not transfer Goose's published
   C++/Rust ratios to AgentLang.

The fixed-record implementation slice stays unchanged. These tests add no
Goose-specific restrictions and authorize no architecture switch by themselves.
