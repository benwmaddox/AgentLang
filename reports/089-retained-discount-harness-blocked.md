# Retention study: harness-blocked retained S07 cell (R04)

Status: R04 (B1/Retained/S07) was blocked before behavior. The independent verifier rejected frozen provenance, and the trace audit exited 1 without producing an audit JSON. Four of twelve actor attempts are recorded: R01 and R03 passed, R02 failed independent behavior acceptance, and R04 has no behavior outcome. No comparative retention advantage is established.

A fresh external actor was launched for the Retained arm using R03's accepted S01 output. The prepared rich starting project contained 53 words, 31 types, and 151 tests. The launch evidence declares gpt-6-luna, max reasoning, and fork_turns=none; those records do not independently verify provider settings. The initial task message had one paragraph shortened and then corrected, which is a documented prompt deviation.

## Actor work and own checks

The trace includes a source lookup for the inherited customer.premium? classifier. The final customer.discounted-balance definition calls customer::premium?(customer), so classifier reuse is visible in the recorded source. The actor-side final test batch passed 6/6 cases with 33/33 instructions and 2/2 branch outcomes covered. Its test-all call passed 163/163 tests. Across the task log, 187 tests ran and 8 failed.

The earlier test batches explain those failures: the first 6 failed because their Instant fixtures did not satisfy the refinement validator; after fixture correction, 4/6 passed and the two remaining Int64 endpoint tests had incorrect expected literals. The actor corrected those literals, then the final 6/6 batch passed. These are actor-side signals only and do not establish independent acceptance.

## Independent verification

The verifier process exited 1 at Verify-RetentionTrial.ps1 line 816 on an undefined $prior, before behavior evaluation. The saved acceptance records 78 checks (76 metadata and 2 behavior), with TestExecutionCount=0; its metadata and behavior flags are false because frozen provenance did not validate and no CLI behavior evaluation was allowed. No business behavior pass or fail was established.

The trace-audit process also exited 1 because its acceptance check identified this frozen study cell. No trace-audit.json was emitted. The audit console is preserved as the original failure evidence.

After the blocked review, the coordinator overrode the explicit success-only teardown condition and authorized controlled close. The actor then sent the exact {"op":"host.close"} control; the trace records host and runtime exit codes 0/0. This protocol deviation documents teardown only and does not repair the blocked acceptance or audit.

## Preserved evidence and limits

The archive contains 625 exact files (10,802,889 bytes): the complete canonical run, final actor project, and six launch/preparation/freeze/verifier/audit evidence files. The index at evidence/089-R04-index.json lists every file by repository-relative path, byte length, and lowercase SHA-256, verified against its source.

The trace records 35 exchanges. The independent 54-case corpus ran zero cases. Native model usage is unavailable; no token, turn, latency, or memory measurement is claimed. R04 provides no retention-effect result.

## Later supplemental evidence

A separate scratch-only scorer subsequently passed all 54 independent S07
cases and verified retained-helper reuse. See [report 090](090-retained-discount-supplemental-scoring.md).
The canonical failure and missing original trace audit described above remain
unchanged; the supplemental result does not reclassify this trial as accepted.
