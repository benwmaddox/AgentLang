# 137 — Guided discovery and new composition

Status: one fresh participant passes independent acceptance. Starting revision:
`4e13549`. This is a bounded follow-up to the help change in report 136, not a
rerun or repair of the six submissions in report 135.

One fresh subagent added a remaining-lifetime-allowance function to a copy of
the accepted report 134 seed. The task requires discovering and composing the
existing customer payment-total abstraction, preserving its errors, and
returning `max(0, cap - paidTotal)` with checked signed Int64 semantics. The
function must pass the actual library gates, preserve inherited definitions
and tests, and complete the task transaction. The helper's contract is public;
its symbol is omitted so discovery remains part of the work.

The task explicitly requires reading default and definition help for Flow/2.
Therefore any use of the new compact-discovery examples is guided exposure,
not evidence of unprompted discovery. A single participant, with no conventional
or old-help control, cannot establish that report 136 caused a better outcome
or that this language is more reliable than F#.

The seed's Money type is nominal but signed; it has no nonnegative refinement.
Normal payment workflows reject nonpositive payments, while the plain Payment
record can represent them. The public task covers those signed values, and the
oracle distinguishes defensive direct-record cases from normal workflow cases.
Checked subtraction is not currently a primitive; safe composition must guard
its range using the existing operations. The runtime is not being extended to
make this trial pass.

Before participant dispatch, the independent oracle, public prompt, current CLI,
seed copy and scorer were frozen. A correct coordinator-only control passed
and a clamp-before-error control failed. After dispatch there were
no hints, repairs, retries or changes to acceptance. The participant had a
60-exchange ceiling. Behavior, library qualification, inherited-state
preservation, helper reuse and finalization will be reported separately.

Dependency reachability is only supporting evidence. Source review must confirm
that the new operation delegates payment aggregation and does not duplicate it.
The final actor message is not authoritative when it disagrees with saved code
or the broker trace.

## Participant result and interpretation

The unchanged independent scorer passes 14/14 cases and all 62 checks. Its
fresh full-project test run passes 171/171 tests. The actor adds one persistent
library function and eight tests, with 54/54 instructions and 10/10 branches
covered in its attached tests; both Result cases are observed. All 55 inherited
word IDs, metadata, definitions, revisions and test hashes, and all 32 types
are preserved. The dictionary diff is 116 insertions and zero deletions.

Source review confirms one call to `customer.paid-total`, no duplicated payment
aggregation and unchanged propagation of the complete helper error. The helper
runs before clamping. For negative totals and nonnegative caps, the candidate
compares the total against `cap - Int64.MaxValue` before subtraction. That
threshold is representable; the remaining guarded subtraction paths also stay
within Int64. This is meaningful new composition, not a redundant alias.

The actor uses 32 of 60 broker exchanges with zero error envelopes. It reads
default help at exchange 2, definition help at 3, compact inventory at 4
(5,012 response bytes), and describes the existing helper at 5. It defines at
29, tests at 30, commits the library function at 31 and commits the task at 32,
then closes the host cleanly. No search or context request occurs. Transport
payloads total 10,162 request bytes and 66,852 response bytes; these are not
model-token or agent-turn counts. The task log's 16 tests are eight tests run
twice, not sixteen distinct tests.

This supports guided discovery and correct reuse of an earlier agent-created
abstraction. It does not measure an improvement attributable to default help:
the task mandated help exposure, and neither an old-help nor F# control ran.
Costs from report 135 are not directly comparable because the task differs.
The incorrect preflight control also achieved complete own-code coverage,
showing why library coverage cannot replace independent behavioral acceptance.
Comparative reliability and the benefit of vocabulary growth remain unproven.

The saved project and trace match the closed actor workspace. The score SHA-256
is `288eb0eec0663ee6bf756e8eda8e582aa34b018d3b513c98a096179529174ad5`;
the audit SHA-256 is
`289b24625ad98fa49da16072e1dbc612fb1f6e8fe0aa6b704479435cc0ca5641`.
The [evidence storage index](evidence/137-guided-discovery-composition/storage-index.json)
locates the compressed raw trial, controls, failed preparation attempts, frozen
inputs, final source and independent scoring. Preparation is retained separately
from participant activity. No participant repair or second attempt occurred.

## Preparation evidence

The current CLI was built freshly with serial Release compilation, zero
warnings and zero errors. Root review independently recomputed all fourteen
expected composition outcomes using arbitrary-precision arithmetic. The
correct control qualifies as a library with nine passing own tests and full
47-instruction/eight-branch coverage; its first successful independent score
passes 14/14 cases. The deliberately wrong clamp-before-error control also
qualifies, with seven passing own tests and full 54-instruction/ten-branch
coverage, but the independent oracle rejects it (10/14 cases pass). These are
scorer controls, not participant results.

Two earlier scorer attempts failed on missing fixture fields before scoring
any case. Their evidence is retained. Final pre-dispatch runs used the
same frozen scorer, including unique composition-case identifiers and exact
BusinessError payload comparisons when the helper fails. Store literal
round trips check fixture construction fidelity; they are not an after-call
mutation test. Purity and source review supply separate preservation evidence.

Final control runs use the same scorer hash
`dd18074f6738c137ffb7e8efc734d9dcabfbd35690c9bba0fe47763e436500a5`:
correct passes 14/14, wrong is rejected at 10/14. The input freeze is
`.agentlang/discovery-composition-137/input-freeze.json`, SHA-256
`ba21b8cfb8d01e2311f7c58964ffd204d31d84b1be2072e3b6e5e8c4af668559`.
Root verified all 312 seed-copy files byte-for-byte and pinned 22 runtime files
plus ten study artifacts. The oracle's authored draft label remains unchanged
to retain the exact bytes used in preflight; the manifest is the authoritative
dispatch freeze. Participant `/root/composition137_actor` uses `gpt-6-luna`,
max reasoning and no inherited conversation. Its only initial file read is the
hash-pinned public prompt; subsequent project operations use the broker.

## Native work remains separate

A read-only audit identified capacity-sized poisoning at scratch checkout and
release as a concrete obstacle to a useful throughput measurement. The async
evaluation plan now records a bounded optional reset profile and its acceptance
requirements. It must preserve boundary and ownership checks, compare the same
semantic oracles, and measure actual writes rather than treating cursor extent
as all reset traffic. This planning result is not an implemented optimization
or a throughput result.
