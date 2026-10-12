# 245 — Campfire logout and native delivery priority

Status: completed interpreter-reference milestone; AOT Campfire delivery is active.

This milestone finishes the bounded logout work already underway when the user
made AOT Campfire and comparable application performance the primary goal.
The interpreter implementation remains a semantic reference for native migration.
No AOT Campfire endpoint or application throughput result is claimed here.

The new library `campfire.authentication.sign-out` optionally deletes the exact
push endpoint for the authenticated user, then destroys the current session by
its nominal ID. Push failure stops session deletion. Eleven scoped tests cover
absent/present endpoint, each closed storage error and short-circuit behavior.
The trusted adapters use parameterized SQL and typed Unit/error results.

DELETE `/session` uses the existing session restoration, forgery, browser and
current-user gates before revocation. It bypasses room lookup. An empty body is
valid; optional endpoint parameters come from query or URL-encoded form, with
form precedence. Duplicate form fields fail explicitly; duplicate query fields fail only when the form supplies no endpoint. The bounded memory-only
body read applies to DELETE as well as form POSTs. Parser errors cannot trigger
revocation. Fourteen scoped HTTP cases cover gates, errors and response directives.

Success redirects to `/`, expires both `session_token` and `_campfire_session`,
and preserves `last_room`. It emits no refresh token or flash. The empty encrypted
session deletion follows the pinned Rust cookie-store reset/commit behavior.
Storage failures do not emit a successful sign-out directive. Individual SQL
statements are atomic; successful push deletion is not rolled back if the later
session deletion fails, matching the reference's ordered separate effects.

Real storage tests prove targeted revocation remains after reopening, other
sessions/users survive, exact user/endpoint scoping handles duplicate matches,
missing rows are idempotent, and trigger failures retain the affected rows.
The clean focused storage build and 17 groups / 201 assertions pass.
The first HTTP build exposed a pipeline indentation error and was repaired.
The first HTTP run exposed an incorrect expected version-comparator call for
Internet Explorer: the existing policy rejects that family directly. Its status,
no-mutation expectations and production policy were retained; only the expected
trace was corrected. Failed diagnostics remain preserved.

## Native delivery change

[The native plan](../docs/CAMPFIRE-AOT-PLAN.md) and PRD/roadmap now prioritize a
complete native Campfire application. Inspection selects the owning arena backend:
it supports strings, nominal types, records and Option/Result, but currently
rejects host calls/effects and lacks lists required by message rendering.
The compiler must export a verified committed entry body and lower pinned typed
host contracts; native SQLite, cookie, browser, rendering and HTTP adapters follow.
The same semantic IR and inspectable language policies remain authoritative.

The first native performance checkpoint is a complete GET/HEAD room endpoint
with real SQLite, compatible cookies, rendered responses and HTTP, without
F#/.NET in the release process. Compiler callback fixtures alone do not establish
that checkpoint. Match the reference endpoint/data/cache/transport/workload before
comparing published Campfire rates. Report 140's roughly 500/sec was an offered-load
mailbox fixture with simulated I/O, not maximum Campfire throughput. The optional
interpreter benchmark was canceled after the user clarified the AOT target.

## Remaining migration boundary

Logout remote-connection disconnection awaits the Cable subsystem, and `/` is
not yet a migrated landing page. Setup, full rendering/assets, live delivery,
remaining routes/data/jobs/deployment and native execution remain required.
The 177-row inventory keeps logout in progress rather than declaring full parity.
The request schema adds a logout-form field; old prototype dictionaries require
a fresh build, while existing SQLite schema is unchanged. No native ABI or Core
compiler change is part of this milestone.

Review found early query-duplicate rejection defeating form precedence. The parser now inspects query values only when no form endpoint is supplied. The success fixture includes duplicate query endpoints and proves only the form-selected user-owned endpoint is removed; a duplicate query without a form still returns 400 with no revocation. The fresh combined gate passes after this repair.

## Final local validation

Ten fresh Release builds, nine executable suites and three policy verifiers pass.
Suite assertions: persistence 73, host integration 474, cookies 76, authentication
storage 201, authentication integration 415, browser 2,354, HTTP 466, sign-in 51,
login-attempt store 4,122. The 466 HTTP assertions preserve the prior 302 and test
real database state, provider order, cookie expiry and post-restart revocation.
The sign-out library qualifies through scoped test overrides without production
IO during startup. Final receipt: `.agentlang/campfire-logout-245/combined-02/receipt.json`.

Trusted local HTTP tests ran outside the sandbox because loopback sockets are
blocked there. Fixtures are synthetic, package auditing remained enabled, and CI
was not used. The crash recovery preserved source edits and inspected running
handles before restarting terminal work. No production databases or secrets were
used. The focused failure and both combined runs remain preserved.
## Preserved evidence

The [archive](evidence/245-campfire-logout-and-native-delivery/evidence.zip)
contains 155 members, including 79 source snapshots, passing/failing receipts,
logs, redacted synthetic traces and review/planning notes. Every member passed
readback verification against the
[manifest](evidence/245-campfire-logout-and-native-delivery/manifest.json).
Archive SHA-256:
`1bd920fc49cf0b5a287e4c53114c1a41829eb828009141e1c742b5cb403c72ef`.
Temporary databases, stores and executable artifacts are excluded.
