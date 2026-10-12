# 244 — Campfire public login HTTP

Status: completed component milestone; full Campfire migration remains active.

This milestone connects the qualified sign-in library to GET/HEAD /session/new
and POST /session. The fixed Flow dispatcher applies unsafe-IP rejection first,
then public login skips cookie verification/restoration and bot authentication.
The existing Origin/Fetch-Site and browser policies precede attempt counting,
credential verification and session issuance. Protected room/message routes
retain their original authentication path.

The trusted attempt store uses a first-hit three-minute window and the Flow
policy allows ten attempts. Counts saturate at eleven. The provider retains
at most 4096 live IP keys, rejects new keys at capacity, and clears expired keys.
This is a bounded in-process logical store under the db.write capability; its
state resets on provider/process restart. It is not a distributed limiter.
A clean focused build and 4122 direct assertions prove exact expiry, key
isolation, saturation, capacity handling and concurrent updates.

The transport normalizes bounded login forms into a closed typed record.
Oversized known/chunked bodies stop before any application provider. Login
ignores irrelevant authentication/last-room cookies, including stale duplicates,
but bounds total cookie/header bytes and requires an unambiguous encrypted
session cookie. Request and provider traces redact credential values.

The fixed login template reflects only HTML-encoded email. Failure alerts are
inline only; they do not persist as next-request flash. Success signs the new
session token, consumes encrypted return-to state, preserves unrelated fields,
and accepts only a normalized destination within the configured public authority.
Invalid, foreign, protocol-relative, backslash/control-containing or non-string
return targets fall back to root. Full account branding, help-contact, assets
and layout parity are still outstanding.

The request-forgery policy follows the pinned Rust Origin/Fetch-Site profile.
As recorded in report 237, that deliberately differs from the older Rails
submodule's hidden-token profile. The Rust method named verify_authenticity_token
explicitly documents that it does not use per-request tokens.

Development attempts preserve structured parser and build diagnostics. Repairs
used the existing int.greater-than word rather than adding compiler syntax,
kept all scoped cases while splitting them into one-owner wrappers, and made
scope labels distinct. The independent Rails bcrypt fixture was corrected after
static review found a copied digest character missing; expected login outcomes
were not changed.

This does not complete migration. First-run account setup, logout, full render
and asset compatibility, live delivery, remaining routes/data/deployment, and
native arena/mailbox application execution remain required.

The fresh focused HTTP build has no warnings or errors. Real Kestrel/SQLite
integration passes 302 assertions, preserving the original 150 checks and adding
public login cases. Nine scoped test wrappers retain all 24 new language cases.
The body-limit trial exposed form parsing rejecting a long key before enforcing
the chunked-body limit. Both form POST routes now pre-read at most 65,537 bytes
into memory before parsing, reject oversized bodies with 413 before providers,
and restore/dispose the buffer after processing. No disk buffering is used.
The test clock was separated from earlier regression expectations; those
expectations were retained. Failed attempts remain in the evidence.
CampfireHttpRequest gains a typed login-form and accepts-html flag; the response
record remains nine fields with closed new view/route cases. Existing SQLite
schema is unchanged. This prototype source-schema change requires a fresh
Campfire dictionary; persisted old request schemas are not silently rewritten.
No Core/compiler/native ABI change was needed. These interpreter host operations
are not yet native Campfire lowering.

Next application milestone: authenticated DELETE /session with targeted durable
session revocation, encrypted session reset, and auth-cookie clearing, followed
by first-run account creation. Root landing, push-subscription cleanup and Cable
disconnection must remain visible acceptance items rather than being inferred
from a successful logout redirect.
## Final local gate

Ten fresh Release builds, nine executable suites and three policy verifiers pass
with package auditing enabled. Suite assertions: persistence 73, host integration
474, cookies 76, authentication storage 181, authentication integration 396,
browser 2,354, HTTP 302, sign-in 51, login-attempt store 4,122. The three policy
verifiers pass their attached, independent and reload checks. The final receipt
is `.agentlang/campfire-login-http-244/combined-01/receipt.json`.

Tests use synthetic fixtures. The trusted HTTP gate ran on the host because the
sandbox denies outbound loopback sockets. The first package restore attempt
could not reach NuGet audit data; the host restore completed with auditing on.
CI was not used. This is component correctness evidence, not application parity,
an agent efficacy experiment or a native throughput measurement.
## Preserved evidence

The [archive](evidence/244-campfire-public-login-http/evidence.zip) contains
146 members, including 75 source snapshots and preserved passing/failing logs,
receipts and redacted synthetic traces. Each member passed readback verification
against the [manifest](evidence/244-campfire-public-login-http/manifest.json).
Archive SHA-256:
`cee047f0f8551fb634d7e6f85fbcdd6373146a0d3a20ae12b253dc78b50179f9`.
Temporary databases, stores and executable artifacts are excluded.
