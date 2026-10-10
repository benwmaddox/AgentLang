# 181 — Validated snapshot reuse during activation

Status: implemented and locally validated, 2026-10-10.

The manifest-backed loader compiled a project to validate its export, then
discarded that compiled snapshot and compiled its state again for activation.
Named-snapshot restore repeated the same work. The bounded change returns the
exact validated `RuntimeSnapshot` and passes it to the existing library
requalification and activation paths. Legacy and empty projects still compile
once after graph validation. No cache, dependency or external capability is
added.

Source-object/hash, manifest identity, binding, type and exported-source checks
remain in place. Durable libraries still run their attached tests and coverage
gates. Named restores retain their clock selection, requalification, storage
restore and activation order. New regression cases exercise named-snapshot
test-file replacement and requalification under the snapshot's saved clock.

## Measurement plan

Use separately built Release baseline/candidate binaries with the same clone
of report 179's qualified retained-1 project. For each of three fresh processes
per build, measure launch-to-first `words` response and three subsequent
`words` responses separately. Use virtual providers, pipe-only transport, a
90-second response timeout and a 2 MiB response limit. Compare response hashes
and project file hashes. This measures activation plus retrieval, not function
replacement, test execution, JIT, service throughput or agent efficacy.

| Build | Fresh first-response samples (seconds) | Median (seconds) | Subsequent response range (ms) |
| --- | --- | --- | --- |
| Baseline | 11.513, 11.511, 15.376 | 11.513 | 1.440–4.933 |
| Candidate | 11.615, 11.826, 10.310 | 11.615 | 1.421–4.811 |

There is no clear measured startup improvement. All 24 responses have the same
canonical JSON digest, and all 290 project files remain byte-identical. These
small sequential samples include process startup and competing local work;
they do not identify which phase dominates activation. The result supports
keeping this small removal of redundant compilation, without claiming that it
solves the activation latency observed in report 169.

## Validation

The fresh focused runtime suite passes 42 groups and 1,732 assertions. It
includes named restoration of test-file overlay identity and library
requalification under the saved snapshot clock. Independent source review
passed with no blocker. All 37 full local Release checks pass, including the
business-policy preflight's 98 checks across 30 independent outcomes.

Full validation command:

```powershell
pwsh -NoProfile -File scripts/Validate.ps1 -Configuration Release -SerialBuild -SkipPackageAudit -ReportPath .agentlang/activation-181/full-validation.json
```

Validation used fresh builds and workspace-local temporary storage. CI was not
run. Package auditing was skipped; this is not a dependency security audit.
The review records a nonblocking coverage limit: no direct negative case
invokes `snapshot.load` library requalification failure and checks the prior
clock, providers, manifest and active program together. The unchanged `finally`
and restore/activation order were checked statically; the existing startup
rejection and new successful saved-clock restore tests cover narrower cases.

## Retained attempts

The initial isolated build used `--no-restore` with a new artifacts directory
and failed for missing assets. A fresh build with cached package restore passed.
An older business-policy seed failed current finite-return library coverage
(`email.delivery-fold-step`); it is retained as a failed baseline attempt and
excluded from performance comparison. The gate was not relaxed.

The first Python async timing harness stalled before launching the CLI. Its
Windows event-loop bootstrap uses a loopback socket pair; a zero-sleep diagnostic
also stalled after import. Both specific sessions were terminated and the
harness changed to bounded subprocess pipes and a reader thread. No permission
or runtime capability was widened.

Focused test preparation also encountered an isolated-output CLI path mismatch
and a new marker fixture committed before its passing test was recorded.
Those failed attempts are retained; the runtime's publication gate is unchanged.

The first evidence collection stopped before creating an archive because its
extension filter omitted the input project's empty `WRITE.lock`. Collection now
includes every explicitly pinned input file and verifies its digest. No runtime
or validation change was needed.

## Evidence and limits

[Evidence index](evidence/181-validated-snapshot-reuse/archive.json) records the
source pins, measurements, complete local gate, static review and retained failed
attempts in a [verified archive](evidence/181-validated-snapshot-reuse/evidence.zip).
Every archived entry is checked against its byte length and SHA-256 digest.
Build binaries and unrelated experiments are excluded.

The slice preserves validation boundaries and removes redundant compilation.
It establishes neither a startup speedup nor comparative agent reliability,
native throughput or a security certification. Profile activation phases before
choosing another optimization.
