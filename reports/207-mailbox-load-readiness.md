# 207 — Current native mailbox load readiness

Status: bounded smoke validation accepted on 2026-10-11 at `d6e1929`.
No language or benchmark source was changed for these runs.

Fresh O2 trusted-generated handlers, native controller/host, F# Release baseline
and shared provider build successfully against the current owning ABI. All
11 host smoke cases pass protocol, state, frame accounting and bounded drain
checks. Both RETURN and KEEP run with four and sixteen scratch slots; F# uses
its ordinary immutable state and GC. All 94 captured source inputs remain
unchanged. This establishes current workload readiness, not sustained throughput
or an agent-edit reliability advantage.

Command: `pwsh -NoProfile -File scripts/Measure-MailboxLoad.ps1 -Smoke`.
The first sandbox run (`bd56926ec1cc4a4093e1cc5f71d201e9`) builds successfully
but its first F# application exits with a socket access-permission error.
It is an invalid comparison, retained separately. The identical host retry
(`79fce6cb7d004abead68e4b38799755e`) exits zero. This required host execution
for local loopback sockets, without modifying firewall settings or dependencies.

The accepted run includes 250/s with zero provider delay, 500/s with 5 ms delay,
and overload at 64,000/s with 5 ms delay, using 4,096-byte payloads, 100 ms
warmup and 250 ms measurement. Overload rejection is expected and accounted for;
passing correctness does not mean meeting the performance qualification gates.
The existing launcher positive/negative memory-cap controls pass. Application
Job Objects enforce a 256 MiB process-commit ceiling; provider memory is measured
separately. Provider sockets bind only to loopback, with bounded payloads,
connections, pending work and shutdown. This is not a public service or an OS
sandbox for arbitrary native code.

Root also [rechecked raw saved accounting](evidence/207-mailbox-load-readiness/raw-accounting-audit.json)
for all eleven cases: offered/admitted/rejected conservation, zero errors and
timeouts, no pending work after drain, latency count, sixteen final mailbox
flags/counters, and provider frames including warmup. This recheck does not
rerun handlers or independently prove each returned byte; the running harness
checks echo content and final state against its established contract.

The [archive](evidence/207-mailbox-load-readiness/validation.zip) retains both
attempts, build/run logs, raw trial files and exact source snapshots. Root
reopened and verified all 317 member lengths and SHA256 hashes against the
[index](evidence/207-mailbox-load-readiness/validation.index.json).

Next: close report 140's explicit missing long-run sixteen-slot comparison.
Use three 30-second repeats each of RETURN-16, KEEP-16 and F# at the same
500/s, 5 ms provider-delay point and 256 MiB ceiling, retaining all failures
and nonqualifying samples. Reuse the current harness and independent content/
accounting checks; do not rerun the whole grid or label this an external-ingress
service result. Agent efficacy remains at [203](203-efficacy-decision-after-organic-creation.md):
bounded discovery/composition observed, comparative reliability and organic
vocabulary accumulation benefit unproven.
