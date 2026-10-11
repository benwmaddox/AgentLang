# 196 — Typed reference maintenance: original cohort results

Status: the original six-participant cohort is complete, audited and packaged.
The representation-adapted supplement was pending at original closure and is
now recorded separately in [report 197](197-typed-reference-record-supplement.md). No comparative reliability
ranking or general vocabulary advantage is established.

The accepted record-evolution capability lets external coding agents migrate
persisted domain data through the language interface. Both retained participants,
the second reset-rich participant and both F# participants pass the original
18-case behavior checker. The first reset-rich candidate uses a nominal
one-field String record rather than the scalar constructor assumed by that
checker: none of its behavior cases executed. This is setup incompatibility,
not 18 observed behavior failures. Keep the original result and evaluate that
immutable candidate with the separately labelled adapter from report 193, as
subsequently completed in report 197.

| Participant | Original independent behavior | Local tests/assertions | Broker exchanges | Rejected requests |
| --- | --- | ---: | ---: | ---: |
| retained-1 | 18/18 executed and passed | 27 | 96 | 9 |
| retained-2 | 18/18 executed and passed | 26 | 80 | 6 |
| reset-1 | 0/18 executed; 18 setup errors | 24 | 78 | 9 |
| reset-2 | 18/18 executed and passed | 23 | 59 | 6 |
| fsharp-1 | 18/18 executed and passed | 13 | 26 | 2 |
| fsharp-2 | 18/18 executed and passed | 17 | 9 | 1 |

Local test and assertion counts use different seed suites and language gates;
they are not comparable measures of behavioral coverage. Rejected requests
include discovery, stale-source/CAS protection, test type checking, history
queries and redundant commits. They are not all software defects.

All six brokers closed normally on their original sessions with host/runtime
exits 0/0. All independent scorers confirm captured source was unchanged. Static
nominal field metadata, raw String/ShipmentId negative controls and selected
helper-signature checks pass for every candidate, including reset-1. The five
constructible candidates also pass their nominal constructor probes. These
type checks do not substitute for behavioral scoring or caller-source review.

The final retained candidate records 26/26 tests and three passing examples.
Its first library commit publishes the atomic group. A second commit rejects
the already-published helper; subsequent inspection confirms it is current at
revision 2 with library maturity. The task then finalizes normally. All ten
seeded function IDs and maturities remain; only the shared find-scan function's
reference parameter changes among authored signatures. The existing chain from
batch ingestion through the shared lookup helper is retained.

| Participant | Inherited tests/assertions | Inherited attached examples |
| --- | --- | --- |
| retained-1 | 21/21 preserved | 3/3 preserved |
| retained-2 | 21/21 preserved | 3 IDs retained; all 3 materially rewritten |
| reset-1 | 18/18 preserved | 2/2 preserved |
| reset-2 | 18/18 preserved | 2/2 preserved |
| fsharp-1 | 8/8 preserved | Not applicable |
| fsharp-2 | 8/8 preserved | Not applicable |

The final retained candidate's example changes exceed nominal-constructor
adaptation. Its first-reference example now checks the first of duplicate scans;
append-one-scan now compares a complete Store; ordered-events replaces the
one-shipment scenario with a two-shipment fixture and full equality. These
strengthen some checks, but do not preserve the original evidence unchanged.
The frozen assignment explicitly prohibited replacing inherited evidence.
Record this as a preservation caveat, including the replaced scenario, rather
than treating passing examples or retained IDs as proof of semantic continuity.
The original bodies remain inspectable in history; that is distinct from
retaining them as current executable examples. No participant repair was made.

| Participant | Request bytes | Response bytes | Broker seconds |
| --- | ---: | ---: | ---: |
| retained-1 | 105,022 | 107,340 | 1,123.069 |
| retained-2 | 67,162 | 113,850 | 1,717.875 |
| reset-1 | 70,804 | 182,375 | 1,312.128 |
| reset-2 | 43,497 | 92,976 | 1,243.289 |
| fsharp-1 | 12,818 | 35,567 | 414.958 |
| fsharp-2 | 23,157 | 17,743 | 227.417 |

F# uses fewer exchanges in this pilot. Protocol bytes and broker duration are
not model token usage, effective context, total agent wall time or service
throughput. There are only two fresh Luna/max participants per arm, in fixed
order, with setup-created vocabulary and an explicit preservation contract.
Reset-rich contains useful domain code; it is not the PRD's primitives-only
flat treatment. This task tests maintenance of supplied vocabulary, not organic
abstraction growth across independent tasks. Do not pool it with the interrupted
cohort 185 or infer general reliability, token or runtime advantages.

Recovery remains a separate endpoint. The final retained participant corrects
discovery mistakes, a stale type-source hash and an inherited test type mismatch;
the redundant second commit concerns an already-published function. Its blank
diff query remains unresolved. Earlier receipts retain unresolved AgentLang
discovery/history queries and one stale F# comment rather than erasing them
after a passing score. Both F# traces correct their rejected requests.
Coordinator transport clarifications and the inconclusive PID-visibility probe
are outside-broker interventions, with no task logic, oracle feedback, repair
or changed trial limits supplied.

The bounded decision is to continue the prototype, without claiming it beats
F# for reliable maintenance. Discovery, shared-helper reuse and guarded nominal
migration work repeatedly through the runtime. Strict typing and current
library checks still do not certify inherited evidence preservation: the
example replacement is a concrete counterexample. Keep independent behavior
and source-preservation review alongside those gates. Do not repeat another
easy composition trial to turn these feasibility results into a superiority
claim.

[Report 197](197-typed-reference-record-supplement.md) subsequently closes the
narrow representation supplement: the captured reset-1 candidate passes all
18 cases under the record-specific adapter. Its original setup-failure score
and this archive remain unchanged; no participant repair was made.

Continue native semantic conformance after this evidence closure. The next implementation remains the
Bool slice in [report 194](194-native-bool-implementation-plan.md), including
its admission review. This cohort adds no native service measurement and
selects no mailbox memory policy. It adds no host capability; the trial brokers
start with empty effect capabilities.

Earlier terminal evidence and limits are recorded in
[report 191](191-typed-reference-maintenance-interim.md),
[report 192](192-reset-reference-maintenance-interim.md),
[report 193](193-conventional-reference-maintenance-interim.md) and
[report 195](195-second-reset-reference-maintenance-interim.md).
The [complete original-cohort evidence index](evidence/196-typed-reference-maintenance-cohort-results/index.json)
seals all six snapshots, traces, independent scores, preservation receipts,
prompts, runtime/source pins and coordinator notes. All 1,360 archive members
were verified by hash and length. Termination and independent scoring ran
locally against the frozen binaries; source/runtime pins were rechecked before
packaging. This report-only milestone requires no shared-runtime rebuild.
