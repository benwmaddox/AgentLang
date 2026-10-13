#define _WIN32_WINNT 0x0A00
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <http.h>
#include <shellapi.h>
#include <bcrypt.h>
#include <winsock2.h>
#include <ws2tcpip.h>

#include <stdint.h>
#include <stddef.h>
#include <limits.h>

#include "http_host_pins.h"
#include "owning_stack_runtime.h"
#include "campfire_room_read_host_abi.h"
#include "room_read_adapter.h"
#include "request_encoder.h"
#include "browser_normalizer.h"
#include "campfire_html_encoder.h"
#include "campfire_http_renderer.h"
#include "cookie_response_codec.h"
#include "cookie_wire.h"
#include "cookie_unescape.h"
#include "cookie_expiry.h"

#if !defined(CAMPFIRE_HTTP_OWNING_RUNTIME_PROFILE_ID) || \
    !defined(CAMPFIRE_HTTP_OWNING_RUNTIME_PROFILE_NAME)
#error "The generated HTTP host pins must declare the owning runtime profile."
#endif
#if CAMPFIRE_HTTP_OWNING_RUNTIME_PROFILE_ID != AL_OWNING_TRUSTED_GENERATED
#error "The host and owning runtime profile pins must match."
#endif
#if !defined(CAMPFIRE_HTTP_STAGE_PROFILE)
#error "The generated HTTP host pins must declare the stage profile setting."
#endif
#if CAMPFIRE_HTTP_STAGE_PROFILE != 0 && CAMPFIRE_HTTP_STAGE_PROFILE != 1
#error "The HTTP host stage profile setting must be zero or one."
#endif

#define HOST_WORKER_MAX 8u
#define HOST_DEFAULT_WORKERS 4u
#define HOST_QUEUE_LENGTH 64u
#define HOST_REQUEST_BUFFER_BYTES 65536u
#define HOST_INPUT_BYTES 1048576u
#define HOST_STACK_BYTES (4u * 1024u * 1024u)
#define HOST_RETAINED_BYTES (4u * 1024u * 1024u)
#define HOST_BODY_INITIAL_BYTES (256u * 1024u)
#define HOST_BODY_BYTES AL_CAMPFIRE_HTTP_RENDERER_MAX_RESPONSE_BYTES
#define HOST_TEXT_ARENA_UNITS 65536u
#define HOST_SECRET_BYTES 4096u
#define HOST_COOKIE_RAW_BYTES 65536u
#define HOST_COOKIE_WIRE_BYTES 196608u
#define HOST_COOKIE_JSON_BYTES 65536u
#define HOST_RETURN_URL_BYTES 16384u
#define HOST_AUTHORITY_UNITS 64u
#define HOST_MAX_HEADER_BYTES 32768u
#define HOST_MAX_COOKIE_HEADER_BYTES 16384u

typedef uint32_t(__cdecl *pure_size_fn)(void);
typedef uint32_t(__cdecl *pure_alignment_fn)(void);
typedef int32_t(__cdecl *pure_init_fn)(void *, uint32_t, const uint8_t *, uint32_t);
typedef void(__cdecl *pure_destroy_fn)(void *);
typedef int32_t(__cdecl *context_init_fn)(al_campfire_room_read_context_v1 *, const uint8_t *, uint32_t, void *, uint32_t *);
typedef int32_t(__cdecl *provision_wal_fn)(const uint8_t *, uint32_t, uint32_t *);
typedef int32_t(__cdecl *context_destroy_fn)(al_campfire_room_read_context_v1 *);
typedef int32_t(__cdecl *execute_fn)(al_owning_stack_context *, void *, const uint8_t *, uint32_t, const uint32_t *, uint32_t, uint8_t *, uint32_t);

typedef struct host_api {
  pure_size_fn pure_size;
  pure_alignment_fn pure_alignment;
  pure_init_fn pure_init;
  pure_destroy_fn pure_destroy;
  context_init_fn context_init;
  provision_wal_fn provision_wal;
  context_destroy_fn context_destroy;
  execute_fn execute;
} host_api;

typedef struct text_span {
  const uint16_t *data;
  uint32_t units;
  uint32_t present;
} text_span;

#if CAMPFIRE_HTTP_STAGE_PROFILE
typedef enum host_stage_id {
  HOST_STAGE_RECEIVE_WAIT = 0,
  HOST_STAGE_INPUT_PREPARE = 1,
  HOST_STAGE_API_EXECUTE = 2,
  HOST_STAGE_RENDERER = 3,
  HOST_STAGE_COOKIE_FINALIZE = 4,
  HOST_STAGE_SEND_API = 5,
  HOST_STAGE_REQUEST_SCRUB = 6,
  HOST_STAGE_COUNT = 7
} host_stage_id;

typedef struct host_stage_profile {
  uint64_t calls[HOST_STAGE_COUNT];
  uint64_t ticks[HOST_STAGE_COUNT];
  uint64_t max_ticks[HOST_STAGE_COUNT];
  uint64_t cpu_start_user_100ns;
  uint64_t cpu_start_kernel_100ns;
  uint64_t cpu_user_100ns;
  uint64_t cpu_kernel_100ns;
  uint32_t cpu_valid;
} host_stage_profile;
#endif

typedef struct worker_state {
  HANDLE thread;
  struct host_config *config;
  uint32_t index;
  uint32_t initialized;
  uint32_t requests;
  int64_t request_now_unix_microseconds;
  void *pure_raw;
  void *pure_context;
  uint32_t pure_bytes;
  uint32_t pure_alignment;
  void *response_raw;
  void *response_context;
  uint32_t response_bytes;
  uint32_t response_alignment;
  al_campfire_room_read_context_v1 database_context;
  al_owning_stack_context runtime;
  uint8_t *stack;
  uint8_t *init_bitmap;
  uint8_t *poison_bitmap;
  uint8_t *request_buffer;
  uint8_t *input;
  uint8_t *retained;
  uint8_t *body;
  uint32_t body_committed_bytes;
  uint16_t *text_arena;
  uint32_t text_used;
  uint16_t *cookie_input;
  uint32_t cookie_input_units;
  uint16_t *session_token;
  uint16_t *encrypted_session;
  uint16_t *last_room;
  uint16_t browser_version[4096];
  uint16_t cookie_decoded[4][CF_COOKIE_UNESCAPE_MAX_OUTPUT_UTF16_UNITS_V1];
  uint32_t cookie_decoded_units[4];
  uint8_t *unescape_scratch;
  uint8_t *cookie_json;
  uint32_t cookie_json_bytes;
  uint8_t *cookie_json_input;
  uint32_t cookie_json_input_bytes;
  uint8_t *cookie_wire[3];
  uint8_t *cookie_raw[3];
  uint32_t cookie_raw_bytes[3];
  uint32_t cookie_wire_bytes[3];
  uint32_t response_cookie_indices[3];
  text_span request_encrypted_cookie;
  uint8_t *return_url;
  uint32_t return_url_bytes;
  uint32_t active_input_bytes;
  uint32_t active_retained_bytes;
  uint32_t active_body_bytes;
  void *browser_scratch_raw;
  void *browser_scratch;
  size_t browser_scratch_bytes;
#if CAMPFIRE_HTTP_STAGE_PROFILE
  host_stage_profile stage_profile;
#endif
} worker_state;

typedef struct host_config {
  HANDLE request_queue;
  HANDLE stop_event;
  WCHAR stop_event_name[256];
  HMODULE module;
  HMODULE sqlite_module;
  host_api api;
  uint32_t worker_count;
  uint32_t owning_runtime_profile;
  uint32_t port;
  uint32_t ready_workers;
  uint32_t cleaned_workers;
  uint32_t fatal_status;
#if CAMPFIRE_HTTP_STAGE_PROFILE
  uint64_t stage_qpc_frequency;
#endif
  HANDLE workers_ready_event;
  uint8_t secret[HOST_SECRET_BYTES];
  uint32_t secret_bytes;
  uint8_t database_path[AL_CAMPFIRE_ROOM_READ_PATH_LIMIT_BYTES + 1u];
  uint32_t database_path_bytes;
  WCHAR module_path[32768];
  WCHAR manifest_path[32768];
  WCHAR sqlite_path[32768];
  WCHAR host_header[HOST_AUTHORITY_UNITS];
  WCHAR authority[HOST_AUTHORITY_UNITS];
  WCHAR prefix[HOST_AUTHORITY_UNITS + 2u];
  worker_state workers[HOST_WORKER_MAX];
} host_config;

static host_config g_host;

static void bytes_zero(void *target, size_t count) {
  if (target != NULL && count != 0u) SecureZeroMemory(target, count);
}

static void clear_request_sensitive(worker_state *worker) {
  if (worker->input != NULL && worker->active_input_bytes <= HOST_INPUT_BYTES)
    bytes_zero(worker->input, worker->active_input_bytes);
  if (worker->retained != NULL) {
    uint32_t bytes = worker->active_retained_bytes <= HOST_RETAINED_BYTES
        ? worker->active_retained_bytes : HOST_RETAINED_BYTES;
    bytes_zero(worker->retained, bytes);
  }
  if (worker->body != NULL) {
    uint32_t bytes = worker->active_body_bytes <= worker->body_committed_bytes
        ? worker->active_body_bytes : worker->body_committed_bytes;
    bytes_zero(worker->body, bytes);
  }
  if (worker->text_arena != NULL && worker->text_used <= HOST_TEXT_ARENA_UNITS)
    bytes_zero(worker->text_arena, (size_t)worker->text_used * sizeof(uint16_t));
  if (worker->cookie_input != NULL && worker->cookie_input_units <= CF_COOKIE_UNESCAPE_MAX_WIRE_UTF16_UNITS_V1)
    bytes_zero(worker->cookie_input, (size_t)worker->cookie_input_units * sizeof(uint16_t));
  for (uint32_t i = 0u; i < 4u; ++i) {
    uint32_t units = worker->cookie_decoded_units[i];
    if (units <= CF_COOKIE_UNESCAPE_MAX_OUTPUT_UTF16_UNITS_V1)
      bytes_zero(worker->cookie_decoded[i], (size_t)units * sizeof(uint16_t));
  }
  if (worker->unescape_scratch != NULL)
    bytes_zero(worker->unescape_scratch, cf_cookie_unescape_scratch_size_v1());
  if (worker->cookie_json != NULL && worker->cookie_json_bytes <= HOST_COOKIE_JSON_BYTES)
    bytes_zero(worker->cookie_json, worker->cookie_json_bytes);
  if (worker->cookie_json_input != NULL && worker->cookie_json_input_bytes <= HOST_COOKIE_RAW_BYTES)
    bytes_zero(worker->cookie_json_input, worker->cookie_json_input_bytes);
  if (worker->return_url != NULL && worker->return_url_bytes <= HOST_RETURN_URL_BYTES)
    bytes_zero(worker->return_url, worker->return_url_bytes);
  if (worker->browser_scratch != NULL && worker->browser_scratch_bytes != 0u)
    bytes_zero(worker->browser_scratch, worker->browser_scratch_bytes);
  bytes_zero(worker->browser_version, sizeof(worker->browser_version));
  for (uint32_t i = 0u; i < 3u; ++i) {
    if (worker->cookie_raw[i] != NULL && worker->cookie_raw_bytes[i] <= HOST_COOKIE_RAW_BYTES)
      bytes_zero(worker->cookie_raw[i], worker->cookie_raw_bytes[i]);
    if (worker->cookie_wire[i] != NULL && worker->cookie_wire_bytes[i] <= HOST_COOKIE_WIRE_BYTES)
      bytes_zero(worker->cookie_wire[i], worker->cookie_wire_bytes[i]);
    worker->cookie_raw_bytes[i] = 0u;
    worker->cookie_wire_bytes[i] = 0u;
    worker->response_cookie_indices[i] = 0u;
  }
  for (uint32_t i = 0u; i < 4u; ++i) worker->cookie_decoded_units[i] = 0u;
  worker->cookie_input_units = 0u;
  worker->cookie_json_bytes = 0u;
  worker->cookie_json_input_bytes = 0u;
  worker->text_used = 0u;
  worker->return_url_bytes = 0u;
  worker->active_input_bytes = 0u;
  worker->active_retained_bytes = 0u;
  worker->active_body_bytes = 0u;
  worker->request_now_unix_microseconds = 0;
  worker->request_encrypted_cookie.data = NULL;
  worker->request_encrypted_cookie.units = 0u;
  worker->request_encrypted_cookie.present = 0u;
}

static void bytes_copy(void *target, const void *source, size_t count) {
  uint8_t *d = (uint8_t *)target;
  const uint8_t *s = (const uint8_t *)source;
  size_t i;
  for (i = 0; i < count; ++i) d[i] = s[i];
}

static uint32_t wlen(const WCHAR *s) {
  uint32_t n = 0u;
  if (s == NULL) return 0u;
  while (s[n] != 0) ++n;
  return n;
}

static uint32_t alen(const char *s) {
  uint32_t n = 0u;
  if (s == NULL) return 0u;
  while (s[n] != 0) ++n;
  return n;
}

static int ascii_equal_ci(const char *left, const char *right, uint32_t length) {
  uint32_t i;
  for (i = 0u; i < length; ++i) {
    char a = left[i], b = right[i];
    if (a >= 'A' && a <= 'Z') a = (char)(a + ('a' - 'A'));
    if (b >= 'A' && b <= 'Z') b = (char)(b + ('a' - 'A'));
    if (a != b) return 0;
  }
  return 1;
}

static int bytes_equal(const char *left, const char *right, uint32_t length) {
  uint32_t i;
  for (i = 0u; i < length; ++i) if (left[i] != right[i]) return 0;
  return 1;
}

static void write_ascii(const char *text) {
  DWORD written = 0u;
  HANDLE output = GetStdHandle(STD_OUTPUT_HANDLE);
  uint32_t length = alen(text);
  if (output != NULL && output != INVALID_HANDLE_VALUE && length != 0u)
    WriteFile(output, text, length, &written, NULL);
}

static void write_u32(uint32_t value) {
  char digits[11];
  uint32_t count = 0u, i;
  do { digits[count++] = (char)('0' + (value % 10u)); value /= 10u; } while (value != 0u && count < 10u);
  for (i = count; i > 0u; --i) {
    char ch = digits[i - 1u];
    DWORD written = 0u;
    WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), &ch, 1u, &written, NULL);
  }
}

#if CAMPFIRE_HTTP_STAGE_PROFILE
static void write_u64(uint64_t value) {
  char digits[20];
  uint32_t count = 0u, i;
  do { digits[count++] = (char)('0' + (value % 10u)); value /= 10u; }
  while (value != 0u && count < (uint32_t)sizeof(digits));
  for (i = count; i > 0u; --i) {
    char ch = digits[i - 1u];
    DWORD written = 0u;
    WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), &ch, 1u, &written, NULL);
  }
}

static uint64_t host_stage_qpc_now(void) {
  LARGE_INTEGER value;
  if (!QueryPerformanceCounter(&value) || value.QuadPart <= 0) return 0u;
  return (uint64_t)value.QuadPart;
}

static void host_stage_record(worker_state *worker, host_stage_id stage,
                              uint64_t started) {
  uint64_t ended = host_stage_qpc_now();
  uint64_t elapsed;
  if (stage >= HOST_STAGE_COUNT || started == 0u || ended < started) return;
  elapsed = ended - started;
  ++worker->stage_profile.calls[stage];
  worker->stage_profile.ticks[stage] += elapsed;
  if (elapsed > worker->stage_profile.max_ticks[stage])
    worker->stage_profile.max_ticks[stage] = elapsed;
}

static uint64_t host_stage_filetime_value(FILETIME value) {
  ULARGE_INTEGER combined;
  combined.LowPart = value.dwLowDateTime;
  combined.HighPart = value.dwHighDateTime;
  return combined.QuadPart;
}

static void host_stage_capture_cpu_start(worker_state *worker) {
  FILETIME created, exited, kernel, user;
  if (GetThreadTimes(GetCurrentThread(), &created, &exited, &kernel, &user)) {
    worker->stage_profile.cpu_start_user_100ns = host_stage_filetime_value(user);
    worker->stage_profile.cpu_start_kernel_100ns = host_stage_filetime_value(kernel);
    worker->stage_profile.cpu_valid = 1u;
  }
}

static void host_stage_capture_cpu_end(worker_state *worker) {
  FILETIME created, exited, kernel, user;
  uint64_t user_end, kernel_end;
  if (worker->stage_profile.cpu_valid == 0u ||
      !GetThreadTimes(GetCurrentThread(), &created, &exited, &kernel, &user)) {
    worker->stage_profile.cpu_valid = 0u;
    return;
  }
  user_end = host_stage_filetime_value(user);
  kernel_end = host_stage_filetime_value(kernel);
  if (user_end < worker->stage_profile.cpu_start_user_100ns ||
      kernel_end < worker->stage_profile.cpu_start_kernel_100ns) {
    worker->stage_profile.cpu_valid = 0u;
    return;
  }
  worker->stage_profile.cpu_user_100ns =
      user_end - worker->stage_profile.cpu_start_user_100ns;
  worker->stage_profile.cpu_kernel_100ns =
      kernel_end - worker->stage_profile.cpu_start_kernel_100ns;
}

static void write_stage_profile(const host_config *config) {
  static const char *const names[HOST_STAGE_COUNT] = {
    "receive_wait", "input_prepare", "api_execute", "renderer",
    "cookie_finalize", "send_api", "request_scrub"
  };
  write_ascii("stage_profile enabled=1 workers=");
  write_u32(config->worker_count);
  write_ascii(" qpc_frequency=");
  write_u64(config->stage_qpc_frequency);
  write_ascii(" units=ticks cpu_units=100ns\r\n");
  for (uint32_t worker_index = 0u; worker_index < config->worker_count;
       ++worker_index) {
    const worker_state *worker = &config->workers[worker_index];
    write_ascii("stage_worker index=");
    write_u32(worker->index);
    write_ascii(" received_requests=");
    write_u32(worker->requests);
    write_ascii(" cpu_valid=");
    write_u32(worker->stage_profile.cpu_valid);
    write_ascii(" worker_loop_user_100ns=");
    write_u64(worker->stage_profile.cpu_user_100ns);
    write_ascii(" worker_loop_kernel_100ns=");
    write_u64(worker->stage_profile.cpu_kernel_100ns);
    for (uint32_t stage = 0u; stage < HOST_STAGE_COUNT; ++stage) {
      write_ascii(" ");
      write_ascii(names[stage]);
      write_ascii("_calls=");
      write_u64(worker->stage_profile.calls[stage]);
      write_ascii(" ");
      write_ascii(names[stage]);
      write_ascii("_ticks=");
      write_u64(worker->stage_profile.ticks[stage]);
      write_ascii(" ");
      write_ascii(names[stage]);
      write_ascii("_max_ticks=");
      write_u64(worker->stage_profile.max_ticks[stage]);
    }
    write_ascii("\r\n");
  }
}
#endif

static int parse_u32(const WCHAR *s, uint32_t *out) {
  uint32_t value = 0u, i = 0u;
  if (s == NULL || s[0] == 0) return 0;
  while (s[i] != 0) {
    uint32_t digit;
    if (s[i] < L'0' || s[i] > L'9') return 0;
    digit = (uint32_t)(s[i] - L'0');
    if (value > (UINT32_MAX - digit) / 10u) return 0;
    value = value * 10u + digit;
    ++i;
  }
  *out = value;
  return 1;
}

static int parse_i64(const WCHAR *s, uint32_t length, int64_t *out) {
  uint32_t i = 0u;
  int negative = 0;
  uint64_t magnitude = 0u, limit;
  if (s == NULL || length == 0u) return 0;
  if (s[i] == L'+' || s[i] == L'-') { negative = s[i] == L'-'; ++i; }
  if (i == length) return 0;
  limit = negative ? (uint64_t)INT64_MAX + 1u : (uint64_t)INT64_MAX;
  for (; i < length; ++i) {
    uint32_t digit;
    if (s[i] < L'0' || s[i] > L'9') return 0;
    digit = (uint32_t)(s[i] - L'0');
    if (magnitude > (limit - digit) / 10u) return 0;
    magnitude = magnitude * 10u + digit;
  }
  if (negative) *out = magnitude == (uint64_t)INT64_MAX + 1u ? INT64_MIN : -(int64_t)magnitude;
  else *out = (int64_t)magnitude;
  return 1;
}

static uint32_t utf8_to_u16(const uint8_t *source, uint32_t source_bytes,
                            uint16_t *target, uint32_t capacity_units) {
  int needed;
  if (source_bytes == 0u) return 0u;
  if (source_bytes > INT_MAX || capacity_units > INT_MAX) return UINT32_MAX;
  needed = MultiByteToWideChar(CP_UTF8, 0, (LPCCH)source, (int)source_bytes,
                               (LPWSTR)target, (int)capacity_units);
  if (needed <= 0) return UINT32_MAX;
  return (uint32_t)needed;
}

static uint32_t u16_to_utf8(const uint16_t *source, uint32_t source_units,
                            uint8_t *target, uint32_t capacity_bytes) {
  int needed;
  if (source_units == 0u) return 0u;
  if (source_units > INT_MAX || capacity_bytes > INT_MAX) return UINT32_MAX;
  needed = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS,
                               (LPCWCH)source, (int)source_units,
                               (LPSTR)target, (int)capacity_bytes, NULL, NULL);
  if (needed <= 0) return UINT32_MAX;
  return (uint32_t)needed;
}

static uint16_t *arena_text(worker_state *worker, const uint8_t *source,
                            uint32_t source_bytes, uint32_t *out_units) {
  uint16_t *at;
  uint32_t available, units;
  if (worker->text_used >= HOST_TEXT_ARENA_UNITS) return NULL;
  available = HOST_TEXT_ARENA_UNITS - worker->text_used;
  at = worker->text_arena + worker->text_used;
  if (source_bytes == 0u) {
    at[0] = 0u;
    worker->text_used += 1u;
    *out_units = 0u;
    return at;
  }
  units = utf8_to_u16(source, source_bytes, at, available);
  if (units == UINT32_MAX || units > available) return NULL;
  worker->text_used += units + 1u;
  at[units] = 0u;
  *out_units = units;
  return at;
}

static int fixed_ascii_path(const WCHAR *path, uint32_t path_units,
                            uint32_t *id_start, uint32_t *id_units) {
  static const WCHAR prefix[] = L"/rooms/";
  uint32_t i;
  if (path == NULL || path_units < 8u) return 0;
  for (i = 0u; i < 7u; ++i) {
    WCHAR a = path[i], b = prefix[i];
    if (a >= L'A' && a <= L'Z') a = (WCHAR)(a + (L'a' - L'A'));
    if (a != b) return 0;
  }
  for (i = 7u; i < path_units; ++i) if (path[i] == L'/') return 0;
  *id_start = 7u;
  *id_units = path_units - 7u;
  return 1;
}

static int64_t current_unix_microseconds_i64(void) {
  FILETIME ft;
  ULARGE_INTEGER value;
  GetSystemTimePreciseAsFileTime(&ft);
  value.LowPart = ft.dwLowDateTime;
  value.HighPart = ft.dwHighDateTime;
  return (int64_t)((value.QuadPart - 116444736000000000ULL) / 10ULL);
}

static uint32_t hash_file_sha256(const WCHAR *path, uint8_t digest[32]) {
  BCRYPT_ALG_HANDLE algorithm = NULL;
  BCRYPT_HASH_HANDLE hash = NULL;
  HANDLE file = INVALID_HANDLE_VALUE;
  UCHAR *object = NULL;
  ULONG object_bytes = 0u, result_bytes = 0u, hash_bytes = 0u;
  uint8_t buffer[16384];
  DWORD read_bytes = 0u;
  NTSTATUS status;
  uint32_t result = 0u;
  status = BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, NULL, 0u);
  if (status < 0) goto done;
  status = BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH, (PUCHAR)&object_bytes,
                             sizeof(object_bytes), &result_bytes, 0u);
  if (status < 0 || object_bytes == 0u || object_bytes > 65536u) goto done;
  status = BCryptGetProperty(algorithm, BCRYPT_HASH_LENGTH, (PUCHAR)&hash_bytes,
                             sizeof(hash_bytes), &result_bytes, 0u);
  if (status < 0 || hash_bytes != 32u) goto done;
  object = (UCHAR *)HeapAlloc(GetProcessHeap(), 0u, object_bytes);
  if (object == NULL) goto done;
  status = BCryptCreateHash(algorithm, &hash, object, object_bytes, NULL, 0u, 0u);
  if (status < 0) goto done;
  file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING,
                     FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, NULL);
  if (file == INVALID_HANDLE_VALUE) goto done;
  for (;;) {
    if (!ReadFile(file, buffer, sizeof(buffer), &read_bytes, NULL)) goto done;
    if (read_bytes == 0u) break;
    status = BCryptHashData(hash, buffer, read_bytes, 0u);
    if (status < 0) goto done;
  }
  status = BCryptFinishHash(hash, digest, 32u, 0u);
  if (status < 0) goto done;
  result = 1u;
done:
  if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
  if (hash != NULL) BCryptDestroyHash(hash);
  if (object != NULL) { bytes_zero(object, object_bytes); HeapFree(GetProcessHeap(), 0u, object); }
  if (algorithm != NULL) BCryptCloseAlgorithmProvider(algorithm, 0u);
  bytes_zero(buffer, sizeof(buffer));
  return result;
}

static int hash_equal(const uint8_t actual[32], const uint8_t expected[32]) {
  uint32_t i, different = 0u;
  for (i = 0u; i < 32u; ++i) different |= (uint32_t)(actual[i] ^ expected[i]);
  return different == 0u;
}

static uint32_t load_export(HMODULE module, const char *name, FARPROC *out) {
  FARPROC value = GetProcAddress(module, name);
  if (value == NULL) return 0u;
  *out = value;
  return 1u;
}

static uint32_t sqlite_import_is_pinned(HMODULE entry_module,
                                        HMODULE sqlite_module) {
  const uint8_t *base = (const uint8_t *)entry_module;
  const IMAGE_DOS_HEADER *dos = (const IMAGE_DOS_HEADER *)base;
  const IMAGE_NT_HEADERS64 *nt;
  const IMAGE_DATA_DIRECTORY *directory;
  const IMAGE_IMPORT_DESCRIPTOR *imports;
  FARPROC expected = GetProcAddress(sqlite_module, "sqlite3_open_v2");
  uint32_t i;
  if (expected == NULL || dos->e_magic != IMAGE_DOS_SIGNATURE ||
      dos->e_lfanew <= 0) return 0u;
  nt = (const IMAGE_NT_HEADERS64 *)(base + (uint32_t)dos->e_lfanew);
  if (nt->Signature != IMAGE_NT_SIGNATURE ||
      nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC) return 0u;
  directory = &nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
  if (directory->VirtualAddress == 0u || directory->Size < sizeof(IMAGE_IMPORT_DESCRIPTOR)) return 0u;
  imports = (const IMAGE_IMPORT_DESCRIPTOR *)(base + directory->VirtualAddress);
  for (i = 0u; i < directory->Size / sizeof(IMAGE_IMPORT_DESCRIPTOR); ++i) {
    const IMAGE_IMPORT_DESCRIPTOR *item = &imports[i];
    const char *library;
    const IMAGE_THUNK_DATA64 *names, *addresses;
    uint32_t thunk_index;
    if (item->Name == 0u) break;
    library = (const char *)(base + item->Name);
    if (alen(library) != alen("e_sqlite3.dll") ||
        !ascii_equal_ci(library, "e_sqlite3.dll", alen(library))) continue;
    names = (const IMAGE_THUNK_DATA64 *)(base +
        (item->OriginalFirstThunk != 0u ? item->OriginalFirstThunk : item->FirstThunk));
    addresses = (const IMAGE_THUNK_DATA64 *)(base + item->FirstThunk);
    for (thunk_index = 0u; names[thunk_index].u1.AddressOfData != 0u; ++thunk_index) {
      const IMAGE_IMPORT_BY_NAME *name;
      if (IMAGE_SNAP_BY_ORDINAL64(names[thunk_index].u1.Ordinal)) continue;
      name = (const IMAGE_IMPORT_BY_NAME *)(base + names[thunk_index].u1.AddressOfData);
      if (bytes_equal((const char *)name->Name, "sqlite3_open_v2",
                      alen("sqlite3_open_v2")))
        return (FARPROC)(uintptr_t)addresses[thunk_index].u1.Function == expected;
    }
    return 0u;
  }
  return 0u;
}

#define LOAD_FUNCTION(field, type, symbol) \
  do { FARPROC p_ = NULL; if (!load_export(config->module, symbol, &p_)) { *failure_stage = 9u; return 0u; } \
       { union { FARPROC raw; type typed; } u_; u_.raw = p_; config->api.field = u_.typed; } } while (0)

static uint32_t load_entry_module(host_config *config,
                                 uint32_t *failure_stage) {
  uint8_t digest[32];
  WCHAR *slash;
  typedef const char *(__cdecl *sqlite_version_fn)(void);
  sqlite_version_fn sqlite_version;
  const char *version_text;
  *failure_stage = 1u;
  if (!hash_file_sha256(config->manifest_path, digest) || !hash_equal(digest, g_expected_manifest_sha256)) return 0u;
  *failure_stage = 2u;
  if (!hash_file_sha256(config->module_path, digest) || !hash_equal(digest, g_expected_module_sha256)) return 0u;
  config->owning_runtime_profile = CAMPFIRE_HTTP_OWNING_RUNTIME_PROFILE_ID;
  *failure_stage = 3u;
  slash = config->sqlite_path;
  bytes_copy(slash, config->module_path, (wlen(config->module_path) + 1u) * sizeof(WCHAR));
  while (*slash != 0) ++slash;
  while (slash > config->sqlite_path && slash[-1] != L'\\' && slash[-1] != L'/') --slash;
  if (slash == config->sqlite_path) return 0u;
  { static const WCHAR name[] = L"e_sqlite3.dll"; uint32_t i; for (i = 0u; i < sizeof(name)/sizeof(name[0]); ++i) slash[i] = name[i]; }
  if (!hash_file_sha256(config->sqlite_path, digest) || !hash_equal(digest, g_expected_sqlite_sha256)) return 0u;
  *failure_stage = 4u;
  config->sqlite_module = LoadLibraryExW(config->sqlite_path, NULL,
      LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
  if (config->sqlite_module == NULL) return 0u;
  *failure_stage = 5u;
  config->module = LoadLibraryExW(config->module_path, NULL,
      LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
  if (config->module == NULL) return 0u;
  *failure_stage = 6u;
  if (!sqlite_import_is_pinned(config->module, config->sqlite_module)) return 0u;
  *failure_stage = 7u;
  {
    FARPROC raw = GetProcAddress(config->sqlite_module, "sqlite3_libversion");
    union { FARPROC raw; sqlite_version_fn typed; } version_export;
    if (raw == NULL) return 0u;
    version_export.raw = raw;
    sqlite_version = version_export.typed;
  }
  version_text = sqlite_version();
  if (version_text == NULL || !bytes_equal(version_text, "3.53.3", 6u) ||
      version_text[6] != 0) return 0u;
  write_ascii("sqlite.version="); write_ascii(version_text); write_ascii(" module_iat=pinned\r\n");
  LOAD_FUNCTION(pure_size, pure_size_fn, "al_campfire_pure_context_size_v1");
  LOAD_FUNCTION(pure_alignment, pure_alignment_fn, "al_campfire_pure_context_alignment_v1");
  LOAD_FUNCTION(pure_init, pure_init_fn, "al_campfire_pure_context_init_v1");
  LOAD_FUNCTION(pure_destroy, pure_destroy_fn, "al_campfire_pure_context_destroy_v1");
  LOAD_FUNCTION(context_init, context_init_fn, "al_campfire_room_read_context_init_v1");
  LOAD_FUNCTION(provision_wal, provision_wal_fn, "al_campfire_room_read_provision_wal_v1");
  LOAD_FUNCTION(context_destroy, context_destroy_fn, "al_campfire_room_read_context_destroy_v1");
  LOAD_FUNCTION(execute, execute_fn, "agentlang_owning_execute_with_host_context");
  *failure_stage = 0u;
  return 1u;
}

#undef LOAD_FUNCTION

static const char *load_failure_stage_name(uint32_t stage) {
  switch (stage) {
    case 1u: return "manifest-hash";
    case 2u: return "entry-module-hash";
    case 3u: return "sqlite-path-or-hash";
    case 4u: return "sqlite-library-load";
    case 5u: return "entry-module-load";
    case 6u: return "sqlite-import-iat";
    case 7u: return "sqlite-version-or-export";
    case 9u: return "entry-export";
    default: return "unknown";
  }
}

static uint32_t align_pointer(void **raw, uint32_t bytes, uint32_t alignment,
                              void **aligned) {
  uintptr_t base, value;
  if (alignment == 0u || (alignment & (alignment - 1u)) != 0u) return 0u;
  *raw = VirtualAlloc(NULL, (SIZE_T)bytes + alignment - 1u,
                      MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
  if (*raw == NULL) return 0u;
  base = (uintptr_t)*raw;
  value = (base + alignment - 1u) & ~(uintptr_t)(alignment - 1u);
  *aligned = (void *)value;
  return 1u;
}

static void worker_release(worker_state *worker) {
  host_config *config = worker->config;
  clear_request_sensitive(worker);
  if (worker->request_buffer != NULL)
    bytes_zero(worker->request_buffer, HOST_REQUEST_BUFFER_BYTES);
  if (worker->initialized) {
    if (config->api.context_destroy(&worker->database_context) != 0)
      InterlockedExchange((volatile LONG *)&config->fatal_status, 50L);
    config->api.pure_destroy(worker->pure_context);
    al_campfire_cookie_response_context_destroy_v1(worker->response_context);
    worker->initialized = 0u;
  }
  if (worker->pure_raw != NULL) VirtualFree(worker->pure_raw, 0u, MEM_RELEASE);
  if (worker->response_raw != NULL) VirtualFree(worker->response_raw, 0u, MEM_RELEASE);
  if (worker->stack != NULL) VirtualFree(worker->stack, 0u, MEM_RELEASE);
  if (worker->init_bitmap != NULL) VirtualFree(worker->init_bitmap, 0u, MEM_RELEASE);
  if (worker->poison_bitmap != NULL) VirtualFree(worker->poison_bitmap, 0u, MEM_RELEASE);
  if (worker->request_buffer != NULL) VirtualFree(worker->request_buffer, 0u, MEM_RELEASE);
  if (worker->input != NULL) VirtualFree(worker->input, 0u, MEM_RELEASE);
  if (worker->retained != NULL) VirtualFree(worker->retained, 0u, MEM_RELEASE);
  if (worker->body != NULL) VirtualFree(worker->body, 0u, MEM_RELEASE);
  if (worker->text_arena != NULL) VirtualFree(worker->text_arena, 0u, MEM_RELEASE);
  if (worker->cookie_input != NULL) VirtualFree(worker->cookie_input, 0u, MEM_RELEASE);
  if (worker->unescape_scratch != NULL) VirtualFree(worker->unescape_scratch, 0u, MEM_RELEASE);
  if (worker->cookie_json != NULL) VirtualFree(worker->cookie_json, 0u, MEM_RELEASE);
  if (worker->cookie_json_input != NULL) VirtualFree(worker->cookie_json_input, 0u, MEM_RELEASE);
  if (worker->return_url != NULL) VirtualFree(worker->return_url, 0u, MEM_RELEASE);
  if (worker->browser_scratch_raw != NULL) VirtualFree(worker->browser_scratch_raw, 0u, MEM_RELEASE);
  { uint32_t i; for (i = 0u; i < 3u; ++i) { if (worker->cookie_wire[i] != NULL) VirtualFree(worker->cookie_wire[i], 0u, MEM_RELEASE); if (worker->cookie_raw[i] != NULL) VirtualFree(worker->cookie_raw[i], 0u, MEM_RELEASE); } }
  if (worker->index < config->worker_count) InterlockedIncrement((volatile LONG *)&config->cleaned_workers);
}

static uint32_t worker_allocate(worker_state *worker) {
  uint32_t error_tag = UINT32_MAX;
  host_config *config = worker->config;
#if CAMPFIRE_HTTP_STAGE_PROFILE
  bytes_zero(&worker->stage_profile, sizeof(worker->stage_profile));
#endif
  if (config->owning_runtime_profile > 1u) return 0u;
  worker->pure_bytes = config->api.pure_size();
  worker->pure_alignment = config->api.pure_alignment();
  worker->response_bytes = al_campfire_cookie_response_context_size_v1();
  worker->response_alignment = al_campfire_cookie_response_context_alignment_v1();
  if (worker->pure_bytes == 0u || worker->pure_bytes > 1048576u || worker->pure_alignment > 4096u ||
      worker->response_bytes == 0u || worker->response_bytes > 1048576u || worker->response_alignment > 4096u) return 0u;
  if (!align_pointer(&worker->pure_raw, worker->pure_bytes, worker->pure_alignment, &worker->pure_context) ||
      !align_pointer(&worker->response_raw, worker->response_bytes, worker->response_alignment, &worker->response_context)) return 0u;
  worker->init_bitmap = NULL;
  worker->poison_bitmap = NULL;
  worker->stack = (uint8_t *)VirtualAlloc(NULL, HOST_STACK_BYTES, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
  if (config->owning_runtime_profile == 0u) {
    worker->init_bitmap = (uint8_t *)VirtualAlloc(NULL, HOST_STACK_BYTES / 8u, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    worker->poison_bitmap = (uint8_t *)VirtualAlloc(NULL, HOST_STACK_BYTES / 8u, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
  }
  worker->request_buffer = (uint8_t *)VirtualAlloc(NULL, HOST_REQUEST_BUFFER_BYTES, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
  worker->input = (uint8_t *)VirtualAlloc(NULL, HOST_INPUT_BYTES, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
  worker->retained = (uint8_t *)VirtualAlloc(NULL, HOST_RETAINED_BYTES, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
  worker->body = (uint8_t *)VirtualAlloc(NULL, HOST_BODY_BYTES, MEM_RESERVE, PAGE_READWRITE);
  if (worker->body != NULL) {
    if (VirtualAlloc(worker->body, HOST_BODY_INITIAL_BYTES, MEM_COMMIT, PAGE_READWRITE) == NULL) {
      VirtualFree(worker->body, 0u, MEM_RELEASE);
      worker->body = NULL;
    } else worker->body_committed_bytes = HOST_BODY_INITIAL_BYTES;
  }
  worker->text_arena = (uint16_t *)VirtualAlloc(NULL, HOST_TEXT_ARENA_UNITS * sizeof(uint16_t), MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
  worker->cookie_input = (uint16_t *)VirtualAlloc(NULL, CF_COOKIE_UNESCAPE_MAX_WIRE_UTF16_UNITS_V1 * sizeof(uint16_t), MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
  worker->unescape_scratch = (uint8_t *)VirtualAlloc(NULL, cf_cookie_unescape_scratch_size_v1(), MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
  worker->cookie_json = (uint8_t *)VirtualAlloc(NULL, HOST_COOKIE_JSON_BYTES, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
  worker->cookie_json_input = (uint8_t *)VirtualAlloc(NULL, HOST_COOKIE_RAW_BYTES, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
  worker->return_url = (uint8_t *)VirtualAlloc(NULL, HOST_RETURN_URL_BYTES, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
  worker->browser_scratch_bytes = al_campfire_browser_scratch_size_v1();
  if (!align_pointer(&worker->browser_scratch_raw, (uint32_t)worker->browser_scratch_bytes,
                     (uint32_t)al_campfire_browser_scratch_alignment_v1(), &worker->browser_scratch)) return 0u;
  { uint32_t i; for (i = 0u; i < 3u; ++i) { worker->cookie_wire[i] = (uint8_t *)VirtualAlloc(NULL, HOST_COOKIE_WIRE_BYTES, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE); worker->cookie_raw[i] = (uint8_t *)VirtualAlloc(NULL, HOST_COOKIE_RAW_BYTES, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE); } }
  if (worker->stack == NULL ||
      (config->owning_runtime_profile == 0u && (worker->init_bitmap == NULL || worker->poison_bitmap == NULL)) ||
      worker->request_buffer == NULL ||
      worker->input == NULL || worker->retained == NULL || worker->body == NULL || worker->text_arena == NULL || worker->cookie_input == NULL ||
      worker->unescape_scratch == NULL || worker->cookie_json == NULL || worker->cookie_json_input == NULL || worker->return_url == NULL || worker->browser_scratch == NULL) return 0u;
  for (uint32_t i = 0u; i < 3u; ++i) if (worker->cookie_wire[i] == NULL || worker->cookie_raw[i] == NULL) return 0u;
  if (config->api.pure_init(worker->pure_context, worker->pure_bytes, config->secret, config->secret_bytes) != 0) return 0u;
  if (al_campfire_cookie_response_context_init_v1(worker->response_context, worker->response_bytes, config->secret, config->secret_bytes) != 0) {
    config->api.pure_destroy(worker->pure_context);
    return 0u;
  }
  bytes_zero(&worker->database_context, sizeof(worker->database_context));
  if (config->api.context_init(&worker->database_context, config->database_path, config->database_path_bytes,
                               worker->pure_context, &error_tag) != 0 || error_tag != 0u) {
    config->api.pure_destroy(worker->pure_context);
    al_campfire_cookie_response_context_destroy_v1(worker->response_context);
    return 0u;
  }
  bytes_zero(&worker->runtime, sizeof(worker->runtime));
  worker->runtime.abi_version = AL_OWNING_STACK_ABI_VERSION;
  worker->runtime.stack_capacity_bytes = HOST_STACK_BYTES;
  worker->runtime.stack_data = worker->stack;
  if (config->owning_runtime_profile == 0u) {
    worker->runtime.init_bitmap_bytes = HOST_STACK_BYTES / 8u;
    worker->runtime.init_bitmap = worker->init_bitmap;
    worker->runtime.poison_bitmap = worker->poison_bitmap;
  } else {
    worker->runtime.init_bitmap_bytes = 0u;
    worker->runtime.init_bitmap = NULL;
    worker->runtime.poison_bitmap = NULL;
  }
  worker->initialized = 1u;
  return 1u;
}

static uint32_t header_value(const HTTP_REQUEST *request, int known_id,
                             const char *name, const char **out_value,
                             uint32_t *out_length, uint32_t *out_count) {
  uint32_t count = 0u, i;
  const char *value = NULL;
  uint32_t length = 0u;
  if (known_id >= 0 && known_id < HttpHeaderRequestMaximum) {
    const HTTP_KNOWN_HEADER *known = &request->Headers.KnownHeaders[known_id];
    if (known->pRawValue != NULL || known->RawValueLength != 0u) {
      ++count; value = known->pRawValue; length = known->RawValueLength;
    }
  }
  for (i = 0u; i < request->Headers.UnknownHeaderCount; ++i) {
    const HTTP_UNKNOWN_HEADER *unknown = &request->Headers.pUnknownHeaders[i];
    if (unknown->pName != NULL && unknown->NameLength == alen(name) &&
        ascii_equal_ci(unknown->pName, name, unknown->NameLength)) {
      ++count; value = unknown->pRawValue; length = unknown->RawValueLength;
    }
  }
  *out_count = count;
  *out_value = value;
  *out_length = length;
  return count > 1u ? 2u : (count == 1u ? 1u : 0u);
}

static uint32_t request_header_bytes(const HTTP_REQUEST *request, uint32_t *out) {
  static const char *known_names[HttpHeaderRequestMaximum] = {
    "Cache-Control", "Connection", "Date", "Keep-Alive", "Pragma", "Trailer", "Transfer-Encoding", "Upgrade", "Via", "Warning",
    "Allow", "Content-Length", "Content-Type", "Content-Encoding", "Content-Language", "Content-Location", "Content-MD5", "Content-Range", "Expires", "Last-Modified",
    "Accept", "Accept-Charset", "Accept-Encoding", "Accept-Language", "Authorization", "Cookie", "Expect", "From", "Host", "If-Match",
    "If-Modified-Since", "If-None-Match", "If-Range", "If-Unmodified-Since", "Max-Forwards", "Proxy-Authorization", "Referer", "Range", "TE", "Translate", "User-Agent"
  };
  uint64_t total = 0u;
  uint32_t i;
  for (i = 0u; i < HttpHeaderRequestMaximum; ++i) {
    const HTTP_KNOWN_HEADER *header = &request->Headers.KnownHeaders[i];
    if (header->pRawValue != NULL || header->RawValueLength != 0u)
      total += alen(known_names[i]) + header->RawValueLength;
  }
  for (i = 0u; i < request->Headers.UnknownHeaderCount; ++i) {
    const HTTP_UNKNOWN_HEADER *header = &request->Headers.pUnknownHeaders[i];
    total += header->NameLength + header->RawValueLength;
  }
  if (total > UINT32_MAX) return 0u;
  *out = (uint32_t)total;
  return 1u;
}

static uint32_t parse_cookie_value(worker_state *worker, const char *raw,
                                   uint32_t raw_bytes, uint16_t **output,
                                   uint32_t *units) {
  uint32_t wire_units;
  size_t required = 0u;
  cf_cookie_unescape_status_v1 status;
  /* The total Cookie header has a transport-level 16 KiB -> 431 limit.
     Rack's per-value decoder limit is a malformed cookie map (400), not a
     request-header overflow. */
  if (raw_bytes > CF_COOKIE_UNESCAPE_MAX_WIRE_UTF8_BYTES_V1) return 2u;
  worker->cookie_input_units = CF_COOKIE_UNESCAPE_MAX_WIRE_UTF16_UNITS_V1;
  wire_units = utf8_to_u16((const uint8_t *)raw, raw_bytes, worker->cookie_input,
                           CF_COOKIE_UNESCAPE_MAX_WIRE_UTF16_UNITS_V1);
  if (wire_units == UINT32_MAX) return 2u;
  worker->cookie_input_units = wire_units;
  status = cf_cookie_unescape_utf16_v1(worker->cookie_input, wire_units,
      worker->cookie_decoded[0], CF_COOKIE_UNESCAPE_MAX_OUTPUT_UTF16_UNITS_V1,
      &required, worker->unescape_scratch, (size_t)cf_cookie_unescape_scratch_size_v1());
  if (status == CF_COOKIE_UNESCAPE_WIRE_TOO_LARGE_V1 ||
      status == CF_COOKIE_UNESCAPE_CAPACITY_V1) return 2u;
  if (status != CF_COOKIE_UNESCAPE_OK_V1 || required > UINT32_MAX) return 2u;
  worker->cookie_decoded_units[0] = (uint32_t)required;
  *output = worker->cookie_decoded[0];
  *units = (uint32_t)required;
  return 0u;
}

static int cookie_unicode_whitespace(uint32_t scalar) {
  return (scalar >= 0x0009u && scalar <= 0x000du) || scalar == 0x0020u ||
         scalar == 0x0085u || scalar == 0x00a0u || scalar == 0x1680u ||
         (scalar >= 0x2000u && scalar <= 0x200au) ||
         scalar == 0x2028u || scalar == 0x2029u || scalar == 0x202fu ||
         scalar == 0x205fu || scalar == 0x3000u;
}

static uint32_t utf8_scalar_forward(const uint8_t *bytes, uint32_t end,
                                    uint32_t *position, uint32_t *scalar) {
  uint32_t at = *position, first, needed, value, minimum, i;
  if (at >= end) return 0u;
  first = bytes[at];
  if (first < 0x80u) { *scalar = first; *position = at + 1u; return 1u; }
  if (first >= 0xc2u && first <= 0xdfu) { needed = 2u; value = first & 0x1fu; minimum = 0x80u; }
  else if (first >= 0xe0u && first <= 0xefu) { needed = 3u; value = first & 0x0fu; minimum = 0x800u; }
  else if (first >= 0xf0u && first <= 0xf4u) { needed = 4u; value = first & 0x07u; minimum = 0x10000u; }
  else { *scalar = 0xfffdu; *position = at + 1u; return 1u; }
  if (needed > end - at) { *scalar = 0xfffdu; *position = at + 1u; return 1u; }
  for (i = 1u; i < needed; ++i) {
    uint32_t next = bytes[at + i];
    if ((next & 0xc0u) != 0x80u) { *scalar = 0xfffdu; *position = at + 1u; return 1u; }
    value = (value << 6u) | (next & 0x3fu);
  }
  if (value < minimum || value > 0x10ffffu ||
      (value >= 0xd800u && value <= 0xdfffu)) {
    *scalar = 0xfffdu; *position = at + 1u; return 1u;
  }
  *scalar = value;
  *position = at + needed;
  return 1u;
}

static uint32_t utf8_scalar_before(const uint8_t *bytes, uint32_t begin,
                                   uint32_t end, uint32_t *scalar) {
  uint32_t start = end - 1u, position, decoded;
  while (start > begin && (bytes[start] & 0xc0u) == 0x80u && end - start < 4u) --start;
  position = start;
  if (!utf8_scalar_forward(bytes, end, &position, scalar) || position != end) {
    *scalar = 0xfffdu;
    return end - 1u;
  }
  decoded = end - start;
  return decoded;
}

static void trim_cookie_part(const uint8_t *bytes, uint32_t *begin,
                             uint32_t *end) {
  uint32_t position, scalar;
  while (*begin < *end) {
    position = *begin;
    (void)utf8_scalar_forward(bytes, *end, &position, &scalar);
    if (!cookie_unicode_whitespace(scalar)) break;
    *begin = position;
  }
  while (*begin < *end) {
    uint32_t width = utf8_scalar_before(bytes, *begin, *end, &scalar);
    if (!cookie_unicode_whitespace(scalar)) break;
    *end -= width;
  }
}

static uint32_t parse_cookies(worker_state *worker, const HTTP_REQUEST *request,
                              text_span *token, text_span *encrypted,
                              text_span *last) {
  static const char session_token_name[] = "session_token";
  static const char encrypted_session_name[] = "_campfire_session";
  static const char last_room_name[] = "last_room";
  const char *header = NULL;
  uint32_t length = 0u, count = 0u;
  uint32_t state = header_value(request, HttpHeaderCookie, "Cookie", &header, &length, &count);
  uint32_t offset = 0u, seen_token = 0u, seen_encrypted = 0u, seen_last = 0u;
  token->data = encrypted->data = last->data = NULL;
  token->units = encrypted->units = last->units = 0u;
  token->present = encrypted->present = last->present = 0u;
  if (state == 2u) return 2u;
  if (state == 0u) return 0u;
  if (length > HOST_MAX_COOKIE_HEADER_BYTES) return 3u;
  if (header == NULL) return 1u;
  while (offset < length) {
    uint32_t end = offset, next, begin, equal = UINT32_MAX, name_end, value_start, value_end;
    uint32_t decoded_units = 0u, decoded_status;
    uint16_t *decoded = NULL;
    while (end < length && header[end] != ';') ++end;
    next = end < length ? end + 1u : length;
    begin = offset;
    trim_cookie_part((const uint8_t *)header, &begin, &end);
    for (uint32_t j = begin; j < end; ++j) if (header[j] == '=') { equal = j; break; }
    if (equal != UINT32_MAX) {
      name_end = equal;
      value_start = equal + 1u;
      value_end = end;
      if (name_end - begin == sizeof(session_token_name) - 1u &&
          bytes_equal(header + begin, session_token_name,
                      sizeof(session_token_name) - 1u)) {
        if (seen_token++) return 2u;
        decoded_status = parse_cookie_value(worker, header + value_start, value_end - value_start, &decoded, &decoded_units);
        if (decoded_status != 0u) return decoded_status;
        bytes_copy(worker->cookie_decoded[1], decoded, decoded_units * sizeof(uint16_t));
        worker->cookie_decoded_units[1] = decoded_units;
        token->data = worker->cookie_decoded[1]; token->units = decoded_units; token->present = 1u;
      } else if (name_end - begin == sizeof(encrypted_session_name) - 1u &&
                 bytes_equal(header + begin, encrypted_session_name,
                             sizeof(encrypted_session_name) - 1u)) {
        if (seen_encrypted++) return 2u;
        decoded_status = parse_cookie_value(worker, header + value_start, value_end - value_start, &decoded, &decoded_units);
        if (decoded_status != 0u) return decoded_status;
        bytes_copy(worker->cookie_decoded[2], decoded, decoded_units * sizeof(uint16_t));
        worker->cookie_decoded_units[2] = decoded_units;
        encrypted->data = worker->cookie_decoded[2]; encrypted->units = decoded_units; encrypted->present = 1u;
      } else if (name_end - begin == sizeof(last_room_name) - 1u &&
                 bytes_equal(header + begin, last_room_name,
                             sizeof(last_room_name) - 1u)) {
        if (seen_last++) return 2u;
        decoded_status = parse_cookie_value(worker, header + value_start, value_end - value_start, &decoded, &decoded_units);
        if (decoded_status != 0u) return decoded_status;
        /* The token and encrypted cookie need their decoded spans at once. Use the final slot for last_room. */
        bytes_copy(worker->cookie_decoded[3], decoded, decoded_units * sizeof(uint16_t));
        worker->cookie_decoded_units[3] = decoded_units;
        last->data = worker->cookie_decoded[3]; last->units = decoded_units; last->present = 1u;
      }
    }
    offset = next;
  }
  return 0u;
}

static int query_hex_value(uint8_t value) {
  if (value >= (uint8_t)'0' && value <= (uint8_t)'9') return value - (uint8_t)'0';
  if (value >= (uint8_t)'a' && value <= (uint8_t)'f') return value - (uint8_t)'a' + 10;
  if (value >= (uint8_t)'A' && value <= (uint8_t)'F') return value - (uint8_t)'A' + 10;
  return -1;
}

static uint32_t query_key_is_bot_key(const uint8_t *raw, uint32_t begin,
                                     uint32_t end) {
  static const char target[] = "bot_key";
  uint32_t at = begin, index = 0u;
  while (at < end) {
    uint8_t decoded = raw[at++];
    if (decoded == (uint8_t)'+') decoded = (uint8_t)' ';
    else if (decoded == (uint8_t)'%' && end - at >= 2u) {
      int high = query_hex_value(raw[at]), low = query_hex_value(raw[at + 1u]);
      if (high >= 0 && low >= 0) {
        decoded = (uint8_t)((high << 4) | low);
        at += 2u;
      }
    }
    if (index >= sizeof(target) - 1u || decoded > 0x7fu) return 0u;
    {
      char actual = (char)decoded;
      char expected = target[index++];
      if (actual >= 'A' && actual <= 'Z') actual = (char)(actual + ('a' - 'A'));
      if (actual != expected) return 0u;
    }
  }
  return index == sizeof(target) - 1u;
}

static uint32_t query_read_token(const uint8_t *raw, uint32_t end,
                                 uint32_t at, uint8_t *value,
                                 uint32_t *next, uint32_t *was_escaped) {
  uint8_t byte;
  if (at >= end) return 0u;
  byte = raw[at];
  *was_escaped = 0u;
  if (byte == (uint8_t)'+') {
    *value = (uint8_t)' ';
    *next = at + 1u;
    return 1u;
  }
  if (byte == (uint8_t)'%' && end - at >= 3u) {
    int high = query_hex_value(raw[at + 1u]), low = query_hex_value(raw[at + 2u]);
    if (high >= 0 && low >= 0) {
      *value = (uint8_t)((high << 4) | low);
      *next = at + 3u;
      *was_escaped = 1u;
      return 1u;
    }
  }
  *value = byte;
  *next = at + 1u;
  return 1u;
}

static uint32_t query_emit_u16(uint16_t *output, uint32_t capacity,
                               uint32_t *used, uint32_t scalar) {
  if (scalar <= 0xffffu) {
    if (*used >= capacity) return 0u;
    output[(*used)++] = (uint16_t)scalar;
    return 1u;
  }
  if (*used > capacity || capacity - *used < 2u) return 0u;
  scalar -= 0x10000u;
  output[(*used)++] = (uint16_t)(0xd800u + (scalar >> 10u));
  output[(*used)++] = (uint16_t)(0xdc00u + (scalar & 0x3ffu));
  return 1u;
}

static uint32_t query_emit_escaped_byte(uint16_t *output, uint32_t capacity,
                                        uint32_t *used, uint8_t value) {
  static const char hex[] = "0123456789ABCDEF";
  if (*used > capacity || capacity - *used < 3u) return 0u;
  output[(*used)++] = (uint16_t)'%';
  output[(*used)++] = (uint16_t)hex[value >> 4u];
  output[(*used)++] = (uint16_t)hex[value & 15u];
  return 1u;
}

static uint32_t query_decode_value(const uint8_t *raw, uint32_t begin,
                                   uint32_t end, uint16_t *output,
                                   uint32_t capacity, uint32_t *units) {
  uint32_t at = begin, used = 0u;
  while (at < end) {
    uint8_t first;
    uint32_t next_first, first_escaped;
    if (!query_read_token(raw, end, at, &first, &next_first, &first_escaped)) return 0u;
    if (first < 0x80u) {
      if (!query_emit_u16(output, capacity, &used, first)) return 0u;
      at = next_first;
      continue;
    }
    {
      uint32_t needed, value, minimum, look = next_first, valid = 1u;
      if (first >= 0xc2u && first <= 0xdfu) { needed = 2u; value = first & 0x1fu; minimum = 0x80u; }
      else if (first >= 0xe0u && first <= 0xefu) { needed = 3u; value = first & 0x0fu; minimum = 0x800u; }
      else if (first >= 0xf0u && first <= 0xf4u) { needed = 4u; value = first & 0x07u; minimum = 0x10000u; }
      else { needed = 0u; value = 0u; minimum = 0u; valid = 0u; }
      for (uint32_t i = 1u; valid && i < needed; ++i) {
        uint8_t continuation;
        uint32_t next, escaped;
        if (!query_read_token(raw, end, look, &continuation, &next, &escaped) ||
            (continuation & 0xc0u) != 0x80u) {
          valid = 0u;
          break;
        }
        value = (value << 6u) | (continuation & 0x3fu);
        look = next;
      }
      if (valid && (value < minimum || value > 0x10ffffu ||
          (value >= 0xd800u && value <= 0xdfffu))) valid = 0u;
      if (!valid) {
        if (first_escaped) {
          if (!query_emit_escaped_byte(output, capacity, &used, first)) return 0u;
        } else if (!query_emit_u16(output, capacity, &used, 0xfffdu)) return 0u;
        at = next_first;
        continue;
      }
      if (!query_emit_u16(output, capacity, &used, value)) return 0u;
      at = look;
    }
  }
  *units = used;
  return 1u;
}

static uint32_t parse_bot_key_query(worker_state *worker,
                                    const HTTP_REQUEST *request,
                                    text_span *bot_key) {
  const uint8_t *raw = (const uint8_t *)request->pRawUrl;
  uint32_t bytes = request->RawUrlLength, query = UINT32_MAX;
  uint32_t at, found = 0u, selected_start = 0u, selected_bytes = 0u;
  size_t required = 0u;
  bot_key->data = NULL; bot_key->units = 0u; bot_key->present = 0u;
  if (raw == NULL || bytes == 0u) return 1u;
  for (at = 0u; at < bytes; ++at) if (raw[at] == (uint8_t)'?') { query = at + 1u; break; }
  if (query == UINT32_MAX) return 1u;
  at = query;
  while (at <= bytes) {
    uint32_t end = at, equal = UINT32_MAX, key_end;
    while (end < bytes && raw[end] != (uint8_t)'&') ++end;
    for (uint32_t i = at; i < end; ++i) if (raw[i] == (uint8_t)'=') { equal = i; break; }
    key_end = equal == UINT32_MAX ? end : equal;
    if (query_key_is_bot_key(raw, at, key_end)) {
      ++found;
      selected_start = equal == UINT32_MAX ? end : equal + 1u;
      selected_bytes = end - selected_start;
    }
    if (end == bytes) break;
    at = end + 1u;
  }
  if (found != 1u || selected_bytes > HOST_MAX_COOKIE_HEADER_BYTES) return 1u;
  worker->cookie_input_units = CF_COOKIE_UNESCAPE_MAX_WIRE_UTF16_UNITS_V1;
  worker->cookie_decoded_units[0] = 0u;
  {
    uint32_t output_units = 0u;
    if (!query_decode_value(raw, selected_start,
          selected_start + selected_bytes, worker->cookie_decoded[0],
          CF_COOKIE_UNESCAPE_MAX_OUTPUT_UTF16_UNITS_V1, &output_units)) return 1u;
    worker->cookie_decoded_units[0] = output_units;
    required = output_units;
  }
  {
    uint32_t begin = 0u, end = (uint32_t)required;
    while (begin < end && (worker->cookie_decoded[0][begin] == 0u ||
           (worker->cookie_decoded[0][begin] >= 9u && worker->cookie_decoded[0][begin] <= 13u) ||
           worker->cookie_decoded[0][begin] == 32u)) ++begin;
    while (end > begin && (worker->cookie_decoded[0][end - 1u] == 0u ||
           (worker->cookie_decoded[0][end - 1u] >= 9u && worker->cookie_decoded[0][end - 1u] <= 13u) ||
           worker->cookie_decoded[0][end - 1u] == 32u)) --end;
    if (end == begin) return 1u;
    if (begin != 0u) bytes_copy(worker->cookie_decoded[0], worker->cookie_decoded[0] + begin,
                                (end - begin) * sizeof(uint16_t));
    if (end < worker->cookie_decoded_units[0]) {
      bytes_zero(worker->cookie_decoded[0] + end,
          (size_t)(worker->cookie_decoded_units[0] - end) * sizeof(uint16_t));
      worker->cookie_decoded_units[0] = end;
    }
    bot_key->data = worker->cookie_decoded[0];
    bot_key->units = end - begin;
    bot_key->present = 1u;
  }
  return 1u;
}

static uint32_t remote_ip_text(worker_state *worker, const HTTP_REQUEST *request,
                               uint16_t **output, uint32_t *units) {
  char buffer[64];
  const SOCKADDR *address = request->Address.pRemoteAddress;
  if (address == NULL) { *output = arena_text(worker, (const uint8_t *)"", 0u, units); return *output == NULL ? 0u : 1u; }
  if (address->sa_family == AF_INET) {
    const SOCKADDR_IN *v4 = (const SOCKADDR_IN *)address;
    const uint8_t *bytes = (const uint8_t *)&v4->sin_addr;
    uint32_t pos = 0u;
    for (uint32_t i = 0u; i < 4u; ++i) {
      uint32_t value = bytes[i]; char digits[3]; uint32_t n = 0u;
      do { digits[n++] = (char)('0' + value % 10u); value /= 10u; } while (value != 0u);
      while (n > 0u) buffer[pos++] = digits[--n];
      if (i != 3u) buffer[pos++] = '.';
    }
    *output = arena_text(worker, (const uint8_t *)buffer, pos, units);
    return *output == NULL ? 0u : 1u;
  }
  if (address->sa_family == AF_INET6) {
    const SOCKADDR_IN6 *v6 = (const SOCKADDR_IN6 *)address;
    if (InetNtopA(AF_INET6, (PVOID)&v6->sin6_addr, buffer, sizeof(buffer)) == NULL) return 0u;
    *output = arena_text(worker, (const uint8_t *)buffer, alen(buffer), units);
    return *output == NULL ? 0u : 1u;
  }
  return 0u;
}

static uint32_t browser_info(worker_state *worker, text_span user_agent,
                             al_campfire_browser_result_v1 *result) {
  al_campfire_browser_status_v1 status;
  bytes_zero(result, sizeof(*result));
  status = al_campfire_browser_normalize_utf16_v1(user_agent.present,
      user_agent.data, user_agent.units,
      worker->browser_scratch, worker->browser_scratch_bytes,
      worker->browser_version, 4096u, result);
  return status == AL_CAMPFIRE_BROWSER_OK_V1;
}

static uint32_t header_text(worker_state *worker, const HTTP_REQUEST *request,
                            int known_id, const char *name,
                            text_span *out) {
  const char *value = NULL;
  uint32_t length = 0u, count = 0u, units = 0u;
  uint32_t status = header_value(request, known_id, name, &value, &length, &count);
  out->present = 0u; out->data = NULL; out->units = 0u;
  if (status == 0u) return 1u;
  if (status == 2u) {
    static const uint8_t duplicate[] = "invalid-duplicate-header";
    uint16_t *text = arena_text(worker, duplicate, (uint32_t)sizeof(duplicate) - 1u, &units);
    if (text == NULL) return 0u;
    out->data = text; out->units = units; out->present = 1u;
    return 1u;
  }
  if (value == NULL) return 0u;
  out->data = arena_text(worker, (const uint8_t *)value, length, &units);
  if (out->data == NULL) return 0u;
  out->units = units; out->present = 1u;
  return 1u;
}

static uint32_t cookie_span_to_utf8(const text_span span, uint8_t *output,
                                    uint32_t capacity, uint32_t *bytes) {
  if (!span.present) { *bytes = 0u; return 1u; }
  *bytes = u16_to_utf8(span.data, span.units, output, capacity);
  return *bytes != UINT32_MAX;
}

static uint32_t append_ascii(uint8_t *buffer, uint32_t capacity,
                             uint32_t *used, const char *text) {
  uint32_t length = alen(text);
  if (*used > capacity || length > capacity - *used) return 0u;
  bytes_copy(buffer + *used, text, length);
  *used += length;
  return 1u;
}

static uint32_t append_decimal_u32(uint8_t *buffer, uint32_t capacity,
                                   uint32_t *used, uint32_t value) {
  uint8_t digits[10];
  uint32_t count = 0u;
  do { digits[count++] = (uint8_t)('0' + (value % 10u)); value /= 10u; } while (value != 0u);
  if (*used > capacity || count > capacity - *used) return 0u;
  while (count > 0u) buffer[(*used)++] = digits[--count];
  return 1u;
}

static uint32_t append_decimal_i64(uint8_t *buffer, uint32_t capacity,
                                   uint32_t *used, int64_t value) {
  uint64_t magnitude;
  uint8_t digits[20];
  uint32_t count = 0u;
  if (value < 0) {
    if (*used >= capacity) return 0u;
    buffer[(*used)++] = '-';
    magnitude = (uint64_t)(-(value + 1)) + 1u;
  } else magnitude = (uint64_t)value;
  do { digits[count++] = (uint8_t)('0' + (magnitude % 10u)); magnitude /= 10u; } while (magnitude != 0u);
  if (*used > capacity || count > capacity - *used) return 0u;
  while (count > 0u) buffer[(*used)++] = digits[--count];
  return 1u;
}

static uint32_t format_expiry(int64_t now_us, uint8_t *output,
                              uint32_t capacity, uint32_t *written) {
  return (uint32_t)al_campfire_cookie_expiry_20_years_v1(
      now_us, output, capacity, written);
}

static uint32_t make_cookie_header(worker_state *worker, uint32_t index,
                                   const char *name, const uint8_t *raw,
                                   uint32_t raw_bytes, int permanent,
                                   int http_only, int64_t now_us) {
  uint32_t used = 0u, required = 0u, escaped;
  uint8_t expiry[64]; uint32_t expiry_bytes = 0u;
  uint8_t *buffer = worker->cookie_wire[index];
  if (raw_bytes > HOST_COOKIE_RAW_BYTES) return 0u;
  worker->cookie_raw_bytes[index] = raw_bytes;
  bytes_copy(worker->cookie_raw[index], raw, raw_bytes);
  if (cf_cookie_escape_ascii(worker->cookie_raw[index], raw_bytes, buffer,
        HOST_COOKIE_WIRE_BYTES, &required) != 0 || required > HOST_COOKIE_WIRE_BYTES) return 0u;
  escaped = required;
  /* Move escaped bytes right to make room for the fixed attribute prefix. */
  if (escaped + 128u > HOST_COOKIE_WIRE_BYTES) return 0u;
  worker->cookie_wire_bytes[index] = 128u + escaped;
  for (uint32_t i = escaped; i > 0u; --i) buffer[i + 128u - 1u] = buffer[i - 1u];
  used = 128u;
  /* Prefix and attributes are written into the reserved gap below. */
  {
    uint8_t prefix[40]; uint32_t prefix_bytes = 0u;
    if (!append_ascii(prefix, sizeof(prefix), &prefix_bytes, name) ||
        !append_ascii(prefix, sizeof(prefix), &prefix_bytes, "=")) return 0u;
    if (prefix_bytes > 128u) return 0u;
    bytes_copy(buffer, prefix, prefix_bytes);
    used = prefix_bytes + escaped;
  }
  /* The escaped span begins at 128, so compact it behind the cookie name. */
  if (escaped != 0u) bytes_copy(buffer + used - escaped, buffer + 128u, escaped);
  if (!append_ascii(buffer, HOST_COOKIE_WIRE_BYTES, &used, "; Path=/; SameSite=Lax")) return 0u;
  worker->cookie_wire_bytes[index] = used;
  if (permanent) {
    if (!format_expiry(now_us, expiry, sizeof(expiry), &expiry_bytes) ||
        !append_ascii(buffer, HOST_COOKIE_WIRE_BYTES, &used, "; Expires=")) return 0u;
    if (expiry_bytes > HOST_COOKIE_WIRE_BYTES - used) return 0u;
    bytes_copy(buffer + used, expiry, expiry_bytes); used += expiry_bytes;
    worker->cookie_wire_bytes[index] = used;
  }
  if (http_only && !append_ascii(buffer, HOST_COOKIE_WIRE_BYTES, &used, "; HttpOnly")) return 0u;
  worker->cookie_wire_bytes[index] = used;
  return 1u;
}

static uint32_t make_return_url(worker_state *worker, const host_config *config,
                                const HTTP_REQUEST *request) {
  uint32_t authority = wlen(config->authority), raw_bytes = request->RawUrlLength;
  int converted;
  if (request->pRawUrl == NULL || raw_bytes > HOST_RETURN_URL_BYTES ||
      authority > HOST_RETURN_URL_BYTES) return 0u;
  converted = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS,
      config->authority, (int)authority, (LPSTR)worker->return_url,
      (int)HOST_RETURN_URL_BYTES, NULL, NULL);
  if (converted <= 0 || (uint32_t)converted + raw_bytes > HOST_RETURN_URL_BYTES) return 0u;
  worker->return_url_bytes = (uint32_t)converted;
  bytes_copy(worker->return_url + worker->return_url_bytes, request->pRawUrl, raw_bytes);
  worker->return_url_bytes += raw_bytes;
  return 1u;
}

static uint32_t make_entry_input(worker_state *worker, const HTTP_REQUEST *request,
                                 uint32_t method, int64_t room_id,
                                 uint32_t *input_bytes) {
  const char *user_agent_raw = NULL;
  uint32_t ua_bytes = 0u, count = 0u;
  text_span user_agent = { NULL, 0u, 0u };
  text_span origin = { NULL, 0u, 0u };
  text_span fetch_site = { NULL, 0u, 0u };
  text_span token = { NULL, 0u, 0u };
  text_span encrypted = { NULL, 0u, 0u };
  text_span last_room = { NULL, 0u, 0u };
  text_span bot_key = { NULL, 0u, 0u };
  uint32_t cookie_status, required = 0u;
  uint16_t *remote = NULL, *base = NULL;
  uint32_t remote_units = 0u, base_units = 0u;
  al_campfire_browser_result_v1 browser;
  cf_room_request request_value;
  uint32_t status;
  worker->text_used = 0u;
  worker->request_encrypted_cookie.data = NULL;
  worker->request_encrypted_cookie.units = 0u;
  worker->request_encrypted_cookie.present = 0u;
  if (header_value(request, HttpHeaderUserAgent, "User-Agent", &user_agent_raw, &ua_bytes, &count) == 2u) {
    static const uint8_t duplicate[] = "invalid-duplicate-header";
    user_agent.data = arena_text(worker, duplicate, (uint32_t)sizeof(duplicate)-1u, &user_agent.units);
    if (user_agent.data == NULL) return 0u;
    user_agent.present = 1u;
  } else if (user_agent_raw != NULL) {
    user_agent.data = arena_text(worker, (const uint8_t *)user_agent_raw, ua_bytes, &user_agent.units);
    if (user_agent.data == NULL) return 0u;
    user_agent.present = 1u;
  }
  if (!header_text(worker, request, -1, "Origin", &origin) ||
      !header_text(worker, request, -1, "Sec-Fetch-Site", &fetch_site)) return 0u;
  cookie_status = parse_cookies(worker, request, &token, &encrypted, &last_room);
  if (cookie_status != 0u) return cookie_status;
  worker->request_encrypted_cookie = encrypted;
  (void)parse_bot_key_query(worker, request, &bot_key);
  if (!remote_ip_text(worker, request, &remote, &remote_units)) return 0u;
  base_units = wlen(worker->config->authority);
  if (worker->text_used >= HOST_TEXT_ARENA_UNITS ||
      base_units + 1u > HOST_TEXT_ARENA_UNITS - worker->text_used) return 0u;
  base = worker->text_arena + worker->text_used;
  bytes_copy(base, worker->config->authority, base_units * sizeof(uint16_t));
  base[base_units] = 0u;
  worker->text_used += base_units + 1u;
  if (!browser_info(worker, user_agent, &browser)) {
    /* Browser input and product bounds are explicit 431 failures, never fallback. */
    return 3u;
  }
  request_value.method = method;
  request_value.room_id = room_id;
  worker->request_now_unix_microseconds = current_unix_microseconds_i64();
  request_value.now_unix_microseconds = worker->request_now_unix_microseconds;
  request_value.remote_ip.data = remote; request_value.remote_ip.length = remote_units;
  request_value.base_url.data = base; request_value.base_url.length = base_units;
  request_value.user_agent.present = user_agent.present; request_value.user_agent.value.data = user_agent.data; request_value.user_agent.value.length = user_agent.units;
  request_value.origin.present = origin.present; request_value.origin.value.data = origin.data; request_value.origin.value.length = origin.units;
  request_value.fetch_site.present = fetch_site.present; request_value.fetch_site.value.data = fetch_site.data; request_value.fetch_site.value.length = fetch_site.units;
  request_value.session_token.present = token.present; request_value.session_token.value.data = token.data; request_value.session_token.value.length = token.units;
  request_value.bot_key.present = bot_key.present; request_value.bot_key.value.data = bot_key.data; request_value.bot_key.value.length = bot_key.units;
  request_value.last_room.present = last_room.present; request_value.last_room.value.data = last_room.data; request_value.last_room.value.length = last_room.units;
  request_value.request_is_ssl = 0u; request_value.force_ssl = 0u;
  request_value.accepts_turbo = 0u; request_value.accepts_html = 1u;
  request_value.browser_family = browser.family;
  request_value.browser_version.present = browser.has_version;
  request_value.browser_version.value.data = browser.has_version ? worker->browser_version : NULL;
  request_value.browser_version.value.length = browser.version_units;
  request_value.browser_is_bot = browser.is_bot;
  status = cf_encode_room_request(&request_value, NULL, 0u, &required);
  if (status != 2u || required == 0u || required > HOST_INPUT_BYTES) return 0u;
  worker->active_input_bytes = required;
  status = cf_encode_room_request(&request_value, worker->input, HOST_INPUT_BYTES, &required);
  if (status != 0u) return 0u;
  *input_bytes = required;
  return 1u;
}

static const char *reason_phrase(uint32_t status) {
  switch (status) {
    case 200u: return "OK";
    case 302u: return "Found";
    case 400u: return "Bad Request";
    case 404u: return "Not Found";
    case 405u: return "Method Not Allowed";
    case 413u: return "Payload Too Large";
    case 431u: return "Request Header Fields Too Large";
    case 500u: return "Internal Server Error";
    default: return "OK";
  }
}

static uint32_t send_response(worker_state *worker, HTTP_REQUEST_ID request_id,
                              uint32_t status, const uint8_t *content_type,
                              uint32_t content_type_bytes,
                              const uint8_t *location, uint32_t location_bytes,
                              const uint8_t *allow, uint32_t allow_bytes,
                              const uint8_t *body, uint32_t body_bytes,
                              uint32_t content_length, int send_entity,
                              uint32_t cookie_count) {
  static const char cache_control[] = "no-store";
  static const char nosniff_name[] = "X-Content-Type-Options";
  static const char nosniff_value[] = "nosniff";
  static const char location_name[] = "Location";
  static const char allow_name[] = "Allow";
  static const char set_cookie_name[] = "Set-Cookie";
  HTTP_RESPONSE response;
  HTTP_DATA_CHUNK chunk;
  HTTP_UNKNOWN_HEADER unknown[8];
  HTTP_CACHE_POLICY cache_policy;
  char content_length_text[11];
  uint32_t content_length_digits = 0u, unknown_count = 0u;
  ULONG bytes_sent = 0u;
#if CAMPFIRE_HTTP_STAGE_PROFILE
  ULONG api_status;
  uint64_t stage_started;
#endif
  const char *reason = reason_phrase(status);
  bytes_zero(&response, sizeof(response));
  bytes_zero(&chunk, sizeof(chunk));
  bytes_zero(unknown, sizeof(unknown));
  bytes_zero(&cache_policy, sizeof(cache_policy));
  response.Version.MajorVersion = 1;
  response.Version.MinorVersion = 1;
  response.StatusCode = (USHORT)status;
  response.pReason = (PCSTR)reason;
  response.ReasonLength = (USHORT)alen(reason);
  response.Headers.KnownHeaders[HttpHeaderCacheControl].pRawValue = cache_control;
  response.Headers.KnownHeaders[HttpHeaderCacheControl].RawValueLength = (USHORT)(sizeof(cache_control) - 1u);
  if (content_type != NULL && content_type_bytes != 0u) {
    response.Headers.KnownHeaders[HttpHeaderContentType].pRawValue = (PCSTR)content_type;
    response.Headers.KnownHeaders[HttpHeaderContentType].RawValueLength = (USHORT)content_type_bytes;
  }
  if (!append_decimal_u32((uint8_t *)content_length_text, sizeof(content_length_text),
                          &content_length_digits, content_length)) return 0u;
  response.Headers.KnownHeaders[HttpHeaderContentLength].pRawValue = content_length_text;
  response.Headers.KnownHeaders[HttpHeaderContentLength].RawValueLength = (USHORT)content_length_digits;
  if (location != NULL && location_bytes != 0u) {
    response.Headers.KnownHeaders[HttpHeaderLocation].pRawValue = (PCSTR)location;
    response.Headers.KnownHeaders[HttpHeaderLocation].RawValueLength = (USHORT)location_bytes;
  }
  if (allow != NULL && allow_bytes != 0u) {
    response.Headers.KnownHeaders[HttpHeaderAllow].pRawValue = (PCSTR)allow;
    response.Headers.KnownHeaders[HttpHeaderAllow].RawValueLength = (USHORT)allow_bytes;
  }
  unknown[unknown_count].pName = nosniff_name;
  unknown[unknown_count].NameLength = (USHORT)(sizeof(nosniff_name) - 1u);
  unknown[unknown_count].pRawValue = nosniff_value;
  unknown[unknown_count].RawValueLength = (USHORT)(sizeof(nosniff_value) - 1u);
  ++unknown_count;
  if (location != NULL && location_bytes != 0u) {
    unknown[unknown_count].pName = location_name;
    unknown[unknown_count].NameLength = (USHORT)(sizeof(location_name) - 1u);
    unknown[unknown_count].pRawValue = (PCSTR)location;
    unknown[unknown_count].RawValueLength = (USHORT)location_bytes;
    ++unknown_count;
    response.Headers.KnownHeaders[HttpHeaderLocation].pRawValue = NULL;
    response.Headers.KnownHeaders[HttpHeaderLocation].RawValueLength = 0u;
  }
  if (allow != NULL && allow_bytes != 0u) {
    unknown[unknown_count].pName = allow_name;
    unknown[unknown_count].NameLength = (USHORT)(sizeof(allow_name) - 1u);
    unknown[unknown_count].pRawValue = (PCSTR)allow;
    unknown[unknown_count].RawValueLength = (USHORT)allow_bytes;
    ++unknown_count;
    response.Headers.KnownHeaders[HttpHeaderAllow].pRawValue = NULL;
    response.Headers.KnownHeaders[HttpHeaderAllow].RawValueLength = 0u;
  }
  if (cookie_count > 3u || unknown_count + cookie_count > 8u) return 0u;
  for (uint32_t i = 0u; i < cookie_count; ++i) {
    unknown[unknown_count].pName = set_cookie_name;
    unknown[unknown_count].NameLength = (USHORT)(sizeof(set_cookie_name) - 1u);
    uint32_t cookie_index = worker->response_cookie_indices[i];
    if (cookie_index > 2u || worker->cookie_wire_bytes[cookie_index] > USHRT_MAX) return 0u;
    unknown[unknown_count].pRawValue = (PCSTR)worker->cookie_wire[cookie_index];
    unknown[unknown_count].RawValueLength = (USHORT)worker->cookie_wire_bytes[cookie_index];
    ++unknown_count;
  }
  response.Headers.UnknownHeaderCount = (USHORT)unknown_count;
  response.Headers.pUnknownHeaders = unknown_count == 0u ? NULL : unknown;
  if (send_entity && body != NULL && body_bytes != 0u) {
    chunk.DataChunkType = HttpDataChunkFromMemory;
    chunk.FromMemory.pBuffer = (PVOID)body;
    chunk.FromMemory.BufferLength = body_bytes;
    response.EntityChunkCount = 1;
    response.pEntityChunks = &chunk;
  }
  cache_policy.Policy = HttpCachePolicyNocache;
  cache_policy.SecondsToLive = 0u;
#if CAMPFIRE_HTTP_STAGE_PROFILE
  stage_started = host_stage_qpc_now();
  api_status = HttpSendHttpResponse(worker->config->request_queue, request_id, 0u,
        &response, &cache_policy, &bytes_sent, NULL, 0u, NULL, NULL);
  host_stage_record(worker, HOST_STAGE_SEND_API, stage_started);
  if (api_status != NO_ERROR) return 0u;
#else
  if (HttpSendHttpResponse(worker->config->request_queue, request_id, 0u,
        &response, &cache_policy, &bytes_sent, NULL, 0u, NULL, NULL) != NO_ERROR) return 0u;
#endif
  return 1u;
}

static uint32_t ensure_body_committed(worker_state *worker, uint32_t body_bytes) {
  uint32_t target;
  SYSTEM_INFO system_info;
  if (body_bytes > HOST_BODY_BYTES) return 0u;
  if (body_bytes <= worker->body_committed_bytes) return 1u;
  GetSystemInfo(&system_info);
  if (system_info.dwPageSize == 0u || body_bytes > UINT32_MAX - system_info.dwPageSize + 1u) return 0u;
  target = (body_bytes + system_info.dwPageSize - 1u) / system_info.dwPageSize * system_info.dwPageSize;
  if (target > HOST_BODY_BYTES) target = HOST_BODY_BYTES;
  if (target <= worker->body_committed_bytes) return 0u;
  if (VirtualAlloc(worker->body + worker->body_committed_bytes,
                   target - worker->body_committed_bytes,
                   MEM_COMMIT, PAGE_READWRITE) == NULL) return 0u;
  worker->body_committed_bytes = target;
  return 1u;
}

static uint32_t host_header_matches(const host_config *config,
                                    const HTTP_REQUEST *request) {
  const char *host = NULL;
  uint32_t bytes = 0u, count = 0u, expected = wlen(config->host_header);
  if (header_value(request, HttpHeaderHost, "Host", &host, &bytes, &count) != 1u ||
      host == NULL || bytes != expected) return 0u;
  for (uint32_t i = 0u; i < expected; ++i) {
    char a = host[i], b = (char)config->host_header[i];
    if (a >= 'A' && a <= 'Z') a = (char)(a + ('a' - 'A'));
    if (b >= 'A' && b <= 'Z') b = (char)(b + ('a' - 'A'));
    if (a != b) return 0u;
  }
  return 1u;
}

static uint32_t content_length_status(const HTTP_REQUEST *request) {
  const char *value = NULL;
  uint32_t bytes = 0u, count = 0u, length = 0u;
  uint32_t state = header_value(request, HttpHeaderContentLength,
                                "Content-Length", &value, &bytes, &count);
  if (state == 0u) return 0u;
  if (state != 1u || value == NULL || bytes == 0u) return 400u;
  for (uint32_t i = 0u; i < bytes; ++i) {
    uint32_t digit;
    if (value[i] < '0' || value[i] > '9') return 400u;
    digit = (uint32_t)(value[i] - '0');
    if (length > (UINT32_MAX - digit) / 10u) return 413u;
    length = length * 10u + digit;
  }
  return length > 65536u ? 413u : 0u;
}

static uint32_t send_empty_status(worker_state *worker,
                                  const HTTP_REQUEST *request,
                                  uint32_t status, const uint8_t *allow,
                                  uint32_t allow_bytes) {
  return send_response(worker, request->RequestId, status, NULL, 0u,
      NULL, 0u, allow, allow_bytes, NULL, 0u, 0u, 0, 0u);
}

static uint32_t execute_and_send(worker_state *worker,
                                 const HTTP_REQUEST *request,
                                 uint32_t method, int64_t room_id);

static uint32_t process_http_request(worker_state *worker,
                                     const HTTP_REQUEST *request) {
  static const uint8_t allow[] = "GET, HEAD";
  uint32_t path_units, id_start = 0u, id_units = 0u, header_bytes = 0u;
  uint32_t content_status, method;
  int64_t room_id;
  const WCHAR *path = request->CookedUrl.pAbsPath;
  if (path == NULL || request->CookedUrl.AbsPathLength % sizeof(WCHAR) != 0u)
    return send_empty_status(worker, request, 404u, NULL, 0u);
  path_units = request->CookedUrl.AbsPathLength / (uint32_t)sizeof(WCHAR);
  if (!fixed_ascii_path(path, path_units, &id_start, &id_units))
    return send_empty_status(worker, request, 404u, NULL, 0u);
  if (request->Verb != HttpVerbGET && request->Verb != HttpVerbHEAD)
    return send_empty_status(worker, request, 405u, allow,
                             (uint32_t)sizeof(allow) - 1u);
  if (!host_header_matches(worker->config, request))
    return send_empty_status(worker, request, 400u, NULL, 0u);
  if (!request_header_bytes(request, &header_bytes) || header_bytes > HOST_MAX_HEADER_BYTES)
    return send_empty_status(worker, request, 431u, NULL, 0u);
  content_status = content_length_status(request);
  if (content_status != 0u)
    return send_empty_status(worker, request, content_status, NULL, 0u);
  if (!parse_i64(path + id_start, id_units, &room_id))
    return send_empty_status(worker, request, 400u, NULL, 0u);
  method = request->Verb == HttpVerbHEAD ? 1u : 0u;
  return execute_and_send(worker, request, method, room_id);
}

static DWORD WINAPI worker_entry(void *argument) {
  worker_state *worker = (worker_state *)argument;
  host_config *config = worker->config;
#if CAMPFIRE_HTTP_STAGE_PROFILE
  uint64_t stage_started;
#endif
  if (!worker_allocate(worker)) {
    InterlockedExchange((volatile LONG *)&config->fatal_status, 60L);
    InterlockedIncrement((volatile LONG *)&config->ready_workers);
    SetEvent(config->workers_ready_event);
    worker_release(worker);
    return 1u;
  }
#if CAMPFIRE_HTTP_STAGE_PROFILE
  host_stage_capture_cpu_start(worker);
#endif
  if (InterlockedIncrement((volatile LONG *)&config->ready_workers) ==
      (LONG)config->worker_count) SetEvent(config->workers_ready_event);
  for (;;) {
    HTTP_REQUEST *request = (HTTP_REQUEST *)worker->request_buffer;
    ULONG bytes_received = 0u;
    ULONG status;
    request->RequestId = HTTP_NULL_ID;
#if CAMPFIRE_HTTP_STAGE_PROFILE
    stage_started = host_stage_qpc_now();
#endif
    status = HttpReceiveHttpRequest(config->request_queue, HTTP_NULL_ID, 0u,
        request, HOST_REQUEST_BUFFER_BYTES, &bytes_received, NULL);
#if CAMPFIRE_HTTP_STAGE_PROFILE
    host_stage_record(worker, HOST_STAGE_RECEIVE_WAIT, stage_started);
#endif
    if (status == NO_ERROR) {
      (void)process_http_request(worker, request);
      ++worker->requests;
#if CAMPFIRE_HTTP_STAGE_PROFILE
      stage_started = host_stage_qpc_now();
#endif
      clear_request_sensitive(worker);
      bytes_zero(worker->request_buffer, HOST_REQUEST_BUFFER_BYTES);
#if CAMPFIRE_HTTP_STAGE_PROFILE
      host_stage_record(worker, HOST_STAGE_REQUEST_SCRUB, stage_started);
#endif
      continue;
    }
    if (status == ERROR_CONNECTION_INVALID) {
      /* A client may disconnect while HTTP.sys is receiving its request. */
      bytes_zero(worker->request_buffer, HOST_REQUEST_BUFFER_BYTES);
      continue;
    }
    if (status == ERROR_OPERATION_ABORTED || status == ERROR_INVALID_HANDLE) {
      if (WaitForSingleObject(config->stop_event, 0u) == WAIT_OBJECT_0) break;
      InterlockedExchange((volatile LONG *)&config->fatal_status, (LONG)status);
      write_ascii("worker.receive status="); write_u32(status);
      write_ascii(" worker="); write_u32(worker->index); write_ascii("\r\n");
      SetEvent(config->stop_event);
      break;
    }
    if (status == ERROR_INVALID_PARAMETER) {
      /* This API error describes a host parameter defect; treating it as a
         peer disconnect would silently shrink the fixed worker pool. */
      InterlockedExchange((volatile LONG *)&config->fatal_status, (LONG)status);
      write_ascii("worker.receive status="); write_u32(status);
      write_ascii(" worker="); write_u32(worker->index); write_ascii("\r\n");
      SetEvent(config->stop_event);
      break;
    }
    if (status == ERROR_MORE_DATA && request->RequestId != HTTP_NULL_ID) {
      (void)send_empty_status(worker, request, 431u, NULL, 0u);
      bytes_zero(worker->request_buffer, HOST_REQUEST_BUFFER_BYTES);
      continue;
    }
    if (WaitForSingleObject(config->stop_event, 0u) == WAIT_OBJECT_0) break;
    InterlockedExchange((volatile LONG *)&config->fatal_status, (LONG)status);
    write_ascii("worker.receive status="); write_u32(status);
    write_ascii(" worker="); write_u32(worker->index); write_ascii("\r\n");
    SetEvent(config->stop_event);
    break;
  }
#if CAMPFIRE_HTTP_STAGE_PROFILE
  host_stage_capture_cpu_end(worker);
#endif
  worker_release(worker);
  return 0u;
}

static int copy_wide(WCHAR *target, uint32_t capacity, const WCHAR *source) {
  uint32_t length = wlen(source);
  if (source == NULL || length + 1u > capacity) return 0;
  bytes_copy(target, source, (length + 1u) * sizeof(WCHAR));
  return 1;
}

static const WCHAR *find_argument(int count, WCHAR **arguments,
                                  const WCHAR *name) {
  for (int i = 1; i + 1 < count; ++i)
    if (lstrcmpiW(arguments[i], name) == 0) return arguments[i + 1];
  return NULL;
}

static int canonical_path(const WCHAR *input, WCHAR *output,
                          uint32_t capacity) {
  DWORD length;
  if (input == NULL || output == NULL || capacity == 0u) return 0;
  length = GetFullPathNameW(input, capacity, output, NULL);
  return length != 0u && length < capacity;
}

static int append_wide_ascii(WCHAR *target, uint32_t capacity,
                             uint32_t *used, const char *text) {
  uint32_t length = alen(text);
  if (*used > capacity || length > capacity - *used) return 0;
  for (uint32_t i = 0u; i < length; ++i) target[(*used)++] = (WCHAR)(uint8_t)text[i];
  return 1;
}

static int append_wide_u32(WCHAR *target, uint32_t capacity,
                           uint32_t *used, uint32_t value) {
  WCHAR digits[10];
  uint32_t count = 0u;
  do { digits[count++] = (WCHAR)(L'0' + value % 10u); value /= 10u; }
  while (value != 0u && count < 10u);
  if (*used > capacity || count > capacity - *used) return 0;
  while (count > 0u) target[(*used)++] = digits[--count];
  return 1;
}

static int read_secret_file(host_config *config, const WCHAR *path) {
  HANDLE file;
  LARGE_INTEGER size;
  DWORD read_bytes = 0u;
  if (path == NULL) return 0;
  file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING,
                     FILE_ATTRIBUTE_NORMAL, NULL);
  if (file == INVALID_HANDLE_VALUE) return 0;
  if (!GetFileSizeEx(file, &size) || size.QuadPart < 30 ||
      size.QuadPart > HOST_SECRET_BYTES) {
    CloseHandle(file);
    return 0;
  }
  if (!ReadFile(file, config->secret, (DWORD)size.QuadPart, &read_bytes, NULL) ||
      read_bytes != (DWORD)size.QuadPart) {
    CloseHandle(file);
    bytes_zero(config->secret, sizeof(config->secret));
    return 0;
  }
  CloseHandle(file);
  config->secret_bytes = read_bytes;
  return 1;
}

static int configure_arguments(host_config *config, int count,
                               WCHAR **arguments, WCHAR *secret_path,
                               uint32_t secret_path_capacity,
                               WCHAR *stop_event_name,
                               uint32_t stop_event_capacity) {
  const WCHAR *module = find_argument(count, arguments, L"--module");
  const WCHAR *manifest = find_argument(count, arguments, L"--manifest");
  const WCHAR *database = find_argument(count, arguments, L"--db");
  const WCHAR *secret = find_argument(count, arguments, L"--secret-file");
  const WCHAR *port_text = find_argument(count, arguments, L"--port");
  const WCHAR *workers_text = find_argument(count, arguments, L"--workers");
  const WCHAR *stop_name = find_argument(count, arguments, L"--stop-event");
  WCHAR canonical_database[32768];
  uint32_t host_used = 0u, authority_used = 0u, prefix_used = 0u;
  int file_exists = 0;
  HANDLE file;
  if (!canonical_path(module, config->module_path, 32768u) ||
      !canonical_path(manifest, config->manifest_path, 32768u) ||
      !canonical_path(database, canonical_database, 32768u) ||
      !canonical_path(secret, secret_path, secret_path_capacity)) return 0;
  if (port_text != NULL && !parse_u32(port_text, &config->port)) return 0;
  if (config->port == 0u || config->port > 65535u) return 0;
  config->worker_count = HOST_DEFAULT_WORKERS;
  if (workers_text != NULL && !parse_u32(workers_text, &config->worker_count)) return 0;
  if (config->worker_count == 0u || config->worker_count > HOST_WORKER_MAX) return 0;
  if (stop_name != NULL && !copy_wide(stop_event_name, stop_event_capacity, stop_name)) return 0;
  if (stop_name != NULL && !copy_wide(config->stop_event_name,
      (uint32_t)(sizeof(config->stop_event_name)/sizeof(config->stop_event_name[0])),
      stop_name)) return 0;
  file = CreateFileW(canonical_database, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                     NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
  if (file != INVALID_HANDLE_VALUE) { file_exists = 1; CloseHandle(file); }
  if (!file_exists || !read_secret_file(config, secret_path)) return 0;
  {
    int utf8_bytes = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS,
        canonical_database, -1, (LPSTR)config->database_path,
        sizeof(config->database_path), NULL, NULL);
    if (utf8_bytes <= 1 || (uint32_t)utf8_bytes > sizeof(config->database_path)) return 0;
    config->database_path_bytes = (uint32_t)utf8_bytes - 1u;
  }
  if (!append_wide_ascii(config->host_header, HOST_AUTHORITY_UNITS,
                         &host_used, "127.0.0.1:") ||
      !append_wide_u32(config->host_header, HOST_AUTHORITY_UNITS,
                       &host_used, config->port) ||
      !append_wide_ascii(config->authority, HOST_AUTHORITY_UNITS,
                         &authority_used, "http://") ||
      !append_wide_ascii(config->authority, HOST_AUTHORITY_UNITS,
                         &authority_used, "127.0.0.1:") ||
      !append_wide_u32(config->authority, HOST_AUTHORITY_UNITS,
                       &authority_used, config->port) ||
      !append_wide_ascii(config->prefix, HOST_AUTHORITY_UNITS + 2u,
                         &prefix_used, "http://") ||
      !append_wide_ascii(config->prefix, HOST_AUTHORITY_UNITS + 2u,
                         &prefix_used, "127.0.0.1:") ||
      !append_wide_u32(config->prefix, HOST_AUTHORITY_UNITS + 2u,
                       &prefix_used, config->port) ||
      !append_wide_ascii(config->prefix, HOST_AUTHORITY_UNITS + 2u,
                         &prefix_used, "/")) return 0;
  config->host_header[host_used] = 0;
  config->authority[authority_used] = 0;
  config->prefix[prefix_used] = 0;
  return 1;
}

static int http_start(host_config *config) {
  HTTPAPI_VERSION version = HTTPAPI_VERSION_2;
  ULONG status, queue_length = HOST_QUEUE_LENGTH, actual_queue_length = 0u;
  ULONG bytes_returned = 0u;
  HTTP_SERVER_SESSION_ID session = 0;
  HTTP_URL_GROUP_ID url_group = 0;
  HTTP_BINDING_INFO binding;
  HANDLE queue = NULL;
  int http_initialized = 0, queue_created = 0, session_created = 0;
  int group_created = 0, prefix_added = 0;
  uint32_t created_threads = 0u, result = 0u;
#if CAMPFIRE_HTTP_STAGE_PROFILE
  LARGE_INTEGER stage_frequency;
  if (!QueryPerformanceFrequency(&stage_frequency) ||
      stage_frequency.QuadPart <= 0) {
    write_ascii("startup.error=QueryPerformanceFrequency\r\n");
    return 0;
  }
  config->stage_qpc_frequency = (uint64_t)stage_frequency.QuadPart;
#endif
  status = HttpInitialize(version, HTTP_INITIALIZE_SERVER, NULL);
  if (status != NO_ERROR) { write_ascii("startup.api=HttpInitialize status="); write_u32(status); write_ascii("\r\n"); goto cleanup; }
  http_initialized = 1;
  status = HttpCreateRequestQueue(version, NULL, NULL, 0u, &queue);
  if (status != NO_ERROR) { write_ascii("startup.api=HttpCreateRequestQueue status="); write_u32(status); write_ascii("\r\n"); goto cleanup; }
  queue_created = 1; config->request_queue = queue;
  status = HttpSetRequestQueueProperty(queue, HttpServerQueueLengthProperty,
                                       &queue_length, sizeof(queue_length), 0u, NULL);
  if (status != NO_ERROR) { write_ascii("startup.api=HttpSetRequestQueueProperty status="); write_u32(status); write_ascii("\r\n"); goto cleanup; }
  status = HttpQueryRequestQueueProperty(queue, HttpServerQueueLengthProperty,
      &actual_queue_length, sizeof(actual_queue_length), 0u, &bytes_returned, NULL);
  if (status != NO_ERROR || bytes_returned != sizeof(actual_queue_length) ||
      actual_queue_length != HOST_QUEUE_LENGTH) {
    write_ascii("startup.api=HttpQueryRequestQueueProperty status="); write_u32(status); write_ascii("\r\n"); goto cleanup;
  }
  status = HttpCreateServerSession(version, &session, 0u);
  if (status != NO_ERROR) { write_ascii("startup.api=HttpCreateServerSession status="); write_u32(status); write_ascii("\r\n"); goto cleanup; }
  session_created = 1;
  status = HttpCreateUrlGroup(session, &url_group, 0u);
  if (status != NO_ERROR) { write_ascii("startup.api=HttpCreateUrlGroup status="); write_u32(status); write_ascii("\r\n"); goto cleanup; }
  group_created = 1;
  bytes_zero(&binding, sizeof(binding));
  binding.Flags.Present = 1;
  binding.RequestQueueHandle = queue;
  status = HttpSetUrlGroupProperty(url_group, HttpServerBindingProperty,
                                   &binding, sizeof(binding));
  if (status != NO_ERROR) { write_ascii("startup.api=HttpSetUrlGroupProperty status="); write_u32(status); write_ascii("\r\n"); goto cleanup; }
  config->stop_event = CreateEventW(NULL, TRUE, FALSE,
      config->stop_event_name[0] == 0 ? NULL : config->stop_event_name);
  config->workers_ready_event = CreateEventW(NULL, TRUE, FALSE, NULL);
  if (config->stop_event == NULL || config->workers_ready_event == NULL) goto cleanup;
  for (uint32_t i = 0u; i < config->worker_count; ++i) {
    worker_state *worker = &config->workers[i];
    worker->config = config; worker->index = i;
    worker->thread = CreateThread(NULL, 0u, worker_entry, worker, 0u, NULL);
    if (worker->thread == NULL) {
      InterlockedExchange((volatile LONG *)&config->fatal_status, 61L);
      SetEvent(config->workers_ready_event);
      SetEvent(config->stop_event);
      goto cleanup_workers;
    }
    ++created_threads;
  }
  if (WaitForSingleObject(config->workers_ready_event, 30000u) != WAIT_OBJECT_0 ||
      config->ready_workers != config->worker_count || config->fatal_status != 0u) {
    InterlockedExchange((volatile LONG *)&config->fatal_status,
        config->fatal_status == 0u ? 62L : (LONG)config->fatal_status);
    SetEvent(config->stop_event);
    goto cleanup_workers;
  }
  status = HttpAddUrlToUrlGroup(url_group, config->prefix, 0u, 0u);
  if (status != NO_ERROR) {
    write_ascii("startup.api=HttpAddUrlToUrlGroup prefix=");
    { char text[HOST_AUTHORITY_UNITS + 2u]; int bytes = WideCharToMultiByte(CP_UTF8, 0, config->prefix, -1, text, sizeof(text), NULL, NULL); if (bytes > 1) write_ascii(text); }
    write_ascii(" status="); write_u32(status); write_ascii("\r\n");
    goto cleanup_workers;
  }
  prefix_added = 1;
  write_ascii("READY prefix=");
  { char text[HOST_AUTHORITY_UNITS + 2u]; int bytes = WideCharToMultiByte(CP_UTF8, 0, config->prefix, -1, text, sizeof(text), NULL, NULL); if (bytes > 1) write_ascii(text); }
  write_ascii(" runtime_profile="); write_ascii(CAMPFIRE_HTTP_OWNING_RUNTIME_PROFILE_NAME);
  write_ascii(" workers="); write_u32(config->worker_count);
  write_ascii(" queue="); write_u32(HOST_QUEUE_LENGTH);
  write_ascii(" retained_cap="); write_u32(HOST_RETAINED_BYTES);
  write_ascii(" body_initial_commit="); write_u32(HOST_BODY_INITIAL_BYTES);
  write_ascii(" body_reserve="); write_u32(HOST_BODY_BYTES);
  write_ascii(" request_buffer_scrub_bytes_per_request="); write_u32(HOST_REQUEST_BUFFER_BYTES);
  write_ascii(" cache=per-response-nocache\r\n");
  WaitForSingleObject(config->stop_event, INFINITE);
  result = 1u;

cleanup_workers:
  if (prefix_added) {
    status = HttpRemoveUrlFromUrlGroup(url_group, config->prefix, 0u);
    if (status != NO_ERROR) { write_ascii("shutdown.api=HttpRemoveUrlFromUrlGroup status="); write_u32(status); write_ascii("\r\n"); result = 0u; }
  }
  if (queue_created) {
    status = HttpShutdownRequestQueue(queue);
    if (status != NO_ERROR && status != ERROR_INVALID_HANDLE) {
      write_ascii("shutdown.api=HttpShutdownRequestQueue status="); write_u32(status); write_ascii("\r\n"); result = 0u;
    }
  }
  for (uint32_t i = 0u; i < created_threads; ++i) {
    WaitForSingleObject(config->workers[i].thread, INFINITE);
    CloseHandle(config->workers[i].thread);
    config->workers[i].thread = NULL;
  }
  if (config->fatal_status != 0u) result = 0u;
#if CAMPFIRE_HTTP_STAGE_PROFILE
  if (result != 0u) write_stage_profile(config);
#endif
  if (config->workers_ready_event != NULL) { CloseHandle(config->workers_ready_event); config->workers_ready_event = NULL; }
  if (config->stop_event != NULL) { CloseHandle(config->stop_event); config->stop_event = NULL; }
cleanup:
  if (group_created) {
    status = HttpCloseUrlGroup(url_group);
    if (status != NO_ERROR) result = 0u;
  }
  if (session_created) {
    status = HttpCloseServerSession(session);
    if (status != NO_ERROR) result = 0u;
  }
  if (queue_created) {
    status = HttpCloseRequestQueue(queue);
    if (status != NO_ERROR) result = 0u;
    config->request_queue = NULL;
  }
  if (http_initialized) {
    status = HttpTerminate(HTTP_INITIALIZE_SERVER, NULL);
    if (status != NO_ERROR) result = 0u;
  }
  return result;
}

void WINAPI native_http_host_start(void) {
  int count = 0;
  WCHAR **arguments = CommandLineToArgvW(GetCommandLineW(), &count);
  WCHAR secret_path[32768];
  WCHAR stop_event_name[256];
  uint32_t error_tag = UINT32_MAX;
  uint32_t load_failure_stage = 0u;
  int exit_code = 2;
  bytes_zero(&g_host, sizeof(g_host));
  bytes_zero(secret_path, sizeof(secret_path));
  bytes_zero(stop_event_name, sizeof(stop_event_name));
  if (arguments == NULL || !configure_arguments(&g_host, count, arguments,
        secret_path, (uint32_t)(sizeof(secret_path)/sizeof(secret_path[0])),
        stop_event_name, (uint32_t)(sizeof(stop_event_name)/sizeof(stop_event_name[0])))) {
    write_ascii("startup.error=arguments-or-private-fixture\r\n");
    goto cleanup;
  }
  if (!load_entry_module(&g_host, &load_failure_stage)) {
    write_ascii("startup.error=artifact-pin-or-load stage=");
    write_ascii(load_failure_stage_name(load_failure_stage));
    write_ascii("\r\n");
    goto cleanup;
  }
  if (g_host.api.provision_wal(g_host.database_path,
      g_host.database_path_bytes, &error_tag) != 0 || error_tag != 0u) {
    write_ascii("startup.error=sqlite-wal-provision error_tag="); write_u32(error_tag); write_ascii("\r\n");
    goto cleanup;
  }
  if (http_start(&g_host)) exit_code = 0;
  write_ascii("shutdown.workers_cleaned="); write_u32(g_host.cleaned_workers);
  write_ascii(" shutdown.fatal_status="); write_u32(g_host.fatal_status);
  write_ascii("\r\n");
cleanup:
  if (g_host.module != NULL) { FreeLibrary(g_host.module); g_host.module = NULL; }
  if (g_host.sqlite_module != NULL) { FreeLibrary(g_host.sqlite_module); g_host.sqlite_module = NULL; }
  bytes_zero(g_host.secret, sizeof(g_host.secret));
  g_host.secret_bytes = 0u;
  if (arguments != NULL) LocalFree(arguments);
  ExitProcess((UINT)exit_code);
}

static uint32_t finalize_response_cookies(worker_state *worker,
    const HTTP_REQUEST *request, const al_campfire_http_render_metadata_v1 *metadata,
    const text_span encrypted_session, uint32_t *cookie_count) {
  uint32_t output_bytes = 0u;
  int32_t cookie_status;
  uint32_t has_session_mutation = metadata->remember_return_to != 0u ||
      metadata->alert.present != 0u || metadata->complete_login != 0u ||
      metadata->complete_logout != 0u;
  *cookie_count = 0u;
  worker->cookie_wire_bytes[0] = worker->cookie_wire_bytes[1] = worker->cookie_wire_bytes[2] = 0u;
  if (has_session_mutation) {
    uint32_t raw_cookie_bytes = 0u, return_bytes = 0u, alert_bytes = 0u;
    const uint8_t *return_url = NULL, *alert = NULL;
    worker->cookie_json_input_bytes = HOST_COOKIE_RAW_BYTES;
    if (!cookie_span_to_utf8(encrypted_session, worker->cookie_json_input,
                             HOST_COOKIE_RAW_BYTES, &raw_cookie_bytes)) return 0u;
    worker->cookie_json_input_bytes = raw_cookie_bytes;
    if (metadata->remember_return_to != 0u) {
      if (!make_return_url(worker, worker->config, request)) return 0u;
      return_url = worker->return_url;
      return_bytes = worker->return_url_bytes;
    }
    if (metadata->alert.present != 0u) {
      if (metadata->alert.data_utf16le == NULL ||
          metadata->alert.code_units > HOST_COOKIE_RAW_BYTES / 4u) return 0u;
      worker->cookie_raw_bytes[0] = HOST_COOKIE_RAW_BYTES;
      alert_bytes = u16_to_utf8((const uint16_t *)metadata->alert.data_utf16le,
          metadata->alert.code_units, worker->cookie_raw[0], HOST_COOKIE_RAW_BYTES);
      if (alert_bytes == UINT32_MAX) return 0u;
      worker->cookie_raw_bytes[0] = alert_bytes;
      alert = worker->cookie_raw[0];
    }
    worker->cookie_json_bytes = HOST_COOKIE_JSON_BYTES;
    cookie_status = al_campfire_cookie_response_finalize_session_v1(
        worker->response_context,
        encrypted_session.present ? worker->cookie_json_input : NULL,
        encrypted_session.present ? raw_cookie_bytes : 0u,
        metadata->remember_return_to, return_url, return_bytes,
        metadata->alert.present, alert, alert_bytes,
        worker->request_now_unix_microseconds, worker->cookie_json,
        HOST_COOKIE_JSON_BYTES, &output_bytes);
    if (cookie_status == 0 && output_bytes <= HOST_COOKIE_JSON_BYTES)
      worker->cookie_json_bytes = output_bytes;
    if (cookie_status != 0 || !make_cookie_header(worker, 0u,
          "_campfire_session", worker->cookie_json, output_bytes,
          0, 1, worker->request_now_unix_microseconds)) return 0u;
    *cookie_count = 1u;
    worker->response_cookie_indices[0] = 0u;
  }
  if (metadata->refresh_session_token.present != 0u) {
    uint32_t token_bytes;
    if (metadata->refresh_session_token.data_utf16le == NULL ||
        metadata->refresh_session_token.code_units > HOST_COOKIE_RAW_BYTES / 4u) return 0u;
    worker->cookie_raw_bytes[1] = HOST_COOKIE_RAW_BYTES;
    token_bytes = u16_to_utf8((const uint16_t *)metadata->refresh_session_token.data_utf16le,
        metadata->refresh_session_token.code_units, worker->cookie_raw[1],
        HOST_COOKIE_RAW_BYTES);
    if (token_bytes == UINT32_MAX) return 0u;
    worker->cookie_raw_bytes[1] = token_bytes;
    worker->cookie_json_bytes = HOST_COOKIE_JSON_BYTES;
    cookie_status = al_campfire_cookie_response_sign_session_token_v1(
        worker->response_context, worker->cookie_raw[1], token_bytes,
        worker->request_now_unix_microseconds, worker->cookie_json,
        HOST_COOKIE_JSON_BYTES, &output_bytes);
    if (cookie_status == 0 && output_bytes <= HOST_COOKIE_JSON_BYTES)
      worker->cookie_json_bytes = output_bytes;
    if (cookie_status != 0 || !make_cookie_header(worker, 1u, "session_token",
          worker->cookie_json, output_bytes, 1, 1,
          worker->request_now_unix_microseconds)) return 0u;
    worker->response_cookie_indices[*cookie_count] = 1u;
    ++*cookie_count;
  }
  if (metadata->has_last_room != 0u) {
    uint32_t last_room_bytes = 0u;
    worker->cookie_raw_bytes[2] = HOST_COOKIE_RAW_BYTES;
    if (!append_decimal_i64(worker->cookie_raw[2], HOST_COOKIE_RAW_BYTES,
          &last_room_bytes, metadata->last_room) ||
        !make_cookie_header(worker, 2u, "last_room", worker->cookie_raw[2],
          last_room_bytes, 1, 0, worker->request_now_unix_microseconds)) return 0u;
    worker->cookie_raw_bytes[2] = last_room_bytes;
    worker->response_cookie_indices[*cookie_count] = 2u;
    ++*cookie_count;
  }
  return 1u;
}

static uint32_t execute_and_send(worker_state *worker, const HTTP_REQUEST *request,
                                 uint32_t method, int64_t room_id) {
  uint32_t input_bytes = 0u, input_extent, body_required = 0u;
  uint32_t cookie_count = 0u, rendered_extent;
  int32_t execute_status;
  int32_t render_status;
  al_campfire_http_render_metadata_v1 metadata;
  const uint8_t *content_type = NULL, *location = NULL;
  uint32_t content_type_bytes = 0u, location_bytes = 0u;
  uint32_t preparation;
#if CAMPFIRE_HTTP_STAGE_PROFILE
  uint64_t stage_started;
#endif
#if CAMPFIRE_HTTP_STAGE_PROFILE
  stage_started = host_stage_qpc_now();
#endif
  preparation = make_entry_input(worker, request, method, room_id, &input_bytes);
#if CAMPFIRE_HTTP_STAGE_PROFILE
  host_stage_record(worker, HOST_STAGE_INPUT_PREPARE, stage_started);
#endif
  if (preparation != 1u) {
    uint32_t status = preparation == 2u ? 400u : preparation == 3u ? 431u : 500u;
    (void)send_response(worker, request->RequestId, status, NULL, 0u,
                        NULL, 0u, NULL, 0u, NULL, 0u, 0u, 0, 0u);
    return 0u;
  }
  input_extent = input_bytes;
#if CAMPFIRE_HTTP_STAGE_PROFILE
  stage_started = host_stage_qpc_now();
#endif
  execute_status = worker->config->api.execute(&worker->runtime,
        &worker->database_context, worker->input, input_bytes,
        &input_extent, 1u, worker->retained, HOST_RETAINED_BYTES);
#if CAMPFIRE_HTTP_STAGE_PROFILE
  host_stage_record(worker, HOST_STAGE_API_EXECUTE, stage_started);
#endif
  worker->active_retained_bytes = execute_status != 0 ||
      worker->runtime.status != AL_OWNING_STATUS_OK
      ? HOST_RETAINED_BYTES
      : worker->runtime.retained_copy_bytes > HOST_RETAINED_BYTES
          ? HOST_RETAINED_BYTES : (uint32_t)worker->runtime.retained_copy_bytes;
  if (execute_status != 0 || worker->runtime.status != AL_OWNING_STATUS_OK ||
      worker->runtime.retained_copy_bytes > HOST_RETAINED_BYTES ||
      worker->runtime.retained_copy_bytes > UINT32_MAX) {
    (void)send_response(worker, request->RequestId, 500u, NULL, 0u,
                        NULL, 0u, NULL, 0u, NULL, 0u, 0u, 0, 0u);
    return 0u;
  }
  rendered_extent = (uint32_t)worker->runtime.retained_copy_bytes;
  bytes_zero(&metadata, sizeof(metadata));
#if CAMPFIRE_HTTP_STAGE_PROFILE
  stage_started = host_stage_qpc_now();
#endif
  render_status = al_campfire_http_render_v1(
      AL_CAMPFIRE_HTTP_RENDERER_MANIFEST_FORMAT_VERSION,
      AL_CAMPFIRE_HTTP_RENDERER_LAYOUT_SCHEMA_VERSION,
      AL_CAMPFIRE_HTTP_RENDERER_LAYOUT_ABI_VERSION,
      worker->retained, rendered_extent, NULL, 0u, &body_required, &metadata);
#if CAMPFIRE_HTTP_STAGE_PROFILE
  host_stage_record(worker, HOST_STAGE_RENDERER, stage_started);
#endif
  if (render_status != AL_CAMPFIRE_HTTP_RENDER_OUTPUT_TOO_SMALL &&
      render_status != AL_CAMPFIRE_HTTP_RENDER_OK) goto internal_failure;
  if (body_required > HOST_BODY_BYTES || !ensure_body_committed(worker, body_required))
    goto internal_failure;
  worker->active_body_bytes = body_required;
#if CAMPFIRE_HTTP_STAGE_PROFILE
  stage_started = host_stage_qpc_now();
#endif
  render_status = al_campfire_http_render_v1(
      AL_CAMPFIRE_HTTP_RENDERER_MANIFEST_FORMAT_VERSION,
      AL_CAMPFIRE_HTTP_RENDERER_LAYOUT_SCHEMA_VERSION,
      AL_CAMPFIRE_HTTP_RENDERER_LAYOUT_ABI_VERSION,
      worker->retained, rendered_extent, worker->body,
      worker->body_committed_bytes, &body_required, &metadata);
#if CAMPFIRE_HTTP_STAGE_PROFILE
  host_stage_record(worker, HOST_STAGE_RENDERER, stage_started);
#endif
  if (render_status != AL_CAMPFIRE_HTTP_RENDER_OK) goto internal_failure;
#if CAMPFIRE_HTTP_STAGE_PROFILE
  stage_started = host_stage_qpc_now();
#endif
  if (!finalize_response_cookies(worker, request, &metadata,
        worker->request_encrypted_cookie, &cookie_count)) {
#if CAMPFIRE_HTTP_STAGE_PROFILE
    host_stage_record(worker, HOST_STAGE_COOKIE_FINALIZE, stage_started);
#endif
    /* Cookie state is borrowed from the request and must be retained until finalization. */
    goto internal_failure;
  }
#if CAMPFIRE_HTTP_STAGE_PROFILE
  host_stage_record(worker, HOST_STAGE_COOKIE_FINALIZE, stage_started);
#endif
  content_type = metadata.content_type_utf8;
  content_type_bytes = metadata.content_type_bytes;
  if (metadata.has_location != 0u) {
    location = metadata.location_utf8;
    location_bytes = metadata.location_bytes;
  }
  if (metadata.status < 100 || metadata.status > 599) goto internal_failure;
  (void)send_response(worker, request->RequestId, (uint32_t)metadata.status,
      content_type, content_type_bytes, location, location_bytes, NULL, 0u,
      worker->body, body_required, body_required,
      request->Verb != HttpVerbHEAD, cookie_count);
  worker->active_retained_bytes = rendered_extent;
  return 1u;

internal_failure:
  (void)send_response(worker, request->RequestId, 500u, NULL, 0u,
                      NULL, 0u, NULL, 0u, NULL, 0u, 0u, 0, 0u);
  worker->active_retained_bytes = worker->runtime.retained_copy_bytes > HOST_RETAINED_BYTES
      ? HOST_RETAINED_BYTES : (uint32_t)worker->runtime.retained_copy_bytes;
  worker->active_body_bytes = body_required;
  return 0u;
}


