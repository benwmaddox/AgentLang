# 193 — Conventional reference maintenance: interim evidence

Status: four of six participants completed. The second reset-rich participant
has been dispatched; the second retained participant is pending. The cohort remains
open and no comparative reliability ranking is established.

Both F# participants closed their original brokers normally with host/runtime
exits 0/0. Their captured validation responses report exit 0.
Independent acceptance passes all 18 behavioral cases for each, static
TrackingReference field metadata, raw String and ShipmentId negative compiler
controls, and the shared lookup helper's positive/negative signature controls.
The scorer confirms that each captured participant's source remained unchanged.

| Participant | Independent cases | Local assertions | Exchanges | Request bytes | Response bytes | Broker seconds |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| fsharp-1 | 18/18 | 13 | 26 | 12,818 | 35,567 | 414.958 |
| fsharp-2 | 18/18 | 17 | 9 | 23,157 | 17,743 | 227.417 |

These measurements describe protocol activity,
not provider tokens, effective context size, total agent wall time or native
service throughput. The read-only audits account for all eight inherited
assertions in each candidate without weakened expectations. The first adds
five focused assertions; the second adds nine. Their original shared lookup
helper is retained with a TrackingReference parameter, and `.fsproj` files
remain unchanged. AgentLang library qualification and task.commit are
not applicable to this conventional arm.

The first trace contains a malformed request rejected before delivery and an
indentation validation failure, each with subsequent successful correction.
The second contains one test-helper type-inference failure followed by a
source correction and successful validation. Neither audit identifies an
unresolved failed request. These recovery findings are separate from behavioral
acceptance.

One documentation limitation remains in fsharp-1: the existing shared-rule
comment describes appending a scan but does not document the new replay no-op.
Passing behavior and assertion preservation do not establish documentation
completeness. No participant repair was performed.

The representation limit in [report 192](192-reset-reference-maintenance-interim.md)
must remain visible in the final comparison: the conventional checker can
construct a one-field String union or record using reflection, whereas the
frozen Flow positive probe uses a fixed scalar constructor name. Reflection is
part of the external F# scorer, not a new language escape hatch. Do not treat
the reset participant's unexecuted cases as observed behavioral defects or
change the frozen checker silently during the cohort.

A focused read-only review confirms that the task wording does not explicitly
require the scalar constructor spelling. After the original cohort closes,
run a separately labelled representation-adapted check on the immutable reset
snapshot: change only construction/projection for the one-field String record,
retain the same 18 model cases and nominal-type controls, and preserve both
original and supplemental results. A supplemental pass would establish only
those behavioral vectors under that representation, not a comparative ranking.

The [evidence index](evidence/193-conventional-reference-maintenance-interim/index.json)
retains the terminal snapshots, complete scorer results, protocol measurements,
preservation audits and representation review. The bundle contains only the
two completed F# participants, not the live reset trial. No task feedback or
repair was supplied to either F# participant;
accepted runtime and frozen study inputs remain unchanged.
