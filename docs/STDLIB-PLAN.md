# AgentLang Campfire pure-Flow standard library plan

Status: plan of record (user, 2026-10-14: "restructure first", "proceed with the stdlib"). Inventory from a read-only pass at commit f81e6fa; line counts are estimates by function group.


## 1. Current state (verified)

- Runtime values (src\AgentLang.Core\Core.fs 25-37): IntValue (int64), FloatValue, BoolValue, StringValue, UnitValue, ListValue, OptionValue, ResultValue, RecordValue (Map), EnumValue, NamedValue. No Bytes value, no u8/u32, no bit operations.
- LangType (Core.fs 13-22): TInt, TFloat, TBool, TString, TUnit, TList, TOption, TResult, TNamed, TVar. No map constructor and no recursive variant type in the core type list.
- JSON: System.Text.Json is used host-side only (serialization). No Flow-visible JSON value.
- Crypto, base64, HMAC, PBKDF2, AES-GCM: not in src\AgentLang.Core. Grep hits in src are serialization only, not byte-level crypto (not yet confirmed in OwningStackAot.fs or LlvmAot.fs beyond JSON-related terms).
- Host profile (applications\campfire\Native\room-read-host.json): tags 1-13 (tag 9 absent), with effects db.read, db.write, random.read.

## 2. Classification by file (S = stdlib primitive, F = Campfire logic to port to Flow, D = delete after port)

Line counts are estimates from function-group ranges, not exact sums.

### C (applications\campfire\Native), about 13,100 lines in the 10 files

| File | Lines | S | F | D |
|---|---|---|---|---|
| http\cookie_response_codec.c | 3206 | ~1040: LE 95-135, input 138-197, UTF-8/base64 210-350, JSON 351-899, base64 encode 2038-2199, crypto 2300-2387 | ~1630: Rails envelope 910-1283, digest/cookie parse 1312-1503, browser 1715-2037, session envelope 2388-2730, session JSON/finalize 2730-3140 | ~210: ABI/context 1504-1714 |
| room_read_pure.c | 2178 | ~595: LE 78-103, input 112-173, UTF-8/base64url 182-294, JSON 294-690 | ~1160: Rails 718-1062, digest 1091-1260, browser 1476-1800, avatar 1800-1910, CSRF 1911-2121 | ~150: context 1325-1476 |
| room_read_adapter.c | 2643 | ~645: profiled SQLite wrappers 35-120, layout 237-700, error/column helpers 758-965 (S-db) | ~980: query table 121-236, row loads 1029-1413, session/user/room/membership 1554-1953, messages 1979-2060 | ~580: transactions 1491-1546, PRAGMA/WAL 2060-2256, context 2307-2373, exports 2415-2643 |
| http\native_http_host.c | 2365 | ~45: UTF-16/UTF-8 helpers 479-523 | ~645: headers/cookies/query 846-1236, remote IP/browser/return URL 1236-1403, finalize cookies 2157-2249 | ~1670: profile/stage 347-444, pins 548-650, module load 650-846, send 1514-1673, worker 1713-1817, config 1817-1947, start 1979/2116 |
| http\browser_normalizer.c | 1548 | ~200: ASCII/Ruby whitespace 83-134 | ~1000: product/comment 153-389, browser/OS 599-998, version 1220, bot 1405-1440 | ~50: export 1484 |
| http\campfire_http_renderer.c | 548 | ~120: cursor reads 107-224 | ~270: row/response parse 224-334, contract check 334 | ~160: sink/emit 374-392, export 482 |
| http\cookie_unescape.c | 336 | ~336: percent and UTF-8 decode | 0 | 0 |
| http\cookie_expiry.c | 136 | ~136: calendar and date format | 0 | 0 (or D if Flow gets a time primitive) |
| http\request_encoder.c | 95 | ~30: writer helpers 7-49 | ~65: room request wire encoding 53 | 0 |
| http\cookie_wire.c | 28 | 0 | ~28: cookie value escape | 0 |
| Totals (approx.) | ~13,100 | ~3,150 | ~5,850 | ~2,800 |

Note: S + F + D is about 11,800. The remaining ~1,300 lines are headers, blank lines, and struct/arena plumbing, counted as D.

### F# (applications\campfire), about 3,665 lines (bin/obj excluded)

| File | Lines | Classification |
|---|---|---|
| Cookies\CookieService.fs | 627 | F: envelope, expiry, legacy envelope (~450). S: JSON parse, strict and urlsafe base64 (~150). Delete after port. |
| Browser\UserAgentNormalizer.fs | 582 | F: pure string logic. Port to Flow. |
| Authentication.Persistence\SqliteProvider.fs | 512 | Not yet classified in detail. Expected F (auth queries) plus S-db (connection layer). |
| Http\Transport.fs | 470 | D: Win32/HTTP.sys plumbing. |
| Host\CampfireHost.fs | 282 | D: marshalling and dispatch (~200). F: avatarToken (173), csrfFormToken (215), boost tokens. S: base64url, HMAC, random (197-211). |
| Authentication.Host\CampfireAuthenticationHost.fs | 333 | Not yet classified in detail. Expected D (wiring) with some F. |
| Persistence\SqliteProvider.fs | 337 | F: PageRoomsForUser (148), MembershipExists (183), LastPage (195), LastPageWithCreators (227), CreateMessage (272), about 250. S-db: open/prepare/bind/step, about 60. S: formatTimestamp, about 30. |
| Browser\RubyVersion.fs | 133 | F: Ruby-style version comparison. |
| Http\Application.fs | 98 | D. |
| Http\ResponseRenderer.fs | 91 | D or F (response shaping, after port). |
| Http\CookieHost.fs | 62 | D. |
| Http\LoginHost.fs | 57 | D. |
| Browser\BrowserHost.fs | 63 | D. |
| Authentication.Persistence\PasswordVerification.fs | 18 | S-crypto: bcrypt verify must be a host call. Flow has no bcrypt primitive. |

### Flow (applications\campfire), about 7,570 lines in 27 files

- Non-test files (about 3,000 lines): F. They stay and grow as the port target.
- *-tests.flow files (about 4,500 lines): kept as gates, not ported. Largest: http-tests.flow 1403, http-login-tests.flow 760, room-message-tests.flow 733, authentication-tests.flow 679.

### Test projects mapped to gates (not run)

- tests\AgentLang.Campfire.NativeRoomRead.Tests and NativeHttp.Tests: C host. Gates: room-read-trusted-off and room-read-diagnostic-off via scripts\Verify-CampfireNativeRoomRead.ps1.
- tests\AgentLang.Campfire.Cookies.Tests and Browser.Tests: F# logic that moves to Flow.
- tests\AgentLang.Campfire.Persistence.Tests and Authentication.Persistence.Tests: SQLite logic.
- tests\AgentLang.CampfireViews.Tests and the Flow test projects: gated by .agentlang\csrf-271\Run-Gates.ps1 (campfire-views, campfire, flow-tests, flow-runtime-tests).

## 3. Standard-library surface (Flow names)

Generic, in `src/` (interpreter and owning runtime), with no Campfire names.
Slice 1 (report 272) and slice 2 (report 274) are done (marked *).

1. `Bytes`* (pure): `bytes.from-utf8`, `bytes.to-utf8 -> Option<String>`,
   `bytes.length`, `bytes.concat`, `bytes.slice -> Option<Bytes>`,
   `bytes.xor -> Option<Bytes>`, `bytes.equal-constant-time?`, `hex.encode`.
2. Base64* (pure): `base64.encode` / `base64.decode -> Option<Bytes>`
   (standard, padded); `base64.url-encode` / `base64.url-decode` (URL-safe,
   unpadded output, either input).
3. Crypto* (pure): `crypto.sha256`, `crypto.hmac-sha256`,
   `crypto.pbkdf2-sha256 -> Option<Bytes>`; slice 2: `crypto.hmac-sha1(key, message) -> Bytes`,
   `crypto.aes-256-gcm-seal(key, nonce, plaintext, aad) -> Option<Bytes>` (ciphertext followed by
   the 16-byte tag; builtins cannot return records, so split it with `bytes.slice`) and
   `crypto.aes-256-gcm-open(key, nonce, ciphertext, tag, aad) -> Option<Bytes>`. None for a key
   other than 32 bytes, a nonce other than 12 or a tag other than 16, and for a failed tag check.
   AES runs on the OS library: BCrypt (CNG, `BCRYPT_AES_GCM_ALG_HANDLE`) on Windows, linked only
   into modules that call it (their IR defines `al_owning_os_crypto_table`); OpenSSL libcrypto (EVP,
   weak references; link `-lcrypto`) on Linux; .NET `AesGcm` in the interpreter. PBKDF2-SHA1 is
   not needed: Rails 7 derives both cookie keys with PBKDF2-HMAC-SHA256.
4. Randomness*: `crypto.random-bytes(count)`, one call fills `count` bytes.
   Effect: `random.read`.
5. Per-worker configuration: values derived once per worker at startup, such
   as PBKDF2 keys and prepared statements, passed to the Flow entry as an
   immutable context. This is configuration, not response caching: Rails and
   Rust also derive keys once and reuse prepared statements.
6. Database: a statement prepared once per worker; `db.query<Row>(statement,
   parameters) -> Result<List<Row>, SqlError>`, one host call per query
   returning typed rows. Effects: `db.read` / `db.write`.
7. JSON*, host-parsed, as the opaque type `Json` (the exact text of one
   validated value; Bytes layout natively): `json.parse(String) -> Option<Json>`
   (strict RFC 8259, containers nest at most 32 deep, unpaired surrogate escapes
   rejected); `json.field(Json, String) -> Option<Json>` (the last duplicate key
   wins, as in Ruby); `json.index(Json, Int) -> Option<Json>` (array element or
   object member value); `json.key-at(Json, Int) -> Option<String>`;
   `json.count -> Option<Int>`; `json.text -> Option<String>`; `json.int ->
   Option<Int>` (integer literals within Int64); `json.bool -> Option<Bool>`;
   `json.null? -> Bool`; `json.encode(Json) -> String` (the text). Encoder:
   `json.quote(String) -> String`, escaped as Rails writes the cookie envelope
   (quote, backslash, b f n r t, other controls and `< > &` as lower-case
   u00xx escapes; `/` and non-ASCII raw). Objects are composed in Flow with
   `string.concat`.
8. Cookies*: `cookie.find(header, name) -> Option<String>` (first match, as
   Rack; parts trimmed of Unicode whitespace) and `cookie.count(header, name) ->
   Int` (to reject duplicates); `cookie.escape` (Rack
   `URI.encode_www_form_component` over UTF-8, upper-case hex) and
   `cookie.unescape` (`+` is space; a malformed `%` keeps the whole value;
   invalid UTF-8 becomes U+FFFD per maximal subpart);
   `cookie.set-cookie(name, value, path, expires, same-site, secure, http-only)
   -> Option<String>` (`name=escaped; Path=; SameSite=; Expires=; Secure;
   HttpOnly`, empty attributes left out; None for a non-token name or an
   attribute with `;`, controls or non-ASCII). Rails signed and encrypted
   cookies are written in Flow from these and the crypto primitives
   (tests/AgentLang.Stdlib.Tests/stdlib2.flow has both directions, checked
   against cookies made with OpenSSL through Node).
9. Time* over Unix microseconds (the .NET DateTime range, years 1-9999):
   `time.rfc1123(Int) -> Option<String>` (cookie Expires),
   `time.iso8601-ms(Int) -> Option<String>` (Rails `exp`,
   `2046-10-14T12:00:00.000Z`), `time.parse-iso8601(String) -> Option<Int>` (the
   forms Rails' `exp` parser accepts; digits below a microsecond round up) and
   `time.add-years(Int, Int) -> Option<Int>` (AddYears: Feb 29 becomes Feb 28).
   Still missing: a clock that returns Unix microseconds (`clock.now` returns
   text and has no native implementation).
10. Secrets: key material is never rendered in logs or errors. Native buffers
   holding keys are zeroed (a scrub policy for arena regions is still to be
   decided).

Per-worker ownership throughout: no shared mutable state, and no locks without
the user's approval.

## 4. Language gaps (top 3)

1. No Bytes type. Values are only Int, Float, Bool, String, Unit, List, Option, Result, Record, Enum, Named (Core.fs 25-37). Keys, nonces, ciphertext, and base64 need a real Bytes value with concat, slice, and xor.
2. No fixed-width integers and no bit operations. Int is int64 only. SHA, AES, and base64 need u8/u32 plus shifts and xor, or they must stay host calls. Recommended: keep the crypto core in the host and expose Bytes-level calls.
3. No typed-JSON story. No recursive variant type appears in LangType. JSON cannot be a Flow value tree. Options: (a) host-parsed JSON with typed accessors (recommended); (b) typed decode into records per shape; (c) recursive variant types (largest effort).

## 5. Ordered migration plan

Step 1: Pure logic (only String ops needed).
- Port browser version compare and UA normalization (RubyVersion.fs, UserAgentNormalizer.fs, browser_normalizer.c, host tag 6) to Flow.
- Delete: browser_normalizer.c (about 1,548), RubyVersion.fs (133), UserAgentNormalizer.fs (582), tag 6 in room-read-host.json.
- Tests: Browser.Tests, browser-tests.flow.
- Done in 272: applications/campfire/user-agent.flow (lean, loop-based; only the paths that can change campfire.browser.supported?), parity corpus user-agent-corpus-tests.flow (canonicalized), tag 6 retired. The first fold-based port (with text-scan.flow) was replaced the same day.
- Expected remaining: C about 11,550; F# about 2,950.

Step 2: CSRF and avatar derivation.
- Needs Bytes, base64url, HMAC-SHA256, batched random_bytes.
- Port csrf-session-secret, csrf-form-token, csrf-boost-tokens (tags 11-13), and avatarToken (CampfireHost.fs 173) to Flow.
- Delete: csrf_* and avatar functions in room_read_pure.c (about 320), CampfireHost.fs token cases (about 100).
- Tests: http-tests.flow CSRF cases, room-message-tests.flow.
- Expected remaining: C about 11,200; F# about 2,850.

Step 3: Rails cookie envelope and session format.
- Needs Bytes, host-parsed JSON, HMAC-SHA1, AES-GCM, PBKDF2 (per worker), time.read.
- Port envelope, expiry, session schema, and session finalize (cookie_response_codec.c 910-1283, 1312-1503, 2388-3140; room_read_pure.c 718-1260; CookieService.fs).
- Delete: most of cookie_response_codec.c (keep crypto host wrappers), room_read_pure.c Rails section, CookieService.fs, cookie_unescape.c, cookie_expiry.c, cookie_wire.c, request_encoder.c.
- Tests: Cookies.Tests, http-login-tests.flow, authentication-tests.flow, sign-in-tests.flow.
- Expected remaining: C about 5,200; F# about 2,100.

Step 4: Room queries and rows.
- Needs db_prepare once per worker and db_query returning typed rows (one call per query, 1 to 9 us per host call).
- Port query shaping, row mapping, membership rules, bot-key split, and message page logic (room_read_adapter.c query table and rows; Persistence\SqliteProvider.fs; Authentication.Persistence\SqliteProvider.fs).
- Delete: row loaders and query code in room_read_adapter.c (about 980), Persistence\SqliteProvider.fs (about 250 F#).
- Keep as host (S-db): SQLite open/prepare/bind/step and PRAGMA/WAL verification.
- Tests: Persistence.Tests, Authentication.Persistence.Tests, room-message-tests.flow, http-room-read-tests.flow.
- Expected remaining: C about 2,300; F# about 1,100.

Step 5: HTTP plumbing and host removal.
- Once the Flow runtime HTTP host covers request parsing, cookie header building, and response send, delete the Campfire native HTTP host.
- Delete: native_http_host.c (about 2,365), campfire_http_renderer.c (about 548), remainders of room_read_pure.c, room_read_adapter.c, cookie_response_codec.c, and the .NET hosts (Transport.fs, Application.fs, ResponseRenderer.fs, CookieHost.fs, LoginHost.fs, BrowserHost.fs, CampfireHost.fs, CampfireAuthenticationHost.fs).
- Keep: minimal SQLite, PBKDF2, and BCrypt host shim, plus PasswordVerification.fs.
- Tests: all NativeRoomRead.Tests, NativeHttp.Tests, Integration.Tests, and the full Run-Gates.ps1 (run by the user; not run by this research).
- Expected end state: C about 800 to 1,200 lines (host shim only). F# about 300 lines (PasswordVerification plus host wiring). Flow grows to about 4,000 to 5,000 non-test lines.

## 5a. What stays native (rule from 2026-10-14)

Long-term rule: C and F# keep only what is truly needed — parsing and
lowering to LLVM, the minimal native runtime, and the OS boundary (sockets,
SQLite, randomness, OS crypto). Everything else moves into Flow's stdlib,
written in Flow where the language allows it.

Slice 1 (report 272) landed as native primitives so the ports can start now:

| Primitive | Long-term home | Why |
| --- | --- | --- |
| Bytes kind, bytes.length / concat / slice / xor / equal-constant-time? | native | value representation and constant-time compare |
| crypto.sha256 / hmac-sha256 / pbkdf2-sha256, crypto.random-bytes | native | OS library since 275: BCryptHash / BCryptDeriveKeyPBKDF2 (pseudo-handles) on Windows, per-worker EVP_MD / EVP_MAC contexts (279; PKCS5_PBKDF2_HMAC) on Linux; random: BCryptGenRandom / getrandom |
| bytes.from-utf8 / to-utf8 | native | UTF-8 validity is a runtime invariant |
| base64.*, hex.encode | Flow later | needs byte access, a byte builder and bit operations in Flow |
| string.split / index-of / slice / byte-at / to-lower-ascii, int.parse | Flow later | expressible once Flow has loops or efficient recursion over bytes |
| crypto.hmac-sha1, crypto.aes-256-gcm-seal / open (274) | native | AES on the OS library (BCrypt / libcrypto); HMAC-SHA1 on BCryptHash / HMAC() too |
| Json type, json.* (274) | native | no recursive types; strict parsing is boundary code |
| cookie.*, time.* (274) | Flow later | byte loops and calendar arithmetic, once Flow has a byte builder |

Flow now has loops and `var` locals (272; CURRENT-SYNTAX "Loops and var"), with
native back edges and per-iteration arena reclamation. The byte-scanning
primitives can now be written in Flow; `stdlib.count-byte` and
`stdlib.first-byte-index` in `tests/AgentLang.Stdlib.Tests/stdlib.flow` show
the pattern. base64 and hex still need a byte builder and bit operations.

Moving a "Flow later" primitive needs the building blocks it uses, the same
known-answer tests (tests/AgentLang.Stdlib.Tests), and a quick-bench check
that the Flow version does not cost noticeable throughput.

## 6. Open items

- Authentication.Persistence\SqliteProvider.fs (512) and Authentication.Host\CampfireAuthenticationHost.fs (333) are not classified in detail.
- Byte-level structures in src\AgentLang.Llvm\native\owning_bank.h and owning_stack_runtime.c were not verified as a Bytes type. Core has none.
- Line counts are estimates from function-group ranges. Exact counts need a per-group line count pass.
