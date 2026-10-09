# Native Option/Result

Status: focused native conformance and all 37 full local Release checks pass.
This is an engineering milestone, not a new agent-efficacy trial.

The stable-arena `OwningStackAot` backend now executes Option/Result operations
from the same compiler-authorized typed semantic IR as the interpreter. Payloads
remain inline. Layout ABI advances to 3; descriptor sizes remain 40/36/16 bytes
for layout/type/field, and stack ABI remains 1. The older graph-backed
`LlvmAot` backend has a separate contract and does not gain sum support.

## Behavior and scope

Some/Ok use tag 0; None/Error use tag 1. None occupies eight bytes and must have
no child payload descriptor. Other cases contain the active child's standalone
encoding after the tag. Supported payloads are Int, Bool, Unit, String,
payload-free enums, acyclic records and nested sums over that closure. Float,
List and refined wrappers remain unsupported in this native slice.

Construction copies the chosen child into a new inline sum once. Matching uses
a validated view of that owner. Local loads, calls and returns transfer
location descriptors; they do not relocate payloads. The compiler merges
provenance across match arms and rewinds only with a static proof. Escaping
payloads and uncertain calls retain storage. No runtime liveness scan,
reference counting or automatic compaction was introduced.

See [the implementation contract](../docs/NATIVE-SUMS-IMPLEMENTATION.md) and
[stable arena lowering](../docs/STABLE-ARENA-LOWERING.md).

## Accepted validation

| Check | Result |
| --- | --- |
| Fresh LLVM Release build | Zero warnings/errors |
| LLVM regression executable | 476 assertions |
| Integrated lifetime analysis | 44 assertions |
| Native storage O0/O2/trap UBSan | 50 cases / 612 checks, identical deterministic metrics |
| Bank C tests O0/O2 | 56 checks each |
| Native-value-stack gate | 38 passing gate checks |
| Cross-backend experiment | 1,018 passing verdicts, zero failures; 218 informational rows |
| Sum-specific experiment | 294 passing verdicts |
| Mailbox integration | 592 passing checks |
| Mailbox policy | 741 passing checks |
| Local real I/O | Seven pinned scenarios across 12 native runs; all pass |
| Full local Release gate | 37 passing checks; 41 minutes 10 seconds |

Each owning O0/O2 sum run covers 26 values, 12 equality pairs, 12 match cases,
seven malformed/raw inputs, 26 host and local-call round trips, short retained
buffers, branch joins, exact/short capacity, fault cleanup and invalid descriptor
graphs. Literal byte fixtures cover empty payloads, embedded NUL, isolated UTF-16
surrogates, nested sums and a dynamic sum preceding a fixed record field.
Payload-view extraction requires zero deep-copy bytes and ordinary transfers
require zero move bytes. Newly materialized fallback strings have separately
pinned literal bytes, store calls and exact expected construction copy counts.

The ordinary UBSan build fails to link Windows symbolizer imports (LNK1120).
The accepted sanitizer result uses the gate's trap-mode UBSan fallback. The
real-I/O gate runs host-side for local loopback sockets, covering O0/O2,
diagnostic/fast/trusted-generated profiles and return/keep suspension policies.
Its 207,422 check rows include variable I/O event observations, not that many
independent test cases or a throughput result.

Accepted evidence roots under `.agentlang/`:

- `owning-stack-003/verification-75c5b5b58292487eb16e059f2de8dc2f`
- `owning-mailbox-001/integration-run-89b34080693044e88b37006eed016863`
- `owning-mailbox-002/policy-run-410f3b453cba422689b7a3c666d99401`
- `owning-mailbox-003/io-run-01bdb24ec8664fb1952813aea6d98f32`
- `native-sums-160/` for fresh LLVM/lifetime logs and C tests

Commands are `Verify-NativeValueStack.ps1`, `Verify-OwningMailbox.ps1
-SerialBuild`, `Verify-OwningMailboxPolicy.ps1` and `Verify-OwningMailboxRealIo.ps1`
under `scripts/`. Full validation uses `Validate.ps1 -Configuration Release
-SerialBuild -SkipPackageAudit -ReportPath
.agentlang/native-sums-160/full-validation-001.json`. Package auditing is
explicitly skipped; no CI or package-security result is claimed.

## Failures that changed the implementation or tests

Independent review found that a coherent descriptor could give None a real
payload. An immutable snapshot accepts the 16-byte mutant; repaired deep and
mailbox validators reject it and preserve output sentinels. The earlier negative
test only rejected stale size metadata. The replacement regression checks the
actual missing rule. Before/after evidence is in
`native-sums-160/option-none-sentinel-review/*-002.log`.

Review also found an avoidable mailbox validation cost: every field scanned all
types. The owner scan now runs only for sentinel fields; ordinary bounds,
flags and reserved checks remain. Fresh C and integration gates pass afterward.

Integration exposed two emitter compile errors and several test-runner issues:
F# interpolation and span-callback errors, a missing nested-match payload load,
JSON evidence rendering of an isolated surrogate, incomplete layout collection,
and a zero-copy predicate incorrectly applied to a new fallback literal.
Corrections preserve all cases and strict payload-view requirements. A later
layout failure exposed unresolved nominal keys in composite display names;
the emitter now resolves those names recursively. Typed case payload keys are
resolved through the returned public layout table by the harness.

The first real-I/O run failed source stability because the coordinator overlapped
it with that display-name correction. Passing behavioral rows did not qualify
it. The accepted second run has unchanged source hashes. All failed gate reports
and logs remain preserved; `native-sums-160/integration-record.md` retains the
interim integration sequence.

Final read-only review found no actionable defect in the reviewed sentinel,
mailbox-scan, sum layout/constructor/match/call/local/lifetime paths. It ran no
tests and did not audit the whole emitter or all host serialization/equality.
The fresh executable gates provide the runtime evidence for their stated scope.

## Research consequence and remaining work

This establishes bounded semantic parity and the intended owning-arena behavior,
not general native release readiness, a final mailbox memory policy or a
performance advantage. JIT/general release support and broader native types
remain work. The standard value-to-JSON renderer still cannot represent the
isolated-surrogate fixture; the conformance harness reports exact code units.

The efficacy result remains [report 159](159-retained-io-vocabulary-follow-on.md):
both submissions passed their behavior oracle, but AgentLang duplicated an
inspected helper while F# reused it. Native implementation work does not change
that finding. A future efficacy trial should use a different maintenance or
composition problem rather than another small I/O variant.

Full Release validation passed on 2026-10-09, including the business-policy
preflight with 98 checks across 30 independent outcomes. CI stays manual-only.
The evidence archive preserves both failed attempts and accepted runs.

## Frozen evidence

[Evidence archive](evidence/160-native-option-result/evidence.zip) and
[archive metadata](evidence/160-native-option-result/archive.json) contain 1,339
entries in 12,062,926 bytes. Every archived entry and its source were hash-checked.
Archive SHA-256: 3b4535c9956da285a764d5bfb5666260490cf5a8f415303380b0f391621ef076.
