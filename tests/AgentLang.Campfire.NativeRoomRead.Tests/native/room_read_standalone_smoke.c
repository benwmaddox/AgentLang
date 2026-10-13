#include <windows.h>
#include <shellapi.h>

#include <stdint.h>
#include <stddef.h>

#define AL_CAMPFIRE_EXPORT
#include "room_read_adapter.h"
#include "room_read_pure.h"
#include "room_read_test_fixture.h"

#define SMOKE_STACK_BYTES (4u * 1024u * 1024u)
#define SMOKE_RETAINED_BYTES (2u * 1024u * 1024u)
#define SMOKE_PURE_STORAGE_BYTES 131072u

enum {
  SMOKE_SUCCESS = 0,
  SMOKE_BAD_ARGUMENTS = 2,
  SMOKE_MODULE_LOAD_FAILED = 3,
  SMOKE_EXPORT_MISSING = 4,
  SMOKE_PURE_CONTEXT_CONTRACT_INVALID = 5,
  SMOKE_PURE_CONTEXT_INIT_FAILED = 6,
  SMOKE_DATABASE_PATH_INVALID = 7,
  SMOKE_WAL_PROVISION_FAILED = 8,
  SMOKE_PROVIDER_CONTEXT_INIT_FAILED = 9,
  SMOKE_ENTRY_CALL_FAILED = 10,
  SMOKE_ENTRY_STATUS_INVALID = 11,
  SMOKE_OUTPUT_EXTENT_INVALID = 12,
  SMOKE_OUTPUT_MISMATCH = 13,
  SMOKE_PROVIDER_CONTEXT_DESTROY_FAILED = 14,
  SMOKE_PROFILE_PAIRING_ACCEPTED = 15,
  SMOKE_INPUT_COUNT_ACCEPTED = 16,
  SMOKE_NULL_INPUT_ACCEPTED = 17,
  SMOKE_NULL_EXTENTS_ACCEPTED = 18,
  SMOKE_BAD_EXTENT_ACCEPTED = 19,
  SMOKE_NULL_PROVIDER_ACCEPTED = 20,
  SMOKE_INVALID_REFINEMENT_ACCEPTED = 21,
  SMOKE_RETAINED_CAPACITY_NOT_REJECTED = 22,
  SMOKE_RETAINED_CAPACITY_PARTIAL_WRITE = 23
};

typedef uint32_t(__cdecl *pure_size_fn)(void);
typedef uint32_t(__cdecl *pure_alignment_fn)(void);
typedef int32_t(__cdecl *pure_init_fn)(void *, uint32_t, const uint8_t *,
                                      uint32_t);
typedef void(__cdecl *pure_destroy_fn)(void *);
typedef int32_t(__cdecl *context_init_fn)(
    al_campfire_room_read_context_v1 *, const uint8_t *, uint32_t, void *,
    uint32_t *);
typedef int32_t(__cdecl *provision_wal_fn)(const uint8_t *, uint32_t,
                                           uint32_t *);
typedef int32_t(__cdecl *context_destroy_fn)(
    al_campfire_room_read_context_v1 *);
typedef int32_t(__cdecl *execute_fn)(
    al_owning_stack_context *, void *, const uint8_t *, uint32_t,
    const uint32_t *, uint32_t, uint8_t *, uint32_t);

#define LOAD_FUNCTION(destination, function_type, symbol_name)               \
  do {                                                                       \
    _Static_assert(sizeof(FARPROC) == sizeof(function_type),                 \
                   "Windows function pointer ABI mismatch");                \
    union {                                                                  \
      FARPROC generic;                                                       \
      function_type typed;                                                   \
    } resolved_symbol;                                                       \
    resolved_symbol.generic = GetProcAddress(module, symbol_name);          \
    if (resolved_symbol.generic == NULL) {                                   \
      result = SMOKE_EXPORT_MISSING;                                         \
      goto cleanup;                                                          \
    }                                                                        \
    destination = resolved_symbol.typed;                                     \
  } while (0)

__declspec(align(64)) static uint8_t pure_storage[SMOKE_PURE_STORAGE_BYTES];
__declspec(align(64)) static uint8_t stack_storage[SMOKE_STACK_BYTES];
__declspec(align(64)) static uint8_t init_bitmap[SMOKE_STACK_BYTES / 8u];
__declspec(align(64)) static uint8_t poison_bitmap[SMOKE_STACK_BYTES / 8u];
__declspec(align(64)) static uint8_t retained_storage[SMOKE_RETAINED_BYTES];
static al_campfire_room_read_context_v1 provider_context;
static al_owning_stack_context runtime_context;
static uint8_t database_path_utf8[AL_CAMPFIRE_ROOM_READ_PATH_LIMIT_BYTES + 1u];

static uint32_t utf8_path_length(const uint8_t *path) {
  uint32_t length = 0u;
  while (path[length] != '\0')
    ++length;
  return length;
}

static int bytes_equal(const uint8_t *left, const uint8_t *right,
                       uint32_t length) {
  uint32_t index;
  for (index = 0u; index < length; ++index) {
    if (left[index] != right[index])
      return 0;
  }
  return 1;
}

static void reset_runtime_context(int32_t runtime_profile,
                                  int invert_bitmap_shape) {
  volatile uint8_t *bytes = (volatile uint8_t *)&runtime_context;
  size_t index;
  int use_bitmaps = runtime_profile == 0;
  if (invert_bitmap_shape)
    use_bitmaps = !use_bitmaps;
  for (index = 0u; index < sizeof(runtime_context); ++index)
    bytes[index] = 0u;
  runtime_context.abi_version = AL_OWNING_STACK_ABI_VERSION;
  runtime_context.stack_capacity_bytes = SMOKE_STACK_BYTES;
  runtime_context.stack_data = stack_storage;
  if (use_bitmaps) {
    runtime_context.init_bitmap_bytes = SMOKE_STACK_BYTES / 8u;
    runtime_context.init_bitmap = init_bitmap;
    runtime_context.poison_bitmap = poison_bitmap;
  }
}

static int entry_rejected(execute_fn execute, int32_t runtime_profile,
                          int invert_bitmap_shape, void *host_context,
                          const uint8_t *input_bytes, uint32_t input_length,
                          const uint32_t *input_extents, uint32_t input_count,
                          uint8_t *retained, uint32_t retained_capacity) {
  int32_t result;
  reset_runtime_context(runtime_profile, invert_bitmap_shape);
  result = execute(&runtime_context, host_context, input_bytes, input_length,
                   input_extents, input_count, retained, retained_capacity);
  return result != 0 || runtime_context.status != AL_OWNING_STATUS_OK;
}

static void fill_bytes(uint8_t *destination, uint32_t length,
                       uint8_t value) {
  uint32_t index;
  for (index = 0u; index < length; ++index)
    destination[index] = value;
}

static int bytes_are(const uint8_t *bytes, uint32_t length, uint8_t value) {
  uint32_t index;
  for (index = 0u; index < length; ++index) {
    if (bytes[index] != value)
      return 0;
  }
  return 1;
}

static void write_i64_le(uint8_t *destination, int64_t value) {
  uint64_t bits = (uint64_t)value;
  uint32_t index;
  for (index = 0u; index < 8u; ++index)
    destination[index] = (uint8_t)(bits >> (index * 8u));
}

void room_read_smoke_start(void) {
  HMODULE module = NULL;
  LPWSTR *arguments = NULL;
  int argument_count = 0;
  pure_size_fn pure_size;
  pure_alignment_fn pure_alignment;
  pure_init_fn pure_init;
  pure_destroy_fn pure_destroy;
  provision_wal_fn provision_wal;
  context_init_fn context_init;
  context_destroy_fn context_destroy;
  execute_fn execute;
  uint32_t error_tag = UINT32_MAX;
  uint32_t input_extent = ROOM_READ_INPUT_BYTES;
  uint32_t path_bytes = 0u;
  uint32_t storage_bytes;
  uint32_t alignment;
  uint32_t written_bytes;
  int32_t runtime_profile = -1;
  int32_t call_status;
  uint8_t invalid_refinement_input[ROOM_READ_INPUT_BYTES];
  int converted_path_bytes;
  int pure_initialized = 0;
  int provider_initialized = 0;
  int result = SMOKE_MODULE_LOAD_FAILED;

  arguments = CommandLineToArgvW(GetCommandLineW(), &argument_count);
  if (arguments == NULL || argument_count != 4) {
    result = SMOKE_BAD_ARGUMENTS;
    goto cleanup;
  }
  if (arguments[3][0] == L'0' && arguments[3][1] == L'\0')
    runtime_profile = 0;
  else if (arguments[3][0] == L'1' && arguments[3][1] == L'\0')
    runtime_profile = 1;
  else {
    result = SMOKE_BAD_ARGUMENTS;
    goto cleanup;
  }
  module = LoadLibraryExW(arguments[1], NULL,
                          LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR |
                              LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
  if (module == NULL)
    goto cleanup;

  LOAD_FUNCTION(pure_size, pure_size_fn, "al_campfire_pure_context_size_v1");
  LOAD_FUNCTION(pure_alignment, pure_alignment_fn,
                "al_campfire_pure_context_alignment_v1");
  LOAD_FUNCTION(pure_init, pure_init_fn, "al_campfire_pure_context_init_v1");
  LOAD_FUNCTION(pure_destroy, pure_destroy_fn,
                "al_campfire_pure_context_destroy_v1");
  LOAD_FUNCTION(context_init, context_init_fn,
                "al_campfire_room_read_context_init_v1");
  LOAD_FUNCTION(provision_wal, provision_wal_fn,
                "al_campfire_room_read_provision_wal_v1");
  LOAD_FUNCTION(context_destroy, context_destroy_fn,
                "al_campfire_room_read_context_destroy_v1");
  LOAD_FUNCTION(execute, execute_fn,
                "agentlang_owning_execute_with_host_context");
  if (pure_size == NULL || pure_alignment == NULL || pure_init == NULL ||
      pure_destroy == NULL || context_init == NULL || provision_wal == NULL ||
      context_destroy == NULL || execute == NULL)
    goto cleanup;

  storage_bytes = pure_size();
  alignment = pure_alignment();
  if (storage_bytes == 0u || storage_bytes > SMOKE_PURE_STORAGE_BYTES ||
      alignment == 0u || alignment > 64u || (alignment & (alignment - 1u)) != 0u)
  {
    result = SMOKE_PURE_CONTEXT_CONTRACT_INVALID;
    goto cleanup;
  }
  if (pure_init(pure_storage, storage_bytes, room_read_secret,
                ROOM_READ_SECRET_BYTES) != 0) {
    result = SMOKE_PURE_CONTEXT_INIT_FAILED;
    goto cleanup;
  }
  pure_initialized = 1;

  converted_path_bytes = WideCharToMultiByte(
      CP_UTF8, WC_ERR_INVALID_CHARS, arguments[2], -1,
      (char *)database_path_utf8, (int)sizeof(database_path_utf8), NULL, NULL);
  if (converted_path_bytes <= 1 ||
      (uint32_t)converted_path_bytes > sizeof(database_path_utf8)) {
    result = SMOKE_DATABASE_PATH_INVALID;
    goto cleanup;
  }
  path_bytes = utf8_path_length(database_path_utf8);
  if (provision_wal(database_path_utf8, path_bytes, &error_tag) != 0 ||
      error_tag != 0u) {
    result = SMOKE_WAL_PROVISION_FAILED;
    goto cleanup;
  }
  if (context_init(&provider_context, database_path_utf8, path_bytes,
                   pure_storage, &error_tag) != 0 ||
      error_tag != 0u) {
    result = SMOKE_PROVIDER_CONTEXT_INIT_FAILED;
    goto cleanup;
  }
  provider_initialized = 1;

  reset_runtime_context(runtime_profile, 0);
  call_status = execute(&runtime_context, &provider_context, room_read_input,
                        ROOM_READ_INPUT_BYTES, &input_extent, 1u,
                        retained_storage, SMOKE_RETAINED_BYTES);
  if (call_status != 0) {
    result = SMOKE_ENTRY_CALL_FAILED;
    goto cleanup;
  }
  if (runtime_context.status != AL_OWNING_STATUS_OK) {
    result = SMOKE_ENTRY_STATUS_INVALID;
    goto cleanup;
  }
  written_bytes = (uint32_t)runtime_context.retained_copy_bytes;
  if (written_bytes != ROOM_READ_EXPECTED_BYTES) {
    result = SMOKE_OUTPUT_EXTENT_INVALID;
    goto cleanup;
  }
  if (!bytes_equal(retained_storage, room_read_expected, written_bytes)) {
    result = SMOKE_OUTPUT_MISMATCH;
    goto cleanup;
  }

  /* A runtime module must reject the context layout for the other profile.
   * Diagnostic requires both bitmaps; TrustedGenerated requires neither. */
  if (!entry_rejected(execute, runtime_profile, 1, &provider_context,
                      room_read_input, ROOM_READ_INPUT_BYTES, &input_extent,
                      1u, retained_storage, SMOKE_RETAINED_BYTES)) {
    result = SMOKE_PROFILE_PAIRING_ACCEPTED;
    goto cleanup;
  }
  if (!entry_rejected(execute, runtime_profile, 0, &provider_context,
                      room_read_input, ROOM_READ_INPUT_BYTES, &input_extent,
                      0u, retained_storage, SMOKE_RETAINED_BYTES)) {
    result = SMOKE_INPUT_COUNT_ACCEPTED;
    goto cleanup;
  }
  if (!entry_rejected(execute, runtime_profile, 0, &provider_context, NULL,
                      ROOM_READ_INPUT_BYTES, &input_extent, 1u,
                      retained_storage, SMOKE_RETAINED_BYTES)) {
    result = SMOKE_NULL_INPUT_ACCEPTED;
    goto cleanup;
  }
  if (!entry_rejected(execute, runtime_profile, 0, &provider_context,
                      room_read_input, ROOM_READ_INPUT_BYTES, NULL, 1u,
                      retained_storage, SMOKE_RETAINED_BYTES)) {
    result = SMOKE_NULL_EXTENTS_ACCEPTED;
    goto cleanup;
  }
  input_extent = ROOM_READ_INPUT_BYTES - 1u;
  if (!entry_rejected(execute, runtime_profile, 0, &provider_context,
                      room_read_input, ROOM_READ_INPUT_BYTES, &input_extent,
                      1u, retained_storage, SMOKE_RETAINED_BYTES)) {
    result = SMOKE_BAD_EXTENT_ACCEPTED;
    goto cleanup;
  }
  input_extent = ROOM_READ_INPUT_BYTES;
  if (!entry_rejected(execute, runtime_profile, 0, NULL, room_read_input,
                      ROOM_READ_INPUT_BYTES, &input_extent, 1u,
                      retained_storage, SMOKE_RETAINED_BYTES)) {
    result = SMOKE_NULL_PROVIDER_ACCEPTED;
    goto cleanup;
  }

  {
    uint32_t index;
    for (index = 0u; index < ROOM_READ_INPUT_BYTES; ++index)
      invalid_refinement_input[index] = room_read_input[index];
  }
  write_i64_le(invalid_refinement_input + ROOM_READ_NOW_OFFSET_BYTES,
               INT64_MAX);
  reset_runtime_context(runtime_profile, 0);
  call_status = execute(&runtime_context, &provider_context,
                        invalid_refinement_input, ROOM_READ_INPUT_BYTES,
                        &input_extent, 1u, retained_storage,
                        SMOKE_RETAINED_BYTES);
  if ((call_status == 0 &&
       runtime_context.status == AL_OWNING_STATUS_OK) ||
      runtime_context.status != AL_OWNING_STATUS_DIAGNOSTIC) {
    result = SMOKE_INVALID_REFINEMENT_ACCEPTED;
    goto cleanup;
  }

  fill_bytes(retained_storage, ROOM_READ_EXPECTED_BYTES, 0xa5u);
  reset_runtime_context(runtime_profile, 0);
  call_status = execute(&runtime_context, &provider_context, room_read_input,
                        ROOM_READ_INPUT_BYTES, &input_extent, 1u,
                        retained_storage, ROOM_READ_EXPECTED_BYTES - 1u);
  if ((call_status == 0 &&
       runtime_context.status == AL_OWNING_STATUS_OK) ||
      runtime_context.status != AL_OWNING_STATUS_RETAINED_CAPACITY) {
    result = SMOKE_RETAINED_CAPACITY_NOT_REJECTED;
    goto cleanup;
  }
  if (!bytes_are(retained_storage, ROOM_READ_EXPECTED_BYTES, 0xa5u)) {
    result = SMOKE_RETAINED_CAPACITY_PARTIAL_WRITE;
    goto cleanup;
  }
  result = SMOKE_SUCCESS;

cleanup:
  if (provider_initialized && context_destroy(&provider_context) != 0)
    result = SMOKE_PROVIDER_CONTEXT_DESTROY_FAILED;
  if (pure_initialized)
    pure_destroy(pure_storage);
  if (module != NULL)
    FreeLibrary(module);
  if (arguments != NULL)
    LocalFree(arguments);
  ExitProcess((UINT)result);
}

#undef LOAD_FUNCTION
