# 242 — Campfire authenticated HTTP slice

Status: validated locally; the complete migration remains in progress.

This milestone moves Campfire beyond isolated policy and storage calls: a typed
Flow application now handles authenticated room display and message creation
through Kestrel and SQLite. It establishes a working path for the migration,
but covers only two route handlers and is not a compatibility or deployment
claim. The full scope remains in the [implementation plan](../docs/CAMPFIRE-IMPLEMENTATION.md)
and the [177-row Rails route inventory](../docs/campfire-route-inventory.json).

## What works in this slice

The application binds one committed Flow entrypoint with a fixed typed request
and response contract. Request data cannot choose function names, protocol
operations or source code. Flow owns the request decisions; narrow F# host
adapters provide cookie handling, SQLite, browser normalization and Kestrel.
The adapter starts from a stable project dictionary and fails on a partially
initialized store instead of silently reseeding it.

The two exercised handlers are `GET /rooms/:id` and
`POST /rooms/:room_id/messages`. Room display uses membership-scoped lookup and
the last 40 messages in chronological order. Message creation uses the
authenticated user as creator, persists before negotiating the response format,
and returns the tested post-write 406 behavior when the client cannot accept the
available response. Missing and malformed input, missing rooms, method handling,
and request-size limits have explicit outcomes.

The test suite sends real requests to an ephemeral local Kestrel listener and
uses temporary SQLite databases. It checks cookie/session restoration, CSRF,
browser rules, room authorization, message creation, transaction rollback,
client-message IDs, request parsing and restart behavior. An absent
client-message ID receives an injected UUID inside the transaction; a supplied
ID, including an empty string, is preserved. This does not change the database
schema.

## Validation evidence

The combined Release gate passed all 18 checks: eight clean builds, seven
executable test suites, and three independent policy verifiers. Package audit
was enabled. The receipt pins the tested source files and Core binaries at
`.agentlang/campfire-http-242/root-combined-03/receipt.json`; its baseline Git
revision is `3734bdc4005de0f2dc42fde36f532c58e75eda7d`.

The seven test suites reported:

| Area | Result |
| --- | ---: |
| SQLite message persistence | 73 assertions |
| Campfire host integration | 474 assertions |
| Rails cookie compatibility | 76 assertions |
| Authentication SQLite storage | 102 assertions |
| Authentication integration | 390 assertions across 43 attached Flow cases |
| Browser normalization and policy | 2,354 assertions |
| Authenticated HTTP and SQLite | 149 assertions |

The browser suite covers 385 pinned User-Agent examples, 44 Ruby version
examples and 352 version comparisons. Its scoped Flow library cases qualify and
reload without constructing a provider; separate tests exercise the real host
provider. The three policy verifiers cover 24 attached and 18 independent
message-policy cases, 29 attached and 840 independent request-policy cases, and
10 attached session tests plus 36 duration cases and two refinement rejections.

The Core value-entrypoint check was run separately from the 18-check Campfire
gate: a clean Release build and 47 groups / 2,512 assertions. It verifies that
invalid inputs, capability denials and aggregate-budget failures are rejected
before providers run, and checks frozen-entrypoint/rebinding behavior and task
accounting. This API accepts already typed values and does not route each
request through parsing or a general-purpose command dispatcher.

Fresh focused regression builds also passed 1,329 frontend assertions, 198 IR
assertions, 57 formatting assertions and 161 interpreter assertions. A fresh
LLVM build and compiled conformance run also passed all 1,010 assertions. The frontend
test now accepts and round-trips the seventh observable effect category,
`random.read`, rather than retaining the old unsupported-category expectation.
Effect counts record provider-boundary crossings; injected UUID-generator counts
are checked separately.

The HTTP test process could not open loopback sockets in the filesystem sandbox
(Windows socket error 10013). The same freshly built local test executable was
therefore run on the host, where it bound only an ephemeral `127.0.0.1` listener.
The run used synthetic Rails cookie fixtures and temporary SQLite data; it did
not use production Campfire data, credentials or an external network service.
Both the focused HTTP run and the final combined run passed all 149 assertions.
This host-side exception applies to local test networking only.

## Fixes established by the tests

- Request-size enforcement now rejects an oversized chunked form before the
  Flow entrypoint or any provider is invoked. The HTTP test confirms a 413 and
  zero provider calls. An earlier version returned 413 only after application
  work; that defect is fixed in the passing source.
- Accept negotiation follows the pinned behavior for absent and browser-style
  wildcard headers. Unsupported HTML responses may produce a 406 after a
  message has committed; explicit Turbo requests and the tested format override
  remain supported.
- Session restoration refreshes a due session before looking up its user. Found
  orphan sessions remain distinguishable from missing sessions, and a user
  lookup error occurs after the refresh commit while suppressing a cookie
  directive in the failed result. Tests reopen SQLite to verify persistence.
- The browser fixture is copied into the test output and its source and license
  notice are packaged with the test project.

Report 241 retains the earlier session-order result. Its finding was corrected
and revalidated here; the historical report remains unchanged.

## What remains for a complete migration

The route inventory is a compatibility checklist, not an implementation count.
Only the two room handlers above have this HTTP evidence, and their inventory
rows remain marked in progress pending broader compatibility checks. This
milestone does not validate the other routes or establish a meaningful
percentage of the application.

The Kestrel adapter is currently a library and test harness, not a deployable
Campfire server. Login and session issuance, full Rails-compatible layouts and
assets, Action Text processing, mentions and attachment behavior, Action Cable
or WebSocket live updates, jobs, notifications, search, bots, administration,
existing-data migration and deployment behavior remain. The current renderer
uses bounded templates and encodes dynamic content as plain text; this is not
full rendering parity. Forwarded headers are ignored and the public authority
is fixed, so a production proxy setup needs explicit trusted-proxy handling.

The application still executes through the interpreter. It has not been lowered
to the intended LLVM release runtime, and this milestone measures neither arena
memory behavior nor native throughput, tail latency or production load. The
next delivery work is login/session issuance, compatible rendering for the
existing handlers, and live delivery; then the remaining route, data, asset and
deployment inventory. Native arena/mailbox integration and full application
parity remain required before the migration can be called complete.

The evidence supports continuing: typed application decisions can now reach a
real HTTP transport and SQLite under an automated local test. It does not yet
answer whether full Campfire parity is feasible in this language, or how the
finished system compares with the Rust implementation on behavior and
performance.

## Preserved evidence

The [sealed archive](evidence/242-campfire-authenticated-http/evidence.zip)
contains 231 members, including 62 source snapshots, validation logs, receipts
and synthetic HTTP/provider traces. Every member was read back and verified
against the [manifest](evidence/242-campfire-authenticated-http/manifest.json).
Archive SHA-256: `88cedb89ffe4ab325115348feb193d3adbf96e56bf2142aab037534faaf5c575`.
Failed development attempts remain separate from the final passing receipts.
The archive excludes SQLite databases, native binaries and temporary build data.
