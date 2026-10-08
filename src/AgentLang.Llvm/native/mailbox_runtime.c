#include "mailbox_runtime.h"

#include <limits.h>
#include <string.h>

#define AL_MAILBOX_RUNTIME_MAGIC UINT64_C(0x414c4d424f583031)
#define AL_MAILBOX_POISON_BYTE 0xA5u
#define AL_MAILBOX_MAX_ROOTS 2u
#define AL_MAILBOX_MAX_INPUTS 3u
#define AL_MAILBOX_ALIGNMENT 8u

typedef struct al_mailbox_bank {
  al_arena owner;
  int64_t roots[AL_MAILBOX_MAX_ROOTS];
  uint32_t root_type_ids[AL_MAILBOX_MAX_ROOTS];
  uint32_t root_count;
  uint32_t reserved;
} al_mailbox_bank;

typedef struct al_mailbox_slot {
  uint32_t initialized;
  uint32_t pending;
  uint32_t active_bank;
  uint32_t last_completed_valid;
  al_mailbox_token pending_token;
  al_mailbox_token last_completed_token;
  al_mailbox_bank banks[2];
} al_mailbox_slot;

typedef struct al_mailbox_layout {
  size_t total_bytes;
  size_t slots_offset;
  size_t scratch_data_offset;
  size_t scratch_nodes_offset;
  size_t workspace_offset;
  size_t bank_backing_offset;
  size_t bank_stride;
  size_t bank_data_offset[2];
  size_t bank_nodes_offset[2];
  uint32_t workspace_capacity;
  uint64_t retained_reserved_bytes;
  uint64_t scratch_reserved_bytes;
  uint64_t workspace_reserved_bytes;
  uint64_t controller_reserved_bytes;
} al_mailbox_layout;

struct al_mailbox_runtime {
  uint64_t magic;
  const al_module_desc *module;
  const al_program_desc *program;
  const al_module_entry *entries[3];
  al_mailbox_config config;
  uint8_t fingerprint[AL_MODULE_FINGERPRINT_BYTES];
  al_mailbox_slot *mailboxes;
  al_arena scratch;
  uint8_t *scratch_data;
  al_node *scratch_nodes;
  int64_t *workspace;
  int64_t invocation_values[AL_MAILBOX_MAX_INPUTS];
  uint32_t invocation_type_ids[AL_MAILBOX_MAX_INPUTS];
  al_runtime_context context;
  uint32_t owner_thread_id;
  uint32_t state_type_id;
  uint32_t continuation_type_id;
  uint32_t int_type_id;
  uint32_t workspace_capacity;
  uint32_t disposed;
  uint32_t busy;
  uint32_t outstanding_scratch_leases;
  uint32_t sequence_exhausted;
  uint64_t runtime_instance_id;
  uint64_t next_token_sequence;
  uint64_t next_generation;
  uint64_t storage_reserved_bytes;
  uint64_t retained_reserved_bytes;
  uint64_t scratch_reserved_bytes;
  uint64_t workspace_reserved_bytes;
  uint64_t scratch_high_water_bytes;
  uint64_t scratch_high_water_nodes;
  uint64_t handler_invocations;
  uint64_t handler_failures;
  uint64_t scratch_lease_acquisitions;
  uint64_t scratch_lease_returns;
};

uint32_t al_mailbox_platform_current_thread_id(void);
int32_t al_mailbox_platform_next_instance_id(uint64_t *out_instance_id);

static uint32_t al_pointer_aligned(const void *pointer, uintptr_t alignment) {
  return pointer == NULL || (((uintptr_t)pointer & (alignment - 1u)) == 0u);
}

static uint32_t al_checked_span(const void *pointer, uint64_t byte_count) {
  uintptr_t begin = (uintptr_t)pointer;
  if (byte_count == 0u) {
    return 1u;
  }
  return pointer != NULL && byte_count <= (uint64_t)(UINTPTR_MAX - begin);
}

static uint32_t al_span_overlaps(const void *left, uint64_t left_bytes,
                                 const void *right, uint64_t right_bytes) {
  uintptr_t left_begin;
  uintptr_t right_begin;
  uintptr_t left_end;
  uintptr_t right_end;

  if (left_bytes == 0u || right_bytes == 0u) {
    return 0u;
  }
  left_begin = (uintptr_t)left;
  right_begin = (uintptr_t)right;
  left_end = left_begin + (uintptr_t)left_bytes;
  right_end = right_begin + (uintptr_t)right_bytes;
  return left_begin < right_end && right_begin < left_end;
}

static uint32_t al_mul_u64(uint64_t left, uint64_t right, uint64_t *out) {
  if (out == NULL || (right != 0u && left > UINT64_MAX / right)) {
    return 0u;
  }
  *out = left * right;
  return 1u;
}

static uint32_t al_add_u64(uint64_t left, uint64_t right, uint64_t *out) {
  if (out == NULL || left > UINT64_MAX - right) {
    return 0u;
  }
  *out = left + right;
  return 1u;
}

static uint32_t al_append_region(size_t *offset, size_t byte_count,
                                 size_t alignment, size_t *region_offset) {
  size_t aligned;
  if (offset == NULL || alignment == 0u ||
      (alignment & (alignment - 1u)) != 0u ||
      *offset > SIZE_MAX - (alignment - 1u)) {
    return 0u;
  }
  aligned = (*offset + (alignment - 1u)) & ~(alignment - 1u);
  if (byte_count > SIZE_MAX - aligned) {
    return 0u;
  }
  if (region_offset != NULL) {
    *region_offset = aligned;
  }
  *offset = aligned + byte_count;
  return 1u;
}

static uint32_t al_checked_array_bytes(uint32_t count, size_t element_size,
                                       size_t *out_bytes) {
  if (out_bytes == NULL ||
      (element_size != 0u && (size_t)count > SIZE_MAX / element_size)) {
    return 0u;
  }
  *out_bytes = (size_t)count * element_size;
  return 1u;
}

static uint32_t al_string_valid(const char *value) {
  return value != NULL && value[0] != '\0';
}

static uint32_t al_type_desc_valid(const al_program_desc *program,
                                   uint32_t type_id) {
  const al_type_desc *type;
  uint32_t field_index;
  size_t fields_bytes;

  if (program == NULL || type_id >= program->type_count) {
    return 0u;
  }
  type = &program->types[type_id];
  if (type->kind < AL_RUNTIME_TYPE_INT || type->kind > AL_RUNTIME_TYPE_RECORD) {
    return 0u;
  }
  if (type->kind != AL_RUNTIME_TYPE_RECORD) {
    return type->field_count == 0u && type->field_types == NULL;
  }
  if ((type->field_count == 0u && type->field_types != NULL) ||
      (type->field_count != 0u &&
       (!al_pointer_aligned(type->field_types, 4u) ||
        !al_checked_array_bytes(type->field_count, sizeof(uint32_t),
                                &fields_bytes) ||
        !al_checked_span(type->field_types, fields_bytes)))) {
    return 0u;
  }
  for (field_index = 0u; field_index < type->field_count; ++field_index) {
    if (type->field_types[field_index] >= program->type_count) {
      return 0u;
    }
  }
  return 1u;
}

static uint32_t al_entry_metadata_valid(const al_module_desc *module,
                                        uint32_t index) {
  const al_module_entry *entry;
  size_t input_bytes;
  size_t output_bytes;
  size_t diagnostics_bytes;
  uint32_t type_index;

  entry = &module->entries[index];
  if (entry->struct_size != sizeof(al_module_entry) ||
      entry->entry_id != index || !al_string_valid(entry->name) ||
      entry->execute == NULL ||
      entry->workspace_capacity < entry->input_count ||
      entry->workspace_capacity < entry->output_count ||
      (entry->input_count != 0u &&
       (!al_pointer_aligned(entry->input_type_ids, 4u) ||
        !al_checked_array_bytes(entry->input_count, sizeof(uint32_t),
                                &input_bytes) ||
        !al_checked_span(entry->input_type_ids, input_bytes))) ||
      (entry->input_count == 0u && entry->input_type_ids != NULL) ||
      (entry->output_count != 0u &&
       (!al_pointer_aligned(entry->output_type_ids, 4u) ||
        !al_checked_array_bytes(entry->output_count, sizeof(uint32_t),
                                &output_bytes) ||
        !al_checked_span(entry->output_type_ids, output_bytes))) ||
      (entry->output_count == 0u && entry->output_type_ids != NULL) ||
      (entry->diagnostic_count != 0u &&
       (!al_pointer_aligned(entry->diagnostics, 8u) ||
        !al_checked_array_bytes(entry->diagnostic_count,
                                sizeof(al_module_diagnostic),
                                &diagnostics_bytes) ||
        !al_checked_span(entry->diagnostics, diagnostics_bytes))) ||
      (entry->diagnostic_count == 0u && entry->diagnostics != NULL)) {
    return 0u;
  }
  for (type_index = 0u; type_index < entry->input_count; ++type_index) {
    if (entry->input_type_ids[type_index] >= module->program->type_count) {
      return 0u;
    }
  }
  for (type_index = 0u; type_index < entry->output_count; ++type_index) {
    if (entry->output_type_ids[type_index] >= module->program->type_count) {
      return 0u;
    }
  }
  for (type_index = 0u; type_index < entry->diagnostic_count; ++type_index) {
    if (!al_string_valid(entry->diagnostics[type_index].code)) {
      return 0u;
    }
  }
  return 1u;
}

static uint32_t al_module_metadata_valid(const al_module_desc *module) {
  size_t entry_bytes;
  size_t type_bytes;
  size_t names_bytes;
  uint32_t index;
  uint32_t fingerprint_nonzero = 0u;

  if (module == NULL || !al_pointer_aligned(module, 8u) ||
      module->abi_version != AL_MODULE_ABI_VERSION ||
      module->struct_size != sizeof(al_module_desc) ||
      module->runtime_abi_version != AL_RUNTIME_ABI_VERSION ||
      module->entry_count == 0u || module->program == NULL ||
      !al_pointer_aligned(module->program, 8u) ||
      !al_pointer_aligned(module->entries, 8u) ||
      !al_pointer_aligned(module->type_names, 8u) ||
      module->program->reserved != 0u || module->program->type_count < 3u ||
      !al_pointer_aligned(module->program->types, 8u) ||
      !al_checked_array_bytes(module->entry_count, sizeof(al_module_entry),
                              &entry_bytes) ||
      !al_checked_span(module->entries, entry_bytes) ||
      !al_checked_array_bytes(module->program->type_count, sizeof(al_type_desc),
                              &type_bytes) ||
      !al_checked_span(module->program->types, type_bytes) ||
      !al_checked_array_bytes(module->program->type_count, sizeof(char *),
                              &names_bytes) ||
      !al_checked_span(module->type_names, names_bytes)) {
    return 0u;
  }
  for (index = 0u; index < AL_MODULE_FINGERPRINT_BYTES; ++index) {
    fingerprint_nonzero |= module->fingerprint[index];
  }
  if (fingerprint_nonzero == 0u) {
    return 0u;
  }
  if (!al_string_valid(module->type_names[0]) ||
      !al_string_valid(module->type_names[1]) ||
      !al_string_valid(module->type_names[2]) ||
      strcmp(module->type_names[0], "Int") != 0 ||
      strcmp(module->type_names[1], "Bool") != 0 ||
      strcmp(module->type_names[2], "Unit") != 0 ||
      module->program->types[0].kind != AL_RUNTIME_TYPE_INT ||
      module->program->types[1].kind != AL_RUNTIME_TYPE_BOOL ||
      module->program->types[2].kind != AL_RUNTIME_TYPE_UNIT) {
    return 0u;
  }
  for (index = 0u; index < module->program->type_count; ++index) {
    if (!al_string_valid(module->type_names[index]) ||
        !al_type_desc_valid(module->program, index)) {
      return 0u;
    }
  }
  for (index = 0u; index < module->entry_count; ++index) {
    uint32_t prior;
    if (!al_entry_metadata_valid(module, index)) {
      return 0u;
    }
    for (prior = 0u; prior < index; ++prior) {
      if (strcmp(module->entries[prior].name, module->entries[index].name) ==
          0) {
        return 0u;
      }
    }
  }
  return 1u;
}

static al_mailbox_result al_validate_bindings(
    const al_module_desc *module, const al_mailbox_config *config,
    uint32_t *out_state_type_id, uint32_t *out_continuation_type_id,
    uint32_t *out_int_type_id, uint32_t *out_workspace_capacity) {
  const al_module_entry *init_entry;
  const al_module_entry *begin_entry;
  const al_module_entry *resume_entry;
  const al_type_desc *state_type;
  const al_type_desc *continuation_type;
  uint32_t state_type_id;
  uint32_t continuation_type_id;
  uint32_t int_type_id;
  uint32_t workspace_capacity;

  if (config == NULL || !al_pointer_aligned(config, 4u) ||
      config->control_abi_version != AL_MAILBOX_CONTROL_ABI_VERSION ||
      config->struct_size != sizeof(al_mailbox_config) ||
      config->mailbox_capacity == 0u || config->scratch_node_capacity == 0u ||
      config->retained_node_capacity == 0u || config->reserved[0] != 0u ||
      config->reserved[1] != 0u) {
    return AL_MAILBOX_INVALID_ARGUMENT;
  }
  if (!al_module_metadata_valid(module)) {
    return AL_MAILBOX_INVALID_MODULE;
  }
  if (config->init_entry_id >= module->entry_count ||
      config->begin_entry_id >= module->entry_count ||
      config->resume_entry_id >= module->entry_count ||
      config->init_entry_id == config->begin_entry_id ||
      config->init_entry_id == config->resume_entry_id ||
      config->begin_entry_id == config->resume_entry_id) {
    return AL_MAILBOX_INVALID_MODULE;
  }
  init_entry = &module->entries[config->init_entry_id];
  begin_entry = &module->entries[config->begin_entry_id];
  resume_entry = &module->entries[config->resume_entry_id];
  if (init_entry->input_count != 1u || init_entry->output_count != 1u ||
      begin_entry->input_count != 2u || begin_entry->output_count != 2u ||
      resume_entry->input_count != 3u || resume_entry->output_count != 1u) {
    return AL_MAILBOX_INVALID_MODULE;
  }
  int_type_id = init_entry->input_type_ids[0];
  state_type_id = init_entry->output_type_ids[0];
  continuation_type_id = begin_entry->output_type_ids[1];
  if (int_type_id != 0u ||
      module->program->types[int_type_id].kind != AL_RUNTIME_TYPE_INT ||
      state_type_id == continuation_type_id ||
      module->program->types[state_type_id].kind != AL_RUNTIME_TYPE_RECORD ||
      module->program->types[continuation_type_id].kind !=
          AL_RUNTIME_TYPE_RECORD ||
      begin_entry->input_type_ids[0] != state_type_id ||
      begin_entry->input_type_ids[1] != int_type_id ||
      begin_entry->output_type_ids[0] != state_type_id ||
      resume_entry->input_type_ids[0] != state_type_id ||
      resume_entry->input_type_ids[1] != continuation_type_id ||
      resume_entry->input_type_ids[2] != int_type_id ||
      resume_entry->output_type_ids[0] != state_type_id) {
    return AL_MAILBOX_INVALID_MODULE;
  }
  state_type = &module->program->types[state_type_id];
  continuation_type = &module->program->types[continuation_type_id];
  if (state_type->kind != AL_RUNTIME_TYPE_RECORD ||
      continuation_type->kind != AL_RUNTIME_TYPE_RECORD) {
    return AL_MAILBOX_INVALID_MODULE;
  }
  workspace_capacity = init_entry->workspace_capacity;
  if (workspace_capacity < begin_entry->workspace_capacity) {
    workspace_capacity = begin_entry->workspace_capacity;
  }
  if (workspace_capacity < resume_entry->workspace_capacity) {
    workspace_capacity = resume_entry->workspace_capacity;
  }
  if (out_state_type_id != NULL) {
    *out_state_type_id = state_type_id;
  }
  if (out_continuation_type_id != NULL) {
    *out_continuation_type_id = continuation_type_id;
  }
  if (out_int_type_id != NULL) {
    *out_int_type_id = int_type_id;
  }
  if (out_workspace_capacity != NULL) {
    *out_workspace_capacity = workspace_capacity;
  }
  return AL_MAILBOX_OK;
}

static al_mailbox_result al_compute_layout(const al_module_desc *module,
                                           const al_mailbox_config *config,
                                           al_mailbox_layout *layout) {
  uint64_t node_bytes64;
  uint64_t per_mailbox_backing64;
  uint64_t retained_total64;
  uint64_t scratch_total64;
  uint64_t workspace_total64;
  uint64_t backing_sum64;
  uint64_t retained_node_bytes64;
  uint64_t scratch_node_bytes64;
  uint64_t per_bank_bytes64;
  size_t node_bytes;
  size_t slot_bytes;
  size_t workspace_bytes;
  size_t offset = 0u;
  size_t per_mailbox_offset = 0u;
  uint32_t type_id;
  al_mailbox_result result;

  if (layout == NULL) {
    return AL_MAILBOX_INVALID_ARGUMENT;
  }
  memset(layout, 0, sizeof(*layout));
  result = al_validate_bindings(module, config, NULL, NULL, NULL,
                                &layout->workspace_capacity);
  if (result != AL_MAILBOX_OK) {
    return result;
  }
  if (!al_mul_u64(config->scratch_node_capacity, sizeof(al_node),
                  &node_bytes64) ||
      node_bytes64 > SIZE_MAX) {
    return AL_MAILBOX_SIZE_OVERFLOW;
  }
  node_bytes = (size_t)node_bytes64;
  if (!al_mul_u64(config->retained_node_capacity, sizeof(al_node),
                  &node_bytes64) ||
      node_bytes64 > SIZE_MAX) {
    return AL_MAILBOX_SIZE_OVERFLOW;
  }
  if (!al_checked_array_bytes(layout->workspace_capacity, sizeof(int64_t),
                              &workspace_bytes) ||
      !al_checked_array_bytes(config->mailbox_capacity, sizeof(al_mailbox_slot),
                              &slot_bytes)) {
    return AL_MAILBOX_SIZE_OVERFLOW;
  }

  if (!al_append_region(&offset, sizeof(al_mailbox_runtime),
                        AL_MAILBOX_ALIGNMENT, NULL) ||
      !al_append_region(&offset, slot_bytes, AL_MAILBOX_ALIGNMENT,
                        &layout->slots_offset) ||
      !al_append_region(&offset, config->scratch_byte_capacity,
                        AL_MAILBOX_ALIGNMENT, &layout->scratch_data_offset) ||
      !al_append_region(&offset, node_bytes, AL_MAILBOX_ALIGNMENT,
                        &layout->scratch_nodes_offset) ||
      !al_append_region(&offset, workspace_bytes, AL_MAILBOX_ALIGNMENT,
                        &layout->workspace_offset)) {
    return AL_MAILBOX_SIZE_OVERFLOW;
  }
  layout->bank_backing_offset = offset;
  for (type_id = 0u; type_id < 2u; ++type_id) {
    if (!al_append_region(&per_mailbox_offset, config->retained_byte_capacity,
                          AL_MAILBOX_ALIGNMENT,
                          &layout->bank_data_offset[type_id]) ||
        !al_append_region(&per_mailbox_offset,
                          (size_t)((uint64_t)config->retained_node_capacity *
                                   sizeof(al_node)),
                          AL_MAILBOX_ALIGNMENT,
                          &layout->bank_nodes_offset[type_id])) {
      return AL_MAILBOX_SIZE_OVERFLOW;
    }
  }
  layout->bank_stride = per_mailbox_offset;
  if (layout->bank_stride != 0u &&
      (size_t)config->mailbox_capacity >
          (SIZE_MAX - offset) / layout->bank_stride) {
    return AL_MAILBOX_SIZE_OVERFLOW;
  }
  offset += layout->bank_stride * (size_t)config->mailbox_capacity;
  layout->total_bytes = offset;

  if (!al_mul_u64(config->retained_node_capacity, sizeof(al_node),
                  &retained_node_bytes64) ||
      !al_add_u64(sizeof(al_arena), config->retained_byte_capacity,
                  &per_bank_bytes64) ||
      !al_add_u64(per_bank_bytes64, retained_node_bytes64, &per_bank_bytes64) ||
      !al_mul_u64(per_bank_bytes64, 2u, &per_mailbox_backing64) ||
      !al_mul_u64(per_mailbox_backing64, config->mailbox_capacity,
                  &retained_total64) ||
      !al_mul_u64(config->scratch_node_capacity, sizeof(al_node),
                  &scratch_node_bytes64) ||
      !al_add_u64(sizeof(al_arena), config->scratch_byte_capacity,
                  &scratch_total64) ||
      !al_add_u64(scratch_total64, scratch_node_bytes64, &scratch_total64) ||
      !al_mul_u64(layout->workspace_capacity, sizeof(int64_t),
                  &workspace_total64) ||
      !al_add_u64(retained_total64, scratch_total64, &backing_sum64) ||
      !al_add_u64(backing_sum64, workspace_total64, &backing_sum64) ||
      backing_sum64 > layout->total_bytes) {
    return AL_MAILBOX_SIZE_OVERFLOW;
  }
  layout->retained_reserved_bytes = retained_total64;
  layout->scratch_reserved_bytes = scratch_total64;
  layout->workspace_reserved_bytes = workspace_total64;
  layout->controller_reserved_bytes = layout->total_bytes - backing_sum64;
  return AL_MAILBOX_OK;
}

static uint32_t al_storage_overlaps_module(const void *storage,
                                           uint64_t storage_bytes,
                                           const al_module_desc *module) {
  uint32_t index;
  size_t count_bytes;

  if (al_span_overlaps(storage, storage_bytes, module, sizeof(*module)) ||
      al_span_overlaps(storage, storage_bytes, module->program,
                       sizeof(*module->program)) ||
      !al_checked_array_bytes(module->entry_count, sizeof(al_module_entry),
                              &count_bytes) ||
      al_span_overlaps(storage, storage_bytes, module->entries, count_bytes) ||
      !al_checked_array_bytes(module->program->type_count, sizeof(al_type_desc),
                              &count_bytes) ||
      al_span_overlaps(storage, storage_bytes, module->program->types,
                       count_bytes) ||
      !al_checked_array_bytes(module->program->type_count, sizeof(char *),
                              &count_bytes) ||
      al_span_overlaps(storage, storage_bytes, module->type_names,
                       count_bytes)) {
    return 1u;
  }
  for (index = 0u; index < module->program->type_count; ++index) {
    const al_type_desc *type = &module->program->types[index];
    if (type->field_count != 0u) {
      (void)al_checked_array_bytes(type->field_count, sizeof(uint32_t),
                                   &count_bytes);
      if (al_span_overlaps(storage, storage_bytes, type->field_types,
                           count_bytes)) {
        return 1u;
      }
    }
  }
  for (index = 0u; index < module->entry_count; ++index) {
    const al_module_entry *entry = &module->entries[index];
    if (entry->input_count != 0u) {
      (void)al_checked_array_bytes(entry->input_count, sizeof(uint32_t),
                                   &count_bytes);
      if (al_span_overlaps(storage, storage_bytes, entry->input_type_ids,
                           count_bytes)) {
        return 1u;
      }
    }
    if (entry->output_count != 0u) {
      (void)al_checked_array_bytes(entry->output_count, sizeof(uint32_t),
                                   &count_bytes);
      if (al_span_overlaps(storage, storage_bytes, entry->output_type_ids,
                           count_bytes)) {
        return 1u;
      }
    }
    if (entry->diagnostic_count != 0u) {
      (void)al_checked_array_bytes(entry->diagnostic_count,
                                   sizeof(al_module_diagnostic), &count_bytes);
      if (al_span_overlaps(storage, storage_bytes, entry->diagnostics,
                           count_bytes)) {
        return 1u;
      }
    }
  }
  return 0u;
}

static uint32_t al_runtime_binding_valid(const al_mailbox_runtime *runtime) {
  uint32_t index;
  if (runtime == NULL || runtime->magic != AL_MAILBOX_RUNTIME_MAGIC ||
      runtime->module == NULL ||
      runtime->module->abi_version != AL_MODULE_ABI_VERSION ||
      runtime->module->struct_size != sizeof(al_module_desc) ||
      runtime->module->runtime_abi_version != AL_RUNTIME_ABI_VERSION ||
      runtime->module->program != runtime->program ||
      runtime->module->entries == NULL ||
      memcmp(runtime->module->fingerprint, runtime->fingerprint,
             AL_MODULE_FINGERPRINT_BYTES) != 0) {
    return 0u;
  }
  for (index = 0u; index < 3u; ++index) {
    uint32_t id = index == 0u   ? runtime->config.init_entry_id
                  : index == 1u ? runtime->config.begin_entry_id
                                : runtime->config.resume_entry_id;
    if (id >= runtime->module->entry_count ||
        &runtime->module->entries[id] != runtime->entries[index] ||
        runtime->entries[index]->entry_id != id ||
        runtime->entries[index]->execute == NULL) {
      return 0u;
    }
  }
  return 1u;
}

static al_mailbox_result al_runtime_access(al_mailbox_runtime *runtime,
                                           uint32_t current_thread_id,
                                           uint32_t allow_disposed) {
  if (runtime == NULL || runtime->magic != AL_MAILBOX_RUNTIME_MAGIC) {
    return AL_MAILBOX_INVALID_ARGUMENT;
  }
  if (current_thread_id != runtime->owner_thread_id) {
    return AL_MAILBOX_WRONG_THREAD;
  }
  if (runtime->disposed != 0u) {
    return allow_disposed != 0u ? AL_MAILBOX_OK : AL_MAILBOX_DISPOSED;
  }
  if (!al_runtime_binding_valid(runtime)) {
    return AL_MAILBOX_INVALID_MODULE;
  }
  if (runtime->busy != 0u || runtime->outstanding_scratch_leases != 0u) {
    return AL_MAILBOX_BUSY;
  }
  return AL_MAILBOX_OK;
}

typedef struct al_external_argument {
  const void *pointer;
  uint64_t byte_count;
  uint32_t is_output;
} al_external_argument;

static uint32_t
al_runtime_external_arguments_valid(const al_mailbox_runtime *runtime,
                                    const al_external_argument *arguments,
                                    uint32_t argument_count) {
  uint32_t index;
  if (runtime == NULL || arguments == NULL ||
      !al_checked_span(runtime, runtime->storage_reserved_bytes)) {
    return 0u;
  }
  for (index = 0u; index < argument_count; ++index) {
    const al_external_argument *argument = &arguments[index];
    uint32_t prior;
    if (argument->byte_count == 0u) {
      continue;
    }
    if (argument->pointer == NULL ||
        !al_checked_span(argument->pointer, argument->byte_count) ||
        al_span_overlaps(argument->pointer, argument->byte_count, runtime,
                         runtime->storage_reserved_bytes) ||
        (argument->is_output != 0u &&
         al_storage_overlaps_module(argument->pointer, argument->byte_count,
                                    runtime->module))) {
      return 0u;
    }
    for (prior = 0u; prior < index; ++prior) {
      if (arguments[prior].byte_count != 0u &&
          al_span_overlaps(argument->pointer, argument->byte_count,
                           arguments[prior].pointer,
                           arguments[prior].byte_count)) {
        return 0u;
      }
    }
  }
  return 1u;
}

static void al_call_info_reset(al_mailbox_call_info *call_info) {
  if (call_info != NULL) {
    call_info->control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
    call_info->struct_size = (uint32_t)sizeof(*call_info);
    call_info->handler_status = AL_RUNTIME_STATUS_INVALID_REQUEST;
    call_info->error_metadata_id = -1;
    call_info->error_argument0 = 0;
    call_info->error_argument1 = 0;
    call_info->steps_consumed = 0u;
    call_info->reserved = 0u;
  }
}

static uint32_t al_call_info_valid(const al_mailbox_call_info *call_info) {
  return call_info == NULL || al_pointer_aligned(call_info, 8u);
}

static al_mailbox_result
al_effective_limits(const al_mailbox_runtime *runtime,
                    const al_mailbox_call_limits *limits, uint32_t *bytes,
                    uint32_t *nodes) {
  if (bytes == NULL || nodes == NULL) {
    return AL_MAILBOX_INVALID_ARGUMENT;
  }
  if (limits == NULL) {
    *bytes = runtime->config.retained_byte_capacity;
    *nodes = runtime->config.retained_node_capacity;
    return AL_MAILBOX_OK;
  }
  if (!al_pointer_aligned(limits, 4u) ||
      limits->control_abi_version != AL_MAILBOX_CONTROL_ABI_VERSION ||
      limits->struct_size != sizeof(al_mailbox_call_limits) ||
      limits->retained_byte_limit > runtime->config.retained_byte_capacity ||
      limits->retained_node_limit > runtime->config.retained_node_capacity) {
    return AL_MAILBOX_INVALID_ARGUMENT;
  }
  *bytes = limits->retained_byte_limit;
  *nodes = limits->retained_node_limit;
  return AL_MAILBOX_OK;
}

static al_mailbox_result al_mailbox_at(al_mailbox_runtime *runtime,
                                       uint32_t mailbox_id,
                                       al_mailbox_slot **out_slot) {
  if (runtime == NULL || out_slot == NULL) {
    return AL_MAILBOX_INVALID_ARGUMENT;
  }
  if (mailbox_id >= runtime->config.mailbox_capacity) {
    return AL_MAILBOX_INVALID_MAILBOX;
  }
  *out_slot = &runtime->mailboxes[mailbox_id];
  return AL_MAILBOX_OK;
}

static void al_clear_bank(al_mailbox_runtime *runtime, al_mailbox_bank *bank,
                          uint32_t poison) {
  if (bank == NULL) {
    return;
  }
  bank->owner.used = 0u;
  bank->owner.node_count = 0u;
  bank->owner.byte_capacity = runtime->config.retained_byte_capacity;
  bank->owner.node_capacity = runtime->config.retained_node_capacity;
  bank->owner.flags = 0u;
  bank->owner.reserved = 0u;
  bank->root_count = 0u;
  bank->reserved = 0u;
  memset(bank->roots, 0, sizeof(bank->roots));
  memset(bank->root_type_ids, 0, sizeof(bank->root_type_ids));
  if (poison != 0u) {
    memset(bank->owner.data, AL_MAILBOX_POISON_BYTE,
           runtime->config.retained_byte_capacity);
    memset(bank->owner.nodes, AL_MAILBOX_POISON_BYTE,
           (size_t)runtime->config.retained_node_capacity * sizeof(al_node));
  }
}

static void al_reset_scratch(al_mailbox_runtime *runtime) {
  runtime->scratch.used = 0u;
  runtime->scratch.node_count = 0u;
  runtime->scratch.byte_capacity = runtime->config.scratch_byte_capacity;
  runtime->scratch.node_capacity = runtime->config.scratch_node_capacity;
  runtime->scratch.flags = 0u;
  runtime->scratch.reserved = 0u;
  memset(runtime->scratch_data, AL_MAILBOX_POISON_BYTE,
         runtime->config.scratch_byte_capacity);
  memset(runtime->scratch_nodes, AL_MAILBOX_POISON_BYTE,
         (size_t)runtime->config.scratch_node_capacity * sizeof(al_node));
  memset(runtime->workspace, AL_MAILBOX_POISON_BYTE,
         (size_t)runtime->workspace_capacity * sizeof(int64_t));
  memset(runtime->invocation_values, AL_MAILBOX_POISON_BYTE,
         sizeof(runtime->invocation_values));
  memset(runtime->invocation_type_ids, AL_MAILBOX_POISON_BYTE,
         sizeof(runtime->invocation_type_ids));
}

static void al_reset_context(al_mailbox_runtime *runtime) {
  memset(&runtime->context, 0, sizeof(runtime->context));
}

static void al_saturating_increment(uint64_t *value) {
  if (value != NULL && *value != UINT64_MAX) {
    ++(*value);
  }
}

static al_mailbox_result
al_validate_output_roots(const al_mailbox_runtime *runtime,
                         const al_mailbox_bank *bank,
                         const al_module_entry *entry) {
  uint32_t root_index;
  if (bank->owner.generation == 0u ||
      bank->owner.used > bank->owner.byte_capacity ||
      bank->owner.node_count > bank->owner.node_capacity ||
      bank->owner.flags != 0u || bank->owner.reserved != 0u) {
    return AL_MAILBOX_INVALID_REFERENCE;
  }
  for (root_index = 0u; root_index < entry->output_count; ++root_index) {
    uint32_t type_id = entry->output_type_ids[root_index];
    const al_type_desc *type = &runtime->program->types[type_id];
    uint64_t bits = (uint64_t)bank->roots[root_index];
    if (type->kind == AL_RUNTIME_TYPE_RECORD) {
      uint32_t generation = (uint32_t)(bits >> 32u);
      uint32_t node_index = (uint32_t)bits;
      if (generation != bank->owner.generation || node_index == 0u ||
          node_index > bank->owner.node_count ||
          bank->owner.nodes[node_index - 1u].type_id != type_id) {
        return AL_MAILBOX_INVALID_REFERENCE;
      }
    } else if (type->kind == AL_RUNTIME_TYPE_BOOL && bits > 1u) {
      return AL_MAILBOX_INVALID_REFERENCE;
    } else if (type->kind == AL_RUNTIME_TYPE_UNIT && bits != 0u) {
      return AL_MAILBOX_INVALID_REFERENCE;
    }
  }
  return AL_MAILBOX_OK;
}

static al_mailbox_result al_map_handler_status(int32_t status) {
  switch (status) {
  case AL_RUNTIME_STATUS_SUCCESS:
    return AL_MAILBOX_OK;
  case AL_RUNTIME_SCRATCH_CAPACITY:
    return AL_MAILBOX_SCRATCH_CAPACITY;
  case AL_RUNTIME_RETAINED_CAPACITY:
    return AL_MAILBOX_RETAINED_CAPACITY;
  case 5:
    return AL_MAILBOX_INVALID_REFERENCE;
  default:
    return AL_MAILBOX_HANDLER_FAILURE;
  }
}

static al_mailbox_result
al_execute_entry(al_mailbox_runtime *runtime, al_mailbox_slot *slot,
                 al_mailbox_bank *input_bank, al_mailbox_bank *output_bank,
                 const al_module_entry *entry, const int64_t *input_values,
                 const al_mailbox_call_limits *limits,
                 al_mailbox_call_info *call_info) {
  al_mailbox_result result;
  uint32_t byte_limit;
  uint32_t node_limit;
  int32_t handler_status = AL_RUNTIME_STATUS_INVALID_REQUEST;
  uint32_t input_index;
  uint32_t scratch_generation;
  uint32_t staging_generation;

  result = al_effective_limits(runtime, limits, &byte_limit, &node_limit);
  if (result != AL_MAILBOX_OK) {
    return result;
  }
  if (entry == NULL || entry->input_count > AL_MAILBOX_MAX_INPUTS ||
      entry->output_count > AL_MAILBOX_MAX_ROOTS || input_values == NULL ||
      output_bank == NULL || slot == NULL || runtime->busy != 0u ||
      runtime->outstanding_scratch_leases != 0u) {
    return AL_MAILBOX_INVALID_ARGUMENT;
  }

  if (runtime->next_generation == 0u ||
      runtime->next_generation > (uint64_t)UINT32_MAX - 1u) {
    return AL_MAILBOX_GENERATION_EXHAUSTED;
  }
  scratch_generation = (uint32_t)runtime->next_generation;
  staging_generation = scratch_generation + 1u;
  runtime->next_generation += 2u;

  al_clear_bank(runtime, output_bank, 1u);
  output_bank->owner.generation = staging_generation;
  output_bank->owner.byte_capacity = byte_limit;
  output_bank->owner.node_capacity = node_limit;
  runtime->scratch.generation = scratch_generation;
  al_reset_scratch(runtime);
  for (input_index = 0u; input_index < entry->input_count; ++input_index) {
    runtime->invocation_values[input_index] = input_values[input_index];
    runtime->invocation_type_ids[input_index] =
        entry->input_type_ids[input_index];
  }

  memset(&runtime->context, 0, sizeof(runtime->context));
  runtime->context.abi_version = AL_RUNTIME_ABI_VERSION;
  runtime->context.error_metadata_id = -1;
  runtime->context.scratch = &runtime->scratch;
  runtime->context.retained = &output_bank->owner;
  runtime->context.workspace = runtime->workspace;
  runtime->context.workspace_capacity = entry->workspace_capacity;
  runtime->context.input_owner = input_bank == NULL ? NULL : &input_bank->owner;
  runtime->context.input_roots = runtime->invocation_values;
  runtime->context.input_root_type_ids = runtime->invocation_type_ids;
  runtime->context.input_root_count = entry->input_count;

  runtime->busy = 1u;
  runtime->outstanding_scratch_leases = 1u;
  al_saturating_increment(&runtime->scratch_lease_acquisitions);
  al_saturating_increment(&runtime->handler_invocations);
  entry->execute(&runtime->context, output_bank->roots, entry->output_count,
                 &handler_status);
  if (runtime->scratch.used > runtime->scratch_high_water_bytes) {
    runtime->scratch_high_water_bytes = runtime->scratch.used;
  }
  if (runtime->scratch.node_count > runtime->scratch_high_water_nodes) {
    runtime->scratch_high_water_nodes = runtime->scratch.node_count;
  }
  if (call_info != NULL) {
    call_info->handler_status = handler_status;
    call_info->error_metadata_id = runtime->context.error_metadata_id;
    call_info->error_argument0 = runtime->context.error_argument0;
    call_info->error_argument1 = runtime->context.error_argument1;
    call_info->steps_consumed = runtime->context.steps_consumed;
  }

  result = al_map_handler_status(handler_status);
  if (result == AL_MAILBOX_OK) {
    result = al_validate_output_roots(runtime, output_bank, entry);
  }
  if (result == AL_MAILBOX_OK) {
    output_bank->root_count = entry->output_count;
    for (input_index = 0u; input_index < entry->output_count; ++input_index) {
      output_bank->root_type_ids[input_index] =
          entry->output_type_ids[input_index];
    }
    output_bank->owner.byte_capacity = runtime->config.retained_byte_capacity;
    output_bank->owner.node_capacity = runtime->config.retained_node_capacity;
  } else {
    al_saturating_increment(&runtime->handler_failures);
    al_clear_bank(runtime, output_bank, 1u);
  }

  al_reset_scratch(runtime);
  al_reset_context(runtime);
  runtime->outstanding_scratch_leases = 0u;
  al_saturating_increment(&runtime->scratch_lease_returns);
  runtime->busy = 0u;
  (void)slot;
  return result;
}

static uint32_t al_token_equal(const al_mailbox_token *left,
                               const al_mailbox_token *right) {
  return left->opaque[0] == right->opaque[0] &&
         left->opaque[1] == right->opaque[1] &&
         left->opaque[2] == right->opaque[2];
}

static void al_make_token(al_mailbox_token *token, uint64_t runtime_id,
                          uint32_t mailbox_id, uint64_t sequence) {
  token->opaque[0] = runtime_id;
  token->opaque[1] = sequence;
  token->opaque[2] = (uint64_t)mailbox_id + 1u;
}

static al_mailbox_result al_validate_token(al_mailbox_runtime *runtime,
                                           uint32_t mailbox_id,
                                           const al_mailbox_slot *slot,
                                           const al_mailbox_token *token) {
  if (token->opaque[0] != runtime->runtime_instance_id) {
    return AL_MAILBOX_CROSS_RUNTIME_TOKEN;
  }
  if (token->opaque[2] != (uint64_t)mailbox_id + 1u) {
    return AL_MAILBOX_WRONG_OWNER_TOKEN;
  }
  if (slot->last_completed_valid != 0u &&
      al_token_equal(token, &slot->last_completed_token)) {
    return AL_MAILBOX_DUPLICATE_TOKEN;
  }
  if (slot->pending != 0u && al_token_equal(token, &slot->pending_token)) {
    return AL_MAILBOX_OK;
  }
  return AL_MAILBOX_STALE_TOKEN;
}

static al_mailbox_result al_runtime_init_impl(const al_module_desc *module,
                                              const al_mailbox_config *config,
                                              void *storage,
                                              uint64_t storage_bytes,
                                              al_mailbox_runtime **out_runtime,
                                              uint32_t owner_thread_id) {
  al_mailbox_storage_requirements requirements;
  al_mailbox_layout layout;
  al_mailbox_runtime *runtime;
  uint8_t *base;
  uint64_t instance_id;
  size_t i;
  al_mailbox_result result;

  if (out_runtime == NULL || !al_pointer_aligned(out_runtime, 8u) ||
      !al_checked_span(out_runtime, sizeof(*out_runtime))) {
    return AL_MAILBOX_INVALID_ARGUMENT;
  }
  if (owner_thread_id == 0u) {
    return AL_MAILBOX_INVALID_ARGUMENT;
  }
  if (config == NULL || !al_checked_span(config, sizeof(*config)) ||
      al_span_overlaps(out_runtime, sizeof(*out_runtime), config,
                       sizeof(*config))) {
    return AL_MAILBOX_INVALID_ARGUMENT;
  }
  result = al_compute_layout(module, config, &layout);
  if (result != AL_MAILBOX_OK) {
    return result;
  }
  requirements.control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  requirements.struct_size = (uint32_t)sizeof(requirements);
  requirements.storage_alignment = AL_MAILBOX_ALIGNMENT;
  requirements.workspace_capacity = layout.workspace_capacity;
  requirements.storage_bytes = (uint64_t)layout.total_bytes;
  requirements.retained_reserved_bytes = layout.retained_reserved_bytes;
  requirements.scratch_reserved_bytes = layout.scratch_reserved_bytes;
  requirements.workspace_reserved_bytes = layout.workspace_reserved_bytes;
  requirements.controller_reserved_bytes = layout.controller_reserved_bytes;

  if (al_span_overlaps(out_runtime, sizeof(*out_runtime), storage,
                       requirements.storage_bytes) ||
      al_span_overlaps(out_runtime, sizeof(*out_runtime), module,
                       sizeof(*module)) ||
      al_storage_overlaps_module(out_runtime, sizeof(*out_runtime), module)) {
    return AL_MAILBOX_INVALID_ARGUMENT;
  }
  *out_runtime = NULL;
  if (storage == NULL || !al_pointer_aligned(storage, AL_MAILBOX_ALIGNMENT) ||
      storage_bytes < requirements.storage_bytes ||
      !al_checked_span(storage, storage_bytes) ||
      !al_checked_span(config, sizeof(*config)) ||
      al_span_overlaps(storage, requirements.storage_bytes, config,
                       sizeof(*config)) ||
      al_span_overlaps(storage, requirements.storage_bytes, out_runtime,
                       sizeof(*out_runtime)) ||
      al_storage_overlaps_module(storage, requirements.storage_bytes, module)) {
    return AL_MAILBOX_INVALID_STORAGE;
  }
  if (al_mailbox_platform_next_instance_id(&instance_id) == 0) {
    return AL_MAILBOX_INSTANCE_EXHAUSTED;
  }

  base = (uint8_t *)storage;
  memset(base, 0, layout.total_bytes);
  runtime = (al_mailbox_runtime *)(void *)base;
  runtime->magic = AL_MAILBOX_RUNTIME_MAGIC;
  runtime->module = module;
  runtime->program = module->program;
  runtime->config = *config;
  runtime->owner_thread_id = owner_thread_id;
  runtime->runtime_instance_id = instance_id;
  runtime->next_token_sequence = 1u;
  runtime->next_generation = 1u;
  runtime->workspace_capacity = layout.workspace_capacity;
  runtime->storage_reserved_bytes = requirements.storage_bytes;
  runtime->retained_reserved_bytes = requirements.retained_reserved_bytes;
  runtime->scratch_reserved_bytes = requirements.scratch_reserved_bytes;
  runtime->workspace_reserved_bytes = requirements.workspace_reserved_bytes;
  runtime->mailboxes = (al_mailbox_slot *)(void *)(base + layout.slots_offset);
  runtime->scratch_data = base + layout.scratch_data_offset;
  runtime->scratch_nodes =
      (al_node *)(void *)(base + layout.scratch_nodes_offset);
  runtime->workspace = (int64_t *)(void *)(base + layout.workspace_offset);
  runtime->state_type_id =
      module->entries[config->init_entry_id].output_type_ids[0];
  runtime->continuation_type_id =
      module->entries[config->begin_entry_id].output_type_ids[1];
  runtime->int_type_id =
      module->entries[config->init_entry_id].input_type_ids[0];
  runtime->entries[0] = &module->entries[config->init_entry_id];
  runtime->entries[1] = &module->entries[config->begin_entry_id];
  runtime->entries[2] = &module->entries[config->resume_entry_id];
  memcpy(runtime->fingerprint, module->fingerprint,
         AL_MODULE_FINGERPRINT_BYTES);
  runtime->scratch.data = runtime->scratch_data;
  runtime->scratch.byte_capacity = config->scratch_byte_capacity;
  runtime->scratch.nodes = runtime->scratch_nodes;
  runtime->scratch.node_capacity = config->scratch_node_capacity;
  runtime->scratch.generation = 0u;
  runtime->scratch.flags = 0u;
  runtime->scratch.reserved = 0u;

  for (i = 0u; i < config->mailbox_capacity; ++i) {
    al_mailbox_slot *slot = &runtime->mailboxes[i];
    size_t mailbox_base = layout.bank_backing_offset + i * layout.bank_stride;
    uint32_t bank_index;
    for (bank_index = 0u; bank_index < 2u; ++bank_index) {
      al_mailbox_bank *bank = &slot->banks[bank_index];
      bank->owner.data =
          base + mailbox_base + layout.bank_data_offset[bank_index];
      bank->owner.byte_capacity = config->retained_byte_capacity;
      bank->owner.nodes =
          (al_node *)(void *)(base + mailbox_base +
                              layout.bank_nodes_offset[bank_index]);
      bank->owner.node_capacity = config->retained_node_capacity;
      bank->owner.generation = 0u;
      bank->owner.flags = 0u;
      bank->owner.reserved = 0u;
      al_clear_bank(runtime, bank, 1u);
    }
  }
  al_reset_scratch(runtime);
  *out_runtime = runtime;
  return AL_MAILBOX_OK;
}

al_mailbox_result al_mailbox_get_storage_requirements(
    const al_module_desc *module, const al_mailbox_config *config,
    al_mailbox_storage_requirements *requirements) {
  al_mailbox_layout layout;
  al_mailbox_result result;
  if (requirements == NULL || !al_pointer_aligned(requirements, 8u)) {
    return AL_MAILBOX_INVALID_ARGUMENT;
  }
  result = al_compute_layout(module, config, &layout);
  if (result != AL_MAILBOX_OK) {
    return result;
  }
  if (al_span_overlaps(requirements, sizeof(*requirements), config,
                       sizeof(*config)) ||
      al_span_overlaps(requirements, sizeof(*requirements), module,
                       sizeof(*module)) ||
      al_storage_overlaps_module(requirements, sizeof(*requirements), module)) {
    return AL_MAILBOX_INVALID_ARGUMENT;
  }
  requirements->control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  requirements->struct_size = (uint32_t)sizeof(*requirements);
  requirements->storage_alignment = AL_MAILBOX_ALIGNMENT;
  requirements->workspace_capacity = layout.workspace_capacity;
  requirements->storage_bytes = (uint64_t)layout.total_bytes;
  requirements->retained_reserved_bytes = layout.retained_reserved_bytes;
  requirements->scratch_reserved_bytes = layout.scratch_reserved_bytes;
  requirements->workspace_reserved_bytes = layout.workspace_reserved_bytes;
  requirements->controller_reserved_bytes = layout.controller_reserved_bytes;
  return AL_MAILBOX_OK;
}

al_mailbox_result al_mailbox_runtime_init(const al_module_desc *module,
                                          const al_mailbox_config *config,
                                          void *storage, uint64_t storage_bytes,
                                          al_mailbox_runtime **out_runtime) {
  uint32_t current_thread_id = al_mailbox_platform_current_thread_id();
  return al_runtime_init_impl(module, config, storage, storage_bytes,
                              out_runtime, current_thread_id);
}

al_mailbox_result al_mailbox_init_mailbox(al_mailbox_runtime *runtime,
                                          uint32_t mailbox_id, int64_t seed,
                                          const al_mailbox_call_limits *limits,
                                          al_mailbox_call_info *call_info) {
  uint32_t current_thread_id = al_mailbox_platform_current_thread_id();
  al_mailbox_slot *slot;
  int64_t inputs[1];
  al_mailbox_result result;
  al_mailbox_bank *output_bank;
  al_external_argument arguments[2] = {
      {limits, limits == NULL ? 0u : sizeof(*limits), 0u},
      {call_info, call_info == NULL ? 0u : sizeof(*call_info), 1u}};

  if (!al_call_info_valid(call_info)) {
    return AL_MAILBOX_INVALID_ARGUMENT;
  }
  result = al_runtime_access(runtime, current_thread_id, 0u);
  if (result != AL_MAILBOX_OK) {
    return result;
  }
  if (!al_runtime_external_arguments_valid(
          runtime, arguments,
          (uint32_t)(sizeof(arguments) / sizeof(arguments[0])))) {
    return AL_MAILBOX_INVALID_ARGUMENT;
  }
  result = al_mailbox_at(runtime, mailbox_id, &slot);
  if (result != AL_MAILBOX_OK) {
    return result;
  }
  al_call_info_reset(call_info);
  if (slot->initialized != 0u) {
    return AL_MAILBOX_ALREADY_INITIALIZED;
  }
  result =
      al_effective_limits(runtime, limits, &(uint32_t){0u}, &(uint32_t){0u});
  if (result != AL_MAILBOX_OK) {
    return result;
  }
  inputs[0] = seed;
  output_bank = &slot->banks[0];
  result = al_execute_entry(runtime, slot, NULL, output_bank,
                            runtime->entries[0], inputs, limits, call_info);
  if (result != AL_MAILBOX_OK) {
    return result;
  }
  slot->active_bank = 0u;
  slot->pending = 0u;
  slot->initialized = 1u;
  memset(&slot->pending_token, 0, sizeof(slot->pending_token));
  memset(&slot->last_completed_token, 0, sizeof(slot->last_completed_token));
  slot->last_completed_valid = 0u;
  return AL_MAILBOX_OK;
}

al_mailbox_result al_mailbox_begin(al_mailbox_runtime *runtime,
                                   uint32_t mailbox_id, int64_t message,
                                   const al_mailbox_call_limits *limits,
                                   al_mailbox_token *out_token,
                                   al_mailbox_call_info *call_info) {
  uint32_t current_thread_id = al_mailbox_platform_current_thread_id();
  al_mailbox_slot *slot;
  al_mailbox_bank *active;
  al_mailbox_bank *output_bank;
  int64_t inputs[2];
  al_mailbox_token token;
  al_mailbox_result result;
  al_external_argument arguments[3] = {
      {limits, limits == NULL ? 0u : sizeof(*limits), 0u},
      {out_token, sizeof(*out_token), 1u},
      {call_info, call_info == NULL ? 0u : sizeof(*call_info), 1u}};

  if (out_token == NULL || !al_pointer_aligned(out_token, 8u) ||
      !al_call_info_valid(call_info)) {
    return AL_MAILBOX_INVALID_ARGUMENT;
  }
  result = al_runtime_access(runtime, current_thread_id, 0u);
  if (result != AL_MAILBOX_OK) {
    return result;
  }
  if (!al_runtime_external_arguments_valid(
          runtime, arguments,
          (uint32_t)(sizeof(arguments) / sizeof(arguments[0])))) {
    return AL_MAILBOX_INVALID_ARGUMENT;
  }
  result = al_mailbox_at(runtime, mailbox_id, &slot);
  if (result != AL_MAILBOX_OK) {
    return result;
  }
  al_call_info_reset(call_info);
  if (slot->initialized == 0u) {
    return AL_MAILBOX_NOT_INITIALIZED;
  }
  if (slot->pending != 0u) {
    return AL_MAILBOX_BUSY;
  }
  if (runtime->sequence_exhausted != 0u || runtime->next_token_sequence == 0u) {
    return AL_MAILBOX_TOKEN_EXHAUSTED;
  }
  result =
      al_effective_limits(runtime, limits, &(uint32_t){0u}, &(uint32_t){0u});
  if (result != AL_MAILBOX_OK) {
    return result;
  }
  active = &slot->banks[slot->active_bank];
  if (active->root_count != 1u ||
      active->root_type_ids[0] != runtime->state_type_id) {
    return AL_MAILBOX_INVALID_REFERENCE;
  }
  inputs[0] = active->roots[0];
  inputs[1] = message;
  output_bank = &slot->banks[1u - slot->active_bank];
  result = al_execute_entry(runtime, slot, active, output_bank,
                            runtime->entries[1], inputs, limits, call_info);
  if (result != AL_MAILBOX_OK) {
    return result;
  }

  al_make_token(&token, runtime->runtime_instance_id, mailbox_id,
                runtime->next_token_sequence);
  slot->active_bank = 1u - slot->active_bank;
  al_clear_bank(runtime, active, 1u);
  slot->pending = 1u;
  slot->pending_token = token;
  *out_token = token;
  if (runtime->next_token_sequence == UINT64_MAX) {
    runtime->next_token_sequence = 0u;
    runtime->sequence_exhausted = 1u;
  } else {
    ++runtime->next_token_sequence;
  }
  return AL_MAILBOX_OK;
}

al_mailbox_result al_mailbox_resume(al_mailbox_runtime *runtime,
                                    uint32_t mailbox_id,
                                    const al_mailbox_token *token,
                                    int64_t message,
                                    const al_mailbox_call_limits *limits,
                                    al_mailbox_call_info *call_info) {
  uint32_t current_thread_id = al_mailbox_platform_current_thread_id();
  al_mailbox_slot *slot;
  al_mailbox_bank *active;
  al_mailbox_bank *output_bank;
  int64_t inputs[3];
  al_mailbox_result result;
  al_mailbox_token completed_token;
  al_external_argument arguments[3] = {
      {token, token == NULL ? 0u : sizeof(*token), 0u},
      {limits, limits == NULL ? 0u : sizeof(*limits), 0u},
      {call_info, call_info == NULL ? 0u : sizeof(*call_info), 1u}};

  if (token == NULL || !al_pointer_aligned(token, 8u) ||
      !al_call_info_valid(call_info)) {
    return AL_MAILBOX_INVALID_ARGUMENT;
  }
  result = al_runtime_access(runtime, current_thread_id, 0u);
  if (result != AL_MAILBOX_OK) {
    return result;
  }
  if (!al_runtime_external_arguments_valid(
          runtime, arguments,
          (uint32_t)(sizeof(arguments) / sizeof(arguments[0])))) {
    return AL_MAILBOX_INVALID_ARGUMENT;
  }
  result = al_mailbox_at(runtime, mailbox_id, &slot);
  if (result != AL_MAILBOX_OK) {
    return result;
  }
  al_call_info_reset(call_info);
  if (slot->initialized == 0u) {
    return AL_MAILBOX_NOT_INITIALIZED;
  }
  result = al_validate_token(runtime, mailbox_id, slot, token);
  if (result != AL_MAILBOX_OK) {
    return result;
  }
  result =
      al_effective_limits(runtime, limits, &(uint32_t){0u}, &(uint32_t){0u});
  if (result != AL_MAILBOX_OK) {
    return result;
  }
  active = &slot->banks[slot->active_bank];
  if (slot->pending == 0u || active->root_count != 2u ||
      active->root_type_ids[0] != runtime->state_type_id ||
      active->root_type_ids[1] != runtime->continuation_type_id) {
    return AL_MAILBOX_STALE_TOKEN;
  }
  inputs[0] = active->roots[0];
  inputs[1] = active->roots[1];
  inputs[2] = message;
  output_bank = &slot->banks[1u - slot->active_bank];
  completed_token = slot->pending_token;
  result = al_execute_entry(runtime, slot, active, output_bank,
                            runtime->entries[2], inputs, limits, call_info);
  if (result != AL_MAILBOX_OK) {
    return result;
  }
  slot->active_bank = 1u - slot->active_bank;
  al_clear_bank(runtime, active, 1u);
  slot->pending = 0u;
  slot->last_completed_token = completed_token;
  slot->last_completed_valid = 1u;
  memset(&slot->pending_token, 0, sizeof(slot->pending_token));
  return AL_MAILBOX_OK;
}

al_mailbox_result al_mailbox_dispose(al_mailbox_runtime *runtime) {
  uint32_t current_thread_id = al_mailbox_platform_current_thread_id();
  uint32_t mailbox_id;
  if (runtime == NULL || runtime->magic != AL_MAILBOX_RUNTIME_MAGIC) {
    return AL_MAILBOX_INVALID_ARGUMENT;
  }
  if (current_thread_id != runtime->owner_thread_id) {
    return AL_MAILBOX_WRONG_THREAD;
  }
  if (runtime->busy != 0u || runtime->outstanding_scratch_leases != 0u) {
    return AL_MAILBOX_BUSY;
  }
  if (runtime->disposed != 0u) {
    return AL_MAILBOX_OK;
  }
  for (mailbox_id = 0u; mailbox_id < runtime->config.mailbox_capacity;
       ++mailbox_id) {
    al_mailbox_slot *slot = &runtime->mailboxes[mailbox_id];
    al_clear_bank(runtime, &slot->banks[0], 1u);
    al_clear_bank(runtime, &slot->banks[1], 1u);
    slot->initialized = 0u;
    slot->pending = 0u;
    slot->active_bank = 0u;
    slot->last_completed_valid = 0u;
    memset(&slot->pending_token, 0, sizeof(slot->pending_token));
    memset(&slot->last_completed_token, 0, sizeof(slot->last_completed_token));
  }
  al_reset_scratch(runtime);
  al_reset_context(runtime);
  runtime->disposed = 1u;
  return AL_MAILBOX_OK;
}

al_mailbox_result al_mailbox_get_stats(al_mailbox_runtime *runtime,
                                       al_mailbox_runtime_stats *stats) {
  uint32_t current_thread_id = al_mailbox_platform_current_thread_id();
  al_mailbox_result result;
  uint32_t mailbox_id;
  uint64_t live_bytes = 0u;
  uint64_t live_nodes = 0u;
  uint32_t initialized = 0u;
  uint32_t pending = 0u;

  if (stats == NULL || !al_pointer_aligned(stats, 8u)) {
    return AL_MAILBOX_INVALID_ARGUMENT;
  }
  result = al_runtime_access(runtime, current_thread_id, 1u);
  if (result != AL_MAILBOX_OK) {
    return result;
  }
  {
    const al_external_argument argument = {stats, sizeof(*stats), 1u};
    if (!al_runtime_external_arguments_valid(runtime, &argument, 1u)) {
      return AL_MAILBOX_INVALID_ARGUMENT;
    }
  }
  for (mailbox_id = 0u; mailbox_id < runtime->config.mailbox_capacity;
       ++mailbox_id) {
    const al_mailbox_slot *slot = &runtime->mailboxes[mailbox_id];
    if (slot->initialized != 0u) {
      const al_arena *owner = &slot->banks[slot->active_bank].owner;
      ++initialized;
      live_bytes += owner->used;
      live_nodes += owner->node_count;
    }
    if (slot->pending != 0u) {
      ++pending;
    }
  }
  stats->control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  stats->struct_size = (uint32_t)sizeof(*stats);
  stats->mailbox_capacity = runtime->config.mailbox_capacity;
  stats->initialized_mailboxes = initialized;
  stats->pending_mailboxes = pending;
  stats->workspace_capacity = runtime->workspace_capacity;
  stats->storage_reserved_bytes = runtime->storage_reserved_bytes;
  stats->retained_reserved_bytes = runtime->retained_reserved_bytes;
  stats->scratch_reserved_bytes = runtime->scratch_reserved_bytes;
  stats->workspace_reserved_bytes = runtime->workspace_reserved_bytes;
  stats->live_retained_bytes = live_bytes;
  stats->live_retained_nodes = live_nodes;
  stats->scratch_high_water_bytes = runtime->scratch_high_water_bytes;
  stats->scratch_high_water_nodes = runtime->scratch_high_water_nodes;
  stats->handler_invocations = runtime->handler_invocations;
  stats->handler_failures = runtime->handler_failures;
  stats->scratch_lease_acquisitions = runtime->scratch_lease_acquisitions;
  stats->scratch_lease_returns = runtime->scratch_lease_returns;
  stats->outstanding_scratch_leases = runtime->outstanding_scratch_leases;
  stats->reserved = 0u;
  return AL_MAILBOX_OK;
}

al_mailbox_result al_mailbox_get_state_view(al_mailbox_runtime *runtime,
                                            uint32_t mailbox_id,
                                            al_mailbox_state_view *view) {
  uint32_t current_thread_id = al_mailbox_platform_current_thread_id();
  al_mailbox_slot *slot;
  al_mailbox_result result;
  al_mailbox_bank *active;

  if (view == NULL || !al_pointer_aligned(view, 8u)) {
    return AL_MAILBOX_INVALID_ARGUMENT;
  }
  result = al_runtime_access(runtime, current_thread_id, 0u);
  if (result != AL_MAILBOX_OK) {
    return result;
  }
  {
    const al_external_argument argument = {view, sizeof(*view), 1u};
    if (!al_runtime_external_arguments_valid(runtime, &argument, 1u)) {
      return AL_MAILBOX_INVALID_ARGUMENT;
    }
  }
  result = al_mailbox_at(runtime, mailbox_id, &slot);
  if (result != AL_MAILBOX_OK) {
    return result;
  }
  if (slot->initialized == 0u) {
    return AL_MAILBOX_NOT_INITIALIZED;
  }
  active = &slot->banks[slot->active_bank];
  view->control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  view->struct_size = (uint32_t)sizeof(*view);
  view->owner = &active->owner;
  view->roots = active->roots;
  view->root_type_ids = active->root_type_ids;
  view->root_count = active->root_count;
  view->state_type_id = runtime->state_type_id;
  view->pending = slot->pending;
  view->reserved = 0u;
  return AL_MAILBOX_OK;
}

#ifdef AL_MAILBOX_RUNTIME_TESTING
al_mailbox_result
al_mailbox_test_set_next_token_sequence(al_mailbox_runtime *runtime,
                                        uint64_t next_sequence) {
  uint32_t current_thread_id = al_mailbox_platform_current_thread_id();
  al_mailbox_result result = al_runtime_access(runtime, current_thread_id, 0u);
  if (result != AL_MAILBOX_OK) {
    return result;
  }
  runtime->next_token_sequence = next_sequence;
  runtime->sequence_exhausted = next_sequence == 0u;
  return AL_MAILBOX_OK;
}

al_mailbox_result
al_mailbox_test_set_next_generation(al_mailbox_runtime *runtime,
                                    uint64_t next_generation) {
  uint32_t current_thread_id = al_mailbox_platform_current_thread_id();
  al_mailbox_result result = al_runtime_access(runtime, current_thread_id, 0u);
  if (result != AL_MAILBOX_OK) {
    return result;
  }
  runtime->next_generation = next_generation;
  return AL_MAILBOX_OK;
}

int32_t al_mailbox_platform_test_exhaust_instance_counter(void);

al_mailbox_result al_mailbox_test_exhaust_instance_counter(void) {
  uint32_t current_thread_id = al_mailbox_platform_current_thread_id();
  if (current_thread_id == 0u ||
      al_mailbox_platform_test_exhaust_instance_counter() == 0) {
    return AL_MAILBOX_INVALID_ARGUMENT;
  }
  return AL_MAILBOX_OK;
}
#endif
