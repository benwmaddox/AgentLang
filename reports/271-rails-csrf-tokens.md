# 271 — Rails CSRF tokens on the room page

Status: Campfire app milestone (views plan slice 3). The native room page now
renders Rails' masked authenticity tokens for its meta tag, the composer form
and all 320 quick-boost forms. They are computed per request from the session's
secret, exactly as Rails computes them. The page is 460,238 bytes, within 0.8%
of Rails' 463,753.

## User direction

- CSRF model: "Rails tokens" (over Rust's header-only protection, or both).
- "Don't do campfire implementation in .net." .NET stays for the language
  toolchain (parsing, lowering, getting to LLVM). From this report on, new
  Campfire host operations are native only, and Flow-level tests use
  host-operation overrides. The .NET CSRF operations below were written before
  that decision. They stay as existing code, and no further Campfire
  behaviour goes into .NET.
- "Have a sub agent do a review and tell me how close we are to apples to
  apples." See "Fairness" below.

## The algorithm, pinned against Rails

The Rust port's committed Rails vectors (`vectors/rails_compat.json`, synthetic
secrets) pin the algorithm:
- The session's `_csrf_token` is URL-safe Base64 of 32 random bytes.
- A form's token is `pad ‖ (pad XOR HMAC-SHA256(secret, "<path>#<method>"))`,
  with a fresh 32-byte pad, URL-safe Base64, no padding (86 characters).
- The page's meta token uses the identifier `!real_csrf_token`.

Checked against the vectors: the global token matches, 3/3 masked global tokens
unmask, and 8/8 form tokens match their HMAC and unmask. The private Rails
capture was taken under a session whose cookie the harness did not keep, so it
cannot serve as the oracle here.

## How it is built

- **Flow.**
  - `CampfireHttpRequest` gains `csrf-token: Option<String>`, the session's
    secret. `CampfireHttpResponse` gains `new-csrf-token: Option<String>`, a
    secret Flow generated for a session that had none, which the host stores.
  - Three host operations, all `random.read`:
    - `campfire.csrf.session-secret(candidate)` returns the session's secret
      if it decodes to 32 bytes, otherwise a fresh one marked `fresh`;
    - `campfire.csrf.form-token(secret, identifier)` computes the meta and
      composer tokens;
    - `campfire.csrf.boost-tokens(secret, message-id)` computes a message's
      eight quick-boost tokens: one HMAC, eight pads from one RNG call.
  - `show-room` maps the messages through `campfire.csrf.tokened-message` (an
    effectful `map` with the secret as context). The pure views then render
    `TokenedMessage` values. Views stay `effects none`.
- **Native host.**
  - The pure provider implements tags 11–13 with CNG.
  - The HTTP host decrypts the Rails session (AES-256-GCM) on each request to
    read `_csrf_token`, as Rails does. A fresh secret is written back through
    `al_campfire_cookie_response_finalize_session_v2`.
  - The request encoder, response renderer (new field, room view only) and
    cookie codec were extended. Pinned helper hashes, the host hash and the
    manifest effect/operation sets were re-pinned.
- **Verification.**
  - The room-read verifier gives requests a fixed valid secret. It compares
    native output with the interpreter with token values masked, and checks
    that every token unmasks to its per-form HMAC (252 assertions per
    profile). The standalone C smoke compares with tokens masked.
  - The HTTP suite checks that a session without a secret receives one and
    that all 320 boost tokens and the meta token unmask (473 assertions).
  - On the page the native host actually served to the benchmark, checked
    against the benchmark session's real secret: meta ✓, composer ✓, 320/320
    boost forms ✓, 320 distinct masks.

## Performance (quick benchmark)

The quick bench now runs 4 lifetimes and takes a true median, matching report
259. The earlier form took the lower middle of 2 runs, which biased req/s down
and CPU/request up.

| Server | Page bytes | Req/s (range) | CPU / req | p99 | Peak working set | Peak private |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| **AgentLang 271 (CSRF tokens)** | 460,238 | **2,749** (2,739–2,763) | **1.32 ms** | 6.2 ms | 23.5 MB | 47.2 MB |
| AgentLang 270 (empty tokens) | 432,546 | 4,004 | 0.95 ms | 5.8 ms | 22.2 MB | 47.2 MB |
| Rust, identity (recorded, 259) | 416,127 | 1,322 | 2.93 ms | 25.9 ms | — | — |
| Rails, identity (recorded, 259) | 463,753 | 19 | 176 ms | 1,108 ms | — | — |

- Rails' per-request CSRF work costs about 0.37 ms per page here: 321 HMAC and
  mask operations, and 40 host calls.
- Batching the RNG gained 1.7%, so the cost is mostly per-call setup (an HMAC
  key schedule and output validation per message). Named for later.
- Rust skips this work entirely, by design (header-only forgery protection).

## Fairness (independent review)

The quick bench matches report 259's native protocol:
- same snapshot, session, room and 40-message window;
- contract validation, gzip off;
- 2 s warmup at 4 clients, 8 s timed at 16 clients;
- caches disabled in all three servers.

It is still **not** an apples-to-apples ratio:
1. The page is not yet equivalent. Rich text is missing (the bodies are escaped
   text, while Rails runs ActionText rendering and sanitization), as are 2
   boosts, the logo version link, loaded-at and the signed stream name: 30 of
   4,896 Rails nodes.
2. The Rails and Rust numbers are recorded from report 259, not rerun in the
   same session.
3. Placement differs: native HTTP.sys on 4 physical cores versus Docker on
   WSL2 on 4 vCPUs. Report 259 itself says the CPU budgets are not equivalent.

Checklist toward a fair claim:
1. Finish rich text and boosts.
2. Rerun all three servers in one session under the report-259 protocol with
   matched placement.
3. Only then state ratios.

## Language note

`items.map(context, word)` rejects a field access (`secret.value`) as its
context argument (`FLOW_CALLBACK_ARGUMENT_ARITY`); binding a local first
works. A follow-up should accept any expression there.

## Tests

- **Gates:** all 21 pass (run `gates-20261014T130410Z`, about 41 min), on
  frozen sources.
- **Room-read verifier:** 252 assertions in both profiles. HTTP suite 473,
  integration 583, CampfireViews 15.

| Gate | Result | Seconds |
| --- | --- | ---: |
| llvm-full-suite | pass | 394 |
| arena-lifetime | pass | 0 |
| owning-compact | pass | 49 |
| owning-html | pass | 66 |
| owning-listbuild | pass | 25 |
| owning-utf8 | pass | 10 |
| owning-templates | pass | 17 |
| campfire-views | pass | 17 |
| acceptance | pass | 11 |
| flow-tests | pass | 7 |
| flow-runtime-tests | pass | 46 |
| ir-interpreter | pass | 5 |
| campfire | pass | 417 |
| native-value-stack | pass | 330 |
| owning-mailbox | pass | 71 |
| ir-parity | pass | 13 |
| native-conformance | pass | 352 |
| owning-lists | pass | 11 |
| owning-list-ops | pass | 34 |
| room-read-trusted-off | pass | 273 |
| room-read-diagnostic-off | pass | 341 |

## Next, first: two syntax changes (user, 2026-10-14)

These come before the stdlib work, so new stdlib and app code uses them.

1. **Enum cases without `case`.** One case name per line, or separated by
   `;` like record fields:

   ```flow
   enum HttpView {
       empty
       room
       append-message
   }
   ```

   - The parser accepts bare cases, and the formatter writes them.
   - Existing sources are migrated. The old `case` spelling stays readable for
     a while, like the old `::` spelling did.
   - `CURRENT-SYNTAX.md` is updated.
2. **`expect` test syntax (decided by the user).** Replace the trailing
   `=> literal` / `=> value <expression>` with `expect` statements:

   ```flow
   test campfire.http.unauthenticated/redirect {
       let response = campfire.http.unauthenticated()
       expect response.status == 302
       expect response.view == HttpView.redirect-root()
       expect effects { db.read: 0, db.write: 0, clock.read: 0, random.read: 0 }
   }
   ```

   - `expect <Bool expression>`; a test may hold several.
   - When the condition is `a == b`, a failure reports both sides' values, so
     diagnostics stay as good as the old expected/actual output.
   - `expect effects { … }` keeps exact effect-count assertions.
   - `==` is structural equality on typed values, as `=>` compared today.
   - The old `=>` forms stay readable during migration. All tests and docs
     (`CURRENT-SYNTAX.md`, authoring help) move to `expect`.

## Next: restructure before more page slices

The user expected the Campfire app to be Flow only. At 271 it holds about
7,600 lines of Flow beside about 13,800 lines of app C and 1,500+ lines of app
F#. They chose to restructure first:

1. **Inventory.** Classify every app C and F# file as a generic stdlib
   primitive, a Flow port, or a deletion. Report "app C / app F# lines
   remaining" in every milestone.
2. **A generic standard library in the language** (`src/`, interpreter and
   owning runtime, no Campfire names): `Bytes`, crypto (HMAC-SHA256, AES-GCM,
   PBKDF2, random bytes), Base64, a coarse database API (one call per query,
   typed rows), cookies and JSON, and an HTTP host that calls a Flow entry word.
   The surface goes to the user for sign-off before code.
3. **Port Campfire's logic to Flow,** one area at a time, keeping the page
   working: CSRF and avatar signing first, then cookies and session, then
   database queries, then HTTP routing.
4. **Delete the .NET Campfire host** (including the CSRF operations added in
   this report) once the interpreter stdlib runs the app and replacement tests
   exist.

The page from this report is the golden copy for every step: a byte comparison
with tokens masked, 320/320 CSRF tokens valid, both room-read profiles, and the
quick bench. Throughput is expected to drop as Flow takes over work the C did.
That is the honest cost, and it is recorded, not won back by moving app logic
into C. Rich text, boosts and the same-session report-259 rerun resume after
the restructure.
