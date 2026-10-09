#define WIN32_LEAN_AND_MEAN

#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>

#include <inttypes.h>
#include <limits.h>
#include <stdarg.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "mailbox_runtime.h"

enum {
  MAILBOX_CAPACITY = 2u,
  SCRATCH_CAPACITY = 262144u,
  RETAINED_CAPACITY = 65536u,
  TEXT_STAGING_CAPACITY = 16384u,
  MAX_PROVIDER_BYTES = 4096u,
  MAX_PROVIDER_FRAME_BYTES = 8u + MAX_PROVIDER_BYTES,
  MAX_STATE_UNITS = MAX_PROVIDER_BYTES * 2u,
  MAX_CASES = 7u,
  MAX_STEPS = 8u,
  MAX_IO_EVENTS = 16384u,
  MAX_PROVIDER_REQUESTS = MAX_STEPS,
  IO_TIMEOUT_MS = 5000u,
  CANCEL_DELAY_MS = 1000u
};

typedef struct text_view {
  const uint8_t *bytes;
  uint32_t byte_count;
} text_view;

typedef struct decoded_text {
  uint32_t unit_count;
  uint16_t units[MAX_STATE_UNITS];
} decoded_text;

typedef struct decoded_state {
  int64_t attempted;
  int64_t completed;
  decoded_text latest;
} decoded_state;

typedef struct mailbox_snapshot {
  uint32_t pending;
  decoded_state state;
  uint32_t has_continuation;
  decoded_text continuation_request;
  uint32_t has_token;
  al_mailbox_token token;
} mailbox_snapshot;

typedef struct step_snapshot {
  uint32_t step;
  const char *op;
  const char *mailbox;
  int32_t status;
  int32_t handler_status;
  int32_t error_metadata_id;
  mailbox_snapshot primary;
  uint32_t other_count;
  struct {
    const char *name;
    mailbox_snapshot snapshot;
  } other[MAILBOX_CAPACITY - 1u];
} step_snapshot;

typedef enum io_event_kind {
  IO_EVENT_PROVIDER_REQUEST = 1,
  IO_EVENT_RECV_SUBMIT = 2,
  IO_EVENT_RECV_TERMINAL = 3,
  IO_EVENT_CANCEL_REQUEST = 4,
  IO_EVENT_CANCEL_API = 5
} io_event_kind;

typedef struct io_event {
  uint32_t sequence;
  uint32_t kind;
  uint32_t mailbox_id;
  const char *mailbox_name;
  uint32_t pending_receives;
  uint32_t error_code;
  uint32_t transferred;
  uint32_t pinned_scratch_slots;
  int32_t result;
  uint32_t flags;
  uint32_t provider_request_index;
} io_event;

typedef struct provider_request_record {
  uint32_t delay_ms;
  uint32_t payload_bytes;
  uint8_t payload[MAX_PROVIDER_BYTES];
} provider_request_record;

typedef struct io_counters {
  uint32_t requests;
  uint32_t receive_submissions;
  uint32_t receive_completions;
  uint32_t cancel_requests;
  uint32_t cancel_acknowledgements;
  uint32_t peak_pending_receives;
  uint32_t pending_receives;
  uint32_t pending_receives_at_end;
} io_counters;

typedef struct case_result {
  const char *id;
  uint32_t step_count;
  step_snapshot steps[MAX_STEPS];
  io_counters io;
  uint32_t io_event_count;
  io_event io_events[MAX_IO_EVENTS];
  uint32_t provider_request_count;
  provider_request_record provider_requests[MAX_PROVIDER_REQUESTS];
  al_mailbox_owning_stats stats;
  al_mailbox_owning_stats after_dispose_stats;
  al_mailbox_reset_stats reset_stats;
} case_result;

typedef struct io_request {
  SOCKET socket;
  OVERLAPPED overlapped;
  WSABUF receive_buffer;
  DWORD receive_flags;
  uint8_t bytes[4u + MAX_PROVIDER_BYTES];
  uint32_t receive_offset;
  uint32_t receive_goal;
  uint32_t response_length;
  uint64_t deadline_ms;
  int32_t last_error;
  uint32_t active;
  uint32_t inflight;
  uint32_t posted_asynchronously;
  uint32_t ready;
  uint32_t cancel_requested;
  uint32_t cancel_terminal;
} io_request;

typedef struct driver_mailbox {
  const char *name;
  uint32_t initialized;
  uint32_t has_token;
  al_mailbox_token token;
  io_request io;
} driver_mailbox;

typedef struct runtime_fixture {
  al_mailbox_runtime *runtime;
  void *storage;
  size_t allocation_bytes;
  al_mailbox_owning_config config;
  al_mailbox_owning_storage_requirements requirements;
} runtime_fixture;

typedef struct case_driver {
  case_result *result;
  runtime_fixture fixture;
  driver_mailbox mailboxes[MAILBOX_CAPACITY];
  uint32_t mailbox_count;
  HANDLE completion_port;
  uint32_t port;
  uint32_t policy;
} case_driver;

typedef struct module_roles {
  uint32_t string_index;
  uint32_t state_index;
  uint32_t continuation_index;
} module_roles;

static case_result g_results[MAX_CASES];
static const al_owning_mailbox_module *g_module;
static module_roles g_roles;
static HANDLE g_completion_port;
static uint8_t g_large_request[MAX_PROVIDER_BYTES];
static uint8_t g_large_response[MAX_PROVIDER_BYTES];
static char g_failure[512];

static int failf(const char *format, ...) {
  va_list args;
  if (g_failure[0] == '\0') {
    va_start(args, format);
    (void)vsnprintf(g_failure, sizeof(g_failure), format, args);
    va_end(args);
  }
  return 0;
}

static uint32_t read_u32_le(const uint8_t *bytes) {
  return (uint32_t)bytes[0] | ((uint32_t)bytes[1] << 8u) |
         ((uint32_t)bytes[2] << 16u) | ((uint32_t)bytes[3] << 24u);
}

static uint16_t read_u16_le(const uint8_t *bytes) {
  return (uint16_t)((uint16_t)bytes[0] | ((uint16_t)bytes[1] << 8u));
}

static uint64_t read_u64_le(const uint8_t *bytes) {
  uint64_t value = 0u;
  uint32_t index;
  for (index = 0u; index < 8u; ++index)
    value |= (uint64_t)bytes[index] << (index * 8u);
  return value;
}

static void write_u32_le(uint8_t *bytes, uint32_t value) {
  bytes[0] = (uint8_t)value;
  bytes[1] = (uint8_t)(value >> 8u);
  bytes[2] = (uint8_t)(value >> 16u);
  bytes[3] = (uint8_t)(value >> 24u);
}

static int type_index_valid(const al_owning_layout *layout,
                            uint32_t type_index) {
  return layout != NULL && layout->types != NULL &&
         type_index < layout->type_count;
}

static int get_record_field(const al_owning_layout *layout,
                            uint32_t record_index, uint32_t field_index,
                            const al_owning_field_descriptor **out_field,
                            const al_owning_type_descriptor **out_child) {
  const al_owning_type_descriptor *record;
  const al_owning_field_descriptor *field;
  if (out_field == NULL || out_child == NULL ||
      !type_index_valid(layout, record_index))
    return 0;
  record = &layout->types[record_index];
  if (record->kind != AL_OWNING_TYPE_RECORD ||
      field_index >= record->field_count ||
      record->first_field > layout->field_count ||
      record->field_count > layout->field_count - record->first_field ||
      layout->fields == NULL)
    return 0;
  field = &layout->fields[record->first_field + field_index];
  if (field->reserved != 0u || field->flags != 0u ||
      !type_index_valid(layout, field->child_type_index))
    return 0;
  *out_field = field;
  *out_child = &layout->types[field->child_type_index];
  return 1;
}

static int validate_record_field(const al_owning_layout *layout,
                                 uint32_t record_index, uint32_t field_index,
                                 uint32_t expected_kind,
                                 uint32_t expected_offset) {
  const al_owning_field_descriptor *field;
  const al_owning_type_descriptor *child;
  if (!get_record_field(layout, record_index, field_index, &field, &child) ||
      child->kind != expected_kind ||
      (field->fixed_offset_bytes != AL_OWNING_LAYOUT_DYNAMIC_U32 &&
       field->fixed_offset_bytes != expected_offset))
    return 0;
  return 1;
}

static int validate_module(const al_owning_mailbox_module *module,
                           module_roles *roles) {
  const al_owning_layout *layout;
  const al_owning_mailbox_entry *initialize;
  const al_owning_mailbox_entry *begin;
  const al_owning_mailbox_entry *resume;
  const al_owning_type_descriptor *state_type;
  const al_owning_type_descriptor *continuation_type;
  const al_owning_type_descriptor *string_type;
  const al_owning_type_descriptor *int_type;
  const al_owning_field_descriptor *unused_field;
  const al_owning_type_descriptor *unused_child;
  uint32_t string_index;
  uint32_t state_index;
  uint32_t continuation_index;
  if (module == NULL || roles == NULL ||
      module->abi_version != AL_OWNING_MAILBOX_ABI_VERSION ||
      module->struct_size != sizeof(*module) || module->layout == NULL ||
      module->layout->abi_version != AL_OWNING_LAYOUT_ABI_VERSION ||
      module->layout->types == NULL ||
      module->layout->type_count == 0u ||
      module->layout->type_count > AL_OWNING_LAYOUT_MAX_TYPES ||
      module->layout->field_count > AL_OWNING_LAYOUT_MAX_FIELDS ||
      (module->layout->field_count != 0u && module->layout->fields == NULL))
    return 0;
  layout = module->layout;
  initialize = &module->entries[0];
  begin = &module->entries[1];
  resume = &module->entries[2];
  if (initialize->input_count != 1u || initialize->output_count != 1u ||
      begin->input_count != 2u || begin->output_count != 2u ||
      resume->input_count != 3u || resume->output_count != 1u ||
      initialize->execute == NULL || begin->execute == NULL ||
      resume->execute == NULL || module->associated_resume == NULL)
    return 0;
  string_index = initialize->input_type_indexes[0];
  state_index = initialize->output_type_indexes[0];
  continuation_index = begin->output_type_indexes[1];
  if (!type_index_valid(layout, string_index) ||
      !type_index_valid(layout, state_index) ||
      !type_index_valid(layout, continuation_index) ||
      begin->input_type_indexes[0] != state_index ||
      begin->input_type_indexes[1] != string_index ||
      begin->output_type_indexes[0] != state_index ||
      resume->input_type_indexes[0] != state_index ||
      resume->input_type_indexes[1] != continuation_index ||
      resume->input_type_indexes[2] != string_index ||
      resume->output_type_indexes[0] != state_index)
    return 0;
  state_type = &layout->types[state_index];
  continuation_type = &layout->types[continuation_index];
  string_type = &layout->types[string_index];
  if (state_type->kind != AL_OWNING_TYPE_RECORD ||
      state_type->field_count != 3u ||
      continuation_type->kind != AL_OWNING_TYPE_RECORD ||
      continuation_type->field_count != 1u ||
      string_type->kind != AL_OWNING_TYPE_STRING ||
      !get_record_field(layout, state_index, 0u, &unused_field, &int_type) ||
      int_type->kind != AL_OWNING_TYPE_I64 ||
      int_type->fixed_payload_bytes != 8u || int_type->fixed_extent_bytes != 8u ||
      !validate_record_field(layout, state_index, 0u, AL_OWNING_TYPE_I64, 0u) ||
      !validate_record_field(layout, state_index, 1u, AL_OWNING_TYPE_I64, 8u) ||
      !validate_record_field(layout, state_index, 2u, AL_OWNING_TYPE_STRING, 16u) ||
      !validate_record_field(layout, continuation_index, 0u,
                             AL_OWNING_TYPE_STRING, 0u) ||
      !get_record_field(layout, continuation_index, 0u, &unused_field,
                        &unused_child) ||
      unused_child->type_id != string_type->type_id)
    return 0;
  roles->string_index = string_index;
  roles->state_index = state_index;
  roles->continuation_index = continuation_index;
  return 1;
}

static int measure_external(const al_owning_layout *layout, uint32_t type_index,
                            const uint8_t *bytes, uint32_t byte_count,
                            uint32_t offset, uint32_t *payload_bytes,
                            uint32_t *extent_bytes) {
  al_owning_stack_context scanner;
  memset(&scanner, 0, sizeof(scanner));
  scanner.abi_version = AL_OWNING_STACK_ABI_VERSION;
  scanner.status = AL_OWNING_STATUS_OK;
  scanner.available_bytes = UINT32_MAX;
  return al_owning_measure_external_value(
             &scanner, layout, type_index, bytes, byte_count, offset, 0u,
             payload_bytes, extent_bytes) == 0;
}

static int decode_inline_string(const uint8_t *bytes, uint32_t byte_count,
                                uint32_t offset, decoded_text *out,
                                uint32_t *out_payload,
                                uint32_t *out_extent) {
  uint32_t units;
  uint64_t payload_wide;
  uint64_t extent_wide;
  uint32_t index;
  if (bytes == NULL || out == NULL || out_payload == NULL ||
      out_extent == NULL || offset > byte_count ||
      byte_count - offset < 8u || (offset & 7u) != 0u)
    return 0;
  units = read_u32_le(bytes + offset);
  if (units > MAX_STATE_UNITS || bytes[offset + 4u] != 0u ||
      bytes[offset + 5u] != 0u || bytes[offset + 6u] != 0u ||
      bytes[offset + 7u] != 0u)
    return 0;
  payload_wide = 8ull + (uint64_t)units * 2ull;
  extent_wide = (payload_wide + 7ull) & ~7ull;
  if (payload_wide > UINT32_MAX || extent_wide > UINT32_MAX ||
      extent_wide > byte_count - offset)
    return 0;
  memset(out, 0, sizeof(*out));
  out->unit_count = units;
  for (index = 0u; index < units; ++index)
    out->units[index] = read_u16_le(bytes + offset + 8u + index * 2u);
  for (index = (uint32_t)payload_wide; index < (uint32_t)extent_wide; ++index) {
    if (bytes[offset + index] != 0u)
      return 0;
  }
  *out_payload = (uint32_t)payload_wide;
  *out_extent = (uint32_t)extent_wide;
  return 1;
}

static int decode_record_string(const al_owning_layout *layout,
                                uint32_t record_index, const uint8_t *bytes,
                                uint32_t extent_bytes, uint32_t payload_bytes,
                                uint32_t field_count, decoded_text *out_text) {
  const al_owning_type_descriptor *record;
  uint32_t offset = 0u;
  uint32_t calculated_payload = 0u;
  uint32_t field_index;
  if (layout == NULL || bytes == NULL || out_text == NULL ||
      !type_index_valid(layout, record_index))
    return 0;
  record = &layout->types[record_index];
  if (record->kind != AL_OWNING_TYPE_RECORD ||
      record->field_count != field_count || record->first_field > layout->field_count ||
      record->field_count > layout->field_count - record->first_field)
    return 0;
  memset(out_text, 0, sizeof(*out_text));
  for (field_index = 0u; field_index < field_count; ++field_index) {
    const al_owning_field_descriptor *field;
    const al_owning_type_descriptor *child;
    uint32_t field_payload;
    uint32_t field_extent;
    if (!get_record_field(layout, record_index, field_index, &field, &child) ||
        (field->fixed_offset_bytes != AL_OWNING_LAYOUT_DYNAMIC_U32 &&
         field->fixed_offset_bytes != offset) ||
        offset > extent_bytes)
      return 0;
    if (child->kind == AL_OWNING_TYPE_I64) {
      if (child->fixed_extent_bytes != 8u || child->fixed_payload_bytes != 8u ||
          extent_bytes - offset < 8u)
        return 0;
      field_payload = 8u;
      field_extent = 8u;
    } else if (child->kind == AL_OWNING_TYPE_STRING) {
      decoded_text parsed;
      if (!decode_inline_string(bytes, extent_bytes, offset, &parsed,
                                &field_payload, &field_extent))
        return 0;
      *out_text = parsed;
    } else {
      return 0;
    }
    if (field_extent > extent_bytes - offset ||
        calculated_payload > UINT32_MAX - field_payload)
      return 0;
    offset += field_extent;
    calculated_payload += field_payload;
  }
  return offset == extent_bytes && calculated_payload == payload_bytes;
}

static int decode_state(const al_owning_layout *layout, uint32_t state_index,
                        const uint8_t *bytes, uint32_t extent_bytes,
                        uint32_t payload_bytes, decoded_state *out_state) {
  const al_owning_type_descriptor *record;
  const al_owning_field_descriptor *field;
  const al_owning_type_descriptor *child;
  uint32_t offset = 0u;
  uint32_t calculated_payload = 0u;
  uint32_t field_payload;
  uint32_t field_extent;
  uint64_t bits;
  if (bytes == NULL || out_state == NULL ||
      !type_index_valid(layout, state_index))
    return 0;
  record = &layout->types[state_index];
  if (record->kind != AL_OWNING_TYPE_RECORD || record->field_count != 3u)
    return 0;
  memset(out_state, 0, sizeof(*out_state));
  if (!get_record_field(layout, state_index, 0u, &field, &child) ||
      child->kind != AL_OWNING_TYPE_I64 ||
      (field->fixed_offset_bytes != AL_OWNING_LAYOUT_DYNAMIC_U32 &&
       field->fixed_offset_bytes != offset) ||
      child->fixed_extent_bytes != 8u || child->fixed_payload_bytes != 8u ||
      extent_bytes - offset < 8u)
    return 0;
  bits = read_u64_le(bytes + offset);
  memcpy(&out_state->attempted, &bits, sizeof(bits));
  offset += 8u;
  calculated_payload += 8u;
  if (!get_record_field(layout, state_index, 1u, &field, &child) ||
      child->kind != AL_OWNING_TYPE_I64 ||
      (field->fixed_offset_bytes != AL_OWNING_LAYOUT_DYNAMIC_U32 &&
       field->fixed_offset_bytes != offset) ||
      child->fixed_extent_bytes != 8u || child->fixed_payload_bytes != 8u ||
      extent_bytes - offset < 8u)
    return 0;
  bits = read_u64_le(bytes + offset);
  memcpy(&out_state->completed, &bits, sizeof(bits));
  offset += 8u;
  calculated_payload += 8u;
  if (!get_record_field(layout, state_index, 2u, &field, &child) ||
      child->kind != AL_OWNING_TYPE_STRING ||
      (field->fixed_offset_bytes != AL_OWNING_LAYOUT_DYNAMIC_U32 &&
       field->fixed_offset_bytes != offset) ||
      !decode_inline_string(bytes, extent_bytes, offset, &out_state->latest,
                            &field_payload, &field_extent) ||
      field_extent > extent_bytes - offset ||
      calculated_payload > UINT32_MAX - field_payload)
    return 0;
  offset += field_extent;
  calculated_payload += field_payload;
  return offset == extent_bytes && calculated_payload == payload_bytes;
}

static int decode_bank_root(const case_driver *driver,
                            const al_owning_byte_bank *bank,
                            uint32_t root_index, uint32_t type_index,
                            const uint8_t **out_bytes, uint32_t *out_extent,
                            uint32_t *out_payload) {
  const al_owning_bank_root *root;
  uint32_t measured_payload;
  uint32_t measured_extent;
  if (driver == NULL || bank == NULL || bank->bytes == NULL ||
      bank->roots == NULL || root_index >= bank->root_count ||
      type_index >= g_module->layout->type_count)
    return 0;
  root = &bank->roots[root_index];
  if (root->type_id != g_module->layout->types[type_index].type_id ||
      root->offset_bytes > bank->used_bytes ||
      root->extent_bytes > bank->used_bytes - root->offset_bytes ||
      root->owner_end_bytes != root->offset_bytes + root->extent_bytes ||
      root->payload_bytes > root->extent_bytes ||
      !measure_external(g_module->layout, type_index, bank->bytes,
                        bank->used_bytes, root->offset_bytes,
                        &measured_payload, &measured_extent) ||
      measured_payload != root->payload_bytes ||
      measured_extent != root->extent_bytes)
    return 0;
  *out_bytes = bank->bytes + root->offset_bytes;
  *out_extent = measured_extent;
  *out_payload = measured_payload;
  return 1;
}

static int decode_attached_root(const al_mailbox_owning_state_view *view,
                                uint32_t root_index, uint32_t type_index,
                                const uint8_t **out_bytes,
                                uint32_t *out_extent,
                                uint32_t *out_payload) {
  const al_owning_bank_stack_slice *root;
  al_owning_stack_context scanner;
  al_owning_value_size size;
  if (view == NULL || view->associated_context == NULL ||
      view->associated_roots == NULL ||
      root_index >= view->associated_root_count)
    return 0;
  root = &view->associated_roots[root_index];
  scanner = *view->associated_context;
  if (root->type_index != type_index || root->reserved != 0u ||
      root->source_offset_bytes > view->associated_context->cursor_bytes ||
      root->source_owner_end_bytes > view->associated_context->cursor_bytes ||
      al_owning_measure_value(&scanner, g_module->layout, type_index,
                              root->source_offset_bytes,
                              root->source_owner_end_bytes, 0u, &size) != 0)
    return 0;
  if (size.extent_bytes > root->source_owner_end_bytes -
                              root->source_offset_bytes)
    return 0;
  *out_bytes = view->associated_context->stack_data + root->source_offset_bytes;
  *out_extent = size.extent_bytes;
  *out_payload = size.payload_bytes;
  return 1;
}

static int get_view(al_mailbox_runtime *runtime, uint32_t mailbox_id,
                    al_mailbox_owning_state_view *view) {
  memset(view, 0, sizeof(*view));
  view->control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  view->struct_size = (uint32_t)sizeof(*view);
  return al_mailbox_get_owning_state_view(runtime, mailbox_id, view) ==
         AL_MAILBOX_OK;
}

static int capture_mailbox(const case_driver *driver, uint32_t mailbox_id,
                           mailbox_snapshot *snapshot) {
  al_mailbox_owning_state_view view;
  const uint8_t *state_bytes;
  uint32_t state_extent;
  uint32_t state_payload;
  const uint8_t *continuation_bytes;
  uint32_t continuation_extent;
  uint32_t continuation_payload;
  uint32_t state_root_index = 0u;
  if (driver == NULL || snapshot == NULL ||
      !get_view(driver->fixture.runtime, mailbox_id, &view) ||
      view.bank == NULL || view.state_type_id !=
                               g_module->layout->types[g_roles.state_index].type_id ||
      view.continuation_type_id != g_module->layout->types[
                                       g_roles.continuation_index].type_id ||
      view.bank->root_count == 0u)
    return 0;
  memset(snapshot, 0, sizeof(*snapshot));
  snapshot->pending = view.pending != 0u;
  if (view.pending != 0u &&
      driver->policy == AL_MAILBOX_OWNING_POLICY_KEEP_ASSOCIATED) {
    if (view.associated_context == NULL || view.associated_roots == NULL ||
        view.associated_root_count != 2u ||
        !decode_attached_root(&view, 0u, g_roles.state_index, &state_bytes,
                              &state_extent, &state_payload))
      return 0;
  } else if (!decode_bank_root(driver, view.bank, state_root_index,
                               g_roles.state_index, &state_bytes,
                               &state_extent, &state_payload)) {
    return 0;
  }
  if (!decode_state(g_module->layout, g_roles.state_index, state_bytes,
                    state_extent, state_payload, &snapshot->state))
    return 0;
  if (view.pending != 0u) {
    if (driver->policy == AL_MAILBOX_OWNING_POLICY_KEEP_ASSOCIATED) {
      if (!decode_attached_root(&view, 1u, g_roles.continuation_index,
                                &continuation_bytes, &continuation_extent,
                                &continuation_payload))
        return 0;
    } else {
      if (view.bank->root_count != 2u ||
          !decode_bank_root(driver, view.bank, 1u,
                            g_roles.continuation_index, &continuation_bytes,
                            &continuation_extent, &continuation_payload))
        return 0;
    }
    if (!decode_record_string(g_module->layout, g_roles.continuation_index,
                              continuation_bytes, continuation_extent,
                              continuation_payload, 1u,
                              &snapshot->continuation_request))
      return 0;
    snapshot->has_continuation = 1u;
    if (!driver->mailboxes[mailbox_id].has_token)
      return 0;
    snapshot->has_token = 1u;
    snapshot->token = driver->mailboxes[mailbox_id].token;
  }
  return 1;
}

static int initialize_call_info(al_mailbox_call_info *info) {
  if (info == NULL)
    return 0;
  memset(info, 0, sizeof(*info));
  info->control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  info->struct_size = (uint32_t)sizeof(*info);
  info->error_metadata_id = -1;
  return 1;
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

static int create_runtime(case_driver *driver) {
  al_mailbox_result result;
  if (driver == NULL)
    return 0;
  memset(&driver->fixture, 0, sizeof(driver->fixture));
  driver->fixture.config.control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  driver->fixture.config.struct_size =
      (uint32_t)sizeof(driver->fixture.config);
  driver->fixture.config.mailbox_capacity = MAILBOX_CAPACITY;
  driver->fixture.config.scratch_byte_capacity = SCRATCH_CAPACITY;
  driver->fixture.config.retained_byte_capacity = RETAINED_CAPACITY;
  driver->fixture.config.text_staging_byte_capacity = TEXT_STAGING_CAPACITY;
  driver->fixture.config.scratch_slot_capacity = MAILBOX_CAPACITY;
  driver->fixture.config.suspension_policy = driver->policy;
  initialize_owning_requirements(&driver->fixture.requirements);
  result = al_mailbox_get_owning_storage_requirements(
      g_module, &driver->fixture.config, &driver->fixture.requirements);
  if (result != AL_MAILBOX_OK ||
      driver->fixture.requirements.storage_bytes == 0u ||
      driver->fixture.requirements.storage_bytes > SIZE_MAX)
    return failf("%s: owning storage requirements failed (%d)",
                 driver->result->id, (int)result);
  driver->fixture.allocation_bytes =
      (size_t)driver->fixture.requirements.storage_bytes;
  driver->fixture.storage = VirtualAlloc(
      NULL, driver->fixture.allocation_bytes, MEM_RESERVE | MEM_COMMIT,
      PAGE_READWRITE);
  if (driver->fixture.storage == NULL)
    return failf("%s: VirtualAlloc failed (%lu)", driver->result->id,
                 (unsigned long)GetLastError());
  result = al_mailbox_runtime_init_owning(
      g_module, &driver->fixture.config, driver->fixture.storage,
      driver->fixture.requirements.storage_bytes, &driver->fixture.runtime);
  if (result != AL_MAILBOX_OK || driver->fixture.runtime == NULL)
    return failf("%s: owning runtime initialization failed (%d)",
                 driver->result->id, (int)result);
  return 1;
}

static int add_mailbox(case_driver *driver, uint32_t mailbox_id,
                       const char *name) {
  if (mailbox_id >= MAILBOX_CAPACITY || name == NULL ||
      driver->mailboxes[mailbox_id].initialized != 0u ||
      driver->mailboxes[mailbox_id].name != NULL)
    return 0;
  driver->mailboxes[mailbox_id].name = name;
  driver->mailboxes[mailbox_id].io.socket = INVALID_SOCKET;
  if (driver->mailbox_count <= mailbox_id)
    driver->mailbox_count = mailbox_id + 1u;
  return 1;
}

static int add_io_event(case_driver *driver, io_event_kind kind,
                        uint32_t mailbox_id, uint32_t error_code,
                        uint32_t transferred, int32_t result,
                        uint32_t pinned_scratch_slots, uint32_t flags) {
  case_result *output;
  io_event *event;
  if (driver == NULL || driver->result == NULL ||
      mailbox_id >= MAILBOX_CAPACITY)
    return 0;
  if (driver->mailboxes[mailbox_id].name == NULL)
    return failf("%s: IO event has no registered mailbox identity",
                 driver->result->id);
  output = driver->result;
  if (output->io_event_count >= MAX_IO_EVENTS)
    return failf("%s: IO event capacity exceeded", output->id);
  event = &output->io_events[output->io_event_count];
  memset(event, 0, sizeof(*event));
  event->sequence = output->io_event_count;
  event->kind = (uint32_t)kind;
  event->mailbox_id = mailbox_id;
  event->mailbox_name = driver->mailboxes[mailbox_id].name;
  event->pending_receives = output->io.pending_receives;
  event->error_code = error_code;
  event->transferred = transferred;
  event->pinned_scratch_slots = pinned_scratch_slots;
  event->result = result;
  event->flags = flags;
  event->provider_request_index = UINT32_MAX;
  ++output->io_event_count;
  return 1;
}

static int connect_loopback_bounded(SOCKET socket_handle,
                                    const struct sockaddr_in *address) {
  u_long nonblocking = 1ul;
  int connect_result;
  int connect_error;
  fd_set writable;
  fd_set exceptional;
  struct timeval timeout;
  int selected;
  int socket_error = 0;
  int socket_error_bytes = (int)sizeof(socket_error);
  if (ioctlsocket(socket_handle, FIONBIO, &nonblocking) == SOCKET_ERROR)
    return 0;
  connect_result = connect(socket_handle, (const struct sockaddr *)address,
                            (int)sizeof(*address));
  if (connect_result == SOCKET_ERROR) {
    connect_error = WSAGetLastError();
    if (connect_error != WSAEWOULDBLOCK && connect_error != WSAEINPROGRESS &&
        connect_error != WSAEALREADY) {
      WSASetLastError(connect_error);
      return 0;
    }
    FD_ZERO(&writable);
    FD_ZERO(&exceptional);
    FD_SET(socket_handle, &writable);
    FD_SET(socket_handle, &exceptional);
    timeout.tv_sec = (long)(IO_TIMEOUT_MS / 1000u);
    timeout.tv_usec = (long)((IO_TIMEOUT_MS % 1000u) * 1000u);
    selected = select(0, NULL, &writable, &exceptional, &timeout);
    if (selected == 0) {
      WSASetLastError(WSAETIMEDOUT);
      return 0;
    }
    if (selected == SOCKET_ERROR)
      return 0;
    if (getsockopt(socket_handle, SOL_SOCKET, SO_ERROR,
                   (char *)&socket_error, &socket_error_bytes) ==
        SOCKET_ERROR)
      return 0;
    if (socket_error != 0) {
      WSASetLastError(socket_error);
      return 0;
    }
  }
  nonblocking = 0ul;
  if (ioctlsocket(socket_handle, FIONBIO, &nonblocking) == SOCKET_ERROR)
    return 0;
  return 1;
}

static int send_all_bounded(SOCKET socket_handle, const uint8_t *bytes,
                            uint32_t byte_count) {
  uint32_t sent = 0u;
  uint64_t deadline = GetTickCount64() + IO_TIMEOUT_MS;
  while (sent < byte_count) {
    uint64_t now = GetTickCount64();
    uint64_t remaining = deadline > now ? deadline - now : 0u;
    DWORD timeout_ms;
    int timeout_bytes = (int)sizeof(timeout_ms);
    int result;
    if (remaining == 0u) {
      WSASetLastError(WSAETIMEDOUT);
      return 0;
    }
    timeout_ms = remaining > (uint64_t)UINT32_MAX
                     ? UINT32_MAX
                     : (DWORD)remaining;
    if (setsockopt(socket_handle, SOL_SOCKET, SO_SNDTIMEO,
                   (const char *)&timeout_ms, timeout_bytes) == SOCKET_ERROR)
      return 0;
    result = send(socket_handle, (const char *)bytes + sent,
                  (int)(byte_count - sent), 0);
    if (result == SOCKET_ERROR || result == 0)
      return 0;
    sent += (uint32_t)result;
  }
  return 1;
}

static int submit_receive(case_driver *driver, uint32_t mailbox_id) {
  driver_mailbox *mailbox = &driver->mailboxes[mailbox_id];
  io_request *request = &mailbox->io;
  int result;
  int socket_error;
  uint32_t remaining;
  if (request->socket == INVALID_SOCKET || request->inflight != 0u ||
      request->ready != 0u || request->receive_goal <= request->receive_offset)
    return failf("%s: invalid overlapped receive state for %s",
                 driver->result->id, mailbox->name);
  remaining = request->receive_goal - request->receive_offset;
  request->receive_buffer.buf = (CHAR *)request->bytes + request->receive_offset;
  request->receive_buffer.len = remaining;
  request->receive_flags = 0u;
  memset(&request->overlapped, 0, sizeof(request->overlapped));
  request->inflight = 1u;
  request->posted_asynchronously = 0u;
  ++driver->result->io.receive_submissions;
  result = WSARecv(request->socket, &request->receive_buffer, 1u, NULL,
                   &request->receive_flags, &request->overlapped, NULL);
  if (result == 0) {
    request->posted_asynchronously = 0u;
  } else {
    socket_error = WSAGetLastError();
    if (socket_error != WSA_IO_PENDING) {
      request->inflight = 0u;
      ++driver->result->io.receive_completions;
      request->last_error = socket_error;
      request->ready = 1u;
      if (!add_io_event(driver, IO_EVENT_RECV_TERMINAL, mailbox_id,
                        (uint32_t)socket_error, 0u, 0, 0u, 0u))
        return 0;
      return 1;
    }
    request->posted_asynchronously = 1u;
  }
  ++driver->result->io.pending_receives;
  if (driver->result->io.pending_receives >
      driver->result->io.peak_pending_receives)
    driver->result->io.peak_pending_receives =
        driver->result->io.pending_receives;
  return add_io_event(driver, IO_EVENT_RECV_SUBMIT, mailbox_id, 0u, 0u, 0,
                      0u, request->posted_asynchronously != 0u ? 1u : 0u);
}

static int start_provider_request(case_driver *driver, uint32_t mailbox_id,
                                  uint32_t delay_ms, text_view response) {
  driver_mailbox *mailbox;
  io_request *request;
  struct sockaddr_in address;
  uint8_t frame[MAX_PROVIDER_FRAME_BYTES];
  uint32_t frame_bytes;
  uint32_t request_index;
  if (driver == NULL || mailbox_id >= MAILBOX_CAPACITY ||
      response.byte_count > MAX_PROVIDER_BYTES ||
      (response.byte_count != 0u && response.bytes == NULL) ||
      delay_ms > 1000u)
    return 0;
  mailbox = &driver->mailboxes[mailbox_id];
  request = &mailbox->io;
  if (mailbox->initialized == 0u || mailbox->name == NULL ||
      request->active != 0u || request->inflight != 0u ||
      request->socket != INVALID_SOCKET)
    return failf("%s: provider request overlaps active work for %s",
                 driver->result->id, mailbox->name == NULL ? "?" : mailbox->name);
  if (driver->result->provider_request_count >= MAX_PROVIDER_REQUESTS)
    return failf("%s: provider request record capacity exceeded",
                 driver->result->id);
  request_index = driver->result->provider_request_count;
  request->socket = WSASocketW(AF_INET, SOCK_STREAM, IPPROTO_TCP, NULL, 0,
                               WSA_FLAG_OVERLAPPED);
  if (request->socket == INVALID_SOCKET)
    return failf("%s: WSASocket failed (%d)", driver->result->id,
                 WSAGetLastError());
  memset(&address, 0, sizeof(address));
  address.sin_family = AF_INET;
  address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
  address.sin_port = htons((u_short)driver->port);
  if (!connect_loopback_bounded(request->socket, &address))
    return failf("%s: loopback connect failed (%d)", driver->result->id,
                 WSAGetLastError());
  write_u32_le(frame, delay_ms);
  write_u32_le(frame + 4u, response.byte_count);
  if (response.byte_count != 0u)
    memcpy(frame + 8u, response.bytes, response.byte_count);
  frame_bytes = 8u + response.byte_count;
  if (!send_all_bounded(request->socket, frame, frame_bytes))
    return failf("%s: provider request send failed (%d)", driver->result->id,
                 WSAGetLastError());
  if (CreateIoCompletionPort((HANDLE)(uintptr_t)request->socket,
                             driver->completion_port,
                             (ULONG_PTR)(mailbox_id + 1u), 0u) == NULL)
    return failf("%s: socket IOCP association failed (%lu)",
                 driver->result->id, (unsigned long)GetLastError());
  request->active = 1u;
  request->receive_offset = 0u;
  request->receive_goal = 4u;
  request->response_length = 0u;
  request->last_error = 0;
  request->deadline_ms = GetTickCount64() + IO_TIMEOUT_MS;
  request->ready = 0u;
  request->cancel_requested = 0u;
  request->cancel_terminal = 0u;
  driver->result->provider_requests[request_index].delay_ms = delay_ms;
  driver->result->provider_requests[request_index].payload_bytes =
      response.byte_count;
  if (response.byte_count != 0u)
    memcpy(driver->result->provider_requests[request_index].payload,
           response.bytes, response.byte_count);
  ++driver->result->provider_request_count;
  ++driver->result->io.requests;
  if (!add_io_event(driver, IO_EVENT_PROVIDER_REQUEST, mailbox_id, 0u,
                    response.byte_count, 0, 0u, delay_ms))
    return 0;
  driver->result->io_events[driver->result->io_event_count - 1u]
      .provider_request_index = request_index;
  return submit_receive(driver, mailbox_id);
}

static int finish_iocp_completion(case_driver *driver, uint32_t mailbox_id,
                                  DWORD transferred, DWORD error_code) {
  io_request *request;
  uint32_t terminal_error = error_code;
  if (mailbox_id >= MAILBOX_CAPACITY)
    return failf("%s: invalid IOCP mailbox key",
                 driver->result->id);
  request = &driver->mailboxes[mailbox_id].io;
  if (request->inflight == 0u || driver->result->io.pending_receives == 0u)
    return failf("%s: terminal IOCP packet has no matching receive",
                 driver->result->id);
  request->inflight = 0u;
  --driver->result->io.pending_receives;
  ++driver->result->io.receive_completions;
  if (terminal_error == 0u && transferred == 0u &&
      request->cancel_requested == 0u)
    terminal_error = WSAECONNRESET;
  if (request->cancel_requested != 0u) {
    request->cancel_terminal = 1u;
    ++driver->result->io.cancel_acknowledgements;
  }
  if (!add_io_event(driver, IO_EVENT_RECV_TERMINAL, mailbox_id,
                    terminal_error, transferred, (int32_t)error_code, 0u,
                    request->cancel_requested != 0u ? 1u : 0u))
    return 0;
  if (request->cancel_requested != 0u) {
    return 1;
  } else if (error_code != 0u) {
    request->last_error = (int32_t)error_code;
    request->ready = 1u;
  } else if (transferred == 0u) {
    request->last_error = WSAECONNRESET;
    request->ready = 1u;
  } else {
    request->receive_offset += transferred;
    if (request->receive_offset > request->receive_goal)
      return failf("%s: WSARecv exceeded its requested buffer",
                   driver->result->id);
    if (request->receive_offset == 4u && request->receive_goal == 4u) {
      uint32_t payload_length = read_u32_le(request->bytes);
      if (payload_length > MAX_PROVIDER_BYTES) {
        request->last_error = WSAEMSGSIZE;
        request->ready = 1u;
      } else {
        request->response_length = payload_length;
        request->receive_goal = 4u + payload_length;
        if (request->receive_goal == request->receive_offset) {
          request->ready = 1u;
        } else if (!submit_receive(driver, mailbox_id)) {
          return 0;
        }
      }
    } else if (request->receive_offset == request->receive_goal) {
      request->ready = 1u;
    } else if (!submit_receive(driver, mailbox_id)) {
      return 0;
    }
  }
  return 1;
}

static DWORD bounded_wait_ms(const io_request *request) {
  uint64_t now = GetTickCount64();
  uint64_t remaining;
  if (now >= request->deadline_ms)
    return 0u;
  remaining = request->deadline_ms - now;
  return remaining > IO_TIMEOUT_MS ? IO_TIMEOUT_MS : (DWORD)remaining;
}

static int dispatch_one_completion(case_driver *driver, DWORD timeout_ms) {
  DWORD transferred = 0u;
  ULONG_PTR key = 0u;
  OVERLAPPED *overlapped = NULL;
  BOOL success;
  DWORD error_code = 0u;
  uint32_t mailbox_id;
  success = GetQueuedCompletionStatus(driver->completion_port, &transferred,
                                     &key, &overlapped, timeout_ms);
  if (!success) {
    error_code = GetLastError();
    if (overlapped == NULL) {
      if (error_code == WAIT_TIMEOUT)
        return 0;
      return failf("%s: GetQueuedCompletionStatus failed (%lu)",
                   driver->result->id, (unsigned long)error_code);
    }
  }
  if (overlapped == NULL || key == 0u || key > MAILBOX_CAPACITY)
    return failf("%s: malformed IOCP completion packet",
                 driver->result->id);
  mailbox_id = (uint32_t)(key - 1u);
  if (overlapped != &driver->mailboxes[mailbox_id].io.overlapped)
    return failf("%s: IOCP returned an unowned OVERLAPPED",
                 driver->result->id);
  return finish_iocp_completion(driver, mailbox_id, transferred, error_code);
}

static int wait_for_response(case_driver *driver, uint32_t mailbox_id) {
  io_request *request = &driver->mailboxes[mailbox_id].io;
  while (request->ready == 0u) {
    DWORD wait_ms = bounded_wait_ms(request);
    if (wait_ms == 0u)
      return failf("%s: provider response timed out for %s",
                   driver->result->id, driver->mailboxes[mailbox_id].name);
    if (!dispatch_one_completion(driver, wait_ms)) {
      if (g_failure[0] != '\0')
        return 0;
      if (GetTickCount64() >= request->deadline_ms)
        return failf("%s: provider response timed out for %s",
                     driver->result->id,
                     driver->mailboxes[mailbox_id].name);
    }
  }
  if (request->last_error != 0)
    return failf("%s: provider receive failed for %s (%d)",
                 driver->result->id, driver->mailboxes[mailbox_id].name,
                 request->last_error);
  return 1;
}

static int close_io_request(driver_mailbox *mailbox) {
  io_request *request;
  if (mailbox == NULL)
    return 0;
  request = &mailbox->io;
  if (request->inflight != 0u)
    return 0;
  if (request->socket != INVALID_SOCKET) {
    if (closesocket(request->socket) == SOCKET_ERROR)
      return 0;
  }
  memset(request, 0, sizeof(*request));
  request->socket = INVALID_SOCKET;
  return 1;
}

static int capture_snapshot(case_driver *driver, uint32_t step,
                            const char *op, uint32_t mailbox_id,
                            int32_t status, int32_t handler_status,
                            int32_t error_metadata_id) {
  case_result *output = driver->result;
  step_snapshot *snapshot;
  uint32_t index;
  if (output->step_count >= MAX_STEPS || mailbox_id >= MAILBOX_CAPACITY)
    return failf("%s: step snapshot capacity exceeded", output->id);
  snapshot = &output->steps[output->step_count];
  memset(snapshot, 0, sizeof(*snapshot));
  snapshot->step = step;
  snapshot->op = op;
  snapshot->mailbox = driver->mailboxes[mailbox_id].name;
  snapshot->status = status;
  snapshot->handler_status = handler_status;
  snapshot->error_metadata_id = error_metadata_id;
  if (!capture_mailbox(driver, mailbox_id, &snapshot->primary))
    return failf("%s: state decode failed at step %u (%s)", output->id,
                 step, op);
  for (index = 0u; index < driver->mailbox_count; ++index) {
    if (index == mailbox_id || driver->mailboxes[index].initialized == 0u)
      continue;
    if (snapshot->other_count >= MAILBOX_CAPACITY - 1u ||
        !capture_mailbox(driver, index,
                         &snapshot->other[snapshot->other_count].snapshot))
      return failf("%s: other mailbox state decode failed at step %u",
                   output->id, step);
    snapshot->other[snapshot->other_count].name = driver->mailboxes[index].name;
    ++snapshot->other_count;
  }
  ++output->step_count;
  return 1;
}

static int initialize_mailbox_step(case_driver *driver, uint32_t mailbox_id,
                                   const char *name, text_view seed,
                                   uint32_t step) {
  al_mailbox_call_info info;
  al_mailbox_result result;
  if (!add_mailbox(driver, mailbox_id, name) || !initialize_call_info(&info))
    return failf("%s: failed to set up mailbox %s", driver->result->id, name);
  result = al_mailbox_init_text(driver->fixture.runtime, mailbox_id,
                                seed.bytes, seed.byte_count, &info);
  if (result != AL_MAILBOX_OK)
    return failf("%s: initialize %s failed (%d, handler %d)",
                 driver->result->id, name, (int)result,
                 (int)info.handler_status);
  driver->mailboxes[mailbox_id].initialized = 1u;
  return capture_snapshot(driver, step, "initialize", mailbox_id, result,
                          info.handler_status, info.error_metadata_id);
}

static int begin_step(case_driver *driver, uint32_t mailbox_id,
                      text_view request_text, text_view first_response,
                      uint32_t delay_ms, uint32_t step) {
  al_mailbox_call_info info;
  al_mailbox_result result;
  driver_mailbox *mailbox = &driver->mailboxes[mailbox_id];
  if (mailbox->initialized == 0u || !initialize_call_info(&info))
    return failf("%s: Begin used an uninitialized mailbox",
                 driver->result->id);
  memset(&mailbox->token, 0, sizeof(mailbox->token));
  result = al_mailbox_begin_text(driver->fixture.runtime, mailbox_id,
                                 request_text.bytes, request_text.byte_count,
                                 &mailbox->token, &info);
  if (result != AL_MAILBOX_OK)
    return failf("%s: Begin failed for %s (%d, handler %d)",
                 driver->result->id, mailbox->name, (int)result,
                 (int)info.handler_status);
  mailbox->has_token = 1u;
  if (!start_provider_request(driver, mailbox_id, delay_ms, first_response))
    return 0;
  return capture_snapshot(driver, step, "begin", mailbox_id, result,
                          info.handler_status, info.error_metadata_id);
}

static int bytes_match(const uint8_t *left, uint32_t left_count,
                       text_view right) {
  return left_count == right.byte_count &&
         (left_count == 0u ||
          (right.bytes != NULL && memcmp(left, right.bytes, left_count) == 0));
}

static int resume_step(case_driver *driver, uint32_t mailbox_id,
                       text_view expected_response, uint32_t retry_delay_ms,
                       uint32_t step) {
  driver_mailbox *mailbox = &driver->mailboxes[mailbox_id];
  io_request *request = &mailbox->io;
  al_mailbox_call_info info;
  al_mailbox_result result;
  if (mailbox->has_token == 0u)
    return failf("%s: Resume has no Begin token for %s",
                 driver->result->id, mailbox->name);
  if (request->active == 0u) {
    if (!start_provider_request(driver, mailbox_id, retry_delay_ms,
                                expected_response))
      return 0;
  }
  if (!wait_for_response(driver, mailbox_id))
    return 0;
  if (!bytes_match(request->bytes + 4u, request->response_length,
                   expected_response))
    return failf("%s: provider response bytes differ from fixed fixture for %s",
                 driver->result->id, mailbox->name);
  if (!initialize_call_info(&info))
    return 0;
  result = al_mailbox_resume_text(driver->fixture.runtime, mailbox_id,
                                  &mailbox->token, request->bytes + 4u,
                                  request->response_length, &info);
  if (result == AL_MAILBOX_OK)
    mailbox->has_token = 0u;
  if (!close_io_request(mailbox))
    return failf("%s: failed to close completed provider request for %s",
                 driver->result->id, mailbox->name);
  return capture_snapshot(driver, step, "resume", mailbox_id, result,
                          info.handler_status, info.error_metadata_id);
}

static int cancel_pending_request(case_driver *driver, uint32_t mailbox_id,
                                  int intentional, uint32_t step) {
  driver_mailbox *mailbox = &driver->mailboxes[mailbox_id];
  io_request *request = &mailbox->io;
  al_mailbox_owning_stats before_stats;
  al_mailbox_owning_stats after_stats;
  al_mailbox_result result;
  BOOL cancelled;
  DWORD cancel_error = 0u;
  uint32_t pinned_before = 0u;
  uint32_t pinned_after = 0u;
  if (mailbox->has_token == 0u)
    return failf("%s: cancellation has no active token for %s",
                 driver->result->id, mailbox->name);
  if (request->inflight == 0u || request->socket == INVALID_SOCKET ||
      (intentional && request->posted_asynchronously == 0u))
    return failf("%s: cancellation was not issued against a pending WSARecv",
                 driver->result->id);
  if (!get_owning_stats(driver->fixture.runtime, &before_stats))
    return failf("%s: failed to read pre-cancel owning stats",
                 driver->result->id);
  pinned_before = before_stats.pinned_scratch_slots;
  request->cancel_requested = 1u;
  cancelled = CancelIoEx((HANDLE)(uintptr_t)request->socket,
                         &request->overlapped);
  if (!cancelled)
    cancel_error = GetLastError();
  ++driver->result->io.cancel_requests;
  if (!add_io_event(driver, IO_EVENT_CANCEL_REQUEST, mailbox_id, cancel_error,
                    0u, cancelled != 0 ? 1 : 0, pinned_before,
                    request->inflight != 0u ? 1u : 0u))
    return 0;
  while (request->cancel_terminal == 0u) {
    DWORD wait_ms = bounded_wait_ms(request);
    if (wait_ms == 0u)
      return failf("%s: cancellation terminal completion timed out for %s",
                   driver->result->id, mailbox->name);
    if (!dispatch_one_completion(driver, wait_ms)) {
      if (g_failure[0] != '\0')
        return 0;
      if (GetTickCount64() >= request->deadline_ms)
        return failf("%s: cancellation terminal completion timed out for %s",
                     driver->result->id, mailbox->name);
    }
  }
  if (request->inflight != 0u || !close_io_request(mailbox))
    return failf("%s: cancellation did not release its receive operation",
                 driver->result->id);
  result = al_mailbox_cancel_text(driver->fixture.runtime, mailbox_id,
                                  &mailbox->token);
  if (result != AL_MAILBOX_OK)
    return failf("%s: cancel_text failed for %s (%d)", driver->result->id,
                 mailbox->name, (int)result);
  mailbox->has_token = 0u;
  if (!get_owning_stats(driver->fixture.runtime, &after_stats))
    return failf("%s: failed to read post-cancel owning stats",
                 driver->result->id);
  pinned_after = after_stats.pinned_scratch_slots;
  if (!add_io_event(driver, IO_EVENT_CANCEL_API, mailbox_id, 0u, 0u,
                    (int32_t)result, pinned_after, 0u))
    return 0;
  return capture_snapshot(driver, step,
                          "host.cancelAfterProviderAcknowledgement",
                          mailbox_id, result, 0, -1);
}

static int dispose_case(case_driver *driver) {
  al_mailbox_result result;
  uint32_t index;
  if (driver == NULL || driver->fixture.runtime == NULL)
    return 0;
  for (index = 0u; index < MAILBOX_CAPACITY; ++index) {
    driver_mailbox *mailbox = &driver->mailboxes[index];
    if (mailbox->io.inflight != 0u)
      return failf("%s: pending WSARecv remains at runtime disposal",
                   driver->result->id);
    if (mailbox->io.socket != INVALID_SOCKET && !close_io_request(mailbox))
      return failf("%s: provider socket close failed during disposal",
                   driver->result->id);
  }
  if (driver->result->io.pending_receives != 0u)
    return failf("%s: receive count is nonzero at runtime disposal",
                 driver->result->id);
  if (!get_owning_stats(driver->fixture.runtime, &driver->result->stats))
    return failf("%s: final pre-dispose stats query failed",
                 driver->result->id);
  if (driver->result->stats.pending_mailboxes != 0u ||
      driver->result->stats.outstanding_scratch_leases != 0u ||
      driver->result->stats.pinned_scratch_slots != 0u)
    return failf("%s: controller retained pending work or scratch leases",
                 driver->result->id);
  result = al_mailbox_dispose(driver->fixture.runtime);
  if (result != AL_MAILBOX_OK)
    return failf("%s: runtime disposal failed (%d)", driver->result->id,
                 (int)result);
  if (al_mailbox_get_reset_stats(driver->fixture.runtime,
                                 &driver->result->reset_stats) !=
          AL_MAILBOX_OK ||
      driver->result->reset_stats.control_abi_version !=
          AL_MAILBOX_CONTROL_ABI_VERSION ||
      driver->result->reset_stats.struct_size !=
          sizeof(driver->result->reset_stats))
    return failf("%s: post-dispose reset stats query failed",
                 driver->result->id);
#if defined(AL_MAILBOX_FAST_RESET) && AL_MAILBOX_FAST_RESET
  if (driver->result->reset_stats.reset_profile !=
      AL_MAILBOX_RESET_PROFILE_FAST)
    return failf("%s: reset stats profile differs from fast build",
                 driver->result->id);
#else
  if (driver->result->reset_stats.reset_profile !=
      AL_MAILBOX_RESET_PROFILE_DIAGNOSTIC)
    return failf("%s: reset stats profile differs from diagnostic build",
                 driver->result->id);
#endif
  if (!get_owning_stats(driver->fixture.runtime,
                        &driver->result->after_dispose_stats))
    return failf("%s: post-dispose stats query failed", driver->result->id);
  if (driver->result->after_dispose_stats.pending_mailboxes != 0u ||
      driver->result->after_dispose_stats.outstanding_scratch_leases != 0u ||
      driver->result->after_dispose_stats.pinned_scratch_slots != 0u)
    return failf("%s: post-dispose stats show retained runtime resources",
                 driver->result->id);
  if (driver->fixture.storage != NULL &&
      VirtualFree(driver->fixture.storage, 0u, MEM_RELEASE) == 0)
    return failf("%s: runtime storage release failed (%lu)",
                 driver->result->id, (unsigned long)GetLastError());
  driver->fixture.storage = NULL;
  driver->fixture.runtime = NULL;
  driver->result->io.pending_receives_at_end =
      driver->result->io.pending_receives;
  return 1;
}

static int begin_case(case_driver *driver, case_result *result,
                      const char *case_id, uint32_t port, uint32_t policy) {
  uint32_t index;
  memset(driver, 0, sizeof(*driver));
  memset(result, 0, sizeof(*result));
  for (index = 0u; index < MAILBOX_CAPACITY; ++index)
    driver->mailboxes[index].io.socket = INVALID_SOCKET;
  result->id = case_id;
  driver->result = result;
  driver->completion_port = g_completion_port;
  driver->port = port;
  driver->policy = policy;
  return create_runtime(driver);
}

static int finish_case(case_driver *driver) {
  return dispose_case(driver);
}

static int run_unicode_case(uint32_t port, uint32_t policy) {
  static const uint8_t seed[] = "A\xf0\x9f\x99\x82";
  static const uint8_t request[] = "\xce\xb4";
  static const uint8_t response[] = {0x00u, 0xf0u, 0x9fu, 0x9au, 0x80u};
  static case_driver driver;
  case_result *result = &g_results[0];
  if (!begin_case(&driver, result, "unicode-and-nul", port, policy) ||
      !initialize_mailbox_step(&driver, 0u, "m1",
                               (text_view){seed, (uint32_t)sizeof(seed) - 1u},
                               0u) ||
      !begin_step(&driver, 0u,
                  (text_view){request, (uint32_t)sizeof(request) - 1u},
                  (text_view){response, (uint32_t)sizeof(response)}, 0u, 1u) ||
      !resume_step(&driver, 0u, (text_view){response, (uint32_t)sizeof(response)},
                   0u, 2u))
    return 0;
  return finish_case(&driver);
}

static int run_empty_case(uint32_t port, uint32_t policy) {
  static const uint8_t seed[] = "discarded-by-completion";
  static const uint8_t empty[] = "";
  static case_driver driver;
  case_result *result = &g_results[1];
  if (!begin_case(&driver, result, "empty-request-and-message", port,
                  policy) ||
      !initialize_mailbox_step(
          &driver, 0u, "empty",
          (text_view){seed, (uint32_t)sizeof(seed) - 1u}, 0u) ||
      !begin_step(&driver, 0u, (text_view){empty, 0u},
                  (text_view){empty, 0u}, 0u, 1u) ||
      !resume_step(&driver, 0u, (text_view){empty, 0u}, 0u, 2u))
    return 0;
  return finish_case(&driver);
}

static int run_sequential_case(uint32_t port, uint32_t policy) {
  static const uint8_t seed[] = "seed";
  static const uint8_t request1[] = "r1:";
  static const uint8_t response1[] = "one";
  static const uint8_t request2[] = "r2:";
  static const uint8_t response2[] = "two";
  static const uint8_t request3[] = "r3:";
  static const uint8_t response3[] = "three";
  static case_driver driver;
  case_result *result = &g_results[2];
  if (!begin_case(&driver, result,
                  "sequential-completions-replace-latest", port, policy) ||
      !initialize_mailbox_step(&driver, 0u, "steady",
                               (text_view){seed, (uint32_t)sizeof(seed) - 1u},
                               0u) ||
      !begin_step(&driver, 0u,
                  (text_view){request1, (uint32_t)sizeof(request1) - 1u},
                  (text_view){response1, (uint32_t)sizeof(response1) - 1u},
                  0u, 1u) ||
      !resume_step(&driver, 0u,
                   (text_view){response1, (uint32_t)sizeof(response1) - 1u},
                   0u, 2u) ||
      !begin_step(&driver, 0u,
                  (text_view){request2, (uint32_t)sizeof(request2) - 1u},
                  (text_view){response2, (uint32_t)sizeof(response2) - 1u},
                  0u, 3u) ||
      !resume_step(&driver, 0u,
                   (text_view){response2, (uint32_t)sizeof(response2) - 1u},
                   0u, 4u) ||
      !begin_step(&driver, 0u,
                  (text_view){request3, (uint32_t)sizeof(request3) - 1u},
                  (text_view){response3, (uint32_t)sizeof(response3) - 1u},
                  0u, 5u) ||
      !resume_step(&driver, 0u,
                   (text_view){response3, (uint32_t)sizeof(response3) - 1u},
                   0u, 6u))
    return 0;
  return finish_case(&driver);
}

static int run_cancel_case(uint32_t port, uint32_t policy) {
  static const uint8_t seed[] = "preserved";
  static const uint8_t request[] = "cancel-me";
  static const uint8_t response[] = "cancel-payload";
  static case_driver driver;
  case_result *result = &g_results[3];
  if (!begin_case(&driver, result,
                  "cancel-after-begin-preserves-updated-state", port,
                  policy) ||
      !initialize_mailbox_step(&driver, 0u, "cancelled",
                               (text_view){seed, (uint32_t)sizeof(seed) - 1u},
                               0u) ||
      !begin_step(&driver, 0u,
                  (text_view){request, (uint32_t)sizeof(request) - 1u},
                  (text_view){response, (uint32_t)sizeof(response) - 1u},
                  CANCEL_DELAY_MS, 1u) ||
      !cancel_pending_request(&driver, 0u, 1, 2u))
    return 0;
  return finish_case(&driver);
}

static int run_retry_case(uint32_t port, uint32_t policy) {
  static const uint8_t seed[] = "prefix";
  static const uint8_t request[] = "operation-";
  static const uint8_t failure[] = "FAIL";
  static const uint8_t response[] = "done";
  static case_driver driver;
  case_result *result = &g_results[4];
  if (!begin_case(&driver, result, "failed-resume-retries-same-token", port,
                  policy) ||
      !initialize_mailbox_step(&driver, 0u, "retry",
                               (text_view){seed, (uint32_t)sizeof(seed) - 1u},
                               0u) ||
      !begin_step(&driver, 0u,
                  (text_view){request, (uint32_t)sizeof(request) - 1u},
                  (text_view){failure, (uint32_t)sizeof(failure) - 1u}, 0u,
                  1u) ||
      !resume_step(&driver, 0u,
                   (text_view){failure, (uint32_t)sizeof(failure) - 1u},
                   0u, 2u) ||
      !resume_step(&driver, 0u,
                   (text_view){response, (uint32_t)sizeof(response) - 1u},
                   0u, 3u))
    return 0;
  return finish_case(&driver);
}

static int run_interleaved_case(uint32_t port, uint32_t policy) {
  static const uint8_t seed_a[] = "alpha";
  static const uint8_t seed_b[] = "beta";
  static const uint8_t request_a[] = "a-request";
  static const uint8_t request_b[] = "b-request";
  static const uint8_t failure[] = "FAIL";
  static const uint8_t response_b[] = ":ok";
  static const uint8_t retry_a[] = ":ok";
  static case_driver driver;
  case_result *result = &g_results[5];
  if (!begin_case(&driver, result, "two-mailboxes-remain-independent", port,
                  policy) ||
      !initialize_mailbox_step(&driver, 0u, "A",
                               (text_view){seed_a, (uint32_t)sizeof(seed_a) - 1u},
                               0u) ||
      !initialize_mailbox_step(&driver, 1u, "B",
                               (text_view){seed_b, (uint32_t)sizeof(seed_b) - 1u},
                               1u) ||
      !begin_step(&driver, 0u,
                  (text_view){request_a, (uint32_t)sizeof(request_a) - 1u},
                  (text_view){failure, (uint32_t)sizeof(failure) - 1u}, 700u,
                  2u) ||
      !begin_step(&driver, 1u,
                  (text_view){request_b, (uint32_t)sizeof(request_b) - 1u},
                  (text_view){response_b, (uint32_t)sizeof(response_b) - 1u},
                  100u, 3u) ||
      !resume_step(&driver, 0u,
                   (text_view){failure, (uint32_t)sizeof(failure) - 1u}, 0u,
                   4u) ||
      !resume_step(&driver, 1u,
                   (text_view){response_b, (uint32_t)sizeof(response_b) - 1u},
                   0u, 5u) ||
      !resume_step(&driver, 0u,
                   (text_view){retry_a, (uint32_t)sizeof(retry_a) - 1u}, 0u,
                   6u))
    return 0;
  return finish_case(&driver);
}

static int run_maximum_case(uint32_t port, uint32_t policy) {
  static const uint8_t seed[] = "small";
  static case_driver driver;
  case_result *result = &g_results[6];
  memset(g_large_request, 'R', sizeof(g_large_request));
  memset(g_large_response, 'M', sizeof(g_large_response));
  if (!begin_case(&driver, result,
                  "maximum-sized-request-and-message", port, policy) ||
      !initialize_mailbox_step(&driver, 0u, "large",
                               (text_view){seed, (uint32_t)sizeof(seed) - 1u},
                               0u) ||
      !begin_step(&driver, 0u,
                  (text_view){g_large_request, MAX_PROVIDER_BYTES},
                  (text_view){g_large_response, MAX_PROVIDER_BYTES}, 0u, 1u) ||
      !resume_step(&driver, 0u,
                   (text_view){g_large_response, MAX_PROVIDER_BYTES}, 0u,
                   2u))
    return 0;
  return finish_case(&driver);
}

static void json_ascii(const char *text) {
  const unsigned char *cursor = (const unsigned char *)text;
  putchar('"');
  while (*cursor != 0u) {
    unsigned char byte = *cursor++;
    if (byte == '"' || byte == '\\') {
      putchar('\\');
      putchar(byte);
    } else if (byte < 0x20u) {
      printf("\\u%04x", (unsigned int)byte);
    } else {
      putchar(byte);
    }
  }
  putchar('"');
}

static void json_utf8_payload(const uint8_t *bytes, uint32_t byte_count) {
  uint32_t index = 0u;
  putchar('"');
  while (index < byte_count) {
    uint8_t byte = bytes[index];
    if (byte == (uint8_t)'"' || byte == (uint8_t)'\\') {
      putchar('\\');
      putchar((int)byte);
      ++index;
    } else if (byte < 0x20u) {
      printf("\\u%04x", (unsigned int)byte);
      ++index;
    } else if (byte == 0xe2u && index + 2u < byte_count &&
               bytes[index + 1u] == 0x80u &&
               (bytes[index + 2u] == 0xa8u || bytes[index + 2u] == 0xa9u)) {
      fputs(bytes[index + 2u] == 0xa8u ? "\\u2028" : "\\u2029", stdout);
      index += 3u;
    } else {
      putchar((int)byte);
      ++index;
    }
  }
  putchar('"');
}

static void emit_utf8_scalar(uint32_t scalar) {
  if (scalar <= 0x7fu) {
    putchar((int)scalar);
  } else if (scalar <= 0x7ffu) {
    putchar((int)(0xc0u | (scalar >> 6u)));
    putchar((int)(0x80u | (scalar & 0x3fu)));
  } else if (scalar <= 0xffffu) {
    putchar((int)(0xe0u | (scalar >> 12u)));
    putchar((int)(0x80u | ((scalar >> 6u) & 0x3fu)));
    putchar((int)(0x80u | (scalar & 0x3fu)));
  } else {
    putchar((int)(0xf0u | (scalar >> 18u)));
    putchar((int)(0x80u | ((scalar >> 12u) & 0x3fu)));
    putchar((int)(0x80u | ((scalar >> 6u) & 0x3fu)));
    putchar((int)(0x80u | (scalar & 0x3fu)));
  }
}

static void json_utf16(const decoded_text *text) {
  uint32_t index;
  putchar('"');
  for (index = 0u; index < text->unit_count; ++index) {
    uint16_t unit = text->units[index];
    uint32_t scalar = unit;
    if (unit == '"' || unit == '\\') {
      putchar('\\');
      putchar((int)unit);
    } else if (unit == '\b') {
      fputs("\\b", stdout);
    } else if (unit == '\f') {
      fputs("\\f", stdout);
    } else if (unit == '\n') {
      fputs("\\n", stdout);
    } else if (unit == '\r') {
      fputs("\\r", stdout);
    } else if (unit == '\t') {
      fputs("\\t", stdout);
    } else if (unit < 0x20u) {
      printf("\\u%04x", (unsigned int)unit);
    } else if (unit >= 0xd800u && unit <= 0xdbffu) {
      if (index + 1u < text->unit_count &&
          text->units[index + 1u] >= 0xdc00u &&
          text->units[index + 1u] <= 0xdfffu) {
        scalar = 0x10000u +
                 (((uint32_t)unit - 0xd800u) << 10u) +
                 ((uint32_t)text->units[index + 1u] - 0xdc00u);
        ++index;
        emit_utf8_scalar(scalar);
      } else {
        printf("\\u%04x", (unsigned int)unit);
      }
    } else if (unit >= 0xdc00u && unit <= 0xdfffu) {
      printf("\\u%04x", (unsigned int)unit);
    } else if (unit == 0x2028u || unit == 0x2029u) {
      printf("\\u%04x", (unsigned int)unit);
    } else {
      emit_utf8_scalar(scalar);
    }
  }
  putchar('"');
}

static void print_token(const mailbox_snapshot *snapshot) {
  if (snapshot->has_token == 0u) {
    fputs("null", stdout);
    return;
  }
  printf("[\"%016" PRIx64 "\",\"%016" PRIx64
         "\",\"%016" PRIx64 "\"]",
         snapshot->token.opaque[0], snapshot->token.opaque[1],
         snapshot->token.opaque[2]);
}

static void print_mailbox_snapshot(const mailbox_snapshot *snapshot) {
  printf("{\"pending\":%s,\"state\":{\"attempted\":%" PRId64
         ",\"completed\":%" PRId64 ",\"latest\":",
         snapshot->pending != 0u ? "true" : "false",
         snapshot->state.attempted, snapshot->state.completed);
  json_utf16(&snapshot->state.latest);
  fputs("},\"continuation\":", stdout);
  if (snapshot->has_continuation != 0u) {
    fputs("{\"request\":", stdout);
    json_utf16(&snapshot->continuation_request);
    putchar('}');
  } else {
    fputs("null", stdout);
  }
  fputs(",\"token\":", stdout);
  print_token(snapshot);
  putchar('}');
}

static void print_step(const step_snapshot *step) {
  printf("{\"step\":%u,\"op\":", step->step);
  json_ascii(step->op);
  fputs(",\"mailbox\":", stdout);
  json_ascii(step->mailbox);
  printf(",\"status\":%d,\"handlerStatus\":%d,\"errorMetadataId\":%d,"
         "\"pending\":%s,\"state\":{\"attempted\":%" PRId64
         ",\"completed\":%" PRId64 ",\"latest\":",
         step->status, step->handler_status, step->error_metadata_id,
         step->primary.pending != 0u ? "true" : "false",
         step->primary.state.attempted, step->primary.state.completed);
  json_utf16(&step->primary.state.latest);
  fputs("},\"continuation\":", stdout);
  if (step->primary.has_continuation != 0u) {
    fputs("{\"request\":", stdout);
    json_utf16(&step->primary.continuation_request);
    putchar('}');
  } else {
    fputs("null", stdout);
  }
  fputs(",\"token\":", stdout);
  print_token(&step->primary);
  fputs(",\"otherMailboxes\":{", stdout);
  for (uint32_t index = 0u; index < step->other_count; ++index) {
    if (index != 0u)
      putchar(',');
    json_ascii(step->other[index].name);
    putchar(':');
    print_mailbox_snapshot(&step->other[index].snapshot);
  }
  fputs("}}", stdout);
}

static void print_io_event(const case_result *result, const io_event *event) {
  const char *kind = "unknown";
  switch ((io_event_kind)event->kind) {
  case IO_EVENT_PROVIDER_REQUEST:
    kind = "provider-request";
    break;
  case IO_EVENT_RECV_SUBMIT:
    kind = "recv-submit";
    break;
  case IO_EVENT_RECV_TERMINAL:
    kind = "recv-terminal";
    break;
  case IO_EVENT_CANCEL_REQUEST:
    kind = "cancel-request";
    break;
  case IO_EVENT_CANCEL_API:
    kind = "cancel-api";
    break;
  }
  printf("{\"sequence\":%u,\"kind\":", event->sequence);
  json_ascii(kind);
  fputs(",\"mailbox\":", stdout);
  json_ascii(event->mailbox_name == NULL ? "?" : event->mailbox_name);
  printf(",\"pendingReceives\":%u", event->pending_receives);
  switch ((io_event_kind)event->kind) {
  case IO_EVENT_PROVIDER_REQUEST: {
    const provider_request_record *request = NULL;
    if (result != NULL &&
        event->provider_request_index < result->provider_request_count)
      request = &result->provider_requests[event->provider_request_index];
    printf(",\"delayMs\":%u,\"payloadBytes\":%u,\"payload\":",
           request == NULL ? event->flags : request->delay_ms,
           request == NULL ? event->transferred : request->payload_bytes);
    if (request == NULL)
      fputs("null", stdout);
    else
      json_utf8_payload(request->payload, request->payload_bytes);
    break;
  }
  case IO_EVENT_RECV_SUBMIT:
    printf(",\"asynchronous\":%s",
           event->flags != 0u ? "true" : "false");
    break;
  case IO_EVENT_RECV_TERMINAL:
    printf(",\"error\":%u,\"transferred\":%u,"
           "\"cancelAcknowledgement\":%s",
           event->error_code, event->transferred,
           event->flags != 0u ? "true" : "false");
    break;
  case IO_EVENT_CANCEL_REQUEST:
    printf(",\"result\":%d,\"error\":%u,"
           "\"cancelIoSucceeded\":%s,\"pinnedScratchSlots\":%u",
           event->result, event->error_code,
           event->result != 0 ? "true" : "false",
           event->pinned_scratch_slots);
    break;
  case IO_EVENT_CANCEL_API:
    printf(",\"result\":%d,\"pinnedScratchSlots\":%u",
           event->result, event->pinned_scratch_slots);
    break;
  }
  putchar('}');
}

static void print_stats(const al_mailbox_owning_stats *stats) {
  printf("{\"mailboxCapacity\":%u,\"initializedMailboxes\":%u,"
         "\"pendingMailboxes\":%u,\"storageReservedBytes\":%" PRIu64
         ",\"retainedReservedBytes\":%" PRIu64
         ",\"scratchReservedBytes\":%" PRIu64
         ",\"textStagingReservedBytes\":%" PRIu64
         ",\"liveRetainedBytes\":%" PRIu64
         ",\"liveRetainedRoots\":%" PRIu64
         ",\"scratchHighWaterBytes\":%" PRIu64
         ",\"utf8InputBytes\":%" PRIu64
         ",\"utf16StagingBytes\":%" PRIu64
         ",\"inputImportBytes\":%" PRIu64
         ",\"publicationCopyBytes\":%" PRIu64
         ",\"deepCopyBytes\":%" PRIu64
         ",\"moveBytes\":%" PRIu64
         ",\"returnedOutputDescriptors\":%" PRIu64
         ",\"turnResetBytes\":%" PRIu64
         ",\"handlerInvocations\":%" PRIu64
         ",\"handlerFailures\":%" PRIu64
         ",\"scratchLeaseAcquisitions\":%" PRIu64
         ",\"scratchLeaseReturns\":%" PRIu64
         ",\"outstandingScratchLeases\":%u,\"scratchSlotCapacity\":%u,"
         "\"pinnedScratchSlots\":%u,\"suspensionPolicy\":%u,"
         "\"pinnedScratchBytes\":%" PRIu64
         ",\"beginPublicationCopyBytes\":%" PRIu64
         ",\"resumeRootImportBytes\":%" PRIu64 "}",
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
         stats->outstanding_scratch_leases, stats->scratch_slot_capacity,
         stats->pinned_scratch_slots, stats->suspension_policy,
         stats->pinned_scratch_bytes, stats->begin_publication_copy_bytes,
         stats->resume_root_import_bytes);
}

static uint64_t saturating_add_u64(uint64_t left, uint64_t right) {
  return right > UINT64_MAX - left ? UINT64_MAX : left + right;
}

static void print_reset_telemetry(void) {
  uint32_t index;
  uint32_t valid = 1u;
  al_mailbox_reset_stats total;
  memset(&total, 0, sizeof(total));
  total.control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  total.struct_size = (uint32_t)sizeof(total);
#if defined(AL_MAILBOX_FAST_RESET) && AL_MAILBOX_FAST_RESET
  total.reset_profile = AL_MAILBOX_RESET_PROFILE_FAST;
#else
  total.reset_profile = AL_MAILBOX_RESET_PROFILE_DIAGNOSTIC;
#endif
  for (index = 0u; index < MAX_CASES; ++index) {
    const al_mailbox_reset_stats *stats = &g_results[index].reset_stats;
    if (stats->control_abi_version != AL_MAILBOX_CONTROL_ABI_VERSION ||
        stats->struct_size != sizeof(*stats) ||
        stats->reset_profile != total.reset_profile) {
      valid = 0u;
      continue;
    }
    total.full_capacity_payload_write_bytes_requested = saturating_add_u64(
        total.full_capacity_payload_write_bytes_requested,
        stats->full_capacity_payload_write_bytes_requested);
    total.live_prefix_payload_write_bytes_requested = saturating_add_u64(
        total.live_prefix_payload_write_bytes_requested,
        stats->live_prefix_payload_write_bytes_requested);
    total.bitmap_store_operations = saturating_add_u64(
        total.bitmap_store_operations, stats->bitmap_store_operations);
    total.turn_reset_cursor_extent_bytes = saturating_add_u64(
        total.turn_reset_cursor_extent_bytes,
        stats->turn_reset_cursor_extent_bytes);
  }
  printf("{\"valid\":%s,\"profile\":\"%s\","
         "\"scope\":\"owningScratchCheckoutRelease\","
         "\"payloadWriteSemantics\":\"logicalRuntimeRequestedBytesNotHardwareTraffic\","
         "\"initializationPoisonWrites\":\"excluded: scratch, retained banks, text staging\","
         "\"fullCapacityPayloadWriteBytesRequested\":%" PRIu64 ","
         "\"livePrefixPayloadWriteBytesRequested\":%" PRIu64 ","
         "\"bitmapStoreOperations\":%" PRIu64 ","
         "\"turnResetCursorExtentBytes\":%" PRIu64 "}",
         valid != 0u ? "true" : "false",
         valid != 0u && total.reset_profile == AL_MAILBOX_RESET_PROFILE_FAST
             ? "fast"
             : (valid != 0u ? "diagnostic" : "invalid"),
         total.full_capacity_payload_write_bytes_requested,
         total.live_prefix_payload_write_bytes_requested,
         total.bitmap_store_operations, total.turn_reset_cursor_extent_bytes);
}

static void print_case(const case_result *result) {
  uint32_t index;
  printf("{\"id\":");
  json_ascii(result->id);
  fputs(",\"steps\":[", stdout);
  for (index = 0u; index < result->step_count; ++index) {
    if (index != 0u)
      putchar(',');
    print_step(&result->steps[index]);
  }
  printf("],\"io\":{\"requests\":%u,\"receiveSubmissions\":%u,"
         "\"receiveCompletions\":%u,\"cancelRequests\":%u,"
         "\"cancelAcknowledgements\":%u,\"peakPendingReceives\":%u,"
         "\"pendingReceivesAtEnd\":%u},\"ioEvents\":[",
         result->io.requests, result->io.receive_submissions,
         result->io.receive_completions, result->io.cancel_requests,
         result->io.cancel_acknowledgements,
         result->io.peak_pending_receives,
         result->io.pending_receives_at_end);
  for (index = 0u; index < result->io_event_count; ++index) {
    if (index != 0u)
      putchar(',');
    print_io_event(result, &result->io_events[index]);
  }
  fputs("],\"stats\":", stdout);
  print_stats(&result->stats);
  fputs(",\"afterDisposeStats\":", stdout);
  print_stats(&result->after_dispose_stats);
  putchar('}');
}

static int parse_port(const char *text, uint32_t *port) {
  char *end = NULL;
  unsigned long value;
  if (text == NULL || port == NULL || *text == '\0')
    return 0;
  value = strtoul(text, &end, 10);
  if (end == text || *end != '\0' || value == 0ul || value > 65535ul)
    return 0;
  *port = (uint32_t)value;
  return 1;
}

static int run_all_cases(uint32_t port, uint32_t policy) {
  return run_unicode_case(port, policy) && run_empty_case(port, policy) &&
         run_sequential_case(port, policy) && run_cancel_case(port, policy) &&
         run_retry_case(port, policy) && run_interleaved_case(port, policy) &&
         run_maximum_case(port, policy);
}

int main(int argc, char **argv) {
  const char *module_path = NULL;
  const char *policy_text = NULL;
  uint32_t port = 0u;
  uint32_t policy;
  int index;
  HMODULE library = NULL;
  al_owning_mailbox_module_fn get_module;
  WSADATA winsock_data;
  int winsock_ready = 0;
  int success = 0;
  if (argc != 7 || strcmp(argv[1], "--module") != 0 ||
      strcmp(argv[3], "--policy") != 0 || strcmp(argv[5], "--port") != 0 ||
      !parse_port(argv[6], &port)) {
    fprintf(stderr,
            "Usage: native-realio-mailbox --module <dll> --policy return|keep --port <loopback-provider-port>\n");
    return 2;
  }
  module_path = argv[2];
  policy_text = argv[4];
  if (strcmp(policy_text, "return") == 0)
    policy = AL_MAILBOX_OWNING_POLICY_RETURN;
  else if (strcmp(policy_text, "keep") == 0)
    policy = AL_MAILBOX_OWNING_POLICY_KEEP_ASSOCIATED;
  else {
    fprintf(stderr, "Policy must be return or keep.\n");
    return 2;
  }
  library = LoadLibraryA(module_path);
  if (library == NULL) {
    fprintf(stderr, "Could not load module DLL (%lu).\n",
            (unsigned long)GetLastError());
    goto done;
  }
  get_module = (al_owning_mailbox_module_fn)(uintptr_t)GetProcAddress(
      library, "agentlang_owning_mailbox_module");
  if (get_module == NULL) {
    fprintf(stderr, "Owning module export is missing (%lu).\n",
            (unsigned long)GetLastError());
    goto done;
  }
  g_module = get_module();
  if (!validate_module(g_module, &g_roles)) {
    fprintf(stderr,
            "Module ABI, role table, or State/Continuation field signature is unsupported.\n");
    goto done;
  }
  if (WSAStartup(MAKEWORD(2, 2), &winsock_data) != 0) {
    fprintf(stderr, "WSAStartup failed.\n");
    goto done;
  }
  winsock_ready = 1;
  g_completion_port = CreateIoCompletionPort(INVALID_HANDLE_VALUE, NULL, 0u, 1u);
  if (g_completion_port == NULL) {
    fprintf(stderr, "IOCP creation failed (%lu).\n",
            (unsigned long)GetLastError());
    goto done;
  }
  if (!run_all_cases(port, policy)) {
    fprintf(stderr, "%s\n", g_failure[0] == '\0'
                                  ? "Native correctness driver failed."
                                  : g_failure);
    /* An error path may still have an OS-owned OVERLAPPED receive. The driver
     * storage has static lifetime; return immediately so process teardown
     * closes the sockets and IOCP without freeing or reusing that storage. */
    return 1;
  }
  success = 1;
  printf("{\"schemaVersion\":1,\"policy\":");
  json_ascii(policy == AL_MAILBOX_OWNING_POLICY_RETURN ? "return" : "keep");
  fputs(",\"resetTelemetry\":", stdout);
  print_reset_telemetry();
  fputs(",\"cases\":[", stdout);
  for (index = 0; index < MAX_CASES; ++index) {
    if (index != 0)
      putchar(',');
    print_case(&g_results[index]);
  }
  fputs("]}\n", stdout);

done:
  if (g_completion_port != NULL)
    CloseHandle(g_completion_port);
  if (winsock_ready)
    WSACleanup();
  if (library != NULL)
    FreeLibrary(library);
  return success ? 0 : 1;
}
