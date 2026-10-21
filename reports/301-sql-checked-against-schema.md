# 301 — Item 1a: SQL checked against the schema at build time

Item 1 (report 300 lists the items) is in two parts:
- **1a**, this report, needs no new syntax. Every SQL literal is checked against
  the app's committed schema when the server is built, and the server refuses
  to run on a database with any other schema.
- **1b** is typed request and response records for queries. It waits for the
  user's choice of syntax.

## What changed

- **`applications/campfire/schema.sql`.** Campfire's schema: Rails at 90b3300,
  dumped with `sqlite3 .schema` from the benchmark snapshot. It is Rails'
  public schema (MIT), with no data. `sqlite_sequence` is left out, because
  SQLite makes it itself.
- **`AgentLang.FlowHttp emit --schema <schema.sql>`** (`SqlCheck.fs`) runs before
  anything else in the build. It parses the app's Flow sources and finds every
  `db.query` and `db.execute` call whose SQL is a literal, in words, tests and
  test files. It builds an in-memory SQLite database from `schema.sql` and
  prepares each literal there. Any of these fails the build, with the file, line
  and column of the Flow call:
  - an unknown table or column, or a syntax error (SQLite's own message);
  - more than one statement in a literal;
  - a parameter count that differs from the call's list, when that list is a
    literal (`[…]`, `list.empty`, `list.singleton`);
  - a `db.query` whose statement writes (`sqlite3_stmt_readonly`), because
    `db.query` is declared `db.read`, so the effect declaration would be false;
  - a `db.query` that returns no columns, or a `db.execute` that only reads rows;
  - more distinct literals than the native host prepares per worker (256);
  - **test-fixture DDL.** `campfire.server.test-fixture` creates its own tables
    for the attached tests. Every table it creates must be in `schema.sql`, and
    every column it declares must be one of that table's.
- **The server's schema guard.** The build compiles the canonical schema text
  into the server (`flow_app_schema.c`). The canonical text is every object's
  type, name, table and SQL, in a fixed order, with FTS shadow tables left out.
  At startup, before the workers start, `fh_db_provision` reads the same text from
  the database and compares it byte for byte. On any difference, the server
  exits with a message naming the object. Without `--schema` the text is empty
  and nothing is checked, as for the FlowHttp test app.
  - **Not checked:** the interpreter host only serves attached tests, which use
    the fixture tables, so it has no guard.

## Checks

- **New suite:** `tests/AgentLang.SqlCheck.Tests`, 31 assertions. Each slip must
  give exactly one error, at the Flow line:

  | Slip | Error |
  | --- | --- |
  | `SELECT id, nmae FROM users` | `no such column: nmae` |
  | `SELECT id FROM user` | `no such table: user` |
  | `SELEC …` | syntax error |
  | 1 placeholder, 2 parameters passed | the counts |
  | 2 placeholders, `list.empty` passed | the counts |
  | `UPDATE … RETURNING` in `db.query` | `db.query is declared db.read` |
  | `SELECT` in `db.execute` | use `db.query` |
  | two statements in one literal | one statement per literal |
  | fixture column `users.nickname` | not in `schema.sql` |
  | fixture table `people` | not in `schema.sql` |
  | a bad query inside a test body | `no such column` |
  | 3 literals with a limit of 2 | at most 2 per worker |

  The same suite checks the passing cases:
  - valid queries, writes and an FTS5 `MATCH` query (Campfire search);
  - a parameter list held in a local, which is not counted;
  - Campfire's own sources: all 76 literal calls are found and all pass;
  - the hash changes when the schema changes, and is stable when it doesn't.
- **Campfire passes as it is:** 76 calls and 72 distinct statements, with no
  errors. The code needed no changes. Its fixture tables are a subset of the
  real ones.
- **The guard, on the real server** (`.agentlang/reopen-300/sql/guard.sh`):

  | Database | This build | The base build |
  | --- | --- | --- |
  | The snapshot | starts | starts |
  | Snapshot plus an index | refused: "the database has index extra_users_name where the build expects index idx_on_endpoint…" | starts |
  | Snapshot without `webhooks` | refused: "…has table accounts where the build expects index index_webhooks_on_user_id" | starts |
  | Snapshot plus a column on `users` | refused: "table users is defined differently" | starts |

- **The hashes agree.** The canonical schema text from `schema.sql` (built by the
  .NET bundle, SQLite 3.53.3) and the text from the snapshot database read
  directly hash to the same value, `76a0de72…`. The Linux server (system
  SQLite 3.45.1) accepted the snapshot, so both versions produce the same text.
- **Byte parity:** all 22 sampled responses match the base build.
- **Suites:** the 15 suites listed in report 300, plus SqlCheck, all pass.
  FlowHttp.Tests builds its test app natively without `--schema`, which
  exercises the empty-schema path on both targets.

## Speed and build time

- **The check takes 0.2 s** of the build. The verified Campfire build took
  40.7 s (41.2 s in report 300).
- **No request-time cost.** The guard runs once at startup and adds nothing to
  requests.
  - Uncached: 1,165 req/s at 3.42 ms CPU per request, against 1,170 req/s at
    3.40 ms for the base build's last run.
  - Cached: the single run read 11% more CPU per request. An ABBA run showed no
    change:

  | Lifetime | req/s | CPU ms per request |
  | --- | ---: | ---: |
  | 1 A (base) | 5,508 | 0.704 |
  | 2 B (this) | 5,572 | 0.696 |
  | 3 B (this) | 5,622 | 0.690 |
  | 4 A (base) | 5,540 | 0.701 |

## What this does not do (1b and later)

- **Values still travel by position.** A query's columns are still read with
  `db.int(row, 0)`, and its parameters bind in order. Neither the column
  positions nor swapped parameters of the same type are checked. Typed request
  and response records (1b) close both gaps.
- **Column types and nullability are unchecked.** That is also 1b.
- **No index check.** Query plans (`EXPLAIN QUERY PLAN`, full scans) aren't
  checked yet, though the same hook could do it.
- **Fixture column types are not compared with the schema's.** Rails declares
  `datetime(6)` (NUMERIC affinity) where the fixture declares TEXT. That doesn't
  matter for the ISO text Campfire stores, but it is a real difference.
