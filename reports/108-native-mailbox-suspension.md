# Native handler mailbox suspension demonstration

Status: bounded host-driven mailbox demonstration validated, 2026-10-07.

## Question

Can actual compiled AgentLang handlers suspend and resume the same mailbox using
only opaque retained state, while another mailbox reuses its scratch backing?
Report 104 tested a standalone C memory model; report 107 established typed
native state re-entry. This experiment connects those pieces without inventing
a language scheduler, new async syntax, or an automatic stack-capture mechanism.

## Frozen scope

Use a small standalone F# experiment host over the existing interpreter and LLVM
APIs. Scheduling and completion delivery are explicit host operations. Handler
logic executes from the same verified semantic IR at O0/O2 and in the interpreter.
This is not a compiler-free native mailbox runtime or a server benchmark.

Two fixed mailbox identities share one bounded scratch arena on one enforced
host thread. Initialization returns persistent State. Begin returns State plus
an explicit Continuation and records one completion token for that mailbox.
The mailbox holds no scratch lease while suspended. Another mailbox can execute
and poison/reset the same backing before the first resumes. Resume accepts the
same mailbox's retained roots and a primitive completion message, returning its
new State. No managed graph decode/re-encode occurs between these invocations.

There is no queued work in this slice: admission to a busy mailbox rejects.
Only one pending token per mailbox exists. Wrong-mailbox, stale and duplicate
completion tokens reject before handler execution. Failed handlers leave the
previous retained owner and pending token unchanged, permitting an explicit
retry. Old state is disposed only after successful replacement. Disposal frees
owners and pool backing; cross-thread operations reject before mutating state.

No real I/O buffers are borrowed, so these tokens do not demonstrate provider
cancellation or safe OS-buffer release. No dictionary replacement/state migration
is implied; all bodies use one verified program snapshot.

## Acceptance and evidence

- Execute A.begin, B.begin/resume using the same scratch, then A.resume; observe
  independent expected results and interpreter/native parity at O0/O2.
- Keep state opaque until the critical reuse sequence completes. Record one pool
  arena owner, zero outstanding leases at each suspension, balanced lease
  return on success/failure, and independent retained byte/node limits.
- Exercise busy admission, wrong/stale/duplicate tokens, handler failure and retry,
  capacity failure, foreign-thread rejection and deterministic cleanup.
- Include readable handler source where the existing frontend API supports it;
  do not add a new production API solely for the demonstration.
- Build fresh isolated artifacts, retain exact commands/source hashes/results,
  and preserve frozen research binaries. No CI auto-runs.

Do not compare this against merely holding an already-reset scratch lease for
an entire request. Both sides would still promote/copy state, and the artificial
reserved backing would bias the result. A fair later comparison needs an actual
scratch-backed suspension-owner alternative with equivalent handlers and limits.
This milestone measures ownership behavior and bounded native storage, not
saturated throughput or an agent reliability advantage. The efficacy conclusions
remain reports 101/102 until a new matched agent comparison is run.

## Implementation and local validation

The handlers load through public Flow/2 parser/lowering APIs into one verified
program. Experiment-local catalog bootstrap registers record constructors and
accessors; production `Runtime.Engine` internals remain private. Typed entry
wrappers call these source-defined handlers against that same program snapshot.

Implementation lives in `experiments/AgentLang.NativeMailbox/`; the host remains
F#/.NET scaffolding. Generated handlers and arena operations use the current
native backend. No solution-wide or production runtime edits were made.

The canonical experiment command is:

```powershell
pwsh -NoProfile -File scripts/Verify-NativeMailboxSuspension.ps1
```

It builds into a new isolated directory, executes the fresh assembly, and checks
only that run's result. An optional `-EvidencePath` copies successful evidence
to a selected output path. Preserve the generated build/run logs and native
artifacts before publication. The fresh canonical run passed; results and exact source hashes are archived
below. Only the experiment and its validation command changed, so production
suites were not rerun after report 107; dependencies were rebuilt in isolation.

The independent expected outcomes and byte/node arithmetic were fixed before
execution in [oracle.json](evidence/108-native-mailbox-suspension/oracle.json).
The source fixture returns two explicit record roots from Begin. A=10/B=100,
deltas 7/3 and divisor 1 yield 17/103; A's next delta 5/divisor 2 yields 19.
One scratch owner has 24 payload bytes and three directory nodes (168 bytes
including its descriptor). Each retained owner reserves at most 16 payload bytes
and two directory nodes (128 bytes including its descriptor). These counts
exclude call workspace, root arrays and managed host/compiler storage; old/new
retained owners overlap during successful replacement.

The single pool deliberately keeps backing available for reuse while no mailbox
holds a lease. Zero leased scratch does not mean zero reserved pool memory or
zero process memory. State replacement also temporarily keeps old and new
retained owners alive together. The experiment must report these distinctions;
it does not implement idle-time trimming or select the final native allocator.

The arena-owner count is not a host allocator-call count: the current native
owner separately allocates its descriptor, payload and node directory. The
experiment reports one shared scratch owner and its configured backing capacity.

## Results and decision

The canonical fresh build passed with zero warnings/errors. The runner passed
120 checks across the interpreter, native O0 and native O2. The external script
also checked backend completeness, numerical outcomes, lease balance and capacity
failures against the pre-execution oracle. A separate coordinator audit checked
exact pool capacities and retained-owner cleanup.

| Observation | Interpreter | Native O0 | Native O2 |
| --- | ---: | ---: | ---: |
| A after first completion | 17 | 17 | 17 |
| B after completion | 103 | 103 | 103 |
| A after second completion | 19 | 19 | 19 |
| Handler invocations, including failures | 9 | 10 | 10 |
| Retained owners created / disposed | 8 / 8 | 8 / 8 | 8 / 8 |
| Scratch owners created | Not applicable | 1 | 1 |
| Scratch lease acquisitions / returns | Not applicable | 10 / 10 | 10 / 10 |
| Outstanding leases at each of three suspensions | Not applicable | 0 | 0 |
| Largest live retained graph, bytes / nodes | Not measured | 16 / 2 | 16 / 2 |

Native execution has one additional invocation because it exercises a seven-byte
retained budget for an eight-byte result. Both modes reject divide-by-zero and
preserve the same pending owner/token for retry. The native capacity failure
likewise preserves pending state. Busy admission, wrong-mailbox, duplicate and
stale tokens reject before invoking a handler. Foreign-thread execution rejects;
cleanup balances all successful owners, is idempotent, and rejects later calls.

A remains opaque while B begins and resumes through the same scratch owner, its
backing is poisoned, and A fails and retries. First decoding occurs afterward.
This validates the intended ownership sequence with actual Flow/2 handlers; it
does not capture an arbitrary suspended language stack automatically.

The six generated DLLs have no PE imports or CLR headers; their extracted C
runtime sources match the unchanged implementation. All ten frozen research
runtime files retain their recorded hashes. Evidence, source/emitted-IR archives,
and independent checks are in [the evidence index](evidence/108-native-mailbox-suspension/index.json).
The tested working tree is based on `23723a6`; the source inventory identifies
this milestone's exact experiment and verification files.

Review removed unbounded completed-token history, corrected cleanup ordering,
and added actual-owner, partial-construction and disposal assertions. An initial
source run failed because it used `State::new` while generated constructors use
`state::new`; the corrected source then passed. This is observed authoring
friction, not a controlled agent trial or comparative efficacy result. The failed
run is preserved. An initial F# runner build also required changing its namespace
to a module; this was host scaffolding, not a language-runtime failure.

Decision: continue the per-turn scratch/explicit-retained-state candidate. The
next native-runtime work is bounded dispatch and pending state outside the .NET
experiment host, with reproducible entry/type metadata for release execution.
Do not infer a final no-heap policy, production throughput, process-memory
savings or an agent reliability advantage from this demonstration. A genuine
whole-request lifetime alternative and real I/O are still needed for the planned
fair comparison; holding already-cleared scratch would not supply that baseline.
