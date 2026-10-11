# 241 — Campfire language session restoration

Status: validated interpreter session restoration. Independent Release builds, integration repeat and combined Campfire gate passed.

`campfire.authentication.restore-session` is language-authored policy over a closed six-operation authentication adapter. It accepts a token already verified by the trusted cookie component, explicit UTC time, and optional request IP/user-agent. It looks up the session, then its user, returns ordinary absence for missing rows, and refreshes only when the existing strict-hour policy says the session is due. It returns a typed user/session projection and whether the HTTP boundary should refresh the cookie. Database errors propagate unchanged.

The function preserves roles and statuses without adding a status filter, matching the pinned Rust restoration lookup. Account deactivation/ban session invalidation is separate application work. `SessionToken` is a nominal identity, not a cryptographic proof: the future fixed HTTP entrypoint must establish cookie verification before calling this function. Arbitrary evaluation and raw storage operations must remain unavailable to HTTP clients.

## Trusted adapter and tests

`Authentication.Host/` registers only session lookup/conditional refresh, user lookup/active bot key, banned-IP lookup and membership-scoped room lookup. Exact typed DTO conversion preserves optional IP/user-agent and UTC microseconds, including pre-epoch timestamps. It exposes no SQL text or .NET escape. Time is an input to the language policy, so restoration has `db.read`/`db.write` effects and no hidden clock read.

The integration suite passes **349 assertions across 41 attached Flow cases**: ten existing UTC/refresh cases, 27 scoped restoration cases and four real SQLite cases. Scoped replacements exercise all database error variants at the three restoration calls, missing records, strict-hour boundaries, metadata absence/update, and all nine role/status combinations. They perform zero real effects. Real cases verify cookie-derived token restoration, due refresh, missing token and trigger-aborted refresh. Tests, qualification and reload create 16 distinct real-provider fixtures and dispose each once; the production sentinel is never called.

The trusted cookie component rejects tampered, expired and wrong-purpose fixtures before restoration in the harness. This is component integration, not proof of HTTP callback ordering. Direct adapter controls additionally exercise its six operation contracts, nominal-input/unknown-operation rejection, bot/ban/room lookup and pre-epoch conversion. Fresh reload retains library maturity for the UTC validator, refresh policy and restoration function.

## Validation and evidence

Root independently rebuilt the Host and harness serially in Release with audited existing assets and `BuildProjectReferences=false`, then repeated the test executable. Both builds have zero warnings/errors and the repeat passes all 349 assertions. Logs are under `.agentlang/campfire-auth-integration-241/root-validation/`; the unique passing run and six source pins are at `runs/20261011T231620084Z-9144fc7d5b9b4c06a4d2db28765cdbf2/`.

The combined `scripts/Verify-Campfire.ps1 -NoRestore` now freshly builds and runs authentication integration alongside the existing components and all three pure-policy verifiers. Its passing receipt is `.agentlang/campfire-host-236/root-combined-auth/receipt.json`. Core remains the same tested build; no new language feature was needed for this slice.

Failed attempts are preserved. The no-host bootstrap initially committed helpers as project words before attempting library qualification; qualification correctly rejected a non-candidate. Bootstrap now tests and qualifies the pure helpers before publishing the remaining types, then qualifies restoration under the manifest-backed Engine. An initial bot fixture used the wrong token shape and was corrected against the fixed storage parser. Silent build failures were resolved with the configured workload resolver and writable task-local temporary directory. New-project sandbox restores failed; reviewed trusted host restores succeeded with auditing enabled. All authentication builds/tests ran in the sandbox.

## Remaining application work

HTTP transport, cookie renewal/response attributes, CSRF/request ordering, login/account invalidation, safe compatible rendering and browser/live-update behavior are still pending. The next step is the fixed authenticated room/message HTTP slice. Native host-operation execution remains explicitly unsupported; this milestone uses the interpreter and .NET host adapters.

Passing root build/run logs and all three passing run receipts/source manifests are included in the 228-member `reports/evidence/236-campfire-host-integration/evidence.zip`, together with the exact new source. See [report 236](236-campfire-host-integration.md) for its independently verified archive hash and the resolved broader gate. Failed authentication attempts remain in the archive logs and private run directories; no database files or production secrets are included.
