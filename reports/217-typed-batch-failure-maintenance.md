# 217 — Fresh typed batch failure maintenance

Status on 2026-10-11: retained arm accepted after independent scoring and source
review; the reset trial is running. This is not yet a closed paired result.

A fresh external Luna/max coding agent maintained an unfamiliar typed library
API through the broker. It changed the batch result from a BusinessError to
`SubscriptionRenewalFailure`, preserving the first failing input's zero-based
index, nominal SubscriptionId and exact inherited error code/message. Complete
successful Store results, input state, existing public APIs and inherited
evidence remain intact. The functions retain their identities and library
qualification after atomic publication and a fresh-process reload.

| Retained endpoint | Independent observation |
| --- | --- |
| Held-out full-result migration cases | 9/9 |
| Original creator behavior regressions | 66/66 |
| Common inherited tests | 137 |
| Total tests | 152/152 |
| Current example owners | 39, all passing |
| Scoring responses | Exactly 116, zero CLI exit |
| Actor, baseline and scoring-copy preservation during score | All unchanged |
| Root/helper identities and library maturity | Preserved |
| Inherited test scenario/assertion preservation | Independent review accepted |

All four original batch tests and two helper tests keep their identities,
fixtures, scenarios, success expectations and underlying causes. Three batch
tests are byte-identical. The existing three-item first-error test adds index/ID
assertions without changing its precedence scenario. Helper adaptations add typed
failure wrappers, counters and stronger localization assertions. A new later-item
test checks index, missing ID, cause code and input preservation. The independent
nine-case scorer separately verifies the complete cause code and message. Review found
no weakened assertion or replaced scenario. Other domain functions, types and
examples are unchanged; the only new type is the related failure record.

The agent uses the retained monthly/annual renewal APIs through the existing
batch helper; it does not rewrite those lower-level rules. This shows a correct
typed maintenance edit on accumulated vocabulary, beyond merely composing a new
wrapper. It does not establish better reliability than F#, a reliability rate or
a causal retention benefit. One fresh participant per unequal terminal-213
continuation project cannot isolate vocabulary availability from implementation
and inherited-test differences. The reset outcome remains pending.

Retained records 39 broker exchanges, 34,259 request wire bytes and 112,799 response
wire bytes, with eight host-reported errors. Broker elapsed time is
685.5625005 seconds, including closure assistance. These are protocol and lifecycle
measurements, not model tokens, context limits or a development-speed comparison.
The participant disclosed denied format/transitive-dependencies and closure
operations. The complete trace remains evidence rather than being cleaned into
a successful-only transcript.

The participant finished its edit and tests but could not close its still-live
broker. Root supplied only the `host.close` transport control, prohibited further
edits/tests and received terminal confirmation. The final trace audit verifies
host/runtime exits 0/0 and a separate 21-byte close request. This post-edit
lifecycle intervention must not be described as wholly unassisted completion.
Root also confirmed broker PIDs using a host-side read-only probe when sandbox
process visibility could not resolve them; no process was restarted or interrupted.
No implementation hints or outcome feedback were supplied.

Before the first dispatch, all 6,617 frozen input files matched length/hash pins.
Before the second, all 6,328 unchanged files matched; only the completed retained
actor was excluded from that second initial-input check. The [216 preparation](216-typed-batch-failure-preflight.md)
established public-interface authoring readiness and independently calibrated
fault controls. Both trials use the same pinned interpreter, virtual I/O, empty
effect grants and 100-exchange cap, in the pre-recorded retained-then-reset order.
Fresh contexts receive the same neutral task packet and no control solutions.
The broker is prompt-governed, not an OS security sandbox; no new network listener,
external service or arbitrary .NET escape is introduced.

Evidence: [sealed retained result index](evidence/217-typed-batch-failure-maintenance/retained-result.index.json).
Its archive contains terminal actor/scoring copies, trace, audit, dispatch and
independent review. Every archive member was reopened and checked against its
length and SHA256. The automated scorer deliberately leaves accepted-edit and
overall pass false until source review is combined separately; passing coverage
alone is not semantic preservation.
The [review clarification](evidence/217-typed-batch-failure-maintenance/retained-review-clarification.md)
corrects overly broad wording in the archived review receipt about the added
test's cause assertion; its acceptance decision is unchanged.

Next close, score and review the fresh reset trial before making a paired
observation. Then improve the CLI's explanation of same-session atomic record
publication without relaxing CAS, type or library gates. The read-only help
investigation was isolated from participant material and caused no trial-runtime
change. The [215 efficacy decision](215-efficacy-after-paired-vocabulary-pilot.md)
still governs broad claims.
