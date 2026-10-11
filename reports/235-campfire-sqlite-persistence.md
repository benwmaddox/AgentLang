# 235 — Campfire SQLite persistence component

Status: standalone storage implemented and locally tested; language integration pending.

Campfire now has a narrow trusted SQLite adapter for room paging, membership lookup, newest-message listing and transactional message creation. Application authorization remains language code. The adapter does not accept SQL, connection strings or database paths from language programs. Its host-selected database must already exist; opening it does not create a replacement database.

## Behavior

The adapter uses nominal Int64 user/room/message IDs, closed room-kind and database-error unions, and optional message bodies. Room pages are keyset-based and bounded to 1–100 rows. Message pages select the newest 40 by creation time and reverse the selected rows. Bodies use `action_text_rich_texts`, matching the upstream schema rather than adding a body column to messages. Client message IDs remain non-unique.

Message creation inserts the message and optional body, conditionally touches the message, and always touches its room in one transaction. SQL inputs are parameters. Clock injection makes timestamps deterministic in tests; UTC timestamps truncate to microseconds and omit the fractional suffix at whole seconds. The eventual host operation must declare both `db.write` and `clock.read` while it reads the injected clock.

The pinned references remain Rust `6dae2fd3c1a2dce61ca11beb731bcce2b42b67ad` and its Rails reference `90b330024dec3e757c79b6a7e6568f93da8e3148`. Tests use a narrow schema fixture with the relevant foreign keys and unique indexes, not a full migrated Campfire database.

## Validation

A fresh serial Release build passed with zero warnings and errors. The standalone console suite passed four groups and 60 assertions; the primary agent repeated the executable successfully against fresh fixture databases. Checks cover room-page limits/cursors, closed room kinds, orphan membership behavior, newest-40 ordering including a timestamp-tie fixture, optional bodies, persistence after reopen, parameterized injection-shaped data, timestamp formatting, typed failures, and transaction rollback after body insertion and final room-touch failures.

The initial sandbox dependency restore failed with `NU1900` because the vulnerability feed was inaccessible. The reviewed project was restored on the host with auditing enabled; builds and executable tests then ran locally in the sandbox. Early compiler errors and a test-fixture expectation error were retained and corrected before the final passing runs. `Microsoft.Data.Sqlite` 10.0.12 is confined to the application persistence project; Core has no SQLite dependency.

## Remaining integration

These results establish the standalone storage component. The four operations still need registration with exact nominal contracts in the language's verified IR, provider result validation, capability preflight, observed effects, and fresh per-test providers with scoped dependency replacement. The first bridge will explicitly reject native execution until its native ABI exists.

Authenticated HTTP handling, membership checks before reads/writes, session and CSRF verification, rich-text sanitization/rendering, attachment handling, after-commit indexing/broadcast/unread callbacks and complete schema/migration parity remain required. Storing injection-shaped HTML safely as SQL data does not establish safe browser rendering. This report makes no claim of a usable application, full upstream parity, native readiness or server performance.

## Evidence

The [archive](evidence/235-campfire-sqlite-persistence/evidence.zip), [member index](evidence/235-campfire-sqlite-persistence/index.json), and [receipt](evidence/235-campfire-sqlite-persistence/receipt.json) retain 18 members, including exact final sources and passing and failed local logs. The primary agent independently read back every member and verified its SHA-256 hash. Archive SHA-256: `d8456e5ceba90e940a6740409c435d8343d11944c3043e73b92606a49921eb4a`.
