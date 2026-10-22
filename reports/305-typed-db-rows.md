# 305 — Item 1b: typed database rows

Item 1a (report 301) checks every SQL literal against the app's schema when
the app is built. The rows still came back as `List<DbRow>`, a list of
untyped values. Each Campfire query then decoded its columns by position, by
hand:
- `db.optional-int(row, 2)` or `db.optional-text(row, 5)`;
- a check that the value was there;
- a default (`int-or-zero`, `text-or-empty`) when it was not.

That decoding was long and easy to get wrong: a swapped column index still
compiled. It was also slow, since each row's values were built as a list and
then scanned again.

Item 1b reads rows straight into a record:

```flow
record PageKey {
    id: Int
    updated-at-ms: Int
}

record ByRoom {
    room-id: Int
}

match db.rows<PageKey>("SELECT messages.id, ... AS updated_at_ms FROM ... WHERE room_id = $room_id", byRoom.new(room-id = 7)) {
    error failure => ...
    ok keys => ...          // keys: List<PageKey>
}
```

## The rules

**Row type.**
- `Row` is a record with no validator.
- Each field is `Int`, `Bool`, `String`, `Bytes`, or an `Option` of one of those.
- Columns map to fields by position. Field *i* reads column *i*.

**Parameters.**
- The second argument is a record.
- Each field binds the SQL parameter with the same name, with dashes as
  underscores. For example, `$room_id` binds the field `room-id`.
- A parameter field may also be a refined scalar of one of those types, such as
  `RoomId`, or an `Option` of one.
- The compiler rejects:
  - a `$name` that no field has;
  - a field that the SQL never names;
  - positional parameters (`?`, `?1`, `:name`, `@name`).

  So a parameter can no longer be passed in the wrong order.
- SQLite numbers named parameters by first appearance. The compiler works that
  order out from the SQL text, skipping quotes and comments, and passes it to
  the host.

**SQL.**
- The SQL must be a literal.
- The call needs `effects db.read`.
- A statement that writes is rejected at build time, the same as for
  `db.query`.

**Build-time check against the schema** (SqlCheck, extending report 301).
Every `db.rows` literal is prepared against `schema.sql`. Its result columns
are then checked against `Row`:
- **Column count:** the statement returns one column per field.
- **Names in order:** column *i* is named after field *i* (`created_at` for
  `created-at`). An expression column needs `AS name`. A swapped pair of
  columns is two errors, one per column.
- **Declared type:**
  - `Int` and `Bool` need INTEGER or NUMERIC affinity.
  - `String` needs TEXT or NUMERIC. NUMERIC is allowed because SQLite keeps text
    that is not a number as text, which is how Rails' `datetime(6)` columns hold
    timestamps.
  - `Bytes` needs BLOB.
  - Expression columns have no declared type, so they are checked when read
    instead.
- **NULLs:** a table column that may be NULL cannot be read into a field that
  is not an `Option`. `INTEGER PRIMARY KEY` is the rowid, which is never NULL.

Errors give the Flow line, for example:
`slip.flow:11:19: db.rows<UserRow>: column users.bio may be NULL but
UserRow.bio is String; make it Option<String> or the column NOT NULL`.

**When a value does not fit at runtime.** A value can still fail to fit:
- an expression column holds the wrong kind;
- a NULL arrives through a LEFT JOIN;
- a database was written by something else.

In those cases `db.rows` returns `SqlError` code 20 (SQLite's "datatype
mismatch"). Its message names the column, as one of:
- `column 3: expected Int, got text`;
- `column 1: expected Bool, got integer other than 0 or 1`;
- `a row has 2 columns; the row type has 1 fields`.

The interpreter and native builds give the same code and the same message.

## How it runs

**Interpreter.** `db.rows<Row>(sql, params)` lowers to:
1. a `DbParams` step, which turns the record into a list of values in SQL
   order;
2. the ordinary `db.query` host call;
3. a `DbDecode` step, which checks and converts each row into `Row`.

The call site still binds exactly one call (`db.query`), so call-site binding,
effects and the vocabulary analysis treat it like any other query.

**Native.**
- The three steps become one host call, `FH_DB_ROWS`. It takes the params
  record, the SQL, and the parameter order as a compile-time constant string,
  copied in place.
- The host binds each field straight from the params record's layout.
- It writes each row's fields straight into a `List<Row>` in the output
  layout. No `DbValue` list is ever built.
- The output goes through the same commit (validation) as every host output.
  Diagnostic builds check it fully.
- A lone `DbDecode`, not after its query, is a compile error
  (`IR_OWNING_STACK_DB_ROWS_SHAPE`), so the fused form is the only native form.

## Campfire

16 calls now use `db.rows`, covering every query on the room page's path:
- session lookups by token and by id;
- user lookups by id and by bot key;
- the room-for-user lookup;
- the membership check and three existence checks (room, and message in room);
- the message page and messages by id;
- the two page-key queries;
- boosts, attachments and mention users.

Each decoder became field reads:
- `message-row` went from 50 lines to a 15-line record copy. Validity is now a
  separate check, `page-row-ok?` (both timestamps parse).
  - `message-page` used to match each row's `Option<MessageWithCreator>` four
    times. Each match located the case by scanning the value, about 1% of
    uncached CPU in the profile.
  - It now checks validity with a Bool and derives one avatar token per run of
    rows by the same creator.
- `session-row` lost its six nested `match` steps on optional columns;
- each page-key loop (`value-int` on `list.get`, a validity flag, a rebuilt
  record) became `result.ok(rows)`.

All the columns read are NOT NULL in the schema, or are already Options, so
valid data reads exactly as before.

**What changed on bad data.** Boosts, attachments and mention users used to
put 0 or "" in place of a malformed value. Now one malformed row fails the
whole query. Their callers already turn a failed query into an empty list.
The message page, sessions and users already rejected malformed rows, and
they still do, now as `SqlError` 20, which `db-error` maps to
`DbError.unavailable()` as before.

## Checks

- **FlowHttp:** 804 assertions. The counter app gained six `db.rows` routes
  (seven cases), and each one is compared between the interpreter and the native servers
  (Windows and Linux, trusted and diagnostic):
  - parameters named in the reverse of the record's field order;
  - an `Option<String>` parameter, both NULL and set;
  - a refined parameter (`min-id: ItemId`, a `type ItemId : Int`);
  - Option, Bool and Bytes row fields;
  - one Bool per row;
  - text into Int;
  - NULL into String;
  - 2 into Bool;
  - two columns into one field.
- **SqlCheck:** 41 assertions, including 10 `db.rows` cases:
  - three valid shapes;
  - swapped columns;
  - an unnamed expression column;
  - a missing column;
  - text into Int;
  - integer into String;
  - a nullable column into a plain field;
  - a write.

  Campfire's 76 literal calls all pass.
- **Suites:** all 17 pass.
- **Byte comparison:** all 33 responses match the item-6c-3 build byte for
  byte.
- **Build:** the verified Campfire build takes 39.7 s.

## Speed

ABBA against the item-6c-3 build, with the protocol of report 285 (WSL2,
4 pinned vCPUs, sweep of 16/32/64/128 connections; each cell is a sweep's
peak). The table is the final build: typed rows plus the `message-page`
change.

| | 1 A (6c-3) | 2 B (1b) | 3 B (1b) | 4 A (6c-3) |
| --- | ---: | ---: | ---: | ---: |
| Uncached req/s | 1,961 | 2,202 | 2,201 | 1,951 |
| Uncached CPU ms per request | 2.11 | 1.88 | 1.88 | 2.13 |
| Cached req/s | 6,464 | 7,053 | 7,096 | 6,520 |
| Cached CPU ms per request | 0.623 | 0.568 | 0.569 | 0.624 |

- **Uncached: 13% more requests a second, and 11% less CPU per request.**
- **Cached: 9% more requests a second, and 9% less CPU per request.** The
  cached page still runs the session, user, room and membership lookups on
  every request; those now run as typed rows.
- **Typed rows alone,** before the `message-page` change, were measured on
  their own ABBA run:
  - uncached: 1,946 → 2,114 req/s (+9%), 2.16 → 1.98 ms of CPU;
  - cached: 6,415 → 6,892 req/s (+7%).
- **Memory:** unchanged, at 15.6 MB uncached and 47.8 MB cached (PSS). No run
  had an invalid response.
- **Profile** (uncached, typed rows before the `message-page` change):
  - `message-row` used to cost 4.3% inclusive; it is no longer a separate cost.
  - `message-page` is 2.8% inclusive, about 1% of that from the Option
    matches, which the change removes.
  - `sqlite3_step`, across all the page's queries, is 11.6% inclusive. Row
    decoding no longer shows up as a Flow cost of its own.
  - The top costs are still item 6's: `al_owning_scan_value` (11.9% flat,
    most of it under `rich-html.tag`) and `scan_text` (6.9%).
- **Build:** the verified Campfire build takes 38.8 s.

## Not done, for the user

- **LEFT JOIN columns.** SqlCheck misses a NOT NULL column read through a LEFT
  JOIN, which can still be NULL. Such a NULL is caught at runtime as
  `SqlError` 20. None of the converted queries hit this case: the one
  LEFT-joined column, `rich_text.body`, is already an `Option`.
- **Row fields are plain types only.** A field such as `id: RoomId` would need
  its validator run on each value, so row records use `Int` and the code wraps
  it (`RoomId.new(row.room-id)`). Parameters already accept refined scalars.
- **Other Campfire queries still use `db.query`:** the sidebar, account,
  search and users pages. Converting them is mechanical, but they are not on the
  measured page.
- **No `Float` fields.** Nothing in Campfire reads a REAL column.
