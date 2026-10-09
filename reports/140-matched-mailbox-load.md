# 140 — Matched mailbox load comparison

Status: bounded comparison complete; independent audit passed. Starting revision:
`d8c65f2`. Measurement: 2026-10-09, 03:07–03:26 UTC.

This experiment measures the current native owning-value runtime against an
equivalent F# request-orchestration workload. Native process memory is lower in
this fixture, and keeping an adequately sized arena pool recovers throughput
lost by a four-slot KEEP pool. A general throughput advantage is not established:
native RETURN's best short-run point failed its 30-second confirmation. This
experiment is separate from research into AI developers' edit reliability.

The [predeclared contract](../experiments/AgentLang.MailboxLoad/CONTRACT.md)
defines exact handler behavior, deterministic scheduled arrivals, bounded
admission, memory enforcement, latency and success gates, repeated trials, and
the arena-capacity follow-up. The native application runs the existing compiled
Flow/2 mailbox handlers; the F# baseline implements their transitions using
ordinary immutable records and strings. Both use a shared persistent-connection
echo protocol with the same payload and delay settings.

The initial 16-mailbox/four-scratch-slot comparison exposes the cost of pinning
arenas during I/O. It cannot alone establish the best KEEP configuration. The
paired 16-slot follow-up tests that distinction without changing the process
memory ceiling. Actual reservation, peak committed memory and peak working set
remain separate measurements below.

## Results

The final run contains 197 valid trials: 11 smoke cases, 126 primary grid trials,
six 30-second confirmations, 12 native +25%-ceiling trials and 42 sixteen-slot
capacity trials. Every trial passed state, protocol, drain and request-accounting
checks. Admission rejection is a measured outcome, not a correctness failure.
All 93 captured build/measurement source inputs remained unchanged. An independent Python
audit recomputed percentiles, qualification, grouping and follow-up selection
from raw files and verified the source hashes.

A rate qualifies when all three grid repetitions have at most 1% rejection,
p99 at most 50 ms, and no correctness failures. These are highest qualifying
**tested points**, not exact capacity estimates or monotonic operating ranges.

| Application, four native scratch slots | Configured I/O delay | Highest qualifying 3 s point | 30 s completed/s | 30 s rejected | 30 s p99 | Confirmation |
|---|---:|---:|---:|---:|---:|---|
| F# | 0 ms | 1,000/s | 997.4 | 0.23% | 15.70 ms | Pass |
| Native RETURN | 0 ms | 8,000/s | 4,460.7 | 44.24% | 14.21 ms | **Fail** |
| Native KEEP | 0 ms | 250/s | 249.6 | 0.15% | 15.66 ms | Pass |
| F# | 5 ms | 500/s | 498.7 | 0.20% | 31.57 ms | Pass |
| Native RETURN | 5 ms | 500/s | 498.8 | 0.19% | 31.45 ms | Pass |
| Native KEEP | 5 ms | None at 250–16,000/s | 128.5 at 250/s | 48.57% | 31.79 ms | Fail at fallback point |

Native RETURN without configured delay passed 250, 500, 1,000 and 8,000/s short
points, but failed 2,000, 4,000 and 16,000/s. Its high-rate response is therefore
non-monotonic and unstable over the longer run. Do not turn the 8,000/s short
point into a claimed eightfold advantage. Lower native no-delay rates were not
given additional 30-second confirmations after the selected point failed.

At the matched **500/s, 5 ms configured-delay** point, medians of three short
runs show the memory/capacity tradeoff:

| Application | Completed/s | Rejected | Peak committed MiB | Peak working-set MiB | All three qualify? |
|---|---:|---:|---:|---:|---|
| F# | 495.7 | 0.27% | 26.20 | 50.15 | Yes |
| Native RETURN, 4 slots | 494.3 | 0.60% | 5.18 | 6.13 | Yes |
| Native KEEP, 4 slots | 128.3 | 74.13% | 5.16 | 6.29 | No |
| Native RETURN, 16 slots | 497.0 | 0.07% | 8.15 | 6.12 | Yes |
| Native KEEP, 16 slots | 496.3 | 0.20% | 8.14 | 7.37 | Yes |

Both native policies reserve exactly 3,169,448 caller-storage bytes with four
slots and 6,317,576 with sixteen slots, an increase of 3,148,128 bytes. Reserved
storage, committed memory and resident working set are different measurements;
the table does not substitute one for another. All final process working-set
queries succeeded, so these peaks are not labeled sampled lower bounds.

KEEP's large four-slot rejection is primarily a capacity finding: it pins arenas
while waiting, and sixteen slots remove that constraint at this matched point.
Small differences between RETURN pool sizes are not evidence of a causal pool
benefit. The sixteen-slot follow-up has only three-second trials; it does not
establish long-run stability or a universal KEEP/RETURN winner. Without configured
delay, sixteen-slot KEEP qualifies at 250 and 500/s, but not 8,000 or 16,000/s.

The equal 256 MiB Job Object ceilings were nonbinding. Across all final trials,
the largest reported application peak commit was 8.54 MiB native and 32.28 MiB
F#. Raising the native ceiling to 320 MiB did not change arena reservation.
Those twelve extra-ceiling trials cannot establish a benefit from extra memory;
the separate sixteen-slot comparison actually changes the pool capacity.

## What this means for the design

The native path is small on this workload, and both arena policies remain viable.
Returning arenas before I/O helps when scratch slots are scarce. Keeping arenas
can be practical with a larger pool, without approaching the F# process footprint
in this fixture. Do not make returning storage at every suspension an irreversible
language rule on this evidence alone.

Throughput is strongly affected by the host/arrival implementation. Both
applications generate arrivals internally; the native host uses IOCP and bounded
synchronous sends, while F# uses async sockets and a completion channel. The
observed roughly 16/32 ms latency bands, low CPU use at several points, and
non-monotonic native results warrant a scheduling investigation. They do not
measure the language's intrinsic compute ceiling. An external-ingress workload
would be needed for a saturated web-server comparison. That is later performance
work, not a prerequisite to the next agent-efficacy study.

This experiment requires no general actor framework. It exercises explicit
mailbox state, single-owner handlers, suspension and bounded scratch storage.
Whether the eventual language exposes specialized data-flow mailboxes or a
broader actor system remains open. The next primary research step is the held-out
shared-rule maintenance comparison described in the roadmap, using existing
retained-language and F# projects and independent behavioral acceptance.

## Limits fixed before measurement

- Internal open-loop scheduling is part of each application. This is a bounded
  request-orchestration benchmark, not an external HTTP ingress/server benchmark.
- Native uses trusted-generated O2 AOT; F# uses Release JIT. Warmup precedes
  measurement, and application state is reset after a full warmup drain.
- The .NET SDK is 9.0.308, with no captured GC/tiering environment overrides.
  Every application is pinned to logical CPU mask `0x1`; the provider uses `0x2`.
  Distinct logical CPUs do not prove distinct physical cores or a dedicated host.
- Every scheduled arrival must be accounted for, including missed or rejected
  arrivals. Drain completions do not increase scored throughput.
- The shared provider is measured separately from each application's enforced
  process-commit limit. A provider bottleneck prevents a language throughput
  conclusion.
- Three-second grid points are exploratory until a qualifying point survives
  the predeclared 30-second confirmation.
- Process memory includes benchmark bookkeeping and raw latency samples as well
  as the application/runtime. Arena reservation is reported separately; this
  experiment does not isolate the minimum production runtime footprint.

## Validation and retained preparation failures

The unchanged one-shot provider mode passed a fresh local
`scripts/Verify-OwningMailboxRealIo.ps1` run: 12 native runs across O0/O2,
diagnostic/fast/trusted-generated profiles, and RETURN/KEEP policies; 209,798
checks passed with no failures. All acceptance source hashes remained stable.
Evidence: `.agentlang/owning-mailbox-003/io-run-240c92345055466f8c50160ed9c91e06/io-evidence.json`.
Provider counters reconciled at 144 accepted connections, 132 completed responses
and 12 expected I/O failures in cancellation cases, with other error counters zero.
An earlier passing run before the persistent-shutdown refinement is retained at
`.agentlang/owning-mailbox-003/io-run-22b39c4017094cb89df40ae1566d9e4f/`.

Review corrected completion timing to include owner-side state publication,
persistent-connection timeout handling, connection-versus-frame accounting, and
the distinction between pending I/O and occupied scratch slots. These corrections
precede scored trials; they are not throughput results.

The integrated smoke passed 11 cases with stable source hashes, independent
arrival/state/frame accounting, and zero application/provider errors:
`.agentlang/mailbox-load-140/0ba8d02146204012991e298f084e513f/run-report.json`.
It covers normal load, overload, native RETURN/KEEP with four slots, and both
policies with sixteen slots. Separate launcher controls verified successful
64 MiB allocation under a 128 MiB ceiling, denied allocation under a 16 MiB
ceiling, timeout termination, and capture of a failing child's exit code/output.

The first launcher smoke is retained at
`.agentlang/mailbox-load-140/4e1e4ffd0d2c4e8fb878a63fe70b5d80/`.
Its exact-peak assertion was too strict: the 16 MiB Job Object denied the probe's
allocation after 14,893,056 payload bytes, but reported a peak 12,288 bytes above
the configured ceiling. The final paired controls verify allocation behavior
and retain the reported peak/overshoot without pretending Windows reports a
byte-exact upper bound. Five additional unscored direct application smokes in
that directory passed while the launcher assertion was being corrected.

The first scored attempt is retained at
`.agentlang/mailbox-load-140/e423e319e2ad4cd8b069cfdbb336499a/`.
Root stopped it after detecting invalid aggregate selection: `Group-Object rate`
did not read keys from live `OrderedDictionary` trial records, collapsing six
rates into one 18-run group with a missing rate. The selector consequently
skipped the required grid extension and chose fallback confirmation rates.
The completed raw trials remain available, but this run's aggregate selections
are invalid. `stop-audit.json` records the intervention and the exact coordinator
source was captured with a matching pre-run hash. The correction must be tested
with live dictionary objects, not only deserialized JSON, before a full fresh run.
The corrected preflight now verifies 108 live dictionary records form 36 groups
of three, preserves numeric ordering and scratch-slot axes, checks highest/next
rate selection, and proves that one failed repetition disqualifies a rate.

## Reproduction and evidence

Run locally on Windows with .NET 9 and the configured clang toolchain:

```powershell
pwsh -NoProfile -File scripts/Verify-OwningMailboxRealIo.ps1
pwsh -NoProfile -File scripts/Measure-MailboxLoad.ps1
```

The measurement command builds fresh compiler, provider, F# baseline and native
artifacts, then runs controls, smoke, repeated grid, confirmations and follow-ups.
The final run is `.agentlang/mailbox-load-140/a8cfc4b28e0b44d3997463773683387a/`;
`independent-audit.py` and `independent-audit.json` record the separate raw-data
audit. The complete repository Release suite was not rerun: this milestone changes
experimental hosts/provider/coordinator and documentation, not compiler or native
runtime sources. Local focused regressions and fresh builds are the validation
scope; CI remains unused.

The [evidence storage index](evidence/140-matched-mailbox-load/storage-index.json)
documents archived raw results, exact source snapshots, preparation failures and
omitted build products. The [ZIP](evidence/140-matched-mailbox-load/mailbox-load-140-evidence.zip)
contains 4,902 hash-verified entries and is 12,214,929 bytes. Its SHA-256 is
`1d3d0b912633ce17c326c269c0f435740746f683da1ae566cde84b2fb3c28642`.
Root independently verified every entry's size/hash and the archive checksum.
