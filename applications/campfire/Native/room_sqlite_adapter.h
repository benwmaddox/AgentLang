#ifndef AGENTLANG_CAMPFIRE_ROOM_SQLITE_ADAPTER_H
#define AGENTLANG_CAMPFIRE_ROOM_SQLITE_ADAPTER_H

#include <stdint.h>

#define AL_CAMPFIRE_ROOM_SQLITE_CONTEXT_ABI_VERSION 1u
#define AL_CAMPFIRE_ROOM_SQLITE_CONTEXT_BYTES 32u
#define AL_CAMPFIRE_ROOM_SQLITE_PATH_LIMIT_BYTES 131072u

enum al_campfire_db_error_tag_v1 {
  AL_CAMPFIRE_DB_ERROR_UNAVAILABLE = 0u,
  AL_CAMPFIRE_DB_ERROR_NOT_FOUND = 1u,
  AL_CAMPFIRE_DB_ERROR_CONSTRAINT_CONFLICT = 2u,
  AL_CAMPFIRE_DB_ERROR_INVALID_INPUT = 3u
};

/* Caller-owned, zero-initialized, per-worker SQLite state. Keep this layout
 * stable for ABI v1; the SQLite handles are opaque to the host. One context
 * must never be used concurrently or from a different thread than the one
 * that initialized it. */
typedef struct al_campfire_room_sqlite_context_v1 {
  void *opaque_db;
  void *opaque_find_stmt;
  uint64_t cookie;
  uint32_t owner_thread_id;
  uint32_t active;
} al_campfire_room_sqlite_context_v1;

#ifndef AL_CAMPFIRE_EXPORT
#if defined(_MSC_VER) || defined(__clang__)
#define AL_CAMPFIRE_EXPORT __declspec(dllexport)
#else
#define AL_CAMPFIRE_EXPORT
#endif
#endif

#if defined(__cplusplus)
extern "C" {
#endif

/* The path is a canonical UTF-8 path supplied by the native host in storage
 * of at least path_bytes + 1 bytes. path_bytes excludes the required NUL at
 * canonical_db_path_utf8[path_bytes]; embedded NULs are rejected. Init borrows
 * that buffer only during sqlite3_open_v2 and never creates the database. A
 * failed init leaves ctx zeroed and writes the corresponding DbError tag. */
AL_CAMPFIRE_EXPORT int32_t al_campfire_room_sqlite_context_init_v1(
    al_campfire_room_sqlite_context_v1 *ctx,
    const uint8_t *canonical_db_path_utf8, uint32_t path_bytes,
    uint32_t *out_db_error_tag);

/* Close the prepared statement and connection. This is idempotent for a zeroed
 * context and must run on the context's owning thread after all calls return. */
AL_CAMPFIRE_EXPORT int32_t al_campfire_room_sqlite_context_destroy_v1(
    al_campfire_room_sqlite_context_v1 *ctx);

#if defined(__cplusplus)
}
#endif

#if defined(__STDC_VERSION__) && __STDC_VERSION__ >= 201112L
_Static_assert(sizeof(void *) == 8, "Campfire SQLite ABI v1 requires x64");
_Static_assert(sizeof(al_campfire_room_sqlite_context_v1) ==
                   AL_CAMPFIRE_ROOM_SQLITE_CONTEXT_BYTES,
               "Campfire SQLite context ABI v1 size changed");
_Static_assert(_Alignof(al_campfire_room_sqlite_context_v1) == 8,
               "Campfire SQLite context ABI v1 alignment changed");
#endif

#endif
