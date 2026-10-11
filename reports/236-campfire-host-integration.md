# 236 — Campfire typed storage integration

Status: validated interpreter storage integration. Application, focused interpreter/native and resolved regression gates passed.

Campfire room/message functions now call the four fixed SQLite operations through a typed, effect-checked host bridge. The language checks membership before message reads or writes and derives the creator from the caller supplied by the future authenticated HTTP boundary. It gives administrators no implicit room-membership bypass. The standalone storage behavior is documented in [report 235](235-campfire-sqlite-persistence.md).

## Application and trusted boundary

`campfire-types.flow` owns the 13 shared nominal, record and enum declarations. `room-message.flow` implements room paging, membership-gated message listing and creation. The integration harness qualifies these three functions and the two administration policies as libraries, persists them, and checks fresh-runtime reload.

The closed per-Engine host manifest retains stable operation IDs, signatures and declared effects in verified semantic IR. It registers only `campfire.rooms.page-for-user`, `campfire.membership.exists?`, `campfire.messages.last-page`, and `campfire.messages.create`. There is no arbitrary SQL, reflection or .NET invocation interface. Database paths and production-provider lifetime belong to the host.

Capability checks happen before provider invocation or scoped replacements. Host results are checked for arity, type, nominal identity, record fields, enum cases, container metadata and pure refinement validators, with value budgets. Tests lazily construct a fresh provider only for the first unoverridden host operation and dispose it after the case. Fully replaced tests construct no database provider, and tests never fall back to production storage.

Database capabilities permit the raw registered operations; they do not enforce row-level user authorization. Membership and creator selection are language-function policies. The future HTTP boundary must invoke fixed authorized application functions and must not expose arbitrary evaluation or raw storage operation selection to clients. This distinction is part of the independent application review, not proof of an HTTP boundary that has not been built.

Direct legacy Stack-authored host calls are rejected because that representation cannot retain their identity. Flow callers retain the exact operation binding. Persistence format is 7, IR formatter version is 6 and snapshot fingerprint version is 4. Native backends conservatively reject programs retaining any host-operation contract, including when the selected entrypoint does not use it. Native Campfire effects remain future work; no interpreter fallback is implicit.

## Local validation so far

All builds are serial Release builds using audited restored assets, with `MSBuildEnableWorkloadResolver=false` and `MSBUILDDISABLENODEREUSE=1`. Fresh focused gates passed:

- Flow: 1,330 assertions; Flow Runtime: 46 groups, 2,462 assertions.
- Semantic IR: 198 assertions; IR formatting: 57; interpreter: 161.
- LLVM native conformance: 1,010 assertions, including scalar/owning host-operation rejection before tools, and 30,400 finite float-formatting patterns per optimization profile.
- Combined `scripts/Verify-Campfire.ps1 -NoRestore`: after the session-restoration extension, six fresh builds and all component/policy gates passed. Storage: 60 assertions; language-to-SQLite integration: 347; cookies: 76; authentication storage: 102; language authentication integration: 349.
- Administration policy: 24 attached tests and 18 independent cases; request policy: 29 attached tests and 840 independent cases; session policy: 10 attached tests, 36 independent duration cases, two refinement rejections and fresh library reload.
- A second direct integration run passed all 347 assertions again, verifying that the harness uses fresh project state on every execution.

The integration includes 52 scoped cases and 10 real SQLite cases. It checks zero production/provider-factory calls under scoped replacement, per-case real-provider disposal, membership denial before downstream access, creator identity, transaction rollback, persisted bodyless messages and database reopen. Raw failed and passing attempts remain under `.agentlang/campfire-host-236/`; the final combined logs, commands and source/runtime hashes are in `root-combined-auth/receipt.json`.

The broad `Validate.ps1 -SerialBuild` run passed 35 of 37 checks. Its structural call-binding probe initially lacked three new host fields, and its isolated conventional preflight failed at the sandbox vulnerability-feed boundary. After the field-only script update, the exact binding probe passed 31 assertions against the same Core SHA-256 (`3dfabb10a30f918d5bbf640e12674900d3b7b7435a17a91da437ff758a8fdf8b`). The trusted repository preflight passed 98 checks across 30 independent outcomes outside the sandbox with auditing enabled. No participant code ran outside the sandbox. The original failed report remains intact; `aggregate-validation.json` records the two resolutions and all 37 checks. This is a resolved gate, not a claim that the original full invocation returned zero.

## Failures found and corrected

A scalar-import implementation initially passed a wrapped nominal value to a validator that accepts its base payload. Positive and negative refined-scalar controls now verify the corrected order. Persistence readers initially rejected the format 7 emitted by the writer; reload controls exposed and corrected that mismatch. Empty initial host-registered projects require narrowly deferred nominal resolution; proposed and loaded nonempty state remain strict.

Existing tests caught an introduced Option-match verifier typo: the None branch was checked using the Some payload's local state. It was corrected without disabling the payload-binding or escape tests. A direct dotted call exposed an effects-header parser bug that consumed the next line as part of an effect name; same-line segment checks and regression cases correct it.

The first combined rerun exposed a fixed-directory integration harness that attempted to bootstrap types into an already committed host-bound project. Every execution now uses a unique directory, preserving earlier evidence without deleting state. Both subsequent runs pass. The first LLVM gate separately found an invalid `Map.singleton` fixture and five missing exhaustive host-target cases. The fixture now uses `Map.ofList`, and each backend explicitly rejects that target. A fresh LLVM build passes with zero warnings/errors and its native conformance run passes all 1,010 assertions.

Sandbox integration restore failed before useful diagnostics; the exact trusted project restore succeeded on the host with NuGet auditing enabled. Integration builds/tests ran in the sandbox. The final trusted preflight exception is documented above; auditing was never disabled.

The first full solution build caught three legacy owning-mailbox experiment contexts missing the new host-operation fields. They now explicitly use an empty manifest and a missing-handler callback; the repeated solution build passed. Auditing the non-solution experiments also found outdated constructors in NativeMailbox, NativeDispatch and NativeValueStack. Those three field-only adapters now freshly build with zero warnings/errors against the tested Core/LLVM artifacts. Their sandbox NU1900 failures and successful audited host restores remain in the evidence history. The structural `.fsx` probe was also updated and passed as described above.

## Limits and next application work

This proves interpreter-backed storage integration, not a running Campfire server. HTTP request order/statuses, rendering, Action Cable, attachments, search and other application features remain incomplete. Rich-text bodies are stored but are not safe or compatible rendering output. Full native arena/mailbox execution and application parity remain part of the overall objective.

Session restoration is now separately validated in [report 241](241-campfire-session-restoration.md); authenticated HTTP handling is next. The sealed archive `reports/evidence/236-campfire-host-integration/evidence.zip` contains 228 members, including 51 source files, failed/passing validation logs and session-policy/restoration receipts. Every member was independently read back and hash-verified. SHA-256: `c6737f6067c25ab124d51f2362c00358c5e5b671655602c9a243fa019f0f3883`. Databases, compiled binaries and diagnostic environment dumps are excluded.
