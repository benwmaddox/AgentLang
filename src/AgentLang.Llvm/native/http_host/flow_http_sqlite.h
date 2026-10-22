/* The SQLite C API subset the Flow HTTP host uses, declared here so neither
 * OS needs SQLite development headers. Windows links e_sqlite3.dll (import
 * library from flow_http_sqlite.def); Linux links the system libsqlite3.so.0. */
#ifndef FLOW_HTTP_SQLITE_H
#define FLOW_HTTP_SQLITE_H

#include <stdint.h>

typedef struct sqlite3 sqlite3;
typedef struct sqlite3_stmt sqlite3_stmt;

#define SQLITE_OK 0
#define SQLITE_RANGE 25
#define SQLITE_MISUSE 21
#define SQLITE_ROW 100
#define SQLITE_DONE 101
#define SQLITE_INTEGER 1
#define SQLITE_FLOAT 2
#define SQLITE_TEXT 3
#define SQLITE_BLOB 4
#define SQLITE_NULL 5
#define SQLITE_OPEN_READWRITE 0x00000002
#define SQLITE_OPEN_CREATE 0x00000004
#define SQLITE_OPEN_NOMUTEX 0x00008000
#define SQLITE_OPEN_PRIVATECACHE 0x00040000
#define SQLITE_STATIC ((void (*)(void *))0)
#define SQLITE_CONFIG_MEMSTATUS 9

int sqlite3_config(int option, ...);

int sqlite3_open_v2(const char *filename, sqlite3 **db, int flags, const char *vfs);
int sqlite3_close_v2(sqlite3 *db);
int sqlite3_extended_result_codes(sqlite3 *db, int onoff);
int sqlite3_busy_timeout(sqlite3 *db, int ms);
int sqlite3_exec(sqlite3 *db, const char *sql, int (*callback)(void *, int, char **, char **), void *arg, char **errmsg);
int sqlite3_prepare_v2(sqlite3 *db, const char *sql, int bytes, sqlite3_stmt **statement, const char **tail);
int sqlite3_step(sqlite3_stmt *statement);
int sqlite3_reset(sqlite3_stmt *statement);
int sqlite3_clear_bindings(sqlite3_stmt *statement);
int sqlite3_finalize(sqlite3_stmt *statement);
int sqlite3_bind_parameter_count(sqlite3_stmt *statement);
int sqlite3_bind_int64(sqlite3_stmt *statement, int index, int64_t value);
int sqlite3_bind_text(sqlite3_stmt *statement, int index, const char *text, int bytes, void (*destructor)(void *));
int sqlite3_bind_blob(sqlite3_stmt *statement, int index, const void *data, int bytes, void (*destructor)(void *));
int sqlite3_bind_null(sqlite3_stmt *statement, int index);
int sqlite3_column_count(sqlite3_stmt *statement);
int sqlite3_column_type(sqlite3_stmt *statement, int column);
int64_t sqlite3_column_int64(sqlite3_stmt *statement, int column);
const unsigned char *sqlite3_column_text(sqlite3_stmt *statement, int column);
const void *sqlite3_column_blob(sqlite3_stmt *statement, int column);
int sqlite3_column_bytes(sqlite3_stmt *statement, int column);
int sqlite3_changes(sqlite3 *db);
const char *sqlite3_errmsg(sqlite3 *db);
int sqlite3_extended_errcode(sqlite3 *db);

#endif
