#include "mailbox_runtime.h"

#include <limits.h>
#include <string.h>

#define AL_MAILBOX_RUNTIME_MAGIC UINT64_C(0x414c4d424f583031)
#define AL_MAILBOX_POISON_BYTE 0xA5u
#if defined(AL_OWNING_TRUSTED_GENERATED) && AL_OWNING_TRUSTED_GENERATED
#define AL_MAILBOX_RUNTIME_TRUSTED 1
#define AL_MAILBOX_RESET_PROFILE_VALUE AL_MAILBOX_RESET_PROFILE_TRUSTED
#elif defined(AL_MAILBOX_FAST_RESET) && AL_MAILBOX_FAST_RESET
#define AL_MAILBOX_RUNTIME_TRUSTED 0
#define AL_MAILBOX_RESET_PROFILE_VALUE AL_MAILBOX_RESET_PROFILE_FAST
#else
#define AL_MAILBOX_RUNTIME_TRUSTED 0
#define AL_MAILBOX_RESET_PROFILE_VALUE AL_MAILBOX_RESET_PROFILE_DIAGNOSTIC
#endif
#define AL_MAILBOX_MAX_ROOTS 2u
#define AL_MAILBOX_MAX_INPUTS 3u
#define AL_MAILBOX_ALIGNMENT 8u
#define AL_MAILBOX_PAYLOAD_ABI3 0u
#define AL_MAILBOX_PAYLOAD_OWNING 1u
#define AL_OWNING_SCRATCH_FREE 0u
#define AL_OWNING_SCRATCH_CHECKED_OUT 1u
#define AL_OWNING_SCRATCH_ATTACHED 2u
#define AL_OWNING_NO_SCRATCH_SLOT UINT32_MAX

typedef struct al_mailbox_bank {
  al_arena owner;
  int64_t roots[AL_MAILBOX_MAX_ROOTS];
  uint32_t root_type_ids[AL_MAILBOX_MAX_ROOTS];
  uint32_t root_count;
  uint32_t reserved;
} al_mailbox_bank;

typedef struct al_mailbox_graph_payload {
  uint32_t active_bank;
  al_mailbox_bank banks[2];
} al_mailbox_graph_payload;

typedef struct al_mailbox_slot {
  uint32_t initialized;
  uint32_t pending;
  uint32_t last_completed_valid;
  al_mailbox_token pending_token;
  al_mailbox_token last_completed_token;
  union {
    al_mailbox_graph_payload graph;
    al_owning_byte_store owning;
  } payload;
} al_mailbox_slot;

typedef struct al_owning_scratch_slot {
  al_owning_stack_context context;
  uint8_t *data;
  uint8_t *init_bitmap;
  uint8_t *poison_bitmap;
  uint32_t state;
  uint32_t owner_mailbox_id;
} al_owning_scratch_slot;

typedef struct al_owning_attachment {
  uint32_t scratch_slot_index;
  uint32_t protected_cursor_bytes;
  uint32_t root_count;
  uint32_t reserved;
  al_owning_bank_stack_slice roots[AL_MAILBOX_MAX_ROOTS];
} al_owning_attachment;

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

typedef struct al_owning_mailbox_layout {
  size_t total_bytes;
  size_t slots_offset;
  size_t scratch_slots_offset;
  size_t attachments_offset;
  size_t scratch_backing_offset;
  size_t scratch_slot_stride;
  size_t scratch_data_offset;
  size_t init_bitmap_offset;
  size_t poison_bitmap_offset;
  size_t text_staging_offset;
  size_t bank_backing_offset;
  size_t bank_stride;
  size_t bank_data_offset[2];
  size_t bank_roots_offset[2];
  size_t bitmap_bytes;
  uint64_t retained_reserved_bytes;
  uint64_t scratch_reserved_bytes;
  uint64_t text_staging_reserved_bytes;
  uint64_t controller_reserved_bytes;
} al_owning_mailbox_layout;

struct al_mailbox_runtime {
  uint64_t magic;
  uint32_t payload_kind;
  uint32_t owning_reserved;
  const al_module_desc *module;
  const al_owning_mailbox_module *owning_module;
  const al_program_desc *program;
  const al_module_entry *entries[3];
  const al_owning_mailbox_entry *owning_entries[3];
  al_mailbox_config config;
  al_mailbox_owning_config owning_config;
  uint8_t fingerprint[AL_MODULE_FINGERPRINT_BYTES];
  al_mailbox_slot *mailboxes;
  al_arena scratch;
  uint8_t *scratch_data;
  al_node *scratch_nodes;
  int64_t *workspace;
  int64_t invocation_values[AL_MAILBOX_MAX_INPUTS];
  uint32_t invocation_type_ids[AL_MAILBOX_MAX_INPUTS];
  al_runtime_context context;
  al_owning_scratch_slot *owning_scratch_slots;
  al_owning_attachment *owning_attachments;
  uint8_t *owning_text_staging;
  al_owning_external_slice owning_inputs[AL_MAILBOX_MAX_INPUTS];
  al_owning_bank_stack_slice owning_outputs[AL_MAILBOX_MAX_ROOTS];
  uint32_t owner_thread_id;
  uint32_t state_type_id;
  uint32_t continuation_type_id;
  uint32_t int_type_id;
  uint32_t owning_string_type_index;
  uint32_t owning_state_type_index;
  uint32_t owning_continuation_type_index;
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
  uint64_t owning_utf8_input_bytes;
  uint64_t owning_utf16_staging_bytes;
  uint64_t owning_input_import_bytes;
  uint64_t owning_publication_copy_bytes;
  uint64_t owning_deep_copy_bytes;
  uint64_t owning_move_bytes;
  uint64_t owning_returned_output_descriptors;
  uint64_t owning_turn_reset_bytes;
  uint64_t owning_reset_full_capacity_payload_write_bytes_requested;
  uint64_t owning_reset_live_prefix_payload_write_bytes_requested;
  uint64_t owning_reset_bitmap_store_operations;
  uint64_t owning_begin_publication_copy_bytes;
  uint64_t owning_resume_root_import_bytes;
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

static al_mailbox_result al_validate_owning_module(
    const al_owning_mailbox_module *module, uint32_t *out_string_index,
    uint32_t *out_state_index, uint32_t *out_continuation_index) {
  const al_owning_layout *layout;
  const al_owning_mailbox_entry *init_entry;
  const al_owning_mailbox_entry *begin_entry;
  const al_owning_mailbox_entry *resume_entry;
  uint32_t string_index;
  uint32_t state_index;
  uint32_t continuation_index;
  uint32_t state_type_id;
  uint32_t continuation_type_id;
  uint32_t index;
  size_t type_bytes;
  size_t field_bytes;

  if (module == NULL || !al_pointer_aligned(module, 8u) ||
      module->abi_version != AL_OWNING_MAILBOX_ABI_VERSION ||
      module->struct_size != sizeof(*module) || module->layout == NULL ||
      !al_pointer_aligned(module->layout, 8u))
    return AL_MAILBOX_INVALID_MODULE;
  layout = module->layout;
  if (layout->abi_version != AL_OWNING_LAYOUT_ABI_VERSION ||
      layout->types == NULL || layout->type_count == 0u ||
      layout->type_count > AL_OWNING_LAYOUT_MAX_TYPES ||
      layout->field_count > AL_OWNING_LAYOUT_MAX_FIELDS ||
      (layout->field_count != 0u && layout->fields == NULL) ||
      !al_pointer_aligned(layout->types, 8u) ||
      (layout->field_count != 0u && !al_pointer_aligned(layout->fields, 4u)) ||
      !al_checked_array_bytes(layout->type_count,
                              sizeof(al_owning_type_descriptor), &type_bytes) ||
      !al_checked_array_bytes(layout->field_count,
                              sizeof(al_owning_field_descriptor),
                              &field_bytes) ||
      !al_checked_span(layout->types, type_bytes) ||
      !al_checked_span(layout->fields, field_bytes))
    return AL_MAILBOX_INVALID_MODULE;

  for (index = 0u; index < layout->type_count; ++index) {
    const al_owning_type_descriptor *type = &layout->types[index];
    uint32_t prior;
    if (type->kind < AL_OWNING_TYPE_I64 ||
        type->kind > AL_OWNING_TYPE_RESULT ||
        (type->kind == AL_OWNING_TYPE_ENUM &&
         (type->case_count == 0u || type->field_count != 0u)) ||
        ((type->kind == AL_OWNING_TYPE_OPTION ||
          type->kind == AL_OWNING_TYPE_RESULT) &&
         (type->case_count != 2u || type->field_count != 2u)) ||
        (type->kind != AL_OWNING_TYPE_ENUM &&
         type->kind != AL_OWNING_TYPE_OPTION &&
         type->kind != AL_OWNING_TYPE_RESULT && type->case_count != 0u) ||
        type->first_field > layout->field_count ||
        type->field_count > layout->field_count - type->first_field)
      return AL_MAILBOX_INVALID_MODULE;
    for (prior = 0u; prior < index; ++prior) {
      if (layout->types[prior].type_id == type->type_id)
        return AL_MAILBOX_INVALID_MODULE;
    }
    if (type->kind == AL_OWNING_TYPE_OPTION ||
        type->kind == AL_OWNING_TYPE_RESULT) {
      uint32_t case_index;
      for (case_index = 0u; case_index < 2u; ++case_index) {
        const al_owning_field_descriptor *field =
            &layout->fields[type->first_field + case_index];
        if (field->fixed_offset_bytes != 8u || field->flags != 0u ||
            field->reserved != 0u ||
            (type->kind == AL_OWNING_TYPE_OPTION && case_index == 1u &&
             field->child_type_index != AL_OWNING_LAYOUT_DYNAMIC_U32) ||
            (field->child_type_index == AL_OWNING_LAYOUT_DYNAMIC_U32 &&
             (type->kind != AL_OWNING_TYPE_OPTION || case_index != 1u)) ||
            (field->child_type_index != AL_OWNING_LAYOUT_DYNAMIC_U32 &&
             field->child_type_index >= layout->type_count))
          return AL_MAILBOX_INVALID_MODULE;
      }
    }
  }
  for (index = 0u; index < layout->field_count; ++index) {
    const al_owning_field_descriptor *field = &layout->fields[index];
    uint32_t owner_index;
    int32_t option_none_row = 0;
    int32_t invalid_sentinel_owner = 0;
    if (field->child_type_index == AL_OWNING_LAYOUT_DYNAMIC_U32) {
      for (owner_index = 0u; owner_index < layout->type_count; ++owner_index) {
        const al_owning_type_descriptor *owner = &layout->types[owner_index];
        if (index >= owner->first_field &&
            index - owner->first_field < owner->field_count) {
          uint32_t case_index = index - owner->first_field;
          if (owner->kind == AL_OWNING_TYPE_OPTION && case_index == 1u) {
            option_none_row = 1;
          } else {
            invalid_sentinel_owner = 1;
          }
        }
      }
    }
    if ((field->child_type_index >= layout->type_count &&
         !(field->child_type_index == AL_OWNING_LAYOUT_DYNAMIC_U32 &&
           option_none_row && !invalid_sentinel_owner)) ||
        (field->flags & ~AL_OWNING_FIELD_ZERO_WIDTH) != 0u ||
        field->reserved != 0u)
      return AL_MAILBOX_INVALID_MODULE;
  }

  init_entry = &module->entries[0];
  begin_entry = &module->entries[1];
  resume_entry = &module->entries[2];
  if (init_entry->input_count != 1u || init_entry->output_count != 1u ||
      begin_entry->input_count != 2u || begin_entry->output_count != 2u ||
      resume_entry->input_count != 3u || resume_entry->output_count != 1u ||
      init_entry->execute == NULL || begin_entry->execute == NULL ||
      resume_entry->execute == NULL || module->associated_resume == NULL)
    return AL_MAILBOX_INVALID_MODULE;

  for (index = 0u; index < AL_OWNING_MAILBOX_ENTRY_COUNT; ++index) {
    const al_owning_mailbox_entry *entry = &module->entries[index];
    uint32_t type_index;
    for (type_index = 0u; type_index < entry->input_count; ++type_index) {
      if (entry->input_type_indexes[type_index] >= layout->type_count)
        return AL_MAILBOX_INVALID_MODULE;
    }
    for (type_index = 0u; type_index < entry->output_count; ++type_index) {
      if (entry->output_type_indexes[type_index] >= layout->type_count)
        return AL_MAILBOX_INVALID_MODULE;
    }
  }

  string_index = init_entry->input_type_indexes[0];
  state_index = init_entry->output_type_indexes[0];
  continuation_index = begin_entry->output_type_indexes[1];
  state_type_id = layout->types[state_index].type_id;
  continuation_type_id = layout->types[continuation_index].type_id;
  if (state_type_id == 0u || continuation_type_id == 0u ||
      state_type_id == continuation_type_id ||
      layout->types[string_index].kind != AL_OWNING_TYPE_STRING ||
      layout->types[state_index].kind != AL_OWNING_TYPE_RECORD ||
      layout->types[continuation_index].kind != AL_OWNING_TYPE_RECORD ||
      layout->types[begin_entry->input_type_indexes[0]].type_id !=
          state_type_id ||
      layout->types[begin_entry->input_type_indexes[1]].type_id !=
          layout->types[string_index].type_id ||
      layout->types[begin_entry->output_type_indexes[0]].type_id !=
          state_type_id ||
      layout->types[resume_entry->input_type_indexes[0]].type_id !=
          state_type_id ||
      layout->types[resume_entry->input_type_indexes[1]].type_id !=
          continuation_type_id ||
      layout->types[resume_entry->input_type_indexes[2]].type_id !=
          layout->types[string_index].type_id ||
      layout->types[resume_entry->output_type_indexes[0]].type_id !=
          state_type_id)
    return AL_MAILBOX_INVALID_MODULE;

  if (out_string_index != NULL)
    *out_string_index = string_index;
  if (out_state_index != NULL)
    *out_state_index = state_index;
  if (out_continuation_index != NULL)
    *out_continuation_index = continuation_index;
  return AL_MAILBOX_OK;
}

static al_mailbox_result al_validate_owning_bindings(
    const al_owning_mailbox_module *module,
    const al_mailbox_owning_config *config, uint32_t *out_string_index,
    uint32_t *out_state_index, uint32_t *out_continuation_index) {
  al_mailbox_result result;
  if (config == NULL || !al_pointer_aligned(config, 4u) ||
      config->control_abi_version != AL_MAILBOX_CONTROL_ABI_VERSION ||
      config->struct_size != sizeof(*config) || config->mailbox_capacity == 0u ||
      config->scratch_byte_capacity == 0u ||
      config->scratch_byte_capacity > (uint32_t)INT32_MAX ||
      config->retained_byte_capacity == 0u ||
      config->text_staging_byte_capacity < 8u ||
      config->scratch_slot_capacity == 0u ||
      config->scratch_slot_capacity > config->mailbox_capacity ||
      config->suspension_policy > AL_MAILBOX_OWNING_POLICY_KEEP_ASSOCIATED)
    return AL_MAILBOX_INVALID_ARGUMENT;
  result = al_validate_owning_module(module, out_string_index, out_state_index,
                                     out_continuation_index);
  return result;
}

static al_mailbox_result al_compute_owning_layout(
    const al_owning_mailbox_module *module,
    const al_mailbox_owning_config *config,
    al_owning_mailbox_layout *layout) {
  uint64_t retained_bank_bytes;
  uint64_t retained_total_bytes;
  uint64_t scratch_bytes;
  uint64_t scratch_per_slot_bytes;
  uint64_t text_bytes;
  uint64_t backing_bytes;
  size_t slot_bytes;
  size_t scratch_slot_metadata_bytes;
  size_t attachment_metadata_bytes;
  size_t root_bytes;
  size_t offset = 0u;
  size_t per_mailbox_offset = 0u;
  size_t per_scratch_slot_offset = 0u;
  al_mailbox_result result;

  if (layout == NULL)
    return AL_MAILBOX_INVALID_ARGUMENT;
  memset(layout, 0, sizeof(*layout));
  result = al_validate_owning_bindings(module, config, NULL, NULL, NULL);
  if (result != AL_MAILBOX_OK)
    return result;
  if (!al_checked_array_bytes(config->mailbox_capacity, sizeof(al_mailbox_slot),
                              &slot_bytes) ||
      !al_checked_array_bytes(config->scratch_slot_capacity,
                              sizeof(al_owning_scratch_slot),
                              &scratch_slot_metadata_bytes) ||
      !al_checked_array_bytes(config->mailbox_capacity,
                              sizeof(al_owning_attachment),
                              &attachment_metadata_bytes) ||
      !al_checked_array_bytes(AL_MAILBOX_MAX_ROOTS,
                              sizeof(al_owning_bank_root), &root_bytes))
    return AL_MAILBOX_SIZE_OVERFLOW;
#if AL_MAILBOX_RUNTIME_TRUSTED
  layout->bitmap_bytes = 0u;
#else
  layout->bitmap_bytes =
      config->scratch_byte_capacity / 8u +
      (config->scratch_byte_capacity % 8u != 0u ? 1u : 0u);
#endif
  if (!al_append_region(&offset, sizeof(al_mailbox_runtime),
                        AL_MAILBOX_ALIGNMENT, NULL) ||
      !al_append_region(&offset, slot_bytes, AL_MAILBOX_ALIGNMENT,
                        &layout->slots_offset) ||
      !al_append_region(&offset, scratch_slot_metadata_bytes,
                        AL_MAILBOX_ALIGNMENT, &layout->scratch_slots_offset) ||
      !al_append_region(&offset, attachment_metadata_bytes,
                        AL_MAILBOX_ALIGNMENT, &layout->attachments_offset) ||
      !al_append_region(&offset, config->text_staging_byte_capacity,
                        AL_MAILBOX_ALIGNMENT, &layout->text_staging_offset))
    return AL_MAILBOX_SIZE_OVERFLOW;

  /* The text staging byte count is caller-selected and may not be aligned. */
  if (!al_append_region(&offset, 0u, AL_MAILBOX_ALIGNMENT, NULL))
    return AL_MAILBOX_SIZE_OVERFLOW;

  if (!al_append_region(&per_scratch_slot_offset,
                        config->scratch_byte_capacity, AL_MAILBOX_ALIGNMENT,
                        &layout->scratch_data_offset) ||
      !al_append_region(&per_scratch_slot_offset, layout->bitmap_bytes,
                        AL_MAILBOX_ALIGNMENT, &layout->init_bitmap_offset) ||
      !al_append_region(&per_scratch_slot_offset, layout->bitmap_bytes,
                        AL_MAILBOX_ALIGNMENT,
                        &layout->poison_bitmap_offset))
    return AL_MAILBOX_SIZE_OVERFLOW;
  /* Each repeated slot begins at the storage alignment, even when the final
   * poison bitmap leaves an odd-sized tail (for example, a 72-byte arena). */
  if (!al_append_region(&per_scratch_slot_offset, 0u, AL_MAILBOX_ALIGNMENT,
                        NULL))
    return AL_MAILBOX_SIZE_OVERFLOW;
  layout->scratch_slot_stride = per_scratch_slot_offset;
  layout->scratch_backing_offset = offset;
  if (layout->scratch_slot_stride != 0u &&
      (size_t)config->scratch_slot_capacity >
          (SIZE_MAX - offset) / layout->scratch_slot_stride)
    return AL_MAILBOX_SIZE_OVERFLOW;
  offset += layout->scratch_slot_stride *
            (size_t)config->scratch_slot_capacity;

  layout->bank_backing_offset = offset;
  for (uint32_t bank_index = 0u; bank_index < 2u; ++bank_index) {
    if (!al_append_region(&per_mailbox_offset,
                          config->retained_byte_capacity,
                          AL_MAILBOX_ALIGNMENT,
                          &layout->bank_data_offset[bank_index]) ||
        !al_append_region(&per_mailbox_offset, root_bytes,
                          AL_MAILBOX_ALIGNMENT,
                          &layout->bank_roots_offset[bank_index]))
      return AL_MAILBOX_SIZE_OVERFLOW;
  }
  layout->bank_stride = per_mailbox_offset;
  if (layout->bank_stride != 0u &&
      (size_t)config->mailbox_capacity >
          (SIZE_MAX - offset) / layout->bank_stride)
    return AL_MAILBOX_SIZE_OVERFLOW;
  offset += layout->bank_stride * (size_t)config->mailbox_capacity;
  layout->total_bytes = offset;

  if (!al_add_u64(config->retained_byte_capacity, root_bytes,
                  &retained_bank_bytes) ||
      !al_mul_u64(retained_bank_bytes, 2u, &retained_bank_bytes) ||
      !al_mul_u64(retained_bank_bytes, config->mailbox_capacity,
                  &retained_total_bytes) ||
      !al_add_u64(config->scratch_byte_capacity,
                  (uint64_t)layout->bitmap_bytes * 2u,
                  &scratch_per_slot_bytes) ||
      !al_mul_u64(scratch_per_slot_bytes, config->scratch_slot_capacity,
                  &scratch_bytes))
    return AL_MAILBOX_SIZE_OVERFLOW;
  text_bytes = config->text_staging_byte_capacity;
  if (!al_add_u64(retained_total_bytes, scratch_bytes, &backing_bytes) ||
      !al_add_u64(backing_bytes, text_bytes, &backing_bytes) ||
      backing_bytes > layout->total_bytes)
    return AL_MAILBOX_SIZE_OVERFLOW;
  layout->retained_reserved_bytes = retained_total_bytes;
  layout->scratch_reserved_bytes = scratch_bytes;
  layout->text_staging_reserved_bytes = text_bytes;
  layout->controller_reserved_bytes = layout->total_bytes - backing_bytes;
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

static uint32_t al_storage_overlaps_owning_module(
    const void *storage, uint64_t storage_bytes,
    const al_owning_mailbox_module *module) {
  const al_owning_layout *layout;
  size_t type_bytes;
  size_t field_bytes;
  if (module == NULL || module->layout == NULL)
    return 1u;
  layout = module->layout;
  if (!al_checked_array_bytes(layout->type_count,
                              sizeof(al_owning_type_descriptor), &type_bytes) ||
      !al_checked_array_bytes(layout->field_count,
                              sizeof(al_owning_field_descriptor),
                              &field_bytes) ||
      al_span_overlaps(storage, storage_bytes, module, sizeof(*module)) ||
      al_span_overlaps(storage, storage_bytes, layout, sizeof(*layout)) ||
      al_span_overlaps(storage, storage_bytes, layout->types, type_bytes) ||
      al_span_overlaps(storage, storage_bytes, layout->fields, field_bytes))
    return 1u;
  return 0u;
}

static uint32_t al_runtime_binding_valid(const al_mailbox_runtime *runtime) {
  uint32_t index;
  if (runtime == NULL || runtime->magic != AL_MAILBOX_RUNTIME_MAGIC)
    return 0u;
  if (runtime->payload_kind == AL_MAILBOX_PAYLOAD_OWNING) {
    uint32_t string_index;
    uint32_t state_index;
    uint32_t continuation_index;
    if (runtime->owning_module == NULL || runtime->module != NULL ||
        runtime->program != NULL ||
        al_validate_owning_module(runtime->owning_module, &string_index,
                                  &state_index, &continuation_index) !=
            AL_MAILBOX_OK ||
        string_index != runtime->owning_string_type_index ||
        state_index != runtime->owning_state_type_index ||
        continuation_index != runtime->owning_continuation_type_index)
      return 0u;
    for (index = 0u; index < AL_OWNING_MAILBOX_ENTRY_COUNT; ++index) {
      if (runtime->owning_entries[index] !=
              &runtime->owning_module->entries[index] ||
          runtime->owning_entries[index]->execute == NULL)
        return 0u;
    }
    return 1u;
  }
  if (runtime->payload_kind != AL_MAILBOX_PAYLOAD_ABI3 ||
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
  if (runtime->busy != 0u) {
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
         ((runtime->payload_kind == AL_MAILBOX_PAYLOAD_ABI3 &&
           al_storage_overlaps_module(argument->pointer,
                                      argument->byte_count, runtime->module)) ||
          (runtime->payload_kind == AL_MAILBOX_PAYLOAD_OWNING &&
           al_storage_overlaps_owning_module(
               argument->pointer, argument->byte_count,
               runtime->owning_module))))) {
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

static void al_saturating_add(uint64_t *value, uint64_t amount) {
  if (value != NULL) {
    *value = amount > UINT64_MAX - *value ? UINT64_MAX : *value + amount;
  }
}

static al_mailbox_result al_reserve_generation_pair(
    al_mailbox_runtime *runtime, uint32_t *out_scratch_generation,
    uint32_t *out_staging_generation) {
  if (runtime == NULL || out_scratch_generation == NULL ||
      out_staging_generation == NULL)
    return AL_MAILBOX_INVALID_ARGUMENT;
  if (runtime->next_generation == 0u ||
      runtime->next_generation > (uint64_t)UINT32_MAX - 1u)
    return AL_MAILBOX_GENERATION_EXHAUSTED;
  *out_scratch_generation = (uint32_t)runtime->next_generation;
  *out_staging_generation = *out_scratch_generation + 1u;
  runtime->next_generation += 2u;
  return AL_MAILBOX_OK;
}

static al_mailbox_result al_mailbox_begin_admission(
    const al_mailbox_runtime *runtime, const al_mailbox_slot *slot) {
  if (runtime == NULL || slot == NULL)
    return AL_MAILBOX_INVALID_ARGUMENT;
  if (slot->initialized == 0u)
    return AL_MAILBOX_NOT_INITIALIZED;
  if (slot->pending != 0u)
    return AL_MAILBOX_BUSY;
  if (runtime->sequence_exhausted != 0u ||
      runtime->next_token_sequence == 0u)
    return AL_MAILBOX_TOKEN_EXHAUSTED;
  return AL_MAILBOX_OK;
}

static al_mailbox_result al_scratch_lease_acquire(
    al_mailbox_runtime *runtime) {
  if (runtime == NULL || runtime->busy != 0u ||
      runtime->outstanding_scratch_leases != 0u)
    return AL_MAILBOX_BUSY;
  runtime->busy = 1u;
  runtime->outstanding_scratch_leases = 1u;
  al_saturating_increment(&runtime->scratch_lease_acquisitions);
  al_saturating_increment(&runtime->handler_invocations);
  return AL_MAILBOX_OK;
}

static void al_scratch_lease_release(al_mailbox_runtime *runtime) {
  if (runtime == NULL)
    return;
  runtime->outstanding_scratch_leases = 0u;
  al_saturating_increment(&runtime->scratch_lease_returns);
  runtime->busy = 0u;
}

static void al_mailbox_commit_begin(al_mailbox_runtime *runtime,
                                    al_mailbox_slot *slot,
                                    const al_mailbox_token *token,
                                    al_mailbox_token *out_token) {
  slot->pending = 1u;
  slot->pending_token = *token;
  *out_token = *token;
  if (runtime->next_token_sequence == UINT64_MAX) {
    runtime->next_token_sequence = 0u;
    runtime->sequence_exhausted = 1u;
  } else {
    ++runtime->next_token_sequence;
  }
}

static void al_mailbox_commit_resume(al_mailbox_slot *slot,
                                     const al_mailbox_token *completed_token) {
  slot->pending = 0u;
  slot->last_completed_token = *completed_token;
  slot->last_completed_valid = 1u;
  memset(&slot->pending_token, 0, sizeof(slot->pending_token));
}

static void al_mailbox_mark_initialized(al_mailbox_slot *slot) {
  slot->pending = 0u;
  slot->initialized = 1u;
  memset(&slot->pending_token, 0, sizeof(slot->pending_token));
  memset(&slot->last_completed_token, 0, sizeof(slot->last_completed_token));
  slot->last_completed_valid = 0u;
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

  result = al_reserve_generation_pair(runtime, &scratch_generation,
                                      &staging_generation);
  if (result != AL_MAILBOX_OK)
    return result;

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

  result = al_scratch_lease_acquire(runtime);
  if (result != AL_MAILBOX_OK)
    return result;
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
  al_scratch_lease_release(runtime);
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
  runtime->payload_kind = AL_MAILBOX_PAYLOAD_ABI3;
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
      al_mailbox_bank *bank = &slot->payload.graph.banks[bank_index];
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

static al_mailbox_result al_map_owning_status(uint32_t status) {
  switch (status) {
  case AL_OWNING_STATUS_OK:
    return AL_MAILBOX_OK;
  case AL_OWNING_STATUS_STACK_CAPACITY:
    return AL_MAILBOX_SCRATCH_CAPACITY;
  case AL_OWNING_STATUS_RETAINED_CAPACITY:
    return AL_MAILBOX_RETAINED_CAPACITY;
  case AL_OWNING_STATUS_INVALID_REQUEST:
    return AL_MAILBOX_INVALID_REFERENCE;
  case AL_OWNING_STATUS_DIAGNOSTIC:
  case AL_OWNING_STATUS_INTERNAL:
  default:
    return AL_MAILBOX_HANDLER_FAILURE;
  }
}

static al_mailbox_result al_map_owning_bank_result(
    al_owning_bank_result result) {
  switch (result) {
  case AL_OWNING_BANK_OK:
    return AL_MAILBOX_OK;
  case AL_OWNING_BANK_ROOT_CAPACITY:
  case AL_OWNING_BANK_BYTE_CAPACITY:
    return AL_MAILBOX_RETAINED_CAPACITY;
  case AL_OWNING_BANK_OVERFLOW:
    return AL_MAILBOX_SIZE_OVERFLOW;
  case AL_OWNING_BANK_INVALID_STORAGE:
    return AL_MAILBOX_INVALID_STORAGE;
  case AL_OWNING_BANK_BUSY:
  case AL_OWNING_BANK_NO_TRANSACTION:
    return AL_MAILBOX_BUSY;
  case AL_OWNING_BANK_INVALID_ARGUMENT:
  case AL_OWNING_BANK_INVALID_VALUE:
  case AL_OWNING_BANK_OVERLAP:
  default:
    return AL_MAILBOX_INVALID_REFERENCE;
  }
}

static al_mailbox_result al_owning_text_measure(
    const uint8_t *utf8, uint32_t byte_count, uint32_t staging_capacity,
    uint32_t *out_payload_bytes, uint32_t *out_extent_bytes) {
  uint64_t units = 0u;
  uint32_t index = 0u;
  uint64_t payload;
  uint64_t extent;
  if ((byte_count != 0u && utf8 == NULL) || out_payload_bytes == NULL ||
      out_extent_bytes == NULL ||
      (utf8 != NULL && !al_checked_span(utf8, byte_count)))
    return AL_MAILBOX_INVALID_ARGUMENT;
  while (index < byte_count) {
    uint32_t scalar;
    uint32_t width;
    uint8_t first = utf8[index];
    if (first <= 0x7fu) {
      scalar = first;
      width = 1u;
    } else if (first >= 0xc2u && first <= 0xdfu) {
      if (byte_count - index < 2u || (utf8[index + 1u] & 0xc0u) != 0x80u)
        return AL_MAILBOX_INVALID_TEXT_ENCODING;
      scalar = ((uint32_t)(first & 0x1fu) << 6u) |
               (uint32_t)(utf8[index + 1u] & 0x3fu);
      width = 2u;
    } else if (first >= 0xe0u && first <= 0xefu) {
      uint8_t second;
      if (byte_count - index < 3u)
        return AL_MAILBOX_INVALID_TEXT_ENCODING;
      second = utf8[index + 1u];
      if ((second & 0xc0u) != 0x80u ||
          (utf8[index + 2u] & 0xc0u) != 0x80u ||
          (first == 0xe0u && second < 0xa0u) ||
          (first == 0xedu && second > 0x9fu))
        return AL_MAILBOX_INVALID_TEXT_ENCODING;
      scalar = ((uint32_t)(first & 0x0fu) << 12u) |
               ((uint32_t)(second & 0x3fu) << 6u) |
               (uint32_t)(utf8[index + 2u] & 0x3fu);
      width = 3u;
    } else if (first >= 0xf0u && first <= 0xf4u) {
      uint8_t second;
      if (byte_count - index < 4u)
        return AL_MAILBOX_INVALID_TEXT_ENCODING;
      second = utf8[index + 1u];
      if ((second & 0xc0u) != 0x80u ||
          (utf8[index + 2u] & 0xc0u) != 0x80u ||
          (utf8[index + 3u] & 0xc0u) != 0x80u ||
          (first == 0xf0u && second < 0x90u) ||
          (first == 0xf4u && second > 0x8fu))
        return AL_MAILBOX_INVALID_TEXT_ENCODING;
      scalar = ((uint32_t)(first & 0x07u) << 18u) |
               ((uint32_t)(second & 0x3fu) << 12u) |
               ((uint32_t)(utf8[index + 2u] & 0x3fu) << 6u) |
               (uint32_t)(utf8[index + 3u] & 0x3fu);
      width = 4u;
    } else {
      return AL_MAILBOX_INVALID_TEXT_ENCODING;
    }
    units += scalar > 0xffffu ? 2u : 1u;
    if (units > ((uint64_t)INT32_MAX - 8u) / 2u)
      return AL_MAILBOX_TEXT_CAPACITY;
    index += width;
  }
  payload = 8u + units * 2u;
  extent = (payload + 7u) & ~UINT64_C(7);
  if (extent > staging_capacity || extent > UINT32_MAX)
    return AL_MAILBOX_TEXT_CAPACITY;
  *out_payload_bytes = (uint32_t)payload;
  *out_extent_bytes = (uint32_t)extent;
  return AL_MAILBOX_OK;
}

static void al_owning_write_u16(uint8_t *destination, uint16_t value) {
  destination[0] = (uint8_t)value;
  destination[1] = (uint8_t)(value >> 8u);
}

static void al_owning_write_u32(uint8_t *destination, uint32_t value) {
  destination[0] = (uint8_t)value;
  destination[1] = (uint8_t)(value >> 8u);
  destination[2] = (uint8_t)(value >> 16u);
  destination[3] = (uint8_t)(value >> 24u);
}

static al_mailbox_result al_owning_text_write(
    const uint8_t *utf8, uint32_t byte_count, uint32_t payload_bytes,
    uint32_t extent_bytes, uint8_t *destination) {
  uint32_t source_index = 0u;
  uint32_t output_index = 8u;
  if (destination == NULL || payload_bytes < 8u || extent_bytes < payload_bytes ||
      ((payload_bytes - 8u) & 1u) != 0u)
    return AL_MAILBOX_INVALID_ARGUMENT;
  memset(destination, 0, extent_bytes);
  al_owning_write_u32(destination, (payload_bytes - 8u) / 2u);
  while (source_index < byte_count) {
    uint32_t scalar;
    uint32_t width;
    uint8_t first = utf8[source_index];
    uint32_t output_width;
    if (first <= 0x7fu) {
      scalar = first;
      width = 1u;
    } else if (first >= 0xc2u && first <= 0xdfu) {
      if (byte_count - source_index < 2u ||
          (utf8[source_index + 1u] & 0xc0u) != 0x80u)
        return AL_MAILBOX_INVALID_TEXT_ENCODING;
      scalar = ((uint32_t)(first & 0x1fu) << 6u) |
               (uint32_t)(utf8[source_index + 1u] & 0x3fu);
      width = 2u;
    } else if (first >= 0xe0u && first <= 0xefu) {
      uint8_t second;
      if (byte_count - source_index < 3u)
        return AL_MAILBOX_INVALID_TEXT_ENCODING;
      second = utf8[source_index + 1u];
      if ((second & 0xc0u) != 0x80u ||
          (utf8[source_index + 2u] & 0xc0u) != 0x80u ||
          (first == 0xe0u && second < 0xa0u) ||
          (first == 0xedu && second > 0x9fu))
        return AL_MAILBOX_INVALID_TEXT_ENCODING;
      scalar = ((uint32_t)(first & 0x0fu) << 12u) |
               ((uint32_t)(utf8[source_index + 1u] & 0x3fu) << 6u) |
               (uint32_t)(utf8[source_index + 2u] & 0x3fu);
      width = 3u;
    } else if (first >= 0xf0u && first <= 0xf4u) {
      uint8_t second;
      if (byte_count - source_index < 4u)
        return AL_MAILBOX_INVALID_TEXT_ENCODING;
      second = utf8[source_index + 1u];
      if ((second & 0xc0u) != 0x80u ||
          (utf8[source_index + 2u] & 0xc0u) != 0x80u ||
          (utf8[source_index + 3u] & 0xc0u) != 0x80u ||
          (first == 0xf0u && second < 0x90u) ||
          (first == 0xf4u && second > 0x8fu))
        return AL_MAILBOX_INVALID_TEXT_ENCODING;
      scalar = ((uint32_t)(first & 0x07u) << 18u) |
               ((uint32_t)(utf8[source_index + 1u] & 0x3fu) << 12u) |
               ((uint32_t)(utf8[source_index + 2u] & 0x3fu) << 6u) |
               (uint32_t)(utf8[source_index + 3u] & 0x3fu);
      width = 4u;
    } else {
      return AL_MAILBOX_INVALID_TEXT_ENCODING;
    }
    output_width = scalar > 0xffffu ? 4u : 2u;
    if (output_index > payload_bytes ||
        output_width > payload_bytes - output_index)
      return AL_MAILBOX_INVALID_TEXT_ENCODING;
    if (scalar <= 0xffffu) {
      al_owning_write_u16(destination + output_index, (uint16_t)scalar);
      output_index += 2u;
    } else {
      uint32_t adjusted = scalar - 0x10000u;
      al_owning_write_u16(destination + output_index,
                          (uint16_t)(0xd800u + (adjusted >> 10u)));
      al_owning_write_u16(destination + output_index + 2u,
                          (uint16_t)(0xdc00u + (adjusted & 0x3ffu)));
      output_index += 4u;
    }
    source_index += width;
  }
  return output_index == payload_bytes ? AL_MAILBOX_OK
                                       : AL_MAILBOX_INVALID_TEXT_ENCODING;
}

al_mailbox_result al_mailbox_get_owning_storage_requirements(
    const al_owning_mailbox_module *module,
    const al_mailbox_owning_config *config,
    al_mailbox_owning_storage_requirements *requirements) {
  al_owning_mailbox_layout layout;
  al_mailbox_result result;
  if (requirements == NULL || !al_pointer_aligned(requirements, 8u))
    return AL_MAILBOX_INVALID_ARGUMENT;
  result = al_compute_owning_layout(module, config, &layout);
  if (result != AL_MAILBOX_OK)
    return result;
  if (al_span_overlaps(requirements, sizeof(*requirements), config,
                       sizeof(*config)) ||
      al_storage_overlaps_owning_module(requirements, sizeof(*requirements),
                                        module))
    return AL_MAILBOX_INVALID_ARGUMENT;
  requirements->control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  requirements->struct_size = (uint32_t)sizeof(*requirements);
  requirements->storage_alignment = AL_MAILBOX_ALIGNMENT;
  requirements->mailbox_capacity = config->mailbox_capacity;
  requirements->storage_bytes = (uint64_t)layout.total_bytes;
  requirements->retained_reserved_bytes = layout.retained_reserved_bytes;
  requirements->scratch_reserved_bytes = layout.scratch_reserved_bytes;
  requirements->text_staging_reserved_bytes =
      layout.text_staging_reserved_bytes;
  requirements->controller_reserved_bytes =
      layout.controller_reserved_bytes;
  return AL_MAILBOX_OK;
}

static al_mailbox_result al_runtime_init_owning_impl(
    const al_owning_mailbox_module *module,
    const al_mailbox_owning_config *config, void *storage,
    uint64_t storage_bytes, al_mailbox_runtime **out_runtime,
    uint32_t owner_thread_id) {
  al_owning_mailbox_layout layout;
  al_mailbox_owning_storage_requirements requirements;
  al_mailbox_runtime *runtime;
  uint8_t *base;
  uint64_t instance_id;
  uint32_t string_index;
  uint32_t state_index;
  uint32_t continuation_index;
  uint32_t mailbox_id;
  al_mailbox_result result;
  if (out_runtime == NULL || !al_pointer_aligned(out_runtime, 8u) ||
      !al_checked_span(out_runtime, sizeof(*out_runtime)) ||
      owner_thread_id == 0u || config == NULL ||
      !al_checked_span(config, sizeof(*config)) ||
      al_span_overlaps(out_runtime, sizeof(*out_runtime), config,
                       sizeof(*config)))
    return AL_MAILBOX_INVALID_ARGUMENT;
  result = al_compute_owning_layout(module, config, &layout);
  if (result != AL_MAILBOX_OK)
    return result;
  result = al_validate_owning_module(module, &string_index, &state_index,
                                     &continuation_index);
  if (result != AL_MAILBOX_OK)
    return result;
  requirements.control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  requirements.struct_size = (uint32_t)sizeof(requirements);
  requirements.storage_alignment = AL_MAILBOX_ALIGNMENT;
  requirements.mailbox_capacity = config->mailbox_capacity;
  requirements.storage_bytes = (uint64_t)layout.total_bytes;
  requirements.retained_reserved_bytes = layout.retained_reserved_bytes;
  requirements.scratch_reserved_bytes = layout.scratch_reserved_bytes;
  requirements.text_staging_reserved_bytes =
      layout.text_staging_reserved_bytes;
  requirements.controller_reserved_bytes = layout.controller_reserved_bytes;
  if (al_span_overlaps(out_runtime, sizeof(*out_runtime), storage,
                       requirements.storage_bytes) ||
      al_span_overlaps(out_runtime, sizeof(*out_runtime), module,
                       sizeof(*module)) ||
      al_storage_overlaps_owning_module(out_runtime, sizeof(*out_runtime),
                                        module))
    return AL_MAILBOX_INVALID_ARGUMENT;
  *out_runtime = NULL;
  if (storage == NULL || !al_pointer_aligned(storage, AL_MAILBOX_ALIGNMENT) ||
      storage_bytes < requirements.storage_bytes ||
      !al_checked_span(storage, storage_bytes) ||
      al_span_overlaps(storage, requirements.storage_bytes, config,
                       sizeof(*config)) ||
      al_span_overlaps(storage, requirements.storage_bytes, out_runtime,
                       sizeof(*out_runtime)) ||
      al_storage_overlaps_owning_module(storage, requirements.storage_bytes,
                                        module))
    return AL_MAILBOX_INVALID_STORAGE;
  if (al_mailbox_platform_next_instance_id(&instance_id) == 0)
    return AL_MAILBOX_INSTANCE_EXHAUSTED;

  base = (uint8_t *)storage;
#if AL_MAILBOX_RUNTIME_TRUSTED
  /* Trusted payload regions are write-before-read. Clear only the controller
   * and slot metadata; scratch, banks, and staging do not need bulk clearing. */
  memset(base, 0, layout.slots_offset);
  memset(base + layout.slots_offset, 0,
         layout.attachments_offset - layout.slots_offset);
#else
  memset(base, 0, layout.total_bytes);
#endif
  runtime = (al_mailbox_runtime *)(void *)base;
  runtime->magic = AL_MAILBOX_RUNTIME_MAGIC;
  runtime->payload_kind = AL_MAILBOX_PAYLOAD_OWNING;
  runtime->owning_module = module;
  runtime->owning_config = *config;
  runtime->config.control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  runtime->config.struct_size = (uint32_t)sizeof(runtime->config);
  runtime->config.mailbox_capacity = config->mailbox_capacity;
  runtime->config.scratch_byte_capacity = config->scratch_byte_capacity;
  runtime->config.retained_byte_capacity = config->retained_byte_capacity;
  runtime->owner_thread_id = owner_thread_id;
  runtime->runtime_instance_id = instance_id;
  runtime->next_token_sequence = 1u;
  runtime->next_generation = 1u;
  runtime->storage_reserved_bytes = requirements.storage_bytes;
  runtime->retained_reserved_bytes = requirements.retained_reserved_bytes;
  runtime->scratch_reserved_bytes = requirements.scratch_reserved_bytes;
  runtime->owning_string_type_index = string_index;
  runtime->owning_state_type_index = state_index;
  runtime->owning_continuation_type_index = continuation_index;
  runtime->state_type_id = module->layout->types[state_index].type_id;
  runtime->continuation_type_id =
      module->layout->types[continuation_index].type_id;
  runtime->owning_entries[0] = &module->entries[0];
  runtime->owning_entries[1] = &module->entries[1];
  runtime->owning_entries[2] = &module->entries[2];
  runtime->mailboxes =
      (al_mailbox_slot *)(void *)(base + layout.slots_offset);
  runtime->owning_scratch_slots =
      (al_owning_scratch_slot *)(void *)(base + layout.scratch_slots_offset);
  runtime->owning_attachments = (al_owning_attachment *)(void *)(
      base + layout.attachments_offset);
  runtime->owning_text_staging = base + layout.text_staging_offset;

  for (mailbox_id = 0u; mailbox_id < config->mailbox_capacity; ++mailbox_id) {
    al_owning_attachment *attachment = &runtime->owning_attachments[mailbox_id];
    attachment->scratch_slot_index = AL_OWNING_NO_SCRATCH_SLOT;
    attachment->protected_cursor_bytes = 0u;
    attachment->root_count = 0u;
    attachment->reserved = 0u;
    memset(attachment->roots, 0, sizeof(attachment->roots));
  }
  for (mailbox_id = 0u; mailbox_id < config->scratch_slot_capacity;
       ++mailbox_id) {
    al_owning_scratch_slot *scratch_slot =
        &runtime->owning_scratch_slots[mailbox_id];
    size_t scratch_base = layout.scratch_backing_offset +
                          (size_t)mailbox_id * layout.scratch_slot_stride;
    scratch_slot->data = base + scratch_base + layout.scratch_data_offset;
    scratch_slot->init_bitmap = layout.bitmap_bytes == 0u
                                    ? NULL
                                    : base + scratch_base +
                                          layout.init_bitmap_offset;
    scratch_slot->poison_bitmap = layout.bitmap_bytes == 0u
                                      ? NULL
                                      : base + scratch_base +
                                            layout.poison_bitmap_offset;
    scratch_slot->state = AL_OWNING_SCRATCH_FREE;
    scratch_slot->owner_mailbox_id = AL_OWNING_NO_SCRATCH_SLOT;
    scratch_slot->context.abi_version = AL_OWNING_STACK_ABI_VERSION;
    scratch_slot->context.stack_capacity_bytes =
        config->scratch_byte_capacity;
    scratch_slot->context.init_bitmap_bytes =
        (uint32_t)layout.bitmap_bytes;
    scratch_slot->context.stack_data = scratch_slot->data;
    scratch_slot->context.init_bitmap = scratch_slot->init_bitmap;
    scratch_slot->context.poison_bitmap = scratch_slot->poison_bitmap;
    scratch_slot->context.trace_events = NULL;
    scratch_slot->context.trace_event_capacity = 0u;
    al_owning_begin(&scratch_slot->context);
#if !AL_MAILBOX_RUNTIME_TRUSTED
    memset(scratch_slot->data, AL_MAILBOX_POISON_BYTE,
           config->scratch_byte_capacity);
#endif
  }

  for (mailbox_id = 0u; mailbox_id < config->mailbox_capacity; ++mailbox_id) {
    al_mailbox_slot *slot = &runtime->mailboxes[mailbox_id];
    size_t mailbox_base =
        layout.bank_backing_offset + (size_t)mailbox_id * layout.bank_stride;
    uint8_t *bank0_bytes = base + mailbox_base + layout.bank_data_offset[0];
    uint8_t *bank1_bytes = base + mailbox_base + layout.bank_data_offset[1];
    al_owning_bank_root *bank0_roots =
        (al_owning_bank_root *)(void *)(base + mailbox_base +
                                        layout.bank_roots_offset[0]);
    al_owning_bank_root *bank1_roots =
        (al_owning_bank_root *)(void *)(base + mailbox_base +
                                        layout.bank_roots_offset[1]);
    al_owning_bank_result bank_result = al_owning_byte_store_init(
        &slot->payload.owning, bank0_bytes, config->retained_byte_capacity,
        bank0_roots, AL_MAILBOX_MAX_ROOTS, bank1_bytes,
        config->retained_byte_capacity, bank1_roots, AL_MAILBOX_MAX_ROOTS);
    if (bank_result != AL_OWNING_BANK_OK)
      return al_map_owning_bank_result(bank_result);
#if !AL_MAILBOX_RUNTIME_TRUSTED
    memset(bank0_bytes, AL_MAILBOX_POISON_BYTE,
           config->retained_byte_capacity);
    memset(bank1_bytes, AL_MAILBOX_POISON_BYTE,
           config->retained_byte_capacity);
#endif
  }
#if !AL_MAILBOX_RUNTIME_TRUSTED
  memset(runtime->owning_text_staging, AL_MAILBOX_POISON_BYTE,
         config->text_staging_byte_capacity);
#endif
  *out_runtime = runtime;
  return AL_MAILBOX_OK;
}

al_mailbox_result al_mailbox_runtime_init_owning(
    const al_owning_mailbox_module *module,
    const al_mailbox_owning_config *config, void *storage,
    uint64_t storage_bytes, al_mailbox_runtime **out_runtime) {
  return al_runtime_init_owning_impl(
      module, config, storage, storage_bytes, out_runtime,
      al_mailbox_platform_current_thread_id());
}

static al_mailbox_result al_owning_bank_root_input(
    al_mailbox_runtime *runtime, const al_owning_byte_bank *bank,
    uint32_t root_index, uint32_t type_index,
    al_owning_external_slice *out_input) {
  al_owning_stack_context scanner;
  const al_owning_bank_root *root;
  uint32_t payload_bytes;
  uint32_t extent_bytes;
  if (bank == NULL || out_input == NULL || bank->bytes == NULL ||
      bank->roots == NULL || root_index >= bank->root_count ||
      type_index >= runtime->owning_module->layout->type_count ||
      !al_checked_span(bank->bytes, bank->byte_capacity))
    return AL_MAILBOX_INVALID_REFERENCE;
  root = &bank->roots[root_index];
  if (root->type_id != runtime->owning_module->layout->types[type_index].type_id ||
      root->extent_bytes == 0u || root->offset_bytes > bank->used_bytes ||
      root->extent_bytes > bank->used_bytes - root->offset_bytes ||
      root->owner_end_bytes != root->offset_bytes + root->extent_bytes ||
      root->payload_bytes > root->extent_bytes)
    return AL_MAILBOX_INVALID_REFERENCE;
  memset(&scanner, 0, sizeof(scanner));
  scanner.abi_version = AL_OWNING_STACK_ABI_VERSION;
  scanner.status = AL_OWNING_STATUS_OK;
  scanner.available_bytes = UINT32_MAX;
  if (al_owning_measure_external_value(
          &scanner, runtime->owning_module->layout,
          type_index, bank->bytes, bank->used_bytes, root->offset_bytes, 0u,
          &payload_bytes, &extent_bytes) != 0 ||
      payload_bytes != root->payload_bytes || extent_bytes != root->extent_bytes)
    return AL_MAILBOX_INVALID_REFERENCE;
  out_input->bytes = bank->bytes + root->offset_bytes;
  out_input->extent_bytes = root->extent_bytes;
  out_input->type_index = type_index;
  return AL_MAILBOX_OK;
}

static al_mailbox_result al_owning_validate_inputs(
    al_mailbox_runtime *runtime, const al_owning_mailbox_entry *entry,
    const al_owning_external_slice *inputs) {
  al_owning_stack_context scanner;
  uint64_t total_bytes = 0u;
  uint32_t index;
  if (runtime == NULL || entry == NULL || inputs == NULL ||
      entry->input_count > AL_MAILBOX_MAX_INPUTS)
    return AL_MAILBOX_INVALID_ARGUMENT;
  memset(&scanner, 0, sizeof(scanner));
  scanner.abi_version = AL_OWNING_STACK_ABI_VERSION;
  scanner.status = AL_OWNING_STATUS_OK;
  scanner.available_bytes = UINT32_MAX;
  for (index = 0u; index < entry->input_count; ++index) {
    uint32_t payload_bytes;
    uint32_t extent_bytes;
    const al_owning_external_slice *input = &inputs[index];
    if (input->bytes == NULL || input->extent_bytes == 0u ||
        input->type_index != entry->input_type_indexes[index] ||
        !al_checked_span(input->bytes, input->extent_bytes) ||
        !al_add_u64(total_bytes, input->extent_bytes, &total_bytes) ||
        total_bytes > runtime->owning_config.scratch_byte_capacity)
      return total_bytes > runtime->owning_config.scratch_byte_capacity
                 ? AL_MAILBOX_SCRATCH_CAPACITY
                 : AL_MAILBOX_INVALID_REFERENCE;
    if (al_owning_measure_external_value(
            &scanner, runtime->owning_module->layout,
            input->type_index, input->bytes, input->extent_bytes, 0u, 0u,
            &payload_bytes, &extent_bytes) != 0 ||
        extent_bytes != input->extent_bytes)
      return AL_MAILBOX_INVALID_REFERENCE;
  }
  return AL_MAILBOX_OK;
}

static al_mailbox_result al_owning_text_input_fits_scratch(
    const al_mailbox_runtime *runtime, uint32_t root_input_count,
    uint32_t text_extent_bytes) {
  uint64_t total_bytes = text_extent_bytes;
  uint32_t index;
  if (runtime == NULL || root_input_count > AL_MAILBOX_MAX_ROOTS)
    return AL_MAILBOX_INVALID_ARGUMENT;
  for (index = 0u; index < root_input_count; ++index) {
    if (!al_add_u64(total_bytes, runtime->owning_inputs[index].extent_bytes,
                    &total_bytes))
      return AL_MAILBOX_SIZE_OVERFLOW;
  }
  return total_bytes > runtime->owning_config.scratch_byte_capacity
             ? AL_MAILBOX_SCRATCH_CAPACITY
             : AL_MAILBOX_OK;
}

static uint32_t al_owning_context_storage_valid(
    const al_mailbox_runtime *runtime,
    const al_owning_scratch_slot *scratch_slot) {
  const al_owning_stack_context *context;
  uint32_t bitmap_bytes;
  if (runtime == NULL || scratch_slot == NULL)
    return 0u;
  context = &scratch_slot->context;
#if AL_MAILBOX_RUNTIME_TRUSTED
  bitmap_bytes = 0u;
#else
  bitmap_bytes = runtime->owning_config.scratch_byte_capacity / 8u +
                 (runtime->owning_config.scratch_byte_capacity % 8u != 0u
                      ? 1u
                      : 0u);
#endif
  return context->abi_version == AL_OWNING_STACK_ABI_VERSION &&
         al_owning_build_profile_valid(context) != 0 &&
         context->stack_capacity_bytes ==
             runtime->owning_config.scratch_byte_capacity &&
         context->cursor_bytes <= context->stack_capacity_bytes &&
         context->peak_cursor_bytes <= context->stack_capacity_bytes &&
         context->peak_cursor_bytes >= context->cursor_bytes &&
         context->init_bitmap_bytes == bitmap_bytes &&
         context->stack_data == scratch_slot->data &&
         context->init_bitmap == scratch_slot->init_bitmap &&
         context->poison_bitmap == scratch_slot->poison_bitmap &&
         context->trace_events == NULL &&
         context->trace_event_capacity == 0u &&
         context->status <= AL_OWNING_STATUS_INTERNAL;
}

/* Mirror only al_owning_begin's bitmap-clear preconditions. The generated
 * callback may leave other context fields invalid; begin resets those fields
 * before it checks the fields below. */
static uint64_t al_owning_begin_bitmap_store_operations(
    const al_owning_stack_context *context) {
  if (context == NULL || al_owning_build_profile_valid(context) == 0)
    return 0u;
#if AL_MAILBOX_RUNTIME_TRUSTED
  return 0u;
#else
  return (uint64_t)context->init_bitmap_bytes * 2u;
#endif
}

static void al_owning_context_restore_storage(
    const al_mailbox_runtime *runtime, al_owning_scratch_slot *scratch_slot,
    uint32_t safe_cursor) {
  al_owning_stack_context *context = &scratch_slot->context;
#if AL_MAILBOX_RUNTIME_TRUSTED
  uint32_t bitmap_bytes = 0u;
#else
  uint32_t bitmap_bytes = runtime->owning_config.scratch_byte_capacity / 8u +
                          (runtime->owning_config.scratch_byte_capacity % 8u !=
                                   0u
                               ? 1u
                               : 0u);
#endif
  context->abi_version = AL_OWNING_STACK_ABI_VERSION;
  context->stack_capacity_bytes =
      runtime->owning_config.scratch_byte_capacity;
  context->cursor_bytes = safe_cursor;
  context->peak_cursor_bytes = safe_cursor;
  context->init_bitmap_bytes = bitmap_bytes;
  context->stack_data = scratch_slot->data;
  context->init_bitmap = scratch_slot->init_bitmap;
  context->poison_bitmap = scratch_slot->poison_bitmap;
  context->trace_events = NULL;
  context->trace_event_capacity = 0u;
  context->trace_event_count = 0u;
  context->status = AL_OWNING_STATUS_INTERNAL;
  context->error_id = 0u;
  context->required_bytes = 0u;
  context->available_bytes = context->stack_capacity_bytes;
  context->deep_copy_bytes = 0u;
  context->move_bytes = 0u;
  context->input_copy_bytes = 0u;
  context->retained_copy_bytes = 0u;
}

static al_mailbox_result al_owning_scratch_slot_checkout(
    al_mailbox_runtime *runtime, uint32_t mailbox_id,
    uint32_t *out_slot_index) {
  uint32_t index;
  if (runtime == NULL || out_slot_index == NULL)
    return AL_MAILBOX_INVALID_ARGUMENT;
  if (runtime->outstanding_scratch_leases >=
      runtime->owning_config.scratch_slot_capacity)
    return AL_MAILBOX_SCRATCH_CAPACITY;
  for (index = 0u; index < runtime->owning_config.scratch_slot_capacity;
       ++index) {
    al_owning_scratch_slot *scratch_slot = &runtime->owning_scratch_slots[index];
    uint64_t bitmap_store_operations;
    if (scratch_slot->state != AL_OWNING_SCRATCH_FREE)
      continue;
    if (al_owning_context_storage_valid(runtime, scratch_slot) == 0u)
      return AL_MAILBOX_INVALID_REFERENCE;
    scratch_slot->state = AL_OWNING_SCRATCH_CHECKED_OUT;
    scratch_slot->owner_mailbox_id = mailbox_id;
#if (!defined(AL_MAILBOX_FAST_RESET) || !AL_MAILBOX_FAST_RESET) &&            \
    !AL_MAILBOX_RUNTIME_TRUSTED
    memset(scratch_slot->data, AL_MAILBOX_POISON_BYTE,
           runtime->owning_config.scratch_byte_capacity);
    al_saturating_add(
        &runtime->owning_reset_full_capacity_payload_write_bytes_requested,
        runtime->owning_config.scratch_byte_capacity);
#endif
    bitmap_store_operations =
        al_owning_begin_bitmap_store_operations(&scratch_slot->context);
    al_owning_begin(&scratch_slot->context);
    al_saturating_add(&runtime->owning_reset_bitmap_store_operations,
                      bitmap_store_operations);
    ++runtime->outstanding_scratch_leases;
    al_saturating_increment(&runtime->scratch_lease_acquisitions);
    *out_slot_index = index;
    return AL_MAILBOX_OK;
  }
  return AL_MAILBOX_SCRATCH_CAPACITY;
}

static uint32_t al_owning_scratch_slot_find_free(
    const al_mailbox_runtime *runtime) {
  uint32_t index;
  if (runtime == NULL)
    return AL_OWNING_NO_SCRATCH_SLOT;
  for (index = 0u; index < runtime->owning_config.scratch_slot_capacity;
       ++index) {
    if (runtime->owning_scratch_slots[index].state ==
        AL_OWNING_SCRATCH_FREE)
      return index;
  }
  return AL_OWNING_NO_SCRATCH_SLOT;
}

static void al_owning_scratch_slot_release(al_mailbox_runtime *runtime,
                                           uint32_t slot_index) {
  al_owning_scratch_slot *scratch_slot;
  uint32_t cursor_bytes;
  uint32_t reset_cursor_bytes;
  uint64_t bitmap_store_operations;
  if (runtime == NULL ||
      slot_index >= runtime->owning_config.scratch_slot_capacity)
    return;
  scratch_slot = &runtime->owning_scratch_slots[slot_index];
  if (scratch_slot->state == AL_OWNING_SCRATCH_FREE)
    return;
  reset_cursor_bytes = scratch_slot->context.cursor_bytes;
  cursor_bytes = reset_cursor_bytes;
  if (cursor_bytes > runtime->owning_config.scratch_byte_capacity)
    cursor_bytes = runtime->owning_config.scratch_byte_capacity;
  al_saturating_add(&runtime->owning_turn_reset_bytes, cursor_bytes);
  /* release_to(0) cannot take its new_cursor > old_cursor early return here,
   * and the context object is owned by this slot. */
  al_owning_release_to(&scratch_slot->context, 0u, 0u, 0u, 0u);
#if !AL_MAILBOX_RUNTIME_TRUSTED
  al_saturating_add(&runtime->owning_reset_bitmap_store_operations,
                    (uint64_t)reset_cursor_bytes * UINT64_C(2));
  al_saturating_add(
      &runtime->owning_reset_live_prefix_payload_write_bytes_requested,
      reset_cursor_bytes);
#endif
#if (!defined(AL_MAILBOX_FAST_RESET) || !AL_MAILBOX_FAST_RESET) &&            \
    !AL_MAILBOX_RUNTIME_TRUSTED
  memset(scratch_slot->data, AL_MAILBOX_POISON_BYTE,
         runtime->owning_config.scratch_byte_capacity);
  al_saturating_add(
      &runtime->owning_reset_full_capacity_payload_write_bytes_requested,
      runtime->owning_config.scratch_byte_capacity);
#endif
  bitmap_store_operations =
      al_owning_begin_bitmap_store_operations(&scratch_slot->context);
  al_owning_begin(&scratch_slot->context);
  al_saturating_add(&runtime->owning_reset_bitmap_store_operations,
                    bitmap_store_operations);
  if (scratch_slot->owner_mailbox_id < runtime->config.mailbox_capacity) {
    al_owning_attachment *attachment =
        &runtime->owning_attachments[scratch_slot->owner_mailbox_id];
    if (attachment->scratch_slot_index == slot_index) {
      attachment->scratch_slot_index = AL_OWNING_NO_SCRATCH_SLOT;
      attachment->protected_cursor_bytes = 0u;
      attachment->root_count = 0u;
      attachment->reserved = 0u;
      memset(attachment->roots, 0, sizeof(attachment->roots));
    }
  }
  scratch_slot->state = AL_OWNING_SCRATCH_FREE;
  scratch_slot->owner_mailbox_id = AL_OWNING_NO_SCRATCH_SLOT;
  if (runtime->outstanding_scratch_leases != 0u)
    --runtime->outstanding_scratch_leases;
  al_saturating_increment(&runtime->scratch_lease_returns);
}

static void al_owning_scratch_slot_attach(al_mailbox_runtime *runtime,
                                          uint32_t slot_index) {
  if (runtime != NULL &&
      slot_index < runtime->owning_config.scratch_slot_capacity &&
      runtime->owning_scratch_slots[slot_index].state ==
          AL_OWNING_SCRATCH_CHECKED_OUT)
    runtime->owning_scratch_slots[slot_index].state =
        AL_OWNING_SCRATCH_ATTACHED;
}

static al_mailbox_result al_owning_turn_begin(al_mailbox_runtime *runtime) {
  if (runtime == NULL || runtime->busy != 0u)
    return AL_MAILBOX_BUSY;
  runtime->busy = 1u;
  al_saturating_increment(&runtime->handler_invocations);
  return AL_MAILBOX_OK;
}

static void al_owning_turn_end(al_mailbox_runtime *runtime) {
  if (runtime != NULL)
    runtime->busy = 0u;
}

static uint64_t al_counter_delta(uint64_t after, uint64_t before) {
  return after >= before ? after - before : UINT64_MAX;
}

static al_mailbox_result al_owning_validate_output_slices(
    al_mailbox_runtime *runtime, const al_owning_mailbox_entry *entry,
    al_owning_stack_context *context,
    const al_owning_bank_stack_slice *outputs) {
  uint32_t index;
  for (index = 0u; index < entry->output_count; ++index) {
    const al_owning_bank_stack_slice *output = &outputs[index];
    al_owning_value_size measured;
    if (output->type_index != entry->output_type_indexes[index] ||
        output->reserved != 0u || output->source_offset_bytes > context->cursor_bytes ||
        output->source_owner_end_bytes > context->cursor_bytes ||
        output->source_offset_bytes >= output->source_owner_end_bytes ||
        (output->source_offset_bytes & 7u) != 0u ||
        al_owning_measure_value(
            context, runtime->owning_module->layout, output->type_index,
            output->source_offset_bytes, output->source_owner_end_bytes, 0u,
            &measured) != 0 ||
        measured.extent_bytes == 0u ||
        measured.extent_bytes > output->source_owner_end_bytes -
                                    output->source_offset_bytes)
      return AL_MAILBOX_INVALID_REFERENCE;
  }
  return AL_MAILBOX_OK;
}

static al_mailbox_result al_owning_validate_begin_state_fits_bank(
    al_mailbox_runtime *runtime, const al_owning_byte_store *store,
    al_owning_stack_context *context,
    const al_owning_bank_stack_slice *state) {
  const al_owning_byte_bank *staging;
  al_owning_value_size measured;
  if (runtime == NULL || store == NULL || context == NULL || state == NULL ||
      store->active_index > 1u ||
      state->type_index != runtime->owning_state_type_index ||
      state->reserved != 0u)
    return AL_MAILBOX_INVALID_REFERENCE;
  staging = &store->banks[1u - store->active_index];
  if (staging->root_capacity == 0u)
    return AL_MAILBOX_RETAINED_CAPACITY;
  if (al_owning_measure_value(
          context, runtime->owning_module->layout, state->type_index,
          state->source_offset_bytes, state->source_owner_end_bytes, 0u,
          &measured) != 0 ||
      measured.extent_bytes == 0u)
    return AL_MAILBOX_INVALID_REFERENCE;
  return measured.extent_bytes <= staging->byte_capacity
             ? AL_MAILBOX_OK
             : AL_MAILBOX_RETAINED_CAPACITY;
}

static void al_owning_record_context_deltas(
    al_mailbox_runtime *runtime, const al_owning_stack_context *before,
    const al_owning_stack_context *after) {
  al_saturating_add(&runtime->owning_deep_copy_bytes,
                    al_counter_delta(after->deep_copy_bytes,
                                     before->deep_copy_bytes));
  al_saturating_add(&runtime->owning_move_bytes,
                    al_counter_delta(after->move_bytes, before->move_bytes));
  al_saturating_add(&runtime->owning_input_import_bytes,
                    al_counter_delta(after->input_copy_bytes,
                                     before->input_copy_bytes));
  if (after->peak_cursor_bytes > runtime->scratch_high_water_bytes)
    runtime->scratch_high_water_bytes = after->peak_cursor_bytes;
}

static al_mailbox_result al_execute_owning_entry(
    al_mailbox_runtime *runtime, al_mailbox_slot *slot,
    uint32_t scratch_slot_index, const al_owning_mailbox_entry *entry,
    const al_owning_external_slice *inputs, uint32_t publish_outputs,
    al_mailbox_call_info *call_info) {
  al_mailbox_result result;
  al_owning_bank_result bank_result;
  al_owning_byte_store *store;
  al_owning_scratch_slot *scratch_slot;
  al_owning_stack_context *context;
  al_owning_stack_context zero_context;
  uint32_t handler_status;
  uint32_t context_storage_valid;
  uint32_t callback_outputs_valid = 0u;
  uint64_t publication_bytes = 0u;
  uint64_t input_delta;
  uint32_t transaction_open = 0u;

  if (runtime == NULL || slot == NULL || entry == NULL ||
      scratch_slot_index >= runtime->owning_config.scratch_slot_capacity ||
      entry->output_count > AL_MAILBOX_MAX_ROOTS)
    return AL_MAILBOX_INVALID_ARGUMENT;
  scratch_slot = &runtime->owning_scratch_slots[scratch_slot_index];
  if (scratch_slot->state != AL_OWNING_SCRATCH_CHECKED_OUT ||
      scratch_slot->owner_mailbox_id >= runtime->config.mailbox_capacity)
    return AL_MAILBOX_INVALID_REFERENCE;
  if (al_owning_context_storage_valid(runtime, scratch_slot) == 0u)
    return AL_MAILBOX_INVALID_REFERENCE;
  result = al_owning_validate_inputs(runtime, entry, inputs);
  if (result != AL_MAILBOX_OK)
    return result;
  if (runtime->busy != 0u)
    return AL_MAILBOX_BUSY;
  {
    uint32_t scratch_generation;
    uint32_t staging_generation;
    result = al_reserve_generation_pair(runtime, &scratch_generation,
                                        &staging_generation);
    if (result != AL_MAILBOX_OK)
      return result;
    (void)scratch_generation;
    (void)staging_generation;
  }
  store = &slot->payload.owning;
  if (publish_outputs != 0u) {
    bank_result = al_owning_byte_store_begin(store);
    if (bank_result != AL_OWNING_BANK_OK)
      return al_map_owning_bank_result(bank_result);
    transaction_open = 1u;
  }
  result = al_owning_turn_begin(runtime);
  if (result != AL_MAILBOX_OK) {
    if (transaction_open != 0u)
      (void)al_owning_byte_store_abort(store);
    return result;
  }
  context = &scratch_slot->context;
  memset(runtime->owning_outputs, 0xff, sizeof(runtime->owning_outputs));
  handler_status = (uint32_t)entry->execute(
      context, inputs, entry->input_count, runtime->owning_outputs,
      entry->output_count);
  context_storage_valid =
      al_owning_context_storage_valid(runtime, scratch_slot);
  if (context_storage_valid == 0u) {
    uint32_t safe_cursor = context->cursor_bytes;
    if (safe_cursor > runtime->owning_config.scratch_byte_capacity)
      safe_cursor = runtime->owning_config.scratch_byte_capacity;
    al_owning_context_restore_storage(runtime, scratch_slot, safe_cursor);
    handler_status = AL_OWNING_STATUS_INTERNAL;
  }
  if (call_info != NULL) {
    call_info->handler_status = (int32_t)handler_status;
    call_info->error_metadata_id =
        context->status == AL_OWNING_STATUS_OK ? -1 : (int32_t)context->error_id;
    call_info->error_argument0 =
        context->status == AL_OWNING_STATUS_OK ? 0 : context->required_bytes;
    call_info->error_argument1 =
        context->status == AL_OWNING_STATUS_OK ? 0 : context->available_bytes;
    call_info->steps_consumed = context->steps_consumed;
  }

  if (context_storage_valid == 0u) {
    result = AL_MAILBOX_INVALID_REFERENCE;
  } else if (handler_status != 0u || context->status != AL_OWNING_STATUS_OK) {
    result = al_map_owning_status(
        context->status == AL_OWNING_STATUS_OK ? handler_status
                                               : context->status);
  } else {
    result = al_owning_validate_output_slices(
        runtime, entry, context, runtime->owning_outputs);
    callback_outputs_valid = result == AL_MAILBOX_OK ? 1u : 0u;
    if (result == AL_MAILBOX_OK && publish_outputs == 0u &&
        entry == runtime->owning_entries[1])
      result = al_owning_validate_begin_state_fits_bank(
          runtime, store, context, &runtime->owning_outputs[0]);
  }

  if (callback_outputs_valid != 0u) {
    /* Count callback-produced descriptors even if later publication fails. */
    al_saturating_add(&runtime->owning_returned_output_descriptors,
                      entry->output_count);
  }

  if (result == AL_MAILBOX_OK && publish_outputs != 0u) {
    bank_result = al_owning_byte_store_stage_stack_values(
        store, context, runtime->owning_module->layout,
        runtime->owning_outputs, entry->output_count, 0u);
    result = al_map_owning_bank_result(bank_result);
    if (result == AL_MAILBOX_OK) {
      bank_result = al_owning_byte_store_commit(store);
      result = al_map_owning_bank_result(bank_result);
      if (result == AL_MAILBOX_OK) {
        publication_bytes =
            al_owning_byte_store_last_committed_copy_bytes(store);
        transaction_open = 0u;
      }
    }
  }

  if (result != AL_MAILBOX_OK) {
    if (transaction_open != 0u)
      (void)al_owning_byte_store_abort(store);
    al_saturating_increment(&runtime->handler_failures);
    if (call_info != NULL && call_info->handler_status == 0) {
      call_info->handler_status =
          result == AL_MAILBOX_RETAINED_CAPACITY
              ? AL_OWNING_STATUS_RETAINED_CAPACITY
              : AL_OWNING_STATUS_INVALID_REQUEST;
      call_info->error_metadata_id = -1;
    }
  } else {
    if (publish_outputs != 0u) {
      al_saturating_add(&runtime->owning_publication_copy_bytes,
                        publication_bytes);
      if (entry == runtime->owning_entries[1])
        al_saturating_add(&runtime->owning_begin_publication_copy_bytes,
                          publication_bytes);
      {
        uint32_t old_index = 1u - store->active_index;
        al_owning_byte_bank *old_bank = &store->banks[old_index];
#if !AL_MAILBOX_RUNTIME_TRUSTED
        memset(store->storage_bytes[old_index], AL_MAILBOX_POISON_BYTE,
               old_bank->byte_capacity);
#endif
        memset(store->storage_roots[old_index], 0,
               (size_t)old_bank->root_capacity * sizeof(al_owning_bank_root));
        old_bank->used_bytes = 0u;
        old_bank->root_count = 0u;
      }
    }
  }

  memset(&zero_context, 0, sizeof(zero_context));
  al_owning_record_context_deltas(runtime, &zero_context, context);
  input_delta = context->input_copy_bytes;
  if (entry == runtime->owning_entries[2]) {
    uint64_t root_bytes = (uint64_t)inputs[0].extent_bytes +
                          (uint64_t)inputs[1].extent_bytes;
    al_saturating_add(&runtime->owning_resume_root_import_bytes,
                      input_delta < root_bytes ? input_delta : root_bytes);
  }
  al_owning_turn_end(runtime);
  return result;
}

static al_mailbox_result al_owning_validate_attached_roots(
    al_mailbox_runtime *runtime, al_owning_scratch_slot *scratch_slot,
    const al_owning_attachment *attachment) {
  al_owning_stack_context *context = &scratch_slot->context;
  uint32_t expected_indexes[AL_MAILBOX_MAX_ROOTS] = {
      runtime->owning_state_type_index,
      runtime->owning_continuation_type_index};
  uint32_t index;
  for (index = 0u; index < AL_MAILBOX_MAX_ROOTS; ++index) {
    const al_owning_bank_stack_slice *root = &attachment->roots[index];
    al_owning_value_size measured;
    if (root->type_index != expected_indexes[index] || root->reserved != 0u ||
        root->source_offset_bytes >= root->source_owner_end_bytes ||
        root->source_owner_end_bytes > attachment->protected_cursor_bytes ||
        (root->source_offset_bytes & 7u) != 0u ||
        al_owning_measure_value(context, runtime->owning_module->layout,
                                root->type_index, root->source_offset_bytes,
                                root->source_owner_end_bytes, 0u,
                                &measured) != 0 ||
        measured.extent_bytes == 0u ||
        measured.extent_bytes > root->source_owner_end_bytes -
                                    root->source_offset_bytes)
      return AL_MAILBOX_INVALID_REFERENCE;
  }
  return AL_MAILBOX_OK;
}

static al_mailbox_result al_execute_owning_associated_resume(
    al_mailbox_runtime *runtime, al_mailbox_slot *mailbox,
    uint32_t mailbox_id, const al_owning_external_slice *completion,
    al_mailbox_call_info *call_info) {
  al_owning_scratch_slot *scratch_slot;
  al_owning_attachment *attachment;
  al_owning_stack_context *context;
  al_owning_stack_context saved_context;
  al_owning_byte_store *store;
  al_owning_bank_result bank_result;
  uint32_t scratch_index;
  uint32_t protected_cursor;
  uint32_t context_storage_valid;
  int32_t handler_status;
  uint32_t callback_outputs_valid = 0u;
  uint32_t transaction_open = 0u;
  uint32_t cursor_after;
  uint64_t publication_bytes = 0u;
  al_mailbox_result result;

  if (runtime == NULL || mailbox == NULL || completion == NULL ||
      mailbox_id >= runtime->config.mailbox_capacity)
    return AL_MAILBOX_INVALID_ARGUMENT;
  attachment = &runtime->owning_attachments[mailbox_id];
  scratch_index = attachment->scratch_slot_index;
  if (attachment->root_count != AL_MAILBOX_MAX_ROOTS ||
      attachment->reserved != 0u ||
      scratch_index >= runtime->owning_config.scratch_slot_capacity)
    return AL_MAILBOX_STALE_TOKEN;
  scratch_slot = &runtime->owning_scratch_slots[scratch_index];
  if (scratch_slot->state != AL_OWNING_SCRATCH_ATTACHED ||
      scratch_slot->owner_mailbox_id != mailbox_id)
    return AL_MAILBOX_STALE_TOKEN;
  context = &scratch_slot->context;
  protected_cursor = attachment->protected_cursor_bytes;
  if (!al_owning_context_storage_valid(runtime, scratch_slot) ||
      context->cursor_bytes != protected_cursor || protected_cursor == 0u ||
      context->status != AL_OWNING_STATUS_OK || context->call_depth != 0u ||
      completion->bytes == NULL || completion->extent_bytes == 0u ||
      completion->type_index != runtime->owning_string_type_index ||
      !al_checked_span(completion->bytes, completion->extent_bytes))
    return AL_MAILBOX_INVALID_REFERENCE;

  saved_context = *context;
  result = al_owning_validate_attached_roots(runtime, scratch_slot, attachment);
  if (result != AL_MAILBOX_OK || context->status != AL_OWNING_STATUS_OK) {
    *context = saved_context;
    return result == AL_MAILBOX_OK ? AL_MAILBOX_INVALID_REFERENCE : result;
  }
  if ((uint64_t)protected_cursor + completion->extent_bytes >
      runtime->owning_config.scratch_byte_capacity)
    return AL_MAILBOX_SCRATCH_CAPACITY;
  if (runtime->busy != 0u)
    return AL_MAILBOX_BUSY;
  {
    uint32_t scratch_generation;
    uint32_t staging_generation;
    result = al_reserve_generation_pair(runtime, &scratch_generation,
                                        &staging_generation);
    if (result != AL_MAILBOX_OK)
      return result;
    (void)scratch_generation;
    (void)staging_generation;
  }

  store = &mailbox->payload.owning;
  bank_result = al_owning_byte_store_begin(store);
  if (bank_result != AL_OWNING_BANK_OK)
    return al_map_owning_bank_result(bank_result);
  transaction_open = 1u;
  result = al_owning_turn_begin(runtime);
  if (result != AL_MAILBOX_OK) {
    (void)al_owning_byte_store_abort(store);
    return result;
  }
  memset(runtime->owning_outputs, 0xff, sizeof(runtime->owning_outputs));
  handler_status = runtime->owning_module->associated_resume(
      context, attachment->roots, attachment->root_count, completion,
      protected_cursor, runtime->owning_outputs, 1u);
  context_storage_valid =
      al_owning_context_storage_valid(runtime, scratch_slot);
  if (context_storage_valid == 0u) {
    uint32_t safe_cursor = context->cursor_bytes;
    if (safe_cursor > runtime->owning_config.scratch_byte_capacity)
      safe_cursor = runtime->owning_config.scratch_byte_capacity;
    al_owning_context_restore_storage(runtime, scratch_slot, safe_cursor);
    handler_status = AL_OWNING_STATUS_INTERNAL;
  }
  cursor_after = context->cursor_bytes;
  if (call_info != NULL) {
    call_info->handler_status = handler_status;
    call_info->error_metadata_id =
        context->status == AL_OWNING_STATUS_OK ? -1 : (int32_t)context->error_id;
    call_info->error_argument0 =
        context->status == AL_OWNING_STATUS_OK ? 0 : context->required_bytes;
    call_info->error_argument1 =
        context->status == AL_OWNING_STATUS_OK ? 0 : context->available_bytes;
    call_info->steps_consumed = context->steps_consumed;
  }
  if (context_storage_valid == 0u) {
    result = AL_MAILBOX_INVALID_REFERENCE;
  } else if (handler_status != 0 || context->status != AL_OWNING_STATUS_OK) {
    result = al_map_owning_status(
        context->status == AL_OWNING_STATUS_OK ? (uint32_t)handler_status
                                               : context->status);
  } else {
    result = al_owning_validate_output_slices(
        runtime, runtime->owning_entries[2], context,
        runtime->owning_outputs);
    callback_outputs_valid = result == AL_MAILBOX_OK ? 1u : 0u;
  }
  if (result == AL_MAILBOX_OK) {
    bank_result = al_owning_byte_store_stage_stack_values(
        store, context, runtime->owning_module->layout,
        runtime->owning_outputs, 1u, 0u);
    result = al_map_owning_bank_result(bank_result);
    if (result == AL_MAILBOX_OK) {
      bank_result = al_owning_byte_store_commit(store);
      result = al_map_owning_bank_result(bank_result);
      if (result == AL_MAILBOX_OK) {
        publication_bytes =
            al_owning_byte_store_last_committed_copy_bytes(store);
        transaction_open = 0u;
      }
    }
  }

  if (context_storage_valid != 0u) {
    al_owning_record_context_deltas(runtime, &saved_context, context);
    if (context->peak_cursor_bytes > runtime->scratch_high_water_bytes)
      runtime->scratch_high_water_bytes = context->peak_cursor_bytes;
  }
  if (callback_outputs_valid != 0u)
    al_saturating_add(&runtime->owning_returned_output_descriptors, 1u);

  if (result != AL_MAILBOX_OK) {
    if (transaction_open != 0u)
      (void)al_owning_byte_store_abort(store);
    al_saturating_increment(&runtime->handler_failures);
    if (call_info != NULL && call_info->handler_status == 0) {
      call_info->handler_status =
          result == AL_MAILBOX_RETAINED_CAPACITY
              ? AL_OWNING_STATUS_RETAINED_CAPACITY
              : (result == AL_MAILBOX_SCRATCH_CAPACITY
                     ? AL_OWNING_STATUS_STACK_CAPACITY
                     : AL_OWNING_STATUS_INVALID_REQUEST);
      call_info->error_metadata_id = -1;
    }
    if (context_storage_valid != 0u && cursor_after >= protected_cursor) {
      al_owning_release_to(context, protected_cursor, 0u, 0u, 0u);
      *context = saved_context;
    } else {
      *context = saved_context;
    }
  } else {
    al_saturating_add(&runtime->owning_publication_copy_bytes,
                      publication_bytes);
    {
      uint32_t old_index = 1u - store->active_index;
      al_owning_byte_bank *old_bank = &store->banks[old_index];
#if !AL_MAILBOX_RUNTIME_TRUSTED
      memset(store->storage_bytes[old_index], AL_MAILBOX_POISON_BYTE,
             old_bank->byte_capacity);
#endif
      memset(store->storage_roots[old_index], 0,
             (size_t)old_bank->root_capacity * sizeof(al_owning_bank_root));
      old_bank->used_bytes = 0u;
      old_bank->root_count = 0u;
    }
    al_owning_scratch_slot_release(runtime, scratch_index);
  }
  al_owning_turn_end(runtime);
  return result;
}

static al_mailbox_result al_owning_stage_text_input(
    al_mailbox_runtime *runtime, const uint8_t *utf8, uint32_t byte_count,
    uint32_t payload_bytes, uint32_t extent_bytes,
    al_owning_external_slice *out_input) {
  al_owning_stack_context scanner;
  al_mailbox_result result;
  uint32_t measured_payload;
  uint32_t measured_extent;
  result = al_owning_text_write(utf8, byte_count, payload_bytes, extent_bytes,
                                runtime->owning_text_staging);
  if (result != AL_MAILBOX_OK)
    return result;
  memset(&scanner, 0, sizeof(scanner));
  scanner.abi_version = AL_OWNING_STACK_ABI_VERSION;
  scanner.status = AL_OWNING_STATUS_OK;
  scanner.available_bytes = UINT32_MAX;
  if (al_owning_measure_external_value(
          &scanner, runtime->owning_module->layout,
          runtime->owning_string_type_index, runtime->owning_text_staging,
          extent_bytes, 0u, 0u, &measured_payload, &measured_extent) != 0 ||
      measured_payload != payload_bytes || measured_extent != extent_bytes)
    return AL_MAILBOX_INVALID_TEXT_ENCODING;
  out_input->bytes = runtime->owning_text_staging;
  out_input->extent_bytes = extent_bytes;
  out_input->type_index = runtime->owning_string_type_index;
  al_saturating_add(&runtime->owning_utf8_input_bytes, byte_count);
  al_saturating_add(&runtime->owning_utf16_staging_bytes, extent_bytes);
  return AL_MAILBOX_OK;
}

al_mailbox_result al_mailbox_init_text(al_mailbox_runtime *runtime,
                                       uint32_t mailbox_id,
                                       const uint8_t *utf8,
                                       uint32_t byte_count,
                                       al_mailbox_call_info *call_info) {
  uint32_t current_thread_id = al_mailbox_platform_current_thread_id();
  al_mailbox_result result;
  al_mailbox_slot *slot;
  uint32_t scratch_slot_index;
  uint32_t payload_bytes;
  uint32_t extent_bytes;
  al_external_argument arguments[2] = {
      {utf8, byte_count, 0u},
      {call_info, call_info == NULL ? 0u : sizeof(*call_info), 1u}};
  if (!al_call_info_valid(call_info))
    return AL_MAILBOX_INVALID_ARGUMENT;
  result = al_runtime_access(runtime, current_thread_id, 0u);
  if (result != AL_MAILBOX_OK)
    return result;
  if (runtime->payload_kind != AL_MAILBOX_PAYLOAD_OWNING)
    return AL_MAILBOX_INVALID_MODULE;
  if (!al_runtime_external_arguments_valid(
          runtime, arguments,
          (uint32_t)(sizeof(arguments) / sizeof(arguments[0]))))
    return AL_MAILBOX_INVALID_ARGUMENT;
  result = al_mailbox_at(runtime, mailbox_id, &slot);
  if (result != AL_MAILBOX_OK)
    return result;
  al_call_info_reset(call_info);
  if (slot->initialized != 0u)
    return AL_MAILBOX_ALREADY_INITIALIZED;
  result = al_owning_text_measure(
      utf8, byte_count, runtime->owning_config.text_staging_byte_capacity,
      &payload_bytes, &extent_bytes);
  if (result != AL_MAILBOX_OK)
    return result;
  if (al_owning_scratch_slot_find_free(runtime) == AL_OWNING_NO_SCRATCH_SLOT)
    return AL_MAILBOX_SCRATCH_CAPACITY;
  result = al_owning_text_input_fits_scratch(runtime, 0u, extent_bytes);
  if (result != AL_MAILBOX_OK)
    return result;
  result = al_owning_stage_text_input(runtime, utf8, byte_count,
                                      payload_bytes, extent_bytes,
                                      &runtime->owning_inputs[0]);
  if (result != AL_MAILBOX_OK)
    return result;
  result = al_owning_scratch_slot_checkout(runtime, mailbox_id,
                                           &scratch_slot_index);
  if (result != AL_MAILBOX_OK)
    return result;
  result = al_execute_owning_entry(
      runtime, slot, scratch_slot_index, runtime->owning_entries[0],
      runtime->owning_inputs, 1u, call_info);
  al_owning_scratch_slot_release(runtime, scratch_slot_index);
  if (result != AL_MAILBOX_OK)
    return result;
  al_mailbox_mark_initialized(slot);
  return AL_MAILBOX_OK;
}

al_mailbox_result al_mailbox_begin_text(al_mailbox_runtime *runtime,
                                        uint32_t mailbox_id,
                                        const uint8_t *utf8,
                                        uint32_t byte_count,
                                        al_mailbox_token *out_token,
                                        al_mailbox_call_info *call_info) {
  uint32_t current_thread_id = al_mailbox_platform_current_thread_id();
  al_mailbox_result result;
  al_mailbox_slot *slot;
  const al_owning_byte_bank *active;
  al_owning_attachment *attachment;
  al_mailbox_token token;
  uint32_t scratch_slot_index;
  uint32_t keep_associated;
  uint32_t payload_bytes;
  uint32_t extent_bytes;
  al_external_argument arguments[3] = {
      {utf8, byte_count, 0u},
      {out_token, out_token == NULL ? 0u : sizeof(*out_token), 1u},
      {call_info, call_info == NULL ? 0u : sizeof(*call_info), 1u}};
  if (out_token == NULL || !al_pointer_aligned(out_token, 8u) ||
      !al_call_info_valid(call_info))
    return AL_MAILBOX_INVALID_ARGUMENT;
  result = al_runtime_access(runtime, current_thread_id, 0u);
  if (result != AL_MAILBOX_OK)
    return result;
  if (runtime->payload_kind != AL_MAILBOX_PAYLOAD_OWNING)
    return AL_MAILBOX_INVALID_MODULE;
  if (!al_runtime_external_arguments_valid(
          runtime, arguments,
          (uint32_t)(sizeof(arguments) / sizeof(arguments[0]))))
    return AL_MAILBOX_INVALID_ARGUMENT;
  result = al_mailbox_at(runtime, mailbox_id, &slot);
  if (result != AL_MAILBOX_OK)
    return result;
  al_call_info_reset(call_info);
  result = al_mailbox_begin_admission(runtime, slot);
  if (result != AL_MAILBOX_OK)
    return result;
  attachment = &runtime->owning_attachments[mailbox_id];
  if (attachment->scratch_slot_index != AL_OWNING_NO_SCRATCH_SLOT ||
      attachment->root_count != 0u)
    return AL_MAILBOX_INVALID_REFERENCE;
  result = al_owning_text_measure(
      utf8, byte_count, runtime->owning_config.text_staging_byte_capacity,
      &payload_bytes, &extent_bytes);
  if (result != AL_MAILBOX_OK)
    return result;
  keep_associated = runtime->owning_config.suspension_policy ==
                    AL_MAILBOX_OWNING_POLICY_KEEP_ASSOCIATED;
  if (keep_associated != 0u &&
      al_owning_scratch_slot_find_free(runtime) == AL_OWNING_NO_SCRATCH_SLOT)
    return AL_MAILBOX_SCRATCH_CAPACITY;
  active = al_owning_byte_store_active(&slot->payload.owning);
  if (active == NULL || active->root_count != 1u)
    return AL_MAILBOX_INVALID_REFERENCE;
  result = al_owning_bank_root_input(
      runtime, active, 0u, runtime->owning_state_type_index,
      &runtime->owning_inputs[0]);
  if (result != AL_MAILBOX_OK)
    return result;
  result = al_owning_text_input_fits_scratch(runtime, 1u, extent_bytes);
  if (result != AL_MAILBOX_OK)
    return result;
  result = al_owning_stage_text_input(runtime, utf8, byte_count,
                                      payload_bytes, extent_bytes,
                                      &runtime->owning_inputs[1]);
  if (result != AL_MAILBOX_OK)
    return result;
  result = al_owning_scratch_slot_checkout(runtime, mailbox_id,
                                           &scratch_slot_index);
  if (result != AL_MAILBOX_OK)
    return result;
  al_make_token(&token, runtime->runtime_instance_id, mailbox_id,
                runtime->next_token_sequence);
  result = al_execute_owning_entry(
      runtime, slot, scratch_slot_index, runtime->owning_entries[1],
      runtime->owning_inputs, keep_associated == 0u ? 1u : 0u, call_info);
  if (result != AL_MAILBOX_OK) {
    al_owning_scratch_slot_release(runtime, scratch_slot_index);
    return result;
  }
  if (keep_associated != 0u) {
    al_owning_scratch_slot *scratch_slot =
        &runtime->owning_scratch_slots[scratch_slot_index];
    attachment->scratch_slot_index = scratch_slot_index;
    attachment->protected_cursor_bytes = scratch_slot->context.cursor_bytes;
    attachment->root_count = AL_MAILBOX_MAX_ROOTS;
    attachment->reserved = 0u;
    memcpy(attachment->roots, runtime->owning_outputs,
           sizeof(attachment->roots));
    al_owning_scratch_slot_attach(runtime, scratch_slot_index);
  } else {
    al_owning_scratch_slot_release(runtime, scratch_slot_index);
  }
  al_mailbox_commit_begin(runtime, slot, &token, out_token);
  return AL_MAILBOX_OK;
}

al_mailbox_result al_mailbox_resume_text(
    al_mailbox_runtime *runtime, uint32_t mailbox_id,
    const al_mailbox_token *token, const uint8_t *utf8, uint32_t byte_count,
    al_mailbox_call_info *call_info) {
  uint32_t current_thread_id = al_mailbox_platform_current_thread_id();
  al_mailbox_result result;
  al_mailbox_slot *slot;
  const al_owning_byte_bank *active;
  al_mailbox_token completed_token;
  uint32_t scratch_slot_index;
  uint32_t payload_bytes;
  uint32_t extent_bytes;
  al_external_argument arguments[3] = {
      {token, token == NULL ? 0u : sizeof(*token), 0u},
      {utf8, byte_count, 0u},
      {call_info, call_info == NULL ? 0u : sizeof(*call_info), 1u}};
  if (token == NULL || !al_pointer_aligned(token, 8u) ||
      !al_call_info_valid(call_info))
    return AL_MAILBOX_INVALID_ARGUMENT;
  result = al_runtime_access(runtime, current_thread_id, 0u);
  if (result != AL_MAILBOX_OK)
    return result;
  if (runtime->payload_kind != AL_MAILBOX_PAYLOAD_OWNING)
    return AL_MAILBOX_INVALID_MODULE;
  if (!al_runtime_external_arguments_valid(
          runtime, arguments,
          (uint32_t)(sizeof(arguments) / sizeof(arguments[0]))))
    return AL_MAILBOX_INVALID_ARGUMENT;
  result = al_mailbox_at(runtime, mailbox_id, &slot);
  if (result != AL_MAILBOX_OK)
    return result;
  al_call_info_reset(call_info);
  if (slot->initialized == 0u)
    return AL_MAILBOX_NOT_INITIALIZED;
  result = al_validate_token(runtime, mailbox_id, slot, token);
  if (result != AL_MAILBOX_OK)
    return result;
  result = al_owning_text_measure(
      utf8, byte_count, runtime->owning_config.text_staging_byte_capacity,
      &payload_bytes, &extent_bytes);
  if (result != AL_MAILBOX_OK)
    return result;
  if (runtime->owning_config.suspension_policy ==
      AL_MAILBOX_OWNING_POLICY_KEEP_ASSOCIATED) {
    al_owning_attachment *attachment = &runtime->owning_attachments[mailbox_id];
    uint32_t attached_index = attachment->scratch_slot_index;
    al_owning_scratch_slot *scratch_slot;
    if (attachment->root_count != AL_MAILBOX_MAX_ROOTS ||
        attached_index >= runtime->owning_config.scratch_slot_capacity)
      return AL_MAILBOX_STALE_TOKEN;
    scratch_slot = &runtime->owning_scratch_slots[attached_index];
    if (scratch_slot->state != AL_OWNING_SCRATCH_ATTACHED ||
        scratch_slot->owner_mailbox_id != mailbox_id ||
        scratch_slot->context.cursor_bytes !=
            attachment->protected_cursor_bytes)
      return AL_MAILBOX_STALE_TOKEN;
    if ((uint64_t)attachment->protected_cursor_bytes + extent_bytes >
        runtime->owning_config.scratch_byte_capacity)
      return AL_MAILBOX_SCRATCH_CAPACITY;
    result = al_owning_stage_text_input(runtime, utf8, byte_count,
                                        payload_bytes, extent_bytes,
                                        &runtime->owning_inputs[2]);
    if (result != AL_MAILBOX_OK)
      return result;
    completed_token = slot->pending_token;
    result = al_execute_owning_associated_resume(
        runtime, slot, mailbox_id, &runtime->owning_inputs[2], call_info);
    if (result != AL_MAILBOX_OK)
      return result;
    al_mailbox_commit_resume(slot, &completed_token);
    return AL_MAILBOX_OK;
  }
  active = al_owning_byte_store_active(&slot->payload.owning);
  if (active == NULL || active->root_count != 2u)
    return AL_MAILBOX_STALE_TOKEN;
  result = al_owning_bank_root_input(
      runtime, active, 0u, runtime->owning_state_type_index,
      &runtime->owning_inputs[0]);
  if (result != AL_MAILBOX_OK)
    return result;
  result = al_owning_bank_root_input(
      runtime, active, 1u, runtime->owning_continuation_type_index,
      &runtime->owning_inputs[1]);
  if (result != AL_MAILBOX_OK)
    return result;
  result = al_owning_text_input_fits_scratch(runtime, 2u, extent_bytes);
  if (result != AL_MAILBOX_OK)
    return result;
  result = al_owning_stage_text_input(runtime, utf8, byte_count,
                                      payload_bytes, extent_bytes,
                                      &runtime->owning_inputs[2]);
  if (result != AL_MAILBOX_OK)
    return result;
  result = al_owning_scratch_slot_checkout(runtime, mailbox_id,
                                           &scratch_slot_index);
  if (result != AL_MAILBOX_OK)
    return result;
  completed_token = slot->pending_token;
  result = al_execute_owning_entry(
      runtime, slot, scratch_slot_index, runtime->owning_entries[2],
      runtime->owning_inputs, 1u, call_info);
  al_owning_scratch_slot_release(runtime, scratch_slot_index);
  if (result != AL_MAILBOX_OK)
    return result;
  al_mailbox_commit_resume(slot, &completed_token);
  return AL_MAILBOX_OK;
}

al_mailbox_result al_mailbox_cancel_text(al_mailbox_runtime *runtime,
                                         uint32_t mailbox_id,
                                         const al_mailbox_token *token) {
  uint32_t current_thread_id = al_mailbox_platform_current_thread_id();
  al_mailbox_result result;
  al_mailbox_slot *slot;
  al_mailbox_token completed_token;
  const al_external_argument argument = {
      token, token == NULL ? 0u : sizeof(*token), 0u};

  if (token == NULL || !al_pointer_aligned(token, 8u))
    return AL_MAILBOX_INVALID_ARGUMENT;
  result = al_runtime_access(runtime, current_thread_id, 0u);
  if (result != AL_MAILBOX_OK)
    return result;
  if (runtime->payload_kind != AL_MAILBOX_PAYLOAD_OWNING)
    return AL_MAILBOX_INVALID_MODULE;
  if (!al_runtime_external_arguments_valid(runtime, &argument, 1u))
    return AL_MAILBOX_INVALID_ARGUMENT;
  result = al_mailbox_at(runtime, mailbox_id, &slot);
  if (result != AL_MAILBOX_OK)
    return result;
  if (slot->initialized == 0u)
    return AL_MAILBOX_NOT_INITIALIZED;
  result = al_validate_token(runtime, mailbox_id, slot, token);
  if (result != AL_MAILBOX_OK)
    return result;
  if (runtime->busy != 0u)
    return AL_MAILBOX_BUSY;

  completed_token = slot->pending_token;
  if (runtime->owning_config.suspension_policy ==
      AL_MAILBOX_OWNING_POLICY_RETURN) {
    const al_owning_byte_bank *active =
        al_owning_byte_store_active(&slot->payload.owning);
    al_owning_external_slice state_input;
    al_owning_external_slice continuation_input;
    al_owning_bank_result bank_result;
    const al_owning_attachment *attachment =
        &runtime->owning_attachments[mailbox_id];

    if (attachment->scratch_slot_index != AL_OWNING_NO_SCRATCH_SLOT ||
        attachment->root_count != 0u || active == NULL ||
        active->root_count != 2u)
      return AL_MAILBOX_STALE_TOKEN;
    result = al_owning_bank_root_input(
        runtime, active, 0u, runtime->owning_state_type_index, &state_input);
    if (result != AL_MAILBOX_OK)
      return result;
    result = al_owning_bank_root_input(
        runtime, active, 1u, runtime->owning_continuation_type_index,
        &continuation_input);
    if (result != AL_MAILBOX_OK)
      return result;
    bank_result =
        al_owning_byte_store_trim_last_root(&slot->payload.owning);
    result = al_map_owning_bank_result(bank_result);
    if (result != AL_MAILBOX_OK)
      return result;
  } else {
    al_owning_attachment *attachment =
        &runtime->owning_attachments[mailbox_id];
    uint32_t scratch_index = attachment->scratch_slot_index;
    al_owning_scratch_slot *scratch_slot;
    al_owning_stack_context *context;
    al_owning_stack_context saved_context;
    al_owning_byte_store *store = &slot->payload.owning;
    al_owning_bank_result bank_result;
    uint64_t publication_bytes;

    if (attachment->root_count != AL_MAILBOX_MAX_ROOTS ||
        attachment->reserved != 0u ||
        scratch_index >= runtime->owning_config.scratch_slot_capacity)
      return AL_MAILBOX_STALE_TOKEN;
    scratch_slot = &runtime->owning_scratch_slots[scratch_index];
    if (scratch_slot->state != AL_OWNING_SCRATCH_ATTACHED ||
        scratch_slot->owner_mailbox_id != mailbox_id)
      return AL_MAILBOX_STALE_TOKEN;
    context = &scratch_slot->context;
    saved_context = *context;
    if (!al_owning_context_storage_valid(runtime, scratch_slot) ||
        context->cursor_bytes != attachment->protected_cursor_bytes ||
        attachment->protected_cursor_bytes == 0u ||
        context->status != AL_OWNING_STATUS_OK || context->call_depth != 0u) {
      *context = saved_context;
      return AL_MAILBOX_INVALID_REFERENCE;
    }
    result = al_owning_validate_attached_roots(runtime, scratch_slot,
                                               attachment);
    if (result != AL_MAILBOX_OK || context->status != AL_OWNING_STATUS_OK) {
      *context = saved_context;
      return result == AL_MAILBOX_OK ? AL_MAILBOX_INVALID_REFERENCE : result;
    }
    bank_result = al_owning_byte_store_begin(store);
    if (bank_result != AL_OWNING_BANK_OK) {
      *context = saved_context;
      return al_map_owning_bank_result(bank_result);
    }
    bank_result = al_owning_byte_store_stage_stack_values(
        store, context, runtime->owning_module->layout, attachment->roots,
        1u, 0u);
    if (bank_result == AL_OWNING_BANK_OK)
      bank_result = al_owning_byte_store_commit(store);
    if (bank_result != AL_OWNING_BANK_OK) {
      if (store->transaction_open != 0u)
        (void)al_owning_byte_store_abort(store);
      *context = saved_context;
      return al_map_owning_bank_result(bank_result);
    }

    publication_bytes =
        al_owning_byte_store_last_committed_copy_bytes(store);
    al_saturating_add(&runtime->owning_publication_copy_bytes,
                      publication_bytes);
    {
      uint32_t old_index = 1u - store->active_index;
      al_owning_byte_bank *old_bank = &store->banks[old_index];
#if !AL_MAILBOX_RUNTIME_TRUSTED
      memset(store->storage_bytes[old_index], AL_MAILBOX_POISON_BYTE,
             old_bank->byte_capacity);
#endif
      memset(store->storage_roots[old_index], 0,
             (size_t)old_bank->root_capacity * sizeof(al_owning_bank_root));
      old_bank->used_bytes = 0u;
      old_bank->root_count = 0u;
    }
    al_owning_scratch_slot_release(runtime, scratch_index);
  }
  al_mailbox_commit_resume(slot, &completed_token);
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
  if (runtime->payload_kind != AL_MAILBOX_PAYLOAD_ABI3) {
    return AL_MAILBOX_INVALID_MODULE;
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
  output_bank = &slot->payload.graph.banks[0];
  result = al_execute_entry(runtime, slot, NULL, output_bank,
                            runtime->entries[0], inputs, limits, call_info);
  if (result != AL_MAILBOX_OK) {
    return result;
  }
  slot->payload.graph.active_bank = 0u;
  al_mailbox_mark_initialized(slot);
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
  if (runtime->payload_kind != AL_MAILBOX_PAYLOAD_ABI3) {
    return AL_MAILBOX_INVALID_MODULE;
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
  result = al_mailbox_begin_admission(runtime, slot);
  if (result != AL_MAILBOX_OK) {
    return result;
  }
  result =
      al_effective_limits(runtime, limits, &(uint32_t){0u}, &(uint32_t){0u});
  if (result != AL_MAILBOX_OK) {
    return result;
  }
  active = &slot->payload.graph.banks[slot->payload.graph.active_bank];
  if (active->root_count != 1u ||
      active->root_type_ids[0] != runtime->state_type_id) {
    return AL_MAILBOX_INVALID_REFERENCE;
  }
  inputs[0] = active->roots[0];
  inputs[1] = message;
  output_bank =
      &slot->payload.graph.banks[1u - slot->payload.graph.active_bank];
  result = al_execute_entry(runtime, slot, active, output_bank,
                            runtime->entries[1], inputs, limits, call_info);
  if (result != AL_MAILBOX_OK) {
    return result;
  }

  al_make_token(&token, runtime->runtime_instance_id, mailbox_id,
                runtime->next_token_sequence);
  slot->payload.graph.active_bank = 1u - slot->payload.graph.active_bank;
  al_clear_bank(runtime, active, 1u);
  al_mailbox_commit_begin(runtime, slot, &token, out_token);
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
  if (runtime->payload_kind != AL_MAILBOX_PAYLOAD_ABI3) {
    return AL_MAILBOX_INVALID_MODULE;
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
  active = &slot->payload.graph.banks[slot->payload.graph.active_bank];
  if (slot->pending == 0u || active->root_count != 2u ||
      active->root_type_ids[0] != runtime->state_type_id ||
      active->root_type_ids[1] != runtime->continuation_type_id) {
    return AL_MAILBOX_STALE_TOKEN;
  }
  inputs[0] = active->roots[0];
  inputs[1] = active->roots[1];
  inputs[2] = message;
  output_bank =
      &slot->payload.graph.banks[1u - slot->payload.graph.active_bank];
  completed_token = slot->pending_token;
  result = al_execute_entry(runtime, slot, active, output_bank,
                            runtime->entries[2], inputs, limits, call_info);
  if (result != AL_MAILBOX_OK) {
    return result;
  }
  slot->payload.graph.active_bank = 1u - slot->payload.graph.active_bank;
  al_clear_bank(runtime, active, 1u);
  al_mailbox_commit_resume(slot, &completed_token);
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
  if (runtime->busy != 0u) {
    return AL_MAILBOX_BUSY;
  }
  if (runtime->disposed != 0u) {
    return AL_MAILBOX_OK;
  }
  for (mailbox_id = 0u; mailbox_id < runtime->config.mailbox_capacity;
       ++mailbox_id) {
    al_mailbox_slot *slot = &runtime->mailboxes[mailbox_id];
    if (runtime->payload_kind == AL_MAILBOX_PAYLOAD_OWNING) {
      al_owning_byte_store *store = &slot->payload.owning;
      uint32_t bank_index;
      for (bank_index = 0u; bank_index < 2u; ++bank_index) {
        al_owning_byte_bank *bank = &store->banks[bank_index];
#if !AL_MAILBOX_RUNTIME_TRUSTED
        memset(store->storage_bytes[bank_index], AL_MAILBOX_POISON_BYTE,
               bank->byte_capacity);
#endif
        memset(store->storage_roots[bank_index], 0,
               (size_t)bank->root_capacity * sizeof(al_owning_bank_root));
        bank->used_bytes = 0u;
        bank->root_count = 0u;
      }
      store->active_index = 0u;
      store->transaction_open = 0u;
      store->transaction_ready = 0u;
      store->last_committed_copy_bytes = 0u;
    } else {
      al_clear_bank(runtime, &slot->payload.graph.banks[0], 1u);
      al_clear_bank(runtime, &slot->payload.graph.banks[1], 1u);
      slot->payload.graph.active_bank = 0u;
    }
    slot->initialized = 0u;
    slot->pending = 0u;
      slot->last_completed_valid = 0u;
    memset(&slot->pending_token, 0, sizeof(slot->pending_token));
    memset(&slot->last_completed_token, 0, sizeof(slot->last_completed_token));
  }
  if (runtime->payload_kind == AL_MAILBOX_PAYLOAD_OWNING) {
    uint32_t scratch_index;
    for (scratch_index = 0u;
         scratch_index < runtime->owning_config.scratch_slot_capacity;
         ++scratch_index) {
      if (runtime->owning_scratch_slots[scratch_index].state !=
          AL_OWNING_SCRATCH_FREE)
        al_owning_scratch_slot_release(runtime, scratch_index);
    }
#if !AL_MAILBOX_RUNTIME_TRUSTED
    memset(runtime->owning_text_staging, AL_MAILBOX_POISON_BYTE,
           runtime->owning_config.text_staging_byte_capacity);
#endif
  } else {
    al_reset_scratch(runtime);
    al_reset_context(runtime);
  }
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
  if (runtime->payload_kind != AL_MAILBOX_PAYLOAD_ABI3) {
    return AL_MAILBOX_INVALID_MODULE;
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
      const al_arena *owner =
          &slot->payload.graph.banks[slot->payload.graph.active_bank].owner;
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

al_mailbox_result al_mailbox_get_owning_stats(
    al_mailbox_runtime *runtime, al_mailbox_owning_stats *stats) {
  uint32_t current_thread_id = al_mailbox_platform_current_thread_id();
  al_mailbox_result result;
  uint32_t mailbox_id;
  uint32_t initialized = 0u;
  uint32_t pending = 0u;
  uint32_t pinned_slots = 0u;
  uint32_t scratch_index;
  uint64_t live_bytes = 0u;
  uint64_t live_roots = 0u;
  if (stats == NULL || !al_pointer_aligned(stats, 8u))
    return AL_MAILBOX_INVALID_ARGUMENT;
  result = al_runtime_access(runtime, current_thread_id, 1u);
  if (result != AL_MAILBOX_OK)
    return result;
  if (runtime->payload_kind != AL_MAILBOX_PAYLOAD_OWNING)
    return AL_MAILBOX_INVALID_MODULE;
  {
    const al_external_argument argument = {stats, sizeof(*stats), 1u};
    if (!al_runtime_external_arguments_valid(runtime, &argument, 1u))
      return AL_MAILBOX_INVALID_ARGUMENT;
  }
  for (mailbox_id = 0u; mailbox_id < runtime->config.mailbox_capacity;
       ++mailbox_id) {
    const al_mailbox_slot *slot = &runtime->mailboxes[mailbox_id];
    if (slot->initialized != 0u) {
      const al_owning_byte_bank *bank =
          al_owning_byte_store_active(&slot->payload.owning);
      ++initialized;
      if (bank != NULL) {
        al_saturating_add(&live_bytes, bank->used_bytes);
        al_saturating_add(&live_roots, bank->root_count);
      }
    }
    if (slot->pending != 0u)
      ++pending;
  }
  for (scratch_index = 0u;
       scratch_index < runtime->owning_config.scratch_slot_capacity;
       ++scratch_index) {
    if (runtime->owning_scratch_slots[scratch_index].state ==
        AL_OWNING_SCRATCH_ATTACHED)
      ++pinned_slots;
  }
  stats->control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  stats->struct_size = (uint32_t)sizeof(*stats);
  stats->mailbox_capacity = runtime->config.mailbox_capacity;
  stats->initialized_mailboxes = initialized;
  stats->pending_mailboxes = pending;
  stats->reserved0 = 0u;
  stats->storage_reserved_bytes = runtime->storage_reserved_bytes;
  stats->retained_reserved_bytes = runtime->retained_reserved_bytes;
  stats->scratch_reserved_bytes = runtime->scratch_reserved_bytes;
  stats->text_staging_reserved_bytes =
      runtime->owning_config.text_staging_byte_capacity;
  stats->live_retained_bytes = live_bytes;
  stats->live_retained_roots = live_roots;
  stats->scratch_high_water_bytes = runtime->scratch_high_water_bytes;
  stats->utf8_input_bytes = runtime->owning_utf8_input_bytes;
  stats->utf16_staging_bytes = runtime->owning_utf16_staging_bytes;
  stats->input_import_bytes = runtime->owning_input_import_bytes;
  stats->publication_copy_bytes = runtime->owning_publication_copy_bytes;
  stats->deep_copy_bytes = runtime->owning_deep_copy_bytes;
  stats->move_bytes = runtime->owning_move_bytes;
  stats->returned_output_descriptors =
      runtime->owning_returned_output_descriptors;
  stats->turn_reset_bytes = runtime->owning_turn_reset_bytes;
  stats->handler_invocations = runtime->handler_invocations;
  stats->handler_failures = runtime->handler_failures;
  stats->scratch_lease_acquisitions = runtime->scratch_lease_acquisitions;
  stats->scratch_lease_returns = runtime->scratch_lease_returns;
  stats->outstanding_scratch_leases = runtime->outstanding_scratch_leases;
  stats->reserved = 0u;
  stats->scratch_slot_capacity =
      runtime->owning_config.scratch_slot_capacity;
  stats->pinned_scratch_slots = pinned_slots;
  stats->suspension_policy = runtime->owning_config.suspension_policy;
  stats->reserved1 = 0u;
  stats->pinned_scratch_bytes =
      (uint64_t)pinned_slots * runtime->owning_config.scratch_byte_capacity;
  stats->begin_publication_copy_bytes =
      runtime->owning_begin_publication_copy_bytes;
  stats->resume_root_import_bytes =
      runtime->owning_resume_root_import_bytes;
  return AL_MAILBOX_OK;
}

al_mailbox_result al_mailbox_get_reset_stats(
    al_mailbox_runtime *runtime, al_mailbox_reset_stats *stats) {
  uint32_t current_thread_id = al_mailbox_platform_current_thread_id();
  al_mailbox_result result;
  if (stats == NULL || !al_pointer_aligned(stats, 8u))
    return AL_MAILBOX_INVALID_ARGUMENT;
  result = al_runtime_access(runtime, current_thread_id, 1u);
  if (result != AL_MAILBOX_OK)
    return result;
  if (runtime->payload_kind != AL_MAILBOX_PAYLOAD_OWNING)
    return AL_MAILBOX_INVALID_MODULE;
  {
    const al_external_argument argument = {stats, sizeof(*stats), 1u};
    if (!al_runtime_external_arguments_valid(runtime, &argument, 1u))
      return AL_MAILBOX_INVALID_ARGUMENT;
  }
  stats->control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  stats->struct_size = (uint32_t)sizeof(*stats);
  stats->reset_profile = AL_MAILBOX_RESET_PROFILE_VALUE;
  stats->reserved = 0u;
  stats->full_capacity_payload_write_bytes_requested =
      runtime->owning_reset_full_capacity_payload_write_bytes_requested;
  stats->live_prefix_payload_write_bytes_requested =
      runtime->owning_reset_live_prefix_payload_write_bytes_requested;
  stats->bitmap_store_operations =
      runtime->owning_reset_bitmap_store_operations;
  stats->turn_reset_cursor_extent_bytes = runtime->owning_turn_reset_bytes;
  return AL_MAILBOX_OK;
}

al_mailbox_result al_mailbox_get_owning_state_view(
    al_mailbox_runtime *runtime, uint32_t mailbox_id,
    al_mailbox_owning_state_view *view) {
  uint32_t current_thread_id = al_mailbox_platform_current_thread_id();
  al_mailbox_slot *slot;
  al_mailbox_result result;
  if (view == NULL || !al_pointer_aligned(view, 8u))
    return AL_MAILBOX_INVALID_ARGUMENT;
  result = al_runtime_access(runtime, current_thread_id, 0u);
  if (result != AL_MAILBOX_OK)
    return result;
  if (runtime->payload_kind != AL_MAILBOX_PAYLOAD_OWNING)
    return AL_MAILBOX_INVALID_MODULE;
  {
    const al_external_argument argument = {view, sizeof(*view), 1u};
    if (!al_runtime_external_arguments_valid(runtime, &argument, 1u))
      return AL_MAILBOX_INVALID_ARGUMENT;
  }
  result = al_mailbox_at(runtime, mailbox_id, &slot);
  if (result != AL_MAILBOX_OK)
    return result;
  if (slot->initialized == 0u)
    return AL_MAILBOX_NOT_INITIALIZED;
  view->control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  view->struct_size = (uint32_t)sizeof(*view);
  view->bank = al_owning_byte_store_active(&slot->payload.owning);
  view->state_type_id = runtime->state_type_id;
  view->continuation_type_id = runtime->continuation_type_id;
  view->pending = slot->pending;
  view->reserved = 0u;
  view->associated_context = NULL;
  view->associated_roots = NULL;
  view->associated_root_count = 0u;
  view->scratch_slot_index = AL_OWNING_NO_SCRATCH_SLOT;
  if (runtime->owning_config.suspension_policy ==
          AL_MAILBOX_OWNING_POLICY_KEEP_ASSOCIATED &&
      slot->pending != 0u) {
    const al_owning_attachment *attachment =
        &runtime->owning_attachments[mailbox_id];
    if (attachment->scratch_slot_index <
            runtime->owning_config.scratch_slot_capacity &&
        attachment->root_count == AL_MAILBOX_MAX_ROOTS) {
      view->associated_context =
          &runtime->owning_scratch_slots[attachment->scratch_slot_index].context;
      view->associated_roots = attachment->roots;
      view->associated_root_count = attachment->root_count;
      view->scratch_slot_index = attachment->scratch_slot_index;
    }
  }
  return view->bank == NULL ? AL_MAILBOX_INVALID_REFERENCE : AL_MAILBOX_OK;
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
  if (runtime->payload_kind != AL_MAILBOX_PAYLOAD_ABI3) {
    return AL_MAILBOX_INVALID_MODULE;
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
  active = &slot->payload.graph.banks[slot->payload.graph.active_bank];
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
