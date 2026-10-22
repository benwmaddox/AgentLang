/* db.query / db.execute for the portable Flow HTTP host, against the SQLite C
 * API. One connection and one prepared-statement cache per worker; nothing is
 * shared between workers. The adapter also routes cache.get / cache.put to the
 * process-wide cache (flow_http_cache.c). Mirrors AgentLang.FlowHttp/Db.fs step for step so
 * interpreter and native results (rows, change counts, SqlError codes) agree. */
#include "flow_http.h"
#include "flow_http_sqlite.h"
#include "flow_http_cache.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define FH_DB_STATEMENTS 256u
#define FH_DB_QUERY 1u
#define FH_DB_EXECUTE 2u
#define FH_CACHE_GET 3u /* src/AgentLang.FlowHttp/Cache.fs nativeOperations */
#define FH_CACHE_PUT 4u
/* db.rows<Row> (report 305): typed parameters and rows; the generated code passes
 * the SQL, the params record and the parameter order, and names the output type. */
#define FH_DB_ROWS 19u
#define FH_PARAMETER_COUNT_MESSAGE "parameter count does not match the statement"

/* DbValueKind ordinals, declaration order in src/stdlib/db.flow. */
enum { FH_KIND_NULL = 0, FH_KIND_INT = 1, FH_KIND_TEXT = 2, FH_KIND_BLOB = 3 };

typedef struct fh_statement {
  char *sql;
  uint32_t sql_length;
  uint64_t hash;
  sqlite3_stmt *statement;
} fh_statement;

struct fh_db {
  sqlite3 *connection;
  uint32_t count;
  fh_statement statements[FH_DB_STATEMENTS];
};

static uint64_t fh_hash(const uint8_t *data, uint32_t length) {
  uint64_t hash = 1469598103934665603ull;
  uint32_t i;
  for (i = 0u; i < length; ++i) { hash ^= data[i]; hash *= 1099511628211ull; }
  return hash;
}

static int fh_exec(sqlite3 *connection, const char *sql) {
  return sqlite3_exec(connection, sql, 0, 0, 0) == SQLITE_OK;
}

fh_db *fh_db_open(const char *path, char *reason, uint32_t reason_capacity) {
  fh_db *db = (fh_db *)calloc(1u, sizeof(fh_db));
  int rc;
  if (db == 0) { snprintf(reason, reason_capacity, "out of memory"); return 0; }
  rc = sqlite3_open_v2(path, &db->connection,
                       SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_NOMUTEX | SQLITE_OPEN_PRIVATECACHE, 0);
  if (rc != SQLITE_OK) {
    snprintf(reason, reason_capacity, "sqlite open failed (%d)", rc);
    fh_db_close(db);
    return 0;
  }
  sqlite3_extended_result_codes(db->connection, 1);
  sqlite3_busy_timeout(db->connection, 5000);
  if (!fh_exec(db->connection, "PRAGMA foreign_keys=ON") ||
      !fh_exec(db->connection, "PRAGMA read_uncommitted=0") ||
      !fh_exec(db->connection, "PRAGMA journal_mode=WAL") ||
      !fh_exec(db->connection, "PRAGMA synchronous=FULL") ||
      !fh_exec(db->connection, "PRAGMA wal_autocheckpoint=1000")) {
    snprintf(reason, reason_capacity, "sqlite setup failed (%d)", sqlite3_extended_errcode(db->connection));
    fh_db_close(db);
    return 0;
  }
  return db;
}

/* The canonical schema the app's SQL was checked against at build time
 * (flow_app_schema.c, written by AgentLang.FlowHttp emit --schema; empty means no
 * check). The query is SqlCheck.canonicalQuery: fields end with 0x1F, rows with 0x1E. */
extern const char flow_app_schema[];
#define FH_SCHEMA_QUERY "SELECT type, name, tbl_name, sql FROM sqlite_schema WHERE sql IS NOT NULL AND name NOT IN (SELECT name FROM pragma_table_list WHERE type = 'shadow') ORDER BY type, name"

/* The type and name of the expected row that starts at `at`, for the message. */
static void fh_schema_expected_object(const char *expected, size_t at, char *out, size_t capacity) {
  size_t k = 0u;
  int fields = 0;
  while (expected[at] != 0 && expected[at] != '\x1e' && k + 1u < capacity) {
    char c = expected[at++];
    if (c == '\x1f') {
      if (++fields == 2) break;
      c = ' ';
    }
    out[k++] = c;
  }
  out[k] = 0;
}

/* 1 when the open database has exactly the build's schema (or the build has none). */
static int fh_schema_matches(sqlite3 *connection, char *reason, uint32_t reason_capacity) {
  const char *expected = flow_app_schema;
  const size_t total = strlen(expected);
  size_t at = 0u;
  sqlite3_stmt *statement = 0;
  int rc, same = 1;
  char object[160];
  if (total == 0u) return 1;
  if (sqlite3_prepare_v2(connection, FH_SCHEMA_QUERY, -1, &statement, 0) != SQLITE_OK) {
    snprintf(reason, reason_capacity, "schema check failed: %s", sqlite3_errmsg(connection));
    return 0;
  }
  while (same && (rc = sqlite3_step(statement)) == SQLITE_ROW) {
    const size_t row = at;
    int column;
    for (column = 0; column < 4 && same; ++column) {
      const char *text = (const char *)sqlite3_column_text(statement, column);
      size_t length = (size_t)sqlite3_column_bytes(statement, column);
      char end = column == 3 ? '\x1e' : '\x1f';
      if (at + length + 1u > total || (length > 0u && memcmp(expected + at, text, length) != 0) || expected[at + length] != end)
        same = 0;
      else
        at += length + 1u;
    }
    if (!same) {
      char found[160];
      snprintf(found, sizeof found, "%s %s", (const char *)sqlite3_column_text(statement, 0), (const char *)sqlite3_column_text(statement, 1));
      fh_schema_expected_object(expected, row, object, sizeof object);
      if (strcmp(found, object) == 0)
        snprintf(reason, reason_capacity, "the database schema differs from the build's: %s is defined differently (rebuild against this schema, or migrate the database)", found);
      else
        snprintf(reason, reason_capacity, "the database schema differs from the build's: the database has %s where the build expects %s (rebuild against this schema, or migrate the database)",
                 found, object[0] != 0 ? object : "nothing more");
    }
  }
  sqlite3_finalize(statement);
  if (same && at != total) {
    fh_schema_expected_object(expected, at, object, sizeof object);
    snprintf(reason, reason_capacity, "the database schema lacks %s, which the build expects (rebuild against this schema, or migrate the database)", object);
    same = 0;
  }
  return same;
}

/* Run once by the main thread before workers start: switching the journal to
 * WAL needs an exclusive lock, which concurrent worker opens would contend. It
 * also refuses a database whose schema is not the one the build checked. */
int fh_db_provision(const char *path, char *reason, uint32_t reason_capacity) {
  fh_db *db = fh_db_open(path, reason, reason_capacity);
  int same;
  if (db == 0) return 0;
  same = fh_schema_matches(db->connection, reason, reason_capacity);
  fh_db_close(db);
  return same;
}

void fh_db_close(fh_db *db) {
  uint32_t i;
  if (db == 0) return;
  for (i = 0u; i < FH_DB_STATEMENTS; ++i) {
    if (db->statements[i].statement != 0) sqlite3_finalize(db->statements[i].statement);
    free(db->statements[i].sql);
  }
  if (db->connection != 0) sqlite3_close_v2(db->connection);
  free(db);
}

uint32_t fh_db_prepared_count(const fh_db *db) { return db == 0 ? 0u : db->count; }

/* Prepared once per worker per SQL text (open addressing), then reused. */
static int fh_statement_for(fh_db *db, const uint8_t *sql, uint32_t length, sqlite3_stmt **out) {
  uint64_t hash = fh_hash(sql, length);
  uint32_t slot = (uint32_t)(hash % FH_DB_STATEMENTS), probe;
  for (probe = 0u; probe < FH_DB_STATEMENTS; ++probe) {
    fh_statement *entry = &db->statements[(slot + probe) % FH_DB_STATEMENTS];
    if (entry->statement == 0) {
      sqlite3_stmt *statement = 0;
      int rc;
      if (db->count + 1u >= FH_DB_STATEMENTS) return SQLITE_RANGE;
      rc = sqlite3_prepare_v2(db->connection, (const char *)sql, (int)length, &statement, 0);
      if (rc != SQLITE_OK) return rc;
      entry->sql = (char *)malloc(length + 1u);
      if (entry->sql == 0) { sqlite3_finalize(statement); return 7; /* SQLITE_NOMEM */ }
      memcpy(entry->sql, sql, length);
      entry->sql[length] = 0;
      entry->sql_length = length;
      entry->hash = hash;
      entry->statement = statement;
      ++db->count;
      *out = statement;
      return SQLITE_OK;
    }
    if (entry->hash == hash && entry->sql_length == length && memcmp(entry->sql, sql, length) == 0) {
      *out = entry->statement;
      return SQLITE_OK;
    }
  }
  return SQLITE_RANGE;
}

static void fh_put_db_error(fh_writer *w, int64_t code, const char *message) {
  fh_put_i64(w, 1); /* Result.error */
  fh_put_i64(w, code);
  fh_put_bytes(w, message, (uint32_t)strlen(message));
}

static void fh_put_sqlite_error(fh_writer *w, fh_db *db) {
  fh_put_db_error(w, (int64_t)sqlite3_extended_errcode(db->connection), sqlite3_errmsg(db->connection));
}

/* Binds List<DbValue> in order. Returns 1 when bound; otherwise writes the
 * SqlError result and returns 0. Returns -1 for a malformed boundary value. */
static int fh_bind(fh_db *db, sqlite3_stmt *statement, fh_reader *parameters, fh_writer *w) {
  uint32_t count = fh_get_count(parameters), i;
  if (parameters->failed) return -1;
  sqlite3_reset(statement);
  sqlite3_clear_bindings(statement);
  if ((uint32_t)sqlite3_bind_parameter_count(statement) != count) {
    fh_put_db_error(w, SQLITE_RANGE, FH_PARAMETER_COUNT_MESSAGE);
    return 0;
  }
  for (i = 0u; i < count; ++i) {
    int64_t kind = fh_get_i64(parameters);
    int64_t integer = fh_get_i64(parameters);
    uint32_t text_length, blob_length;
    const uint8_t *text = fh_get_bytes(parameters, &text_length);
    const uint8_t *blob = fh_get_bytes(parameters, &blob_length);
    int rc;
    if (parameters->failed) return -1;
    switch (kind) {
      case FH_KIND_INT: rc = sqlite3_bind_int64(statement, (int)i + 1, integer); break;
      case FH_KIND_TEXT: rc = sqlite3_bind_text(statement, (int)i + 1, (const char *)text, (int)text_length, SQLITE_STATIC); break;
      case FH_KIND_BLOB: rc = sqlite3_bind_blob(statement, (int)i + 1, blob_length ? (const void *)blob : (const void *)"", (int)blob_length, SQLITE_STATIC); break;
      case FH_KIND_NULL: rc = sqlite3_bind_null(statement, (int)i + 1); break;
      default: return -1;
    }
    if (rc != SQLITE_OK) { fh_put_sqlite_error(w, db); return 0; }
  }
  return 1;
}

static void fh_put_row(fh_writer *w, sqlite3_stmt *statement) {
  int columns = sqlite3_column_count(statement), column;
  fh_put_list_header(w, (uint32_t)columns);
  for (column = 0; column < columns; ++column) {
    int type = sqlite3_column_type(statement, column);
    if (type == SQLITE_INTEGER) {
      fh_put_i64(w, FH_KIND_INT);
      fh_put_i64(w, sqlite3_column_int64(statement, column));
      fh_put_bytes(w, "", 0u);
      fh_put_bytes(w, "", 0u);
    } else if (type == SQLITE_NULL) {
      fh_put_i64(w, FH_KIND_NULL);
      fh_put_i64(w, 0);
      fh_put_bytes(w, "", 0u);
      fh_put_bytes(w, "", 0u);
    } else if (type == SQLITE_BLOB) {
      const void *blob = sqlite3_column_blob(statement, column);
      int length = sqlite3_column_bytes(statement, column);
      fh_put_i64(w, FH_KIND_BLOB);
      fh_put_i64(w, 0);
      fh_put_bytes(w, "", 0u);
      fh_put_bytes(w, blob, (uint32_t)length);
    } else { /* TEXT, and REAL rendered as text */
      const unsigned char *text = sqlite3_column_text(statement, column);
      int length = sqlite3_column_bytes(statement, column);
      fh_put_i64(w, FH_KIND_TEXT);
      fh_put_i64(w, 0);
      fh_put_bytes(w, text, (uint32_t)length);
      fh_put_bytes(w, "", 0u);
    }
  }
}

static int32_t fh_run(fh_db *db, uint32_t operation, const uint8_t *sql, uint32_t sql_length,
                      fh_reader *parameters, fh_writer *w) {
  sqlite3_stmt *statement = 0;
  int rc = fh_statement_for(db, sql, sql_length, &statement), bound;
  if (rc != SQLITE_OK) {
    if (rc == SQLITE_RANGE && statement == 0 && db->count + 1u >= FH_DB_STATEMENTS)
      fh_put_db_error(w, SQLITE_RANGE, "too many distinct SQL statements");
    else
      fh_put_sqlite_error(w, db);
    return 0;
  }
  bound = fh_bind(db, statement, parameters, w);
  if (bound < 0) return 1;
  if (bound == 0) { sqlite3_clear_bindings(statement); return 0; }
  if (operation == FH_DB_QUERY) {
    uint32_t list_at, rows = 0u;
    fh_put_i64(w, 0); /* Result.ok */
    list_at = fh_put_list_header(w, 0u);
    for (;;) {
      rc = sqlite3_step(statement);
      if (rc == SQLITE_ROW) { fh_put_row(w, statement); ++rows; if (w->failed) break; continue; }
      break;
    }
    if (w->failed) { sqlite3_reset(statement); sqlite3_clear_bindings(statement); return 1; }
    if (rc != SQLITE_DONE) {
      w->used = 0u;
      fh_put_sqlite_error(w, db);
    } else {
      fh_patch_count(w, list_at, rows);
    }
  } else {
    do { rc = sqlite3_step(statement); } while (rc == SQLITE_ROW);
    if (rc != SQLITE_DONE) fh_put_sqlite_error(w, db);
    else { fh_put_i64(w, 0); fh_put_i64(w, (int64_t)sqlite3_changes(db->connection)); }
  }
  sqlite3_reset(statement);
  sqlite3_clear_bindings(statement);
  return 0;
}

/* ---------------------------------------------------------------- db.rows */

/* The extent of a record field value of a supported kind (Int, Bool, String,
 * Bytes, or an Option of one), reading tags and length headers in place. */
static int fh_field_extent(const al_owning_layout *layout, uint32_t type_index,
                           const uint8_t *data, uint32_t at, uint32_t end, uint32_t *extent) {
  const al_owning_type_descriptor *type;
  uint32_t length;
  if (type_index >= layout->type_count || at > end || end - at < 8u) return 0;
  type = &layout->types[type_index];
  switch (type->kind) {
    case AL_OWNING_TYPE_I64: case AL_OWNING_TYPE_BOOL: *extent = 8u; return 1;
    case AL_OWNING_TYPE_STRING: case AL_OWNING_TYPE_BYTES:
      memcpy(&length, data + at, 4);
      *extent = fh_pad8(8u + length);
      return *extent <= end - at;
    case AL_OWNING_TYPE_OPTION: {
      int64_t tag;
      uint32_t inner;
      memcpy(&tag, data + at, 8);
      if (tag == 1) { *extent = 8u; return 1; }
      if (tag != 0 || !fh_field_extent(layout, layout->fields[type->first_field].child_type_index, data, at + 8u, end, &inner)) return 0;
      *extent = 8u + inner;
      return 1;
    }
    default: return 0;
  }
}

/* Binds one parameter value at data[at] of the given type to SQL parameter index. */
static int fh_bind_field(sqlite3_stmt *statement, int index, const al_owning_layout *layout,
                         uint32_t type_index, const uint8_t *data, uint32_t at) {
  const al_owning_type_descriptor *type = &layout->types[type_index];
  int64_t number;
  uint32_t length;
  switch (type->kind) {
    case AL_OWNING_TYPE_I64: case AL_OWNING_TYPE_BOOL:
      memcpy(&number, data + at, 8);
      return sqlite3_bind_int64(statement, index, number);
    case AL_OWNING_TYPE_STRING:
      memcpy(&length, data + at, 4);
      return sqlite3_bind_text(statement, index, (const char *)(data + at + 8u), (int)length, SQLITE_STATIC);
    case AL_OWNING_TYPE_BYTES:
      memcpy(&length, data + at, 4);
      return sqlite3_bind_blob(statement, index, length ? (const void *)(data + at + 8u) : (const void *)"", (int)length, SQLITE_STATIC);
    case AL_OWNING_TYPE_OPTION:
      memcpy(&number, data + at, 8);
      if (number == 1) return sqlite3_bind_null(statement, index);
      return fh_bind_field(statement, index, layout, layout->fields[type->first_field].child_type_index, data, at + 8u);
    default: return SQLITE_MISUSE;
  }
}

#define FH_DB_ROWS_MAX_FIELDS 64u

/* Binds the params record's fields in `order` ("2,0,1": field indexes, one per
 * SQL parameter). Returns 1 when bound, 0 after writing an SqlError, -1 on a
 * malformed input. */
static int fh_rows_bind(fh_db *db, sqlite3_stmt *statement, const al_owning_layout *layout,
                        const fh_host_input *params, const uint8_t *order, uint32_t order_length, fh_writer *w) {
  const al_owning_type_descriptor *record;
  uint32_t offsets[FH_DB_ROWS_MAX_FIELDS];
  uint32_t at = 0u, field, count = 0u, k = 0u;
  if (params->type_index >= layout->type_count) return -1;
  record = &layout->types[params->type_index];
  if (record->kind != AL_OWNING_TYPE_RECORD || record->field_count > FH_DB_ROWS_MAX_FIELDS) return -1;
  for (field = 0u; field < record->field_count; ++field) {
    uint32_t extent;
    offsets[field] = at;
    if (!fh_field_extent(layout, layout->fields[record->first_field + field].child_type_index, params->data, at, params->extent_bytes, &extent)) return -1;
    at += extent;
  }
  sqlite3_reset(statement);
  sqlite3_clear_bindings(statement);
  while (k < order_length) {
    uint32_t index = 0u;
    int rc;
    while (k < order_length && order[k] >= '0' && order[k] <= '9') index = index * 10u + (uint32_t)(order[k++] - '0');
    if (k < order_length) ++k; /* the comma */
    if (index >= record->field_count) return -1;
    ++count;
    if (count > (uint32_t)sqlite3_bind_parameter_count(statement)) break;
    rc = fh_bind_field(statement, (int)count, layout, layout->fields[record->first_field + index].child_type_index, params->data, offsets[index]);
    if (rc != SQLITE_OK) { fh_put_sqlite_error(w, db); return 0; }
  }
  if ((uint32_t)sqlite3_bind_parameter_count(statement) != count) {
    fh_put_db_error(w, SQLITE_RANGE, FH_PARAMETER_COUNT_MESSAGE);
    return 0;
  }
  return 1;
}

static const char *fh_column_kind(int type) {
  return type == SQLITE_NULL ? "null" : type == SQLITE_INTEGER ? "integer" : type == SQLITE_BLOB ? "blob" : "text";
}

static const char *fh_expected_kind(uint32_t kind) {
  return kind == AL_OWNING_TYPE_I64 ? "Int" : kind == AL_OWNING_TYPE_BOOL ? "Bool" : kind == AL_OWNING_TYPE_STRING ? "String" : kind == AL_OWNING_TYPE_BYTES ? "Bytes" : "a supported type";
}

/* Writes one column into the Row layout; on a mismatch fills `problem` (the
 * interpreter's DbRows.decode message) and returns 0. */
static int fh_rows_column(fh_writer *w, sqlite3_stmt *statement, int column, const al_owning_layout *layout,
                          uint32_t type_index, char *problem, size_t problem_capacity) {
  const al_owning_type_descriptor *type = &layout->types[type_index];
  int sql_type = sqlite3_column_type(statement, column);
  uint32_t kind = type->kind;
  if (kind == AL_OWNING_TYPE_OPTION) {
    if (sql_type == SQLITE_NULL) { fh_put_i64(w, 1); return 1; }
    fh_put_i64(w, 0);
    type_index = layout->fields[type->first_field].child_type_index;
    kind = layout->types[type_index].kind;
  }
  if (kind == AL_OWNING_TYPE_I64 && sql_type == SQLITE_INTEGER) {
    fh_put_i64(w, sqlite3_column_int64(statement, column));
    return 1;
  }
  if (kind == AL_OWNING_TYPE_BOOL && sql_type == SQLITE_INTEGER) {
    int64_t value = sqlite3_column_int64(statement, column);
    if (value == 0 || value == 1) { fh_put_i64(w, value); return 1; }
    snprintf(problem, problem_capacity, "column %d: expected Bool, got integer other than 0 or 1", column + 1);
    return 0;
  }
  /* TEXT, and REAL rendered as text, as db.query does. */
  if (kind == AL_OWNING_TYPE_STRING && (sql_type == SQLITE_TEXT || sql_type == SQLITE_FLOAT)) {
    const unsigned char *text = sqlite3_column_text(statement, column);
    fh_put_bytes(w, text, (uint32_t)sqlite3_column_bytes(statement, column));
    return 1;
  }
  if (kind == AL_OWNING_TYPE_BYTES && sql_type == SQLITE_BLOB) {
    fh_put_bytes(w, sqlite3_column_blob(statement, column), (uint32_t)sqlite3_column_bytes(statement, column));
    return 1;
  }
  snprintf(problem, problem_capacity, "column %d: expected %s, got %s", column + 1, fh_expected_kind(kind), fh_column_kind(sql_type));
  return 0;
}

/* db.rows<Row>: binds the params record, then writes Result<List<Row>, SqlError>
 * in the output type's layout. Same answers as the interpreter's DbParams,
 * db.query and DbDecode. */
static int32_t fh_rows(fh_db *db, const al_owning_layout *layout, const fh_host_input *inputs,
                       uint32_t output_type_index, fh_writer *w) {
  fh_reader reader;
  const uint8_t *sql, *order;
  uint32_t sql_length, order_length, rows = 0u, list_at;
  const al_owning_type_descriptor *result, *list, *row;
  sqlite3_stmt *statement = 0;
  int rc, bound;
  char problem[160];
  reader.data = inputs[0].data; reader.extent = inputs[0].extent_bytes; reader.at = 0u; reader.failed = 0;
  sql = fh_get_bytes(&reader, &sql_length);
  reader.data = inputs[2].data; reader.extent = inputs[2].extent_bytes; reader.at = 0u; reader.failed = 0;
  order = fh_get_bytes(&reader, &order_length);
  if (sql == 0 || order == 0 || output_type_index >= layout->type_count) return 1;
  result = &layout->types[output_type_index];
  if (result->kind != AL_OWNING_TYPE_RESULT) return 1;
  list = &layout->types[layout->fields[result->first_field].child_type_index];
  if (list->kind != AL_OWNING_TYPE_LIST) return 1;
  row = &layout->types[layout->fields[list->first_field].child_type_index];
  if (row->kind != AL_OWNING_TYPE_RECORD) return 1;
  rc = fh_statement_for(db, sql, sql_length, &statement);
  if (rc != SQLITE_OK) {
    if (rc == SQLITE_RANGE && statement == 0 && db->count + 1u >= FH_DB_STATEMENTS)
      fh_put_db_error(w, SQLITE_RANGE, "too many distinct SQL statements");
    else
      fh_put_sqlite_error(w, db);
    return 0;
  }
  bound = fh_rows_bind(db, statement, layout, &inputs[1], order, order_length, w);
  if (bound < 0) return 1;
  if (bound == 0) { sqlite3_clear_bindings(statement); return 0; }
  fh_put_i64(w, 0); /* Result.ok */
  list_at = fh_put_list_header(w, 0u);
  problem[0] = 0;
  for (;;) {
    uint32_t field;
    rc = sqlite3_step(statement);
    if (rc != SQLITE_ROW) break;
    if ((uint32_t)sqlite3_column_count(statement) != row->field_count) {
      snprintf(problem, sizeof problem, "a row has %d columns; the row type has %u fields", sqlite3_column_count(statement), row->field_count);
      break;
    }
    for (field = 0u; field < row->field_count && problem[0] == 0; ++field)
      fh_rows_column(w, statement, (int)field, layout, layout->fields[row->first_field + field].child_type_index, problem, sizeof problem);
    if (problem[0] != 0 || w->failed) break;
    ++rows;
  }
  if (w->failed) { sqlite3_reset(statement); sqlite3_clear_bindings(statement); return 1; }
  if (problem[0] != 0) {
    w->used = 0u;
    fh_put_db_error(w, 20, problem); /* SQLITE_MISMATCH */
  } else if (rc != SQLITE_DONE) {
    w->used = 0u;
    fh_put_sqlite_error(w, db);
  } else {
    fh_patch_count(w, list_at, rows);
  }
  sqlite3_reset(statement);
  sqlite3_clear_bindings(statement);
  return 0;
}

/* cache.get(key) -> Option<String> (a hit's bytes are copied straight into
 * the output, which is the request arena); cache.put(key, value) -> Bool. */
static int32_t fh_cache_host(uint32_t worker, uint32_t operation, const fh_host_input *inputs,
                             uint32_t input_count, fh_writer *w) {
  fh_reader key_reader, value_reader;
  const uint8_t *key, *value;
  uint32_t key_length, value_length;
  if (inputs == 0 || input_count != (operation == FH_CACHE_GET ? 1u : 2u)) return 1;
  key_reader.data = inputs[0].data; key_reader.extent = inputs[0].extent_bytes; key_reader.at = 0u; key_reader.failed = 0;
  key = fh_get_bytes(&key_reader, &key_length);
  if (key_reader.failed) return 1;
  if (operation == FH_CACHE_GET) {
    if (fh_cache_get(worker, key, key_length, &value, &value_length)) {
      fh_put_i64(w, 0); /* some */
      fh_put_bytes(w, value, value_length);
    } else {
      fh_put_i64(w, 1); /* none */
    }
    return 0;
  }
  value_reader.data = inputs[1].data; value_reader.extent = inputs[1].extent_bytes; value_reader.at = 0u; value_reader.failed = 0;
  value = fh_get_bytes(&value_reader, &value_length);
  if (value_reader.failed) return 1;
  fh_put_i64(w, fh_cache_put(worker, key, key_length, value, value_length) ? 1 : 0);
  return 0;
}

/* ------------------------------------------------------------ worker store
 * Host operations of src/AgentLang.FlowHttp/Store.fs (tags 5-17) over the
 * worker's own store and response stream. Every offset is bounds-checked and
 * Int accesses must be 8-byte aligned; a failed check is none/false. */
#define FH_STORE_CAPACITY 5u
#define FH_STORE_INT 6u
#define FH_STORE_INTS 7u
#define FH_STORE_SET_INT 8u
#define FH_STORE_WRITE 9u
#define FH_STORE_BYTES 10u
#define FH_STORE_TEXT 11u
#define FH_STORE_MATCHES 12u
#define FH_STORE_HASH 13u
#define FH_RESPONSE_WRITE 14u
#define FH_RESPONSE_WRITE_STORE 15u
#define FH_RESPONSE_WRITTEN 16u
#define FH_RESPONSE_DISCARD 17u
#define FH_RESPONSE_WRITE_FILE 18u

static int fh_input_i64(const fh_host_input *input, int64_t *value) {
  if (input->extent_bytes < 8u) return 0;
  memcpy(value, input->data, 8);
  return 1;
}

static const uint8_t *fh_input_bytes(const fh_host_input *input, uint32_t *length) {
  fh_reader r;
  const uint8_t *data;
  r.data = input->data; r.extent = input->extent_bytes; r.at = 0u; r.failed = 0;
  data = fh_get_bytes(&r, length);
  return r.failed ? 0 : data;
}

static int fh_store_range(const fh_provider *p, int64_t offset, int64_t length) {
  return offset >= 0 && length >= 0 && (uint64_t)offset <= p->store_bytes &&
         (uint64_t)length <= p->store_bytes - (uint64_t)offset;
}

static int fh_store_aligned(const fh_provider *p, int64_t offset) {
  return (offset & 7) == 0 && fh_store_range(p, offset, 8);
}

static int fh_stream_append(fh_provider *p, const uint8_t *data, uint32_t length) {
  if (length > p->stream_capacity - p->stream_used) return 0;
  if (length > 0u) memcpy(p->stream + p->stream_used, data, length);
  p->stream_used += length;
  return 1;
}

/* Plain relative segments of [A-Za-z0-9._-]: no root, no empty, "." or ".." segment. */
static int fh_safe_relative(const uint8_t *path, uint32_t length) {
  uint32_t i, start = 0u;
  if (length == 0u || length > 512u) return 0;
  for (i = 0u; i <= length; ++i) {
    if (i == length || path[i] == '/') {
      uint32_t n = i - start;
      if (n == 0u || (n == 1u && path[start] == '.') || (n == 2u && path[start] == '.' && path[start + 1u] == '.')) return 0;
      start = i + 1u;
    } else {
      uint8_t c = path[i];
      if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-')) return 0;
    }
  }
  return 1;
}

/* response.write-file: the file at files_root/path appended to the stream, whole or not at all. */
static int fh_stream_file(fh_provider *p, const uint8_t *path, uint32_t length) {
  char full[1200];
  size_t root_length, got;
  FILE *f;
  uint32_t before = p->stream_used;
  int ok = 1;
  if (p->files_root == 0 || !fh_safe_relative(path, length)) return 0;
  root_length = strlen(p->files_root);
  if (root_length + 1u + length + 1u > sizeof(full)) return 0;
  memcpy(full, p->files_root, root_length);
  full[root_length] = '/';
  memcpy(full + root_length + 1u, path, length);
  full[root_length + 1u + length] = 0;
  f = fopen(full, "rb");
  if (f == 0) return 0;
  for (;;) {
    uint32_t room = p->stream_capacity - p->stream_used;
    if (room == 0u) { int extra = fgetc(f); if (extra != EOF) ok = 0; break; }
    got = fread(p->stream + p->stream_used, 1u, room, f);
    p->stream_used += (uint32_t)got;
    if (got < room) { if (ferror(f)) ok = 0; break; }
  }
  fclose(f);
  if (!ok) p->stream_used = before;
  return ok;
}

static int32_t fh_store_host(fh_provider *p, uint32_t operation, const fh_host_input *inputs,
                             uint32_t input_count, fh_writer *w) {
  int64_t a = 0, b = 0;
  const uint8_t *text;
  uint32_t text_length = 0u;
  if (inputs == 0 || input_count == 0u || input_count > 2u) return 1;
  switch (operation) {
    case FH_STORE_CAPACITY:
      fh_put_i64(w, (int64_t)p->store_bytes);
      return 0;
    case FH_STORE_INT:
      if (!fh_input_i64(&inputs[0], &a)) return 1;
      if (fh_store_aligned(p, a)) { int64_t v; memcpy(&v, p->store + a, 8); fh_put_i64(w, 0); fh_put_i64(w, v); }
      else fh_put_i64(w, 1);
      return 0;
    case FH_STORE_INTS:
      if (input_count != 2u || !fh_input_i64(&inputs[0], &a) || !fh_input_i64(&inputs[1], &b)) return 1;
      if (b >= 0 && b <= 64 && (a & 7) == 0 && fh_store_range(p, a, b * 8)) {
        int64_t i;
        fh_put_i64(w, 0);
        fh_put_list_header(w, (uint32_t)b);
        for (i = 0; i < b; ++i) { int64_t v; memcpy(&v, p->store + a + i * 8, 8); fh_put_i64(w, v); }
      } else {
        fh_put_i64(w, 1);
      }
      return 0;
    case FH_STORE_SET_INT:
      if (input_count != 2u || !fh_input_i64(&inputs[0], &a) || !fh_input_i64(&inputs[1], &b)) return 1;
      if (fh_store_aligned(p, a)) { memcpy(p->store + a, &b, 8); fh_put_i64(w, 1); }
      else fh_put_i64(w, 0);
      return 0;
    case FH_STORE_WRITE:
    case FH_STORE_MATCHES:
      if (input_count != 2u || !fh_input_i64(&inputs[0], &a) || (text = fh_input_bytes(&inputs[1], &text_length)) == 0) return 1;
      if (!fh_store_range(p, a, text_length)) { fh_put_i64(w, 0); return 0; }
      if (operation == FH_STORE_WRITE) {
        if (text_length > 0u) memcpy(p->store + a, text, text_length);
        fh_put_i64(w, 1);
      } else {
        fh_put_i64(w, text_length == 0u || memcmp(p->store + a, text, text_length) == 0 ? 1 : 0);
      }
      return 0;
    case FH_STORE_BYTES:
    case FH_STORE_TEXT:
      if (input_count != 2u || !fh_input_i64(&inputs[0], &a) || !fh_input_i64(&inputs[1], &b)) return 1;
      /* Text is validated by the runtime when it commits the host output. */
      if (fh_store_range(p, a, b) && b <= 0x7fffffff) { fh_put_i64(w, 0); fh_put_bytes(w, p->store + a, (uint32_t)b); }
      else fh_put_i64(w, 1);
      return 0;
    case FH_STORE_HASH: {
      uint64_t hash = 14695981039346656037ull;
      uint32_t i;
      if ((text = fh_input_bytes(&inputs[0], &text_length)) == 0) return 1;
      for (i = 0u; i < text_length; ++i) hash = (hash ^ text[i]) * 1099511628211ull;
      fh_put_i64(w, (int64_t)hash);
      return 0;
    }
    case FH_RESPONSE_WRITE:
      if ((text = fh_input_bytes(&inputs[0], &text_length)) == 0) return 1;
      fh_put_i64(w, fh_stream_append(p, text, text_length) ? 1 : 0);
      return 0;
    case FH_RESPONSE_WRITE_STORE:
      if (input_count != 2u || !fh_input_i64(&inputs[0], &a) || !fh_input_i64(&inputs[1], &b)) return 1;
      fh_put_i64(w, fh_store_range(p, a, b) && b <= 0x7fffffff && fh_stream_append(p, p->store + a, (uint32_t)b) ? 1 : 0);
      return 0;
    case FH_RESPONSE_WRITTEN:
      fh_put_bytes(w, p->stream, p->stream_used);
      return 0;
    case FH_RESPONSE_DISCARD:
      fh_put_i64(w, (int64_t)p->stream_used);
      p->stream_used = 0u;
      return 0;
    case FH_RESPONSE_WRITE_FILE:
      if ((text = fh_input_bytes(&inputs[0], &text_length)) == 0) return 1;
      fh_put_i64(w, fh_stream_file(p, text, text_length) ? 1 : 0);
      return 0;
    default:
      return 1;
  }
}

int32_t al_host_flow_http_v1(void *provider_context, uint32_t operation,
                             const al_owning_layout *layout,
                             const fh_host_input *inputs, uint32_t input_count,
                             uint32_t output_type_index, uint8_t *output,
                             uint32_t output_capacity_bytes,
                             uint32_t *output_extent_bytes) {
  fh_provider *provider = (fh_provider *)provider_context;
  fh_db *db = provider == 0 ? 0 : provider->db;
  fh_reader sql_reader, parameters;
  fh_writer w;
  const uint8_t *sql;
  uint32_t sql_length;
  int32_t status;
  if (provider != 0 && output != 0 && operation >= FH_STORE_CAPACITY && operation <= FH_RESPONSE_WRITE_FILE) {
    w.data = output; w.capacity = output_capacity_bytes; w.used = 0u; w.failed = 0;
    status = fh_store_host(provider, operation, inputs, input_count, &w);
    if (status != 0 || w.failed) return 1;
    *output_extent_bytes = w.used;
    return 0;
  }
  if (provider != 0 && output != 0 && (operation == FH_CACHE_GET || operation == FH_CACHE_PUT)) {
    w.data = output; w.capacity = output_capacity_bytes; w.used = 0u; w.failed = 0;
    status = fh_cache_host(provider->worker, operation, inputs, input_count, &w);
    if (status != 0 || w.failed) return 1;
    *output_extent_bytes = w.used;
    return 0;
  }
  if (operation == FH_DB_ROWS) {
    if (db == 0 || input_count != 3u || inputs == 0 || output == 0 || layout == 0) return 1;
    w.data = output; w.capacity = output_capacity_bytes; w.used = 0u; w.failed = 0;
    status = fh_rows(db, layout, inputs, output_type_index, &w);
    if (status != 0 || w.failed) return 1;
    *output_extent_bytes = w.used;
    return 0;
  }
  if (db == 0 || input_count != 2u || inputs == 0 || output == 0 ||
      (operation != FH_DB_QUERY && operation != FH_DB_EXECUTE))
    return 1;
  sql_reader.data = inputs[0].data; sql_reader.extent = inputs[0].extent_bytes; sql_reader.at = 0u; sql_reader.failed = 0;
  parameters.data = inputs[1].data; parameters.extent = inputs[1].extent_bytes; parameters.at = 0u; parameters.failed = 0;
  sql = fh_get_bytes(&sql_reader, &sql_length);
  if (sql_reader.failed) return 1;
  w.data = output; w.capacity = output_capacity_bytes; w.used = 0u; w.failed = 0;
  status = fh_run(db, operation, sql, sql_length, &parameters, &w);
  if (status != 0 || w.failed) return 1;
  *output_extent_bytes = w.used;
  return 0;
}
