# 229 â€” Canonical Float text across backends

Status: accepted after fresh local validation, 2026-10-11.

Canonical Float text now preserves exact binary64 values across source rendering,
inspection, interpreter execution, IR JSON and owning LLVM execution. All 37
full-suite checks pass with package auditing enabled. The exact fresh native
verifier passes 1,008 assertions; native value-stack and owning-mailbox gates
also pass. No comparative agent advantage or throughput improvement is claimed.

The prototype now specifies invariant shortest-roundtrip finite binary64 text rather than delegating the contract to the host formatter. Report 224 preserved a .NET G/R counterexample: bits `0410000000000000` format to text that parses to a neighboring value. The required language text is `4.1045368012983762E-289`. This work does not reproduce that defect to obtain apparent backend parity.

The approved implementation uses one exact-interval BigInteger formatter for managed source, IR displays, value inspection and interpreted `float.to-string`, plus the existing bounded integer native interval formatter. Both preserve signed zero and add `.0` when required to retain Float syntax. Finite shortest-decimal selection and exponent rendering are defined in the PRD. Existing validation of nonfinite values is unchanged; diagnostic labels are separate.

Acceptance requires a fresh build, focused managed path regressions, reproducible independent CPython shortest-decimal corpus comparison, bitwise parsing checks and interpreted/native parity at O0 and O2, followed by applicable native and full local regression gates. The corpus remains in small batches and fresh machine-frame screening rejects invalid, dynamic or oversized individual frames before loading. This does not prove a complete call-chain budget or permit untrusted native execution.

No package, external effect grant, arbitrary host escape or network listener is introduced. Frozen studies retain their pinned runtimes and historical evidence. No performance or agent-efficacy benefit is established by formatter conformance.

Focused managed validation passes Source 157, Flow 1,327, IR Formatting 57,
IR Interpreter 161 and Value Inspection 50 assertions. Both managed and native
formatters match all 30,400 pinned CPython oracle rows, including parse-back
bits; native O0/O2 passes 263 assertions. The root regenerated the oracle and
verified byte-identical fixture output. Independent scoped source review found
no blocking formatter issue; this sampled corpus is not an exhaustive proof.

A further actual Protocol serialization probe found that the IR DTO's numeric
`floatValue` field still serialized the known counterexample to adjacent bits.
Its exact display was insufficient to make that numeric field lossless. The
current IR DTO uses canonical text for `floatValue`, empty strings for inactive
literal slots, and format version 5. Regression tests serialize through the
actual Protocol path and verify JSON string kind, matching display and exact
bits for the counterexample and signed zero. Other IR-version expectations were
audited; owning ABI/layout and mailbox versions are separate and unchanged.

Failed validation attempts remain preserved: the first new LLVM test build had
a multiline assertion indentation error; the first oracle gate pinned the
CPython build string with incorrect spacing. Correcting those test defects made
the focused gate pass without changing expected Float values. Sandbox fresh
builds then failed to reach the NuGet vulnerability feed (NU1900), including the
full local gate. Host retries kept package auditing enabled and completed the
broad gates.

The audit-enabled host native value-stack and owning-mailbox verifiers pass.
The broader gates also caught three follow-up issues. A runtime accessor test
still expected `1` for a Float result and now expects `1.0`; a separate arithmetic
case checks `2.0`. A native test incorrectly required every generated module to
contain a scratch allocation, even when no allocation was needed. Its placement
check now permits zero allocations while continuing to check every actual
allocation. Neither correction changes language semantics to satisfy a test.

Finally, historical Growing fixtures contain Float literals in their individual
source objects but integral spellings in their aggregate export. The corrected
formatter makes that inconsistency visible through `STORAGE_PROJECT_MISMATCH`.
Current-runtime regression gates now construct disposable
snapshots from the hash-pinned authoritative seed and check their generated IDs
across a fresh-process reload. The frozen artifacts and strict storage comparison
remain unchanged. Fixture and full-suite acceptance pass.

The first audit-enabled full gate completed all 37 checks. Five checks failed:
language acceptance and Flow runtime acceptance had stale Float text expectations;
the snapshot, renewal acceptance and renewal replay gates used the historical
Growing export. Business-policy preflight passed 98 checks over 30 independent
outcomes. After correcting the expectations, fresh focused suites pass 35
language acceptance groups / 642 assertions and 45 Flow runtime groups / 2,261
assertions. Initial sandbox test runs could not write their system-temp projects;
reruns use scoped workspace temp directories and retain the failed logs.

The scratch-test follow-up's fresh audit-enabled Release build passes with no
warnings or errors, refined-Int passes 79 assertions, and the full native suite
passes 1,008 assertions. The exact isolated native verifier also passes all
1,008 assertions on the host with auditing enabled; a sandbox restore failed
before tests on NU1900. The four focused fixture verifiers pass 129, 32, 73 and
46 checks respectively. Independent fixture review corrected an invalid lookup
of a nonexistent manifest field and added an explicit save/load manifest hash
round trip using snapshot metadata and `CURRENT`. These are local validations,
not a security proof or a throughput result.

Final receipts are `full-validation-final-host.json` (37/37 checks, audit
enabled), `full-validation-final-host.business-policy-preflight.json` (98 checks,
30 outcomes), and `native-conformance-final-host.log` (1,008 assertions). The
value-stack gate passes 50 checks; the owning-mailbox receipt passes 2,255 checks.
Their exact receipts and the preceding failed attempts are included in the
evidence archive. IR inspection format is 5; owning ABI 2, owning layout 4 and
mailbox ABI 2 are unchanged. This is a prototype breaking change: current source
keeps Float literal identity with `.0`; historical snapshots retain their bytes
and use their pinned runtimes for historical replay.

Remaining native work includes a combined call-chain stack budget and a
worker-process boundary before exposing native execution to untrusted programs.
No new package, network listener, external effect grant or host escape was added.

The [evidence archive](evidence/229-canonical-float-text/candidate-and-validation.zip)
contains 248 independently verified members and 47 source pins; SHA-256
`05031f4146f67d72d61e6462f14e3982d03622b166e376fda802fa82d48a7fef`.
The [index](evidence/229-canonical-float-text/index.json) records member hashes,
sizes, exclusions and verified references to the unchanged 224/225 archives.
Its report snapshot precedes this archive-link paragraph.
