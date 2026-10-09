#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>

#include <errno.h>
#include <inttypes.h>
#include <limits.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "mailbox_runtime.h"

enum {
  MAILBOX_COUNT = 16u,
  DEFAULT_SCRATCH_SLOTS = 4u,
  SCRATCH_BYTES = 262144u,
  RETAINED_BYTES = 65536u,
  TEXT_STAGING_BYTES = 16384u,
  MAX_PAYLOAD_BYTES = 4096u,
  MAX_REQUEST_FRAME_BYTES = 8u + MAX_PAYLOAD_BYTES,
  MAX_RESPONSE_BYTES = 4u + MAX_PAYLOAD_BYTES,
  CATCH_UP_BATCH = 64u,
  IO_TIMEOUT_MS = 5000u,
  DRAIN_TIMEOUT_MS = 5000u,
  MAX_WAIT_MS = 10u
};

typedef struct options {
  const char *module_path;
  const char *output_path;
  uint32_t port;
  uint32_t rate;
  uint32_t payload_bytes;
  uint32_t delay_ms;
  uint32_t warmup_ms;
  uint32_t duration_ms;
  uint32_t scratch_slots;
  uint32_t policy;
  uint32_t seen_module;
  uint32_t seen_output;
  uint32_t seen_port;
  uint32_t seen_rate;
  uint32_t seen_payload;
  uint32_t seen_delay;
  uint32_t seen_warmup;
  uint32_t seen_duration;
  uint32_t seen_scratch_slots;
  uint32_t seen_policy;
} options;

typedef struct mailbox_io {
  SOCKET socket;
  OVERLAPPED overlapped;
  WSABUF receive_buffer;
  DWORD receive_flags;
  uint8_t response[MAX_RESPONSE_BYTES];
  uint32_t receive_offset;
  uint32_t receive_goal;
  uint32_t response_bytes;
  uint32_t active;
  uint32_t inflight;
  uint32_t timed_out;
  uint32_t outcome_recorded;
  uint32_t faulted;
  uint32_t has_token;
  int32_t receive_error;
  uint64_t arrival_qpc;
  uint64_t io_deadline_qpc;
  al_mailbox_token token;
  uint64_t attempted;
  uint64_t completed;
} mailbox_io;

typedef struct run_result {
  uint64_t offered;
  uint64_t admitted;
  uint64_t rejected;
  uint64_t completed;
  uint64_t completed_within_window;
  uint64_t warmup_completed;
  uint64_t errors;
  uint64_t timed_out;
  uint64_t pending_at_end;
  uint64_t peak_pending;
  uint64_t missed_arrivals;
  uint64_t max_dispatch_lateness_us;
  uint64_t latency_count;
  uint64_t latency_capacity;
  uint64_t *latencies;
  uint32_t verified;
  uint32_t failed;
} run_result;

typedef struct runtime_fixture {
  al_mailbox_runtime *runtime;
  void *storage;
  size_t allocation_bytes;
  al_mailbox_owning_config config;
  al_mailbox_owning_storage_requirements requirements;
} runtime_fixture;

typedef struct module_roles {
  uint32_t string_index;
  uint32_t state_index;
  uint32_t continuation_index;
} module_roles;

static options g_options;
static const al_owning_mailbox_module *g_module;
static module_roles g_roles;
static runtime_fixture g_fixture;
static mailbox_io g_mailboxes[MAILBOX_COUNT];
static run_result g_result;
static HANDLE g_completion_port;
static LARGE_INTEGER g_qpc_frequency;
static uint8_t g_payload[MAX_PAYLOAD_BYTES];
static uint32_t g_payload_bytes;
static uint32_t g_measured_phase;
static uint32_t g_active_count;
static uint32_t g_winsock_started;
static uint64_t g_phase_cutoff_qpc;
static char g_failure[512];

static int failf(const char *format, ...) {
  if (g_failure[0] == '\0') {
    va_list args;
    va_start(args, format);
    (void)vsnprintf(g_failure, sizeof(g_failure), format, args);
    va_end(args);
  }
  return 0;
}

static uint64_t qpc_now(void) {
  LARGE_INTEGER value;
  if (!QueryPerformanceCounter(&value)) {
    failf("QueryPerformanceCounter failed (%lu)",
          (unsigned long)GetLastError());
    return 0u;
  }
  return (uint64_t)value.QuadPart;
}

static uint64_t milliseconds_to_qpc(uint64_t milliseconds) {
  return (milliseconds * (uint64_t)g_qpc_frequency.QuadPart) / 1000u;
}

static uint64_t qpc_to_microseconds(uint64_t ticks) {
  const uint64_t frequency = (uint64_t)g_qpc_frequency.QuadPart;
  return (ticks / frequency) * 1000000u +
         ((ticks % frequency) * 1000000u) / frequency;
}

static uint64_t read_u64_le(const uint8_t *bytes) {
  uint64_t value = 0u;
  uint32_t index;
  for (index = 0u; index < 8u; ++index)
    value |= (uint64_t)bytes[index] << (index * 8u);
  return value;
}

static uint32_t read_u32_le(const uint8_t *bytes) {
  return (uint32_t)bytes[0] | ((uint32_t)bytes[1] << 8u) |
         ((uint32_t)bytes[2] << 16u) | ((uint32_t)bytes[3] << 24u);
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

static int record_field(const al_owning_layout *layout, uint32_t record_index,
                        uint32_t field_index,
                        const al_owning_field_descriptor **out_field,
                        const al_owning_type_descriptor **out_child) {
  const al_owning_type_descriptor *record;
  const al_owning_field_descriptor *field;
  if (out_field == NULL || out_child == NULL ||
      !type_index_valid(layout, record_index))
    return 0;
  record = &layout->types[record_index];
  if (record->kind != AL_OWNING_TYPE_RECORD ||
      field_index >= record->field_count || layout->fields == NULL ||
      record->first_field > layout->field_count ||
      record->field_count > layout->field_count - record->first_field)
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
  return record_field(layout, record_index, field_index, &field, &child) &&
         child->kind == expected_kind &&
         (field->fixed_offset_bytes == AL_OWNING_LAYOUT_DYNAMIC_U32 ||
          field->fixed_offset_bytes == expected_offset);
}

static int validate_module(const al_owning_mailbox_module *module,
                           module_roles *roles) {
  const al_owning_layout *layout;
  const al_owning_mailbox_entry *initialize;
  const al_owning_mailbox_entry *begin;
  const al_owning_mailbox_entry *resume;
  const al_owning_type_descriptor *string_type;
  const al_owning_type_descriptor *state_type;
  const al_owning_type_descriptor *continuation_type;
  const al_owning_field_descriptor *field;
  const al_owning_type_descriptor *child;
  uint32_t string_index;
  uint32_t state_index;
  uint32_t continuation_index;
  uint32_t type_index;
  if (module == NULL || roles == NULL ||
      module->abi_version != AL_OWNING_MAILBOX_ABI_VERSION ||
      module->struct_size != sizeof(*module) || module->layout == NULL ||
      module->layout->abi_version != AL_OWNING_LAYOUT_ABI_VERSION ||
      module->layout->types == NULL || module->layout->type_count == 0u ||
      module->layout->type_count > AL_OWNING_LAYOUT_MAX_TYPES ||
      module->layout->field_count > AL_OWNING_LAYOUT_MAX_FIELDS ||
      (module->layout->field_count != 0u && module->layout->fields == NULL) ||
      module->associated_resume == NULL)
    return 0;
  layout = module->layout;
  for (type_index = 0u; type_index < layout->type_count; ++type_index) {
    const al_owning_type_descriptor *type = &layout->types[type_index];
    if (type->kind < AL_OWNING_TYPE_I64 ||
        type->kind > AL_OWNING_TYPE_STRING || type->case_count != 0u)
      return 0;
  }
  initialize = &module->entries[0];
  begin = &module->entries[1];
  resume = &module->entries[2];
  if (initialize->input_count != 1u || initialize->output_count != 1u ||
      begin->input_count != 2u || begin->output_count != 2u ||
      resume->input_count != 3u || resume->output_count != 1u ||
      initialize->execute == NULL || begin->execute == NULL ||
      resume->execute == NULL)
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
  string_type = &layout->types[string_index];
  state_type = &layout->types[state_index];
  continuation_type = &layout->types[continuation_index];
  if (string_type->kind != AL_OWNING_TYPE_STRING ||
      state_type->kind != AL_OWNING_TYPE_RECORD ||
      state_type->field_count != 3u ||
      continuation_type->kind != AL_OWNING_TYPE_RECORD ||
      continuation_type->field_count != 1u ||
      !record_field(layout, state_index, 0u, &field, &child) ||
      child->kind != AL_OWNING_TYPE_I64 || child->fixed_payload_bytes != 8u ||
      child->fixed_extent_bytes != 8u ||
      !validate_record_field(layout, state_index, 0u, AL_OWNING_TYPE_I64, 0u) ||
      !validate_record_field(layout, state_index, 1u, AL_OWNING_TYPE_I64, 8u) ||
      !validate_record_field(layout, state_index, 2u, AL_OWNING_TYPE_STRING, 16u) ||
      !validate_record_field(layout, continuation_index, 0u,
                             AL_OWNING_TYPE_STRING, 0u) ||
      !record_field(layout, continuation_index, 0u, &field, &child) ||
      child->type_id != string_type->type_id)
    return 0;
  roles->string_index = string_index;
  roles->state_index = state_index;
  roles->continuation_index = continuation_index;
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

static int absolute_windows_path(const char *path) {
  if (path == NULL || path[0] == '\0')
    return 0;
  return (((path[0] >= 'A' && path[0] <= 'Z') ||
           (path[0] >= 'a' && path[0] <= 'z')) &&
          path[1] == ':' && (path[2] == '\\' || path[2] == '/')) ||
         (path[0] == '\\' && path[1] == '\\');
}

static int parse_u32_value(const char *text, uint32_t minimum,
                           uint32_t maximum, uint32_t *out_value) {
  char *end = NULL;
  unsigned long long value;
  if (text == NULL || out_value == NULL || text[0] < '0' || text[0] > '9')
    return 0;
  errno = 0;
  value = strtoull(text, &end, 10);
  if (errno != 0 || end == text || *end != '\0' || value < minimum ||
      value > maximum)
    return 0;
  *out_value = (uint32_t)value;
  return 1;
}

static int parse_options(int argc, char **argv, options *parsed) {
  int index;
  memset(parsed, 0, sizeof(*parsed));
  parsed->scratch_slots = DEFAULT_SCRATCH_SLOTS;
  for (index = 1; index < argc; index += 2) {
    const char *name = argv[index];
    const char *value;
    uint32_t number;
    if (index + 1 >= argc)
      return 0;
    value = argv[index + 1];
    if (strcmp(name, "--module") == 0) {
      if (parsed->seen_module != 0u || value[0] == '\0')
        return 0;
      parsed->seen_module = 1u;
      parsed->module_path = value;
    } else if (strcmp(name, "--policy") == 0) {
      if (parsed->seen_policy != 0u)
        return 0;
      parsed->seen_policy = 1u;
      if (strcmp(value, "return") == 0)
        parsed->policy = AL_MAILBOX_OWNING_POLICY_RETURN;
      else if (strcmp(value, "keep") == 0)
        parsed->policy = AL_MAILBOX_OWNING_POLICY_KEEP_ASSOCIATED;
      else
        return 0;
    } else if (strcmp(name, "--port") == 0) {
      if (parsed->seen_port != 0u ||
          !parse_u32_value(value, 1u, 65535u, &number))
        return 0;
      parsed->seen_port = 1u;
      parsed->port = number;
    } else if (strcmp(name, "--rate") == 0) {
      if (parsed->seen_rate != 0u ||
          !parse_u32_value(value, 1u, 64000u, &number))
        return 0;
      parsed->seen_rate = 1u;
      parsed->rate = number;
    } else if (strcmp(name, "--payload-bytes") == 0) {
      if (parsed->seen_payload != 0u ||
          !parse_u32_value(value, 1u, MAX_PAYLOAD_BYTES, &number))
        return 0;
      parsed->seen_payload = 1u;
      parsed->payload_bytes = number;
    } else if (strcmp(name, "--delay-ms") == 0) {
      if (parsed->seen_delay != 0u ||
          !parse_u32_value(value, 0u, 1000u, &number))
        return 0;
      parsed->seen_delay = 1u;
      parsed->delay_ms = number;
    } else if (strcmp(name, "--warmup-ms") == 0) {
      if (parsed->seen_warmup != 0u ||
          !parse_u32_value(value, 0u, 10000u, &number))
        return 0;
      parsed->seen_warmup = 1u;
      parsed->warmup_ms = number;
    } else if (strcmp(name, "--duration-ms") == 0) {
      if (parsed->seen_duration != 0u ||
          !parse_u32_value(value, 100u, 30000u, &number))
        return 0;
      parsed->seen_duration = 1u;
      parsed->duration_ms = number;
    } else if (strcmp(name, "--scratch-slots") == 0) {
      if (parsed->seen_scratch_slots != 0u ||
          !parse_u32_value(value, 4u, 16u, &number) ||
          (number != 4u && number != 16u))
        return 0;
      parsed->seen_scratch_slots = 1u;
      parsed->scratch_slots = number;
    } else if (strcmp(name, "--output") == 0) {
      if (parsed->seen_output != 0u || !absolute_windows_path(value))
        return 0;
      parsed->seen_output = 1u;
      parsed->output_path = value;
    } else {
      return 0;
    }
  }
  return parsed->seen_module != 0u && parsed->seen_policy != 0u &&
         parsed->seen_port != 0u && parsed->seen_rate != 0u &&
         parsed->seen_payload != 0u && parsed->seen_delay != 0u &&
         parsed->seen_warmup != 0u && parsed->seen_duration != 0u &&
         parsed->seen_output != 0u;
}

static void initialize_requirements(
    al_mailbox_owning_storage_requirements *requirements) {
  memset(requirements, 0, sizeof(*requirements));
  requirements->control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  requirements->struct_size = (uint32_t)sizeof(*requirements);
}

static int create_runtime(void) {
  al_mailbox_result result;
  uint32_t mailbox_id;
  if (g_fixture.storage == NULL) {
    memset(&g_fixture.config, 0, sizeof(g_fixture.config));
    g_fixture.config.control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
    g_fixture.config.struct_size = (uint32_t)sizeof(g_fixture.config);
    g_fixture.config.mailbox_capacity = MAILBOX_COUNT;
    g_fixture.config.scratch_byte_capacity = SCRATCH_BYTES;
    g_fixture.config.retained_byte_capacity = RETAINED_BYTES;
    g_fixture.config.text_staging_byte_capacity = TEXT_STAGING_BYTES;
    g_fixture.config.scratch_slot_capacity = g_options.scratch_slots;
    g_fixture.config.suspension_policy = g_options.policy;
    initialize_requirements(&g_fixture.requirements);
    result = al_mailbox_get_owning_storage_requirements(
        g_module, &g_fixture.config, &g_fixture.requirements);
    if (result != AL_MAILBOX_OK || g_fixture.requirements.storage_bytes == 0u ||
        g_fixture.requirements.storage_bytes > (uint64_t)SIZE_MAX)
      return failf("owning storage requirements failed (%d)", (int)result);
    g_fixture.allocation_bytes = (size_t)g_fixture.requirements.storage_bytes;
    g_fixture.storage = VirtualAlloc(NULL, g_fixture.allocation_bytes,
                                     MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    if (g_fixture.storage == NULL)
      return failf("VirtualAlloc for mailbox storage failed (%lu)",
                   (unsigned long)GetLastError());
  }
  g_fixture.runtime = NULL;
  result = al_mailbox_runtime_init_owning(
      g_module, &g_fixture.config, g_fixture.storage,
      g_fixture.requirements.storage_bytes, &g_fixture.runtime);
  if (result != AL_MAILBOX_OK || g_fixture.runtime == NULL)
    return failf("owning runtime initialization failed (%d)", (int)result);
  for (mailbox_id = 0u; mailbox_id < MAILBOX_COUNT; ++mailbox_id) {
    al_mailbox_call_info info;
    if (!initialize_call_info(&info))
      return failf("call info initialization failed");
    result = al_mailbox_init_text(g_fixture.runtime, mailbox_id, NULL, 0u,
                                  &info);
    if (result != AL_MAILBOX_OK)
      return failf("mailbox initialization failed for %u (%d, handler %d)",
                   mailbox_id, (int)result, (int)info.handler_status);
  }
  return 1;
}

static int dispose_runtime(void) {
  al_mailbox_result result;
  if (g_fixture.runtime == NULL)
    return 1;
  result = al_mailbox_dispose(g_fixture.runtime);
  if (result != AL_MAILBOX_OK)
    return failf("runtime disposal failed (%d)", (int)result);
  g_fixture.runtime = NULL;
  return 1;
}

static int connect_socket_bounded(SOCKET socket_handle,
                                  const struct sockaddr_in *address) {
  u_long nonblocking = 1ul;
  int result;
  int connect_error;
  int socket_error = 0;
  int socket_error_bytes = (int)sizeof(socket_error);
  fd_set writable;
  fd_set exceptional;
  struct timeval timeout;
  if (ioctlsocket(socket_handle, FIONBIO, &nonblocking) == SOCKET_ERROR)
    return 0;
  result = connect(socket_handle, (const struct sockaddr *)address,
                   (int)sizeof(*address));
  if (result == SOCKET_ERROR) {
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
    timeout.tv_sec = 5;
    timeout.tv_usec = 0;
    result = select(0, NULL, &writable, &exceptional, &timeout);
    if (result == 0) {
      WSASetLastError(WSAETIMEDOUT);
      return 0;
    }
    if (result == SOCKET_ERROR ||
        getsockopt(socket_handle, SOL_SOCKET, SO_ERROR, (char *)&socket_error,
                   &socket_error_bytes) == SOCKET_ERROR ||
        socket_error != 0) {
      if (socket_error != 0)
        WSASetLastError(socket_error);
      return 0;
    }
  }
  nonblocking = 0ul;
  return ioctlsocket(socket_handle, FIONBIO, &nonblocking) != SOCKET_ERROR;
}

static int open_provider_connections(uint32_t port) {
  uint32_t mailbox_id;
  for (mailbox_id = 0u; mailbox_id < MAILBOX_COUNT; ++mailbox_id) {
    struct sockaddr_in address;
    int enabled = 1;
    g_mailboxes[mailbox_id].socket = WSASocketW(
        AF_INET, SOCK_STREAM, IPPROTO_TCP, NULL, 0u, WSA_FLAG_OVERLAPPED);
    if (g_mailboxes[mailbox_id].socket == INVALID_SOCKET)
      return failf("WSASocket for mailbox %u failed (%d)", mailbox_id,
                   WSAGetLastError());
    if (setsockopt(g_mailboxes[mailbox_id].socket, IPPROTO_TCP, TCP_NODELAY,
                   (const char *)&enabled, (int)sizeof(enabled)) == SOCKET_ERROR)
      return failf("TCP_NODELAY for mailbox %u failed (%d)", mailbox_id,
                   WSAGetLastError());
    memset(&address, 0, sizeof(address));
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    address.sin_port = htons((u_short)port);
    if (!connect_socket_bounded(g_mailboxes[mailbox_id].socket, &address))
      return failf("loopback connect for mailbox %u failed (%d)", mailbox_id,
                   WSAGetLastError());
    if (CreateIoCompletionPort((HANDLE)(uintptr_t)g_mailboxes[mailbox_id].socket,
                               g_completion_port,
                               (ULONG_PTR)(mailbox_id + 1u), 0u) == NULL)
      return failf("IOCP association for mailbox %u failed (%lu)", mailbox_id,
                   (unsigned long)GetLastError());
  }
  return 1;
}

static int send_all(SOCKET socket_handle, const uint8_t *bytes,
                    uint32_t byte_count) {
  uint32_t sent = 0u;
  DWORD timeout_ms = IO_TIMEOUT_MS;
  if (setsockopt(socket_handle, SOL_SOCKET, SO_SNDTIMEO,
                 (const char *)&timeout_ms, (int)sizeof(timeout_ms)) ==
      SOCKET_ERROR)
    return 0;
  while (sent < byte_count) {
    int result = send(socket_handle, (const char *)bytes + sent,
                      (int)(byte_count - sent), 0);
    if (result == SOCKET_ERROR || result == 0)
      return 0;
    sent += (uint32_t)result;
  }
  return 1;
}

static void close_mailbox_socket(mailbox_io *mailbox) {
  if (mailbox->socket != INVALID_SOCKET) {
    (void)shutdown(mailbox->socket, SD_BOTH);
    (void)closesocket(mailbox->socket);
    mailbox->socket = INVALID_SOCKET;
  }
}

static void record_operation_outcome(mailbox_io *mailbox, int timed_out) {
  if (mailbox->outcome_recorded != 0u)
    return;
  mailbox->outcome_recorded = 1u;
  if (timed_out != 0) {
    ++g_result.timed_out;
    mailbox->timed_out = 1u;
  } else {
    ++g_result.errors;
  }
  g_result.failed = 1u;
}

static void finish_mailbox_operation(mailbox_io *mailbox) {
  mailbox->active = 0u;
  mailbox->has_token = 0u;
  mailbox->receive_offset = 0u;
  mailbox->receive_goal = 0u;
  mailbox->response_bytes = 0u;
  mailbox->receive_error = 0;
  mailbox->io_deadline_qpc = 0u;
  memset(&mailbox->token, 0, sizeof(mailbox->token));
  if (g_active_count != 0u)
    --g_active_count;
}

static int cancel_mailbox_operation(uint32_t mailbox_id, int timed_out) {
  mailbox_io *mailbox = &g_mailboxes[mailbox_id];
  al_mailbox_result result;
  record_operation_outcome(mailbox, timed_out);
  if (mailbox->inflight != 0u)
    return failf("cannot release mailbox %u while WSARecv owns its buffer",
                 mailbox_id);
  if (mailbox->has_token != 0u && g_fixture.runtime != NULL) {
    result = al_mailbox_cancel_text(g_fixture.runtime, mailbox_id,
                                    &mailbox->token);
    if (result != AL_MAILBOX_OK)
      return failf("runtime cancellation for mailbox %u failed (%d)",
                   mailbox_id, (int)result);
  }
  mailbox->faulted = 1u;
  close_mailbox_socket(mailbox);
  mailbox->has_token = 0u;
  finish_mailbox_operation(mailbox);
  return 1;
}

static int post_receive(uint32_t mailbox_id) {
  mailbox_io *mailbox = &g_mailboxes[mailbox_id];
  int result;
  int socket_error;
  if (mailbox->socket == INVALID_SOCKET || mailbox->inflight != 0u ||
      mailbox->receive_goal <= mailbox->receive_offset ||
      mailbox->receive_goal > MAX_RESPONSE_BYTES)
    return failf("invalid receive state for mailbox %u", mailbox_id);
  mailbox->receive_buffer.buf = (CHAR *)mailbox->response +
                                mailbox->receive_offset;
  mailbox->receive_buffer.len = mailbox->receive_goal - mailbox->receive_offset;
  mailbox->receive_flags = 0u;
  memset(&mailbox->overlapped, 0, sizeof(mailbox->overlapped));
  mailbox->inflight = 1u;
  result = WSARecv(mailbox->socket, &mailbox->receive_buffer, 1u, NULL,
                   &mailbox->receive_flags, &mailbox->overlapped, NULL);
  if (result == SOCKET_ERROR) {
    socket_error = WSAGetLastError();
    if (socket_error != WSA_IO_PENDING) {
      mailbox->inflight = 0u;
      mailbox->receive_error = socket_error;
      return 0;
    }
  }
  return 1;
}

static int append_latency(uint64_t elapsed_qpc) {
  uint64_t micros = qpc_to_microseconds(elapsed_qpc);
  if (g_result.latency_count >= g_result.latency_capacity)
    return failf("latency sample capacity exceeded");
  g_result.latencies[g_result.latency_count++] = micros;
  return 1;
}

static int complete_provider_response(uint32_t mailbox_id) {
  mailbox_io *mailbox = &g_mailboxes[mailbox_id];
  al_mailbox_call_info info;
  al_mailbox_result result;
  uint64_t completed_qpc;
  uint32_t index;
  if (mailbox->active == 0u || mailbox->has_token == 0u ||
      mailbox->response_bytes != g_payload_bytes) {
    return cancel_mailbox_operation(mailbox_id, 0);
  }
  for (index = 0u; index < mailbox->response_bytes; ++index) {
    if (mailbox->response[4u + index] != g_payload[index])
      return cancel_mailbox_operation(mailbox_id, 0);
  }
  if (!initialize_call_info(&info))
    return failf("call info initialization failed");
  result = al_mailbox_resume_text(g_fixture.runtime, mailbox_id,
                                  &mailbox->token, mailbox->response + 4u,
                                  mailbox->response_bytes, &info);
  if (result != AL_MAILBOX_OK)
    return cancel_mailbox_operation(mailbox_id, 0);
  completed_qpc = qpc_now();
  if (g_failure[0] != '\0')
    return 0;
  ++mailbox->completed;
  if (g_measured_phase != 0u) {
    ++g_result.completed;
    if (completed_qpc <= g_phase_cutoff_qpc)
      ++g_result.completed_within_window;
    if (completed_qpc < mailbox->arrival_qpc ||
        !append_latency(completed_qpc - mailbox->arrival_qpc))
      return 0;
  } else {
    ++g_result.warmup_completed;
  }
  finish_mailbox_operation(mailbox);
  return 1;
}

static int handle_iocp_completion(uint32_t mailbox_id, DWORD transferred,
                                  DWORD error_code) {
  mailbox_io *mailbox;
  if (mailbox_id >= MAILBOX_COUNT)
    return failf("IOCP returned an invalid mailbox key");
  mailbox = &g_mailboxes[mailbox_id];
  if (mailbox->inflight == 0u)
    return failf("IOCP completion has no matching receive for mailbox %u",
                 mailbox_id);
  mailbox->inflight = 0u;
  if (mailbox->timed_out != 0u)
    return cancel_mailbox_operation(mailbox_id, 1);
  if (error_code != 0u) {
    mailbox->receive_error = (int32_t)error_code;
    return cancel_mailbox_operation(mailbox_id, 0);
  }
  if (transferred == 0u)
    return cancel_mailbox_operation(mailbox_id, 0);
  mailbox->receive_offset += transferred;
  if (mailbox->receive_offset > mailbox->receive_goal)
    return failf("WSARecv exceeded its target for mailbox %u", mailbox_id);
  if (mailbox->receive_offset < mailbox->receive_goal)
    return post_receive(mailbox_id);
  if (mailbox->receive_goal == 4u) {
    mailbox->response_bytes = read_u32_le(mailbox->response);
    if (mailbox->response_bytes > MAX_PAYLOAD_BYTES)
      return cancel_mailbox_operation(mailbox_id, 0);
    mailbox->receive_goal = 4u + mailbox->response_bytes;
    if (mailbox->receive_goal == mailbox->receive_offset)
      return complete_provider_response(mailbox_id);
    return post_receive(mailbox_id);
  }
  return complete_provider_response(mailbox_id);
}

/* Returns 1 on a timeout packet, 2 after dispatching a completion, and 0 on error. */
static int dispatch_one(DWORD timeout_ms) {
  DWORD transferred = 0u;
  ULONG_PTR key = 0u;
  OVERLAPPED *overlapped = NULL;
  BOOL success = GetQueuedCompletionStatus(g_completion_port, &transferred,
                                           &key, &overlapped, timeout_ms);
  DWORD error_code = success ? 0u : GetLastError();
  uint32_t mailbox_id;
  if (!success && overlapped == NULL) {
    if (error_code == WAIT_TIMEOUT)
      return 1;
    return failf("GetQueuedCompletionStatus failed (%lu)",
                 (unsigned long)error_code);
  }
  if (overlapped == NULL || key == 0u || key > MAILBOX_COUNT)
    return failf("IOCP returned a malformed completion packet");
  mailbox_id = (uint32_t)(key - 1u);
  if (overlapped != &g_mailboxes[mailbox_id].overlapped)
    return failf("IOCP returned an unowned OVERLAPPED for mailbox %u",
                 mailbox_id);
  if (!handle_iocp_completion(mailbox_id, transferred, error_code))
    return 0;
  return 2;
}

static uint64_t arrivals_in_window(uint32_t rate, uint32_t duration_ms) {
  uint64_t product = (uint64_t)rate * (uint64_t)duration_ms;
  return (product + 999u) / 1000u;
}

static uint64_t arrival_time(uint64_t epoch, uint64_t sequence,
                             uint32_t rate) {
  return epoch + (sequence * (uint64_t)g_qpc_frequency.QuadPart) / rate;
}

static DWORD wait_duration_ms(uint64_t now, uint64_t target) {
  uint64_t remaining;
  uint64_t milliseconds;
  if (target <= now)
    return 0u;
  remaining = target - now;
  milliseconds = (remaining * 1000u +
                  (uint64_t)g_qpc_frequency.QuadPart - 1u) /
                 (uint64_t)g_qpc_frequency.QuadPart;
  if (milliseconds > MAX_WAIT_MS)
    milliseconds = MAX_WAIT_MS;
  return milliseconds == 0u ? 1u : (DWORD)milliseconds;
}

static int accept_arrival(uint32_t mailbox_id, uint64_t scheduled_qpc,
                          uint64_t now_qpc, uint32_t delay_ms) {
  mailbox_io *mailbox = &g_mailboxes[mailbox_id];
  uint8_t frame[MAX_REQUEST_FRAME_BYTES];
  al_mailbox_call_info info;
  al_mailbox_result result;
  uint64_t lateness;
  uint32_t frame_bytes;
  if (g_measured_phase != 0u) {
    lateness = qpc_to_microseconds(now_qpc - scheduled_qpc);
    if (lateness > g_result.max_dispatch_lateness_us)
      g_result.max_dispatch_lateness_us = lateness;
  }
  if (mailbox->active != 0u || mailbox->faulted != 0u ||
      mailbox->socket == INVALID_SOCKET) {
    if (g_measured_phase != 0u)
      ++g_result.rejected;
    return 1;
  }
  memset(&mailbox->token, 0, sizeof(mailbox->token));
  if (!initialize_call_info(&info))
    return failf("call info initialization failed");
  result = al_mailbox_begin_text(g_fixture.runtime, mailbox_id, g_payload,
                                 g_payload_bytes, &mailbox->token, &info);
  if (result == AL_MAILBOX_BUSY || result == AL_MAILBOX_SCRATCH_CAPACITY ||
      result == AL_MAILBOX_RETAINED_CAPACITY ||
      result == AL_MAILBOX_TEXT_CAPACITY) {
    if (g_measured_phase != 0u)
      ++g_result.rejected;
    return 1;
  }
  if (result != AL_MAILBOX_OK)
    return failf("Begin failed for mailbox %u (%d, handler %d)", mailbox_id,
                 (int)result, (int)info.handler_status);
  mailbox->active = 1u;
  mailbox->has_token = 1u;
  mailbox->outcome_recorded = 0u;
  mailbox->timed_out = 0u;
  mailbox->arrival_qpc = scheduled_qpc;
  mailbox->io_deadline_qpc =
      now_qpc + milliseconds_to_qpc(IO_TIMEOUT_MS);
  ++mailbox->attempted;
  ++g_active_count;
  if (g_measured_phase != 0u) {
    ++g_result.admitted;
    if (g_active_count > g_result.peak_pending)
      g_result.peak_pending = g_active_count;
  }
  write_u32_le(frame, delay_ms);
  write_u32_le(frame + 4u, g_payload_bytes);
  memcpy(frame + 8u, g_payload, g_payload_bytes);
  frame_bytes = 8u + g_payload_bytes;
  if (!send_all(mailbox->socket, frame, frame_bytes)) {
    return cancel_mailbox_operation(mailbox_id, 0);
  }
  mailbox->receive_offset = 0u;
  mailbox->receive_goal = 4u;
  mailbox->response_bytes = 0u;
  if (!post_receive(mailbox_id))
    return cancel_mailbox_operation(mailbox_id, 0);
  return 1;
}

static int service_deadlines(uint64_t now, uint64_t hard_deadline) {
  uint32_t mailbox_id;
  for (mailbox_id = 0u; mailbox_id < MAILBOX_COUNT; ++mailbox_id) {
    mailbox_io *mailbox = &g_mailboxes[mailbox_id];
    if (mailbox->active == 0u || mailbox->outcome_recorded != 0u)
      continue;
    if (now >= mailbox->io_deadline_qpc || now >= hard_deadline) {
      mailbox->timed_out = 1u;
      record_operation_outcome(mailbox, 1);
      if (mailbox->inflight != 0u) {
        if (!CancelIoEx((HANDLE)(uintptr_t)mailbox->socket,
                        &mailbox->overlapped)) {
          DWORD error_code = GetLastError();
          if (error_code != ERROR_NOT_FOUND)
            return failf("CancelIoEx for mailbox %u failed (%lu)", mailbox_id,
                         (unsigned long)error_code);
        }
      } else if (!cancel_mailbox_operation(mailbox_id, 1)) {
        return 0;
      }
    }
  }
  return 1;
}

static int run_phase(uint32_t duration_ms, uint32_t measured,
                     uint32_t delay_ms) {
  uint64_t epoch;
  uint64_t cutoff;
  uint64_t drain_deadline;
  uint64_t total_arrivals;
  uint64_t sequence = 0u;
  if (duration_ms == 0u)
    return 1;
  epoch = qpc_now();
  if (g_failure[0] != '\0')
    return 0;
  cutoff = epoch + milliseconds_to_qpc(duration_ms);
  g_phase_cutoff_qpc = cutoff;
  drain_deadline = cutoff + milliseconds_to_qpc(DRAIN_TIMEOUT_MS);
  total_arrivals = arrivals_in_window(g_options.rate, duration_ms);
  g_measured_phase = measured;
  for (;;) {
    uint64_t now = qpc_now();
    uint32_t batch = 0u;
    if (g_failure[0] != '\0')
      return 0;
    if (now < cutoff) {
      while (batch < CATCH_UP_BATCH && sequence < total_arrivals) {
        uint64_t scheduled = arrival_time(epoch, sequence, g_options.rate);
        now = qpc_now();
        if (g_failure[0] != '\0')
          return 0;
        if (scheduled >= cutoff || scheduled > now || now >= cutoff)
          break;
        if (measured != 0u)
          ++g_result.offered;
        if (!accept_arrival((uint32_t)(sequence % MAILBOX_COUNT), scheduled,
                            now, delay_ms))
          return 0;
        ++sequence;
        ++batch;
      }
      if (!service_deadlines(now, drain_deadline))
        return 0;
      if (batch != 0u) {
        if (dispatch_one(0u) == 0)
          return 0;
      } else {
        uint64_t next = arrival_time(epoch, sequence, g_options.rate);
        DWORD wait_ms = wait_duration_ms(now, next);
        if (g_active_count != 0u && wait_ms > 1u)
          wait_ms = 1u;
        if (dispatch_one(wait_ms) == 0)
          return 0;
      }
      continue;
    }
    if (measured != 0u && sequence < total_arrivals) {
      uint64_t remaining = total_arrivals - sequence;
      g_result.offered += remaining;
      g_result.rejected += remaining;
      g_result.missed_arrivals += remaining;
      sequence = total_arrivals;
    }
    if (g_active_count == 0u)
      break;
    if (now >= drain_deadline) {
      uint32_t mailbox_id;
      for (mailbox_id = 0u; mailbox_id < MAILBOX_COUNT; ++mailbox_id) {
        mailbox_io *mailbox = &g_mailboxes[mailbox_id];
        if (mailbox->active != 0u && mailbox->outcome_recorded == 0u) {
          mailbox->timed_out = 1u;
          record_operation_outcome(mailbox, 1);
          if (mailbox->inflight != 0u &&
              !CancelIoEx((HANDLE)(uintptr_t)mailbox->socket,
                          &mailbox->overlapped)) {
            DWORD error_code = GetLastError();
            if (error_code != ERROR_NOT_FOUND)
              return failf("CancelIoEx at drain deadline failed (%lu)",
                           (unsigned long)error_code);
          }
        }
      }
      while (g_active_count != 0u) {
        int dispatch_result = dispatch_one(0u);
        if (dispatch_result == 0)
          return 0;
        if (dispatch_result == 1)
          break;
      }
      break;
    }
    if (!service_deadlines(now, drain_deadline))
      return 0;
    if (g_active_count != 0u && dispatch_one(1u) == 0)
      return 0;
  }
  g_measured_phase = 0u;
  return 1;
}

static int verify_mailbox_state(uint32_t mailbox_id) {
  mailbox_io *mailbox = &g_mailboxes[mailbox_id];
  al_mailbox_owning_state_view view;
  const al_owning_bank_root *root;
  const al_owning_byte_bank *bank;
  const uint8_t *bytes;
  uint32_t units;
  uint32_t expected_units;
  uint32_t index;
  uint32_t string_extent;
  uint32_t expected_extent;
  uint64_t attempted;
  uint64_t completed;
  memset(&view, 0, sizeof(view));
  view.control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  view.struct_size = (uint32_t)sizeof(view);
  if (mailbox->active != 0u ||
      al_mailbox_get_owning_state_view(g_fixture.runtime, mailbox_id, &view) !=
          AL_MAILBOX_OK ||
      view.pending != 0u || view.bank == NULL || view.bank->roots == NULL ||
      view.bank->bytes == NULL || view.bank->root_count != 1u ||
      view.state_type_id !=
          g_module->layout->types[g_roles.state_index].type_id)
    return 0;
  bank = view.bank;
  root = &bank->roots[0];
  if (root->type_id != view.state_type_id ||
      root->offset_bytes > bank->used_bytes ||
      root->extent_bytes > bank->used_bytes - root->offset_bytes ||
      root->owner_end_bytes != root->offset_bytes + root->extent_bytes ||
      root->extent_bytes < 24u)
    return 0;
  bytes = bank->bytes + root->offset_bytes;
  attempted = read_u64_le(bytes);
  completed = read_u64_le(bytes + 8u);
  units = read_u32_le(bytes + 16u);
  if (bytes[20] != 0u || bytes[21] != 0u || bytes[22] != 0u ||
      bytes[23] != 0u || attempted != mailbox->attempted ||
      completed != mailbox->completed)
    return 0;
  expected_units = mailbox->completed == 0u ? 0u : g_payload_bytes * 2u;
  if (units != expected_units)
    return 0;
  string_extent = (8u + units * 2u + 7u) & ~7u;
  expected_extent = 16u + string_extent;
  if (root->extent_bytes != expected_extent ||
      root->payload_bytes != 24u + units * 2u)
    return 0;
  for (index = 0u; index < units; ++index) {
    uint8_t expected = mailbox->completed == 0u
                           ? 0u
                           : g_payload[index % g_payload_bytes];
    uint16_t actual = (uint16_t)bytes[24u + index * 2u] |
                      ((uint16_t)bytes[25u + index * 2u] << 8u);
    if (actual != expected)
      return 0;
  }
  for (index = 24u + units * 2u; index < expected_extent; ++index) {
    if (bytes[index] != 0u)
      return 0;
  }
  return 1;
}

static int verify_final_state(void) {
  uint32_t mailbox_id;
  int verified = 1;
  uint64_t attempted = 0u;
  uint64_t completed = 0u;
  for (mailbox_id = 0u; mailbox_id < MAILBOX_COUNT; ++mailbox_id) {
    if (!verify_mailbox_state(mailbox_id))
      verified = 0;
    attempted += g_mailboxes[mailbox_id].attempted;
    completed += g_mailboxes[mailbox_id].completed;
  }
  if (attempted != g_result.admitted || completed != g_result.completed ||
      g_result.offered != g_result.admitted + g_result.rejected ||
      g_result.admitted !=
          g_result.completed + g_result.errors + g_result.timed_out ||
      g_result.pending_at_end != 0u || g_result.errors != 0u ||
      g_result.timed_out != 0u)
    verified = 0;
  g_result.verified = verified != 0 ? 1u : 0u;
  if (verified == 0)
    g_result.failed = 1u;
  return verified;
}

static int write_result(const options *parsed) {
  FILE *output = NULL;
  uint32_t mailbox_id;
  uint64_t index;
  const char *policy = parsed->policy == AL_MAILBOX_OWNING_POLICY_RETURN
                           ? "return"
                           : "keep";
  if (fopen_s(&output, parsed->output_path, "wb") != 0 || output == NULL)
    return failf("could not open result file (errno %d)", errno);
  fprintf(output,
          "{\"schemaVersion\":1,\"backend\":\"native\",\"policy\":\"%s\","
          "\"rate\":%u,\"payloadBytes\":%u,\"delayMs\":%u,"
          "\"warmupMs\":%u,\"durationMs\":%u,\"scratchSlots\":%u,"
          "\"offered\":%" PRIu64 ",\"admitted\":%" PRIu64
          ",\"rejected\":%" PRIu64 ",\"completed\":%" PRIu64
          ",\"completedWithinWindow\":%" PRIu64
          ",\"warmupCompleted\":%" PRIu64 ",\"missedArrivals\":%" PRIu64
          ",\"maxDispatchLatenessMicroseconds\":%" PRIu64
          ",\"errors\":%" PRIu64 ",\"timedOut\":%" PRIu64
          ",\"pendingAtEnd\":%" PRIu64 ",\"peakPending\":%" PRIu64
          ",\"verified\":%s,\"latencyMicroseconds\":[",
          policy, parsed->rate, parsed->payload_bytes, parsed->delay_ms,
          parsed->warmup_ms, parsed->duration_ms, parsed->scratch_slots,
          g_result.offered, g_result.admitted, g_result.rejected,
          g_result.completed, g_result.completed_within_window,
          g_result.warmup_completed, g_result.missed_arrivals,
          g_result.max_dispatch_lateness_us, g_result.errors,
          g_result.timed_out, g_result.pending_at_end, g_result.peak_pending,
          g_result.verified != 0u ? "true" : "false");
  for (index = 0u; index < g_result.latency_count; ++index) {
    if (index != 0u)
      fputc(',', output);
    fprintf(output, "%" PRIu64, g_result.latencies[index]);
  }
  fputs("],\"mailboxes\":[", output);
  for (mailbox_id = 0u; mailbox_id < MAILBOX_COUNT; ++mailbox_id) {
    mailbox_io *mailbox = &g_mailboxes[mailbox_id];
    if (mailbox_id != 0u)
      fputc(',', output);
    fprintf(output,
            "{\"id\":%u,\"attempted\":%" PRIu64
            ",\"completed\":%" PRIu64 ",\"latestVerified\":%s}",
            mailbox_id, mailbox->attempted, mailbox->completed,
            verify_mailbox_state(mailbox_id) ? "true" : "false");
  }
  fprintf(output, "],\"storageReservedBytes\":%" PRIu64 "}\n",
          g_fixture.requirements.storage_bytes);
  if (fclose(output) != 0)
    return failf("writing result file failed (errno %d)", errno);
  return 1;
}

static int setup_io(uint32_t port) {
  WSADATA data;
  if (WSAStartup(MAKEWORD(2, 2), &data) != 0)
    return failf("WSAStartup failed");
  g_winsock_started = 1u;
  if (!QueryPerformanceFrequency(&g_qpc_frequency) ||
      g_qpc_frequency.QuadPart <= 0)
    return failf("QueryPerformanceFrequency failed (%lu)",
                 (unsigned long)GetLastError());
  g_completion_port = CreateIoCompletionPort(INVALID_HANDLE_VALUE, NULL, 0u, 1u);
  if (g_completion_port == NULL)
    return failf("IOCP creation failed (%lu)", (unsigned long)GetLastError());
  return open_provider_connections(port);
}

static void cleanup_io(void) {
  uint32_t mailbox_id;
  for (mailbox_id = 0u; mailbox_id < MAILBOX_COUNT; ++mailbox_id)
    close_mailbox_socket(&g_mailboxes[mailbox_id]);
  if (g_completion_port != NULL) {
    CloseHandle(g_completion_port);
    g_completion_port = NULL;
  }
  if (g_winsock_started != 0u) {
    (void)WSACleanup();
    g_winsock_started = 0u;
  }
}

int main(int argc, char **argv) {
  HMODULE library = NULL;
  al_owning_mailbox_module_fn get_module;
  uint64_t expected;
  uint64_t warmup_completed;
  uint64_t *latencies;
  uint64_t latency_capacity;
  uint32_t index;
  int exit_code = 1;
  if (!parse_options(argc, argv, &g_options)) {
    fprintf(stderr,
            "Usage: load-host --module DLL --policy return|keep --port PORT "
            "--rate RATE --payload-bytes BYTES --delay-ms MS --warmup-ms MS "
            "--duration-ms MS [--scratch-slots 4|16] --output ABSOLUTE_JSON_PATH\n");
    return 2;
  }
  memset(&g_result, 0, sizeof(g_result));
  memset(g_mailboxes, 0, sizeof(g_mailboxes));
  for (index = 0u; index < MAILBOX_COUNT; ++index)
    g_mailboxes[index].socket = INVALID_SOCKET;
  for (index = 0u; index < g_options.payload_bytes; ++index)
    g_payload[index] = (uint8_t)('a' + (index % 26u));
  g_payload_bytes = g_options.payload_bytes;
  expected = arrivals_in_window(g_options.rate, g_options.duration_ms);
  latency_capacity = expected;
  if (latency_capacity == 0u ||
      latency_capacity > (uint64_t)(SIZE_MAX / sizeof(uint64_t))) {
    fprintf(stderr, "Invalid latency sample capacity.\n");
    return 2;
  }
  g_result.latency_capacity = latency_capacity;
  g_result.latencies =
      (uint64_t *)calloc((size_t)latency_capacity, sizeof(uint64_t));
  if (g_result.latencies == NULL) {
    fprintf(stderr, "Could not allocate latency samples.\n");
    return 1;
  }
  library = LoadLibraryA(g_options.module_path);
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
    fprintf(stderr, "Module ABI or mailbox flow signature is unsupported.\n");
    goto done;
  }
  if (!create_runtime() || !setup_io(g_options.port))
    goto fail;
  if (g_options.warmup_ms != 0u) {
    if (!run_phase(g_options.warmup_ms, 0u, g_options.delay_ms))
      goto fail;
    if (g_active_count != 0u || g_result.failed != 0u) {
      failf("warmup did not drain cleanly");
      goto fail;
    }
  }
  warmup_completed = g_result.warmup_completed;
  if (!dispose_runtime() || !create_runtime())
    goto fail;
  latencies = g_result.latencies;
  latency_capacity = g_result.latency_capacity;
  memset(&g_result, 0, sizeof(g_result));
  g_result.warmup_completed = warmup_completed;
  g_result.latencies = latencies;
  g_result.latency_capacity = latency_capacity;
  for (index = 0u; index < MAILBOX_COUNT; ++index) {
    g_mailboxes[index].attempted = 0u;
    g_mailboxes[index].completed = 0u;
    g_mailboxes[index].active = 0u;
    g_mailboxes[index].inflight = 0u;
    g_mailboxes[index].timed_out = 0u;
    g_mailboxes[index].outcome_recorded = 0u;
    g_mailboxes[index].has_token = 0u;
    g_mailboxes[index].faulted = 0u;
    memset(&g_mailboxes[index].token, 0, sizeof(g_mailboxes[index].token));
  }
  if (g_active_count != 0u) {
    failf("warmup left provider operations pending before measurement");
    goto fail;
  }
  if (!run_phase(g_options.duration_ms, 1u, g_options.delay_ms))
    goto fail;
  g_result.pending_at_end = g_active_count;
  (void)verify_final_state();
  if (!write_result(&g_options))
    goto fail;
  exit_code = g_result.failed == 0u && g_result.verified != 0u ? 0 : 1;
  goto done;

fail:
  if (g_failure[0] != '\0')
    fprintf(stderr, "%s\n", g_failure);
  else
    fprintf(stderr, "Native mailbox load host failed.\n");

done:
  if (g_active_count != 0u) {
    /* IOCP still owns request buffers; process teardown releases them safely. */
    return exit_code;
  }
  if (g_fixture.runtime != NULL)
    (void)dispose_runtime();
  if (g_fixture.storage != NULL)
    (void)VirtualFree(g_fixture.storage, 0u, MEM_RELEASE);
  cleanup_io();
  if (library != NULL)
    FreeLibrary(library);
  free(g_result.latencies);
  return exit_code;
}
