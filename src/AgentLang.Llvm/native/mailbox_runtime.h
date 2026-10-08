#ifndef AGENTLANG_MAILBOX_RUNTIME_H
#define AGENTLANG_MAILBOX_RUNTIME_H

#include "module_abi.h"

#ifdef __cplusplus
extern "C" {
#endif

enum { AL_MAILBOX_CONTROL_ABI_VERSION = 1u };

typedef enum al_mailbox_result {
  AL_MAILBOX_OK = 0,
  AL_MAILBOX_INVALID_ARGUMENT = 1,
  AL_MAILBOX_INVALID_MODULE = 2,
  AL_MAILBOX_INVALID_STORAGE = 3,
  AL_MAILBOX_WRONG_THREAD = 4,
  AL_MAILBOX_DISPOSED = 5,
  AL_MAILBOX_INVALID_MAILBOX = 6,
  AL_MAILBOX_NOT_INITIALIZED = 7,
  AL_MAILBOX_ALREADY_INITIALIZED = 8,
  AL_MAILBOX_BUSY = 9,
  AL_MAILBOX_NOT_PENDING = 10,
  AL_MAILBOX_CROSS_RUNTIME_TOKEN = 11,
  AL_MAILBOX_WRONG_OWNER_TOKEN = 12,
  AL_MAILBOX_STALE_TOKEN = 13,
  AL_MAILBOX_DUPLICATE_TOKEN = 14,
  AL_MAILBOX_TOKEN_EXHAUSTED = 15,
  AL_MAILBOX_GENERATION_EXHAUSTED = 16,
  AL_MAILBOX_HANDLER_FAILURE = 17,
  AL_MAILBOX_SCRATCH_CAPACITY = 18,
  AL_MAILBOX_RETAINED_CAPACITY = 19,
  AL_MAILBOX_INVALID_REFERENCE = 20,
  AL_MAILBOX_SIZE_OVERFLOW = 21,
  AL_MAILBOX_INSTANCE_EXHAUSTED = 22
} al_mailbox_result;

typedef struct al_mailbox_runtime al_mailbox_runtime;

/* Entry IDs index module->entries; entry_id must equal its table index. */
typedef struct al_mailbox_config {
  uint32_t control_abi_version;
  uint32_t struct_size;
  uint32_t mailbox_capacity;
  uint32_t scratch_byte_capacity;
  uint32_t scratch_node_capacity;
  uint32_t retained_byte_capacity;
  uint32_t retained_node_capacity;
  uint32_t init_entry_id;
  uint32_t begin_entry_id;
  uint32_t resume_entry_id;
  uint32_t reserved[2];
} al_mailbox_config;

/* Passing NULL limits uses the physical retained capacities. A non-NULL
 * structure applies both limits literally, including an explicit zero. */
typedef struct al_mailbox_call_limits {
  uint32_t control_abi_version;
  uint32_t struct_size;
  uint32_t retained_byte_limit;
  uint32_t retained_node_limit;
} al_mailbox_call_limits;

typedef struct al_mailbox_storage_requirements {
  uint32_t control_abi_version;
  uint32_t struct_size;
  uint32_t storage_alignment;
  uint32_t workspace_capacity;
  uint64_t storage_bytes;
  uint64_t retained_reserved_bytes;
  uint64_t scratch_reserved_bytes;
  uint64_t workspace_reserved_bytes;
  uint64_t controller_reserved_bytes;
} al_mailbox_storage_requirements;

/* Runtime status is returned separately. handler_status uses
 * AL_RUNTIME_STATUS_*; error_metadata_id and error arguments are copied from
 * the ABI3 execution context before scratch is reset. */
typedef struct al_mailbox_call_info {
  uint32_t control_abi_version;
  uint32_t struct_size;
  int32_t handler_status;
  int32_t error_metadata_id;
  int64_t error_argument0;
  int64_t error_argument1;
  uint32_t steps_consumed;
  uint32_t reserved;
} al_mailbox_call_info;

/* Tokens are opaque capabilities. Copying one aliases the same completion;
 * it does not mint another completion. Their identity is runtime-scoped. */
typedef struct al_mailbox_token {
  uint64_t opaque[3];
} al_mailbox_token;

typedef struct al_mailbox_runtime_stats {
  uint32_t control_abi_version;
  uint32_t struct_size;
  uint32_t mailbox_capacity;
  uint32_t initialized_mailboxes;
  uint32_t pending_mailboxes;
  uint32_t workspace_capacity;
  uint64_t storage_reserved_bytes;
  uint64_t retained_reserved_bytes;
  uint64_t scratch_reserved_bytes;
  uint64_t workspace_reserved_bytes;
  uint64_t live_retained_bytes;
  uint64_t live_retained_nodes;
  uint64_t scratch_high_water_bytes;
  uint64_t scratch_high_water_nodes;
  uint64_t handler_invocations;
  uint64_t handler_failures;
  uint64_t scratch_lease_acquisitions;
  uint64_t scratch_lease_returns;
  uint32_t outstanding_scratch_leases;
  uint32_t reserved;
} al_mailbox_runtime_stats;

/* Borrowed until the next mutating API call or disposal. roots[0] is State;
 * while pending, roots[1] is its Continuation. */
typedef struct al_mailbox_state_view {
  uint32_t control_abi_version;
  uint32_t struct_size;
  const al_arena *owner;
  const int64_t *roots;
  const uint32_t *root_type_ids;
  uint32_t root_count;
  uint32_t state_type_id;
  uint32_t pending;
  uint32_t reserved;
} al_mailbox_state_view;

al_mailbox_result al_mailbox_get_storage_requirements(
    const al_module_desc *module, const al_mailbox_config *config,
    al_mailbox_storage_requirements *requirements);

al_mailbox_result al_mailbox_runtime_init(const al_module_desc *module,
                                          const al_mailbox_config *config,
                                          void *storage, uint64_t storage_bytes,
                                          al_mailbox_runtime **out_runtime);

al_mailbox_result al_mailbox_init_mailbox(al_mailbox_runtime *runtime,
                                          uint32_t mailbox_id, int64_t seed,
                                          const al_mailbox_call_limits *limits,
                                          al_mailbox_call_info *call_info);

al_mailbox_result al_mailbox_begin(al_mailbox_runtime *runtime,
                                   uint32_t mailbox_id, int64_t message,
                                   const al_mailbox_call_limits *limits,
                                   al_mailbox_token *out_token,
                                   al_mailbox_call_info *call_info);

al_mailbox_result al_mailbox_resume(al_mailbox_runtime *runtime,
                                    uint32_t mailbox_id,
                                    const al_mailbox_token *token,
                                    int64_t message,
                                    const al_mailbox_call_limits *limits,
                                    al_mailbox_call_info *call_info);

/* Idempotent on the creating thread. The caller-supplied storage remains
 * caller-owned. Keep the loaded module alive through the final API call,
 * including post-disposal stats reads. */
al_mailbox_result al_mailbox_dispose(al_mailbox_runtime *runtime);

/* Stats remain readable on the creator thread after disposal to expose final
 * cleanup and lease counts. */
al_mailbox_result al_mailbox_get_stats(al_mailbox_runtime *runtime,
                                       al_mailbox_runtime_stats *stats);

al_mailbox_result al_mailbox_get_state_view(al_mailbox_runtime *runtime,
                                            uint32_t mailbox_id,
                                            al_mailbox_state_view *view);

#ifdef AL_MAILBOX_RUNTIME_TESTING
/* Honest exhaustion hooks for direct C boundary tests only. */
al_mailbox_result
al_mailbox_test_set_next_token_sequence(al_mailbox_runtime *runtime,
                                        uint64_t next_sequence);
al_mailbox_result
al_mailbox_test_set_next_generation(al_mailbox_runtime *runtime,
                                    uint64_t next_generation);
al_mailbox_result al_mailbox_test_exhaust_instance_counter(void);
#endif

#if defined(__cplusplus)
#define AL_MAILBOX_ASSERT(condition, message)                                  \
  static_assert((condition), message)
#define AL_MAILBOX_ALIGNOF(type) alignof(type)
#else
#define AL_MAILBOX_ASSERT(condition, message)                                  \
  _Static_assert((condition), message)
#define AL_MAILBOX_ALIGNOF(type) _Alignof(type)
#endif

AL_MAILBOX_ASSERT(sizeof(al_mailbox_config) == 48, "config size");
AL_MAILBOX_ASSERT(offsetof(al_mailbox_config, mailbox_capacity) == 8,
                  "config capacity offset");
AL_MAILBOX_ASSERT(offsetof(al_mailbox_config, init_entry_id) == 28,
                  "config entry IDs offset");
AL_MAILBOX_ASSERT(sizeof(al_mailbox_call_limits) == 16, "call limits size");
AL_MAILBOX_ASSERT(offsetof(al_mailbox_call_limits, retained_byte_limit) == 8,
                  "call limits byte offset");
AL_MAILBOX_ASSERT(sizeof(al_mailbox_storage_requirements) == 56,
                  "storage requirements size");
AL_MAILBOX_ASSERT(offsetof(al_mailbox_storage_requirements, storage_bytes) ==
                      16,
                  "storage byte count offset");
AL_MAILBOX_ASSERT(offsetof(al_mailbox_storage_requirements,
                           retained_reserved_bytes) == 24,
                  "retained storage offset");
AL_MAILBOX_ASSERT(sizeof(al_mailbox_call_info) == 40, "call info size");
AL_MAILBOX_ASSERT(offsetof(al_mailbox_call_info, handler_status) == 8,
                  "call status offset");
AL_MAILBOX_ASSERT(offsetof(al_mailbox_call_info, error_argument0) == 16,
                  "call error args offset");
AL_MAILBOX_ASSERT(sizeof(al_mailbox_token) == 24, "token size");
AL_MAILBOX_ASSERT(AL_MAILBOX_ALIGNOF(al_mailbox_token) == 8, "token alignment");
AL_MAILBOX_ASSERT(sizeof(al_mailbox_runtime_stats) == 128, "stats size");
AL_MAILBOX_ASSERT(offsetof(al_mailbox_runtime_stats, storage_reserved_bytes) ==
                      24,
                  "stats storage offset");
AL_MAILBOX_ASSERT(offsetof(al_mailbox_runtime_stats,
                           scratch_lease_acquisitions) == 104,
                  "stats lease metrics offset");
AL_MAILBOX_ASSERT(offsetof(al_mailbox_runtime_stats,
                           outstanding_scratch_leases) == 120,
                  "stats outstanding leases offset");
AL_MAILBOX_ASSERT(sizeof(al_mailbox_state_view) == 48, "state view size");
AL_MAILBOX_ASSERT(offsetof(al_mailbox_state_view, owner) == 8,
                  "state view owner offset");
AL_MAILBOX_ASSERT(offsetof(al_mailbox_state_view, root_count) == 32,
                  "state view roots offset");

#undef AL_MAILBOX_ASSERT
#undef AL_MAILBOX_ALIGNOF

#ifdef __cplusplus
}
#endif
#endif
