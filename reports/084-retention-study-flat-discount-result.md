# Retention study: Flat S07 fresh external-agent cell (R02)

Status: R02 (B1/Flat/S07) completed with a passing trace-integrity audit and
failed independent task acceptance. Two of twelve cells are complete: R01
(B1/Flat/S01) passed; R02 failed. No comparative retention advantage is
established.

A fresh external actor was launched for the Flat arm from its six-type,
zero-authored-word baseline. The recorded request declared `gpt-6-luna`, max
reasoning, and `fork_turns=none`; the trace audit cannot independently prove the
provider's model settings. The actor authored the strongly typed pure word
`customer.discounted-balance : Customer -> Money`, attached eight tests and one
example, and made no runtime change or coordinator repair.

## Independent result

The verifier completed 333 checks. Metadata passed, while behavior failed. All
54 exact signed-Money cases ran: 42 passed and 12 failed. Every failed result
differed from the BigInteger oracle by one minor unit. The failed case IDs were
`premium-2`, `premium-3`, `premium-5`, `premium-6`, `premium-9`, `premium-10`,
`premium-12`, `premium-13`, `premium-15`, `premium-16`, `premium-17`, and
`premium-18`.

The actor's final implementation contains this calculation:

```agentlang
let minorUnits = Money::value(balance);
let discount = ::divide(minorUnits, 10);
Money::new(::subtract(minorUnits, discount))
```

For the frozen public phrase “truncate fractional minor units toward zero,” the
independent oracle truncates the final discounted balance, or 90% of the
premium balance, toward zero. At 109 minor units, the actor first truncates the
10% discount to 10 and returns 99; the oracle expects 98 from truncating 98.1.
The actor's own `premium-positive-fraction` test expected 99, so that self-test
did not express the independent endpoint rule. The frozen acceptance corpus was
unchanged.

The trace records three attached-test batches: 8 executions with 8 failures
after invalid `Instant` fixtures, then 8 executions with 2 failures from wrong
endpoint literals, then 8 executions with no failures after the actor revised
its expected values. The final actor metadata reported all 8 tests passing,
20/20 instructions covered, 2/2 branch outcomes covered, and its one example
passing. These results show that the actor produced a documented and locally
tested function; they do not establish correctness against the task's business
rule. Independent acceptance exposed that gap.

## Integrity and limits

The production trace audit passed 956 checks across 27 calls and exchanges. It
recorded 24 explicit test results with 10 failed test results, no protocol
diagnostics, and host/runtime exit codes of 0/0. Request and response payloads
were 11,335 and 33,156 bytes. This integrity pass establishes a complete,
auditable run; it does not reverse the independent behavior failure.

Native model usage was unavailable. Payload bytes are not token usage, and this
cell makes no latency or memory claim. One Flat S01 cell passed and one Flat S07
cell failed, so the result does not establish a vocabulary-retention effect,
general language failure, or broad actor-reliability conclusion. The unchanged
003 study remains incomplete. The reliability follow-up is still an unfrozen
draft and should follow completion of the current study.

The 49-file archive preserves the complete run tree, starting and final
projects, launch/preparation records, and console evidence under
`reports/evidence/084-R02/`. The index records ordinal path order, byte lengths,
and lowercase SHA256 hashes; every archived file was compared to its canonical
source. Canonical actor and run files remain in place.
