#ifndef AGENTLANG_CAMPFIRE_ROOM_READ_SQLITE_SUBSET_H
#define AGENTLANG_CAMPFIRE_ROOM_READ_SQLITE_SUBSET_H

/* Curated declarations for the stable SQLite C functions used by the fixed
 * Campfire provider. Keep these synchronized with room_read_sqlite.def. */

#include <stdint.h>

#if defined(_MSC_VER) || defined(__clang__)
#define SQLITE_API __declspec(dllimport)
#else
#define SQLITE_API
#endif

typedef struct sqlite3 sqlite3;
typedef struct sqlite3_stmt sqlite3_stmt;
typedef int64_t sqlite3_int64;
typedef void (*sqlite3_destructor_type)(void *);

#define SQLITE_OK 0
#define SQLITE_ERROR 1
#define SQLITE_NOMEM 7
#define SQLITE_CONSTRAINT 19
#define SQLITE_ROW 100
#define SQLITE_DONE 101
#define SQLITE_INTEGER 1
#define SQLITE_FLOAT 2
#define SQLITE_TEXT 3
#define SQLITE_BLOB 4
#define SQLITE_NULL 5

#define SQLITE_OPEN_READWRITE 0x00000002
#define SQLITE_OPEN_NOMUTEX 0x00008000
#define SQLITE_OPEN_PRIVATECACHE 0x00040000

#define SQLITE_DBCONFIG_ENABLE_FKEY 1002

/* The database is Windows x64 and input strings are UTF-16LE owning strings. */
#define SQLITE_TRANSIENT ((sqlite3_destructor_type)(intptr_t)-1)

SQLITE_API int sqlite3_open_v2(const char *filename, sqlite3 **database,
                               int flags, const char *vfs);
SQLITE_API int sqlite3_close(sqlite3 *database);
SQLITE_API int sqlite3_get_autocommit(sqlite3 *database);
SQLITE_API int sqlite3_libversion_number(void);
SQLITE_API int sqlite3_prepare_v2(sqlite3 *database, const char *sql,
                                  int byte_count, sqlite3_stmt **statement,
                                  const char **tail);
SQLITE_API int sqlite3_bind_int64(sqlite3_stmt *statement, int index,
                                  sqlite3_int64 value);
SQLITE_API int sqlite3_bind_text(sqlite3_stmt *statement, int index,
                                 const void *value, int byte_count,
                                 sqlite3_destructor_type destructor);
SQLITE_API int sqlite3_bind_null(sqlite3_stmt *statement, int index);
SQLITE_API int sqlite3_step(sqlite3_stmt *statement);
SQLITE_API int sqlite3_reset(sqlite3_stmt *statement);
SQLITE_API int sqlite3_clear_bindings(sqlite3_stmt *statement);
SQLITE_API int sqlite3_finalize(sqlite3_stmt *statement);
SQLITE_API int sqlite3_changes(sqlite3 *database);
SQLITE_API int sqlite3_column_type(sqlite3_stmt *statement, int column);
SQLITE_API sqlite3_int64 sqlite3_column_int64(sqlite3_stmt *statement,
                                               int column);
SQLITE_API const unsigned char *sqlite3_column_text(sqlite3_stmt *statement,
                                                     int column);
SQLITE_API int sqlite3_column_bytes(sqlite3_stmt *statement, int column);
SQLITE_API int sqlite3_errcode(sqlite3 *database);
SQLITE_API int sqlite3_extended_errcode(sqlite3 *database);
SQLITE_API int sqlite3_extended_result_codes(sqlite3 *database, int enabled);
SQLITE_API int sqlite3_busy_timeout(sqlite3 *database, int milliseconds);
SQLITE_API int sqlite3_db_config(sqlite3 *database, int option, ...);

#undef SQLITE_API

#endif
