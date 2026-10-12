# 243 — Campfire sign-in foundation

Status: validated locally; all 20 combined Release checks passed.

Campfire now has a language-authored `campfire.authentication.sign-in` function
that composes fixed credential verification and session creation. Invalid
credentials return ordinary absence; database/provider errors propagate; only
successful authentication can reach session creation. New Campfire dictionaries
install and qualify the function as a discoverable library. HTTP sign-in routing
is still the next application step, and the complete migration remains active.

The trusted adapter queries active users by exact email, releases SQLite before
bcrypt, and returns only the existing safe user projection. Password digests do
not enter language values. BCrypt.Net-Next 4.2.0 supplies normal bcrypt verification;
no bcrypt implementation or hashing scheme is invented here. Its published
package and release are recorded with the restored SHA-512 dependency pin.

Session issuance uses cryptographic, unbiased 24-character base58 tokens and
an atomic insert into the existing sessions schema. Explicit UTC time is
truncated to microseconds and used for all three timestamps. Token-generation
failures, malformed generated tokens, SQL constraints and rollback have closed
outcomes. Neither credential verification nor failure inserts a session.

Focused validation passed 51 runtime assertions / 14 scoped cases and 181
storage assertions / 15 groups. The scoped library tests cover success, absent
credentials, all DbError outcomes, valid roles and request metadata options.
They construct no effect provider and leave production SQLite unchanged. Real
invocation, capability denial before providers, library reload, typed adapter
boundaries and restoration of the newly issued token are checked separately.
An exclusive SQLite writer can commit during password verification, proving
that the lookup reader/connection has already been released.

The packaged independent Rails fixture contains three digests and eight
verification pairs from the pinned reference: ordinary text, UTF-8, and the
72-byte password boundary. It does not independently cover a multibyte character
split at byte 72. Fixture provenance and license accompany the copied data.

Review found that NULL/empty digests could skip bcrypt while unknown accounts
performed dummy work. Passwordless accounts now perform the same cost-12 dummy
verification and still return absence, regardless of the verifier's result.
This deliberately differs from the pinned Rust fast rejection for absent
digests; it does not establish uniform timing for every possible stored hash.

Build/test development failures are preserved, including F# harness annotations,
fixture packaging, and a fixture INSERT column-order defect. Expected behavior
was not weakened to make those attempts pass. The original 102 storage
assertions remain in the expanded passing suite.

Remaining application work: public GET /session/new and POST /session,
request-order/rate-limit policy, compatible sign-in rendering, encrypted
return-to consumption and signed-cookie finalization. Existing data/assets,
remaining routes, live protocols, deployment and native arena/mailbox execution
remain required for the complete Campfire migration. No route is marked
validated by this foundation alone.

Dependency references: [NuGet package](https://www.nuget.org/packages/BCrypt.Net-Next/4.2.0),
[upstream release](https://github.com/BcryptNet/bcrypt.net/releases/tag/v4.2.0).

## Final local regression

Nine clean builds, eight executable suites and three policy verifiers passed.
The suites reported: persistence 73, host integration 474, cookies 76,
authentication storage 181, restoration integration 396 across 43 attached cases,
browser 2,354, HTTP 150, and sign-in 51 across 14 scoped cases.
The new startup check proves the sign-in function is discoverable as a library.
The final receipt is `.agentlang/campfire-login-243/combined-02/receipt.json`.
No Core/compiler or native ABI change was needed for this milestone.

NuGet restore auditing and a direct-plus-transitive vulnerability query completed
without a reported vulnerable package. This is dependency-feed evidence, not a
security guarantee. The sandbox could not reach NuGet audit data, so restoration
ran on the host with auditing enabled. Trusted regression tests likewise used
the host for the ephemeral loopback HTTP listener, with synthetic fixtures.
CI was not used.

## Preserved evidence

The [archive](evidence/243-campfire-sign-in-foundation/evidence.zip) contains
143 members, including 64 source snapshots, logs, receipts and synthetic traces.
Every member passed readback verification against the
[manifest](evidence/243-campfire-sign-in-foundation/manifest.json). Archive SHA-256:
`6e89a5c35c288aacf5521073297ad69d044fff90784f9a019048499f8efd110f`.
Temporary databases and executable artifacts are excluded.
