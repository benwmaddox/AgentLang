# 208 — Thirty-second sixteen-slot mailbox confirmation

Status: nine-trial measurement complete; copy-accounting endpoint unmeasured.
No native performance qualification at the predeclared point. Starting revision `d6e1929`, with the focused runner
change. Run `8e5d6ef649044874ba7c4635cd24df5b`, 2026-10-11 08:32–08:38 UTC.

All nine measured trials pass behavior, echo/final-state checks, accounting and
bounded drain. F# meets the rejection/latency gate in all three repeats; native
RETURN and KEEP miss the rejection gate in all three. Native process memory is
lower in this fixture. This does not select a winning arena policy, establish a
throughput advantage, or change the agent-efficacy conclusion in report 203.

The [frozen plan](../experiments/AgentLang.MailboxLoad/confirmation-208.md)
closes report 140's missing long-run sixteen-slot follow-up. Three rotated
repeats per variant use 500 offered requests/s, 4,096-byte payloads, 5 ms provider
delay, 1 s warmup, 30 s measurement, and the same enforced 256 MiB application
process-commit ceiling. Native variants reserve sixteen scratch slots; F# uses
normal immutable state/GC. Qualification requires all three repeats to pass
correctness, reject at most 1%, and have p99 at most 50 ms.

| Variant | Repeat | Completed/s within window | Rejected | p99 ms | Qualifies |
| --- | ---: | ---: | ---: | ---: | --- |
| F# | 1 | 497.07 | 0.533% | 32.947 | Yes |
| F# | 2 | 497.17 | 0.513% | 32.373 | Yes |
| F# | 3 | 497.47 | 0.453% | 32.177 | Yes |
| RETURN-16 | 1 | 492.93 | 1.367% | 33.332 | No |
| RETURN-16 | 2 | 492.20 | 1.513% | 32.908 | No |
| RETURN-16 | 3 | 490.67 | 1.813% | 33.555 | No |
| KEEP-16 | 1 | 492.57 | 1.433% | 33.383 | No |
| KEEP-16 | 2 | 492.90 | 1.373% | 32.742 | No |
| KEEP-16 | 3 | 491.93 | 1.567% | 33.139 | No |

Median completed/s is 497.17 for F#, 492.20 for RETURN and 492.57 for KEEP.
Median p99 is respectively 32.373, 33.332 and 33.139 ms. Native policy differences
are small in these samples and do not establish a causal policy benefit.

Process-memory medians and observed ranges, in MiB:

| Variant | Peak committed: median [range] | Peak working set: median [range] |
| --- | ---: | ---: |
| F# | 28.32 [28.30–28.43] | 54.27 [54.20–54.36] |
| RETURN-16 | 8.25 [8.23–8.31] | 6.22 [6.22–6.22] |
| KEEP-16 | 8.24 [8.24–8.30] | 7.47 [7.47–7.47] |

Both native policies reserve exactly 6,317,704 caller-storage bytes. Reservation,
commit and working set are distinct quantities. All ceilings are nonbinding;
raising a ceiling would not explain these observations. The provider is measured
separately and excluded from application memory. Working-set queries succeed.
The load host does not emit copy-byte counters, so the plan's copy-accounting
endpoint is unmeasured. Do not infer zero copying or a copy-cost benefit from
the policy names; earlier mechanism tests remain separate evidence.

Command: `pwsh -NoProfile -File scripts/Measure-MailboxLoad.ps1 -Confirm16Slot`,
on the host because report 207 established sandbox socket restrictions. Fresh
builds, launcher memory-cap controls and all eleven smoke cases pass before the
nine measurements. PowerShell syntax, conflicting-mode rejection before setup,
and final whitespace checks pass. Default full-matrix and smoke execution paths
remain available; CI remains manual. No runtime, handler or provider code was
changed, and sockets remain bounded and loopback-only.

All 94 captured source hashes remain unchanged. The runner does not include
the plan in that list: a separately recorded pre-launch plan hash
`d5ab9db071fcf7afe8e4fd9c40d354fd4ebfe900e67e9f86246fe2633fb2e8b1`
also matches after completion. Independent source review finds no blocker in
the new mode's gates/order/claim scope. Root rechecks all twenty raw trials,
request/mailbox/provider conservation, nearest-rank p99, per-trial and per-group
qualification, exact nine-trial configuration and repeat sets. These checks
match the runner; saved-data audit does not replace runtime content validation.

The [archive](evidence/208-sixteen-slot-confirmation/validation.zip) and
[index](evidence/208-sixteen-slot-confirmation/validation.index.json) retain raw
samples, exact commands/logs, frozen plan/runner, source snapshots, root audit
and review. Root reopened and verified all 379 member lengths/hashes.

This workload schedules arrivals inside each application, includes driver/polling
overhead, uses one CPU per application, and is not an external HTTP service.
Each trial offers 15,000 requests; native rejection counts are 205–272, versus
68–80 for F#. Recorded missed arrivals account for only 0–6 rejected requests
per trial, so missing end-of-window dispatch alone cannot explain all rejection.
The remaining causes are not isolated here. Do not tune thresholds or rerun
individual failures. Keep both policies; investigate dispatch/polling/admission
timing before another performance ranking, rather than treating shorter samples
as sustained results or building a large new benchmark framework.

Primary agent efficacy remains at [203](203-efficacy-decision-after-organic-creation.md):
discovery and composition demonstrated on bounded tasks; comparative edit
reliability and benefit from organically accumulated vocabulary unproven.
