# 311 — The recommended follow-ups to report 310

Report 310 ended with low-risk follow-ups. This report does them and measures
each one on its own. The checks are the same as in 310:
- the 45 sampled responses are byte-identical to the report-310 build;
- all suites pass;
- an ABBA run against the step before it (WSL2, 4 pinned vCPUs, sweep
  16/32/64/128).

| Step | Change | Uncached | Cached | Kept? |
| --- | --- | ---: | ---: | --- |
| 1 | C runtime tests in the gate | — | — | yes |
| 2 | `inline fn` on six rich-html byte predicates | −1% | noise | **no, reverted** |
| 3 | one avatar token per distinct user on a page | **+2.2%** | noise | yes |
| 4 | view links from the route table | **−4%** | −2% | yes, your call |
| 5 | SQLite memory statistics off | **+9%** | **+27%** | yes |

## 1. The C runtime's own tests are in the gate

**The problem.** `scripts/Verify-NativeValueStack.ps1` ran the C unit tests,
but it now fails before reaching them: it first builds
`experiments/AgentLang.NativeValueStack`, which no longer compiles against the
current `Value` type. Report 310 had to run them by hand.

**The fix.** `scripts/Test-NativeRuntime.ps1` builds and runs the tests with
the toolchain's clang, with the same flags as before (`-Wall -Wextra -Werror`):
- `owning_stack_runtime_test.c` at O0, at O2, and under trap-mode UBSan
  (58 cases, 872 checks);
- `html_escape_test.c` at O2 and under UBSan. This is report 310's exhaustive
  check of the 8-byte escape scan against the old byte loop: 361,378 inputs,
  with a fixed pseudo-random generator so every platform tests the same
  strings.

It takes 11 s, and the gate script runs it after the .NET suites.

## 2. `inline fn` on the byte predicates: no gain, reverted

**The change.** `space?`, `name-char?`, `word-byte?`, `ascii-word?`,
`url-char?` and `letter?` are one-line byte tests called on most characters.
`rich-html.tag` made 7 full native calls to `space?`. With `inline fn`, the
optimized build inlines them; the shipped `rich-html.tag` made no call to
`space?` afterwards.

**Speed** (ABBA against the report-310 build):

| | 1 A | 2 B | 3 B | 4 A |
| --- | ---: | ---: | ---: | ---: |
| Uncached req/s | 3,112 | 3,071 | 3,104 | 3,135 |
| Uncached CPU ms per request | 1.386 | 1.407 | 1.397 | 1.385 |
| Cached req/s | 8,000 | 7,918 | 7,340 | 8,008 |
| Cached CPU ms per request | 0.526 | 0.518 | 0.514 | 0.491 |

- **Uncached: about 1% slower**, and both B runs are below both A runs.
- **Cached:** noise; run 3B was a slow lifetime.

The calls were cheap next to the larger code they left in `rich-html.tag` and
`rich-html.scan`, so the change is reverted. Report 308's guess that this was
"the next small lead" was wrong.

## 3. One avatar token per distinct user

**What was slow.** Each avatar link carries a signed token: an HMAC-SHA256
from libcrypto, then Base64 and hex. The benchmark room page shows 43 avatar
links for 4 users, but tokens were made far more often than once per user:
- `message-page` made one per run of messages by the same creator;
- `boosts-for` made one per boost.

**The fix.** Both keep the tokens they have made, with a
`campfire.server.known-token` lookup over a short list, and sign each user
once. The bytes are the same, since a token depends only on the key and the
user id.

**Speed** (ABBA against step 2):

| | 1 A | 2 B | 3 B | 4 A |
| --- | ---: | ---: | ---: | ---: |
| Uncached req/s | 3,059 | 3,160 | 3,073 | 3,038 |
| Uncached CPU ms per request | 1.379 | 1.347 | 1.346 | 1.375 |
| Cached req/s | 7,926 | 7,815 | 7,770 | 7,918 |
| Cached CPU ms per request | 0.515 | 0.523 | 0.522 | 0.518 |

- **Uncached: 2.2% more requests a second, and 2.2% less CPU per request.**
- **Cached:** within noise. The cached page builds its messages from the
  cache, so it makes few tokens either way.

## 4. View links come from the route table

**The change.** Links to the read routes are now built with
`campfireRoute.path` through small `campfire.route.*` helpers:
- the room, a message permalink, a room's messages;
- a user, an avatar;
- the sidebar, searches and account pages.

So a link can no longer name a path the table lacks; renaming a route is a
compile error, not a broken link. The table moved from
`server/server-request.flow` to `routes.flow`, so the views and the server
share it. Write routes (edit, involvement, `account/users`, ban) are not in
the table, and their links stay as text. Links inside tests stay as text,
because they test the router.

**Speed** (ABBA against step 3):

| | 1 A | 2 B | 3 B | 4 A |
| --- | ---: | ---: | ---: | ---: |
| Uncached req/s | 3,121 | 2,983 | 2,994 | 3,120 |
| Uncached CPU ms per request | 1.357 | 1.409 | 1.410 | 1.353 |
| Cached req/s | 7,902 | 7,751 | 7,687 | 7,868 |
| Cached CPU ms per request | 0.516 | 0.494 | 0.526 | 0.521 |

- **Uncached: 4% fewer requests a second, and 4% more CPU per request.**
- **Cached: about 2% fewer requests a second.**

**Why it costs.** The links used to be literal text inside the templates,
written with no work. Now each one runs at request time:
- it builds the route value;
- it checks each String parameter for bytes that need percent-encoding;
- it joins the pieces;
- it checks the result as a `Url`.

The room page has about 160 such links (four for each of 40 messages).

**What would make it free.** Have the compiler build a link at compile time
when its parameters are already digits, or inline `path` for a known case.
Neither is done; whether to keep the links, and whether that compiler work is
worth it, is for you to decide.

## 5. SQLite memory statistics off

**What was slow.** The system SQLite (3.45.1, thread-safe, default
`SQLITE_DEFAULT_MEMSTATUS=1`) keeps memory statistics. Every SQLite allocation
then takes one process-wide mutex that all four workers share. The report-310
profile showed this as futex wake-ups under `sqlite3Malloc`, about 3% of CPU.

**The fix.** `fh_db_configure` calls `sqlite3_config(SQLITE_CONFIG_MEMSTATUS,
0)` once in `main`, before the first SQLite call and before any worker starts.
The host never reads the statistics: there is no `sqlite3_memory_used` and no
soft heap limit. Windows needs `sqlite3_config` in the `.def` import list,
which is added.

**Speed** (ABBA against step 4):

| | 1 A | 2 B | 3 B | 4 A |
| --- | ---: | ---: | ---: | ---: |
| Uncached req/s | 2,943 | 3,249 | 3,240 | 2,987 |
| Uncached CPU ms per request | 1.428 | 1.340 | 1.337 | 1.419 |
| Cached req/s | 7,779 | 9,701 | 9,409 | 7,286 |
| Cached CPU ms per request | 0.517 | 0.455 | 0.466 | 0.535 |

- **Uncached: 9% more requests a second, and 6% less CPU per request.**
- **Cached: 27% more requests a second, and 12% less CPU per request.** It is
  the largest cached gain of the reopened work. The cached page does much less
  other work per request, so the shared lock was a larger share of its time
  (not profiled separately).

## Campfire stays an application

Every change here is either generic or Campfire's own Flow:
- the byte predicates, the escaping and the integer loads are generic standard
  library and runtime code;
- the SQLite setting is generic host code;
- the token memo and the route links are in Campfire's Flow sources.

An audit of `src/` (compiler, runtime, standard library, HTTP host) found **no
Campfire logic**. What it did find:
- **Comments** that described generic code in Campfire terms ("the Campfire
  host PRAGMAs", an old `al_campfire_…` name, a `CampfireRoute` example). They
  now use neutral names.
- **The runtime's profiling categories 9–17 are named after Campfire
  operations**: `SESSION_FIND_BY_TOKEN`, `ROOM_FIND_FOR_USER`,
  `MESSAGE_LAST_PAGE` and others. They are used only by the old native
  room-read adapter in `applications/campfire/Native` and its smoke test. They
  are left in place, because removing them belongs with removing that adapter.
- **`rich-html` in the standard library** is a Rails/Action Text
  canonical-HTML library. It is generic to Rails-style rich text, but its tag
  list was chosen to match what Campfire's messages contain.
- **Fixed SQLite PRAGMAs in the host** (foreign keys, WAL, `synchronous=FULL`)
  are reasonable defaults for any app, but they were chosen for Campfire, and
  an app cannot change them.

**Old non-Flow Campfire code still lives in `applications/campfire`.** It is
the pre-Flow implementation: F# projects (`Http`, `Host`, `Cookies`,
`Persistence`, `Authentication.Host`, `Authentication.Persistence`) and C
(`Native`: the room-read adapter, a native HTTP host, cookie codecs). The
served Campfire uses none of it. The served build is Flow sources only, on the
generic host. The old code is still in `AgentLang.sln` and used by four old
scripts and some old test projects. It is application code, not runtime code,
but it is the C and F# that the "Campfire is pure Flow" rule says should go.
