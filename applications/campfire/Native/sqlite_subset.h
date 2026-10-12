#ifndef AGENTLANG_CAMPFIRE_SQLITE_SUBSET_H
#define AGENTLANG_CAMPFIRE_SQLITE_SUBSET_H

/* This intentionally small declaration set matches only the stable public
 * SQLite C calls used by the fixed Campfire adapter. The binary comes from the
 * hash-pinned SQLitePCLRaw.lib.e_sqlite3 NuGet package; it does not ship a C
 * header or import library. Keep declarations paired with e_sqlite3.def. */

#include <stdint.h>

#if defined(_MSC_VER) || defined(__clang__)
#define SQLITE_API __declspec(dllimport)
#else
#define SQLITE_API
#endif

typedef struct sqlite3 sqlite3;
typedef struct sqlite3_stmt sqlite3_stmt;
typedef int64_t sqlite3_int64;

#define SQLITE_OK 0
#define SQLITE_ERROR 1
#define SQLITE_NOMEM 7
#define SQLITE_ROW 100
#define SQLITE_DONE 101
#define SQLITE_NULL 5

#define SQLITE_OPEN_READWRITE 0x00000002
#define SQLITE_OPEN_NOMUTEX 0x00008000
#define SQLITE_OPEN_PRIVATECACHE 0x00040000

#define SQLITE_DBCONFIG_ENABLE_FKEY 1002

SQLITE_API int sqlite3_open_v2(const char *filename, sqlite3 **database,
                               int flags, const char *vfs);
SQLITE_API int sqlite3_close(sqlite3 *database);
SQLITE_API int sqlite3_prepare_v2(sqlite3 *database, const char *sql,
                                  int byte_count, sqlite3_stmt **statement,
                                  const char **tail);
SQLITE_API int sqlite3_bind_int64(sqlite3_stmt *statement, int index,
                                  sqlite3_int64 value);
SQLITE_API int sqlite3_step(sqlite3_stmt *statement);
SQLITE_API int sqlite3_reset(sqlite3_stmt *statement);
SQLITE_API int sqlite3_clear_bindings(sqlite3_stmt *statement);
SQLITE_API int sqlite3_finalize(sqlite3_stmt *statement);
SQLITE_API int sqlite3_column_type(sqlite3_stmt *statement, int column);
SQLITE_API sqlite3_int64 sqlite3_column_int64(sqlite3_stmt *statement,
                                               int column);
SQLITE_API const void *sqlite3_column_text16(sqlite3_stmt *statement,
                                              int column);
SQLITE_API int sqlite3_column_bytes16(sqlite3_stmt *statement, int column);
SQLITE_API int sqlite3_errcode(sqlite3 *database);
SQLITE_API int sqlite3_extended_errcode(sqlite3 *database);
SQLITE_API int sqlite3_extended_result_codes(sqlite3 *database, int enabled);
SQLITE_API int sqlite3_busy_timeout(sqlite3 *database, int milliseconds);
SQLITE_API int sqlite3_db_config(sqlite3 *database, int option, ...);

/* Included in the curated import library for host-side dependency auditing.
 * Adapter code does not expose a generic SQLite function table to AgentLang. */
SQLITE_API const char *sqlite3_libversion(void);
SQLITE_API const char *sqlite3_sourceid(void);
SQLITE_API int sqlite3_compileoption_used(const char *option);

#undef SQLITE_API

#endif
