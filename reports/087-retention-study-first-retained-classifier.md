# Retention study: first retained classifier cell (R03)

Status: R03 (B1/Retained/S01) passed independent acceptance and trace-integrity
audit. Three of twelve cells are complete: R01 passed, R02 failed, and R03
passed. The retention comparison remains incomplete, with no comparative
advantage established.

The launch evidence records a fresh external actor request for `gpt-6-luna`,
max reasoning, and `fork_turns=none`, using the frozen public S01 prompt verbatim.
The starting project was the shared rich baseline with 53 words, 31 types, and
151 tests. The trace audit records declared model settings; it cannot
independently prove which provider settings the external actor used.

## Implementation and acceptance

The actor implemented `customer.premium? : Customer -> Bool` as a pure library
word. It compares the raw kind string to exactly `premium`, without trimming or
case folding:

```agentlang
word customer.premium?(customer: Customer) -> Bool {
    effects none
    doc "Returns true only when a customer's raw kind is exactly the ordinal string \"premium\"."
    ::equals(customer::kind(customer), "premium")
}
```

Independent acceptance passed 157 checks and all ten S01 oracle cases. The
actor's six new tests passed, covering all four instructions in the direct
equality word; it has no branch outcomes. Its one example passed. The full
library's 157 attached tests also passed: 151 were in the frozen rich baseline
and six were added by the actor. The result required no runtime edit, new
primitive, or coordinator repair.

The first explicit test batch failed all six runs because the fixtures used a
noncanonical `Instant`. After correction, the second six-test batch passed. The
actor's task commit reran those six tests. Its task log records 18 test results
with six failures; the trace audit records the two explicit `test` requests as
12 results with six failures. The initial fixture failures do not contradict
the final accepted result, but they remain part of the execution record.

## Integrity and limits

The production trace audit passed 886 checks across 22 calls and exchanges. It
records the exact actor-owned `host.close`, zero host/runtime exit codes, and
12 explicit test results. The coordinator records accepted verification before
the close request. This is a successful integrity and acceptance result for
one classifier task; the true/false cases do not establish a broader finite
coverage feature or general language reliability.

Native model usage was unavailable. The recorded request and response payloads
were 5,232 and 28,948 bytes; these are not token usage. This cell makes no
latency or memory claim. It used the unchanged frozen 003 runtime and `word`
syntax; it does not validate the proposed `fn` syntax, module structure, or
library maturity gates.

The next cell is R04 (B1/Retained/S07). It must start from this accepted R03
final project and verify the same-block transfer against R03's acceptance and
project hashes. The failed R02 Flat S07 result does not authorize a fallback in
the Retained arm.

The exact 603-file run, starting and final projects, launch/preparation records,
and console evidence are archived under `reports/evidence/087-R03/`. The index
lists full repository-relative paths in ordinal order with byte lengths and
lowercase SHA256 hashes; every archived file's length and SHA256 matches its
canonical source. Canonical actor and run files remain in place.
