# 214 — Native completion-service diagnostic

Date: 2026-10-11. Decision: accept an opt-in diagnostic; retain one-packet
service as default and both arena policies. No throughput qualification or
memory-policy selection follows from this run.

The native fixture can now drain already-queued IOCP completions at each existing
service point. This isolates one driver choice while holding arrival batch size,
synchronous sends, handler, provider and arena policies fixed. The actual parsed
mode is reported and validated; trial/group identities separate the modes. The
semantic IR, language compiler/runtime ABIs and effect permissions are unchanged.

Fresh local validation ran `pwsh -NoProfile -File scripts/Measure-MailboxLoad.ps1
-DriverServiceDiagnostic`. It rebuilt the launcher, LLVM module, native host,
provider and F# reference, including C warnings-as-errors and four launcher
controls. Eleven smoke and fifteen diagnostic trials passed state, protocol,
accounting, native-copy, memory-cap and bounded-drain checks. All 94 source pins
remained stable. All application/provider exits were zero with no errors,
timeouts or pending work after drain. Three parse-only negative controls reject
unknown, invalid and duplicate mode arguments before network work.

Each diagnostic group has three rotated repeats at 64,000 offered arrivals/s,
4,096-byte payload, 5 ms provider delay, 100 ms warmup and 250 ms measured duration.
The application job commit limit is 256 MiB; provider memory is measured separately.

| Variant | Completed within window per second | Rejected fraction | p99 latency, ms |
| --- | --- | --- | --- |
| F# reference | 896–960 | 98.4–98.5% | 33.769–58.270 |
| RETURN, one-packet | 832–968 | 98.3875–98.6% | 30.995–32.205 |
| RETURN, drain-ready | 832–1,024 | 98.3–98.6% | 27.170–40.758 |
| KEEP, one-packet | 828–904 | 98.49375–98.60625% | 32.552–40.003 |
| KEEP, drain-ready | 960–1,024 | 98.3–98.4% | 17.759–29.016 |

Draining ready completions improves the observed KEEP ranges in this short
fixture; RETURN ranges overlap. Every group fails the rejection qualification
gate. Several arms also have substantial missed arrivals. This is an overload
diagnostic, not maximum sustainable throughput, a causal explanation of the
500/s result in report 208, or a general advantage over F#. A focused long-run
comparison at that earlier workload is the next useful native measurement;
keep this diagnostic optional until then.

Independent read-only source review checks default behavior, error completions,
zero-timeout exhaustion, option validation, grouping and unchanged ABI/capability
boundary. A separate raw-result audit reconciles all 26 application/provider
runner receipts, mailbox counts, mode identities, executable hash and cleanup.
The source reviewer and raw-result auditor have different roles; the latter also
authored the bounded implementation. No language efficacy claim derives from
this native experiment.

The first sandbox attempt fresh-built successfully but stopped at the first F#
smoke trial because loopback socket access was denied. Its failed receipts are
preserved separately. The successful host retry uses the existing provider bound
to `IPAddress.Loopback`, bounded jobs and local output directories. It adds no
public listener, .NET escape hatch or new language effect grants. This validates
the bounded fixture, not a comprehensive security audit.

[Validation archive](evidence/214-completion-service-diagnostic/validation.zip)
and [index](evidence/214-completion-service-diagnostic/validation.index.json)
include source snapshots, raw receipts, parse controls and reviews. The failed
sandbox attempt has its own [archive](evidence/214-completion-service-diagnostic/sandbox-attempt.zip)
and [index](evidence/214-completion-service-diagnostic/sandbox-attempt.index.json).
All archive members are reopened and checked by length and SHA256; compiled
binaries and build intermediates are omitted, with build identities retained.
