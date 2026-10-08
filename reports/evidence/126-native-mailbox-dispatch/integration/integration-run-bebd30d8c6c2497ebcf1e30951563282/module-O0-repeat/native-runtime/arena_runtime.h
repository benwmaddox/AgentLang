#ifndef AGENTLANG_ARENA_RUNTIME_H
#define AGENTLANG_ARENA_RUNTIME_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

enum {
  AL_RUNTIME_ABI_VERSION = 3u,
  AL_RUNTIME_TYPE_INT = 1u,
  AL_RUNTIME_TYPE_BOOL = 2u,
  AL_RUNTIME_TYPE_UNIT = 3u,
  AL_RUNTIME_TYPE_RECORD = 4u,
  AL_RUNTIME_STATUS_SUCCESS = 0,
  AL_RUNTIME_STATUS_DIAGNOSTIC = 1,
  AL_RUNTIME_STATUS_INVALID_REQUEST = 2
};

/* Descriptor kinds use the AL_RUNTIME_TYPE_* values; type IDs index
 * Program.types from zero. */

typedef enum al_runtime_result {
  AL_RUNTIME_OK = 0,
  AL_RUNTIME_INVALID_REQUEST = 1,
  AL_RUNTIME_INVALID_REFERENCE = 2,
  AL_RUNTIME_SCRATCH_CAPACITY = 3,
  AL_RUNTIME_RETAINED_CAPACITY = 4
} al_runtime_result;

typedef struct al_arena al_arena;

/* ABI v3 context. The first 64 bytes preserve the v2 layout and the first 32
 * bytes preserve the published ABI v1 prefix. */
typedef struct al_runtime_context {
  uint32_t abi_version;
  uint32_t steps_consumed;
  int32_t error_metadata_id;
  int32_t reserved_prefix;
  int64_t error_argument0;
  int64_t error_argument1;
  al_arena *scratch;
  al_arena *retained;
  int64_t *workspace;
  uint32_t workspace_capacity;
  uint32_t reserved_tail;
  /* Immutable prior owner and ordered invocation argument bits/type IDs. */
  const al_arena *input_owner;
  const int64_t *input_roots;
  const uint32_t *input_root_type_ids;
  uint32_t input_root_count;
  uint32_t reserved_v3;
} al_runtime_context;

/* Callers assign distinct nonzero generations and must not reuse/wrap a live
 * owner ID. */
struct al_arena {
  uint8_t *data;
  uint32_t byte_capacity;
  uint32_t used;
  struct al_node *nodes;
  uint32_t node_capacity;
  uint32_t node_count;
  uint32_t generation;
  uint32_t flags;
  uint64_t reserved;
};

typedef struct al_node {
  uint32_t type_id;
  uint32_t field_count;
  uint32_t payload_offset;
  uint32_t payload_bytes;
  uint32_t mark;
  uint32_t reserved;
  uint64_t forward_handle;
} al_node;

typedef struct al_type_desc {
  uint32_t kind;
  uint32_t field_count;
  const uint32_t *field_types;
} al_type_desc;

typedef struct al_program_desc {
  const al_type_desc *types;
  uint32_t type_count;
  uint32_t reserved;
} al_program_desc;

/* Validate the full entry contract before generated code reads ABI v3 fields.
 */
al_runtime_result
al_runtime_validate_request(al_runtime_context *ctx, int64_t *public_outputs,
                            uint32_t output_count, uint32_t output_capacity,
                            int32_t *status,
                            uint32_t expected_workspace_capacity);

/* ABI v3 invocation inputs are ordered value/type-ID arrays. Import validates
 * them against the compiled body types, copies the immutable input owner graph
 * into empty scratch when one is supplied, and stages rewritten values in
 * workspace[0..input_count). */
al_runtime_result
al_runtime_import_state(al_runtime_context *ctx, const al_program_desc *program,
                        const uint32_t *expected_input_type_ids,
                        uint32_t input_count);

/* Runtime helper outputs and record-construction inputs live in ctx->workspace.
 */
/* Capacity failures set error_argument0/1 to required total bytes and node
 * count. */
al_runtime_result
al_runtime_make_record(al_runtime_context *ctx, const al_program_desc *program,
                       uint32_t type_id, const int64_t *fields,
                       uint32_t field_count, int64_t *out_handle);

al_runtime_result
al_runtime_get_field(al_runtime_context *ctx, const al_program_desc *program,
                     uint32_t expected_record_type_id, int64_t record_handle,
                     uint32_t field_index, int64_t *out_value);

al_runtime_result al_runtime_equal(al_runtime_context *ctx,
                                   const al_program_desc *program,
                                   uint32_t type_id, int64_t left,
                                   int64_t right, uint32_t *out_bool);

/* Roots are typed workspace slots; public roots are written only on success. */
/* Promotion capacity args report unique reachable payload bytes and node count.
 */
al_runtime_result al_runtime_promote(al_runtime_context *ctx,
                                     const al_program_desc *program,
                                     const int64_t *scratch_roots,
                                     const uint32_t *root_type_ids,
                                     uint32_t root_count, int64_t *public_roots,
                                     uint32_t public_capacity);

#if defined(__cplusplus)
#define AL_RUNTIME_STATIC_ASSERT(condition, message)                           \
  static_assert((condition), message)
#define AL_RUNTIME_ALIGNOF(type) alignof(type)
#else
#define AL_RUNTIME_STATIC_ASSERT(condition, message)                           \
  _Static_assert((condition), message)
#define AL_RUNTIME_ALIGNOF(type) _Alignof(type)
#endif

AL_RUNTIME_STATIC_ASSERT(sizeof(al_runtime_context) == 96,
                         "ABI v3 context size");
AL_RUNTIME_STATIC_ASSERT(AL_RUNTIME_ALIGNOF(al_runtime_context) == 8,
                         "ABI v3 context alignment");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_runtime_context, abi_version) == 0,
                         "context abi_version offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_runtime_context, steps_consumed) == 4,
                         "context steps_consumed offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_runtime_context, error_metadata_id) == 8,
                         "context error_metadata_id offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_runtime_context, reserved_prefix) == 12,
                         "context reserved offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_runtime_context, error_argument0) == 16,
                         "context error_argument0 offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_runtime_context, error_argument1) == 24,
                         "context error_argument1 offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_runtime_context, scratch) == 32,
                         "context scratch offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_runtime_context, retained) == 40,
                         "context retained offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_runtime_context, workspace) == 48,
                         "context workspace offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_runtime_context, workspace_capacity) == 56,
                         "context workspace_capacity offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_runtime_context, reserved_tail) == 60,
                         "context tail reserved offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_runtime_context, input_owner) == 64,
                         "context input owner offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_runtime_context, input_roots) == 72,
                         "context input roots offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_runtime_context, input_root_type_ids) ==
                             80,
                         "context input root type IDs offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_runtime_context, input_root_count) == 88,
                         "context input root count offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_runtime_context, reserved_v3) == 92,
                         "context v3 reserved offset");

AL_RUNTIME_STATIC_ASSERT(sizeof(al_arena) == 48, "arena size");
AL_RUNTIME_STATIC_ASSERT(AL_RUNTIME_ALIGNOF(al_arena) == 8, "arena alignment");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_arena, data) == 0, "arena data offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_arena, byte_capacity) == 8,
                         "arena byte_capacity offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_arena, used) == 12, "arena used offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_arena, nodes) == 16, "arena nodes offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_arena, node_capacity) == 24,
                         "arena node_capacity offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_arena, node_count) == 28,
                         "arena node_count offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_arena, generation) == 32,
                         "arena generation offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_arena, flags) == 36, "arena flags offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_arena, reserved) == 40,
                         "arena reserved offset");

AL_RUNTIME_STATIC_ASSERT(sizeof(al_node) == 32, "node size");
AL_RUNTIME_STATIC_ASSERT(AL_RUNTIME_ALIGNOF(al_node) == 8, "node alignment");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_node, type_id) == 0,
                         "node type_id offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_node, field_count) == 4,
                         "node field_count offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_node, payload_offset) == 8,
                         "node payload_offset offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_node, payload_bytes) == 12,
                         "node payload_bytes offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_node, mark) == 16, "node mark offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_node, reserved) == 20,
                         "node reserved offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_node, forward_handle) == 24,
                         "node forward_handle offset");

AL_RUNTIME_STATIC_ASSERT(sizeof(al_type_desc) == 16, "type descriptor size");
AL_RUNTIME_STATIC_ASSERT(AL_RUNTIME_ALIGNOF(al_type_desc) == 8,
                         "type descriptor alignment");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_type_desc, kind) == 0,
                         "type descriptor kind offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_type_desc, field_count) == 4,
                         "type descriptor field_count offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_type_desc, field_types) == 8,
                         "type descriptor field_types offset");
AL_RUNTIME_STATIC_ASSERT(sizeof(al_program_desc) == 16,
                         "program descriptor size");
AL_RUNTIME_STATIC_ASSERT(AL_RUNTIME_ALIGNOF(al_program_desc) == 8,
                         "program descriptor alignment");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_program_desc, types) == 0,
                         "program descriptor types offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_program_desc, type_count) == 8,
                         "program descriptor type_count offset");
AL_RUNTIME_STATIC_ASSERT(offsetof(al_program_desc, reserved) == 12,
                         "program descriptor reserved offset");

#undef AL_RUNTIME_ALIGNOF
#undef AL_RUNTIME_STATIC_ASSERT

#ifdef __cplusplus
}
#endif

#endif
