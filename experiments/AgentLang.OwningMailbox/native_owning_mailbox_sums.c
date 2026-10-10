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
  SCRATCH_BYTES = 16384u,
  STAGING_BYTES = 512u,
  MAILBOX_COUNT = 2u,
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

typedef struct raw_pending_snapshot {
  uintptr_t context_address;
  uint32_t pending;
  uint32_t bank_root_count;
  uint32_t bank_used_bytes;
  al_owning_bank_root bank_roots[AL_OWNING_MAILBOX_MAX_OUTPUTS];
  uint8_t bank_bytes[ROOT_BYTES_MAX * AL_OWNING_MAILBOX_MAX_OUTPUTS];
  uint32_t associated_root_count;
  uint32_t associated_cursor_bytes;
  al_owning_bank_stack_slice associated_roots[AL_OWNING_MAILBOX_MAX_OUTPUTS];
  uint8_t associated_prefix[SCRATCH_BYTES];
} raw_pending_snapshot;

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
} profile_stats;

typedef struct policy_evidence {
  bank_snapshot unicode_initialize;
  bank_snapshot unicode_suspended_bank;
  bank_snapshot unicode_suspended_associated;
  bank_snapshot unicode_before_failure;
  bank_snapshot unicode_after_failure;
  bank_snapshot unicode_final;
  bank_snapshot empty_initialize;
  bank_snapshot empty_begin_bank;
  bank_snapshot empty_begin_associated;
  bank_snapshot empty_error_final;
  bank_snapshot empty_error_root;
  bank_snapshot empty_error_to_ok_begin_bank;
  bank_snapshot empty_error_to_ok_begin_associated;
  bank_snapshot empty_error_to_ok_final;
  bank_snapshot capacity_initialize;
  bank_snapshot capacity_begin_bank;
  bank_snapshot capacity_begin_associated;
  bank_snapshot capacity_before_failure;
  bank_snapshot capacity_failure;
  bank_snapshot capacity_final;
  uint32_t unicode_fail_status;
  uint32_t capacity_fail_status;
  uint32_t unicode_failure_preserved;
  uint32_t capacity_failure_preserved;
  uint32_t unicode_malformed_tag_rejected;
  uint32_t unicode_malformed_payload_rejected;
  uint32_t error_round_trip_ok;
  uint32_t move_zero;
  profile_stats stats;
} policy_evidence;

static named_check checks[32];
static size_t check_count;
static unsigned int failure_count;
static const al_owning_mailbox_module *loaded_module;
static uint32_t state_index;
static uint32_t continuation_index;
static uint32_t string_index;
static uint32_t chunk_index;
static uint32_t option_index;
static uint32_t result_index;

static const char *const required_check_names[] = {
    "diagnostic, fast-reset, and trusted-generated host builds each run the sum lifecycle",
    "RETURN and KEEP_ASSOCIATED both publish and resume nested nominal sum payloads",
    "Some survives RETURN bank import and KEEP associated-root resume",
    "Some/Ok, None/Error, Unicode, embedded NUL, and empty payload bytes match this fixture",
    "the Error completion arm is extracted through an ordinary call on a second actual mailbox resume",
    "FAIL occurs after both helper matches and preserves roots and pending token for retry",
    "one-byte-short retained capacity preserves roots and pending token for retry",
    "malformed active sum tag and active nested string payload are rejected with scanner output sentinels unchanged",
    "malformed active tag and payload through mailbox APIs preserve pending roots and token in every profile",
    "an invalid inactive Result alternative is rejected with scanner and controller output sentinels unchanged",
    "ordinary calls and matches report zero payload moves",
    "Layout ABI remains 3 and stack ABI remains 1"};

static const char *const hex_unicode_initialize =
    "0000000000000000030000000000000041003dd842de0000";
static const char *const hex_continuation_some_delta =
    "00000000000000000100000000000000b403000000000000";
static const char *const hex_unicode_final =
    "0000000000000000070000000000000041003dd842deb40300003dd880de0000";
static const char *const hex_z_initialize =
    "000000000000000001000000000000005a00000000000000";
static const char *const hex_continuation_none = "0100000000000000";
static const char *const hex_error_err =
    "010000000000000003000000000000004500520052000000";
static const char *const hex_ok_err =
    "000000000000000003000000000000004500520052000000";
static const char *const hex_capacity_empty =
    "00000000000000000000000000000000";

static void record_check(size_t index, int passed) {
  if (index >= sizeof(required_check_names) / sizeof(required_check_names[0])) {
    ++failure_count;
    return;
  }
  checks[index].name = required_check_names[index];
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
      view.bank->root_count > AL_OWNING_MAILBOX_MAX_OUTPUTS)
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
        al_owning_measure_value((al_owning_stack_context *)view.associated_context,
                                loaded_module->layout, slice->type_index,
                                slice->source_offset_bytes,
                                slice->source_owner_end_bytes, 0u, &size) != 0 ||
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

static void empty_snapshot(bank_snapshot *snapshot, uint32_t pending) {
  memset(snapshot, 0, sizeof(*snapshot));
  snapshot->pending = pending;
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
        view.associated_context->cursor_bytes > SCRATCH_BYTES)
      return 0;
    snapshot->context_address = (uintptr_t)view.associated_context;
    snapshot->associated_root_count = view.associated_root_count;
    snapshot->associated_cursor_bytes =
        view.associated_context->cursor_bytes;
    memcpy(snapshot->associated_roots, view.associated_roots,
           sizeof(snapshot->associated_roots));
    if (snapshot->associated_cursor_bytes != 0u)
      memcpy(snapshot->associated_prefix,
             view.associated_context->stack_data,
             snapshot->associated_cursor_bytes);
  }
  return 1;
}

static int snapshot_matches_root(const bank_snapshot *snapshot,
                                 uint32_t index, uint32_t type_id,
                                 uint32_t offset, uint32_t payload,
                                 uint32_t extent, const char *hex) {
  const root_snapshot *root;
  if (snapshot == NULL || index >= snapshot->root_count)
    return 0;
  root = &snapshot->roots[index];
  return root->type_id == type_id && root->offset_bytes == offset &&
         root->payload_bytes == payload && root->extent_bytes == extent &&
         root->owner_end_bytes == offset + extent &&
         strcmp(root->serialized_hex, hex) == 0;
}

static int snapshot_equal(const bank_snapshot *left,
                          const bank_snapshot *right) {
  return memcmp(left, right, sizeof(*left)) == 0;
}

static int create_runtime(const al_owning_mailbox_module *module,
                          uint32_t policy, uint32_t retained_capacity,
                          uint32_t mailbox_capacity,
                          runtime_fixture *fixture) {
  al_mailbox_result result;
  if (fixture == NULL)
    return 0;
  memset(fixture, 0, sizeof(*fixture));
  fixture->config.control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  fixture->config.struct_size = (uint32_t)sizeof(fixture->config);
  fixture->config.mailbox_capacity = mailbox_capacity;
  fixture->config.scratch_byte_capacity = SCRATCH_BYTES;
  fixture->config.retained_byte_capacity = retained_capacity;
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

static void add_stats(profile_stats *total,
                      const al_mailbox_owning_stats *stats) {
  total->utf8_input_bytes += stats->utf8_input_bytes;
  total->utf16_staging_bytes += stats->utf16_staging_bytes;
  total->input_import_bytes += stats->input_import_bytes;
  total->publication_copy_bytes += stats->publication_copy_bytes;
  total->begin_publication_copy_bytes += stats->begin_publication_copy_bytes;
  total->resume_root_import_bytes += stats->resume_root_import_bytes;
  total->deep_copy_bytes += stats->deep_copy_bytes;
  total->move_bytes += stats->move_bytes;
  total->returned_output_descriptors += stats->returned_output_descriptors;
  total->handler_invocations += stats->handler_invocations;
  total->handler_failures += stats->handler_failures;
}

static void capture_policy_bank_and_associated(
    al_mailbox_runtime *runtime, uint32_t mailbox_id, uint32_t policy,
    bank_snapshot *bank, bank_snapshot *associated) {
  if (!capture_bank(runtime, mailbox_id, bank))
    ++failure_count;
  if (policy == AL_MAILBOX_OWNING_POLICY_KEEP_ASSOCIATED) {
    if (!capture_associated(runtime, mailbox_id, associated))
      ++failure_count;
  } else {
    al_mailbox_owning_state_view view;
    uint32_t pending = get_view(runtime, mailbox_id, &view) ? view.pending : 0u;
    empty_snapshot(associated, pending);
  }
}

static uint8_t *mutable_root_bytes(al_mailbox_runtime *runtime,
                                   uint32_t mailbox_id, uint32_t policy,
                                   uint32_t root_index) {
  al_mailbox_owning_state_view view;
  if (!get_view(runtime, mailbox_id, &view))
    return NULL;
  if (policy == AL_MAILBOX_OWNING_POLICY_RETURN) {
    if (view.bank == NULL || root_index >= view.bank->root_count)
      return NULL;
    return (uint8_t *)(uintptr_t)(view.bank->bytes +
                                 view.bank->roots[root_index].offset_bytes);
  }
  if (view.associated_context == NULL || view.associated_roots == NULL ||
      root_index >= view.associated_root_count)
    return NULL;
  return (uint8_t *)(uintptr_t)(view.associated_context->stack_data +
                                view.associated_roots[root_index]
                                    .source_offset_bytes);
}

static int malformed_resume_preserves_pending(
    al_mailbox_runtime *runtime, uint32_t mailbox_id, uint32_t policy,
    const al_mailbox_token *token, uint32_t kind) {
  al_mailbox_token token_before;
  al_mailbox_call_info info;
  bank_snapshot valid_before;
  raw_pending_snapshot before;
  raw_pending_snapshot after;
  uint8_t original[ROOT_BYTES_MAX];
  uint8_t *root;
  uint32_t byte_offset;
  uint32_t expected_extent;
  al_mailbox_result result;
  static const uint8_t empty[1] = {0u};
  al_mailbox_owning_state_view view;
  if (token == NULL || !get_view(runtime, mailbox_id, &view) ||
      view.pending == 0u || view.bank == NULL)
    return 0;
  if (!capture_logical(runtime, mailbox_id, policy, &valid_before))
    return 0;
  root = mutable_root_bytes(runtime, mailbox_id, policy,
                            kind == 0u ? 0u : 1u);
  if (root == NULL)
    return 0;
  byte_offset = kind == 0u ? 0u : 8u;
  expected_extent = valid_before.roots[kind == 0u ? 0u : 1u].extent_bytes;
  if (expected_extent > sizeof(original))
    return 0;
  memcpy(original, root, expected_extent);
  if (kind == 0u)
    write_u64_le(root, UINT64_C(2));
  else {
    write_u32_le(root + byte_offset, UINT32_MAX);
  }
  if (!capture_raw_pending(runtime, mailbox_id, &before)) {
    memcpy(root, original, expected_extent);
    return 0;
  }
  token_before = *token;
  initialize_call_info(&info);
  result = al_mailbox_resume_text(runtime, mailbox_id, token, empty, 0u, &info);
  if (!capture_raw_pending(runtime, mailbox_id, &after) ||
      !get_view(runtime, mailbox_id, &view)) {
    memcpy(root, original, expected_extent);
    return 0;
  }
  if (result != AL_MAILBOX_INVALID_REFERENCE ||
      memcmp(&before, &after, sizeof(before)) != 0 || view.pending == 0u ||
      memcmp(token, &token_before, sizeof(token_before)) != 0) {
    memcpy(root, original, expected_extent);
    return 0;
  }
  memcpy(root, original, expected_extent);
  return 1;
}

static int module_layout_valid(const al_owning_mailbox_module *module) {
  const al_owning_layout *layout;
  const al_owning_type_descriptor *state;
  const al_owning_type_descriptor *continuation;
  const al_owning_type_descriptor *chunk;
  const al_owning_type_descriptor *option;
  const al_owning_type_descriptor *result;
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
      chunk_index >= module->layout->type_count ||
      option_index >= module->layout->type_count ||
      result_index >= module->layout->type_count)
    return 0;
  layout = module->layout;
  state = &layout->types[state_index];
  continuation = &layout->types[continuation_index];
  chunk = &layout->types[chunk_index];
  option = &layout->types[option_index];
  result = &layout->types[result_index];
  if (state->kind != AL_OWNING_TYPE_RECORD || state->field_count != 1u ||
      state->first_field >= layout->field_count ||
      layout->fields[state->first_field].child_type_index != result_index ||
      continuation->kind != AL_OWNING_TYPE_RECORD ||
      continuation->field_count != 1u ||
      continuation->first_field >= layout->field_count ||
      layout->fields[continuation->first_field].child_type_index != option_index ||
      chunk->kind != AL_OWNING_TYPE_RECORD || chunk->field_count != 1u ||
      chunk->first_field >= layout->field_count ||
      layout->fields[chunk->first_field].child_type_index != string_index ||
      layout->types[string_index].kind != AL_OWNING_TYPE_STRING ||
      option->kind != AL_OWNING_TYPE_OPTION || option->case_count != 2u ||
      option->field_count != 2u || option->first_field >= layout->field_count ||
      option->field_count > layout->field_count - option->first_field ||
      layout->fields[option->first_field].child_type_index != chunk_index ||
      layout->fields[option->first_field].fixed_offset_bytes != 8u ||
      layout->fields[option->first_field + 1u].child_type_index !=
          AL_OWNING_LAYOUT_DYNAMIC_U32 ||
      layout->fields[option->first_field + 1u].fixed_offset_bytes != 8u ||
      result->kind != AL_OWNING_TYPE_RESULT || result->case_count != 2u ||
      result->field_count != 2u || result->first_field + 1u >= layout->field_count ||
      layout->fields[result->first_field].child_type_index != chunk_index ||
      layout->fields[result->first_field + 1u].child_type_index != string_index)
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

static int external_failure_preserves_sentinels(
    uint32_t type_index, const al_owning_layout *layout,
    const uint8_t *source, uint32_t source_length) {
  direct_fixture fixture;
  uint32_t payload = UINT32_C(0x12345678);
  uint32_t extent = UINT32_C(0x87654321);
  int32_t result;
  setup_direct(&fixture);
  result = al_owning_measure_external_value(
      &fixture.context, layout, type_index, source, source_length, 0u, 0u,
      &payload, &extent);
  return result != 0 && payload == UINT32_C(0x12345678) &&
         extent == UINT32_C(0x87654321);
}

static int direct_malformed_checks(const al_owning_mailbox_module *module) {
  static const uint8_t valid_state[24] = {
      0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u,
      3u, 0u, 0u, 0u, 0u, 0u, 0u, 0u,
      0x41u, 0u, 0x3du, 0xd8u, 0x42u, 0xdeu, 0u, 0u};
  uint8_t malformed[24];
  al_owning_type_descriptor *types = NULL;
  al_owning_field_descriptor *fields = NULL;
  al_owning_layout invalid_layout;
  al_owning_mailbox_module invalid_module;
  al_mailbox_owning_config config;
  al_mailbox_owning_storage_requirements requirements;
  al_mailbox_owning_storage_requirements requirements_before;
  direct_fixture fixture;
  uint32_t payload = UINT32_C(0x13579bdf);
  uint32_t extent = UINT32_C(0x2468ace0);
  int scan_result;
  int controller_result;
  int okay;
  setup_direct(&fixture);
  scan_result = al_owning_measure_external_value(
      &fixture.context, module->layout, state_index, valid_state,
      (uint32_t)sizeof(valid_state), 0u, 0u, &payload, &extent);
  okay = scan_result == 0 && payload == 22u && extent == 24u;
  memcpy(malformed, valid_state, sizeof(malformed));
  write_u64_le(malformed, UINT64_C(2));
  okay = okay && external_failure_preserves_sentinels(
                     state_index, module->layout, malformed,
                     (uint32_t)sizeof(malformed));
  memcpy(malformed, valid_state, sizeof(malformed));
  write_u32_le(malformed + 8u, UINT32_MAX);
  okay = okay && external_failure_preserves_sentinels(
                     state_index, module->layout, malformed,
                     (uint32_t)sizeof(malformed));

  types = (al_owning_type_descriptor *)malloc(
      (size_t)module->layout->type_count * sizeof(*types));
  fields = (al_owning_field_descriptor *)malloc(
      (size_t)module->layout->field_count * sizeof(*fields));
  if (types == NULL || fields == NULL) {
    free(types);
    free(fields);
    return 0;
  }
  memcpy(types, module->layout->types,
         (size_t)module->layout->type_count * sizeof(*types));
  memcpy(fields, module->layout->fields,
         (size_t)module->layout->field_count * sizeof(*fields));
  invalid_layout = *module->layout;
  invalid_layout.types = types;
  invalid_layout.fields = fields;
  fields[types[result_index].first_field + 1u].child_type_index = UINT32_MAX;
  okay = okay && external_failure_preserves_sentinels(
                     state_index, &invalid_layout, valid_state,
                     (uint32_t)sizeof(valid_state));

  invalid_module = *module;
  invalid_module.layout = &invalid_layout;
  memset(&config, 0, sizeof(config));
  config.control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  config.struct_size = (uint32_t)sizeof(config);
  config.mailbox_capacity = 1u;
  config.scratch_byte_capacity = SCRATCH_BYTES;
  config.retained_byte_capacity = 64u;
  config.text_staging_byte_capacity = STAGING_BYTES;
  config.scratch_slot_capacity = 1u;
  config.suspension_policy = AL_MAILBOX_OWNING_POLICY_RETURN;
  memset(&requirements, 0xa5, sizeof(requirements));
  requirements_before = requirements;
  controller_result = al_mailbox_get_owning_storage_requirements(
      &invalid_module, &config, &requirements);
  okay = okay && controller_result == AL_MAILBOX_INVALID_MODULE &&
         memcmp(&requirements, &requirements_before, sizeof(requirements)) == 0;
  free(fields);
  free(types);
  return okay;
}

static int invalid_tag_or_payload_preserved(
    al_mailbox_runtime *runtime, uint32_t mailbox_id, uint32_t policy,
    const al_mailbox_token *token, uint32_t kind) {
  return malformed_resume_preserves_pending(runtime, mailbox_id, policy,
                                             token, kind);
}

static int run_unicode(al_mailbox_runtime *runtime, uint32_t policy,
                       policy_evidence *evidence) {
  static const uint8_t seed[] = {0x41u, 0xf0u, 0x9fu, 0x99u, 0x82u};
  static const uint8_t chunk[] = {0xceu, 0xb4u};
  static const uint8_t fail_message[] = {'F', 'A', 'I', 'L'};
  static const uint8_t final_message[] = {0u, 0xf0u, 0x9fu, 0x9au, 0x80u};
  al_mailbox_call_info info;
  al_mailbox_token token;
  al_mailbox_token token_before;
  al_mailbox_result result;
  raw_pending_snapshot raw_before;
  raw_pending_snapshot raw_after;
  initialize_call_info(&info);
  result = al_mailbox_init_text(runtime, 0u, seed, (uint32_t)sizeof(seed),
                                &info);
  if (result != AL_MAILBOX_OK ||
      !capture_bank(runtime, 0u, &evidence->unicode_initialize))
    return 0;
  initialize_call_info(&info);
  result = al_mailbox_begin_text(runtime, 0u, chunk, (uint32_t)sizeof(chunk),
                                 &token, &info);
  if (result != AL_MAILBOX_OK)
    return 0;
  capture_policy_bank_and_associated(
      runtime, 0u, policy, &evidence->unicode_suspended_bank,
      &evidence->unicode_suspended_associated);
  evidence->unicode_malformed_tag_rejected =
      invalid_tag_or_payload_preserved(runtime, 0u, policy, &token, 0u);
  evidence->unicode_malformed_payload_rejected =
      invalid_tag_or_payload_preserved(runtime, 0u, policy, &token, 1u);
  if (!capture_logical(runtime, 0u, policy,
                       &evidence->unicode_before_failure) ||
      !capture_raw_pending(runtime, 0u, &raw_before))
    return 0;
  token_before = token;
  initialize_call_info(&info);
  result = al_mailbox_resume_text(runtime, 0u, &token, fail_message,
                                  (uint32_t)sizeof(fail_message), &info);
  evidence->unicode_fail_status = (uint32_t)result;
  evidence->unicode_failure_preserved =
      result == AL_MAILBOX_HANDLER_FAILURE &&
      capture_logical(runtime, 0u, policy, &evidence->unicode_after_failure) &&
      capture_raw_pending(runtime, 0u, &raw_after) &&
      snapshot_equal(&evidence->unicode_before_failure,
                     &evidence->unicode_after_failure) &&
      memcmp(&raw_before, &raw_after, sizeof(raw_before)) == 0 &&
      memcmp(&token_before, &token, sizeof(token_before)) == 0;
  initialize_call_info(&info);
  result = al_mailbox_resume_text(runtime, 0u, &token, final_message,
                                  (uint32_t)sizeof(final_message), &info);
  return result == AL_MAILBOX_OK &&
         capture_bank(runtime, 0u, &evidence->unicode_final);
}

static int run_error_round_trip(al_mailbox_runtime *runtime, uint32_t policy,
                                policy_evidence *evidence) {
  static const uint8_t seed[] = {'Z'};
  static const uint8_t error_message[] = {'E', 'R', 'R'};
  static const uint8_t empty[1] = {0u};
  al_mailbox_call_info info;
  al_mailbox_token token;
  al_mailbox_result result;
  initialize_call_info(&info);
  result = al_mailbox_init_text(runtime, 1u, seed, (uint32_t)sizeof(seed),
                                &info);
  if (result != AL_MAILBOX_OK ||
      !capture_bank(runtime, 1u, &evidence->empty_initialize))
    return 0;
  initialize_call_info(&info);
  result = al_mailbox_begin_text(runtime, 1u, empty, 0u, &token, &info);
  if (result != AL_MAILBOX_OK)
    return 0;
  capture_policy_bank_and_associated(runtime, 1u, policy,
                                     &evidence->empty_begin_bank,
                                     &evidence->empty_begin_associated);
  initialize_call_info(&info);
  result = al_mailbox_resume_text(runtime, 1u, &token, error_message,
                                  (uint32_t)sizeof(error_message), &info);
  if (result != AL_MAILBOX_OK ||
      !capture_bank(runtime, 1u, &evidence->empty_error_final))
    return 0;
  evidence->empty_error_root = evidence->empty_error_final;

  initialize_call_info(&info);
  result = al_mailbox_begin_text(runtime, 1u, empty, 0u, &token, &info);
  if (result != AL_MAILBOX_OK)
    return 0;
  capture_policy_bank_and_associated(
      runtime, 1u, policy, &evidence->empty_error_to_ok_begin_bank,
      &evidence->empty_error_to_ok_begin_associated);
  initialize_call_info(&info);
  result = al_mailbox_resume_text(runtime, 1u, &token, empty, 0u, &info);
  evidence->error_round_trip_ok =
      result == AL_MAILBOX_OK &&
      capture_bank(runtime, 1u, &evidence->empty_error_to_ok_final) &&
      evidence->empty_error_to_ok_final.root_count == 1u &&
      snapshot_matches_root(&evidence->empty_error_to_ok_final, 0u,
                            loaded_module->layout->types[state_index].type_id,
                            0u, 22u, 24u, hex_ok_err);
  return evidence->error_round_trip_ok != 0u;
}

static int run_capacity(al_mailbox_runtime *runtime, uint32_t policy,
                        policy_evidence *evidence) {
  static const uint8_t long_text[] = {'1', '2', '3', '4', '5', '6', '7'};
  static const uint8_t empty[1] = {0u};
  al_mailbox_call_info info;
  al_mailbox_token token;
  al_mailbox_token token_before;
  al_mailbox_result result;
  al_mailbox_owning_state_view view;
  raw_pending_snapshot raw_before;
  raw_pending_snapshot raw_after;
  initialize_call_info(&info);
  result = al_mailbox_init_text(runtime, 0u, empty, 0u, &info);
  if (result != AL_MAILBOX_OK ||
      !capture_bank(runtime, 0u, &evidence->capacity_initialize))
    return 0;
  initialize_call_info(&info);
  result = al_mailbox_begin_text(runtime, 0u, empty, 0u, &token, &info);
  if (result != AL_MAILBOX_OK)
    return 0;
  capture_policy_bank_and_associated(runtime, 0u, policy,
                                     &evidence->capacity_begin_bank,
                                     &evidence->capacity_begin_associated);
  if (!capture_logical(runtime, 0u, policy,
                       &evidence->capacity_before_failure) ||
      !capture_raw_pending(runtime, 0u, &raw_before))
    return 0;
  token_before = token;
  initialize_call_info(&info);
  result = al_mailbox_resume_text(runtime, 0u, &token, long_text,
                                  (uint32_t)sizeof(long_text), &info);
  evidence->capacity_fail_status = (uint32_t)result;
  evidence->capacity_failure_preserved =
      result == AL_MAILBOX_RETAINED_CAPACITY &&
      capture_logical(runtime, 0u, policy, &evidence->capacity_failure) &&
      capture_raw_pending(runtime, 0u, &raw_after) &&
      get_view(runtime, 0u, &view) && view.pending != 0u &&
      snapshot_equal(&evidence->capacity_before_failure,
                     &evidence->capacity_failure) &&
      memcmp(&raw_before, &raw_after, sizeof(raw_before)) == 0 &&
      memcmp(&token, &token_before, sizeof(token)) == 0;
  initialize_call_info(&info);
  result = al_mailbox_resume_text(runtime, 0u, &token, empty, 0u, &info);
  return result == AL_MAILBOX_OK &&
         capture_bank(runtime, 0u, &evidence->capacity_final);
}

static int run_policy(const al_owning_mailbox_module *module,
                      uint32_t policy, policy_evidence *evidence) {
  runtime_fixture main_fixture;
  runtime_fixture capacity_fixture;
  al_mailbox_owning_stats main_stats;
  al_mailbox_owning_stats capacity_stats;
  int okay = 1;
  memset(evidence, 0, sizeof(*evidence));
  memset(&main_fixture, 0, sizeof(main_fixture));
  memset(&capacity_fixture, 0, sizeof(capacity_fixture));
  memset(&main_stats, 0, sizeof(main_stats));
  memset(&capacity_stats, 0, sizeof(capacity_stats));
  if (!create_runtime(module, policy, 64u, MAILBOX_COUNT, &main_fixture))
    return 0;
  okay = run_unicode(main_fixture.runtime, policy, evidence) && okay;
  okay = run_error_round_trip(main_fixture.runtime, policy, evidence) && okay;
  if (!read_stats(main_fixture.runtime, &main_stats))
    okay = 0;
  if (!create_runtime(module, policy, 31u, 1u, &capacity_fixture)) {
    dispose_runtime(&main_fixture);
    return 0;
  }
  okay = run_capacity(capacity_fixture.runtime, policy, evidence) && okay;
  if (!read_stats(capacity_fixture.runtime, &capacity_stats))
    okay = 0;
  add_stats(&evidence->stats, &main_stats);
  add_stats(&evidence->stats, &capacity_stats);
  evidence->move_zero = evidence->stats.move_bytes == 0u;
  dispose_runtime(&capacity_fixture);
  dispose_runtime(&main_fixture);
  return okay;
}

static int validate_policy_evidence(const policy_evidence *evidence,
                                    uint32_t policy) {
  uint32_t state_type_id = loaded_module->layout->types[state_index].type_id;
  uint32_t continuation_type_id =
      loaded_module->layout->types[continuation_index].type_id;
  int keep = policy == AL_MAILBOX_OWNING_POLICY_KEEP_ASSOCIATED;
  const bank_snapshot *unicode_pair =
      keep ? &evidence->unicode_suspended_associated
           : &evidence->unicode_suspended_bank;
  const bank_snapshot *empty_pair =
      keep ? &evidence->empty_begin_associated : &evidence->empty_begin_bank;
  const bank_snapshot *error_followup_pair =
      keep ? &evidence->empty_error_to_ok_begin_associated
           : &evidence->empty_error_to_ok_begin_bank;
  const bank_snapshot *capacity_pair =
      keep ? &evidence->capacity_begin_associated
           : &evidence->capacity_begin_bank;
  int okay =
      snapshot_matches_root(&evidence->unicode_initialize, 0u, state_type_id,
                            0u, 22u, 24u, hex_unicode_initialize) &&
      evidence->unicode_suspended_bank.pending == 1u &&
      evidence->unicode_suspended_bank.root_count == (keep ? 1u : 2u) &&
      snapshot_matches_root(&evidence->unicode_suspended_bank, 0u,
                            state_type_id, 0u, 22u, 24u,
                            hex_unicode_initialize) &&
      (keep ? snapshot_matches_root(&evidence->unicode_suspended_associated,
                                   1u, continuation_type_id, 24u, 18u, 24u,
                                   hex_continuation_some_delta)
            : evidence->unicode_suspended_associated.root_count == 0u) &&
      evidence->unicode_suspended_associated.pending == 1u &&
      evidence->unicode_suspended_associated.root_count == (keep ? 2u : 0u) &&
      snapshot_matches_root(unicode_pair, 0u, state_type_id, 0u, 22u, 24u,
                            hex_unicode_initialize) &&
      snapshot_matches_root(unicode_pair, 1u, continuation_type_id, 24u,
                            18u, 24u, hex_continuation_some_delta) &&
      evidence->unicode_before_failure.pending == 1u &&
      evidence->unicode_before_failure.root_count == 2u &&
      snapshot_matches_root(&evidence->unicode_before_failure, 0u,
                            state_type_id, 0u, 22u, 24u,
                            hex_unicode_initialize) &&
      snapshot_matches_root(&evidence->unicode_before_failure, 1u,
                            continuation_type_id, 24u, 18u, 24u,
                            hex_continuation_some_delta) &&
      evidence->unicode_after_failure.pending == 1u &&
      evidence->unicode_after_failure.root_count == 2u &&
      snapshot_matches_root(&evidence->unicode_after_failure, 0u,
                            state_type_id, 0u, 22u, 24u,
                            hex_unicode_initialize) &&
      snapshot_matches_root(&evidence->unicode_after_failure, 1u,
                            continuation_type_id, 24u, 18u, 24u,
                            hex_continuation_some_delta) &&
      evidence->unicode_failure_preserved != 0u &&
      evidence->unicode_fail_status == AL_MAILBOX_HANDLER_FAILURE &&
      evidence->unicode_malformed_tag_rejected != 0u &&
      evidence->unicode_malformed_payload_rejected != 0u &&
      snapshot_matches_root(&evidence->unicode_final, 0u, state_type_id, 0u,
                            30u, 32u, hex_unicode_final) &&
      evidence->unicode_final.pending == 0u &&
      snapshot_matches_root(&evidence->empty_initialize, 0u, state_type_id,
                            0u, 18u, 24u, hex_z_initialize) &&
      evidence->empty_begin_bank.pending == 1u &&
      evidence->empty_begin_bank.root_count == (keep ? 1u : 2u) &&
      evidence->empty_begin_associated.pending == 1u &&
      evidence->empty_begin_associated.root_count == (keep ? 2u : 0u) &&
      snapshot_matches_root(empty_pair, 0u, state_type_id, 0u, 18u, 24u,
                            hex_z_initialize) &&
      snapshot_matches_root(empty_pair, 1u, continuation_type_id, 24u,
                            8u, 8u,
                            hex_continuation_none) &&
      snapshot_matches_root(&evidence->empty_error_final, 0u, state_type_id,
                            0u, 22u, 24u, hex_error_err) &&
      evidence->empty_error_to_ok_begin_bank.root_count == (keep ? 1u : 2u) &&
      evidence->empty_error_to_ok_begin_bank.pending == 1u &&
      evidence->empty_error_to_ok_begin_associated.pending == 1u &&
      evidence->empty_error_to_ok_begin_associated.root_count == (keep ? 2u : 0u) &&
      snapshot_matches_root(error_followup_pair, 0u, state_type_id, 0u, 22u,
                            24u, hex_error_err) &&
      snapshot_matches_root(error_followup_pair, 1u, continuation_type_id,
                            24u, 8u, 8u, hex_continuation_none) &&
      snapshot_matches_root(&evidence->empty_error_to_ok_final, 0u,
                            state_type_id, 0u, 22u, 24u, hex_ok_err) &&
      evidence->empty_error_to_ok_final.pending == 0u &&
      evidence->error_round_trip_ok != 0u &&
      snapshot_matches_root(&evidence->capacity_initialize, 0u, state_type_id,
                            0u, 16u, 16u, hex_capacity_empty) &&
      evidence->capacity_begin_bank.pending == 1u &&
      evidence->capacity_begin_bank.root_count == (keep ? 1u : 2u) &&
      evidence->capacity_begin_associated.pending == 1u &&
      evidence->capacity_begin_associated.root_count == (keep ? 2u : 0u) &&
      snapshot_matches_root(capacity_pair, 0u, state_type_id, 0u, 16u, 16u,
                            hex_capacity_empty) &&
      snapshot_matches_root(capacity_pair, 1u, continuation_type_id, 16u,
                            8u, 8u, hex_continuation_none) &&
      evidence->capacity_fail_status == AL_MAILBOX_RETAINED_CAPACITY &&
      evidence->capacity_failure_preserved != 0u &&
      evidence->capacity_before_failure.pending == 1u &&
      evidence->capacity_before_failure.root_count == 2u &&
      snapshot_matches_root(&evidence->capacity_before_failure, 0u,
                            state_type_id, 0u, 16u, 16u,
                            hex_capacity_empty) &&
      snapshot_matches_root(&evidence->capacity_before_failure, 1u,
                            continuation_type_id, 16u, 8u, 8u,
                            hex_continuation_none) &&
      evidence->capacity_failure.pending == 1u &&
      evidence->capacity_failure.root_count == 2u &&
      snapshot_matches_root(&evidence->capacity_failure, 0u, state_type_id,
                            0u, 16u, 16u, hex_capacity_empty) &&
      snapshot_matches_root(&evidence->capacity_failure, 1u,
                            continuation_type_id, 16u, 8u, 8u,
                            hex_continuation_none) &&
      snapshot_matches_root(&evidence->capacity_final, 0u, state_type_id,
                            0u, 16u, 16u, hex_capacity_empty) &&
      evidence->capacity_final.pending == 0u && evidence->move_zero != 0u;
  return okay;
}

static void print_snapshot_json(const bank_snapshot *snapshot) {
  uint32_t index;
  printf("{\"pending\":%s,\"rootCount\":%u,\"usedBytes\":%u,\"roots\":[",
         snapshot->pending != 0u ? "true" : "false", snapshot->root_count,
         snapshot->used_bytes);
  for (index = 0u; index < snapshot->root_count; ++index) {
    const root_snapshot *root = &snapshot->roots[index];
    if (index != 0u)
      printf(",");
    printf("{\"typeId\":%u,\"offsetBytes\":%u,\"extentBytes\":%u,\"payloadBytes\":%u,\"ownerEndBytes\":%u,\"serializedHex\":\"%s\"}",
           root->type_id, root->offset_bytes, root->extent_bytes,
           root->payload_bytes, root->owner_end_bytes, root->serialized_hex);
  }
  printf("]}");
}

static void print_profile_stats_json(const profile_stats *stats) {
  printf("{\"utf8InputBytes\":%" PRIu64 ",\"utf16StagingBytes\":%" PRIu64 ",\"inputImportBytes\":%" PRIu64 ",\"publicationCopyBytes\":%" PRIu64 ",\"beginPublicationCopyBytes\":%" PRIu64 ",\"resumeRootImportBytes\":%" PRIu64 ",\"deepCopyBytes\":%" PRIu64 ",\"moveBytes\":%" PRIu64 ",\"returnedOutputDescriptors\":%" PRIu64 ",\"handlerInvocations\":%" PRIu64 ",\"handlerFailures\":%" PRIu64 "}",
         stats->utf8_input_bytes, stats->utf16_staging_bytes,
         stats->input_import_bytes, stats->publication_copy_bytes,
         stats->begin_publication_copy_bytes,
         stats->resume_root_import_bytes, stats->deep_copy_bytes,
         stats->move_bytes, stats->returned_output_descriptors,
         stats->handler_invocations, stats->handler_failures);
}

static void print_unicode_json(const policy_evidence *evidence) {
  printf("{\"initialize\":");
  print_snapshot_json(&evidence->unicode_initialize);
  printf(",\"suspendedBank\":");
  print_snapshot_json(&evidence->unicode_suspended_bank);
  printf(",\"suspendedAssociated\":");
  print_snapshot_json(&evidence->unicode_suspended_associated);
  printf(",\"beforeFailure\":");
  print_snapshot_json(&evidence->unicode_before_failure);
  printf(",\"afterFailure\":");
  print_snapshot_json(&evidence->unicode_after_failure);
  printf(",\"final\":");
  print_snapshot_json(&evidence->unicode_final);
  printf(",\"failStatus\":%u,\"failurePreserved\":%s,\"malformedTagRejected\":%s,\"malformedPayloadRejected\":%s}",
         evidence->unicode_fail_status,
         evidence->unicode_failure_preserved ? "true" : "false",
         evidence->unicode_malformed_tag_rejected ? "true" : "false",
         evidence->unicode_malformed_payload_rejected ? "true" : "false");
}

static void print_empty_json(const policy_evidence *evidence) {
  printf("{\"initialize\":");
  print_snapshot_json(&evidence->empty_initialize);
  printf(",\"beginBank\":");
  print_snapshot_json(&evidence->empty_begin_bank);
  printf(",\"beginAssociated\":");
  print_snapshot_json(&evidence->empty_begin_associated);
  printf(",\"final\":");
  print_snapshot_json(&evidence->empty_error_final);
  printf(",\"errorRoot\":");
  print_snapshot_json(&evidence->empty_error_root);
  printf(",\"errorToOkBeginBank\":");
  print_snapshot_json(&evidence->empty_error_to_ok_begin_bank);
  printf(",\"errorToOkBeginAssociated\":");
  print_snapshot_json(&evidence->empty_error_to_ok_begin_associated);
  printf(",\"errorToOkFinal\":");
  print_snapshot_json(&evidence->empty_error_to_ok_final);
  printf(",\"errorRoundTripOk\":%s}",
         evidence->error_round_trip_ok ? "true" : "false");
}

static void print_capacity_json(const policy_evidence *evidence) {
  printf("{\"initialize\":");
  print_snapshot_json(&evidence->capacity_initialize);
  printf(",\"beginBank\":");
  print_snapshot_json(&evidence->capacity_begin_bank);
  printf(",\"beginAssociated\":");
  print_snapshot_json(&evidence->capacity_begin_associated);
  printf(",\"beforeFailure\":");
  print_snapshot_json(&evidence->capacity_before_failure);
  printf(",\"failure\":");
  print_snapshot_json(&evidence->capacity_failure);
  printf(",\"final\":");
  print_snapshot_json(&evidence->capacity_final);
  printf(",\"failureResult\":%u,\"failurePreserved\":%s}",
         evidence->capacity_fail_status,
         evidence->capacity_failure_preserved ? "true" : "false");
}

static void print_checks_json(void) {
  size_t index;
  printf("[");
  for (index = 0u; index < check_count; ++index) {
    if (index != 0u)
      printf(",");
    printf("{\"name\":\"%s\",\"passed\":%s}", checks[index].name,
           checks[index].passed ? "true" : "false");
  }
  printf("]");
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

static void print_policy_json(const policy_evidence *evidence) {
  printf("{\"unicode\":");
  print_unicode_json(evidence);
  printf(",\"empty\":");
  print_empty_json(evidence);
  printf(",\"capacity\":");
  print_capacity_json(evidence);
  printf(",\"stats\":");
  print_profile_stats_json(&evidence->stats);
  printf("}");
}

int main(int argc, char **argv) {
  HMODULE library = NULL;
  al_owning_mailbox_module_fn get_module;
  const al_owning_mailbox_module *module = NULL;
  policy_evidence return_evidence;
  policy_evidence associated_evidence;
  int api_valid = 0;
  int return_ok = 0;
  int associated_ok = 0;
  int scanner_ok = 0;
  int profile_ok = 0;
  int abi_ok = 0;
  int empty_final_ok = 0;
  uint32_t stack_abi = AL_OWNING_STACK_ABI_VERSION;

  if (argc != 8) {
    fprintf(stderr,
            "Usage: native-owning-mailbox-sums <module.dll> <StateIndex> <ContinuationIndex> <StringIndex> <ChunkIndex> <OptionIndex> <ResultIndex>\n");
    return 2;
  }
  api_valid = parse_index(argv[2], &state_index) &&
              parse_index(argv[3], &continuation_index) &&
              parse_index(argv[4], &string_index) &&
              parse_index(argv[5], &chunk_index) &&
              parse_index(argv[6], &option_index) &&
              parse_index(argv[7], &result_index);
  library = LoadLibraryA(argv[1]);
  if (library != NULL) {
    get_module = (al_owning_mailbox_module_fn)(uintptr_t)GetProcAddress(
        library, "agentlang_owning_mailbox_module");
    if (get_module != NULL)
      module = get_module();
  }
  loaded_module = module;
  api_valid = api_valid && module_layout_valid(module);
  if (api_valid) {
    return_ok = run_policy(module, AL_MAILBOX_OWNING_POLICY_RETURN,
                           &return_evidence) &&
                validate_policy_evidence(&return_evidence,
                                         AL_MAILBOX_OWNING_POLICY_RETURN);
    associated_ok = run_policy(module,
                               AL_MAILBOX_OWNING_POLICY_KEEP_ASSOCIATED,
                               &associated_evidence) &&
                    validate_policy_evidence(
                        &associated_evidence,
                        AL_MAILBOX_OWNING_POLICY_KEEP_ASSOCIATED);
    scanner_ok = direct_malformed_checks(module);
    profile_ok = return_ok && associated_ok;
    empty_final_ok = return_evidence.empty_error_final.root_count == 1u &&
                     associated_evidence.empty_error_final.root_count == 1u &&
                     return_evidence.empty_error_to_ok_final.root_count == 1u &&
                     associated_evidence.empty_error_to_ok_final.root_count == 1u;
    abi_ok = module->abi_version == AL_OWNING_MAILBOX_ABI_VERSION &&
             module->layout->abi_version == AL_OWNING_LAYOUT_ABI_VERSION &&
             stack_abi == 1u && sizeof(al_owning_type_descriptor) == 36u &&
             sizeof(al_owning_field_descriptor) == 16u &&
             sizeof(al_owning_layout) == 40u;
  } else {
    memset(&return_evidence, 0, sizeof(return_evidence));
    memset(&associated_evidence, 0, sizeof(associated_evidence));
  }

  record_check(0u, api_valid && profile_ok);
  record_check(1u, return_ok && associated_ok);
   record_check(2u, return_evidence.unicode_before_failure.root_count == 2u &&
                        associated_evidence.unicode_before_failure.root_count == 2u &&
                        return_evidence.unicode_before_failure.roots[1].type_id ==
                            module->layout->types[continuation_index].type_id &&
                        associated_evidence.unicode_before_failure.roots[1].type_id ==
                            module->layout->types[continuation_index].type_id);
  record_check(3u, return_evidence.unicode_final.root_count == 1u &&
                       associated_evidence.unicode_final.root_count == 1u &&
                       empty_final_ok &&
                       return_evidence.capacity_final.root_count == 1u &&
                       associated_evidence.capacity_final.root_count == 1u);
  record_check(4u, return_evidence.error_round_trip_ok != 0u &&
                       associated_evidence.error_round_trip_ok != 0u);
  record_check(5u, return_evidence.unicode_failure_preserved != 0u &&
                       associated_evidence.unicode_failure_preserved != 0u);
  record_check(6u, return_evidence.capacity_failure_preserved != 0u &&
                       associated_evidence.capacity_failure_preserved != 0u);
  record_check(7u, scanner_ok);
  record_check(8u, return_evidence.unicode_malformed_tag_rejected != 0u &&
                       return_evidence.unicode_malformed_payload_rejected != 0u &&
                       associated_evidence.unicode_malformed_tag_rejected != 0u &&
                       associated_evidence.unicode_malformed_payload_rejected != 0u);
  record_check(9u, scanner_ok);
  record_check(10u, return_evidence.move_zero != 0u &&
                        associated_evidence.move_zero != 0u);
  record_check(11u, abi_ok);

  printf("{\"passed\":%s,\"failureCount\":%u,\"checks\":",
         failure_count == 0u ? "true" : "false", failure_count);
  print_checks_json();
  printf(",\"abi\":{\"moduleAbiVersion\":%u,\"moduleStructSizeBytes\":%u,\"layoutAbiVersion\":%u,\"stackAbiVersion\":%u,\"typeDescriptorSizeBytes\":%u,\"fieldDescriptorSizeBytes\":%u,\"layoutDescriptorSizeBytes\":%u},\"policies\":{\"RETURN\":",
         module != NULL ? module->abi_version : 0u,
         (uint32_t)sizeof(al_owning_mailbox_module),
         module != NULL && module->layout != NULL
             ? module->layout->abi_version
             : 0u,
         stack_abi, (uint32_t)sizeof(al_owning_type_descriptor),
         (uint32_t)sizeof(al_owning_field_descriptor),
         (uint32_t)sizeof(al_owning_layout));
  print_policy_json(&return_evidence);
  printf(",\"KEEP_ASSOCIATED\":");
  print_policy_json(&associated_evidence);
  printf("}}\n");
  if (library != NULL)
    FreeLibrary(library);
  return failure_count == 0u ? 0 : 1;
}
