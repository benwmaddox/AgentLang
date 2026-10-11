# 192 — Reset-rich reference maintenance: interim evidence

Status: two of six participants completed; the cohort remains open. The first
F# participant has been dispatched. This is not a completed comparison.

The first reset-rich participant closed its original broker normally with
host/runtime exits 0/0 after 78 exchanges. Its trace reports 24/24 project tests
passing and successful task finalization. Independent acceptance did not pass.

The participant introduced `record TrackingReference { field value: String }`
and used the generated `trackingReference.new(value = ...)` constructor.
The frozen checker instead probes the scalar constructor
`TrackingReference.new(...)`. Those probes reject with FLOW_UNKNOWN_CALL. Its
subsequent raw String fallback fails argument typing, so **zero of the 18
behavioral cases executed**. These are construction/setup errors, not 18
observed replay-behavior failures. The original scorer result is retained.

Static record-field metadata, raw String and ShipmentId rejection controls,
and lower-level lookup helper signatures pass. The scorer verifies that the
captured participant source is unchanged. These checks establish nominal field
separation, but do not establish independent end-to-end behavioral acceptance.

The task requested a nominal TrackingReference over String with no predicate
or normalization. Its wording did not explicitly specify constructor spelling.
The single-field record choice differs from the scalar representation used by
the accepted readiness control and frozen checker. Keep this compatibility
limit visible when interpreting the pilot; do not infer a reliability ranking
from it or silently adapt the checker during the cohort.

Protocol measurements are 70,804 request-payload bytes, 182,375 response-payload
bytes and 1,312.128 seconds of broker duration. They are not provider token
usage, effective context size or total agent wall time. The lower exchange
count than retained-1 is not evidence of a cheaper correct completion.

The separate read-only source comparison accounts for all 18 inherited tests
and two examples: eight are byte-identical and twelve wrap the same String
literals in the new record constructor. No removed, weakened or semantically
changed inherited cases were observed. Six added tests bring the total to 24.
All nine seeded function identities, prior source history and maturity levels
are retained. The existing lookup-step library function published with its
library gate; the other eight remain project functions. Current source routes
ingestion through that lookup step. All 87 captured file pins match.

Three rejected discovery/help requests remain unresolved. Three stale-source
edit failures have a later successful atomic definition. An invalid example
lookup has later passing example calls. A second commit attempt was rejected
after the group had already published; the captured task revision is present.
No code repair or oracle feedback was supplied to the participant. The runtime,
frozen study inputs and empty external-effect capability policy are unchanged.

The [evidence index](evidence/192-reset-reference-maintenance-interim/index.json)
retains the terminal capture, complete scorer result, measurements and
preservation audit. It contains only the completed reset participant, not the
live F# trial. The other four participant results remain pending; report each
endpoint separately before drawing conclusions.
