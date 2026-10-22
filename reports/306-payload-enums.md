# 306 — Item 2a: enums whose cases carry a payload

Item 2 is typed routes: a route table declared as an enum, one case per route,
each case carrying its path parameters, with `at "/rooms/{room}"` paths. Flow
enums had no payloads, so the item runs in three steps:
- **(a)** Enums whose cases carry a value, in the language and the interpreter.
  Native builds reject them with a diagnostic.
- **(b)** Native builds lay payload enums out as a true sum.
- **(c)** `at` routes, and Campfire's router built on them.

## (a) The language

```flow
enum Route {
    room(RoomId)
    around(RoomAround)
    sidebar
}

fn campfire.route-name(route: Route) -> String {
    match route {
        room id => string.concat("room ", int.to-string(RoomId.value(id)))
        around place => "around"
        sidebar => "sidebar"
    }
}

let route = Route.room(RoomId.new(5))
```

**One payload value per case,** written the way `Option` and `Result` are:
- A case declares at most one payload type: `room(RoomId)`. A case with
  several fields uses a record, as `around(RoomAround)` does.
- A match arm binds the payload with a name, `room id =>`, just as `some value`
  does. Or it ignores the payload, `room =>`.
- Binding a case that has no payload is an error (`FLOW_MATCH_ENUM_BINDING`).
- The constructor takes the payload: `Route.room(id)`. A bare case is built as
  before, `Route.sidebar()`.
- Equality compares the case, then the payload.
- Payload enums can sit in records, lists and options like any other value.

Multi-field cases, such as `room(id: RoomId, at: Int)`, could be added later as
sugar over a generated record. They are not needed for routes.

**How it runs.** The payload lives in every layer, with nothing desugared, so
errors and test failures show the real types:
- The Core enum definition records one optional payload type per case.
- Values are `EnumValue(type, case, payload)`.
- An IR `MatchEnum` arm can bind its payload to a local, as `MatchOption`'s
  some-arm does. The verifier checks that:
  - only a payload case binds;
  - the local has the payload's type;
  - the local does not escape the arm.
- The constructor's generated target takes the payload as its input.
- The interpreter carries the payload in its runtime value. Values a host
  passes in are checked against the declared payload: a payload case without
  its value is rejected, and so is a value on a bare case.

**Payload-free enums are unchanged:**
- Their source text, fingerprints, IR JSON and native layout are byte-for-byte
  what they were.
- A binding adds fingerprint text only when an arm has one, so stored
  dictionaries and existing words keep their identities.

**Native builds** reject a payload enum, with `IR_OWNING_STACK_ENUM_PAYLOAD`,
until (b). The legacy LLVM backend already rejects every enum.

**Checks:**
- A new suite, `AgentLang.PayloadEnums.Tests`, has 116 assertions:
  - syntax: payload declarations, binding and ignoring arms, and formatting
    that round-trips;
  - type errors: a binding on a bare case, a payload of the wrong type, a
    missing payload, a missing case, and a payload used as the wrong type;
  - every attached test of a route enum with Int, record, `Option` and `List`
    payloads plus a bare case;
  - interpreter entry points with payload values in and out, including an
    81-pair equality matrix;
  - payload enums inside a record, a list and an option;
  - host values with a missing or unexpected payload, both rejected;
  - the native rejection.
- **Suites:** all 23 pass. The gate now includes the new suite, plus the
  value-inspection, storage, discovery, vocabulary and source suites that the
  changed types touch.
- **Campfire:** unchanged. The verified build takes 39.2 s, and its
  `module.ll` is byte-identical to the item-1b build.

## (b) Native builds

A payload enum is laid out the way `Result` is: an 8-byte case tag, then the
active case's payload. A bare case is the tag alone. It uses a new layout kind,
`AL_OWNING_TYPE_UNION` (12), and `case_count` cases, any of which may be bare.

**The emitter:**
- `buildSum` already took any list of cases. A payload enum passes its own
  cases.
- A payload case is built by the code that builds `Result.ok`, which writes the
  tag and copies the payload in. A bare case is one 8-byte store.
- A match locates the active case with `al_owning_locate_sum_case`, as a
  `Result` match does. It switches over every case and stores the payload in
  the arm's local when the arm binds one.
- Local planning, binder collection and the arena-lifetime proof treat an arm's
  binding the way they treat a `Result` match's ok local.
- Host values (test inputs and outputs) encode and decode the tag and the
  payload.

**The C runtime:** five functions branched on Option and Result:
- descriptor validation;
- type-graph validation;
- value scanning;
- typed equality;
- locating the active case.

Each now asks `al_owning_is_sum_kind`, and reads its case bound from the
descriptor's case count instead of a fixed 2. Option and Result keep their
exact two-case rules. Payload-free enums are unchanged: an 8-byte ordinal of
kind 6.

**Checks:**
- `AgentLang.PayloadEnums.Tests` now has 736 assertions. Every entry point gives
  the interpreter's values at O0, O2 and trusted O2:
  - all 12 sample routes through `describe` and `kind`;
  - the 144-pair equality matrix;
  - construction;
  - a payload enum in a list, an option and a record.

  The payloads cover Int, String (including `é🚀` and a value longer than a
  word), a record, an `Option<String>` and a `List<String>`, plus a bare case.
- **Suites:** all 23 pass.
- **Campfire:** its `module.ll` is byte-identical to (a), and all 33 responses
  match. The build takes 38.3 s.

**Speed:** ABBA against the (a) build. Campfire has no payload enums, so its
module is identical; only the C runtime's sum checks changed.

| | 1 A (2a) | 2 B (2b) | 3 B (2b) | 4 A (2a) |
| --- | ---: | ---: | ---: | ---: |
| Uncached req/s | 2,247 | 2,265 | 2,280 | 2,199 |
| Uncached CPU ms per request | 1.89 | 1.88 | 1.88 | 1.89 |
| Cached req/s | 7,010 | 6,942 | 7,045 | 6,898 |
| Cached CPU ms per request | 0.573 | 0.552 | 0.571 | 0.556 |

The result is neutral, within run-to-run noise (+2% uncached, about 0% cached).
Reading the case bound from the descriptor costs nothing measurable.

## (c) Typed routes

```flow
enum SiteRoute {
    home at "/"
    room(String) at "/rooms/{room}"
    around(RoomAround) at "/rooms/{room}/@{message}"
    sidebar at "/users/me/sidebar"
    numbered(Int) at "/numbers/{n}"
}
```

A path is matched literally, segment by segment:
- **A literal segment** matches its exact text, case-sensitively.
- **`{name}`** matches one non-empty segment.
- **`prefix{name}`** matches a segment that starts with the prefix and has more
  after it.
- **Parameter types:** a parameter is a String or an Int. A segment that does
  not parse as an Int names no route.
- **Payloads:** a case with a String or Int payload has exactly one parameter.
  A case with a record payload names each of the record's fields once, and that
  record has no validator. A bare case has no parameters.
- **No percent-decoding:** `/rooms/%31` is the room `"%31"`, not room 1.

**Checked when the enum is defined:**
- Every path starts with `/` and has no empty segment (`FLOW_ROUTE_PATTERN`).
- When one case has a path, every case does (`FLOW_ROUTE_MISSING`).
- The parameters fit the payload (`FLOW_ROUTE_PAYLOAD`).
- **No two paths can match the same text** (`FLOW_ROUTE_OVERLAP`). Two paths
  overlap when they have as many segments, and every pair of segments can match
  the same text:
  - two literals match only if they are equal;
  - a literal and a parameter can always match;
  - `@me` and `@{x}` can match the same text.

  So the order of the cases never matters. This also rules out the common
  `/users/me` beside `/users/{id}`; such a table needs a different shape.

**Generated words.** For `SiteRoute`, the generated words are:
- `siteRoute.parse(target) -> Option<SiteRoute>`;
- `siteRoute.path(route) -> Url`;
- `siteRoute.escape(text)`, when a path has a String parameter. It
  percent-encodes every byte except `A-Z a-z 0-9 - . _ ~`.

They are ordinary Flow words, generated as source when the enum is defined. So
the interpreter, the verifier and native builds treat them like any other
words, and `describe` shows their code.

`path` returns the built-in `Url`, so a route goes straight into an `href` hole
(report 302), and the escaping means `Url.new` never fails. Each word comes
with generated tests:
- every case's sample path, with `"x"` for a String and `7` for an Int;
- every case's round trip through `parse(path(sample))`;
- two paths that name no route;
- the escaping of a few bytes.

The words commit with those tests, like anything else. A word of yours with a
generated name is an error (`FLOW_ROUTE_NAME`).

An exhaustive `match` on the parsed route is the dispatcher, so a new route that
no handler covers fails to compile.

**Checks:** `AgentLang.PayloadEnums.Tests` now has 871 assertions:
- one rejection for each rule above;
- formatting that keeps `at` paths;
- the generated words committing with their tests;
- `parse` on 21 paths, chosen to cover Campfire's edge cases:
  - `/Rooms/1` and `/rooms/1/MESSAGES` (case);
  - `/rooms/` and `/rooms/1/` (trailing slash);
  - `/rooms/1/@` and `/rooms/1/@2.json` (the `@{message}` segment);
  - `/users//avatar` (an empty segment);
  - `/numbers/x` (an Int that does not parse);
  - `rooms/1` and the empty path;
- `path` with escaping;
- an exhaustive dispatcher, interpreter against native at O2 and trusted O2.

**Limits:**
- **A route table is fixed once defined in a live dictionary.** Defining the
  same enum again with other paths is `FLOW_ROUTE_WORDS_CHANGED`, and removing
  its paths leaves the generated words in place, because the dictionary has no
  way to retire words. A fresh build, such as every app build, is unaffected.
- **At O0, a large table can exceed the native 64 KB frame screen.** The
  eight-route test table's parser uses 67 KB at O0, because O0 keeps a stack
  slot per temporary. O2, which every app build uses, has no such problem.
  Splitting `parse` per route would fix it.
- **Payload enums are untested at the host boundary:** as a host input or
  output, a worker context, a store value or a mailbox message. Campfire's
  route stays internal to the app.

## (c2) Campfire's router

Before this change, `campfire.server.request` worked out the route through
string tests: `room-path`, `room-id-text`, a `starts-with`/`ends-with` test for
user pages and avatars, and six Bool flags on `ServerRequest` (`paging`,
`sidebar`, `search`, `user-page`, `avatar`, `account`). `handle` then read those
flags in a chain of `if`s. Now the routes are a table:

```flow
enum CampfireRoute {
    room(String) at "/rooms/{room}"
    around(AroundPath) at "/rooms/{room}/@{message}"
    messages(String) at "/rooms/{room}/messages"
    sidebar at "/users/me/sidebar"
    searches at "/searches"
    user(String) at "/users/{user}"
    avatar(String) at "/users/{token}/avatar"
    account at "/account/edit"
}
```

**Dispatch.** `ServerRequest` carries the route, and `handle` dispatches on it
with one exhaustive `match`. Two parity quirks stay in Campfire's own code, in
`campfire.server.route`, rather than in the table:
- the `/rooms/` prefix matches in any case: `/Rooms/1` is a room;
- an `@message` segment may not contain a dot, which is the `at_message`
  route's constraint.

**Parameter checks** stay in the app:
- Room ids and the message are text in the table. A room id that does not parse
  is still a 400 (`parse-room-id`), and the message is still cast as
  ActiveRecord casts it (`ruby-to-i`).
- A route without a room still carries room 0, so the checks still run in the
  same order: 404, 405, 400 Host, 431, Content-Length, 400 id.

`room-path`, `room-id-text` and their tests are gone. The view links are
unchanged in this item: they still build their paths themselves.

**Checks:**
- **Byte comparison:** it gained 12 router cases, which the old router answers
  as follows:
  - `/Rooms/1` and `/ROOMS/1/messages`: 200;
  - `/rooms/x1` and `/rooms/%31`: 400;
  - 404 for each of:
    - `/rooms/1/MESSAGES`;
    - `/rooms/1/@2.json` and `/rooms/1/@`;
    - `/rooms/1/`;
    - `/users//avatar`;
    - `/users/me/sidebar/` and `/searches/`;
    - `/up`.

  All 45 responses are byte-identical to the item-2b build.
- **Tests:** the verified build commits the generated route words with their
  tests, plus a new `campfire.server.route/forms` test. CampfireViews and
  SqlCheck pass.
- **Build:** the verified build takes 37.8 s, and `module.ll` is 3 KB smaller.

**Speed:** ABBA against the item-2b build.

| | 1 A (2b) | 2 B (2c) | 3 B (2c) | 4 A (2b) |
| --- | ---: | ---: | ---: | ---: |
| Uncached req/s | 2,213 | 2,222 | 2,214 | 2,218 |
| Uncached CPU ms per request | 1.886 | 1.878 | 1.889 | 1.883 |
| Cached req/s | 7,070 | 7,201 | 7,139 | 7,004 |
| Cached CPU ms per request | 0.572 | 0.565 | 0.572 | 0.578 |

Neutral. Uncached is flat, and cached is about +2% (0.575 → 0.568 ms of CPU),
because the route is worked out once instead of from repeated `http.path`
calls. Item 2 is about correctness and clarity, not speed.

## Not done, for the user

- **View links** still build their paths by hand. `campfireRoute.path` returns
  `Url`, so a link can be `{campfireRoute.path(CampfireRoute.room(id))}` in an
  `href`. Converting the views is mechanical, but each changed link puts the
  byte-identical output at risk, so it was left out.
- **Routes for writes** (`POST /rooms/{id}/messages` and others) are not in the
  table. The native host serves only the read routes.
- **The table has no method.** GET and HEAD are checked before dispatch, as
  before.
