# 191 — Typed-reference maintenance: interim evidence

Status: one of six participants completed; the cohort remains open. This is
not a three-condition comparison result.

The first retained-vocabulary participant in the [fresh cohort](190-typed-reference-maintenance-cohort.md)
completed its task through one V2 broker and passed all 18 independent cases.
The nominal construction control, raw String and ShipmentId rejection controls,
static field metadata and helper signature checks also passed. The scorer
verified that it left the captured participant source unchanged.

The terminal audit confirms normal host/runtime exits 0/0 after 96 exchanges.
The captured trace records 27/27 project tests, passing examples, publication
and successful task finalization. A separate read-only audit accounts for all
21 inherited tests and three examples: seven are byte-identical, sixteen have
same-value nominal constructor adaptations and formatting, and one changes
formatting only. No removed or weakened assertions were observed. Six new
tests bring the project total to 27.

All ten seeded function IDs, prior source history and maturity levels were
preserved. The two existing library functions remain library functions; the
other eight remain project functions. The only intended public signature
change is the lookup helper's reference parameter becoming TrackingReference.
Current source confirms the ingestion chain reaches the existing find-scan
and scan-lookup-step helpers.

Protocol measurements are 105,022 request-payload bytes, 107,340 response-payload
bytes and 1,123.069 seconds of broker duration. These do not measure model token
usage, effective context size or total agent wall time. The participant used
nearly the entire 100-exchange allowance.

The trace includes nine rejected requests. Seven discovery or replacement
failures have subsequent successful corrective operations. Two revision-diff
queries after publication remain unresolved. Passing acceptance and task
finalization therefore do not establish complete error recovery.

This is the first fresh participant evidence that the formerly blocked
persisted-record maintenance task can succeed through the accepted interface.
It does not show a vocabulary advantage over reset-rich AgentLang or F#, and
does not establish organic vocabulary accumulation. The first reset-rich trial
has been dispatched without oracle feedback. The other four trials are pending.

The accepted runtime and frozen study inputs remain unchanged. No new effects,
network provider, host capability or dependency were introduced by this trial.
The comparison is about reliable pure-model maintenance, not security isolation
or native runtime performance.

The terminal capture, complete oracle result, trace measurements and preservation
audit are preserved in the [evidence index](evidence/191-typed-reference-maintenance-interim/index.json).
The bundle contains only the completed retained participant, not the live reset
trial. The final cohort report will
assess behavior, evidence preservation, library qualification, finalization and
recovery separately.

The [delivery roadmap](../docs/ROADMAP.md) now reflects the accepted record
evolution implementation and this open cohort. Its prior prerequisite is no
longer blocked; the remaining work here is the comparison and its analysis.
