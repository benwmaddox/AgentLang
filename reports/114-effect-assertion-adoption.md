# Effect-assertion adoption: behavior repaired, regression protection absent

One fresh Flow/2 actor repaired the reminder behavior and passed the independent
behavioral oracle. Its seven attached tests did not detect the original
extra-write defect when that defect was restored in a disposable copy, so the
adoption criterion was not met. The discovery result is confounded: every
successful help request selected Flow/1 by default, while effect-count syntax is
documented only in Flow/2 help. This single run does not establish whether the
actor would use the feature after seeing version-matched help.

## Trial and controls

The trial used one fresh `gpt-6-luna` actor with max reasoning and 27 broker exchanges. The frozen
prompt, verifier, and runtime metadata are in
[the evidence package](evidence/114-effect-assertion-adoption/README.md).
The pinned runtime came from source checkpoint
`a8e7d34b9e8ae50ea3281a4df31fb8b17014028c`; its CLI assembly SHA-256 is
`af2da9bfeaa706a4c67592465b23c48847aadbbd2d6d578342df1412662c3646`. The
starting project was report 111's committed faulty control at target revision 2.
The copied verifier retained its behavior checks and changed only seed metadata
assumptions.

Before dispatch, the preflight passed 52 checks. The copied verifier accepted its
positive control in 143 checks. Its return-only faulty control reported 96
checks and failed at the intended exact effect assertion: `fs.read=2,
fs.write=1; expected 2/0`. The preflight also confirmed that the legacy attached
tests passed on the faulty seed. These controls show the pinned runtime and
copied oracle could distinguish the extra write.

## Outcome

The actor committed revision 4 of `invoice.queue-reminder-once` as a persistent
library replacement. Its final project had seven attached tests and one
example. The independent behavioral verifier passed all 146 checks, and the
actor's 43-file project inventory was unchanged by verification.

The source evidence reports zero effect-assertion tests. On a separate
disposable copy, I restored the frozen extra-write definition while retaining
the actor's seven tests. All seven still passed, all 25/25 instructions and
4/4 branches were covered, and the replacement was accepted at revision 5.
Thus the suite checks returned values and stored contents but does not reject an
identical-content write. Full coverage did not provide the missing effect
assertion. The final behavior repair succeeded; the requested regression-test
adoption did not.

## Help-version confound and process evidence

The actor made five help requests. Four succeeded—bare help and the `define`,
`examples`, and `replacement` topics—and all omitted `syntaxVersion`, so each
returned Flow/1 help. The fifth requested unknown topic `task`. The prompt
identified Flow/2 for `define` and `eval`, but its help example was unversioned.
Effect assertions appear only in Flow/2 help. The actor therefore was not shown
the feature's help text. A controlled follow-up should request version-2 help
without giving the assertion syntax in the prompt; this result should not be
read as evidence that the actor could not use the feature.

The broker trace records `host-close`, then `session-end` with host and runtime
exit code 0 and no stderr. The actor's final message says its later status poll
returned “Unknown process id,” so it had no exit code from that poll. Both
records are preserved. The disposable mutant's CLI process exited 0 and its
raw JSONL was saved; the first PowerShell summary attempt exited 1 because
`PSCustomObject.Contains()` was called. The structured summary was reconstructed
from those saved responses and the recovery command exited 0. The mutant did
not change the actor's final project.

This was a single adoption probe, not a comparative efficacy or superiority
result. The evidence package includes the exact actor message and broker trace,
frozen prompt/runtime/provenance, verifier copies and reviews, preflight
controls, final behavioral acceptance, source inventories, and disposable
mutant inputs and outputs. It contains no runtime binaries. See its `README.md`
and `SHA256SUMS.txt` for the package map and integrity index.

Follow-up: [Report 115](115-versioned-help-adoption.md) records the separate
versioned-help replay using the same pinned runtime, seed and oracle. The negative
result above remains unchanged.
