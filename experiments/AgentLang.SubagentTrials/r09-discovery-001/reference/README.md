# R09 F# reference fixture adapter

Run the built console program with exactly two arguments: the frozen `oracle.json`
path and the output JSON path. The adapter constructs each Store through the
public `AgentLang.Business.Domain` operations, projects every recipe-created
value through public accessors, computes reference totals with checked Int64
addition, and records constructor failures and per-case oracle comparisons.
Error messages are retained only for constructor diagnostics; expected results
are compared by stable error code.

From the repository root, build and run it with:

```powershell
dotnet build experiments/AgentLang.SubagentTrials/r09-discovery-001/reference/ReferenceFixture.fsproj --artifacts-path .agentlang/r09-discovery-001/reference-evidence/artifacts -c Release -p:NuGetAudit=false -m:1
dotnet .agentlang/r09-discovery-001/reference-evidence/artifacts/bin/ReferenceFixture/release/ReferenceFixture.dll experiments/AgentLang.SubagentTrials/r09-discovery-001/oracle.json .agentlang/r09-discovery-001/reference-evidence/reference-fixtures.json
```

## Deterministic fixture metadata

Symbolic numeric identifiers use a canonical GUID whose first group is the
kind namespace and whose final twelve decimal digits are the symbol number:

| Kind | Symbol | Canonical GUID |
| --- | --- | --- |
| customer | `c1` | `10000000-0000-0000-0000-000000000001` |
| customer | `c2` | `10000000-0000-0000-0000-000000000002` |
| customer | `c9` | `10000000-0000-0000-0000-000000000009` |
| invoice | `i1` | `20000000-0000-0000-0000-000000000001` |
| payment | `pay1` | `30000000-0000-0000-0000-000000000001` |
| product | `p1` | `40000000-0000-0000-0000-000000000001` |
| subscription | `s1` | `50000000-0000-0000-0000-000000000001` |

The formula also applies to higher numeric suffixes. Each case starts with a new
empty Store. Product numbers follow invoice-array order; invoice numbers are
the oracle symbols; payment numbers follow paid-invoice array order. Payments
are serialized by ascending generated GUID and looked up one key at a time,
which is the same order as the paid-invoice recipe. The existing F# `Store`
does not expose a public payment enumeration operation, so this adapter does
not claim to inspect its private map iteration. For `overflow-remains-sticky`,
the recorded amount sequence must be `9223372036854775807`, `1`, `10`.

Default customers use email `cN@example.test`, kind `standard`, balance `0`
minor units, and creation time `2000-01-01T00:00:00.0000000Z`. Oracle overrides
replace only their named fields. The domain trims customer kind and product
names and normalizes dates to UTC. Invoice and payment times are
`2001-02-03T04:05:06.0000000Z`. Products are named `Reference product N`.

Extra state starts subscription `s1` for `c1` on the first invoice product,
with term `annual`, from `2001-02-03T00:00:00.0000000Z` through
`2002-02-03T00:00:00.0000000Z`. Email message N has subject
`R09 reference message NNN` and body `Reference fixture message NNN.`; all
requested messages are queued in order, then the requested sent count is
delivered from the head of the queue with the deterministic successful email
provider. Thus the frozen one-pending/one-sent recipe queues two distinct
messages and delivers the first once.

Dates in output use seven fractional digits and a literal `Z` after converting
to UTC. Money amounts are decimal strings in minor units, preserving the full
Int64 range. The complete Store projection includes counts from public
`Store.summary`, every known generated entity obtained through public lookup
operations, and both public email queues; list lengths are checked against all
seven summary counts.
