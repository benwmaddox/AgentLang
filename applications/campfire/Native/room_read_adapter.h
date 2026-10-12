#ifndef AGENTLANG_CAMPFIRE_ROOM_READ_ADAPTER_H
#define AGENTLANG_CAMPFIRE_ROOM_READ_ADAPTER_H

#include "campfire_room_read_host_abi.h"

#include <stddef.h>
#include <stdint.h>

#define AL_CAMPFIRE_ROOM_READ_CONTEXT_ABI_VERSION 1u
#define AL_CAMPFIRE_ROOM_READ_CONTEXT_BYTES 136u
#define AL_CAMPFIRE_ROOM_READ_PATH_LIMIT_BYTES 131072u
#define AL_CAMPFIRE_ROOM_READ_SQLITE_STATEMENT_COUNT 13u

enum al_campfire_db_error_tag_v1 {
  AL_CAMPFIRE_DB_ERROR_UNAVAILABLE = 0u,
  AL_CAMPFIRE_DB_ERROR_NOT_FOUND = 1u,
  AL_CAMPFIRE_DB_ERROR_CONSTRAINT_CONFLICT = 2u,
  AL_CAMPFIRE_DB_ERROR_INVALID_INPUT = 3u
};

enum al_campfire_room_read_statement_v1 {
  AL_CAMPFIRE_READ_STMT_SESSION_FIND_TOKEN = 0u,
  AL_CAMPFIRE_READ_STMT_SESSION_FIND_ID = 1u,
  AL_CAMPFIRE_READ_STMT_SESSION_REFRESH_UPDATE = 2u,
  AL_CAMPFIRE_READ_STMT_USER_FIND_ID = 3u,
  AL_CAMPFIRE_READ_STMT_USER_BOT_KEY = 4u,
  AL_CAMPFIRE_READ_STMT_ROOM_FIND_FOR_USER = 5u,
  AL_CAMPFIRE_READ_STMT_MEMBERSHIP_EXISTS = 6u,
  AL_CAMPFIRE_READ_STMT_ROOM_EXISTS = 7u,
  AL_CAMPFIRE_READ_STMT_MESSAGES_LAST_PAGE = 8u,
  AL_CAMPFIRE_READ_STMT_BEGIN_DEFERRED = 9u,
  AL_CAMPFIRE_READ_STMT_BEGIN_IMMEDIATE = 10u,
  AL_CAMPFIRE_READ_STMT_COMMIT = 11u,
  AL_CAMPFIRE_READ_STMT_ROLLBACK = 12u
};

/* Caller-owned, zero-initialized and worker-thread-confined SQLite state.
 * The pure context is borrowed and is owned by the host. A context must not be
 * called concurrently or migrated to another thread while its connection is
 * open. `active` is 0 idle, 1 running, 2 poisoned, or 3 running-and-poisoned;
 * poisoned contexts reject calls but can still be destroyed on their owner
 * thread. The opaque statement array order is fixed by the enum above. */
typedef struct al_campfire_room_read_context_v1 {
  void *opaque_db;
  void *opaque_statements[AL_CAMPFIRE_ROOM_READ_SQLITE_STATEMENT_COUNT];
  void *pure_context;
  uint64_t cookie;
  uint32_t owner_thread_id;
  uint32_t active;
} al_campfire_room_read_context_v1;

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

/* The canonical UTF-8 path is borrowed during initialization. It must have a
 * trailing NUL at path_bytes, contain no embedded NUL, and name an existing
 * WAL database already provisioned by al_campfire_room_read_provision_wal_v1.
 * Initialization verifies WAL and configures per-connection safeguards but
 * never changes journal mode. `pure_context` is borrowed and never destroyed. */
AL_CAMPFIRE_EXPORT int32_t al_campfire_room_read_context_init_v1(
    al_campfire_room_read_context_v1 *ctx,
    const uint8_t *canonical_db_path_utf8, uint32_t path_bytes,
    void *pure_context, uint32_t *out_db_error_tag);

/* Startup-only, caller-serialized provisioning. Enables WAL on an existing
 * database using fixed PRAGMAs; it never creates a database or accepts SQL. */
AL_CAMPFIRE_EXPORT int32_t al_campfire_room_read_provision_wal_v1(
    const uint8_t *canonical_db_path_utf8, uint32_t path_bytes,
    uint32_t *out_db_error_tag);

/* Finalizes every prepared statement and closes the connection. Idempotent on
 * a zeroed context; run on the initializing worker thread after calls return. */
AL_CAMPFIRE_EXPORT int32_t al_campfire_room_read_context_destroy_v1(
    al_campfire_room_read_context_v1 *ctx);

int32_t al_host_campfire_room_read_v1(
    void *provider_context, uint32_t operation,
    const al_owning_layout *layout,
    const al_campfire_room_read_input *inputs, uint32_t input_count,
    uint32_t output_type_index, uint8_t *output,
    uint32_t output_capacity_bytes, uint32_t *output_extent_bytes);

#if defined(__cplusplus)
}
#endif

#if defined(_MSC_VER) || defined(__clang__)
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wreserved-identifier"
#endif
#if defined(__STDC_VERSION__) && __STDC_VERSION__ >= 201112L
_Static_assert(sizeof(void *) == 8, "Campfire room-read ABI v1 requires x64");
_Static_assert(sizeof(al_campfire_room_read_context_v1) ==
                   AL_CAMPFIRE_ROOM_READ_CONTEXT_BYTES,
               "Campfire room-read context ABI v1 size changed");
_Static_assert(_Alignof(al_campfire_room_read_context_v1) == 8,
               "Campfire room-read context ABI v1 alignment changed");
_Static_assert(offsetof(al_campfire_room_read_context_v1, opaque_db) == 0u &&
                   offsetof(al_campfire_room_read_context_v1,
                            opaque_statements) == 8u &&
                   offsetof(al_campfire_room_read_context_v1, pure_context) ==
                       112u &&
                   offsetof(al_campfire_room_read_context_v1, cookie) == 120u &&
                   offsetof(al_campfire_room_read_context_v1,
                            owner_thread_id) == 128u &&
                   offsetof(al_campfire_room_read_context_v1, active) == 132u,
               "Campfire room-read context ABI v1 offsets changed");
#endif
#if defined(_MSC_VER) || defined(__clang__)
#pragma clang diagnostic pop
#endif

#endif
