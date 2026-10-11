# 195 — Second reset-rich reference maintenance: interim evidence

Status: five of six participants completed. The final retained-vocabulary
participant is running. The frozen cohort remains open; no comparative
reliability ranking is established.
The [delivery roadmap](../docs/ROADMAP.md) now reflects all five completed
participants and the required post-cohort representation check.

The second reset-rich participant passes all 18 independent behavior cases,
nominal construction probes, static field metadata, raw String and ShipmentId
negative controls, and the lower-level lookup helper signature check. The
scorer confirms that the captured participant source remained unchanged.
This candidate uses a scalar `TrackingReference : String`, which the frozen
checker can construct. The separate record-constructor limitation in
[report 192](192-reset-reference-maintenance-interim.md) remains unresolved
by this result and still requires the planned post-cohort supplemental check.

The read-only preservation audit accounts for all 18 inherited tests and two
examples. Their token sequences match after removing formatting/root-dot
qualification and adapting reference literals to the nominal constructor;
this is a bounded source comparison, not a formal AST equivalence proof.
Five tests were added, and captured final validation reports 23/23 passing.
All nine seeded function IDs remain; five advance to revision 2 and four stay
at revision 1. The existing lookup step retains library maturity and is reused
by the ingestion rule and ordered batch call chain.

Commit exchange 55 reports 23 attached tests passed and selected candidates
committed. Exchange 59 finalizes the task, followed by normal host-close with
host/runtime exits 0/0 and no runtime stderr. The candidate's 89 captured file
hashes match the immutable snapshot; all 61 initial seed pins also match.

| Exchanges | Request bytes | Response bytes | Broker seconds |
| ---: | ---: | ---: | ---: |
| 59 | 43,497 | 92,976 | 1,243.289 |

These are protocol measurements, not model tokens, effective context size,
total agent wall time or native service throughput. Four of six rejected
requests have subsequent corrective evidence: a discovery argument, an
inherited test's type adaptation, replacement CAS shape and a history argument.
Two diff queries remain unresolved; passing behavior does not erase those
recovery failures.

The coordinator clarified how to use ordinary exec/write_stdin transport for
the already-authorized broker. The saved note calls this prelaunch, but its
timestamp establishes clarification after host session startup and before
the first broker exchange. No task logic, oracle feedback, code repair or
limit changes were supplied. This outside-broker interaction is retained in
the evidence and must remain visible in cohort interpretation.

The broker started with no effect capabilities, revised functions declare
`effects none`, and task effects are empty. No host capability or runtime
change was made for this milestone. These facts do not constitute a broader
security certification.

The [evidence index](evidence/195-second-reset-reference-maintenance-interim/index.json)
contains the terminal trace, snapshot, complete independent results,
preservation receipts and transport note. Its verified archive has 306 members
and excludes the live final participant. Shared runtime and frozen study pins
were rechecked before packaging. Local termination, source/hash, oracle and
archive checks passed; no shared-runtime rebuild was needed for this report.
