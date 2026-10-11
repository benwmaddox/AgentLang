# Mailbox rejection and owning-copy diagnostics

Date: 2026-10-11. Starting commit: `89aa146`.
Decision: accept the diagnostic additions for the existing bounded fixture.
This is an unscored correctness smoke run, not a new performance comparison.

The native load host now reports rejection categories and takes one owning-runtime
statistics snapshot after measured work drains. The measurement script validates
those counters independently from the fixture's typed state and continuation
layout. No scheduler, admission policy, socket behavior, handler, runtime ABI,
memory policy or library semantics changed.

## Results

The final local command was `pwsh -NoProfile -File scripts/Measure-MailboxLoad.ps1 -Smoke`.
Fresh launcher, native module, host, provider and F# builds passed, including
launcher memory-cap controls and C warnings-as-errors. All eleven smoke cases
passed protocol, state, accounting and drain checks; all 94 tracked source hashes
remained stable. Eight native cases also passed the independent copy oracle.

Selected rejection counts from that final run:

| Policy / slots / offered rate / I/O delay | Admitted | Rejected | Active mailbox | Scratch capacity | Missed deadline |
|---|---:|---:|---:|---:|---:|
| RETURN / 4 / 500/s / 5 ms | 124 | 1 | 1 | 0 | 0 |
| KEEP / 4 / 500/s / 5 ms | 32 | 93 | 1 | 85 | 7 |
| RETURN / 16 / 64000/s / 5 ms | 254 | 15746 | 15692 | 0 | 54 |
| KEEP / 16 / 64000/s / 5 ms | 255 | 15745 | 15698 | 0 | 47 |

Unavailable-mailbox, runtime-busy, retained-capacity and text-capacity counts
were zero in all eight native cases. Four-slot KEEP exhibits actual scratch-pool
scarcity. Both sixteen-slot overload cases primarily reject an already-active
mailbox identity. These short overload observations do **not** explain the
500/s sixteen-slot rejection result in [report 208](208-sixteen-slot-confirmation.md).

KEEP reports zero begin-publication and resume-root-import bytes in all four
cases, confirming the intended suspension-boundary copy avoidance. At sixteen
slots under overload, RETURN reports 5,988,288 bytes in each of those categories.
Internal deep-copy bytes remain substantial: 14,323,936 for RETURN and 14,381,360
for KEEP in those cases. All eight cases report zero relocation move bytes.
The cases admitted different numbers of requests; these totals are not a matched
per-request performance comparison. Stable arena locations and reset do not by
themselves remove construction copies.

Counters measure logical requested/serialized bytes, not physical memory
bandwidth. Publication bytes include begin-publication bytes; input-import bytes
include resume-root imports. Do not sum overlapping categories. The scope includes
sixteen fresh runtime initializations, measured work and drain, and excludes the
disposed warmup runtime. Final leases, pending mailboxes and pinned scratch are
zero; scratch acquisitions equal returns.

## Oracle and review

An independent source review caught an overly general initial assumption that
handler failures must be zero. A retained-capacity rejection can occur after a
Begin callback has validated its output descriptors. The final oracle accounts
for that attempted callback without counting an unsuccessful publication.

Root calibration accepts all eight saved native positives and one synthetic
postcallback retained rejection, and rejects eight intentionally corrupted
counter fixtures. The synthetic case checks the oracle; it does not execute that
runtime capacity branch. Neither final smoke nor calibration establishes arbitrary
callback-failure accounting. The oracle is specific to this typed ASCII echo
fixture and its current bounded capacities; generalized postcallback scratch
failure accounting needs separate validation before reuse elsewhere.

The initial smoke also passed eleven cases and seven negative calibration
controls. Its evidence was sealed before the review correction. Final evidence
contains the corrected source and calibration. Both archives were reopened and
every member length and SHA256 verified:

- [Initial archive index](evidence/209-mailbox-diagnostics/initial-validation.index.json): 270 members.
- [Final archive index](evidence/209-mailbox-diagnostics/validation.index.json): 272 members;
  archive SHA256 `420367ab0fb22a73973896989f49d61106c75f8aa065710158810721477f7994`.

## Interpretation and next step

Source investigation finds the C driver services one IOCP packet after a catch-up
batch, while the F# driver drains available channel completions. C admission-frame
sends are synchronous; F# sends are asynchronous. These are plausible timing
differences, not demonstrated causes. This slice intentionally leaves both drivers
unchanged. A bounded servicing experiment is the next performance investigation;
retain both arena policies and the existing acceptance gates.

The primary [agent efficacy decision](203-efficacy-decision-after-organic-creation.md)
is unchanged: discovery and composition have been demonstrated in bounded tasks,
but a comparative reliability benefit and organic vocabulary accumulation remain
unproven. Native diagnostics do not answer those research questions.

Security scope is unchanged: the benchmark uses existing bounded loopback I/O,
and adds no external capability, dependency, public listener or language escape
hatch. Trusted generated native descriptor pointers remain a trust boundary;
these diagnostics do not provide an OS sandbox.
