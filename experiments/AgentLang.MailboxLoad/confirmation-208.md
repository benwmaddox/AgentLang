# Fixed-point sixteen-slot confirmation (208)

Freeze this plan before the first `-Confirm16Slot` run. Do not select rates or
change qualification from the observed results.

Question: does the adequately sized native pool from report 140 preserve
correct behavior and acceptable throughput/tail latency over 30 seconds at the
previously tested 500/s, 5 ms provider-delay point? This is a bounded internal
arrival/loopback workload, not an external-ingress server or agent-efficacy study.

Reuse the existing fresh builds, launcher controls, eleven smoke cases, handler
semantics, provider, independent content/accounting checks and source-hash gate.
Then run exactly three repeats of each variant: native RETURN with sixteen
scratch slots, native KEEP with sixteen scratch slots, and ordinary F# GC.
Rotate order F#/RETURN/KEEP, RETURN/KEEP/F#, KEEP/F#/RETURN so each occupies
each order position once.

Every scored trial uses 500 offered requests/s, 4,096-byte payloads, 5 ms
configured provider delay, 1,000 ms warmup, 30,000 ms measurement, the existing
five-second drain deadline, and a 256 MiB application Job Object process-commit
ceiling. Provider is measured separately; same CPU/affinity policy as the
established contract. No hidden queue, external dependency, handler tuning,
ceiling increase or rate search. KEEP retains actual live scratch while pending.

Preserve every trial, including performance failures and invalid executions.
Correctness failures stop the existing runner and invalidate a complete paired
comparison; no restart/selection of an individual bad sample. Each variant
qualifies only if all three repeats pass existing correctness checks, reject at
most 1% and have p99 latency at most 50 ms. Nonqualification is a research
outcome, not a reason to change thresholds. Check nine trials, three groups,
three distinct repeats per group, raw request/mailbox/provider conservation,
and recompute nearest-rank p99 and group qualification from saved samples.

Report each repeat and ranges/medians for completed-within-window throughput,
rejection, p99, peak process commit and working set; retain distinct native
reservation/copy accounting. Do not add together memory measurements or equate
commit with resident memory. Report nonbinding ceilings. Three 30-second runs
at one rate are not a highest capacity estimate, universal policy choice,
production certification or comparative agent reliability result. Keep report
140's historical results unchanged.

Command: `pwsh -NoProfile -File scripts/Measure-MailboxLoad.ps1 -Confirm16Slot`.
Run on the host if sandbox loopback socket restrictions recur. Validate syntax,
mode exclusivity before builds, final source stability and whitespace locally.
Save reports and sealed source/raw evidence before merge/push.
