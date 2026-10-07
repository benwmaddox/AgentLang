#include "arena_runtime.h"

#include <limits.h>

#define AL_RUNTIME_MAX_EQUAL_DEPTH 256u
#define AL_RUNTIME_MAX_EQUAL_NODES 100000u

typedef struct al_span {
  uintptr_t begin;
  uintptr_t end;
  uint32_t present;
} al_span;

typedef struct al_internal_spans {
  al_span values[8];
} al_internal_spans;

typedef struct al_equal_frame {
  uint32_t left_index;
  uint32_t right_index;
  uint32_t field_index;
} al_equal_frame;

static uint32_t al_pointer_aligned(const void *pointer, uintptr_t alignment) {
  return pointer == NULL || (((uintptr_t)pointer & (alignment - 1u)) == 0u);
}

static uint32_t al_make_span(const void *pointer, uint64_t byte_count,
                             al_span *span) {
  uintptr_t begin;

  if (span == NULL) {
    return 0u;
  }

  begin = (uintptr_t)pointer;
  if (byte_count == 0u) {
    span->begin = begin;
    span->end = begin;
    span->present = 0u;
    return 1u;
  }

  if (pointer == NULL || byte_count > (uint64_t)(UINTPTR_MAX - begin)) {
    return 0u;
  }

  span->begin = begin;
  span->end = begin + (uintptr_t)byte_count;
  span->present = 1u;
  return 1u;
}

static uint32_t al_spans_overlap(const al_span *left, const al_span *right) {
  if (left->present == 0u || right->present == 0u) {
    return 0u;
  }
  return left->begin < right->end && right->begin < left->end;
}

static uint32_t al_spans_disjoint(const al_span *spans, uint32_t count) {
  uint32_t left;
  uint32_t right;

  for (left = 0u; left < count; ++left) {
    for (right = left + 1u; right < count; ++right) {
      if (al_spans_overlap(&spans[left], &spans[right]) != 0u) {
        return 0u;
      }
    }
  }
  return 1u;
}

static uint32_t al_span_contains(const al_span *outer, const al_span *inner) {
  if (inner->present == 0u) {
    return 1u;
  }
  return outer->present != 0u && inner->begin >= outer->begin &&
         inner->end <= outer->end;
}

static uint32_t al_make_array_span(const void *pointer, uint32_t count,
                                   uint32_t element_size, al_span *span) {
  return al_make_span(pointer, (uint64_t)count * (uint64_t)element_size, span);
}

static uint32_t al_context_spans(al_runtime_context *ctx,
                                 al_internal_spans *spans) {
  if (ctx == NULL || spans == NULL || !al_pointer_aligned(ctx, 8u)) {
    return 0u;
  }

  if (!al_make_span(ctx, sizeof(*ctx), &spans->values[0]) ||
      !al_make_span(ctx->scratch, sizeof(*ctx->scratch), &spans->values[1]) ||
      !al_make_span(ctx->retained, sizeof(*ctx->retained), &spans->values[2]) ||
      !al_make_array_span(ctx->workspace, ctx->workspace_capacity,
                          (uint32_t)sizeof(int64_t), &spans->values[3]) ||
      !al_make_span(ctx->scratch->data, ctx->scratch->byte_capacity,
                    &spans->values[4]) ||
      !al_make_array_span(ctx->scratch->nodes, ctx->scratch->node_capacity,
                          (uint32_t)sizeof(al_node), &spans->values[5]) ||
      !al_make_span(ctx->retained->data, ctx->retained->byte_capacity,
                    &spans->values[6]) ||
      !al_make_array_span(ctx->retained->nodes, ctx->retained->node_capacity,
                          (uint32_t)sizeof(al_node), &spans->values[7])) {
    return 0u;
  }

  return al_spans_disjoint(spans->values, 8u);
}

static uint32_t al_context_valid(al_runtime_context *ctx,
                                 al_internal_spans *spans) {
  const al_arena *scratch;
  const al_arena *retained;

  if (ctx == NULL || !al_pointer_aligned(ctx, 8u)) {
    return 0u;
  }

  /* Keep the ABI prefix check ahead of every access to the ABI v2 tail. */
  if (ctx->abi_version != AL_RUNTIME_ABI_VERSION) {
    return 0u;
  }

  if (ctx->reserved_prefix != 0 || ctx->reserved_tail != 0u ||
      !al_pointer_aligned(ctx->workspace, 8u) ||
      (ctx->workspace_capacity != 0u && ctx->workspace == NULL) ||
      ctx->scratch == NULL || ctx->retained == NULL ||
      !al_pointer_aligned(ctx->scratch, 8u) ||
      !al_pointer_aligned(ctx->retained, 8u) || ctx->scratch == ctx->retained) {
    return 0u;
  }

  scratch = ctx->scratch;
  retained = ctx->retained;
  if (scratch->generation == 0u || retained->generation == 0u ||
      scratch->generation == retained->generation || scratch->flags != 0u ||
      retained->flags != 0u || scratch->reserved != 0u ||
      retained->reserved != 0u || scratch->used > scratch->byte_capacity ||
      retained->used > retained->byte_capacity ||
      scratch->node_count > scratch->node_capacity ||
      retained->node_count > retained->node_capacity ||
      (scratch->used & 7u) != 0u || (retained->used & 7u) != 0u ||
      retained->node_count != 0u || retained->used != 0u ||
      !al_pointer_aligned(scratch->data, 8u) ||
      !al_pointer_aligned(retained->data, 8u) ||
      !al_pointer_aligned(scratch->nodes, 8u) ||
      !al_pointer_aligned(retained->nodes, 8u) ||
      (scratch->byte_capacity != 0u && scratch->data == NULL) ||
      (retained->byte_capacity != 0u && retained->data == NULL) ||
      (scratch->node_capacity != 0u && scratch->nodes == NULL) ||
      (retained->node_capacity != 0u && retained->nodes == NULL)) {
    return 0u;
  }

  return al_context_spans(ctx, spans);
}

static uint32_t al_workspace_contains(const al_runtime_context *ctx,
                                      const void *pointer, uint64_t byte_count,
                                      uintptr_t alignment) {
  al_span workspace;
  al_span candidate;

  if (!al_pointer_aligned(pointer, alignment) ||
      !al_make_array_span(ctx->workspace, ctx->workspace_capacity,
                          (uint32_t)sizeof(int64_t), &workspace) ||
      !al_make_span(pointer, byte_count, &candidate)) {
    return 0u;
  }

  return al_span_contains(&workspace, &candidate);
}

static uint32_t al_span_disjoint_from_context(const al_runtime_context *ctx,
                                              const al_span *candidate) {
  al_internal_spans spans;
  uint32_t index;

  if (!al_context_spans((al_runtime_context *)ctx, &spans)) {
    return 0u;
  }

  for (index = 0u; index < 8u; ++index) {
    if (al_spans_overlap(&spans.values[index], candidate)) {
      return 0u;
    }
  }
  return 1u;
}

static uint32_t al_program_valid(const al_runtime_context *ctx,
                                 const al_program_desc *program) {
  al_span program_span;
  al_span type_array_span;
  uint32_t type_index;

  if (program == NULL || !al_pointer_aligned(program, 8u) ||
      !al_make_span(program, sizeof(*program), &program_span) ||
      !al_span_disjoint_from_context(ctx, &program_span) ||
      program->reserved != 0u || !al_pointer_aligned(program->types, 8u) ||
      (program->type_count != 0u && program->types == NULL) ||
      !al_make_array_span(program->types, program->type_count,
                          (uint32_t)sizeof(al_type_desc), &type_array_span) ||
      !al_span_disjoint_from_context(ctx, &type_array_span) ||
      al_spans_overlap(&program_span, &type_array_span)) {
    return 0u;
  }

  for (type_index = 0u; type_index < program->type_count; ++type_index) {
    const al_type_desc *type = &program->types[type_index];
    al_span fields_span;
    uint32_t field_index;

    if (type->kind < AL_RUNTIME_TYPE_INT ||
        type->kind > AL_RUNTIME_TYPE_RECORD ||
        !al_pointer_aligned(type->field_types, 4u) ||
        (type->kind != AL_RUNTIME_TYPE_RECORD && type->field_count != 0u) ||
        (uint64_t)type->field_count * (uint64_t)sizeof(uint32_t) >
            (uint64_t)UINTPTR_MAX ||
        (type->kind == AL_RUNTIME_TYPE_RECORD &&
         type->field_count > UINT32_MAX / (uint32_t)sizeof(int64_t)) ||
        (type->field_count != 0u && type->field_types == NULL) ||
        !al_make_array_span(type->field_types, type->field_count,
                            (uint32_t)sizeof(uint32_t), &fields_span) ||
        !al_span_disjoint_from_context(ctx, &fields_span) ||
        al_spans_overlap(&program_span, &fields_span) ||
        al_spans_overlap(&type_array_span, &fields_span)) {
      return 0u;
    }

    for (field_index = 0u; field_index < type->field_count; ++field_index) {
      if (type->field_types[field_index] >= program->type_count) {
        return 0u;
      }
    }
  }
  return 1u;
}

static uint32_t al_program_disjoint_from_span(const al_program_desc *program,
                                              const al_span *candidate) {
  al_span program_span;
  al_span type_array_span;
  uint32_t type_index;

  if (!al_make_span(program, sizeof(*program), &program_span) ||
      !al_make_array_span(program->types, program->type_count,
                          (uint32_t)sizeof(al_type_desc), &type_array_span) ||
      al_spans_overlap(&program_span, candidate) ||
      al_spans_overlap(&type_array_span, candidate)) {
    return 0u;
  }

  for (type_index = 0u; type_index < program->type_count; ++type_index) {
    al_span fields_span;
    const al_type_desc *type = &program->types[type_index];
    if (!al_make_array_span(type->field_types, type->field_count,
                            (uint32_t)sizeof(uint32_t), &fields_span) ||
        al_spans_overlap(&fields_span, candidate)) {
      return 0u;
    }
  }
  return 1u;
}

static const al_type_desc *al_type_at(const al_program_desc *program,
                                      uint32_t type_id) {
  if (program == NULL || type_id >= program->type_count) {
    return NULL;
  }
  return &program->types[type_id];
}

static uint64_t al_read_u64(const uint8_t *data) {
  uint64_t value = 0u;
  uint32_t byte_index;

  for (byte_index = 0u; byte_index < 8u; ++byte_index) {
    value |= (uint64_t)data[byte_index] << (byte_index * 8u);
  }
  return value;
}

static void al_write_u64(uint8_t *data, uint64_t value) {
  uint32_t byte_index;

  for (byte_index = 0u; byte_index < 8u; ++byte_index) {
    data[byte_index] = (uint8_t)(value >> (byte_index * 8u));
  }
}

static void al_write_u32(uint8_t *data, uint32_t value) {
  uint32_t byte_index;
  for (byte_index = 0u; byte_index < 4u; ++byte_index) {
    data[byte_index] = (uint8_t)(value >> (byte_index * 8u));
  }
}

static void al_arena_write_slot(al_arena *arena, const al_node *node,
                                uint32_t field_index, uint64_t bits) {
  al_write_u64(arena->data + node->payload_offset +
                   field_index * (uint32_t)sizeof(int64_t),
               bits);
}

static uint64_t al_make_handle(uint32_t generation, uint32_t node_index) {
  return ((uint64_t)generation << 32u) | (uint64_t)node_index;
}

static uint32_t al_node_basic_valid(const al_program_desc *program,
                                    const al_arena *arena, uint32_t node_index,
                                    const al_node **out_node) {
  const al_node *node;
  const al_type_desc *type;
  uint64_t expected_bytes;

  if (node_index == 0u || node_index > arena->node_count ||
      arena->nodes == NULL) {
    return 0u;
  }

  node = &arena->nodes[node_index - 1u];
  type = al_type_at(program, node->type_id);
  if (type == NULL || type->kind != AL_RUNTIME_TYPE_RECORD ||
      node->reserved != 0u || node->field_count != type->field_count) {
    return 0u;
  }

  expected_bytes = (uint64_t)type->field_count * (uint64_t)sizeof(int64_t);
  if (expected_bytes > UINT32_MAX ||
      node->payload_bytes != (uint32_t)expected_bytes ||
      (node->payload_offset & 7u) != 0u ||
      (uint64_t)node->payload_offset + (uint64_t)node->payload_bytes >
          arena->used) {
    return 0u;
  }

  if (out_node != NULL) {
    *out_node = node;
  }
  return 1u;
}

static uint32_t al_resolve_handle(const al_runtime_context *ctx,
                                  const al_program_desc *program,
                                  uint64_t handle, uint32_t expected_type_id,
                                  const al_arena **out_arena,
                                  const al_node **out_node,
                                  uint32_t *out_node_index) {
  uint32_t owner = (uint32_t)(handle >> 32u);
  uint32_t node_index = (uint32_t)handle;
  const al_arena *arena;
  const al_node *node;

  if (owner == 0u || node_index == 0u) {
    return 0u;
  }

  if (owner == ctx->scratch->generation) {
    arena = ctx->scratch;
  } else if (owner == ctx->retained->generation) {
    arena = ctx->retained;
  } else {
    return 0u;
  }

  if (node_index > arena->node_count ||
      !al_node_basic_valid(program, arena, node_index, &node) ||
      node->type_id != expected_type_id) {
    return 0u;
  }

  if (out_arena != NULL) {
    *out_arena = arena;
  }
  if (out_node != NULL) {
    *out_node = node;
  }
  if (out_node_index != NULL) {
    *out_node_index = node_index;
  }
  return 1u;
}

static uint32_t al_value_valid(const al_runtime_context *ctx,
                               const al_program_desc *program, uint32_t type_id,
                               uint64_t value_bits,
                               uint32_t require_scratch_record) {
  const al_type_desc *type = al_type_at(program, type_id);
  const al_arena *owner;

  if (type == NULL) {
    return 0u;
  }

  switch (type->kind) {
  case AL_RUNTIME_TYPE_INT:
    return 1u;
  case AL_RUNTIME_TYPE_BOOL:
    return value_bits <= 1u;
  case AL_RUNTIME_TYPE_UNIT:
    return value_bits == 0u;
  case AL_RUNTIME_TYPE_RECORD:
    if (!al_resolve_handle(ctx, program, value_bits, type_id, &owner, NULL,
                           NULL)) {
      return 0u;
    }
    return require_scratch_record == 0u || owner == ctx->scratch;
  default:
    return 0u;
  }
}

static uint32_t al_scratch_graph_valid(al_runtime_context *ctx,
                                       const al_program_desc *program) {
  const al_arena *arena = ctx->scratch;
  uint64_t payload_cursor = 0u;
  uint32_t node_offset;

  /* Validate the whole directory on each call so every public helper stands
   * alone. */
  for (node_offset = 0u; node_offset < arena->node_count; ++node_offset) {
    uint32_t node_index = node_offset + 1u;
    const al_node *node;
    const al_type_desc *type;
    uint32_t field_index;

    if (!al_node_basic_valid(program, arena, node_index, &node) ||
        node->payload_offset != (uint32_t)payload_cursor || node->mark != 0u ||
        node->forward_handle != 0u) {
      return 0u;
    }
    type = al_type_at(program, node->type_id);
    if (type == NULL) {
      return 0u;
    }

    for (field_index = 0u; field_index < type->field_count; ++field_index) {
      uint32_t field_type_id = type->field_types[field_index];
      uint64_t bits = al_read_u64(arena->data + node->payload_offset +
                                  field_index * (uint32_t)sizeof(int64_t));
      const al_type_desc *field_type = al_type_at(program, field_type_id);

      if (field_type == NULL ||
          !al_value_valid(ctx, program, field_type_id, bits, 1u)) {
        return 0u;
      }
      if (field_type->kind == AL_RUNTIME_TYPE_RECORD &&
          (uint32_t)bits >= node_index) {
        return 0u;
      }
    }

    payload_cursor += (uint64_t)node->payload_bytes;
    if (payload_cursor > arena->used) {
      return 0u;
    }
  }

  return payload_cursor == arena->used;
}

static uint32_t al_value_equal_scalar(uint32_t kind, uint64_t left,
                                      uint64_t right) {
  if (kind == AL_RUNTIME_TYPE_INT) {
    return left == right;
  }
  if (kind == AL_RUNTIME_TYPE_BOOL) {
    return left == right && left <= 1u;
  }
  if (kind == AL_RUNTIME_TYPE_UNIT) {
    return left == 0u && right == 0u;
  }
  return 0u;
}

static void al_clear_scratch_transients(al_arena *scratch) {
  uint32_t index;
  for (index = 0u; index < scratch->node_count; ++index) {
    scratch->nodes[index].mark = 0u;
    scratch->nodes[index].forward_handle = 0u;
  }
}

al_runtime_result
al_runtime_validate_request(al_runtime_context *ctx, int64_t *public_outputs,
                            uint32_t output_count, uint32_t output_capacity,
                            int32_t *status,
                            uint32_t expected_workspace_capacity) {
  al_span context_span;
  al_span output_span;
  al_span status_span;
  al_internal_spans internal;
  uint32_t index;

  if (ctx == NULL || !al_pointer_aligned(ctx, 8u) || status == NULL ||
      !al_pointer_aligned(status, 4u) ||
      !al_make_span(ctx, sizeof(*ctx), &context_span) ||
      !al_make_array_span(public_outputs, output_capacity,
                          (uint32_t)sizeof(int64_t), &output_span) ||
      !al_make_span(status, sizeof(*status), &status_span) ||
      !al_pointer_aligned(public_outputs, 8u) ||
      al_spans_overlap(&context_span, &status_span) ||
      al_spans_overlap(&output_span, &status_span)) {
    return AL_RUNTIME_INVALID_REQUEST;
  }

  if (ctx->abi_version != AL_RUNTIME_ABI_VERSION) {
    return AL_RUNTIME_INVALID_REQUEST;
  }

  if (!al_context_valid(ctx, &internal)) {
    return AL_RUNTIME_INVALID_REQUEST;
  }

  for (index = 0u; index < 8u; ++index) {
    if (al_spans_overlap(&output_span, &internal.values[index]) ||
        al_spans_overlap(&status_span, &internal.values[index])) {
      return AL_RUNTIME_INVALID_REQUEST;
    }
  }

  *status = AL_RUNTIME_STATUS_INVALID_REQUEST;
  if (output_count > output_capacity ||
      ctx->workspace_capacity != expected_workspace_capacity) {
    return AL_RUNTIME_INVALID_REQUEST;
  }

  *status = AL_RUNTIME_STATUS_SUCCESS;
  return AL_RUNTIME_OK;
}

al_runtime_result
al_runtime_make_record(al_runtime_context *ctx, const al_program_desc *program,
                       uint32_t type_id, const int64_t *fields,
                       uint32_t field_count, int64_t *out_handle) {
  al_internal_spans internal;
  const al_type_desc *type;
  al_span fields_span;
  al_span output_span;
  uint64_t payload_bytes;
  uint32_t field_index;
  al_arena *scratch;
  al_node *node;
  uint64_t handle;

  if (!al_context_valid(ctx, &internal) || !al_program_valid(ctx, program)) {
    return AL_RUNTIME_INVALID_REQUEST;
  }
  if (!al_scratch_graph_valid(ctx, program)) {
    return AL_RUNTIME_INVALID_REFERENCE;
  }
  type = al_type_at(program, type_id);
  if (type == NULL || type->kind != AL_RUNTIME_TYPE_RECORD ||
      type->field_count != field_count ||
      !al_workspace_contains(ctx, out_handle, sizeof(*out_handle), 8u) ||
      !al_make_array_span(fields, field_count, (uint32_t)sizeof(int64_t),
                          &fields_span) ||
      (field_count != 0u &&
       !al_span_contains(&internal.values[3], &fields_span)) ||
      (field_count == 0u && fields != NULL &&
       !al_pointer_aligned(fields, 8u)) ||
      !al_make_span(out_handle, sizeof(*out_handle), &output_span)) {
    return AL_RUNTIME_INVALID_REQUEST;
  }

  if (al_spans_overlap(&fields_span, &output_span)) {
    return AL_RUNTIME_INVALID_REQUEST;
  }

  for (field_index = 0u; field_index < field_count; ++field_index) {
    uint64_t value_bits = al_read_u64((const uint8_t *)fields +
                                      field_index * (uint32_t)sizeof(int64_t));
    if (!al_value_valid(ctx, program, type->field_types[field_index],
                        value_bits, 1u)) {
      const al_type_desc *field_type =
          al_type_at(program, type->field_types[field_index]);
      return field_type->kind == AL_RUNTIME_TYPE_RECORD
                 ? AL_RUNTIME_INVALID_REFERENCE
                 : AL_RUNTIME_INVALID_REQUEST;
    }
  }

  scratch = ctx->scratch;
  payload_bytes = (uint64_t)field_count * (uint64_t)sizeof(int64_t);
  if (scratch->node_count >= scratch->node_capacity ||
      (uint64_t)scratch->used + payload_bytes > scratch->byte_capacity) {
    ctx->error_argument0 = (int64_t)((uint64_t)scratch->used + payload_bytes);
    ctx->error_argument1 = (int64_t)((uint64_t)scratch->node_count + 1u);
    return AL_RUNTIME_SCRATCH_CAPACITY;
  }

  node = &scratch->nodes[scratch->node_count];
  node->type_id = type_id;
  node->field_count = field_count;
  node->payload_offset = scratch->used;
  node->payload_bytes = (uint32_t)payload_bytes;
  node->mark = 0u;
  node->reserved = 0u;
  node->forward_handle = 0u;
  for (field_index = 0u; field_index < field_count; ++field_index) {
    uint64_t value_bits = al_read_u64((const uint8_t *)fields +
                                      field_index * (uint32_t)sizeof(int64_t));
    al_arena_write_slot(scratch, node, field_index, value_bits);
  }

  handle = al_make_handle(scratch->generation, scratch->node_count + 1u);
  scratch->used += (uint32_t)payload_bytes;
  scratch->node_count += 1u;
  al_write_u64((uint8_t *)out_handle, handle);
  return AL_RUNTIME_OK;
}

al_runtime_result
al_runtime_get_field(al_runtime_context *ctx, const al_program_desc *program,
                     uint32_t expected_record_type_id, int64_t record_handle,
                     uint32_t field_index, int64_t *out_value) {
  al_internal_spans internal;
  const al_type_desc *record_type;
  const al_arena *arena;
  const al_node *node;
  uint64_t value_bits;

  if (!al_context_valid(ctx, &internal) || !al_program_valid(ctx, program)) {
    return AL_RUNTIME_INVALID_REQUEST;
  }
  if (!al_scratch_graph_valid(ctx, program)) {
    return AL_RUNTIME_INVALID_REFERENCE;
  }
  record_type = al_type_at(program, expected_record_type_id);
  if (record_type == NULL || record_type->kind != AL_RUNTIME_TYPE_RECORD ||
      !al_workspace_contains(ctx, out_value, sizeof(*out_value), 8u)) {
    return AL_RUNTIME_INVALID_REQUEST;
  }

  if (!al_resolve_handle(ctx, program, (uint64_t)record_handle,
                         expected_record_type_id, &arena, &node, NULL) ||
      arena != ctx->scratch) {
    return AL_RUNTIME_INVALID_REFERENCE;
  }
  if (field_index >= record_type->field_count) {
    return AL_RUNTIME_INVALID_REQUEST;
  }

  value_bits = al_read_u64(arena->data + node->payload_offset +
                           field_index * (uint32_t)sizeof(int64_t));
  al_write_u64((uint8_t *)out_value, value_bits);
  return AL_RUNTIME_OK;
}

al_runtime_result al_runtime_equal(al_runtime_context *ctx,
                                   const al_program_desc *program,
                                   uint32_t type_id, int64_t left,
                                   int64_t right, uint32_t *out_bool) {
  al_internal_spans internal;
  const al_type_desc *type;
  al_equal_frame stack[AL_RUNTIME_MAX_EQUAL_DEPTH];
  uint32_t depth = 0u;
  uint32_t compared_nodes = 0u;
  uint32_t equal = 1u;
  const al_arena *left_arena;
  const al_arena *right_arena;
  const al_node *left_node;
  const al_node *right_node;
  uint32_t left_index;
  uint32_t right_index;

  if (!al_context_valid(ctx, &internal) || !al_program_valid(ctx, program)) {
    return AL_RUNTIME_INVALID_REQUEST;
  }
  if (!al_scratch_graph_valid(ctx, program)) {
    return AL_RUNTIME_INVALID_REFERENCE;
  }
  type = al_type_at(program, type_id);
  if (type == NULL ||
      !al_workspace_contains(ctx, out_bool, sizeof(*out_bool), 4u)) {
    return AL_RUNTIME_INVALID_REQUEST;
  }

  if (!al_value_valid(ctx, program, type_id, (uint64_t)left, 0u) ||
      !al_value_valid(ctx, program, type_id, (uint64_t)right, 0u)) {
    return type->kind == AL_RUNTIME_TYPE_RECORD ? AL_RUNTIME_INVALID_REFERENCE
                                                : AL_RUNTIME_INVALID_REQUEST;
  }

  if (type->kind != AL_RUNTIME_TYPE_RECORD) {
    equal = al_value_equal_scalar(type->kind, (uint64_t)left, (uint64_t)right);
  } else {
    if (!al_resolve_handle(ctx, program, (uint64_t)left, type_id, &left_arena,
                           &left_node, &left_index) ||
        !al_resolve_handle(ctx, program, (uint64_t)right, type_id, &right_arena,
                           &right_node, &right_index) ||
        left_arena != ctx->scratch || right_arena != ctx->scratch) {
      return AL_RUNTIME_INVALID_REFERENCE;
    }
    (void)left_node;
    (void)right_node;
    if (left_index != right_index) {
      stack[depth].left_index = left_index;
      stack[depth].right_index = right_index;
      stack[depth].field_index = 0u;
      depth += 1u;
      compared_nodes = 1u;
    }
  }

  while (depth != 0u && equal != 0u) {
    al_equal_frame *frame = &stack[depth - 1u];
    const al_node *frame_left = &ctx->scratch->nodes[frame->left_index - 1u];
    const al_node *frame_right = &ctx->scratch->nodes[frame->right_index - 1u];
    const al_type_desc *frame_type = al_type_at(program, frame_left->type_id);

    if (frame->field_index >= frame_type->field_count) {
      depth -= 1u;
    } else {
      uint32_t current_field = frame->field_index;
      uint32_t field_type_id = frame_type->field_types[current_field];
      const al_type_desc *field_type = al_type_at(program, field_type_id);
      uint64_t left_bits =
          al_read_u64(ctx->scratch->data + frame_left->payload_offset +
                      current_field * (uint32_t)sizeof(int64_t));
      uint64_t right_bits =
          al_read_u64(ctx->scratch->data + frame_right->payload_offset +
                      current_field * (uint32_t)sizeof(int64_t));

      frame->field_index += 1u;
      if (field_type->kind == AL_RUNTIME_TYPE_RECORD) {
        uint32_t child_left_index = (uint32_t)left_bits;
        uint32_t child_right_index = (uint32_t)right_bits;
        if (child_left_index != child_right_index) {
          if (depth >= AL_RUNTIME_MAX_EQUAL_DEPTH ||
              compared_nodes >= AL_RUNTIME_MAX_EQUAL_NODES) {
            return AL_RUNTIME_INVALID_REFERENCE;
          }
          stack[depth].left_index = child_left_index;
          stack[depth].right_index = child_right_index;
          stack[depth].field_index = 0u;
          depth += 1u;
          compared_nodes += 1u;
        }
      } else if (!al_value_equal_scalar(field_type->kind, left_bits,
                                        right_bits)) {
        equal = 0u;
      }
    }
  }

  al_write_u32((uint8_t *)out_bool, equal);
  return AL_RUNTIME_OK;
}

al_runtime_result al_runtime_promote(al_runtime_context *ctx,
                                     const al_program_desc *program,
                                     const int64_t *scratch_roots,
                                     const uint32_t *root_type_ids,
                                     uint32_t root_count, int64_t *public_roots,
                                     uint32_t public_capacity) {
  al_internal_spans internal;
  al_span roots_span;
  al_span root_types_span;
  al_span public_span;
  uint64_t required_bytes = 0u;
  uint32_t required_nodes = 0u;
  uint32_t root_index;
  uint32_t source_index;
  uint32_t destination_index;
  uint32_t payload_cursor = 0u;
  al_arena *scratch;
  al_arena *retained;

  if (!al_context_valid(ctx, &internal) || !al_program_valid(ctx, program)) {
    return AL_RUNTIME_INVALID_REQUEST;
  }
  if (!al_scratch_graph_valid(ctx, program)) {
    return AL_RUNTIME_INVALID_REFERENCE;
  }
  if (scratch_roots != ctx->workspace || root_count > ctx->workspace_capacity ||
      root_count > public_capacity ||
      !al_make_array_span(scratch_roots, root_count, (uint32_t)sizeof(int64_t),
                          &roots_span) ||
      !al_make_array_span(root_type_ids, root_count, (uint32_t)sizeof(uint32_t),
                          &root_types_span) ||
      !al_make_array_span(public_roots, public_capacity,
                          (uint32_t)sizeof(int64_t), &public_span) ||
      !al_pointer_aligned(root_type_ids, 4u) ||
      !al_pointer_aligned(public_roots, 8u) ||
      (root_count != 0u && (root_type_ids == NULL || public_roots == NULL)) ||
      !al_span_disjoint_from_context(ctx, &root_types_span) ||
      !al_span_disjoint_from_context(ctx, &public_span) ||
      !al_program_disjoint_from_span(program, &root_types_span) ||
      !al_program_disjoint_from_span(program, &public_span) ||
      al_spans_overlap(&root_types_span, &public_span) ||
      al_spans_overlap(&roots_span, &public_span) ||
      al_spans_overlap(&roots_span, &root_types_span)) {
    return AL_RUNTIME_INVALID_REQUEST;
  }

  scratch = ctx->scratch;
  retained = ctx->retained;
  for (root_index = 0u; root_index < root_count; ++root_index) {
    uint32_t root_type_id = root_type_ids[root_index];
    const al_type_desc *root_type = al_type_at(program, root_type_id);
    uint64_t root_bits = al_read_u64((const uint8_t *)scratch_roots +
                                     (size_t)root_index * sizeof(int64_t));
    if (root_type == NULL) {
      return AL_RUNTIME_INVALID_REQUEST;
    }
    if (!al_value_valid(ctx, program, root_type_id, root_bits, 1u)) {
      return root_type->kind == AL_RUNTIME_TYPE_RECORD
                 ? AL_RUNTIME_INVALID_REFERENCE
                 : AL_RUNTIME_INVALID_REQUEST;
    }
  }

  for (root_index = 0u; root_index < root_count; ++root_index) {
    const al_type_desc *root_type =
        al_type_at(program, root_type_ids[root_index]);
    if (root_type->kind == AL_RUNTIME_TYPE_RECORD) {
      uint32_t node_index =
          (uint32_t)al_read_u64((const uint8_t *)scratch_roots +
                                (size_t)root_index * sizeof(int64_t));
      scratch->nodes[node_index - 1u].mark = 1u;
    }
  }

  for (source_index = scratch->node_count; source_index != 0u; --source_index) {
    al_node *node = &scratch->nodes[source_index - 1u];
    const al_type_desc *type;
    uint32_t field_index;
    if (node->mark == 0u) {
      continue;
    }
    type = al_type_at(program, node->type_id);
    for (field_index = 0u; field_index < type->field_count; ++field_index) {
      uint32_t field_type_id = type->field_types[field_index];
      const al_type_desc *field_type = al_type_at(program, field_type_id);
      if (field_type->kind == AL_RUNTIME_TYPE_RECORD) {
        uint32_t child_index =
            (uint32_t)al_read_u64(scratch->data + node->payload_offset +
                                  field_index * (uint32_t)sizeof(int64_t));
        scratch->nodes[child_index - 1u].mark = 1u;
      }
    }
  }

  for (source_index = 0u; source_index < scratch->node_count; ++source_index) {
    const al_node *node = &scratch->nodes[source_index];
    if (node->mark != 0u) {
      ++required_nodes;
      required_bytes += (uint64_t)node->payload_bytes;
    }
  }
  if (required_bytes > UINT32_MAX || required_nodes > retained->node_capacity ||
      required_bytes > retained->byte_capacity) {
    ctx->error_argument0 = (int64_t)required_bytes;
    ctx->error_argument1 = (int64_t)required_nodes;
    al_clear_scratch_transients(scratch);
    return AL_RUNTIME_RETAINED_CAPACITY;
  }

  destination_index = 0u;
  for (source_index = 0u; source_index < scratch->node_count; ++source_index) {
    al_node *node = &scratch->nodes[source_index];
    if (node->mark != 0u) {
      if (destination_index == UINT32_MAX) {
        al_clear_scratch_transients(scratch);
        return AL_RUNTIME_RETAINED_CAPACITY;
      }
      destination_index += 1u;
      node->forward_handle =
          al_make_handle(retained->generation, destination_index);
    }
  }

  for (source_index = 0u; source_index < scratch->node_count; ++source_index) {
    const al_node *node = &scratch->nodes[source_index];
    const al_type_desc *type;
    uint32_t field_index;
    if (node->mark == 0u) {
      continue;
    }
    type = al_type_at(program, node->type_id);
    for (field_index = 0u; field_index < type->field_count; ++field_index) {
      if (al_type_at(program, type->field_types[field_index])->kind ==
          AL_RUNTIME_TYPE_RECORD) {
        uint32_t child_index =
            (uint32_t)al_read_u64(scratch->data + node->payload_offset +
                                  field_index * (uint32_t)sizeof(int64_t));
        if (child_index == 0u || child_index >= source_index + 1u ||
            scratch->nodes[child_index - 1u].mark == 0u ||
            scratch->nodes[child_index - 1u].forward_handle == 0u) {
          al_clear_scratch_transients(scratch);
          return AL_RUNTIME_INVALID_REFERENCE;
        }
      }
    }
  }

  destination_index = 0u;
  payload_cursor = 0u;
  for (source_index = 0u; source_index < scratch->node_count; ++source_index) {
    const al_node *source_node = &scratch->nodes[source_index];
    al_node *destination_node;
    const al_type_desc *type;
    uint32_t field_index;
    if (source_node->mark == 0u) {
      continue;
    }

    destination_index += 1u;
    destination_node = &retained->nodes[destination_index - 1u];
    type = al_type_at(program, source_node->type_id);
    destination_node->type_id = source_node->type_id;
    destination_node->field_count = source_node->field_count;
    destination_node->payload_offset = payload_cursor;
    destination_node->payload_bytes = source_node->payload_bytes;
    destination_node->mark = 0u;
    destination_node->reserved = 0u;
    destination_node->forward_handle = 0u;

    for (field_index = 0u; field_index < source_node->field_count;
         ++field_index) {
      uint64_t value_bits =
          al_read_u64(scratch->data + source_node->payload_offset +
                      field_index * (uint32_t)sizeof(int64_t));
      if (al_type_at(program, type->field_types[field_index])->kind ==
          AL_RUNTIME_TYPE_RECORD) {
        uint32_t child_index = (uint32_t)value_bits;
        value_bits = scratch->nodes[child_index - 1u].forward_handle;
      }
      al_arena_write_slot(retained, destination_node, field_index, value_bits);
    }
    payload_cursor += source_node->payload_bytes;
  }

  retained->used = (uint32_t)required_bytes;
  retained->node_count = required_nodes;

  for (root_index = 0u; root_index < root_count; ++root_index) {
    const al_type_desc *root_type =
        al_type_at(program, root_type_ids[root_index]);
    uint64_t root_bits = al_read_u64((const uint8_t *)scratch_roots +
                                     (size_t)root_index * sizeof(int64_t));
    if (root_type->kind == AL_RUNTIME_TYPE_RECORD) {
      uint32_t node_index = (uint32_t)root_bits;
      root_bits = scratch->nodes[node_index - 1u].forward_handle;
    }
    al_write_u64((uint8_t *)public_roots + (size_t)root_index * sizeof(int64_t),
                 root_bits);
  }

  al_clear_scratch_transients(scratch);
  return AL_RUNTIME_OK;
}
