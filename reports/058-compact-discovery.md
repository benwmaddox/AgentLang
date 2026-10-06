# 058 — Compact discovery and exact Flow references

Status: verified local interface milestone, 2026-10-06.

Report 057's 15 accepted trials establish feasible discovery, composition and
retention, with no overall agent-cost advantage. In Growing task 5, the full
`words` response accounts for 14,829 of 26,828 response bytes. Repeated qualified
name errors are another measured source of friction. This milestone addresses
those two interface costs before further matched external-agent trials.

## Change

`words` accepts a strict optional Boolean `compact`. True returns sorted ordinary
names and syntax names, marked as compact; omitted or false retains the full
listing. `:words --compact` selects the same view. Invalid human options are
errors rather than silently ignored arguments.

Actual word descriptions expose a parser-verified exact `flowReference`.
Dictionary `add` becomes `::add`; `customer.renewal-balance` becomes
`customer::renewal-balance`. Unsupported identities return null and an
explanation. The metadata names a target, not an invocation template: types,
arguments, output arity and callback rules still govern use. Syntax constructs
retain their separate descriptions.

The [implementation plan](../docs/COMPACT-DISCOVERY-PLAN.md) defines the contract.
No grammar, semantic IR, storage version, primitive, capability or library
coverage policy changes. Frozen pilot-001 binaries, prompts and trial artifacts
remain unchanged.

## Validation

Fresh `pwsh -NoProfile -File scripts/Validate.ps1` passed all **32 required
Release checks**, with zero build warnings/errors. Saved
[local validation](evidence/058-local-validation.json) records dirty parent
`995c29e`; this is local working-tree evidence, not clean committed-source CI.
Language acceptance passed 35 groups / 626 assertions, Flow Runtime 20 / 650,
and CLI 8 / 115. The gate also verifies persistence, typed IR, existing matched
fixtures, negative behavioral controls, trial-host boundaries, parser limits
and whitespace. Two initial builds caught new-test syntax errors; those were
repaired before this passing gate.

Checks cover matching sorted inventories including generated, candidate,
temporary and deprecated words; default versus explicit false full-response
identity; strict invalid options before inspection; unchanged descriptions and
inspection logs; top-level/nested JSONL transport; human command recovery; and
real calls through described references, including nominal constructors and
static callbacks despite name collisions. An
[independent read-only review](evidence/058-independent-review.json) found no
concrete correctness or compatibility issues; the fresh gate is authoritative.

The [direct probe](evidence/058-discovery-probe.json) uses the fresh default
dictionary, with 49 ordinary words and 12 syntax constructs. Complete serialized
responses measure **12,913 UTF-8 bytes full versus 968 compact**: 92.5% smaller.
This fixture differs from pilot task 5's grown dictionary; do not combine the
counts or treat the change as a measured reduction in that agent's total context.
The probe pins the locally built CLI/Core hashes. Existing full listing content
is preserved; selected descriptions gain additive metadata.

The preceding report-only publication has separate clean committed-source
evidence: [main CI identity](evidence/057-main-ci.json) and
[full validation](evidence/057-main-validation.json). Run 37482178374 passed all
32 required checks on clean `995c29e7b6bc6d2e46e99373532d2335f1b535c4`.

## Research limit and next decision

Smaller serialized inventories are an interface measurement, not model-token or
task-cost evidence. Repeat matched Flat/Growing/Conventional trials with
standardized archived prompts and rotated order. Address the conventional
whole-file replacement asymmetry before interpreting request-byte differences.
Exact subagent model usage remains unavailable; retain that limitation explicitly.
Full-domain expansion, memory/mailbox research and LLVM remain behind external
agent validation. The full PRD remains incomplete.
