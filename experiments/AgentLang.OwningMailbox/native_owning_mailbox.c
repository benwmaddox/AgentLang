#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <inttypes.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "mailbox_runtime.h"

enum {
  EXPECTED_CONTINUATION_TYPE_ID = 4u,
  EXPECTED_STATE_TYPE_ID = 5u,
  EXPECTED_STRING_TYPE_ID = 6u,
  EXPECTED_LAYOUT_TYPE_COUNT = 5u,
  EXPECTED_CONTINUATION_INDEX = 2u,
  EXPECTED_STATE_INDEX = 3u,
  EXPECTED_STRING_INDEX = 4u,
  SMALL_BYTES = 64u,
  DIRECT_CAPACITY = 65536u,
  DIRECT_BITMAP_BYTES = DIRECT_CAPACITY / 8u,
  DIRECT_TRACE_EVENTS = 2048u
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
  char serialized_hex[SMALL_BYTES * 2u + 1u];
} root_snapshot;

typedef struct bank_snapshot {
  uint32_t pending;
  uint32_t root_count;
  uint32_t used_bytes;
  root_snapshot roots[AL_OWNING_MAILBOX_MAX_OUTPUTS];
} bank_snapshot;

typedef struct runtime_fixture {
  al_mailbox_runtime *runtime;
  void *storage_allocation;
  void *storage;
  uint64_t storage_bytes;
  al_mailbox_owning_config config;
  al_mailbox_owning_storage_requirements requirements;
} runtime_fixture;

typedef struct direct_buffers {
  al_owning_stack_context context;
  _Alignas(8) uint8_t stack[DIRECT_CAPACITY];
  uint8_t initialized[DIRECT_BITMAP_BYTES];
  uint8_t poisoned[DIRECT_BITMAP_BYTES];
  al_owning_stack_event events[DIRECT_TRACE_EVENTS];
} direct_buffers;

static named_check checks[192];
static size_t check_count;
static unsigned int failure_count;
static al_mailbox_owning_stats boundary_stats;
static al_mailbox_owning_stats scratch_failure_stats;
static al_mailbox_owning_stats large_request_stats;
static al_mailbox_owning_storage_requirements main_requirements;

static void record_check(const char *name, int passed) {
  if (check_count < sizeof(checks) / sizeof(checks[0])) {
    checks[check_count].name = name;
    checks[check_count].passed = passed != 0;
    ++check_count;
  } else {
    ++failure_count;
  }
  if (!passed)
    ++failure_count;
}

#define CHECK(name, condition) record_check((name), (condition))

static uint32_t align8(uint32_t value) {
  return (value + 7u) & ~7u;
}

static void write_u32_le(uint8_t *destination, uint32_t value) {
  destination[0] = (uint8_t)value;
  destination[1] = (uint8_t)(value >> 8u);
  destination[2] = (uint8_t)(value >> 16u);
  destination[3] = (uint8_t)(value >> 24u);
}

static uint32_t encode_expected_string(const uint16_t *units,
                                       uint32_t unit_count, uint8_t *output,
                                       uint32_t output_capacity) {
  uint32_t payload_bytes = 8u + unit_count * 2u;
  uint32_t extent_bytes = align8(payload_bytes);
  uint32_t index;
  if (output == NULL || extent_bytes > output_capacity)
    return 0u;
  memset(output, 0, extent_bytes);
  write_u32_le(output, unit_count);
  for (index = 0u; index < unit_count; ++index) {
    output[8u + index * 2u] = (uint8_t)units[index];
    output[9u + index * 2u] = (uint8_t)(units[index] >> 8u);
  }
  return extent_bytes;
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

static uint64_t fnv1a64(const uint8_t *bytes, uint32_t byte_count) {
  uint64_t value = UINT64_C(1469598103934665603);
  uint32_t index;
  for (index = 0u; index < byte_count; ++index) {
    value ^= bytes[index];
    value *= UINT64_C(1099511628211);
  }
  return value;
}

static void initialize_call_info(al_mailbox_call_info *info) {
  memset(info, 0, sizeof(*info));
  info->control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  info->struct_size = (uint32_t)sizeof(*info);
  info->error_metadata_id = -1;
}

static void initialize_owning_stats(al_mailbox_owning_stats *stats) {
  memset(stats, 0, sizeof(*stats));
  stats->control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  stats->struct_size = (uint32_t)sizeof(*stats);
}

static void initialize_owning_requirements(
    al_mailbox_owning_storage_requirements *requirements) {
  memset(requirements, 0, sizeof(*requirements));
  requirements->control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  requirements->struct_size = (uint32_t)sizeof(*requirements);
}

static int get_owning_stats(al_mailbox_runtime *runtime,
                            al_mailbox_owning_stats *stats) {
  initialize_owning_stats(stats);
  return al_mailbox_get_owning_stats(runtime, stats) == AL_MAILBOX_OK;
}

static int create_runtime(const al_owning_mailbox_module *module,
                          uint32_t mailbox_capacity,
                          uint32_t scratch_capacity,
                          uint32_t retained_capacity,
                          uint32_t staging_capacity,
                          runtime_fixture *fixture, int test_storage_rejection) {
  al_mailbox_result result;
  al_mailbox_runtime *rejected = NULL;
  size_t allocation_bytes;
  if (fixture == NULL)
    return 0;
  memset(fixture, 0, sizeof(*fixture));
  fixture->config.control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  fixture->config.struct_size = (uint32_t)sizeof(fixture->config);
  fixture->config.mailbox_capacity = mailbox_capacity;
  fixture->config.scratch_byte_capacity = scratch_capacity;
  fixture->config.retained_byte_capacity = retained_capacity;
  fixture->config.text_staging_byte_capacity = staging_capacity;
  initialize_owning_requirements(&fixture->requirements);
  result = al_mailbox_get_owning_storage_requirements(
      module, &fixture->config, &fixture->requirements);
  if (result != AL_MAILBOX_OK || fixture->requirements.storage_bytes == 0u ||
      fixture->requirements.storage_alignment == 0u)
    return 0;
  if (fixture->requirements.storage_bytes > SIZE_MAX - 64u)
    return 0;
  allocation_bytes = (size_t)fixture->requirements.storage_bytes + 64u;
  fixture->storage_allocation = VirtualAlloc(
      NULL, allocation_bytes, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
  if (fixture->storage_allocation == NULL)
    return 0;
  fixture->storage = fixture->storage_allocation;
  fixture->storage_bytes = fixture->requirements.storage_bytes;
  if (test_storage_rejection) {
    result = al_mailbox_runtime_init_owning(
        module, &fixture->config, fixture->storage,
        fixture->storage_bytes - 1u, &rejected);
    CHECK("owning runtime rejects one-byte-short caller storage",
          result == AL_MAILBOX_INVALID_STORAGE && rejected == NULL);
    rejected = NULL;
    result = al_mailbox_runtime_init_owning(
        module, &fixture->config, (uint8_t *)fixture->storage + 1u,
        fixture->storage_bytes, &rejected);
    CHECK("owning runtime rejects misaligned caller storage",
          result == AL_MAILBOX_INVALID_STORAGE && rejected == NULL);
  }
  result = al_mailbox_runtime_init_owning(
      module, &fixture->config, fixture->storage, fixture->storage_bytes,
      &fixture->runtime);
  if (result == AL_MAILBOX_OK && fixture->runtime != NULL)
    return 1;
  if (fixture->storage_allocation != NULL) {
    (void)VirtualFree(fixture->storage_allocation, 0u, MEM_RELEASE);
    fixture->storage_allocation = NULL;
    fixture->storage = NULL;
  }
  return 0;
}

static void dispose_runtime(runtime_fixture *fixture) {
  if (fixture == NULL)
    return;
  if (fixture->runtime != NULL) {
    (void)al_mailbox_dispose(fixture->runtime);
    fixture->runtime = NULL;
  }
  if (fixture->storage_allocation != NULL) {
    (void)VirtualFree(fixture->storage_allocation, 0u, MEM_RELEASE);
    fixture->storage_allocation = NULL;
    fixture->storage = NULL;
  }
}

static int get_view(al_mailbox_runtime *runtime, uint32_t mailbox_id,
                    al_mailbox_owning_state_view *view) {
  memset(view, 0, sizeof(*view));
  view->control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  view->struct_size = (uint32_t)sizeof(*view);
  return al_mailbox_get_owning_state_view(runtime, mailbox_id, view) ==
         AL_MAILBOX_OK;
}

static int snapshot_bank(al_mailbox_runtime *runtime, uint32_t mailbox_id,
                         bank_snapshot *snapshot) {
  al_mailbox_owning_state_view view;
  uint32_t index;
  uint32_t expected_offset = 0u;
  if (snapshot == NULL || !get_view(runtime, mailbox_id, &view) ||
      view.bank == NULL || view.bank->bytes == NULL || view.bank->roots == NULL ||
      view.bank->root_count > AL_OWNING_MAILBOX_MAX_OUTPUTS)
    return 0;
  memset(snapshot, 0, sizeof(*snapshot));
  snapshot->pending = view.pending;
  snapshot->root_count = view.bank->root_count;
  snapshot->used_bytes = view.bank->used_bytes;
  for (index = 0u; index < view.bank->root_count; ++index) {
    const al_owning_bank_root *root = &view.bank->roots[index];
    root_snapshot *target = &snapshot->roots[index];
    target->type_id = root->type_id;
    target->offset_bytes = root->offset_bytes;
    target->extent_bytes = root->extent_bytes;
    target->payload_bytes = root->payload_bytes;
    target->owner_end_bytes = root->owner_end_bytes;
    if (root->offset_bytes != expected_offset ||
        root->owner_end_bytes != root->offset_bytes + root->extent_bytes ||
        root->owner_end_bytes > view.bank->used_bytes ||
        root->extent_bytes > SMALL_BYTES)
      return 0;
    hex_encode(view.bank->bytes + root->offset_bytes, root->extent_bytes,
               target->serialized_hex, sizeof(target->serialized_hex));
    expected_offset = root->owner_end_bytes;
  }
  return expected_offset == view.bank->used_bytes;
}

static uint64_t bank_signature(al_mailbox_runtime *runtime,
                               uint32_t mailbox_id) {
  al_mailbox_owning_state_view view;
  uint64_t signature;
  uint32_t index;
  if (!get_view(runtime, mailbox_id, &view) || view.bank == NULL)
    return 0u;
  signature = fnv1a64(view.bank->bytes, view.bank->used_bytes);
  for (index = 0u; index < view.bank->root_count; ++index) {
    const uint8_t *metadata = (const uint8_t *)&view.bank->roots[index];
    uint32_t byte_index;
    for (byte_index = 0u; byte_index < sizeof(al_owning_bank_root);
         ++byte_index) {
      signature ^= metadata[byte_index];
      signature *= UINT64_C(1099511628211);
    }
  }
  signature ^= view.pending;
  return signature;
}

static int check_string_root(const al_owning_byte_bank *bank,
                             uint32_t root_index, uint32_t expected_type_id,
                             const uint16_t *units, uint32_t unit_count) {
  uint8_t expected[SMALL_BYTES];
  uint32_t extent;
  const al_owning_bank_root *root;
  if (bank == NULL || bank->roots == NULL || bank->bytes == NULL ||
      root_index >= bank->root_count)
    return 0;
  extent = encode_expected_string(units, unit_count, expected, sizeof(expected));
  if (extent == 0u)
    return 0;
  root = &bank->roots[root_index];
  return root->type_id == expected_type_id && root->offset_bytes <= bank->used_bytes &&
         root->extent_bytes == extent && root->payload_bytes == 8u + unit_count * 2u &&
         root->owner_end_bytes == root->offset_bytes + extent &&
         root->owner_end_bytes <= bank->used_bytes &&
         memcmp(bank->bytes + root->offset_bytes, expected, extent) == 0;
}

static int check_main_bank(al_mailbox_runtime *runtime, uint32_t mailbox_id,
                           uint32_t pending, uint32_t root_count,
                           const uint16_t *first, uint32_t first_count,
                           const uint16_t *second, uint32_t second_count) {
  al_mailbox_owning_state_view view;
  uint32_t expected_used = 0u;
  uint32_t index;
  if (!get_view(runtime, mailbox_id, &view) || view.bank == NULL ||
      view.state_type_id != EXPECTED_STATE_TYPE_ID ||
      view.continuation_type_id != EXPECTED_CONTINUATION_TYPE_ID ||
      view.pending != pending || view.bank->root_count != root_count)
    return 0;
  if (root_count > 0u) {
    uint32_t extent = align8(8u + first_count * 2u);
    if (!check_string_root(view.bank, 0u, EXPECTED_STATE_TYPE_ID, first,
                           first_count))
      return 0;
    expected_used += extent;
  }
  if (root_count > 1u) {
    uint32_t extent = align8(8u + second_count * 2u);
    if (!check_string_root(view.bank, 1u, EXPECTED_CONTINUATION_TYPE_ID,
                           second, second_count))
      return 0;
    expected_used += extent;
  }
  for (index = 0u; index < root_count; ++index) {
    if (view.bank->roots[index].offset_bytes !=
        (index == 0u ? 0u : view.bank->roots[0].extent_bytes))
      return 0;
  }
  return view.bank->used_bytes == expected_used;
}

static int initialize_module_module(const al_owning_mailbox_module *module) {
  const al_owning_mailbox_entry *initialize;
  const al_owning_mailbox_entry *begin;
  const al_owning_mailbox_entry *resume;
  const al_owning_layout *layout;
  if (module == NULL || module->abi_version != AL_OWNING_MAILBOX_ABI_VERSION ||
      module->struct_size != sizeof(*module) || module->layout == NULL ||
      module->layout->abi_version != AL_OWNING_LAYOUT_ABI_VERSION ||
      module->layout->types == NULL ||
      module->layout->type_count != EXPECTED_LAYOUT_TYPE_COUNT)
    return 0;
  layout = module->layout;
  initialize = &module->entries[0];
  begin = &module->entries[1];
  resume = &module->entries[2];
  if (layout->types[EXPECTED_CONTINUATION_INDEX].type_id !=
          EXPECTED_CONTINUATION_TYPE_ID ||
      layout->types[EXPECTED_STATE_INDEX].type_id != EXPECTED_STATE_TYPE_ID ||
      layout->types[EXPECTED_STRING_INDEX].type_id != EXPECTED_STRING_TYPE_ID ||
      layout->types[EXPECTED_CONTINUATION_INDEX].kind != AL_OWNING_TYPE_RECORD ||
      layout->types[EXPECTED_STATE_INDEX].kind != AL_OWNING_TYPE_RECORD ||
      layout->types[EXPECTED_STRING_INDEX].kind != AL_OWNING_TYPE_STRING)
    return 0;
  return initialize->input_count == 1u && initialize->output_count == 1u &&
         initialize->input_type_indexes[0] == EXPECTED_STRING_INDEX &&
         initialize->output_type_indexes[0] == EXPECTED_STATE_INDEX &&
         begin->input_count == 2u && begin->output_count == 2u &&
         begin->input_type_indexes[0] == EXPECTED_STATE_INDEX &&
         begin->input_type_indexes[1] == EXPECTED_STRING_INDEX &&
         begin->output_type_indexes[0] == EXPECTED_STATE_INDEX &&
         begin->output_type_indexes[1] == EXPECTED_CONTINUATION_INDEX &&
         resume->input_count == 3u && resume->output_count == 1u &&
         resume->input_type_indexes[0] == EXPECTED_STATE_INDEX &&
         resume->input_type_indexes[1] == EXPECTED_CONTINUATION_INDEX &&
         resume->input_type_indexes[2] == EXPECTED_STRING_INDEX &&
         resume->output_type_indexes[0] == EXPECTED_STATE_INDEX &&
         initialize->execute != NULL && begin->execute != NULL &&
         resume->execute != NULL;
}

static int valid_module_roles(const al_owning_mailbox_module *module) {
  return initialize_module_module(module);
}

static int setup_direct(direct_buffers *buffers, uint32_t capacity) {
  if (buffers == NULL || capacity > DIRECT_CAPACITY || capacity == 0u)
    return 0;
  memset(buffers, 0, sizeof(*buffers));
  buffers->context.abi_version = AL_OWNING_STACK_ABI_VERSION;
  buffers->context.stack_capacity_bytes = capacity;
  buffers->context.init_bitmap_bytes =
      capacity / 8u + (capacity % 8u != 0u ? 1u : 0u);
  buffers->context.trace_event_capacity = DIRECT_TRACE_EVENTS;
  buffers->context.stack_data = buffers->stack;
  buffers->context.init_bitmap = buffers->initialized;
  buffers->context.poison_bitmap = buffers->poisoned;
  buffers->context.trace_events = buffers->events;
  al_owning_begin(&buffers->context);
  return buffers->context.status == AL_OWNING_STATUS_OK;
}

static void fill_invalid_outputs(al_owning_bank_stack_slice *outputs,
                                 uint32_t count) {
  memset(outputs, 0xa5, sizeof(*outputs) * count);
}

static int outputs_unchanged(const al_owning_bank_stack_slice *outputs,
                             const al_owning_bank_stack_slice *before,
                             uint32_t count) {
  return memcmp(outputs, before, sizeof(*outputs) * count) == 0;
}

static int outputs_invalidated(const al_owning_bank_stack_slice *outputs,
                               uint32_t count) {
  uint32_t index;
  for (index = 0u; index < count; ++index) {
    if (outputs[index].type_index != UINT32_MAX ||
        outputs[index].source_offset_bytes != 0u ||
        outputs[index].source_owner_end_bytes != 0u ||
        outputs[index].reserved != 0u)
      return 0;
  }
  return 1;
}

static void run_direct_callback_preflight(
    const al_owning_mailbox_module *module) {
  static const uint16_t seed_units[] = {0x0041u, 0xd83du, 0xde42u};
  static const uint16_t chunk_units[] = {0x03b4u};
  uint8_t seed[SMALL_BYTES];
  uint8_t chunk[SMALL_BYTES];
  uint32_t seed_extent = encode_expected_string(seed_units, 3u, seed,
                                                (uint32_t)sizeof(seed));
  uint32_t chunk_extent = encode_expected_string(chunk_units, 1u, chunk,
                                                 (uint32_t)sizeof(chunk));
  al_owning_external_slice input;
  al_owning_external_slice begin_inputs[2];
  al_owning_bank_stack_slice outputs[2];
  al_owning_bank_stack_slice before[2];
  direct_buffers buffers;
  int32_t callback_status;
  DWORD original_protection = 0u;
  DWORD ignored_protection = 0u;

  input.bytes = seed;
  input.extent_bytes = seed_extent;
  input.type_index = EXPECTED_STRING_INDEX;
  fill_invalid_outputs(outputs, 2u);
  memcpy(before, outputs, sizeof(outputs));
  CHECK("direct callback fixture context initializes",
        setup_direct(&buffers, DIRECT_CAPACITY));
  callback_status = module->entries[0].execute(
      &buffers.context, &input, 0u, outputs, 1u);
  CHECK("callback rejects a wrong input count before writes",
        callback_status != 0 &&
            buffers.context.status == AL_OWNING_STATUS_INVALID_REQUEST &&
            outputs_unchanged(outputs, before, 2u));

  CHECK("direct callback context resets between malformed cases",
        setup_direct(&buffers, DIRECT_CAPACITY));
  input.type_index = EXPECTED_STATE_INDEX;
  fill_invalid_outputs(outputs, 2u);
  memcpy(before, outputs, sizeof(outputs));
  callback_status = module->entries[0].execute(
      &buffers.context, &input, 1u, outputs, 1u);
  CHECK("callback rejects an incorrect layout type index before writes",
        callback_status != 0 &&
            buffers.context.status == AL_OWNING_STATUS_INVALID_REQUEST &&
            outputs_unchanged(outputs, before, 2u));
  input.type_index = EXPECTED_STRING_INDEX;

  CHECK("direct callback context resets for an out-of-range input layout index",
        setup_direct(&buffers, DIRECT_CAPACITY));
  input.type_index = UINT32_MAX;
  fill_invalid_outputs(outputs, 2u);
  memcpy(before, outputs, sizeof(outputs));
  callback_status = module->entries[0].execute(
      &buffers.context, &input, 1u, outputs, 1u);
  CHECK("callback rejects out-of-range input index before layout dereference",
        callback_status != 0 &&
            buffers.context.status == AL_OWNING_STATUS_INVALID_REQUEST &&
            buffers.context.input_copy_bytes == 0u &&
            outputs_unchanged(outputs, before, 2u));
  input.type_index = EXPECTED_STRING_INDEX;

  CHECK("direct callback context resets for zero extent",
        setup_direct(&buffers, DIRECT_CAPACITY));
  input.extent_bytes = 0u;
  fill_invalid_outputs(outputs, 2u);
  memcpy(before, outputs, sizeof(outputs));
  callback_status = module->entries[0].execute(
      &buffers.context, &input, 1u, outputs, 1u);
  CHECK("callback rejects zero-length external descriptors before writes",
        callback_status != 0 &&
            buffers.context.status == AL_OWNING_STATUS_INVALID_REQUEST &&
            outputs_unchanged(outputs, before, 2u));
  input.extent_bytes = seed_extent;

  CHECK("direct callback context resets for a truncated extent",
        setup_direct(&buffers, DIRECT_CAPACITY));
  input.extent_bytes = seed_extent - 8u;
  fill_invalid_outputs(outputs, 2u);
  memcpy(before, outputs, sizeof(outputs));
  callback_status = module->entries[0].execute(
      &buffers.context, &input, 1u, outputs, 1u);
  CHECK("callback rejects a truncated serialized value after invalidating requested output",
        callback_status != 0 &&
            buffers.context.status == AL_OWNING_STATUS_INVALID_REQUEST &&
            outputs_invalidated(outputs, 1u) &&
            outputs_unchanged(outputs + 1u, before + 1u, 1u) &&
            buffers.context.cursor_bytes == 0u &&
            buffers.context.input_copy_bytes == 0u &&
            buffers.context.deep_copy_bytes == 0u);
  input.extent_bytes = seed_extent;

  CHECK("direct callback context resets for null source",
        setup_direct(&buffers, DIRECT_CAPACITY));
  input.bytes = NULL;
  fill_invalid_outputs(outputs, 2u);
  memcpy(before, outputs, sizeof(outputs));
  callback_status = module->entries[0].execute(
      &buffers.context, &input, 1u, outputs, 1u);
  CHECK("callback rejects a null source for nonempty input before writes",
        callback_status != 0 &&
            buffers.context.status == AL_OWNING_STATUS_INVALID_REQUEST &&
            outputs_unchanged(outputs, before, 2u));
  input.bytes = seed;

  CHECK("direct callback context resets for output-capacity preflight",
        setup_direct(&buffers, DIRECT_CAPACITY));
  fill_invalid_outputs(outputs, 2u);
  memcpy(before, outputs, sizeof(outputs));
  callback_status = module->entries[0].execute(
      &buffers.context, &input, 1u, outputs, 0u);
  CHECK("callback rejects insufficient output descriptor capacity before writes",
        callback_status != 0 &&
            buffers.context.status == AL_OWNING_STATUS_INVALID_REQUEST &&
            outputs_unchanged(outputs, before, 2u));

  CHECK("direct callback context resets for stack overlap",
        setup_direct(&buffers, DIRECT_CAPACITY));
  input.bytes = buffers.context.stack_data;
  input.extent_bytes = seed_extent;
  fill_invalid_outputs(outputs, 2u);
  memcpy(before, outputs, sizeof(outputs));
  callback_status = module->entries[0].execute(
      &buffers.context, &input, 1u, outputs, 1u);
  CHECK("callback rejects a source overlapping its working arena",
        callback_status != 0 &&
            buffers.context.status == AL_OWNING_STATUS_INVALID_REQUEST &&
            outputs_unchanged(outputs, before, 2u));

  CHECK("direct callback context resets for context overlap",
        setup_direct(&buffers, DIRECT_CAPACITY));
  input.bytes = (const uint8_t *)&buffers.context;
  fill_invalid_outputs(outputs, 2u);
  memcpy(before, outputs, sizeof(outputs));
  callback_status = module->entries[0].execute(
      &buffers.context, &input, 1u, outputs, 1u);
  CHECK("callback rejects a source overlapping context metadata",
        callback_status != 0 &&
            buffers.context.status == AL_OWNING_STATUS_INVALID_REQUEST &&
            outputs_unchanged(outputs, before, 2u));

  CHECK("direct callback context resets for output-table overlap",
        setup_direct(&buffers, DIRECT_CAPACITY));
  input.bytes = (const uint8_t *)outputs;
  fill_invalid_outputs(outputs, 2u);
  memcpy(before, outputs, sizeof(outputs));
  callback_status = module->entries[0].execute(
      &buffers.context, &input, 1u, outputs, 1u);
  CHECK("callback rejects a source overlapping result descriptor metadata",
        callback_status != 0 &&
            buffers.context.status == AL_OWNING_STATUS_INVALID_REQUEST &&
            outputs_unchanged(outputs, before, 2u));

  CHECK("direct callback context resets for descriptor-table aliasing",
        setup_direct(&buffers, DIRECT_CAPACITY));
  fill_invalid_outputs(outputs, 2u);
  memcpy(before, outputs, sizeof(outputs));
  callback_status = module->entries[0].execute(
      &buffers.context, (const al_owning_external_slice *)outputs, 1u,
      outputs, 1u);
  CHECK("callback rejects overlapping input and output descriptor tables before writes",
        callback_status != 0 &&
            buffers.context.status == AL_OWNING_STATUS_INVALID_REQUEST &&
            outputs_unchanged(outputs, before, 2u));

  CHECK("direct callback context resets for output overlap with module metadata",
        setup_direct(&buffers, DIRECT_CAPACITY));
  fill_invalid_outputs(outputs, 2u);
  memcpy(before, module, sizeof(al_owning_bank_stack_slice));
  callback_status = module->entries[0].execute(
      &buffers.context, &input, 1u,
      (al_owning_bank_stack_slice *)(uintptr_t)module, 1u);
  CHECK("callback rejects output descriptor table overlapping module metadata",
        callback_status != 0 &&
            buffers.context.status == AL_OWNING_STATUS_INVALID_REQUEST &&
            memcmp(module, before, sizeof(al_owning_bank_stack_slice)) == 0);

  CHECK("direct callback context resets for overflowing source pointer span",
        setup_direct(&buffers, DIRECT_CAPACITY));
  input.bytes = (const uint8_t *)(uintptr_t)(UINTPTR_MAX - (uintptr_t)7u);
  fill_invalid_outputs(outputs, 2u);
  memcpy(before, outputs, sizeof(outputs));
  callback_status = module->entries[0].execute(
      &buffers.context, &input, 1u, outputs, 1u);
  CHECK("callback rejects an overflowing source range without dereference",
        callback_status != 0 &&
            buffers.context.status == AL_OWNING_STATUS_INVALID_REQUEST &&
            buffers.context.input_copy_bytes == 0u &&
            outputs_unchanged(outputs, before, 2u));
  input.bytes = seed;

  CHECK("generated begin callback rejects corrupted out-of-range role metadata",
        setup_direct(&buffers, DIRECT_CAPACITY));
  begin_inputs[0].bytes = seed;
  begin_inputs[0].extent_bytes = seed_extent;
  begin_inputs[0].type_index = EXPECTED_STATE_INDEX;
  begin_inputs[1].bytes = chunk;
  begin_inputs[1].extent_bytes = chunk_extent;
  begin_inputs[1].type_index = EXPECTED_STRING_INDEX;
  fill_invalid_outputs(outputs, 2u);
  memcpy(before, outputs, sizeof(outputs));
  if (VirtualProtect((LPVOID)(uintptr_t)module, sizeof(*module), PAGE_READWRITE,
                     &original_protection)) {
    al_owning_mailbox_module *writable_module =
        (al_owning_mailbox_module *)(uintptr_t)module;
    BOOL protection_restored;
    uint32_t init_state = writable_module->entries[0].output_type_indexes[0];
    uint32_t begin_state_in = writable_module->entries[1].input_type_indexes[0];
    uint32_t begin_state_out = writable_module->entries[1].output_type_indexes[0];
    uint32_t resume_state_in = writable_module->entries[2].input_type_indexes[0];
    writable_module->entries[0].output_type_indexes[0] = UINT32_MAX;
    writable_module->entries[1].input_type_indexes[0] = UINT32_MAX;
    writable_module->entries[1].output_type_indexes[0] = UINT32_MAX;
    writable_module->entries[2].input_type_indexes[0] = UINT32_MAX;
    callback_status = module->entries[1].execute(
        &buffers.context, begin_inputs, 2u, outputs, 2u);
    writable_module->entries[0].output_type_indexes[0] = init_state;
    writable_module->entries[1].input_type_indexes[0] = begin_state_in;
    writable_module->entries[1].output_type_indexes[0] = begin_state_out;
    writable_module->entries[2].input_type_indexes[0] = resume_state_in;
    protection_restored = VirtualProtect(
        (LPVOID)(uintptr_t)module, sizeof(*module), original_protection,
        &ignored_protection);
    CHECK("begin role metadata bounds check precedes type-layout dereference",
          callback_status != 0 &&
              buffers.context.status == AL_OWNING_STATUS_INVALID_REQUEST &&
              outputs_unchanged(outputs, before, 2u) &&
              protection_restored != 0 &&
              module->entries[0].output_type_indexes[0] == init_state &&
              module->entries[1].input_type_indexes[0] == begin_state_in &&
              module->entries[1].output_type_indexes[0] == begin_state_out &&
              module->entries[2].input_type_indexes[0] == resume_state_in);
  } else {
    CHECK("begin role metadata bounds check precedes type-layout dereference",
          0);
  }

  CHECK("direct callback context resets for aggregate preflight",
        setup_direct(&buffers, 24u));
  input.bytes = seed;
  input.extent_bytes = seed_extent;
  begin_inputs[0].bytes = seed;
  begin_inputs[0].extent_bytes = seed_extent;
  begin_inputs[0].type_index = EXPECTED_STATE_INDEX;
  begin_inputs[1].bytes = chunk;
  begin_inputs[1].extent_bytes = chunk_extent;
  begin_inputs[1].type_index = EXPECTED_STRING_INDEX;
  fill_invalid_outputs(outputs, 2u);
  memcpy(before, outputs, sizeof(outputs));
  callback_status = module->entries[1].execute(
      &buffers.context, begin_inputs, 2u, outputs, 2u);
  CHECK("callback preflights aggregate input extents before partial import",
        callback_status != 0 &&
            buffers.context.status == AL_OWNING_STATUS_STACK_CAPACITY &&
            buffers.context.input_copy_bytes == 0u &&
            outputs_unchanged(outputs, before, 2u));
}

static int capture_success_snapshot(al_mailbox_runtime *runtime,
                                    uint32_t mailbox_id,
                                    bank_snapshot *snapshot,
                                    const char *check_name) {
  int okay = snapshot_bank(runtime, mailbox_id, snapshot);
  CHECK(check_name, okay);
  return okay;
}

static int expected_stats_after_small_lifecycle(
    const al_mailbox_owning_stats *stats) {
  return stats->initialized_mailboxes == 2u && stats->pending_mailboxes == 0u &&
         stats->live_retained_bytes == 40u && stats->live_retained_roots == 2u &&
         stats->utf8_input_bytes == 13u && stats->utf16_staging_bytes == 80u &&
         stats->input_import_bytes == 152u &&
          stats->publication_copy_bytes == 112u && stats->deep_copy_bytes == 184u &&
          stats->move_bytes == 0u && stats->returned_output_descriptors == 8u &&
          stats->turn_reset_bytes > 0u && stats->scratch_high_water_bytes > 0u &&
         stats->handler_invocations == 6u && stats->handler_failures == 0u &&
         stats->scratch_lease_acquisitions == 6u &&
         stats->scratch_lease_returns == 6u &&
         stats->outstanding_scratch_leases == 0u;
}

static int assert_storage_arithmetic(const runtime_fixture *fixture) {
  uint64_t bank_bytes = (uint64_t)fixture->config.retained_byte_capacity +
                        2u * sizeof(al_owning_bank_root);
  uint64_t expected_retained = bank_bytes * 2u *
                               fixture->config.mailbox_capacity;
  uint64_t expected_bitmap = fixture->config.scratch_byte_capacity / 8u +
                             (fixture->config.scratch_byte_capacity % 8u != 0u);
  uint64_t expected_scratch = fixture->config.scratch_byte_capacity +
                              2u * expected_bitmap;
  uint64_t accounted = fixture->requirements.retained_reserved_bytes +
                       fixture->requirements.scratch_reserved_bytes +
                       fixture->requirements.text_staging_reserved_bytes +
                       fixture->requirements.controller_reserved_bytes;
  return fixture->requirements.mailbox_capacity ==
             fixture->config.mailbox_capacity &&
         fixture->requirements.retained_reserved_bytes == expected_retained &&
         fixture->requirements.scratch_reserved_bytes == expected_scratch &&
         fixture->requirements.text_staging_reserved_bytes ==
             fixture->config.text_staging_byte_capacity &&
         fixture->requirements.controller_reserved_bytes > 0u &&
         fixture->requirements.storage_bytes == accounted;
}

static void run_small_lifecycle(const al_owning_mailbox_module *module,
                                bank_snapshot snapshots[6],
                                al_mailbox_owning_stats *success_stats,
                                runtime_fixture *main_fixture_out) {
  static const uint8_t seed_utf8[] = {0x41u, 0xf0u, 0x9fu, 0x99u, 0x82u};
  static const uint8_t delta_utf8[] = {0xceu, 0xb4u};
  static const uint8_t completion_utf8[] = {0x00u, 0xf0u, 0x9fu, 0x9au, 0x80u};
  static const uint8_t byte_b[] = {0x42u};
  static const uint16_t seed_units[] = {0x0041u, 0xd83du, 0xde42u};
  static const uint16_t delta_units[] = {0x03b4u};
  static const uint16_t completion_units[] = {0x0041u, 0xd83du, 0xde42u,
                                               0x03b4u, 0x0000u, 0xd83du,
                                               0xde80u};
  static const uint16_t empty_units[1] = {0u};
  static const uint8_t empty_utf8[] = "";
  static const uint16_t b_units[] = {0x0042u};
  runtime_fixture fixture;
  al_mailbox_call_info info;
  al_mailbox_token token_a;
  al_mailbox_token token_b;
  al_mailbox_owning_stats stats;
  al_mailbox_result result;

  memset(&token_a, 0, sizeof(token_a));
  memset(&token_b, 0, sizeof(token_b));
  CHECK("main owning runtime uses caller-owned two-mailbox storage",
        create_runtime(module, 2u, 65536u, 16384u, 16384u, &fixture, 1));
  if (fixture.runtime == NULL) {
    dispose_runtime(&fixture);
    return;
  }
  CHECK("main storage requirements match independent backing arithmetic",
        assert_storage_arithmetic(&fixture));

  initialize_call_info(&info);
  result = al_mailbox_init_text(fixture.runtime, 0u, seed_utf8,
                                (uint32_t)sizeof(seed_utf8), &info);
  CHECK("UTF-8 initialize succeeds for mailbox A", result == AL_MAILBOX_OK);
  CHECK("mailbox A publishes source-derived Unicode bytes after scratch reset",
        check_main_bank(fixture.runtime, 0u, 0u, 1u, seed_units, 3u, NULL, 0u));
  capture_success_snapshot(fixture.runtime, 0u, &snapshots[0],
                           "capture mailbox A initialization bank");

  initialize_call_info(&info);
  result = al_mailbox_init_text(fixture.runtime, 1u, empty_utf8, 0u, &info);
  CHECK("empty UTF-8 initialize succeeds for mailbox B",
        result == AL_MAILBOX_OK);
  CHECK("empty root has canonical eight-byte serialization",
        check_main_bank(fixture.runtime, 1u, 0u, 1u, empty_units, 0u, NULL, 0u));
  capture_success_snapshot(fixture.runtime, 1u, &snapshots[1],
                           "capture empty mailbox initialization bank");

  initialize_call_info(&info);
  result = al_mailbox_begin_text(fixture.runtime, 0u, delta_utf8,
                                 (uint32_t)sizeof(delta_utf8), &token_a, &info);
  CHECK("mailbox A begin returns a pending token", result == AL_MAILBOX_OK);
  CHECK("begin publishes two independently typed roots",
        check_main_bank(fixture.runtime, 0u, 1u, 2u, seed_units, 3u,
                        delta_units, 1u));
  capture_success_snapshot(fixture.runtime, 0u, &snapshots[2],
                           "capture mailbox A pending roots");

  initialize_call_info(&info);
  result = al_mailbox_begin_text(fixture.runtime, 1u, empty_utf8, 0u, &token_b, &info);
  CHECK("mailbox B begin accepts an empty continuation",
        result == AL_MAILBOX_OK);
  CHECK("empty continuation remains a typed, standalone root",
        check_main_bank(fixture.runtime, 1u, 1u, 2u, empty_units, 0u,
                        empty_units, 0u));
  capture_success_snapshot(fixture.runtime, 1u, &snapshots[3],
                           "capture mailbox B pending roots");

  initialize_call_info(&info);
  result = al_mailbox_resume_text(fixture.runtime, 0u, &token_a,
                                  completion_utf8,
                                  (uint32_t)sizeof(completion_utf8), &info);
  CHECK("mailbox A resume accepts embedded NUL and four-byte UTF-8",
        result == AL_MAILBOX_OK);
  CHECK("resume bytes equal independently serialized concatenated Unicode",
        check_main_bank(fixture.runtime, 0u, 0u, 1u, completion_units, 7u,
                        NULL, 0u));
  capture_success_snapshot(fixture.runtime, 0u, &snapshots[4],
                           "capture mailbox A completed state");

  initialize_call_info(&info);
  result = al_mailbox_resume_text(fixture.runtime, 1u, &token_b, byte_b,
                                  (uint32_t)sizeof(byte_b), &info);
  CHECK("mailbox B resumes while preserving mailbox A output",
        result == AL_MAILBOX_OK);
  CHECK("mailbox B output is isolated and canonical",
        check_main_bank(fixture.runtime, 1u, 0u, 1u, b_units, 1u, NULL, 0u));
  capture_success_snapshot(fixture.runtime, 1u, &snapshots[5],
                           "capture mailbox B completed state after A scratch reuse");

  CHECK("both active banks survive shared scratch reuse independently",
        check_main_bank(fixture.runtime, 0u, 0u, 1u, completion_units, 7u,
                        NULL, 0u) &&
            check_main_bank(fixture.runtime, 1u, 0u, 1u, b_units, 1u, NULL,
                            0u));
  CHECK("small lifecycle public traffic counters match independent extent sums",
        get_owning_stats(fixture.runtime, &stats) &&
            expected_stats_after_small_lifecycle(&stats));
  if (success_stats != NULL)
    *success_stats = stats;
  if (main_fixture_out != NULL)
    *main_fixture_out = fixture;
  else
    dispose_runtime(&fixture);
}

static void run_utf8_boundaries(const al_owning_mailbox_module *module) {
  static const uint8_t valid0[] = {0x41u};
  static const uint8_t valid1[] = {0xdfu, 0xbfu};
  static const uint8_t valid2[] = {0xeeu, 0x80u, 0x80u};
  static const uint8_t valid3[] = {0xf4u, 0x8fu, 0xbfu, 0xbfu};
  static const uint16_t units0[] = {0x0041u};
  static const uint16_t units1[] = {0x07ffu};
  static const uint16_t units2[] = {0xe000u};
  static const uint16_t units3[] = {0xdbffu, 0xdfffu};
  const uint8_t *inputs[4] = {valid0, valid1, valid2, valid3};
  const uint32_t input_lengths[4] = {1u, 2u, 3u, 4u};
  const uint16_t *units[4] = {units0, units1, units2, units3};
  const uint32_t unit_lengths[4] = {1u, 1u, 1u, 2u};
  runtime_fixture fixture;
  uint32_t index;
  CHECK("UTF-8 boundary runtime initializes caller storage",
        create_runtime(module, 4u, 4096u, 128u, 128u, &fixture, 0));
  if (fixture.runtime == NULL)
    return;
  for (index = 0u; index < 4u; ++index) {
    al_mailbox_call_info info;
    char check_name[64];
    initialize_call_info(&info);
    (void)snprintf(check_name, sizeof(check_name),
                   "valid UTF-8 width %u converts to exact UTF-16 root", index + 1u);
    CHECK(check_name,
          al_mailbox_init_text(fixture.runtime, index, inputs[index],
                               input_lengths[index], &info) == AL_MAILBOX_OK &&
              check_main_bank(fixture.runtime, index, 0u, 1u, units[index],
                              unit_lengths[index], NULL, 0u));
  }
  {
    al_mailbox_owning_stats stats;
  CHECK("UTF-8 boundary cases count canonical imports and publications separately",
          get_owning_stats(fixture.runtime, &stats) &&
              stats.utf8_input_bytes == 10u &&
              stats.utf16_staging_bytes == 64u &&
              stats.input_import_bytes == 64u &&
              stats.publication_copy_bytes == 64u &&
              stats.deep_copy_bytes == 64u && stats.move_bytes == 0u &&
              stats.returned_output_descriptors == 4u);
    boundary_stats = stats;
  }
  dispose_runtime(&fixture);
}

static void run_utf8_failures_and_tokens(
    const al_owning_mailbox_module *module, runtime_fixture *fixture) {
  static const uint8_t overlong[] = {0xc0u, 0xafu};
  static const uint8_t truncated[] = {0xf0u, 0x9fu, 0x92u};
  static const uint8_t surrogate[] = {0xedu, 0xa0u, 0x80u};
  static const uint8_t above_max[] = {0xf4u, 0x90u, 0x80u, 0x80u};
  static const uint8_t continuation[] = {0x80u};
  static const uint8_t replacement[] = {0x72u};
  static const uint8_t x_text[] = {0x78u};
  static const uint8_t y_text[] = {0x79u};
  static const uint16_t completed_units[] = {0x0041u, 0xd83du, 0xde42u,
                                              0x03b4u, 0x0000u, 0xd83du,
                                              0xde80u};
  static const uint16_t b_units[] = {0x0042u};
  static const uint16_t x_units[] = {0x0078u};
  static const uint16_t y_units[] = {0x0079u};
  static const uint16_t second_b_units[] = {0x0042u, 0x0079u, 0x0072u,
                                             0x0079u, 0x0072u};
  static const uint16_t recovered_units[] = {
      0x0041u, 0xd83du, 0xde42u, 0x03b4u, 0x0000u, 0xd83du,
      0xde80u, 0x0078u, 0x0072u};
  al_mailbox_token token_a;
  al_mailbox_token token_b;
  al_mailbox_token token_b_second;
  al_mailbox_token token_b_third;
  al_mailbox_call_info info;
  al_mailbox_owning_stats before_stats;
  al_mailbox_owning_stats after_stats;
  uint64_t before_signature;
  al_mailbox_result result;
  runtime_fixture foreign_runtime;
  static const uint8_t c_text[] = {0x63u};
  static const uint8_t d_text[] = {0x64u};
  static const char *invalid_names[5] = {
      "invalid UTF-8 overlong sequence is rejected before handler execution",
      "invalid UTF-8 truncated sequence is rejected before handler execution",
      "invalid UTF-8 surrogate scalar is rejected before handler execution",
      "invalid UTF-8 above-U+10FFFF scalar is rejected before handler execution",
      "invalid UTF-8 lone continuation is rejected before handler execution"};
  al_mailbox_token token_foreign;
  uint64_t handlers_before_rejections;

  memset(&token_a, 0, sizeof(token_a));
  memset(&token_b, 0, sizeof(token_b));
  memset(&token_b_second, 0, sizeof(token_b_second));
  memset(&token_b_third, 0, sizeof(token_b_third));
  initialize_call_info(&info);
  CHECK("begin after completed A creates a fresh per-mailbox token",
        al_mailbox_begin_text(fixture->runtime, 0u, x_text, sizeof(x_text),
                              &token_a, &info) == AL_MAILBOX_OK);
  initialize_call_info(&info);
  CHECK("begin after completed B creates independent token",
        al_mailbox_begin_text(fixture->runtime, 1u, y_text, sizeof(y_text),
                              &token_b, &info) == AL_MAILBOX_OK);
  before_signature = bank_signature(fixture->runtime, 0u);
  CHECK("snapshot pending A before malformed UTF-8 attempts",
        get_owning_stats(fixture->runtime, &before_stats));
  {
    const uint8_t *invalid_values[5] = {overlong, truncated, surrogate,
                                         above_max, continuation};
    const uint32_t invalid_lengths[5] = {sizeof(overlong), sizeof(truncated),
                                          sizeof(surrogate), sizeof(above_max),
                                          sizeof(continuation)};
    uint32_t index;
    for (index = 0u; index < 5u; ++index) {
      initialize_call_info(&info);
      result = al_mailbox_resume_text(fixture->runtime, 0u, &token_a,
                                      invalid_values[index],
                                      invalid_lengths[index], &info);
       CHECK(invalid_names[index], result == AL_MAILBOX_INVALID_TEXT_ENCODING);
    }
  }
  CHECK("malformed UTF-8 attempts preserve active bank and pending token",
        bank_signature(fixture->runtime, 0u) == before_signature &&
            get_owning_stats(fixture->runtime, &after_stats) &&
            after_stats.handler_invocations == before_stats.handler_invocations &&
            after_stats.pending_mailboxes == before_stats.pending_mailboxes);
  handlers_before_rejections = after_stats.handler_invocations;

  initialize_call_info(&info);
  result = al_mailbox_resume_text(fixture->runtime, 1u, &token_a, replacement,
                                  sizeof(replacement), &info);
  CHECK("same-runtime cross-mailbox token is rejected before dispatch",
        result == AL_MAILBOX_WRONG_OWNER_TOKEN);
  CHECK("wrong-owner token leaves mailbox B pending without handler dispatch",
        check_main_bank(fixture->runtime, 1u, 1u, 2u, b_units, 1u, y_units,
                        1u) &&
            get_owning_stats(fixture->runtime, &after_stats) &&
            after_stats.handler_invocations == handlers_before_rejections);

  before_signature = bank_signature(fixture->runtime, 0u);
  initialize_call_info(&info);
  result = al_mailbox_resume_text(fixture->runtime, 0u, &token_a,
                                  (const uint8_t *)"FAIL", 4u, &info);
  CHECK("handler diagnostic failure is returned through the native controller",
        result == AL_MAILBOX_HANDLER_FAILURE &&
            info.handler_status == AL_OWNING_STATUS_DIAGNOSTIC &&
            info.error_metadata_id >= 0);
  CHECK("handler failure preserves active bytes and pending state for retry",
         bank_signature(fixture->runtime, 0u) == before_signature &&
             check_main_bank(fixture->runtime, 0u, 1u, 2u, completed_units, 7u,
                             x_units, 1u));
  initialize_call_info(&info);
  CHECK("the original pending token remains usable after handler failure",
        al_mailbox_resume_text(fixture->runtime, 0u, &token_a, replacement,
                               sizeof(replacement), &info) == AL_MAILBOX_OK);
  CHECK("A recovers after failure without borrowing scratch bytes",
        check_main_bank(fixture->runtime, 0u, 0u, 1u, recovered_units, 9u,
                        NULL, 0u));

  initialize_call_info(&info);
  CHECK("B pending token completes after unrelated A failure and retry",
        al_mailbox_resume_text(fixture->runtime, 1u, &token_b,
                               replacement, sizeof(replacement), &info) ==
            AL_MAILBOX_OK);

  initialize_call_info(&info);
  CHECK("consumed token is rejected as duplicate without handler dispatch",
        al_mailbox_resume_text(fixture->runtime, 0u, &token_a, replacement,
                               sizeof(replacement), &info) ==
            AL_MAILBOX_DUPLICATE_TOKEN);
  CHECK("duplicate A token rejection does not invoke its handler",
        get_owning_stats(fixture->runtime, &after_stats) &&
            after_stats.handler_invocations == handlers_before_rejections + 3u);
  initialize_call_info(&info);
  CHECK("completed token is rejected again on its owner mailbox",
        al_mailbox_resume_text(fixture->runtime, 1u, &token_b, replacement,
                               sizeof(replacement), &info) ==
            AL_MAILBOX_DUPLICATE_TOKEN);
  CHECK("duplicate B token rejection does not invoke its handler",
        get_owning_stats(fixture->runtime, &after_stats) &&
            after_stats.handler_invocations == handlers_before_rejections + 3u);

  initialize_call_info(&info);
  CHECK("B begins a second lifecycle after its first completion",
        al_mailbox_begin_text(fixture->runtime, 1u, y_text, sizeof(y_text),
                              &token_b_second, &info) == AL_MAILBOX_OK);
  initialize_call_info(&info);
  result = al_mailbox_resume_text(fixture->runtime, 1u, &token_b_second,
                                  replacement, sizeof(replacement), &info);
  CHECK("second B token completes and replaces last-completed token",
        result == AL_MAILBOX_OK);
  CHECK("second B result contains source-derived B-y-r-y-r text",
        check_main_bank(fixture->runtime, 1u, 0u, 1u, second_b_units, 5u,
                        NULL, 0u));
  initialize_call_info(&info);
  result = al_mailbox_begin_text(fixture->runtime, 1u, y_text, sizeof(y_text),
                                 &token_b_third, &info);
  CHECK("B begins a third lifecycle with a newer pending token",
        result == AL_MAILBOX_OK);
  CHECK("third B pending roots preserve prior state and new continuation",
        check_main_bank(fixture->runtime, 1u, 1u, 2u, second_b_units, 5u,
                        y_units, 1u));
  initialize_call_info(&info);
  CHECK("older valid token is rejected as stale after last-completed advances",
        al_mailbox_resume_text(fixture->runtime, 1u, &token_b, replacement,
                                sizeof(replacement), &info) ==
            AL_MAILBOX_STALE_TOKEN);
  CHECK("stale token rejection does not invoke its handler",
        get_owning_stats(fixture->runtime, &after_stats) &&
            after_stats.handler_invocations == handlers_before_rejections + 6u);
  initialize_call_info(&info);
  CHECK("newer token survives stale-token attempt",
        al_mailbox_resume_text(fixture->runtime, 1u, &token_b_third,
                               replacement, sizeof(replacement), &info) ==
            AL_MAILBOX_OK);

  CHECK("cross-runtime token test runtime initializes",
        create_runtime(module, 1u, 4096u, 1024u, 1024u, &foreign_runtime, 0));
  if (foreign_runtime.runtime != NULL) {
    uint64_t foreign_handlers;
    initialize_call_info(&info);
    CHECK("cross-runtime fixture initializes",
          al_mailbox_init_text(foreign_runtime.runtime, 0u, c_text,
                               sizeof(c_text), &info) == AL_MAILBOX_OK);
    initialize_call_info(&info);
    CHECK("cross-runtime fixture creates a pending token",
          al_mailbox_begin_text(foreign_runtime.runtime, 0u, d_text,
                                sizeof(d_text), &token_foreign, &info) ==
              AL_MAILBOX_OK);
    CHECK("cross-runtime baseline counters are readable",
          get_owning_stats(foreign_runtime.runtime, &before_stats));
    foreign_handlers = before_stats.handler_invocations;
    CHECK("cross-runtime token is rejected before callback invocation",
          al_mailbox_resume_text(foreign_runtime.runtime, 0u, &token_a,
                                 replacement, sizeof(replacement), &info) ==
              AL_MAILBOX_CROSS_RUNTIME_TOKEN);
    CHECK("cross-runtime token rejection does not invoke handler",
          get_owning_stats(foreign_runtime.runtime, &after_stats) &&
              after_stats.handler_invocations == foreign_handlers);
    initialize_call_info(&info);
    CHECK("foreign runtime's own token remains usable",
          al_mailbox_resume_text(foreign_runtime.runtime, 0u, &token_foreign,
                                 replacement, sizeof(replacement), &info) ==
              AL_MAILBOX_OK);
    dispose_runtime(&foreign_runtime);
  }
  CHECK("all main token rejections avoid handler invocation and balance leases",
        get_owning_stats(fixture->runtime, &after_stats) &&
            after_stats.handler_invocations == handlers_before_rejections + 7u &&
            after_stats.outstanding_scratch_leases == 0u &&
            after_stats.scratch_lease_acquisitions ==
                after_stats.scratch_lease_returns);
}

static void run_scratch_capacity_failure(
    const al_owning_mailbox_module *module) {
  static const uint8_t state_text[] = {0x73u};
  static const uint8_t continuation_text[] = {0x63u};
  static const uint8_t retry_text[] = {0x72u};
  static const uint16_t state_units[] = {0x0073u};
  static const uint16_t continuation_units[] = {0x0063u};
  static uint8_t oversized[256];
  runtime_fixture fixture;
  al_mailbox_call_info info;
  al_mailbox_token token;
  uint64_t signature;
  al_mailbox_owning_stats before_stats;
  al_mailbox_owning_stats after_stats;
  memset(oversized, 0x41, sizeof(oversized));
  CHECK("scratch-failure runtime initializes small bounded storage",
        create_runtime(module, 1u, 128u, 2048u, 1024u, &fixture, 0));
  if (fixture.runtime == NULL) {
    dispose_runtime(&fixture);
    return;
  }
  initialize_call_info(&info);
  CHECK("scratch-failure state initializes",
        al_mailbox_init_text(fixture.runtime, 0u, state_text,
                             sizeof(state_text), &info) == AL_MAILBOX_OK);
  initialize_call_info(&info);
  CHECK("scratch-failure test begins pending completion",
        al_mailbox_begin_text(fixture.runtime, 0u, continuation_text,
                              sizeof(continuation_text), &token, &info) ==
            AL_MAILBOX_OK);
  signature = bank_signature(fixture.runtime, 0u);
  CHECK("scratch-failure test captures pre-call counters",
        get_owning_stats(fixture.runtime, &before_stats));
  initialize_call_info(&info);
  CHECK("oversized canonical input reports scratch-capacity failure",
        al_mailbox_resume_text(fixture.runtime, 0u, &token, oversized,
                               sizeof(oversized), &info) ==
            AL_MAILBOX_SCRATCH_CAPACITY &&
            info.handler_status == AL_RUNTIME_STATUS_INVALID_REQUEST);
  CHECK("scratch failure preserves active roots and pending token",
        bank_signature(fixture.runtime, 0u) == signature &&
            check_main_bank(fixture.runtime, 0u, 1u, 2u, state_units, 1u,
                            continuation_units, 1u));
  CHECK("scratch failure leaves publication unchanged and balances its lease",
        get_owning_stats(fixture.runtime, &after_stats) &&
            after_stats.publication_copy_bytes ==
                before_stats.publication_copy_bytes &&
            after_stats.pending_mailboxes == 1u &&
            after_stats.outstanding_scratch_leases == 0u &&
            after_stats.scratch_lease_acquisitions ==
                after_stats.scratch_lease_returns);
  CHECK("scratch aggregate preflight rejects before staging, import, or callback",
        get_owning_stats(fixture.runtime, &after_stats) &&
            after_stats.utf8_input_bytes == before_stats.utf8_input_bytes &&
            after_stats.utf16_staging_bytes == before_stats.utf16_staging_bytes &&
            after_stats.input_import_bytes == before_stats.input_import_bytes &&
            after_stats.returned_output_descriptors ==
                before_stats.returned_output_descriptors &&
            after_stats.handler_invocations == before_stats.handler_invocations &&
            after_stats.scratch_lease_acquisitions ==
                before_stats.scratch_lease_acquisitions);
  initialize_call_info(&info);
  CHECK("original token retries successfully after scratch-capacity failure",
        al_mailbox_resume_text(fixture.runtime, 0u, &token, retry_text,
                               sizeof(retry_text), &info) == AL_MAILBOX_OK);
  CHECK("scratch-failure metrics retain the completed retry counters",
        get_owning_stats(fixture.runtime, &scratch_failure_stats));
  dispose_runtime(&fixture);
}

static void run_retained_capacity_failure(
    const al_owning_mailbox_module *module) {
  static uint8_t large_text[4096];
  static const uint8_t chunk[] = {0x58u};
  static const uint16_t chunk_units[] = {0x0058u};
  runtime_fixture fixture;
  al_mailbox_call_info info;
  al_mailbox_token token;
  al_mailbox_owning_state_view view;
  al_mailbox_owning_stats stats_before;
  al_mailbox_owning_stats stats_after;
  uint64_t signature;
  uint32_t index;
  memset(large_text, 0x41, sizeof(large_text));
  CHECK("large-request runtime initializes with 16 KiB publication banks",
        create_runtime(module, 1u, 65536u, 16384u, 16384u, &fixture, 0));
  if (fixture.runtime == NULL) {
    dispose_runtime(&fixture);
    return;
  }
  initialize_call_info(&info);
  CHECK("native text request of exactly 4096 bytes initializes",
        al_mailbox_init_text(fixture.runtime, 0u, large_text,
                             sizeof(large_text), &info) == AL_MAILBOX_OK);
  CHECK("large initialization publishes the exact 8200-byte serialized String",
        get_view(fixture.runtime, 0u, &view) && view.bank != NULL &&
            view.bank->used_bytes == 8200u && view.bank->root_count == 1u &&
            view.bank->roots[0].type_id == EXPECTED_STATE_TYPE_ID &&
            view.bank->roots[0].extent_bytes == 8200u &&
            view.bank->roots[0].payload_bytes == 8200u &&
            view.bank->bytes[0] == 0x00u && view.bank->bytes[1] == 0x10u &&
            view.bank->bytes[2] == 0x00u && view.bank->bytes[3] == 0x00u);
  for (index = 8u; index < 8200u; ++index) {
    uint8_t expected = (index & 1u) == 0u ? 0x41u : 0u;
    if (view.bank->bytes[index] != expected)
      break;
  }
  CHECK("large root retains all 4096 ASCII code units",
        index == 8200u);
  initialize_call_info(&info);
  CHECK("large mailbox enters pending state with a small continuation",
        al_mailbox_begin_text(fixture.runtime, 0u, chunk, sizeof(chunk), &token,
                              &info) == AL_MAILBOX_OK &&
            get_view(fixture.runtime, 0u, &view) && view.bank != NULL &&
                view.bank->root_count == 2u && view.bank->used_bytes == 8216u &&
                view.bank->roots[0].type_id == EXPECTED_STATE_TYPE_ID &&
                view.bank->roots[0].extent_bytes == 8200u &&
                view.bank->roots[1].type_id == EXPECTED_CONTINUATION_TYPE_ID &&
                view.bank->roots[1].extent_bytes == 16u &&
                check_string_root(view.bank, 1u, EXPECTED_CONTINUATION_TYPE_ID,
                                  chunk_units, 1u) &&
                view.pending == 1u);
  signature = bank_signature(fixture.runtime, 0u);
  CHECK("large mailbox snapshots counters before retained-capacity failure",
        get_owning_stats(fixture.runtime, &stats_before));
  initialize_call_info(&info);
  CHECK("4096-byte completion exceeds bank capacity after handler success",
        al_mailbox_resume_text(fixture.runtime, 0u, &token, large_text,
                               sizeof(large_text), &info) ==
            AL_MAILBOX_RETAINED_CAPACITY);
  CHECK("retained-capacity failure leaves both active roots byte-stable",
        bank_signature(fixture.runtime, 0u) == signature &&
            get_view(fixture.runtime, 0u, &view) && view.pending == 1u &&
            view.bank->root_count == 2u && view.bank->used_bytes == 8216u &&
            view.bank->roots[0].extent_bytes == 8200u &&
            view.bank->roots[1].extent_bytes == 16u);
  CHECK("retained failure does not publish a partial bank",
        get_owning_stats(fixture.runtime, &stats_after) &&
            stats_after.publication_copy_bytes ==
                stats_before.publication_copy_bytes &&
            stats_after.pending_mailboxes == 1u &&
            stats_after.outstanding_scratch_leases == 0u &&
            stats_after.scratch_lease_acquisitions ==
                stats_after.scratch_lease_returns);
  initialize_call_info(&info);
  CHECK("same token completes after retained-capacity retry with empty text",
        al_mailbox_resume_text(fixture.runtime, 0u, &token,
                               (const uint8_t *)"", 0u, &info) == AL_MAILBOX_OK);
  CHECK("retried large result is self-contained in the bank",
        get_view(fixture.runtime, 0u, &view) && view.pending == 0u &&
            view.bank->root_count == 1u && view.bank->used_bytes == 8208u &&
            view.bank->roots[0].extent_bytes == 8208u &&
            view.bank->roots[0].payload_bytes == 8202u);
  for (index = 8u; index < 8200u; ++index) {
    uint8_t expected = (index & 1u) == 0u ? 0x41u : 0u;
    if (view.bank->bytes[index] != expected)
      break;
  }
  CHECK("retried large result preserves request and appends the continuation",
        index == 8200u && view.bank->bytes[8200u] == 0x58u &&
            view.bank->bytes[8201u] == 0x00u);
  CHECK("large request stats report staging, imports, construction and publication separately",
        get_owning_stats(fixture.runtime, &stats_after) &&
            stats_after.utf8_input_bytes == 8193u &&
             stats_after.utf16_staging_bytes == 16424u &&
             stats_after.input_import_bytes == 41056u &&
             stats_after.publication_copy_bytes == 24624u &&
             stats_after.deep_copy_bytes == 73880u && stats_after.move_bytes == 0u &&
             stats_after.returned_output_descriptors == 5u);
  large_request_stats = stats_after;
  dispose_runtime(&fixture);
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

static void print_stats_json(const al_mailbox_owning_stats *stats) {
  printf("{\"mailboxCapacity\":%u,\"initializedMailboxes\":%u,\"pendingMailboxes\":%u,\"storageReservedBytes\":%" PRIu64 ",\"retainedReservedBytes\":%" PRIu64 ",\"scratchReservedBytes\":%" PRIu64 ",\"textStagingReservedBytes\":%" PRIu64 ",\"liveRetainedBytes\":%" PRIu64 ",\"liveRetainedRoots\":%" PRIu64 ",\"scratchHighWaterBytes\":%" PRIu64 ",\"utf8InputBytes\":%" PRIu64 ",\"utf16StagingBytes\":%" PRIu64 ",\"inputImportBytes\":%" PRIu64 ",\"publicationCopyBytes\":%" PRIu64 ",\"deepCopyBytes\":%" PRIu64 ",\"moveBytes\":%" PRIu64 ",\"returnedOutputDescriptors\":%" PRIu64 ",\"turnResetBytes\":%" PRIu64 ",\"handlerInvocations\":%" PRIu64 ",\"handlerFailures\":%" PRIu64 ",\"scratchLeaseAcquisitions\":%" PRIu64 ",\"scratchLeaseReturns\":%" PRIu64 ",\"outstandingScratchLeases\":%u}",
         stats->mailbox_capacity, stats->initialized_mailboxes,
         stats->pending_mailboxes, stats->storage_reserved_bytes,
         stats->retained_reserved_bytes, stats->scratch_reserved_bytes,
         stats->text_staging_reserved_bytes, stats->live_retained_bytes,
         stats->live_retained_roots, stats->scratch_high_water_bytes,
         stats->utf8_input_bytes, stats->utf16_staging_bytes,
         stats->input_import_bytes, stats->publication_copy_bytes,
         stats->deep_copy_bytes, stats->move_bytes,
          stats->returned_output_descriptors, stats->turn_reset_bytes,
         stats->handler_invocations, stats->handler_failures,
         stats->scratch_lease_acquisitions, stats->scratch_lease_returns,
         stats->outstanding_scratch_leases);
}

static void print_requirements_json(
    const al_mailbox_owning_storage_requirements *requirements) {
  printf("{\"storageAlignment\":%u,\"mailboxCapacity\":%u,\"storageBytes\":%" PRIu64 ",\"retainedReservedBytes\":%" PRIu64 ",\"scratchReservedBytes\":%" PRIu64 ",\"textStagingReservedBytes\":%" PRIu64 ",\"controllerReservedBytes\":%" PRIu64 "}",
         requirements->storage_alignment, requirements->mailbox_capacity,
         requirements->storage_bytes, requirements->retained_reserved_bytes,
         requirements->scratch_reserved_bytes,
         requirements->text_staging_reserved_bytes,
         requirements->controller_reserved_bytes);
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

int main(int argc, char **argv) {
  HMODULE library;
  al_owning_mailbox_module_fn get_module;
  const al_owning_mailbox_module *module;
  bank_snapshot snapshots[6];
  al_mailbox_owning_stats main_stats;
  runtime_fixture main_fixture;
  int main_fixture_ready = 0;
  if (argc != 2) {
    fprintf(stderr, "Usage: native-owning-mailbox <owning-mailbox-module.dll>\n");
    return 2;
  }
  memset(snapshots, 0, sizeof(snapshots));
  memset(&main_stats, 0, sizeof(main_stats));
  memset(&main_fixture, 0, sizeof(main_fixture));
  memset(&boundary_stats, 0, sizeof(boundary_stats));
  memset(&scratch_failure_stats, 0, sizeof(scratch_failure_stats));
  memset(&large_request_stats, 0, sizeof(large_request_stats));
  memset(&main_requirements, 0, sizeof(main_requirements));
  library = LoadLibraryA(argv[1]);
  CHECK("generated owning mailbox module loads",
        library != NULL);
  if (library == NULL)
    goto done;
  get_module = (al_owning_mailbox_module_fn)(uintptr_t)GetProcAddress(
      library, "agentlang_owning_mailbox_module");
  CHECK("generated owning module exports its versioned descriptor",
        get_module != NULL);
  if (get_module == NULL)
    goto done;
  module = get_module();
  CHECK("shared owning layout and role type indexes match source-derived oracle",
        valid_module_roles(module));
  if (!valid_module_roles(module))
    goto done;

  run_direct_callback_preflight(module);
  run_small_lifecycle(module, snapshots, &main_stats, &main_fixture);
  main_fixture_ready = main_fixture.runtime != NULL;
  if (main_fixture_ready)
    main_requirements = main_fixture.requirements;
  run_utf8_boundaries(module);
  if (main_fixture_ready)
    run_utf8_failures_and_tokens(module, &main_fixture);
  run_scratch_capacity_failure(module);
  run_retained_capacity_failure(module);

done:
  if (main_fixture_ready)
    dispose_runtime(&main_fixture);
  printf("{\"passed\":%s,\"failureCount\":%u,\"checks\":",
         failure_count == 0u ? "true" : "false", failure_count);
  print_checks_json();
  printf(",\"observed\":{\"unicodeInitialize\":");
  print_snapshot_json(&snapshots[0]);
  printf(",\"emptyInitialize\":");
  print_snapshot_json(&snapshots[1]);
  printf(",\"unicodeBegin\":");
  print_snapshot_json(&snapshots[2]);
  printf(",\"emptyBegin\":");
  print_snapshot_json(&snapshots[3]);
  printf(",\"unicodeResume\":");
  print_snapshot_json(&snapshots[4]);
  printf(",\"emptyResume\":");
  print_snapshot_json(&snapshots[5]);
  printf("},\"successStats\":");
  print_stats_json(&main_stats);
  printf(",\"storageRequirements\":");
  print_requirements_json(&main_requirements);
  printf(",\"boundaryStats\":");
  print_stats_json(&boundary_stats);
  printf(",\"scratchFailureRetryStats\":");
  print_stats_json(&scratch_failure_stats);
  printf(",\"largeRequestStats\":");
  print_stats_json(&large_request_stats);
  printf("}\n");
  if (library != NULL)
    FreeLibrary(library);
  return failure_count == 0u ? 0 : 1;
}
