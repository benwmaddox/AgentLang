# 270 — The room page renders Rails' message partials

Status: Campfire app and language milestone (views plan slice 2). The native
room page now renders every message with Campfire's own message partial,
written in Flow. That needed:
- one new host operation, returning messages with their creators;
- two language additions, `string.replace` and `map` with a context;
- one trusted-runtime cut (270a).

The page is now 432,546 bytes (Rails: 463,753; Rust: 416,127). It is missing
30 of the 4,896 Rails nodes; the Rust port misses 325.

## User direction

- "We need apples to apples to really compare."
- "Feel free to use subagents to work in parallel."
- Map context: the user chose `items.map(context, word)` over closures or an
  app-level workaround.
- "For html, I think if you pass in an html partial, it can assume it doesn't
  have to be escaped." That is already the rule: `Html` and `List<Html>` holes
  are inserted as-is, and only `String` holes are escaped. Message bodies stay
  escaped text until a sanitizer exists. The stored ActionText HTML is user
  content, and Rails sanitizes it first.

## Language

- **`string.replace(text, old, new)`.** Replaces every non-overlapping
  occurrence, left to right, in ordinal UTF-8 bytes. An empty `old` leaves the
  text unchanged.
  - The interpreter and the owning backend both implement it, with a two-pass
    plan/write in the C runtime.
  - Tests: owning-utf8, 222 assertions, native equal to the interpreter at
    O0 and O2.
  - Campfire uses it to turn stored `YYYY-MM-DD HH:MM:SS` into ISO times and
    the avatar `v=` stamp.
- **`items.map(context, word)`.**
  - The callback takes `(Context, Item)`.
  - The context is evaluated once and borrowed by every call; callbacks still
    cannot capture locals.
  - New IR operation `ListMapWithContext`, implemented in the verifier,
    interpreter, formatting, arena lifetime and both backends (the old LLVM
    backend rejects it).
  - `filter` and `each` with a context raise `FLOW_CALLBACK_CONTEXT_MAP_ONLY`.
  - Two native bugs were found by the Campfire page and fixed: the item and
    context slots were swapped in validation and in the element walk, and the
    source binding did not count the new operation as an authored call.
  - Native tests in owning-list-ops (226 assertions, both profiles, O0 and O2):
    Int, record, large-String and List contexts, an empty list, nesting, and
    240 items.
  - A 40-item record-context map with an Html template takes 34 µs (trusted
    O2).

## Host: messages with creators (tag 10)

- **`campfire.messages.last-page-with-creators`**
  (`RoomId → Result<List<MessageWithCreator>, DbError>`) replaces tag 9. Tag 9
  is retired from the native manifest and its number is not reused. The .NET
  host keeps `last-page` for its other routes.
- **`MessageWithCreator`** puts its fixed-size fields first:
  - 5 Int fields: id, room, creator, created and updated epoch milliseconds;
  - 6 String fields: client id, created-at, creator name, bio and
    updated-at, avatar token;
  - then `Option` body.

  Trusted builds therefore locate every field inline.
- **One query.** The newest 40 are cut first (`ROW_NUMBER()` over
  `created_at DESC`, ids only). Joins, `strftime` and the rich-text body then
  run for those rows only and come back oldest first. The adapter writes the
  page in one pass.
  - The first version ran a joined query twice (measure, then write) over every
    row of the room: 545 µs per GET in the stage profile.
- **Avatar tokens.** Rails `user.signed_id(purpose: :avatar)`:
  - The pure provider derives the PBKDF2 key once per worker. In .NET, the
    providers now take `secret_key_base`.
  - Native and .NET use the same recipe.
  - All 4 distinct avatar URLs on the page equal the Rails capture's,
    checked by equality only.
- **Epoch milliseconds** equal Rails' `data-message-timestamp` for all 40
  messages.

## The message partial

- **`applications/campfire/message.flow`.** `campfire.views.rails-message(context,
  message)` is generated from Campfire's `messages/_message` markup: actions,
  presentation and the boosts frame, about 9.9 KB of template. Every per-message
  and page value is a hole.
  - It renders the title, avatar URL, ISO times and permalinks in Flow.
  - Boosts (2 on this page) and rich text are later slices. CSRF tokens are
    empty until slice 3.
- **Byte check.** With CSRF tokens, boosts, body text and the deployment base URL
  masked, 39 of 40 native message partials are byte-identical to Rails'. The
  40th differs only where the comparison script cuts off the last message, at
  the layout tail.
  - Bodies match as-is for the 8 plain-text messages.
  - The 32 rich-text bodies differ because they are escaped, which is the
    rich-text slice.

## Performance

### Quick benchmark

| Server | Page bytes | Req/s | CPU / req | p99 | Peak working set | Peak private |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| **AgentLang 270** | 432,546 | **4,004** | **0.95 ms** | 5.8 ms | 22.2 MB | 47.2 MB |
| AgentLang 270, first single-pass run | 432,546 | 3,738 | 0.96 ms | 5.8 ms | 22.3 MB | 47.3 MB |
| AgentLang 270, two-pass joined query | 432,546 | 1,041 | 3.54 ms | 18.5 ms | 21.9 MB | 46.9 MB |
| AgentLang 270a (43 KB page) | 42,813 | 9,447 | 0.39 ms | 1.9 ms | 12.4 MB | 46.2 MB |
| Rust, identity (recorded) | 416,127 | 1,322 | 2.93 ms | 25.9 ms | — | — |
| Rails, identity (recorded) | 463,753 | 19 | 176 ms | 1,108 ms | — | — |

- On a page of about the same size, the native host serves 3.0× Rust's
  recorded throughput at about a third of its CPU per request (0.95 against
  2.93 ms).
- That is still not a claimed ratio. The pages differ by 30 nodes (rich text,
  2 boosts, CSRF tokens, logo version, loaded-at and stream name). The Rust
  and Rails records also come from report 259's Docker/WSL placement.

### Stage profile

Run `p270b`, against the first two-pass query version, on the profiling
harness's synthetic fixture (413 KB page). That fixture gained the `users.bio`
column Rails has. Per GET in the Off arms (2,670 req/s, 3.65 cores):

| Stage | µs |
| --- | ---: |
| Language execution | ~1,120 |
| ↳ last page with creators (Detailed): 89 SQLite steps at 6.2 µs | 545 |
| HTTP.sys send (432 KB) | ~185 |
| Response rendering (body copy) | ~105 |
| Scrub | ~24 |

The single-pass query is the fix this profile pointed at.

### 270a: trusted re-checks

Trusted builds no longer:
- call `check_initialized` in descriptor checks (only the diagnostic runtime
  tracks initialized bytes);
- re-walk the static type graph on every measure, locate and compare.

Results: the Rails-sized partial ×40 went from 446 to 435 µs, and the quick
bench (43 KB page) from 0.41 to 0.39 ms CPU per request.

**Correction found by the gates.** The type-graph skip first applied to every
value. The owning-mailbox gate showed that values admitted from a host or
caller (external views) depend on the full walk: a malformed sum tag and an
invalid inactive `Result` alternative were no longer rejected cleanly. Trusted
builds now skip the walk only for values that verified code built, and keep it
for external input.

## Parity

`compare_html_structure.py --mask-deployment` against the Rails capture:
- **AgentLang 270:** 30 of 4,896 Rails nodes missing (268: 4,580).
- **Rust port:** 325 missing, all CSRF.

The remaining gaps are rich-text paragraphs, 2 boosts, the logo version link,
loaded-at and the signed stream name.

## Tests

- **Gates:** all 21 pass (run `gates-20261014T092953Z`, about 32 min), on frozen
  sources.
- **First run (`gates-20261014T084702Z`):** two failures.
  - `room-read-diagnostic-off`: D: ran out of space ("no space on device" from
    clang).
  - `owning-mailbox`: the 270a type-graph correction above.

  95 old room-read run folders were pruned with the user's approval, the
  runtime fix was committed, and every gate was rerun.
- **Room-read verifier:** 238 assertions in both profiles, rendering the full
  Rails partials.
- **Campfire suites:** HTTP 468 assertions, integration 583. List callbacks:
  226 (owning-list-ops). UTF-8: 222.

| Gate | Result | Seconds |
| --- | --- | ---: |
| llvm-full-suite | pass | 294 |
| arena-lifetime | pass | 0 |
| owning-compact | pass | 35 |
| owning-html | pass | 41 |
| owning-listbuild | pass | 23 |
| owning-utf8 | pass | 10 |
| owning-templates | pass | 16 |
| campfire-views | pass | 16 |
| acceptance | pass | 9 |
| flow-tests | pass | 7 |
| flow-runtime-tests | pass | 31 |
| ir-interpreter | pass | 4 |
| campfire | pass | 339 |
| native-value-stack | pass | 327 |
| owning-mailbox | pass | 68 |
| ir-parity | pass | 13 |
| native-conformance | pass | 285 |
| owning-lists | pass | 11 |
| owning-list-ops | pass | 33 |
| room-read-trusted-off | pass | 189 |
| room-read-diagnostic-off | pass | 195 |

## Next

1. **Slice 3: CSRF.** Masked per-form authenticity tokens and the page's CSRF
   meta tag. Rust is missing exactly these 325 nodes.
2. **Rich text.** Sanitize ActionText HTML to Rails' allow list, then return
   it as `Html` (no escaping, no `html.raw` of user content).
3. **Boosts.** A boosts-for-page host operation; the boost partial, mapped with
   context.
4. Logo version, loaded-at and the signed stream name in `PageChrome`; the
   VAPID key as a hole.
5. Rerun the report-259 protocol for an equivalent-page comparison with Rails
   and Rust.
