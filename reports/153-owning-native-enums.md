# 153 — Closed enums in the owning native backend

Status: implemented and validated locally; all 37 Release checks and focused native gates pass.

The selected owning backend now executes payload-free closed enums, including
exhaustive matches and enum fields in fixed and String-bearing records. Fresh
interpreter/native O0/O2 conformance and malformed-input checks pass after the
two implementation defects described below were repaired. This is a bounded
native capability result, not evidence of comparative agent reliability.

## Intended behavior

Extend the selected stable-arena `OwningStackAot` backend with closed,
payload-free nominal enums. Each value is an inline eight-byte case ordinal;
the verified type identity distinguishes enums that happen to share ordinals.
Use the existing semantic IR for construction and exhaustive matching. Preserve
compiler-controlled rewinds, immutable source values and inline record storage.
No heap object, reference count or runtime liveness scan is introduced.

Validate enum type/case names in host encoding and reject malformed ordinal
bytes at native ingress, including ignored inputs and nested record fields.
The type descriptor gains a `uint32_t case_count` at byte 32: descriptor size
36, layout ABI 2, enum kind 6. Non-enums require zero case count; enums require
a positive count. The layout struct stays 40 bytes; Stack ABI 1, context and
event layouts do not change. Invalid external values use the existing
`INVALID_REQUEST` status and must not overwrite retained outputs.

The active stable-arena emitter serves both fixed-size and String-bearing
layouts. Initial inspection mistook a historical fixed emitter for an active
path; it is unused and will not be extended. Existing conservative lifetime
analysis may retain storage around enum operations rather than insert a rewind.
That is safe and consistent with the selected policy; this slice does not claim
new lifetime optimizations.

Generic mailbox/bank layout consumers can use validated enum leaves under their
existing record contracts. Handwritten application hosts retain their explicit
schema boundaries. This does not select full actors over specialized mailboxes.
Native Option/Result payload layouts remain unsupported pending their own design.

## Acceptance plan

- Compare interpreter and native O0/O2 results for constructors, every match
  case, equality, locals, calls and record projection.
- Use independent literal ordinals, field offsets and retained-byte fixtures
  for fixed records and String-bearing records.
- Reject wrong nominal types, unknown case names, negative/out-of-range raw
  tags, malformed counts/extents and nested invalid tags, including unused inputs.
- Preserve retained output on failure and stable-arena copy/move behavior.
- Audit generated and handwritten descriptor consumers, offsets, versions and
  deterministic oracles; run C storage checks at O0/O2 and with trap-mode UBSan.
- Run `scripts/Verify-NativeValueStack.ps1`, applicable mailbox/bank/real-I/O
  gates and the local Release validation. Use fresh isolated artifact directories
  for native gates, preserving failed attempts and their binary-input records.

This is a native semantic-conformance milestone, not another agent efficacy
trial or evidence of a throughput advantage. Focused results and limitations
and full Release results are recorded below.

The efficacy conclusion remains report 151's: the dictionary, validated domain
types and library gates are usable mechanisms, but the trials have not shown a
comparative reliability advantage over F#. Report 152 repairs an observed domain
defect. This native work makes those language guarantees available at another
execution boundary; it does not add a new agent-comparison result. A later study
should exercise a materially different maintenance problem rather than repeat
the successful billing arithmetic tasks.

## Validation observations so far

Standalone C storage checks pass at O0, O2 and trap-mode UBSan: 48 cases,
565 checks. Bank checks pass at O0/O2 (49 checks each); generic mailbox and
release-runtime checks also pass. These checks do not prove generated LLVM
conformance.

The first fresh native-value gate failed while compiling its F# test runner:
ambiguous IR body inference and a malformed tuple expression. The runner was
repaired without relaxing its semantic assertions; an isolated fresh Release
build then passed with zero warnings and errors. The subsequent full gate is
recorded below.

The first owning-mailbox integration gate built the F# compiler successfully,
then clang rejected generated LLVM containing `%%input.bytes` in an operand.
The failure occurred before native integration tests ran. Preserve that failed
attempt at `.agentlang/owning-mailbox-001/integration-run-d3549fd5ec834b27aea0da218c90913e/`;
repair operand formatting and rerun with fresh artifacts before claiming success.

After that repair, the fresh owning-mailbox gate passed at
`.agentlang/owning-mailbox-001/integration-run-58a42ac52faf42d18f1ae271d16e7281/`.
The policy gate also passed at
`.agentlang/owning-mailbox-002/policy-run-7a665e6d31f0464588d5f071753ba3e5/`.
These results precede the next ingress repair and require fresh validation of
the final compiler source.

Native-value attempt two completed but exposed four assertion failures: at
both O0 and O2, a declared enum extent of four bytes and a shortened dynamic
record extent returned success instead of `INVALID_REQUEST`. Other assertions
passed. Evidence is retained at
`.agentlang/owning-stack-003/verification-1620d6264dc04edebb3e3b5306ad5d76/`.
Do not weaken these rejection assertions; repair the failure-status path.
Independent review traced a scanner bounds short-circuit that returns failure
without setting the context status. The external measurement API consequently
reports the unchanged success status. Audit analogous guards and add direct C
regressions as well as retaining the generated-entry checks.

The first real-I/O gate stopped at a loopback connection failure (Windows socket
error 10013) before completing its correctness cases. That failed attempt is
retained separately from the passing host-side retry below.

The scanner repair uses one owner-bounded readable check for enum, scalar and
empty-record leaves. It preserves the existing external `INVALID_REQUEST` and
internal `INTERNAL` status mapping. Five external short-value regressions and
one internal regression now pass in the direct C suite: 49 cases / 572 checks,
with the fixed baseline unchanged at 16 / 189 and dynamic checks at 33 / 383.
O0, O2 and trap-mode UBSan pass; ordinary UBSan failed to link due to unresolved
MSVC-target sanitizer imports. Diagnosis and outputs are in
`.agentlang/native-enums-153/native/run-011/`. Independent read-only review found
the bounds arithmetic sound. Fresh generated-entry, mailbox and real-I/O gates
subsequently passed on this source, as recorded below; the real-I/O retry ran
host-side to permit its local loopback connection.

## Final focused results

All four fresh gates exited zero on the repaired source:

| Gate | Evidence directory under `.agentlang/` |
| --- | --- |
| Native value stack | `owning-stack-003/verification-c9929f1642a64de3b631349aead9f640` |
| Mailbox integration | `owning-mailbox-001/integration-run-b0f05ed417cc48f386d070c10b49e115` |
| Mailbox policy | `owning-mailbox-002/policy-run-107a81cf7a6a4235b99131179a456e4a` |
| Real I/O | `owning-mailbox-003/io-run-aefa9615d956424bbbb56a672809e36b` |

Commands were `pwsh -NoProfile -File scripts/Verify-NativeValueStack.ps1`,
`Verify-OwningMailbox.ps1 -SerialBuild`, `Verify-OwningMailboxPolicy.ps1`, and
`Verify-OwningMailboxRealIo.ps1` (each latter script under `scripts/`). The real-I/O
retry ran host-side to permit the local loopback connection; the failed sandbox
attempt is retained. No remote service or CI run was used.

The full check is `pwsh -NoProfile -File scripts/Validate.ps1 -Configuration
Release -SerialBuild -SkipPackageAudit -ReportPath
.agentlang/native-enums-153/validation-001.json`. It completed successfully with
37 checks and zero failures, including the business-policy preflight's 98 checks
across 30 control runs. The run lasted approximately 34 minutes. Package
vulnerability audit was explicitly skipped; this is not a package-security result.

The [evidence archive](evidence/153-owning-native-enums/evidence.zip) preserves
the failed attempts, final gates, full Release report, reviews and source
snapshots without build binaries. Its [manifest metadata](evidence/153-owning-native-enums/archive.json)
records 561 entries, 5,023,023 compressed bytes and SHA-256
`a811fd7000cd3eef0081f863c41c8fe683e1f16b782a1e9c6cf7330232a81e2b`.
Every archived entry was hash-verified after packaging.
