#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <inttypes.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "mailbox_runtime.h"

enum {
  ROOT_BYTES_MAX = 64u,
  RAW_BYTES_MAX = 512u,
  SCRATCH_BYTES = 16384u,
  STAGING_BYTES = 512u,
  DIRECT_BYTES = 2048u
};

typedef struct named_check {
  const char *name;
  int passed;
} named_check;

typedef struct root_snapshot {
  uint32_t type_id;
  uint32_t offset_bytes;
  uint32_t extent_bytes;
  uint32_t payload_bytes;
  uint32_t owner_end_bytes;
  char serialized_hex[ROOT_BYTES_MAX * 2u + 1u];
} root_snapshot;

typedef struct bank_snapshot {
  uint32_t pending;
  uint32_t root_count;
  uint32_t used_bytes;
  root_snapshot roots[AL_OWNING_MAILBOX_MAX_OUTPUTS];
} bank_snapshot;

typedef struct raw_pending_snapshot {
  uintptr_t context_address;
  uint32_t pending;
  uint32_t bank_root_count;
  uint32_t bank_used_bytes;
  al_owning_bank_root bank_roots[AL_OWNING_MAILBOX_MAX_OUTPUTS];
  uint8_t bank_bytes[RAW_BYTES_MAX];
  uint32_t associated_root_count;
  uint32_t associated_cursor_bytes;
  al_owning_bank_stack_slice associated_roots[AL_OWNING_MAILBOX_MAX_OUTPUTS];
  uint8_t associated_prefix[RAW_BYTES_MAX];
} raw_pending_snapshot;

typedef struct runtime_fixture {
  al_mailbox_runtime *runtime;
  void *storage;
  uint64_t storage_bytes;
  al_mailbox_owning_config config;
  al_mailbox_owning_storage_requirements requirements;
} runtime_fixture;

typedef struct direct_fixture {
  al_owning_stack_context context;
  _Alignas(8) uint8_t stack[DIRECT_BYTES];
  uint8_t initialized[DIRECT_BYTES / 8u];
  uint8_t poisoned[DIRECT_BYTES / 8u];
} direct_fixture;

typedef struct call_evidence {
  uint32_t result;
  uint32_t error_id;
  uint32_t steps_consumed;
} call_evidence;

typedef struct profile_stats {
  uint64_t utf8_input_bytes;
  uint64_t utf16_staging_bytes;
  uint64_t input_import_bytes;
  uint64_t publication_copy_bytes;
  uint64_t begin_publication_copy_bytes;
  uint64_t resume_root_import_bytes;
  uint64_t deep_copy_bytes;
  uint64_t move_bytes;
  uint64_t returned_output_descriptors;
  uint64_t handler_invocations;
  uint64_t handler_failures;
  uint64_t turn_reset_bytes;
  uint32_t pending_mailboxes;
  uint64_t live_retained_roots;
  uint32_t outstanding_scratch_leases;
  uint32_t pinned_scratch_slots;
  uint64_t pinned_scratch_bytes;
} profile_stats;

typedef struct direct_call {
  uint32_t callback_result;
  uint32_t context_status;
  uint32_t error_id;
  uint32_t steps_consumed;
  uint32_t output_contract;
  bank_snapshot outputs;
} direct_call;

typedef struct direct_evidence {
  direct_call valid_begin;
  direct_call negative_id;
  direct_call overflow_id;
  direct_call some_empty_label;
  direct_call result_error_empty_label;
  direct_call malformed_option_tag;
  direct_call malformed_result_tag;
  direct_call wrong_input_index;
} direct_evidence;

typedef struct policy_evidence {
  bank_snapshot initialized;
  bank_snapshot pending_bank;
  bank_snapshot pending_associated;
  bank_snapshot before_empty_resume;
  bank_snapshot after_empty_resume;
  bank_snapshot after_fail_resume;
  bank_snapshot final;
  raw_pending_snapshot raw_pending;
  raw_pending_snapshot raw_after_empty;
  raw_pending_snapshot raw_after_fail;
  al_mailbox_token token_before;
  al_mailbox_token token_after_empty;
  al_mailbox_token token_after_fail;
  uint32_t empty_resume_preserved;
  uint32_t fail_resume_preserved;
  call_evidence initialize_call;
  call_evidence begin_call;
  call_evidence empty_resume_call;
  call_evidence fail_resume_call;
  call_evidence retry_resume_call;
  profile_stats stats;
} policy_evidence;

static named_check checks[13];
static size_t check_count;
static unsigned int failure_count;
static const al_owning_mailbox_module *loaded_module;
static uint32_t state_index;
static uint32_t continuation_index;
static uint32_t string_index;
static uint32_t option_string_index;
static uint32_t option_id_index;
static uint32_t result_index;
static uint32_t positive_id_index;
static uint32_t non_empty_string_index;
static uint8_t fixture_initialized_state[48];

static const char *const check_names[] = {
    "refined layout indexes, nominal IDs, entry signatures, and ABI match the frozen module contract",
    "direct generated begin accepts the independently serialized valid State and returns State plus Continuation",
    "direct negative and active Option and Result String refinements fail with invalid output descriptors",
    "direct Int64.MinValue reaches checked division and fails with invalid output descriptors",
    "malformed nested Option and Result tags fail value scanning after invalidating outputs",
    "wrong input type index fails boundary preflight and preserves output sentinels",
    "RETURN and KEEP_ASSOCIATED execute init, begin, failed resumes, and retry through text APIs",
    "empty refinement failure preserves logical roots, raw roots, and the token under both policies",
    "runtime divide failure preserves logical roots, raw roots, and the same token under both policies",
    "retry completes with no pending mailbox or retained scratch lease",
    "native lifecycle emitted independent step and raw statistics for both policies",
    "all controller raw-byte, copy, publication, import, and descriptor metrics are present",
    "stack ABI remains 1 and the owning layout ABI remains 3"};

static void record_check(size_t index, int passed) {
  if (index >= sizeof(check_names) / sizeof(check_names[0])) {
    ++failure_count;
    return;
  }
  checks[index].name = check_names[index];
  checks[index].passed = passed != 0;
  check_count = index + 1u;
  if (!passed)
    ++failure_count;
}

static void write_u32_le(uint8_t *bytes, uint32_t value) {
  bytes[0] = (uint8_t)value;
  bytes[1] = (uint8_t)(value >> 8u);
  bytes[2] = (uint8_t)(value >> 16u);
  bytes[3] = (uint8_t)(value >> 24u);
}

static void write_u64_le(uint8_t *bytes, uint64_t value) {
  uint32_t index;
  for (index = 0u; index < 8u; ++index)
    bytes[index] = (uint8_t)(value >> (index * 8u));
}

static void hex_encode(const uint8_t *bytes, uint32_t byte_count,
                       char *output, size_t output_capacity) {
  static const char digits[] = "0123456789abcdef";
  uint32_t index;
  if (output_capacity < (size_t)byte_count * 2u + 1u) {
    if (output_capacity != 0u)
      output[0] = '\0';
    return;
  }
  for (index = 0u; index < byte_count; ++index) {
    output[index * 2u] = digits[bytes[index] >> 4u];
    output[index * 2u + 1u] = digits[bytes[index] & 15u];
  }
  output[byte_count * 2u] = '\0';
}

static void initialize_call_info(al_mailbox_call_info *info) {
  memset(info, 0, sizeof(*info));
  info->control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  info->struct_size = (uint32_t)sizeof(*info);
  info->error_metadata_id = -1;
}

static int get_view(al_mailbox_runtime *runtime, uint32_t mailbox_id,
                    al_mailbox_owning_state_view *view) {
  memset(view, 0, sizeof(*view));
  view->control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  view->struct_size = (uint32_t)sizeof(*view);
  return al_mailbox_get_owning_state_view(runtime, mailbox_id, view) ==
         AL_MAILBOX_OK;
}

static int capture_bank(al_mailbox_runtime *runtime, uint32_t mailbox_id,
                        bank_snapshot *snapshot) {
  al_mailbox_owning_state_view view;
  uint32_t index;
  uint32_t next_offset = 0u;
  if (snapshot == NULL || !get_view(runtime, mailbox_id, &view) ||
      view.bank == NULL || view.bank->bytes == NULL ||
      view.bank->root_count > AL_OWNING_MAILBOX_MAX_OUTPUTS ||
      view.bank->used_bytes > RAW_BYTES_MAX)
    return 0;
  memset(snapshot, 0, sizeof(*snapshot));
  snapshot->pending = view.pending;
  snapshot->root_count = view.bank->root_count;
  snapshot->used_bytes = view.bank->used_bytes;
  for (index = 0u; index < view.bank->root_count; ++index) {
    const al_owning_bank_root *root = &view.bank->roots[index];
    root_snapshot *target = &snapshot->roots[index];
    if (root->offset_bytes != next_offset ||
        root->owner_end_bytes != root->offset_bytes + root->extent_bytes ||
        root->owner_end_bytes > view.bank->used_bytes ||
        root->extent_bytes == 0u || root->extent_bytes > ROOT_BYTES_MAX)
      return 0;
    target->type_id = root->type_id;
    target->offset_bytes = root->offset_bytes;
    target->extent_bytes = root->extent_bytes;
    target->payload_bytes = root->payload_bytes;
    target->owner_end_bytes = root->owner_end_bytes;
    hex_encode(view.bank->bytes + root->offset_bytes, root->extent_bytes,
               target->serialized_hex, sizeof(target->serialized_hex));
    next_offset = root->owner_end_bytes;
  }
  return next_offset == view.bank->used_bytes;
}

static int capture_associated(al_mailbox_runtime *runtime, uint32_t mailbox_id,
                              bank_snapshot *snapshot) {
  al_mailbox_owning_state_view view;
  uint32_t index;
  uint32_t next_offset = 0u;
  if (snapshot == NULL || !get_view(runtime, mailbox_id, &view) ||
      view.pending == 0u || view.associated_context == NULL ||
      view.associated_roots == NULL || view.associated_root_count != 2u)
    return 0;
  memset(snapshot, 0, sizeof(*snapshot));
  snapshot->pending = 1u;
  snapshot->root_count = 2u;
  for (index = 0u; index < view.associated_root_count; ++index) {
    const al_owning_bank_stack_slice *slice = &view.associated_roots[index];
    root_snapshot *target = &snapshot->roots[index];
    al_owning_value_size size;
    if (slice->type_index >= loaded_module->layout->type_count ||
        al_owning_measure_value(
            (al_owning_stack_context *)view.associated_context,
            loaded_module->layout, slice->type_index,
            slice->source_offset_bytes, slice->source_owner_end_bytes, 0u,
            &size) != 0 ||
        size.extent_bytes == 0u || size.extent_bytes > ROOT_BYTES_MAX ||
        slice->source_offset_bytes + size.extent_bytes >
            view.associated_context->cursor_bytes)
      return 0;
    target->type_id = loaded_module->layout->types[slice->type_index].type_id;
    target->offset_bytes = next_offset;
    target->extent_bytes = size.extent_bytes;
    target->payload_bytes = size.payload_bytes;
    target->owner_end_bytes = next_offset + size.extent_bytes;
    hex_encode(view.associated_context->stack_data + slice->source_offset_bytes,
               size.extent_bytes, target->serialized_hex,
               sizeof(target->serialized_hex));
    next_offset += size.extent_bytes;
  }
  snapshot->used_bytes = next_offset;
  return 1;
}

static int capture_raw_pending(al_mailbox_runtime *runtime,
                               uint32_t mailbox_id,
                               raw_pending_snapshot *snapshot) {
  al_mailbox_owning_state_view view;
  if (snapshot == NULL || !get_view(runtime, mailbox_id, &view) ||
      view.bank == NULL || view.bank->bytes == NULL ||
      view.bank->roots == NULL ||
      view.bank->root_count > AL_OWNING_MAILBOX_MAX_OUTPUTS ||
      view.bank->used_bytes > sizeof(snapshot->bank_bytes))
    return 0;
  memset(snapshot, 0, sizeof(*snapshot));
  snapshot->pending = view.pending;
  snapshot->bank_root_count = view.bank->root_count;
  snapshot->bank_used_bytes = view.bank->used_bytes;
  if (view.bank->root_count != 0u)
    memcpy(snapshot->bank_roots, view.bank->roots,
           (size_t)view.bank->root_count * sizeof(view.bank->roots[0]));
  if (view.bank->used_bytes != 0u)
    memcpy(snapshot->bank_bytes, view.bank->bytes, view.bank->used_bytes);
  if (view.associated_context != NULL || view.associated_roots != NULL) {
    if (view.associated_context == NULL || view.associated_roots == NULL ||
        view.associated_root_count != AL_OWNING_MAILBOX_MAX_OUTPUTS ||
        view.associated_context->cursor_bytes >
            sizeof(snapshot->associated_prefix))
      return 0;
    snapshot->context_address = (uintptr_t)view.associated_context;
    snapshot->associated_root_count = view.associated_root_count;
    snapshot->associated_cursor_bytes = view.associated_context->cursor_bytes;
    memcpy(snapshot->associated_roots, view.associated_roots,
           sizeof(snapshot->associated_roots));
    if (snapshot->associated_cursor_bytes != 0u)
      memcpy(snapshot->associated_prefix,
             view.associated_context->stack_data,
             snapshot->associated_cursor_bytes);
  }
  return 1;
}

static int capture_logical(al_mailbox_runtime *runtime, uint32_t mailbox_id,
                           uint32_t policy, bank_snapshot *snapshot) {
  al_mailbox_owning_state_view view;
  if (!get_view(runtime, mailbox_id, &view))
    return 0;
  if (policy == AL_MAILBOX_OWNING_POLICY_KEEP_ASSOCIATED &&
      view.pending != 0u)
    return capture_associated(runtime, mailbox_id, snapshot);
  return capture_bank(runtime, mailbox_id, snapshot);
}

static int create_runtime(const al_owning_mailbox_module *module,
                          uint32_t policy, runtime_fixture *fixture) {
  al_mailbox_result result;
  if (fixture == NULL)
    return 0;
  memset(fixture, 0, sizeof(*fixture));
  fixture->config.control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  fixture->config.struct_size = (uint32_t)sizeof(fixture->config);
  fixture->config.mailbox_capacity = 1u;
  fixture->config.scratch_byte_capacity = SCRATCH_BYTES;
  fixture->config.retained_byte_capacity = RAW_BYTES_MAX;
  fixture->config.text_staging_byte_capacity = STAGING_BYTES;
  fixture->config.scratch_slot_capacity = 1u;
  fixture->config.suspension_policy = policy;
  fixture->requirements.control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  fixture->requirements.struct_size = (uint32_t)sizeof(fixture->requirements);
  result = al_mailbox_get_owning_storage_requirements(
      module, &fixture->config, &fixture->requirements);
  if (result != AL_MAILBOX_OK || fixture->requirements.storage_bytes == 0u ||
      fixture->requirements.storage_bytes > SIZE_MAX)
    return 0;
  fixture->storage_bytes = fixture->requirements.storage_bytes;
  fixture->storage = VirtualAlloc(NULL, (size_t)fixture->storage_bytes,
                                 MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
  if (fixture->storage == NULL)
    return 0;
  result = al_mailbox_runtime_init_owning(
      module, &fixture->config, fixture->storage, fixture->storage_bytes,
      &fixture->runtime);
  if (result == AL_MAILBOX_OK && fixture->runtime != NULL)
    return 1;
  (void)VirtualFree(fixture->storage, 0u, MEM_RELEASE);
  fixture->storage = NULL;
  fixture->storage_bytes = 0u;
  return 0;
}

static void dispose_runtime(runtime_fixture *fixture) {
  if (fixture == NULL)
    return;
  if (fixture->runtime != NULL) {
    (void)al_mailbox_dispose(fixture->runtime);
    fixture->runtime = NULL;
  }
  if (fixture->storage != NULL) {
    (void)VirtualFree(fixture->storage, 0u, MEM_RELEASE);
    fixture->storage = NULL;
  }
}

static int read_stats(al_mailbox_runtime *runtime,
                      al_mailbox_owning_stats *stats) {
  memset(stats, 0, sizeof(*stats));
  stats->control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  stats->struct_size = (uint32_t)sizeof(*stats);
  return al_mailbox_get_owning_stats(runtime, stats) == AL_MAILBOX_OK;
}

static void copy_stats(profile_stats *target,
                       const al_mailbox_owning_stats *source) {
  target->utf8_input_bytes = source->utf8_input_bytes;
  target->utf16_staging_bytes = source->utf16_staging_bytes;
  target->input_import_bytes = source->input_import_bytes;
  target->publication_copy_bytes = source->publication_copy_bytes;
  target->begin_publication_copy_bytes =
      source->begin_publication_copy_bytes;
  target->resume_root_import_bytes = source->resume_root_import_bytes;
  target->deep_copy_bytes = source->deep_copy_bytes;
  target->move_bytes = source->move_bytes;
  target->returned_output_descriptors = source->returned_output_descriptors;
  target->handler_invocations = source->handler_invocations;
  target->handler_failures = source->handler_failures;
  target->turn_reset_bytes = source->turn_reset_bytes;
  target->pending_mailboxes = source->pending_mailboxes;
  target->live_retained_roots = source->live_retained_roots;
  target->outstanding_scratch_leases = source->outstanding_scratch_leases;
  target->pinned_scratch_slots = source->pinned_scratch_slots;
  target->pinned_scratch_bytes = source->pinned_scratch_bytes;
}

static uint32_t find_type_id(const al_owning_layout *layout,
                             uint32_t expected_id) {
  uint32_t result = UINT32_MAX;
  uint32_t index;
  if (layout == NULL || layout->types == NULL)
    return UINT32_MAX;
  for (index = 0u; index < layout->type_count; ++index) {
    if (layout->types[index].type_id == expected_id) {
      if (result != UINT32_MAX)
        return UINT32_MAX;
      result = index;
    }
  }
  return result;
}

static int module_layout_valid(const al_owning_mailbox_module *module) {
  const al_owning_layout *layout;
  const al_owning_type_descriptor *state;
  const al_owning_type_descriptor *continuation;
  const al_owning_type_descriptor *option_string;
  const al_owning_type_descriptor *option_id;
  const al_owning_type_descriptor *result;
  uint32_t state_fields[4];
  uint32_t continuation_fields[4];
  uint32_t index;
  const al_owning_mailbox_entry *initialize;
  const al_owning_mailbox_entry *begin;
  const al_owning_mailbox_entry *resume;
  if (module == NULL || module->abi_version != AL_OWNING_MAILBOX_ABI_VERSION ||
      module->struct_size != sizeof(*module) || module->layout == NULL ||
      module->layout->abi_version != AL_OWNING_LAYOUT_ABI_VERSION ||
      module->layout->types == NULL || module->layout->fields == NULL ||
      state_index >= module->layout->type_count ||
      continuation_index >= module->layout->type_count ||
      string_index >= module->layout->type_count ||
      option_string_index >= module->layout->type_count ||
      option_id_index >= module->layout->type_count ||
      result_index >= module->layout->type_count)
    return 0;
  layout = module->layout;
  positive_id_index = find_type_id(layout, 6u);
  non_empty_string_index = find_type_id(layout, 5u);
  state = &layout->types[state_index];
  continuation = &layout->types[continuation_index];
  option_string = &layout->types[option_string_index];
  option_id = &layout->types[option_id_index];
  result = &layout->types[result_index];
  if (positive_id_index == UINT32_MAX || non_empty_string_index == UINT32_MAX ||
      state->type_id != 7u || continuation->type_id != 4u ||
      layout->types[string_index].type_id != 8u ||
      state->kind != AL_OWNING_TYPE_RECORD || state->field_count != 4u ||
      state->first_field > layout->field_count ||
      state->field_count > layout->field_count - state->first_field ||
      continuation->kind != AL_OWNING_TYPE_RECORD ||
      continuation->field_count != 4u ||
      continuation->first_field > layout->field_count ||
      continuation->field_count > layout->field_count - continuation->first_field ||
      layout->types[positive_id_index].kind != AL_OWNING_TYPE_I64 ||
      layout->types[positive_id_index].type_id != 6u ||
      layout->types[non_empty_string_index].kind != AL_OWNING_TYPE_STRING ||
      layout->types[non_empty_string_index].type_id != 5u ||
      layout->types[string_index].kind != AL_OWNING_TYPE_STRING ||
      option_string->kind != AL_OWNING_TYPE_OPTION ||
      option_id->kind != AL_OWNING_TYPE_OPTION ||
      option_string->case_count != 2u || option_id->case_count != 2u ||
      option_string->field_count != 2u || option_id->field_count != 2u ||
      layout->field_count < 2u ||
      option_string->first_field > layout->field_count - 2u ||
      option_id->first_field > layout->field_count - 2u ||
      result->kind != AL_OWNING_TYPE_RESULT || result->field_count != 2u ||
      result->case_count != 2u || result->first_field > layout->field_count - 2u)
    return 0;
  for (index = 0u; index < 4u; ++index) {
    state_fields[index] = layout->fields[state->first_field + index].child_type_index;
    continuation_fields[index] =
        layout->fields[continuation->first_field + index].child_type_index;
  }
  if (state_fields[0] != positive_id_index ||
      state_fields[1] != non_empty_string_index ||
      state_fields[2] != option_string_index || state_fields[3] != result_index ||
      continuation_fields[0] != positive_id_index ||
      continuation_fields[1] != non_empty_string_index ||
      continuation_fields[2] != option_id_index ||
      continuation_fields[3] != result_index ||
      layout->fields[option_string->first_field].child_type_index !=
          non_empty_string_index ||
      layout->fields[option_id->first_field].child_type_index != positive_id_index ||
      layout->fields[result->first_field].child_type_index != positive_id_index ||
      layout->fields[result->first_field + 1u].child_type_index !=
          non_empty_string_index)
    return 0;
  initialize = &module->entries[0];
  begin = &module->entries[1];
  resume = &module->entries[2];
  return initialize->input_count == 1u && initialize->output_count == 1u &&
         initialize->input_type_indexes[0] == string_index &&
         initialize->output_type_indexes[0] == state_index &&
         begin->input_count == 2u && begin->output_count == 2u &&
         begin->input_type_indexes[0] == state_index &&
         begin->input_type_indexes[1] == string_index &&
         begin->output_type_indexes[0] == state_index &&
         begin->output_type_indexes[1] == continuation_index &&
         resume->input_count == 3u && resume->output_count == 1u &&
         resume->input_type_indexes[0] == state_index &&
         resume->input_type_indexes[1] == continuation_index &&
         resume->input_type_indexes[2] == string_index &&
         resume->output_type_indexes[0] == state_index &&
         initialize->execute != NULL && begin->execute != NULL &&
         resume->execute != NULL && module->associated_resume != NULL;
}

static void setup_direct(direct_fixture *fixture) {
  memset(fixture, 0, sizeof(*fixture));
  fixture->context.abi_version = AL_OWNING_STACK_ABI_VERSION;
  fixture->context.stack_capacity_bytes = DIRECT_BYTES;
  fixture->context.init_bitmap_bytes = AL_OWNING_TRUSTED_GENERATED
                                           ? 0u
                                           : DIRECT_BYTES / 8u;
  fixture->context.stack_data = fixture->stack;
  fixture->context.init_bitmap = AL_OWNING_TRUSTED_GENERATED
                                     ? NULL
                                     : fixture->initialized;
  fixture->context.poison_bitmap = AL_OWNING_TRUSTED_GENERATED
                                       ? NULL
                                       : fixture->poisoned;
  al_owning_begin(&fixture->context);
}

static int descriptor_is_invalid(const al_owning_bank_stack_slice *slice) {
  return slice->type_index == UINT32_MAX && slice->source_offset_bytes == 0u &&
         slice->source_owner_end_bytes == 0u && slice->reserved == 0u;
}

static int descriptors_invalid(const al_owning_bank_stack_slice *outputs,
                               uint32_t count) {
  uint32_t index;
  for (index = 0u; index < count; ++index) {
    if (!descriptor_is_invalid(&outputs[index]))
      return 0;
  }
  return 1;
}

static int capture_direct_outputs(const al_owning_stack_context *context,
                                 const al_owning_bank_stack_slice *outputs,
                                 uint32_t count, bank_snapshot *snapshot) {
  uint32_t index;
  uint32_t next_offset = 0u;
  if (snapshot == NULL || count > AL_OWNING_MAILBOX_MAX_OUTPUTS)
    return 0;
  memset(snapshot, 0, sizeof(*snapshot));
  snapshot->root_count = count;
  for (index = 0u; index < count; ++index) {
    root_snapshot *target = &snapshot->roots[index];
    al_owning_value_size size;
    const al_owning_bank_stack_slice *slice = &outputs[index];
    if (slice->type_index >= loaded_module->layout->type_count ||
        slice->source_offset_bytes > slice->source_owner_end_bytes ||
        slice->source_owner_end_bytes > context->cursor_bytes ||
        al_owning_measure_value((al_owning_stack_context *)context,
                                loaded_module->layout, slice->type_index,
                                slice->source_offset_bytes,
                                slice->source_owner_end_bytes, 0u, &size) != 0 ||
        size.extent_bytes == 0u || size.extent_bytes > ROOT_BYTES_MAX ||
        slice->source_offset_bytes + size.extent_bytes > context->cursor_bytes)
      return 0;
    target->type_id = loaded_module->layout->types[slice->type_index].type_id;
    target->offset_bytes = next_offset;
    target->extent_bytes = size.extent_bytes;
    target->payload_bytes = size.payload_bytes;
    target->owner_end_bytes = next_offset + size.extent_bytes;
    hex_encode(context->stack_data + slice->source_offset_bytes,
               size.extent_bytes, target->serialized_hex,
               sizeof(target->serialized_hex));
    next_offset += size.extent_bytes;
  }
  snapshot->used_bytes = next_offset;
  return 1;
}

static int run_direct_begin(const uint8_t *state_bytes, uint32_t state_extent,
                            uint32_t mode, direct_call *evidence) {
  static const uint8_t text_b[16] = {
      1u, 0u, 0u, 0u, 0u, 0u, 0u, 0u,
      0x42u, 0u, 0u, 0u, 0u, 0u, 0u, 0u};
  static const al_owning_bank_stack_slice output_canaries[2] = {
      {UINT32_C(0x12345678), UINT32_C(0x11111111), UINT32_C(0x22222222),
       UINT32_C(0x33333333)},
      {UINT32_C(0x87654321), UINT32_C(0x44444444), UINT32_C(0x55555555),
       UINT32_C(0x66666666)}};
  direct_fixture fixture;
  al_owning_external_slice inputs[2];
  al_owning_bank_stack_slice outputs[2];
  int32_t callback_result;
  if (state_bytes == NULL || evidence == NULL)
    return 0;
  memset(evidence, 0, sizeof(*evidence));
  setup_direct(&fixture);
  inputs[0].bytes = state_bytes;
  inputs[0].extent_bytes = state_extent;
  inputs[0].type_index = mode == 2u ? continuation_index : state_index;
  inputs[1].bytes = text_b;
  inputs[1].extent_bytes = (uint32_t)sizeof(text_b);
  inputs[1].type_index = string_index;
  memcpy(outputs, output_canaries, sizeof(outputs));
  callback_result = loaded_module->entries[1].execute(
      &fixture.context, inputs, 2u, outputs, 2u);
  evidence->callback_result = (uint32_t)callback_result;
  evidence->context_status = fixture.context.status;
  evidence->error_id = fixture.context.error_id;
  evidence->steps_consumed = fixture.context.steps_consumed;
  if (mode == 0u) {
    evidence->output_contract =
        callback_result == 0 && fixture.context.status == AL_OWNING_STATUS_OK &&
        capture_direct_outputs(&fixture.context, outputs, 2u,
                               &evidence->outputs);
  } else if (mode == 1u) {
    evidence->output_contract = callback_result != 0 &&
                                fixture.context.status != AL_OWNING_STATUS_OK &&
                                descriptors_invalid(outputs, 2u);
  } else if (mode == 2u) {
    evidence->output_contract = callback_result != 0 &&
                                fixture.context.status != AL_OWNING_STATUS_OK &&
                                memcmp(outputs, output_canaries,
                                       sizeof(outputs)) == 0;
  } else {
    evidence->output_contract = callback_result != 0 &&
                                fixture.context.status != AL_OWNING_STATUS_OK &&
                                descriptors_invalid(outputs, 2u);
  }
  return evidence->output_contract != 0u;
}

static int run_direct_controls(direct_evidence *evidence) {
  uint8_t state_a[48];
  uint8_t bad[64];
  int okay = 1;
  memcpy(state_a, fixture_initialized_state, sizeof(state_a));
  okay = run_direct_begin(state_a, (uint32_t)sizeof(state_a), 0u,
                          &evidence->valid_begin) && okay;

  memcpy(bad, state_a, sizeof(state_a));
  write_u64_le(bad, UINT64_MAX);
  okay = run_direct_begin(bad, 48u, 1u, &evidence->negative_id) && okay;

  memcpy(bad, state_a, sizeof(state_a));
  write_u64_le(bad, UINT64_C(0x8000000000000000));
  okay = run_direct_begin(bad, 48u, 1u, &evidence->overflow_id) && okay;

  memset(bad, 0, 64u);
  memcpy(bad, state_a, 24u);
  write_u64_le(bad + 24u, UINT64_C(0));
  write_u64_le(bad + 32u, UINT64_C(0));
  memcpy(bad + 40u, state_a + 32u, 8u);
  memcpy(bad + 48u, state_a + 40u, 8u);
  okay = run_direct_begin(bad, 56u, 1u,
                          &evidence->some_empty_label) && okay;

  memcpy(bad, state_a, sizeof(state_a));
  write_u32_le(bad + 32u, 1u);
  write_u32_le(bad + 40u, 0u);
  write_u64_le(bad + 40u, UINT64_C(0));
  okay = run_direct_begin(bad, 48u, 1u,
                          &evidence->result_error_empty_label) && okay;

  memcpy(bad, state_a, sizeof(state_a));
  write_u64_le(bad + 24u, UINT64_C(2));
  okay = run_direct_begin(bad, 48u, 1u,
                          &evidence->malformed_option_tag) && okay;
  memcpy(bad, state_a, sizeof(state_a));
  write_u64_le(bad + 32u, UINT64_C(2));
  okay = run_direct_begin(bad, 48u, 1u,
                          &evidence->malformed_result_tag) && okay;
  okay = run_direct_begin(state_a, (uint32_t)sizeof(state_a), 2u,
                          &evidence->wrong_input_index) && okay;
  return okay;
}

static void capture_call(call_evidence *target, al_mailbox_result result,
                         const al_mailbox_call_info *info) {
  target->result = (uint32_t)result;
  target->error_id = info->error_metadata_id < 0
                         ? UINT32_MAX
                         : (uint32_t)info->error_metadata_id;
  target->steps_consumed = info->steps_consumed;
}

static int capture_policy_pair(al_mailbox_runtime *runtime, uint32_t policy,
                               bank_snapshot *bank,
                               bank_snapshot *associated) {
  if (!capture_bank(runtime, 0u, bank))
    return 0;
  if (policy == AL_MAILBOX_OWNING_POLICY_KEEP_ASSOCIATED)
    return capture_associated(runtime, 0u, associated);
  memset(associated, 0, sizeof(*associated));
  associated->pending = bank->pending;
  return 1;
}

static int run_policy(const al_owning_mailbox_module *module, uint32_t policy,
                      policy_evidence *evidence) {
  static const uint8_t text_a[] = {'A'};
  static const uint8_t text_b[] = {'B'};
  static const uint8_t text_c[] = {'C'};
  static const uint8_t text_fail[] = {'F', 'A', 'I', 'L'};
  static const uint8_t text_empty[1] = {0u};
  runtime_fixture fixture;
  al_mailbox_call_info info;
  al_mailbox_owning_stats stats;
  al_mailbox_owning_state_view view;
  al_mailbox_token token;
  al_mailbox_result result;
  int okay = 1;
  memset(evidence, 0, sizeof(*evidence));
  memset(&fixture, 0, sizeof(fixture));
  memset(&token, 0, sizeof(token));
  if (!create_runtime(module, policy, &fixture))
    return 0;

  initialize_call_info(&info);
  result = al_mailbox_init_text(fixture.runtime, 0u, text_a,
                                (uint32_t)sizeof(text_a), &info);
  capture_call(&evidence->initialize_call, result, &info);
  okay = result == AL_MAILBOX_OK &&
         capture_bank(fixture.runtime, 0u, &evidence->initialized) && okay;

  initialize_call_info(&info);
  result = al_mailbox_begin_text(fixture.runtime, 0u, text_b,
                                 (uint32_t)sizeof(text_b), &token, &info);
  capture_call(&evidence->begin_call, result, &info);
  okay = result == AL_MAILBOX_OK &&
         capture_policy_pair(fixture.runtime, policy,
                             &evidence->pending_bank,
                             &evidence->pending_associated) &&
         capture_logical(fixture.runtime, 0u, policy,
                         &evidence->before_empty_resume) &&
         capture_raw_pending(fixture.runtime, 0u, &evidence->raw_pending) &&
         okay;
  evidence->token_before = token;

  initialize_call_info(&info);
  result = al_mailbox_resume_text(fixture.runtime, 0u, &token, text_empty, 0u,
                                  &info);
  capture_call(&evidence->empty_resume_call, result, &info);
  evidence->token_after_empty = token;
  evidence->empty_resume_preserved =
      result == AL_MAILBOX_HANDLER_FAILURE &&
      capture_logical(fixture.runtime, 0u, policy,
                      &evidence->after_empty_resume) &&
      capture_raw_pending(fixture.runtime, 0u, &evidence->raw_after_empty) &&
      get_view(fixture.runtime, 0u, &view) && view.pending != 0u &&
      memcmp(&evidence->before_empty_resume, &evidence->after_empty_resume,
             sizeof(evidence->before_empty_resume)) == 0 &&
      memcmp(&evidence->raw_pending, &evidence->raw_after_empty,
             sizeof(evidence->raw_pending)) == 0 &&
      memcmp(&evidence->token_before, &evidence->token_after_empty,
             sizeof(token)) == 0;

  initialize_call_info(&info);
  result = al_mailbox_resume_text(fixture.runtime, 0u, &token, text_fail,
                                  (uint32_t)sizeof(text_fail), &info);
  capture_call(&evidence->fail_resume_call, result, &info);
  evidence->token_after_fail = token;
  evidence->fail_resume_preserved =
      result == AL_MAILBOX_HANDLER_FAILURE &&
      capture_logical(fixture.runtime, 0u, policy,
                      &evidence->after_fail_resume) &&
      capture_raw_pending(fixture.runtime, 0u, &evidence->raw_after_fail) &&
      get_view(fixture.runtime, 0u, &view) && view.pending != 0u &&
      memcmp(&evidence->before_empty_resume, &evidence->after_fail_resume,
             sizeof(evidence->before_empty_resume)) == 0 &&
      memcmp(&evidence->raw_pending, &evidence->raw_after_fail,
             sizeof(evidence->raw_pending)) == 0 &&
      memcmp(&evidence->token_before, &evidence->token_after_fail,
             sizeof(token)) == 0;

  initialize_call_info(&info);
  result = al_mailbox_resume_text(fixture.runtime, 0u, &token, text_c,
                                  (uint32_t)sizeof(text_c), &info);
  capture_call(&evidence->retry_resume_call, result, &info);
  okay = result == AL_MAILBOX_OK &&
         capture_bank(fixture.runtime, 0u, &evidence->final) && okay;
  if (!read_stats(fixture.runtime, &stats))
    okay = 0;
  else
    copy_stats(&evidence->stats, &stats);
  dispose_runtime(&fixture);
  return okay;
}

static int type_layout_contract(const al_owning_mailbox_module *module) {
  const al_owning_layout *layout = module->layout;
  const al_owning_type_descriptor *state = &layout->types[state_index];
  const al_owning_type_descriptor *continuation =
      &layout->types[continuation_index];
  uint32_t expected_ids[] = {4u, 5u, 6u, 7u, 8u};
  uint32_t index;
  for (index = 0u; index < sizeof(expected_ids) / sizeof(expected_ids[0]);
       ++index) {
    if (find_type_id(layout, expected_ids[index]) == UINT32_MAX)
      return 0;
  }
  return state->type_id == 7u && state->field_count == 4u &&
         continuation->type_id == 4u && continuation->field_count == 4u &&
         layout->types[option_string_index].kind == AL_OWNING_TYPE_OPTION &&
         layout->types[option_id_index].kind == AL_OWNING_TYPE_OPTION &&
         layout->types[result_index].kind == AL_OWNING_TYPE_RESULT;
}

static void print_snapshot_json(const bank_snapshot *snapshot) {
  uint32_t index;
  printf("{\"pending\":%u,\"rootCount\":%u,\"usedBytes\":%u,\"roots\":[",
         snapshot->pending, snapshot->root_count, snapshot->used_bytes);
  for (index = 0u; index < snapshot->root_count; ++index) {
    const root_snapshot *root = &snapshot->roots[index];
    if (index != 0u)
      putchar(',');
    printf("{\"typeId\":%u,\"offsetBytes\":%u,\"extentBytes\":%u,\"payloadBytes\":%u,\"ownerEndBytes\":%u,\"serializedHex\":\"%s\"}",
           root->type_id, root->offset_bytes, root->extent_bytes,
           root->payload_bytes, root->owner_end_bytes,
           root->serialized_hex);
  }
  printf("]}");
}

static void print_call_json(const call_evidence *call) {
  printf("{\"result\":%u,\"errorId\":%u,\"stepsConsumed\":%u}",
         call->result, call->error_id, call->steps_consumed);
}

static void print_token_json(const al_mailbox_token *token) {
  printf("{\"opaque\":[\"%016" PRIx64 "\",\"%016" PRIx64 "\",\"%016" PRIx64 "\"]}",
         token->opaque[0], token->opaque[1], token->opaque[2]);
}

static void print_direct_call_json(const direct_call *call) {
  printf("{\"callbackResult\":%u,\"contextStatus\":%u,\"errorId\":%u,\"stepsConsumed\":%u,\"outputContract\":%s,\"outputs\":",
         call->callback_result, call->context_status, call->error_id,
         call->steps_consumed, call->output_contract ? "true" : "false");
  print_snapshot_json(&call->outputs);
  putchar('}');
}

static void print_stats_json(const profile_stats *stats) {
  printf("{\"utf8InputBytes\":%" PRIu64
         ",\"utf16StagingBytes\":%" PRIu64
         ",\"inputImportBytes\":%" PRIu64
         ",\"publicationCopyBytes\":%" PRIu64
         ",\"beginPublicationCopyBytes\":%" PRIu64
         ",\"resumeRootImportBytes\":%" PRIu64
         ",\"deepCopyBytes\":%" PRIu64
         ",\"moveBytes\":%" PRIu64
         ",\"returnedOutputDescriptors\":%" PRIu64
         ",\"handlerInvocations\":%" PRIu64
         ",\"handlerFailures\":%" PRIu64
         ",\"turnResetBytes\":%" PRIu64
         ",\"pendingMailboxes\":%u,\"liveRetainedRoots\":%" PRIu64 ",\"outstandingScratchLeases\":%u,\"pinnedScratchSlots\":%u,\"pinnedScratchBytes\":%" PRIu64 "}",
         stats->utf8_input_bytes, stats->utf16_staging_bytes,
         stats->input_import_bytes, stats->publication_copy_bytes,
         stats->begin_publication_copy_bytes,
         stats->resume_root_import_bytes, stats->deep_copy_bytes,
         stats->move_bytes, stats->returned_output_descriptors,
         stats->handler_invocations, stats->handler_failures,
         stats->turn_reset_bytes, stats->pending_mailboxes,
         stats->live_retained_roots, stats->outstanding_scratch_leases,
         stats->pinned_scratch_slots, stats->pinned_scratch_bytes);
}

static void print_raw_snapshot_json(const raw_pending_snapshot *snapshot) {
  char bank_hex[RAW_BYTES_MAX * 2u + 1u];
  char associated_hex[RAW_BYTES_MAX * 2u + 1u];
  uint32_t index;
  hex_encode(snapshot->bank_bytes, snapshot->bank_used_bytes, bank_hex,
             sizeof(bank_hex));
  hex_encode(snapshot->associated_prefix,
             snapshot->associated_cursor_bytes, associated_hex,
             sizeof(associated_hex));
  printf("{\"pending\":%u,\"bankRootCount\":%u,\"bankUsedBytes\":%u,\"bankBytesHex\":\"%s\",\"bankRoots\":[",
         snapshot->pending, snapshot->bank_root_count,
         snapshot->bank_used_bytes, bank_hex);
  for (index = 0u; index < snapshot->bank_root_count; ++index) {
    const al_owning_bank_root *root = &snapshot->bank_roots[index];
    if (index != 0u)
      putchar(',');
    printf("{\"typeId\":%u,\"offsetBytes\":%u,\"extentBytes\":%u,\"payloadBytes\":%u,\"ownerEndBytes\":%u}",
           root->type_id, root->offset_bytes, root->extent_bytes,
           root->payload_bytes, root->owner_end_bytes);
  }
  printf("],\"associatedRootCount\":%u,\"associatedCursorBytes\":%u,\"associatedPrefixHex\":\"%s\",\"associatedRoots\":[",
         snapshot->associated_root_count,
         snapshot->associated_cursor_bytes, associated_hex);
  for (index = 0u; index < snapshot->associated_root_count; ++index) {
    const al_owning_bank_stack_slice *root = &snapshot->associated_roots[index];
    if (index != 0u)
      putchar(',');
    printf("{\"typeIndex\":%u,\"sourceOffsetBytes\":%u,\"sourceOwnerEndBytes\":%u,\"reserved\":%u}",
           root->type_index, root->source_offset_bytes,
           root->source_owner_end_bytes, root->reserved);
  }
  putchar(']');
  printf(",\"contextAddress\":\"%" PRIxPTR "\"}",
         snapshot->context_address);
}

static void print_policy_json(const policy_evidence *evidence) {
  printf("{\"initialize\":");
  print_snapshot_json(&evidence->initialized);
  printf(",\"pendingBank\":");
  print_snapshot_json(&evidence->pending_bank);
  printf(",\"pendingAssociated\":");
  print_snapshot_json(&evidence->pending_associated);
  printf(",\"beforeEmptyResume\":");
  print_snapshot_json(&evidence->before_empty_resume);
  printf(",\"afterEmptyResume\":");
  print_snapshot_json(&evidence->after_empty_resume);
  printf(",\"afterFailResume\":");
  print_snapshot_json(&evidence->after_fail_resume);
  printf(",\"rawPending\":");
  print_raw_snapshot_json(&evidence->raw_pending);
  printf(",\"rawAfterEmptyResume\":");
  print_raw_snapshot_json(&evidence->raw_after_empty);
  printf(",\"rawAfterFailResume\":");
  print_raw_snapshot_json(&evidence->raw_after_fail);
  printf(",\"final\":");
  print_snapshot_json(&evidence->final);
  printf(",\"calls\":{\"initialize\":");
  print_call_json(&evidence->initialize_call);
  printf(",\"begin\":");
  print_call_json(&evidence->begin_call);
  printf(",\"resumeEmpty\":");
  print_call_json(&evidence->empty_resume_call);
  printf(",\"resumeFail\":");
  print_call_json(&evidence->fail_resume_call);
  printf(",\"retry\":");
  print_call_json(&evidence->retry_resume_call);
  printf("},\"preservation\":{\"emptyResume\":%s,\"failResume\":%s},\"stats\":",
         evidence->empty_resume_preserved ? "true" : "false",
         evidence->fail_resume_preserved ? "true" : "false");
  print_stats_json(&evidence->stats);
  printf(",\"tokens\":{\"before\":");
  print_token_json(&evidence->token_before);
  printf(",\"afterEmptyResume\":");
  print_token_json(&evidence->token_after_empty);
  printf(",\"afterFailResume\":");
  print_token_json(&evidence->token_after_fail);
  putchar('}');
  putchar('}');
}

static void print_checks_json(void) {
  size_t index;
  putchar('[');
  for (index = 0u; index < check_count; ++index) {
    if (index != 0u)
      putchar(',');
    printf("{\"name\":\"%s\",\"passed\":%s}", checks[index].name,
           checks[index].passed ? "true" : "false");
  }
  putchar(']');
}

static int parse_index(const char *text, uint32_t *value) {
  char *end = NULL;
  unsigned long parsed;
  if (text == NULL || value == NULL || text[0] == '\0')
    return 0;
  parsed = strtoul(text, &end, 10);
  if (end == text || *end != '\0' || parsed > UINT32_MAX)
    return 0;
  *value = (uint32_t)parsed;
  return 1;
}

static int hex_nibble(char value, uint8_t *nibble) {
  if (value >= '0' && value <= '9') {
    *nibble = (uint8_t)(value - '0');
    return 1;
  }
  if (value >= 'a' && value <= 'f') {
    *nibble = (uint8_t)(value - 'a' + 10);
    return 1;
  }
  if (value >= 'A' && value <= 'F') {
    *nibble = (uint8_t)(value - 'A' + 10);
    return 1;
  }
  return 0;
}

static int parse_hex_bytes(const char *text, uint8_t *bytes,
                           uint32_t byte_count) {
  uint32_t index;
  if (text == NULL || bytes == NULL || strlen(text) != (size_t)byte_count * 2u)
    return 0;
  for (index = 0u; index < byte_count; ++index) {
    uint8_t high;
    uint8_t low;
    if (!hex_nibble(text[index * 2u], &high) ||
        !hex_nibble(text[index * 2u + 1u], &low))
      return 0;
    bytes[index] = (uint8_t)((high << 4u) | low);
  }
  return 1;
}

int main(int argc, char **argv) {
  HMODULE library = NULL;
  al_owning_mailbox_module_fn get_module;
  const al_owning_mailbox_module *module = NULL;
  policy_evidence return_evidence;
  policy_evidence associated_evidence;
  direct_evidence direct;
  int args_valid;
  int layout_valid = 0;
  int direct_ok = 0;
  int return_ok = 0;
  int associated_ok = 0;
  int abi_ok = 0;
  uint32_t stack_abi = AL_OWNING_STACK_ABI_VERSION;

  if (argc != 9) {
    fprintf(stderr,
            "Usage: native-owning-mailbox-refinements <module.dll> <StateIndex> <ContinuationIndex> <StringIndex> <OptionNonEmptyStringIndex> <OptionPositiveIdIndex> <ResultIndex> <StateFixtureHex>\n");
    return 2;
  }
  args_valid = parse_index(argv[2], &state_index) &&
               parse_index(argv[3], &continuation_index) &&
               parse_index(argv[4], &string_index) &&
               parse_index(argv[5], &option_string_index) &&
               parse_index(argv[6], &option_id_index) &&
               parse_index(argv[7], &result_index) &&
               parse_hex_bytes(argv[8], fixture_initialized_state,
                               (uint32_t)sizeof(fixture_initialized_state));
  library = LoadLibraryA(argv[1]);
  if (library != NULL) {
    get_module = (al_owning_mailbox_module_fn)(uintptr_t)GetProcAddress(
        library, "agentlang_owning_mailbox_module");
    if (get_module != NULL)
      module = get_module();
  }
  loaded_module = module;
  layout_valid = args_valid && module_layout_valid(module);
  memset(&return_evidence, 0, sizeof(return_evidence));
  memset(&associated_evidence, 0, sizeof(associated_evidence));
  memset(&direct, 0, sizeof(direct));
  if (layout_valid) {
    direct_ok = run_direct_controls(&direct);
    return_ok = run_policy(module, AL_MAILBOX_OWNING_POLICY_RETURN,
                           &return_evidence);
    associated_ok = run_policy(module,
                               AL_MAILBOX_OWNING_POLICY_KEEP_ASSOCIATED,
                               &associated_evidence);
    abi_ok = module->abi_version == AL_OWNING_MAILBOX_ABI_VERSION &&
             module->layout->abi_version == AL_OWNING_LAYOUT_ABI_VERSION &&
             stack_abi == 1u && sizeof(al_owning_type_descriptor) == 36u &&
             sizeof(al_owning_field_descriptor) == 16u &&
             sizeof(al_owning_layout) == 40u &&
             sizeof(al_owning_mailbox_module) == 144u;
  }

  record_check(0u, layout_valid && type_layout_contract(module));
  record_check(1u, direct_ok && direct.valid_begin.output_contract);
  record_check(2u, direct.negative_id.output_contract &&
                       direct.some_empty_label.output_contract &&
                       direct.result_error_empty_label.output_contract);
  record_check(3u, direct.overflow_id.output_contract);
  record_check(4u, direct.malformed_option_tag.output_contract &&
                       direct.malformed_result_tag.output_contract);
  record_check(5u, direct.wrong_input_index.output_contract);
  record_check(6u, return_ok && associated_ok);
  record_check(7u, return_evidence.empty_resume_preserved &&
                       associated_evidence.empty_resume_preserved);
  record_check(8u, return_evidence.fail_resume_preserved &&
                       associated_evidence.fail_resume_preserved);
  record_check(9u, return_evidence.stats.pending_mailboxes == 0u &&
                       associated_evidence.stats.pending_mailboxes == 0u &&
                       return_evidence.stats.outstanding_scratch_leases == 0u &&
                       associated_evidence.stats.outstanding_scratch_leases == 0u &&
                       return_evidence.stats.pinned_scratch_slots == 0u &&
                       associated_evidence.stats.pinned_scratch_slots == 0u &&
                       return_evidence.stats.pinned_scratch_bytes == 0u &&
                       associated_evidence.stats.pinned_scratch_bytes == 0u);
  record_check(10u, return_evidence.initialize_call.steps_consumed != 0u &&
                       return_evidence.begin_call.steps_consumed != 0u &&
                       return_evidence.empty_resume_call.steps_consumed != 0u &&
                       return_evidence.fail_resume_call.steps_consumed != 0u &&
                       return_evidence.retry_resume_call.steps_consumed != 0u &&
                       associated_evidence.initialize_call.steps_consumed != 0u &&
                       associated_evidence.begin_call.steps_consumed != 0u &&
                       associated_evidence.empty_resume_call.steps_consumed != 0u &&
                       associated_evidence.fail_resume_call.steps_consumed != 0u &&
                       associated_evidence.retry_resume_call.steps_consumed != 0u);
  record_check(11u, return_evidence.stats.handler_invocations == 5u &&
                        associated_evidence.stats.handler_invocations == 5u &&
                        return_evidence.stats.returned_output_descriptors != 0u &&
                        associated_evidence.stats.returned_output_descriptors != 0u);
  record_check(12u, abi_ok);

  printf("{\"passed\":%s,\"failureCount\":%u,\"checks\":",
         failure_count == 0u ? "true" : "false", failure_count);
  print_checks_json();
  printf(",\"abi\":{\"moduleAbiVersion\":%u,\"moduleStructSizeBytes\":%u,\"layoutAbiVersion\":%u,\"stackAbiVersion\":%u,\"typeDescriptorSizeBytes\":%u,\"fieldDescriptorSizeBytes\":%u,\"layoutDescriptorSizeBytes\":%u},\"direct\":{\"validBegin\":",
         module != NULL ? module->abi_version : 0u,
         (uint32_t)sizeof(al_owning_mailbox_module),
         module != NULL && module->layout != NULL
             ? module->layout->abi_version
             : 0u,
         stack_abi, (uint32_t)sizeof(al_owning_type_descriptor),
         (uint32_t)sizeof(al_owning_field_descriptor),
         (uint32_t)sizeof(al_owning_layout));
  print_direct_call_json(&direct.valid_begin);
  printf(",\"negativeId\":");
  print_direct_call_json(&direct.negative_id);
  printf(",\"overflowId\":");
  print_direct_call_json(&direct.overflow_id);
  printf(",\"someEmptyLabel\":");
  print_direct_call_json(&direct.some_empty_label);
  printf(",\"resultErrorEmptyLabel\":");
  print_direct_call_json(&direct.result_error_empty_label);
  printf(",\"postPreflightMalformedOptionTag\":");
  print_direct_call_json(&direct.malformed_option_tag);
  printf(",\"postPreflightMalformedResultTag\":");
  print_direct_call_json(&direct.malformed_result_tag);
  printf(",\"earlyBoundaryWrongInputTypeIndex\":");
  print_direct_call_json(&direct.wrong_input_index);
  printf("},\"policies\":{\"RETURN\":");
  print_policy_json(&return_evidence);
  printf(",\"KEEP_ASSOCIATED\":");
  print_policy_json(&associated_evidence);
  printf("}}\n");
  if (library != NULL)
    FreeLibrary(library);
  return failure_count == 0u ? 0 : 1;
}
