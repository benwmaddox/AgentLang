# 066 — Inspection response budget for external-agent trials

Status: locally verified, 2026-10-06.

The preceding publication passed all 32 checks in a clean checkout on `d691592`;
saved [CI metadata](evidence/065-main-ci.json) and
[validation](evidence/065-main-validation.json) identify that exact source.

The completed [rotated repeat](064-repeat-agent-purpose-review.md) established
small-domain vocabulary discovery and reuse, while an overall efficiency benefit
remains unproven. [Milestone 065](065-callable-compact-context.md) added callable
references to bounded compact context. This milestone implements the separately
scoped [retrieval boundary](../docs/SELECTIVE-CONTEXT-PLAN.md) needed to constrain
and account for application inspection traffic in later fresh-subagent trials.

The optional host cap applies to complete inspection response payloads, not
model tokens or context windows. The default remains uncapped. Raw runtime
responses and selected host responses must remain distinguishable; only a
successful stdout flush supports a pipe-delivery event. Mutations, tests and
validation remain visible, and uncertain execution is never replayed.

The [planned matched study](../docs/SELECTIVE-RETRIEVAL-STUDY.md) uses four fresh
Luna/max actors on the same task and seed, with the same tools and fallback
allowed. A [seed audit](evidence/066-study-seed-audit.json) matches all 33 files
to the archived repeat task-4 starting inventory. A separate-copy
[metadata preflight](evidence/066-study-preflight.json), with archived
[requests](evidence/066-preflight-requests.jsonl) and
[responses](evidence/066-preflight-responses.jsonl), passed all 11 existing tests.
Both helper contexts fit depth 4, 16 words and 6,000 data bytes with no word/type
omissions. Their full response payloads were 2,122 and 1,902 bytes. These are
preparation measurements, not agent outcomes.
The two selected `describe` responses totaled 2,135 bytes, less than the two
contexts' 4,024 bytes; the latter also includes dependency/type closure. Smaller
traffic under compact guidance remains an empirical question.
The [extended preflight](evidence/066-study-budget-selection.json) measures shared
discovery plus each retrieval prefix and freezes a common 16,000-byte inspection
allowance before any actor launch, leaving room for honest fallback. Actual
runtime/host prelaunch pins and comparative outcomes remain pending.
The [raw frame capture](evidence/066-raw-preflight-frames.json) preserves CR and
excludes LF to match broker payload accounting. The initial text-based probes
stripped CR; their serialized JSON counts remain separately labeled in the
summary rather than treated as raw payload counts.

The implemented host uses profile-specific audited operation names. An omitted
cap preserves schema-1 response/trace fields; explicit zero enables admission
control. Schema 2 records raw valid runtime responses separately from selected
responses, admission prefixes and post-flush delivery. Terminal controls are
included in selected/delivery totals. `failed-tests`, execution and mutations
remain outside admission. Complete valid responses observed during transport
failure remain forensic evidence, with uncertain execution and no admission or
automatic replay. The review corrected the latent trace-label race when a valid
response precedes completion of a failed/timed-out request write.

The first [focused attempt](evidence/066-initial-focused-failure.json) used an
older pinned CLI and stopped after 24 passing checks on a fake-child Unicode
encoding error. The fixture now explicitly emits UTF-8; existing limits,
deadlines and assertions were not weakened. The [current-CLI focused run](evidence/066-focused-host.json)
passed all 33 checks, including real definitions/tests/commit at zero inspection
budget and a valid early response with uncertain request delivery. Its
[trace audit](evidence/066-focused-trace-audit.json) verified 14 schema-2 sessions.

The exact full Release gate,
`pwsh -NoProfile -File scripts/Validate.ps1 -ReportPath .agentlang/reports/066-local-validation.json -Configuration Release`,
built fresh artifacts and passed all 32 checks. Saved
[validation](evidence/066-local-validation.json),
[host detail](evidence/066-local-subagent-host.json), and
[trace audit](evidence/066-local-trace-audit.json) identify dirty parent `d691592`
with this change. The [read-only audit script](evidence/066-audit-host-traces.ps1)
checks actual raw/selected byte hashes, admission prefixes and every counter
snapshot against the trace stream and independently captured stdout totals.
Clean committed-source [main CI](evidence/066-main-ci.json) subsequently passed
all 32 checks at `94a2d7f`, with `dirty: false` in the saved
[validation artifact](evidence/066-main-validation.json).
No F# runtime, type, effect, semantic IR or storage version changed.

No new comparative agent outcome is claimed. The next milestone is the pinned
four-actor matched retrieval study. Existing frozen pilot and repeat artifacts
remain unchanged, and the full PRD remains incomplete.
