#include "owning_stack_runtime.h"
#include "campfire_room_host_abi.h"
#define AL_CAMPFIRE_EXPORT __declspec(dllimport)
#include "room_sqlite_adapter.h"
#include "sqlite_subset.h"

#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <windows.h>

#define SQLITE_OPEN_CREATE 0x00000004
#define OUTPUT_CAPACITY 65536u
#define ENTRY_STACK_CAPACITY 16384u
#define ENTRY_BITMAP_CAPACITY (ENTRY_STACK_CAPACITY / 8u)
#define ENTRY_RETAINED_CAPACITY 4096u

__declspec(dllimport) int32_t agentlang_owning_execute_with_host_context(
    al_owning_stack_context *context, void *provider_context,
    const uint8_t *input, uint32_t input_bytes,
    const uint32_t *input_extents, uint32_t input_count, uint8_t *retained,
    uint32_t retained_capacity_bytes);

#define REQUIRE(condition)                                                     \
  do {                                                                         \
    if (!(condition)) {                                                        \
      fprintf(stderr, "room SQLite smoke failed at line %d\n", __LINE__);     \
      return __LINE__;                                                         \
    }                                                                          \
  } while (0)

typedef struct wrong_thread_call {
  al_campfire_room_sqlite_context_v1 *context;
  uint8_t output[OUTPUT_CAPACITY];
  uint32_t extent;
  int32_t lookup_status;
  int32_t destroy_status;
} wrong_thread_call;

static uint64_t read_u64_le(const uint8_t *bytes, uint32_t offset) {
  uint32_t index;
  uint64_t value = 0u;
  for (index = 0u; index < 8u; ++index)
    value |= (uint64_t)bytes[offset + index] << (index * 8u);
  return value;
}

static uint16_t read_u16_le(const uint8_t *bytes, uint32_t offset) {
  return (uint16_t)((uint16_t)bytes[offset] |
                    (uint16_t)((uint16_t)bytes[offset + 1u] << 8u));
}

static uint32_t padded_string_extent(uint32_t code_units) {
  return (8u + code_units * 2u + 7u) & ~7u;
}

static int read_ascii_string(const uint8_t *output, uint32_t capacity,
                             uint32_t *cursor, const char *expected) {
  uint32_t index;
  uint32_t length = (uint32_t)strlen(expected);
  uint32_t extent;
  if (*cursor > capacity || capacity - *cursor < 8u ||
      read_u64_le(output, *cursor) != (uint64_t)length)
    return 0;
  extent = padded_string_extent(length);
  if (extent > capacity - *cursor)
    return 0;
  for (index = 0u; index < length; ++index) {
    if (read_u16_le(output, *cursor + 8u + index * 2u) !=
        (uint16_t)(uint8_t)expected[index])
      return 0;
  }
  *cursor += extent;
  return 1;
}

static int read_utf16_string(const uint8_t *output, uint32_t capacity,
                             uint32_t *cursor, const uint16_t *expected,
                             uint32_t expected_length) {
  uint32_t index;
  uint32_t extent;
  if (*cursor > capacity || capacity - *cursor < 8u ||
      read_u64_le(output, *cursor) != (uint64_t)expected_length)
    return 0;
  extent = padded_string_extent(expected_length);
  if (extent > capacity - *cursor)
    return 0;
  for (index = 0u; index < expected_length; ++index) {
    if (read_u16_le(output, *cursor + 8u + index * 2u) != expected[index])
      return 0;
  }
  *cursor += extent;
  return 1;
}

static int expect_room(const uint8_t *output, uint32_t capacity,
                       uint32_t extent, int64_t room_id, uint32_t name_tag,
                       const char *ascii_name, const uint16_t *utf16_name,
                       uint32_t utf16_name_length, uint32_t kind_tag,
                       int64_t creator_id, const char *created_at,
                       const char *updated_at) {
  uint32_t cursor = 0u;
  if (read_u64_le(output, cursor) != 0u)
    return 0;
  cursor += 8u; /* Result.Ok */
  if (read_u64_le(output, cursor) != 0u)
    return 0;
  cursor += 8u; /* Option.Some */
  if (read_u64_le(output, cursor) != (uint64_t)room_id)
    return 0;
  cursor += 8u;
  if (read_u64_le(output, cursor) != (uint64_t)name_tag)
    return 0;
  cursor += 8u;
  if (name_tag == 0u) {
    int valid_name = ascii_name != NULL
                         ? read_ascii_string(output, capacity, &cursor,
                                             ascii_name)
                         : read_utf16_string(output, capacity, &cursor,
                                             utf16_name,
                                             utf16_name_length);
    if (!valid_name)
      return 0;
  } else if (name_tag != 1u || ascii_name != NULL || utf16_name != NULL) {
    return 0;
  }
  if (cursor > capacity || capacity - cursor < 16u ||
      read_u64_le(output, cursor) != (uint64_t)kind_tag)
    return 0;
  cursor += 8u;
  if (read_u64_le(output, cursor) != (uint64_t)creator_id)
    return 0;
  cursor += 8u;
  if (!read_ascii_string(output, capacity, &cursor, created_at) ||
      !read_ascii_string(output, capacity, &cursor, updated_at))
    return 0;
  return cursor == extent;
}

static int expect_none(const uint8_t *output, uint32_t extent) {
  return extent == 16u && read_u64_le(output, 0u) == 0u &&
         read_u64_le(output, 8u) == 1u;
}

static int expect_unavailable(const uint8_t *output, uint32_t extent) {
  return extent == 16u && read_u64_le(output, 0u) == 1u &&
         read_u64_le(output, 8u) == AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;
}

static int run_compiled_entry(
    al_campfire_room_sqlite_context_v1 *provider_context) {
  static al_owning_stack_context runtime_context;
  static uint8_t stack_storage[ENTRY_STACK_CAPACITY];
  static uint8_t init_bitmap[ENTRY_BITMAP_CAPACITY];
  static uint8_t poison_bitmap[ENTRY_BITMAP_CAPACITY];
  static const int64_t input_values[2] = {7, 101};
  static const uint32_t input_extents[2] = {8u, 8u};
  static uint8_t retained[ENTRY_RETAINED_CAPACITY];
  uint64_t written_bytes;
  int32_t status;

  runtime_context.abi_version = AL_OWNING_STACK_ABI_VERSION;
  runtime_context.stack_capacity_bytes = ENTRY_STACK_CAPACITY;
  runtime_context.init_bitmap_bytes = ENTRY_BITMAP_CAPACITY;
  runtime_context.stack_data = stack_storage;
  runtime_context.init_bitmap = init_bitmap;
  runtime_context.poison_bitmap = poison_bitmap;
  status = agentlang_owning_execute_with_host_context(
      &runtime_context, provider_context, (const uint8_t *)input_values,
      (uint32_t)sizeof(input_values), input_extents, 2u, retained,
      ENTRY_RETAINED_CAPACITY);
  written_bytes = runtime_context.retained_copy_bytes;
  if (status != 0 || runtime_context.status != AL_OWNING_STATUS_OK ||
      written_bytes > ENTRY_RETAINED_CAPACITY)
    return 0;
  return expect_room(retained, ENTRY_RETAINED_CAPACITY,
                     (uint32_t)written_bytes, 101, 0u, "General", NULL, 0u,
                     0u, 7,
                     "2026-06-07 18:19:20.123456-04:00",
                     "2026-06-07T22:19:21.654321+00:00");
}

static int execute_sql(sqlite3 *database, const char *sql) {
  const char *cursor = sql;
  while (cursor != NULL && *cursor != '\0') {
    sqlite3_stmt *statement = NULL;
    const char *tail = NULL;
    int rc = sqlite3_prepare_v2(database, cursor, -1, &statement, &tail);
    if (rc != SQLITE_OK || tail == NULL || tail == cursor) {
      if (statement != NULL)
        (void)sqlite3_finalize(statement);
      return 0;
    }
    if (statement != NULL) {
      rc = sqlite3_step(statement);
      if (rc != SQLITE_DONE) {
        (void)sqlite3_finalize(statement);
        return 0;
      }
      rc = sqlite3_finalize(statement);
      if (rc != SQLITE_OK)
        return 0;
    }
    cursor = tail;
  }
  return 1;
}

static int row_count(sqlite3 *database, const char *sql, int64_t expected) {
  sqlite3_stmt *statement = NULL;
  int ok = 0;
  if (sqlite3_prepare_v2(database, sql, -1, &statement, NULL) != SQLITE_OK ||
      statement == NULL)
    return 0;
  if (sqlite3_step(statement) == SQLITE_ROW &&
      sqlite3_column_int64(statement, 0) == expected)
    ok = 1;
  (void)sqlite3_finalize(statement);
  return ok;
}

static DWORD WINAPI wrong_thread_proc(void *parameter) {
  wrong_thread_call *call = (wrong_thread_call *)parameter;
  call->extent = UINT32_MAX;
  call->lookup_status = al_host_campfire_rooms_find_for_user_v1(
      call->context, 7, 101, call->output, (uint32_t)sizeof(call->output),
      &call->extent);
  call->destroy_status =
      al_campfire_room_sqlite_context_destroy_v1(call->context);
  return 0u;
}

int main(int argc, char **argv) {
  static const uint16_t unicode_name[] = {0x03bbu, 0xd83du, 0xde80u,
                                          0x0020u, 0x6771u, 0x4eacu};
  al_campfire_room_sqlite_context_v1 context = {0};
  al_campfire_room_sqlite_context_v1 second_context = {0};
  al_campfire_room_sqlite_context_v1 missing_context = {0};
  sqlite3 *setup_database = NULL;
  uint8_t output[OUTPUT_CAPACITY];
  uint32_t extent = 0u;
  uint32_t error_tag = UINT32_MAX;
  char missing_path[4096];
  DWORD attributes;
  HANDLE wrong_thread;
  wrong_thread_call wrong_thread_result;
  sqlite3_stmt *stable_statement;
  int status;

  REQUIRE(argc == 2 && argv[1] != NULL && argv[1][0] != '\0');
  REQUIRE(sizeof(void *) == 8u);
  REQUIRE(sizeof(context) == AL_CAMPFIRE_ROOM_SQLITE_CONTEXT_BYTES);
  REQUIRE((uintptr_t)&context % 8u == 0u);

  (void)DeleteFileA(argv[1]);
  REQUIRE(sqlite3_open_v2(argv[1], &setup_database,
                          SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE |
                              SQLITE_OPEN_PRIVATECACHE,
                          NULL) == SQLITE_OK);
  REQUIRE(execute_sql(setup_database,
                      "CREATE TABLE rooms (id INTEGER PRIMARY KEY, name TEXT "
                      "NULL, type TEXT NOT NULL, creator_id INTEGER NULL, "
                      "created_at TEXT NOT NULL, updated_at TEXT NOT NULL)"));
  REQUIRE(execute_sql(setup_database,
                      "CREATE TABLE memberships (user_id INTEGER NOT NULL, "
                      "room_id INTEGER NOT NULL, PRIMARY KEY (user_id, "
                      "room_id))"));
  REQUIRE(execute_sql(setup_database,
                      "INSERT INTO rooms VALUES (101, 'General', "
                      "'Rooms::Open', 7, '2026-06-07 "
                      "18:19:20.123456-04:00', '2026-06-07T22:19:21.654321+00:00')"));
  REQUIRE(execute_sql(setup_database,
                      "INSERT INTO rooms VALUES (102, NULL, 'Rooms::Closed', "
                      "7, '2026-06-08 08:00:00', "
                      "'2026-06-08 08:01:02.000007Z')"));
  REQUIRE(execute_sql(setup_database,
                      "INSERT INTO rooms VALUES (103, CAST(X'CEBBF09F9A8020E69DB1E4BAAC' "
                      "AS TEXT), 'Rooms::Direct', -2, "
                      "'2026-06-09T14:15:16.987654+05:45', "
                      "'2026-06-09 08:30:17.000001 UTC')"));
  REQUIRE(execute_sql(setup_database,
                      "INSERT INTO memberships VALUES (7, 101)"));
  REQUIRE(execute_sql(setup_database,
                      "INSERT INTO memberships VALUES (7, 102)"));
  REQUIRE(execute_sql(setup_database,
                      "INSERT INTO memberships VALUES (8, 103)"));
  REQUIRE(sqlite3_close(setup_database) == SQLITE_OK);
  setup_database = NULL;

  (void)snprintf(missing_path, sizeof(missing_path), "%s.missing", argv[1]);
  (void)DeleteFileA(missing_path);
  attributes = GetFileAttributesA(missing_path);
  REQUIRE(attributes == INVALID_FILE_ATTRIBUTES);
  status = al_campfire_room_sqlite_context_init_v1(
      &missing_context, (const uint8_t *)missing_path,
      (uint32_t)strlen(missing_path), &error_tag);
  REQUIRE(status != 0 && error_tag == AL_CAMPFIRE_DB_ERROR_UNAVAILABLE);
  REQUIRE(GetFileAttributesA(missing_path) == INVALID_FILE_ATTRIBUTES);

  error_tag = UINT32_MAX;
  REQUIRE(al_campfire_room_sqlite_context_init_v1(
              &context, (const uint8_t *)argv[1], (uint32_t)strlen(argv[1]),
              &error_tag) == 0);
  REQUIRE(error_tag == AL_CAMPFIRE_DB_ERROR_UNAVAILABLE);
  REQUIRE(context.opaque_db != NULL && context.opaque_find_stmt != NULL &&
          context.cookie != 0u && context.owner_thread_id != 0u &&
          context.active == 0u);
  stable_statement = (sqlite3_stmt *)context.opaque_find_stmt;

  REQUIRE(al_campfire_room_sqlite_context_init_v1(
              &second_context, (const uint8_t *)argv[1],
              (uint32_t)strlen(argv[1]), &error_tag) == 0);
  REQUIRE(second_context.opaque_db != context.opaque_db &&
          second_context.opaque_find_stmt != context.opaque_find_stmt);
  memset(output, 0, sizeof(output));
  REQUIRE(al_host_campfire_rooms_find_for_user_v1(
              &second_context, 7, 102, output, OUTPUT_CAPACITY, &extent) == 0);
  REQUIRE(expect_room(output, OUTPUT_CAPACITY, extent, 102, 1u, NULL, NULL,
                      0u, 1u, 7, "2026-06-08 08:00:00",
                      "2026-06-08 08:01:02.000007Z"));
  REQUIRE(second_context.active == 0u);
  REQUIRE(al_campfire_room_sqlite_context_destroy_v1(&second_context) == 0);

  memset(&wrong_thread_result, 0, sizeof(wrong_thread_result));
  wrong_thread_result.context = &context;
  wrong_thread = CreateThread(NULL, 0u, wrong_thread_proc, &wrong_thread_result,
                              0u, NULL);
  REQUIRE(wrong_thread != NULL);
  REQUIRE(WaitForSingleObject(wrong_thread, INFINITE) == WAIT_OBJECT_0);
  REQUIRE(CloseHandle(wrong_thread) != 0);
  REQUIRE(wrong_thread_result.lookup_status != 0 &&
          wrong_thread_result.extent == 0u);
  REQUIRE(wrong_thread_result.destroy_status != 0 &&
          context.opaque_db != NULL && context.opaque_find_stmt == stable_statement);

  extent = UINT32_MAX;
  REQUIRE(al_host_campfire_rooms_find_for_user_v1(
              &context, 7, 101, output, 1u, &extent) != 0);
  REQUIRE(extent == 0u && context.active == 0u &&
          context.opaque_find_stmt == stable_statement);

  memset(output, 0, sizeof(output));
  REQUIRE(al_host_campfire_rooms_find_for_user_v1(
              &context, 7, 101, output, OUTPUT_CAPACITY, &extent) == 0);
  REQUIRE(expect_room(output, OUTPUT_CAPACITY, extent, 101, 0u, "General",
                      NULL, 0u, 0u, 7,
                      "2026-06-07 18:19:20.123456-04:00",
                      "2026-06-07T22:19:21.654321+00:00"));
  REQUIRE(context.opaque_find_stmt == stable_statement && context.active == 0u);
  REQUIRE(run_compiled_entry(&context));
  REQUIRE(context.opaque_find_stmt == stable_statement && context.active == 0u);

  REQUIRE(al_host_campfire_rooms_find_for_user_v1(
              &context, 8, 103, output, OUTPUT_CAPACITY, &extent) == 0);
  REQUIRE(expect_room(output, OUTPUT_CAPACITY, extent, 103, 0u, NULL,
                      unicode_name, (uint32_t)(sizeof(unicode_name) /
                                               sizeof(unicode_name[0])),
                      2u, -2, "2026-06-09T14:15:16.987654+05:45",
                      "2026-06-09 08:30:17.000001 UTC"));
  REQUIRE(context.opaque_find_stmt == stable_statement);

  REQUIRE(al_host_campfire_rooms_find_for_user_v1(
              &context, 7, 103, output, OUTPUT_CAPACITY, &extent) == 0);
  REQUIRE(expect_none(output, extent));
  REQUIRE(al_host_campfire_rooms_find_for_user_v1(
              &context, 8, 101, output, OUTPUT_CAPACITY, &extent) == 0);
  REQUIRE(expect_none(output, extent));
  REQUIRE(al_host_campfire_rooms_find_for_user_v1(
              &context, 7, 999, output, OUTPUT_CAPACITY, &extent) == 0);
  REQUIRE(expect_none(output, extent));

  REQUIRE(sqlite3_open_v2(argv[1], &setup_database,
                          SQLITE_OPEN_READWRITE | SQLITE_OPEN_PRIVATECACHE,
                          NULL) == SQLITE_OK);
  REQUIRE(execute_sql(setup_database,
                      "UPDATE rooms SET type = 'Rooms::Hidden' WHERE id = 101"));
  REQUIRE(al_host_campfire_rooms_find_for_user_v1(
              &context, 7, 101, output, OUTPUT_CAPACITY, &extent) == 0);
  REQUIRE(expect_unavailable(output, extent));
  REQUIRE(execute_sql(setup_database,
                      "UPDATE rooms SET type = 'Rooms::Open' WHERE id = 101"));
  REQUIRE(execute_sql(setup_database,
                      "UPDATE rooms SET created_at = 'not-a-timestamp' WHERE "
                      "id = 101"));
  REQUIRE(al_host_campfire_rooms_find_for_user_v1(
              &context, 7, 101, output, OUTPUT_CAPACITY, &extent) == 0);
  REQUIRE(expect_unavailable(output, extent));
  REQUIRE(execute_sql(setup_database,
                      "UPDATE rooms SET created_at = '2026-06-07 "
                      "18:19:20.123456-04:00' WHERE id = 101"));
  REQUIRE(execute_sql(setup_database,
                      "UPDATE rooms SET creator_id = NULL WHERE id = 101"));
  REQUIRE(al_host_campfire_rooms_find_for_user_v1(
              &context, 7, 101, output, OUTPUT_CAPACITY, &extent) == 0);
  REQUIRE(expect_unavailable(output, extent));
  REQUIRE(execute_sql(setup_database,
                      "UPDATE rooms SET creator_id = 7 WHERE id = 101"));
  REQUIRE(execute_sql(setup_database, "DROP TABLE memberships"));
  REQUIRE(al_host_campfire_rooms_find_for_user_v1(
              &context, 7, 101, output, OUTPUT_CAPACITY, &extent) == 0);
  REQUIRE(expect_unavailable(output, extent));
  REQUIRE(execute_sql(setup_database,
                      "CREATE TABLE memberships (user_id INTEGER NOT NULL, "
                      "room_id INTEGER NOT NULL, PRIMARY KEY (user_id, "
                      "room_id))"));
  REQUIRE(execute_sql(setup_database,
                      "INSERT INTO memberships VALUES (7, 101)"));
  REQUIRE(execute_sql(setup_database,
                      "INSERT INTO memberships VALUES (7, 102)"));
  REQUIRE(execute_sql(setup_database,
                      "INSERT INTO memberships VALUES (8, 103)"));
  /* A recovered SQL error leaves the worker live; destruction is a valid
   * owning-thread operation, so do not destroy it before the recovery query. */
  REQUIRE(context.cookie != 0u && context.opaque_db != NULL);
  REQUIRE(context.opaque_find_stmt == stable_statement && context.active == 0u);
  REQUIRE(al_host_campfire_rooms_find_for_user_v1(
              &context, 7, 101, output, OUTPUT_CAPACITY, &extent) == 0);
  REQUIRE(expect_room(output, OUTPUT_CAPACITY, extent, 101, 0u, "General",
                      NULL, 0u, 0u, 7,
                      "2026-06-07 18:19:20.123456-04:00",
                      "2026-06-07T22:19:21.654321+00:00"));
  REQUIRE(sqlite3_close(setup_database) == SQLITE_OK);
  setup_database = NULL;

  REQUIRE(sqlite3_open_v2(argv[1], &setup_database,
                          SQLITE_OPEN_READWRITE | SQLITE_OPEN_PRIVATECACHE,
                          NULL) == SQLITE_OK);
  REQUIRE(row_count(setup_database, "SELECT count(*) FROM rooms", 3));
  REQUIRE(row_count(setup_database, "SELECT count(*) FROM memberships", 3));
  REQUIRE(sqlite3_close(setup_database) == SQLITE_OK);
  setup_database = NULL;

  REQUIRE(al_campfire_room_sqlite_context_destroy_v1(&context) == 0);
  REQUIRE(context.opaque_db == NULL && context.opaque_find_stmt == NULL &&
          context.cookie == 0u && context.owner_thread_id == 0u &&
          context.active == 0u);
  REQUIRE(al_campfire_room_sqlite_context_init_v1(
              &context, (const uint8_t *)argv[1], (uint32_t)strlen(argv[1]),
              &error_tag) == 0);
  REQUIRE(al_host_campfire_rooms_find_for_user_v1(
              &context, 8, 103, output, OUTPUT_CAPACITY, &extent) == 0);
  REQUIRE(expect_room(output, OUTPUT_CAPACITY, extent, 103, 0u, NULL,
                      unicode_name, (uint32_t)(sizeof(unicode_name) /
                                               sizeof(unicode_name[0])),
                      2u, -2, "2026-06-09T14:15:16.987654+05:45",
                      "2026-06-09 08:30:17.000001 UTC"));
  REQUIRE(al_campfire_room_sqlite_context_destroy_v1(&context) == 0);

  return 0;
}
