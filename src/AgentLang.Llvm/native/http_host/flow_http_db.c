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

#define FH_DB_STATEMENTS 256u
#define FH_DB_QUERY 1u
#define FH_DB_EXECUTE 2u
#define FH_CACHE_GET 3u /* src/AgentLang.FlowHttp/Cache.fs nativeOperations */
#define FH_CACHE_PUT 4u
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

/* Run once by the main thread before workers start: switching the journal to
 * WAL needs an exclusive lock, which concurrent worker opens would contend. */
int fh_db_provision(const char *path, char *reason, uint32_t reason_capacity) {
  fh_db *db = fh_db_open(path, reason, reason_capacity);
  if (db == 0) return 0;
  fh_db_close(db);
  return 1;
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

int32_t al_host_flow_http_v1(void *provider_context, uint32_t operation,
                             const al_owning_layout *layout,
                             const fh_host_input *inputs, uint32_t input_count,
                             uint32_t output_type_index, uint8_t *output,
                             uint32_t output_capacity_bytes,
                             uint32_t *output_extent_bytes) {
  const fh_provider *provider = (const fh_provider *)provider_context;
  fh_db *db = provider == 0 ? 0 : provider->db;
  fh_reader sql_reader, parameters;
  fh_writer w;
  const uint8_t *sql;
  uint32_t sql_length;
  int32_t status;
  (void)layout;
  (void)output_type_index;
  if (provider != 0 && output != 0 && (operation == FH_CACHE_GET || operation == FH_CACHE_PUT)) {
    w.data = output; w.capacity = output_capacity_bytes; w.used = 0u; w.failed = 0;
    status = fh_cache_host(provider->worker, operation, inputs, input_count, &w);
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
