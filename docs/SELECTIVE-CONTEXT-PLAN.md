# Selective context and external-agent evaluation

Status: callable context verified in
[report 065](../reports/065-callable-compact-context.md) and inspection admission
locally verified in [report 066](../reports/066-inspection-response-budget.md);
evaluation remains planned, 2026-10-06. This follows the completed
[rotated repeat](../reports/064-repeat-agent-purpose-review.md). The full PRD
remains open; this plan advances context generation and evaluation without
changing language semantics or adopting a native/memory backend.

## First bounded change

The existing `context` operation already selects a deterministic dependency/type
closure and admits complete entries under depth, word and UTF-8 data budgets.
Add `flowReference` and `flowReferenceUnavailableReason` to its word entries,
using the same parser-verified resolver as `describe`. Build those fields before
budget admission. Do not append metadata after serialization, truncate JSON, or
change the existing hard limits.

`name` is the exact dictionary key for queries, declarations and test owners.
`flowReference` is the source spelling of a call target, not a declaration name
or a complete invocation. Inputs, outputs and effects still determine whether
an invocation is valid. Unrepresentable ordinary targets have a null reference
and a deterministic reason; syntax descriptors are separate from ordinary words.

Acceptance requires describe/context parity for root, qualified, case-sensitive,
reserved and intercepted names; generated constructors; actual typed Flow use
of returned references; complete serialized-data byte accounting and tight
omission/root-too-small cases; deterministic output; and no word/provider
execution during inspection. Run the existing Discovery and language acceptance
suites after a fresh build, followed by the complete Release validation gate.

This is an interface prerequisite, not evidence of cheaper agent development.
Do not alter the frozen early-flow-001 or early-flow-002 prompts, traces or states.

## Separate retrieval-budget boundary

The HTTP harness already caps complete prepared provider-request bytes. That
does not control an external Codex subagent's model window. The trial host has
per-line request/response and exchange limits, but no aggregate retrieval cap.

The optional cap applies to a documented, audited set of inspection
operations. Admit complete runtime responses, including their diagnostics,
atomically. Preserve raw responses in the forensic trace when a fixed host
budget-denial response is selected. Never hide mutation, test or validation
results, claim rollback, or replay uncertain execution. Inspection can still
append task bookkeeping entries; it is not a promise of zero host-state effects.

Conventional inspection is `inspect`, `read` and `search`. Audit the language
operations individually: for example, `failed-tests` runs tests and therefore
cannot be treated as passive inspection. Do not let the agent choose the
classification. Keep the default host behavior and historical traces unchanged.

Track admitted inspection payload bytes separately from every selected host
response, denial/control responses and noninspection responses. The existing
host traces a response before stdout flush, so a selected line is not proof of
delivery or model consumption. A future delivery claim needs a post-flush event;
even that establishes pipe delivery rather than insertion into model context.
Use overflow-safe cumulative arithmetic and distinguish zero-budget rejection
before execution from suppression after an inspected response is received.

Validate exact/equal/over-budget cumulative boundaries, runtime diagnostics,
UTF-8 and framing, exhausted-budget preflight, visible mutation results,
unchanged timeout/uncertainty behavior, fresh-session reset and default relay
compatibility. Raw runtime and selected/delivered host bytes need separate
hashes, counters and versioned trace semantics.

## Evaluation scope

Use fresh subagents and matched tasks, start states and hidden acceptance.
Compare the same task with different retrieval treatments; do not call a trend
across differently difficult tasks a retention benefit. Archive intended prompts,
operation policies, byte limits, source/binary pins, starting state, complete
traces, final state and coordinator interventions before interpreting outcomes.

Application retrieval bytes, protocol exchanges and definition size are distinct
from model tokens, turns, latency and provider context windows. Exact usage stays
unavailable unless the subagent interface supplies it. The PRD's 2k–32k model-
context comparisons and success criteria are not satisfied by this plan alone.
Broader domain, small-context outcomes and controlled cost evidence remain
required after this prerequisite.

The [matched retrieval study](SELECTIVE-RETRIEVAL-STUDY.md) is the next bounded
outcome comparison: four fresh actors on one common retained-vocabulary task,
same allowlist and 16,000-byte inspection allowance, with Full/Compact guidance
and detailed fallback allowed. The starting-state audit and metadata preflight
are preparation only. [Checkpoint 067](../reports/067-selective-retrieval-study.md)
records all four accepted trials on the published budget host and newly built
pinned runtime. Compact guidance used more inspection bytes in this bounded
comparison; keep both retrieval routes available. Broader efficiency and
small-context outcomes remain unproven.
