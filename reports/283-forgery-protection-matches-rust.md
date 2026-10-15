# 283 — Forgery protection matches the Rust reference: no CSRF tokens in pages

User decision (2026-10-15), after report 282 found that the Rust reference
emits no CSRF tokens: **Flow matches Rust.** Rails keeps its token scheme in the
benchmark, labeled as the one server that does token work.

## What the Rust reference does

`kit/src/ctx.rs` `verify_authenticity_token` implements Rails main's
`protect_from_forgery using: :header_only`:
- GET and HEAD pass.
- Writes must have a matching `Origin` and a `Sec-Fetch-Site` of
  `same-origin` or `same-site`. A missing header is allowed only when neither
  the request nor the app uses SSL, and a `null` origin is rejected.
- Pages carry no tokens: no `csrf-param`/`csrf-token` meta tags and no
  `authenticity_token` fields (`views/src/helpers/forms.rs`). So a page is the
  same bytes until what it shows changes.
- The session cookie is written only when the session changed
  (`kit/src/session.rs`).

## What changed in Flow

- **Writes were already protected only by headers.**
  `campfire.request.forgery-check-passed?` checks Origin and Sec-Fetch-Site,
  and no request path ever verified a token: `campfire.csrf.valid-token?` had
  no caller outside tests. The tokens were rendered and never checked, so the
  security posture is unchanged.
- **Views:** the layout's two meta tags, the composer's hidden token, and the
  eight boost forms' hidden tokens per message are gone. `PageChrome` loses its
  two token fields, and `campfire.views.rails-message` takes the message
  directly. The markup around them now matches Rust's page exactly.
- **Request path:** both copies of show-room (`http.flow` and
  `server/server.flow`) no longer create a secret, mask about 322 tokens per
  page, or set `new-csrf-token`. The room path drops the `random.read` effect.
- **Session cookie:** the server keeps `_campfire_session` raw and decrypts it
  only when a response changes the session (return-to, flash), as Rust loads
  it lazily. A room GET no longer decrypts it with AES-GCM, and a session
  without a CSRF secret is not given one. `mutate-session` keeps a
  `_csrf_token` that Rails wrote, unchanged.
- **Deleted:**
  - `csrf.flow` and `csrf-tests.flow`;
  - the `CsrfSecret`, `BoostTokens` and `TokenedMessage` types;
  - `tokened-message` (in both copies) and the server's `session-csrf`.

  The fragment template version moves to `flow-2`.
- **Kept, always none:** `CampfireHttpRequest.csrf-token` and
  `CampfireHttpResponse.new-csrf-token`. The Windows native host and the .NET
  hosts encode those record layouts. Removing them is scheduled together with
  those hosts (docs/PENDING-REVIEW.md "Scheduled removals").
- **App code** (non-test files under `applications/campfire`): Flow
  5,787 → 5,567 lines. C and F# are unchanged.

## Tests

Every changed expectation was made stricter, not loosened:

| Where | Before | Now |
| --- | --- | --- |
| Native room-read verifier (both profiles) | native vs interpreter equal with token values masked; every token unmasks to its HMAC | exactly equal, no masking; no `csrf`, no `authenticity_token` and no session secret in the page; 8 token-free boost forms per message. 266 assertions per profile |
| C standalone smoke | bytes equal, skipping 86 characters at each token | bytes exactly equal |
| Kestrel HTTP tests | meta, composer and 320 boost tokens unmask to the session's HMAC | no token markup; 320 token-free boost forms; no `_campfire_session` written; two consecutive GETs byte-identical. 475 assertions |
| `campfire.server.handle/room-page` | 2 Set-Cookies (a fresh CSRF session plus last_room) | exactly 1 (`last_room`); no token markup; the same request renders the same bytes |
| Views tests | boost form carries token `t8` | boost form has no token, and the page has no `csrf` or `authenticity_token`; composer markup checked |
| Server fragment tests | cached fragment equals a first render under a different secret | cached fragment equals a fresh render; the cached page equals the uncached page |
| Integration suite counts | 86 / 100 | 64 / 78: the 21 CSRF tests and 1 tokened-message test are gone |
| Flow server parity script | bodies compared with tokens masked; tokens unmasked with the session secret | bodies compared exactly; no token markup or session secret; first page == steady page; all of these count toward PASS |

**The native host profile check caught one mistake.** At first,
`campfire.http.room-read` still declared `random.read` after nothing it called
needed it. The closed profile requires an entry's declared and inferred effects
to match, so the native room-read gate failed with
`IR_NATIVE_HOST_PROFILE_MISMATCH`. The effects are exact now (`db.read`,
`db.write`). The host manifest also drops the deleted types. Both room-read
gates and the `campfire` gate were then rerun and pass (`rerun/`).

**Gates:** 25 at the working tree (`gates-20261015T113536Z`). 23 passed first
time. The two native room-read gates failed as above, were fixed, and pass on
the rerun.

**Native parity** (Flow server against the HTTP.sys native host, trusted O2):
PASS on Windows and on Linux, each with the fragment cache off and with 32 MB.
- All 14 cases match on status, headers, Set-Cookie and body.
- No token markup appears, and the first page equals the steady page.
- loadgen `--validate` has 0 invalid responses.
- With the cache on, 40 misses are followed by only hits (`parity2-win-*.txt`, `parity3-linux-*.txt`; the two `parity2-linux` attempts stopped on a reused WSL run name and are kept).

## Performance (quick interleaved check, Linux, 4 pinned vCPUs, 2 reps)

Same harness as 282 (`sweep-lifetime.sh`), with Flow built at this change.
Rust has its fragment cache on and its response cache off.

| Server | Cache | Conn | Req/s | CPU ms/req | p50 / p99 ms | Client util | PSS MB |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Rust | off | 16 | 1,497 | 2.54 | 10.2 / 22.3 | 0.04 | 91 |
| Rust | off | 64 | 1,465 | 2.59 | 42.0 / 82.2 | 0.04 | 94 |
| Flow | off | 16 | 3,090 | 1.34 | 3.9 / 15.1 | 0.09 | 18 |
| Flow | off | 64 | 3,054 | 1.35 | 19.0 / 51.4 | 0.08 | 18 |
| Rust | fragment 32 MB | 16 | 9,432 | 0.43 | 1.6 / 3.1 | 0.38 | 47 |
| Rust | fragment 32 MB | 64 | 8,900 | 0.45 | 6.8 / 13.7 | 0.33 | 51 |
| Flow | 32 MB | 16 | 3,333 | 1.22 | 4.2 / 14.7 | 0.09 | 18 |
| Flow | 32 MB | 64 | 3,363 | 1.22 | 17.7 / 45.9 | 0.09 | 18 |

Every cell is saturated, with 0 invalid responses.
- **Flow's uncached page** went from 2.06 to 1.34 ms per request. 282 and 283
  are different sessions, and drift between sessions is about 20%, but the
  masking work is gone.
- **The page is now byte-constant,** so the load generator's client CPU on Flow
  dropped from 0.54 to 0.09.
- **Flow's cache now saves only about 9%** (1.34 → 1.22 ms). The page left to
  cache is cheap to render. What remains per request is fetching data, signing
  avatars for every message, copying and re-validating each hit, and splicing
  the page. Rust's cached path does none of those.

  That is the next milestone, the per-worker store, planned in
  [docs/WORKER-STORE-PLAN.md](../docs/WORKER-STORE-PLAN.md).

**Fairness:** the pages are still not identical. Flow is about 413.8 KB on Linux
and Rust about 416.1 KB; Flow still lacks rich text and two boosts. No speed
ratio is claimed.

## Next

The per-worker store (docs/WORKER-STORE-PLAN.md):
- a configurable static section per worker (32 MB total for the benchmark,
  split between workers);
- the cache logic in Flow, with oldest-first eviction over a packed ring;
- the app choosing per call whether a value goes to the output buffer or onto
  the data stack;
- an in-order response writer;
- Campfire fetching only what the frame needs first, as Rust does.
